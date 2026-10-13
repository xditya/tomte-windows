using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tomte.AI;

/// <summary>Google Gemini API (generateContent) over raw HTTP and SSE.</summary>
public sealed class GeminiProvider(HttpClient http, string key) : IChatProvider
{
    private const string BaseUrl = "https://generativelanguage.googleapis.com/v1beta";

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Streaming | ProviderCapabilities.Vision | ProviderCapabilities.ToolUse | ProviderCapabilities.Pdf;

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        // one page of 1000; follow nextPageToken if the list ever outgrows it.
        using var doc = await ProviderHttp.GetJsonAsync(http, Authorize(new(HttpMethod.Get, $"{BaseUrl}/models?pageSize=1000")), key, ct);
        return [.. doc.RootElement.GetProperty("models").EnumerateArray()
            .Where(m => m.TryGetProperty("supportedGenerationMethods", out var methods)
                && methods.EnumerateArray().Any(x => x.GetString() == "generateContent"))
            .Select(m => new ModelInfo(
                m.GetProperty("name").GetString()!.Replace("models/", "", StringComparison.Ordinal),
                m.TryGetProperty("inputTokenLimit", out var max) ? max.GetInt32() : null))];
    }

    public async IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var url = $"{BaseUrl}/models/{Uri.EscapeDataString(request.Model)}:streamGenerateContent?alt=sse";
        var response = await ProviderHttp.SendAsync(http, Authorize(ProviderHttp.Post(url, BuildBody(request))), key, ct);

        string? stopReason = null;
        int input = 0, output = 0, calls = 0;
        await foreach (var data in ProviderHttp.ReadSseAsync(response, ct))
        {
            using var doc = ProviderHttp.ParseJson(data, key);
            var chunk = doc.RootElement;
            if (chunk.TryGetProperty("error", out _))
                throw new ProviderException(ProviderHttp.Scrub(ProviderHttp.ErrorMessage(data), key));
            if (chunk.TryGetProperty("usageMetadata", out var usage))
            {
                input = usage.TryGetProperty("promptTokenCount", out var p) ? p.GetInt32() : input;
                output = usage.TryGetProperty("candidatesTokenCount", out var c) ? c.GetInt32() : output;
            }
            if (!chunk.TryGetProperty("candidates", out var candidates) || candidates.GetArrayLength() == 0)
                continue;
            var candidate = candidates[0];
            if (candidate.TryGetProperty("finishReason", out var finish))
                stopReason = finish.GetString();
            if (!candidate.TryGetProperty("content", out var content) || !content.TryGetProperty("parts", out var parts))
                continue;
            foreach (var part in parts.EnumerateArray())
            {
                if (part.TryGetProperty("thought", out var thought) && thought.GetBoolean())
                    continue;
                if (part.TryGetProperty("text", out var text))
                    yield return new TextDelta(text.GetString()!);
                else if (part.TryGetProperty("functionCall", out var call))
                {
                    // Gemini sends each call whole. Older models give no id, so number them.
                    var id = call.TryGetProperty("id", out var callId) ? callId.GetString()! : $"call_{calls}";
                    calls++;
                    var args = call.TryGetProperty("args", out var a) ? a.Clone() : ProviderHttp.ParseArguments("", key);
                    var signature = part.TryGetProperty("thoughtSignature", out var sig) ? sig.GetString() : null;
                    yield return new ToolCallEvent(new ToolCallPart(id, call.GetProperty("name").GetString()!, args, signature));
                }
            }
        }
        if (stopReason is null)
            throw new ProviderException("The stream ended before the message finished.");
        yield return new TurnDone(stopReason, input, output);
    }

    internal static JsonObject BuildBody(ChatRequest request)
    {
        var body = new JsonObject
        {
            ["contents"] = new JsonArray([.. request.Messages.Select(m => (JsonNode)new JsonObject
            {
                ["role"] = m.Role == ChatRole.User ? "user" : "model",
                ["parts"] = new JsonArray([.. m.Parts.Select(Part).OfType<JsonNode>()]),
            })]),
            ["generationConfig"] = new JsonObject { ["maxOutputTokens"] = request.MaxTokens },
        };
        if (request.System is not null)
            body["systemInstruction"] = new JsonObject { ["parts"] = new JsonArray(new JsonObject { ["text"] = request.System }) };
        if (request.Tools is { Count: > 0 } tools)
        {
            body["tools"] = new JsonArray(new JsonObject
            {
                ["functionDeclarations"] = new JsonArray([.. tools.Select(t => (JsonNode)new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(t.InputSchema.GetRawText()),
                })]),
            });
        }
        return body;
    }

    private static JsonNode? Part(ContentPart part) => part switch
    {
        TextPart t => new JsonObject { ["text"] = t.Text },
        ImagePart i => Inline(i.MediaType, i.Data),
        PdfPart p => Inline("application/pdf", p.Data),
        ToolCallPart c => WithSignature(new JsonObject
        {
            ["functionCall"] = new JsonObject { ["name"] = c.Name, ["args"] = JsonNode.Parse(c.Input.GetRawText()) },
        }, c.Signature),
        ToolResultPart r => new JsonObject
        {
            ["functionResponse"] = new JsonObject
            {
                ["name"] = r.Name,
                ["response"] = new JsonObject { [r.IsError ? "error" : "result"] = r.Content },
            },
        },
        OpaquePart => null,
        _ => throw new NotSupportedException(part.GetType().Name),
    };

    private static JsonObject Inline(string mime, byte[] data) =>
        new() { ["inlineData"] = new JsonObject { ["mimeType"] = mime, ["data"] = Convert.ToBase64String(data) } };

    private static JsonObject WithSignature(JsonObject part, string? signature)
    {
        if (signature is not null)
            part["thoughtSignature"] = signature;
        return part;
    }

    private HttpRequestMessage Authorize(HttpRequestMessage request)
    {
        // Header, not the ?key= query string, so the key never lands in a URL or an exception message.
        request.Headers.Add("x-goog-api-key", key);
        return request;
    }
}
