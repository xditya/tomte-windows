using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tomte.AI;

/// <summary>
/// Any endpoint that speaks the OpenAI chat completions API: OpenAI, OpenRouter, Ollama, LM Studio.
/// <paramref name="baseUrl"/> ends before <c>/chat/completions</c>, for example
/// <c>http://localhost:11434/v1</c>. Local servers need no key.
/// </summary>
public sealed partial class OpenAICompatibleProvider(HttpClient http, string baseUrl, string? key) : IChatProvider
{
    private readonly string _baseUrl = baseUrl.TrimEnd('/');

    /// <summary>No PDF: the chat completions API has no common document input, so PDFs are extracted locally.</summary>
    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Streaming | ProviderCapabilities.Vision | ProviderCapabilities.ToolUse;

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        JsonDocument doc;
        try
        {
            doc = await ProviderHttp.GetJsonAsync(http, Authorize(new(HttpMethod.Get, $"{_baseUrl}/models")), key, ct);
        }
        catch (ProviderException e) when (e.Status == 404)
        {
            return [];
        }
        using (doc)
        {
            // OpenRouter says what a model reads; plain OpenAI-compatible lists do not.
            return [.. doc.RootElement.GetProperty("data").EnumerateArray().Select(m => new ModelInfo(
                m.GetProperty("id").GetString()!,
                m.TryGetProperty("context_length", out var max) && max.ValueKind == JsonValueKind.Number ? max.GetInt32() : null,
                m.TryGetProperty("architecture", out var a) && a.TryGetProperty("input_modalities", out var input) && input.ValueKind == JsonValueKind.Array
                    ? input.EnumerateArray().Any(x => x.GetString() == "image") : null))];
        }
    }

    public async IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var response = await ProviderHttp.SendAsync(http, Authorize(ProviderHttp.Post($"{_baseUrl}/chat/completions", BuildBody(request, _baseUrl))), key, ct);

        // Tool calls stream as fragments keyed by index; the id and name come in the first fragment.
        var calls = new SortedDictionary<int, (string Id, string Name, StringBuilder Args)>();
        string? stopReason = null;
        int input = 0, output = 0;

        await foreach (var data in ProviderHttp.ReadSseAsync(response, ct))
        {
            if (data == "[DONE]")
                break;
            using var doc = ProviderHttp.ParseJson(data, key);
            var chunk = doc.RootElement;
            if (chunk.TryGetProperty("error", out _))
                throw new ProviderException(ProviderHttp.Scrub(ProviderHttp.ErrorMessage(data), key));
            if (chunk.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object)
            {
                input = usage.TryGetProperty("prompt_tokens", out var p) ? p.GetInt32() : input;
                output = usage.TryGetProperty("completion_tokens", out var c) ? c.GetInt32() : output;
            }
            if (!chunk.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
                continue;
            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var finish) && finish.ValueKind == JsonValueKind.String)
                stopReason = finish.GetString();
            if (!choice.TryGetProperty("delta", out var delta))
                continue;
            if (delta.TryGetProperty("content", out var content) && content.GetString() is { Length: > 0 } text)
                yield return new TextDelta(text);
            if (!delta.TryGetProperty("tool_calls", out var toolCalls) || toolCalls.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var call in toolCalls.EnumerateArray())
            {
                var index = call.TryGetProperty("index", out var i) ? i.GetInt32() : calls.Count;
                if (!calls.TryGetValue(index, out var entry))
                    entry = calls[index] = ("", "", new StringBuilder());
                if (call.TryGetProperty("id", out var id) && id.GetString() is { Length: > 0 } idText)
                    calls[index] = entry = (idText, entry.Name, entry.Args);
                if (call.TryGetProperty("function", out var fn))
                {
                    if (fn.TryGetProperty("name", out var name) && name.GetString() is { Length: > 0 } nameText)
                        calls[index] = entry = (entry.Id, nameText, entry.Args);
                    if (fn.TryGetProperty("arguments", out var args))
                        entry.Args.Append(args.GetString());
                }
            }
        }

        // A dropped connection before finish_reason must not look like a short, finished answer.
        if (stopReason is null)
            throw new ProviderException("The stream ended before the message finished.");
        foreach (var (index, (id, name, args)) in calls)
            yield return new ToolCallEvent(new ToolCallPart(id.Length > 0 ? id : $"call_{index}", name, ProviderHttp.ParseArguments(args.ToString(), key)));
        yield return new TurnDone(stopReason, input, output);
    }

    internal static JsonObject BuildBody(ChatRequest request, string baseUrl = "")
    {
        var messages = new JsonArray();
        if (request.System is not null)
            messages.Add(new JsonObject { ["role"] = "system", ["content"] = request.System });
        foreach (var m in request.Messages)
        {
            if (m.Role == ChatRole.Assistant)
            {
                var text = string.Concat(m.Parts.OfType<TextPart>().Select(t => t.Text));
                var message = new JsonObject { ["role"] = "assistant", ["content"] = text.Length > 0 ? text : null };
                var toolCalls = m.Parts.OfType<ToolCallPart>().ToList();
                if (toolCalls.Count > 0)
                {
                    message["tool_calls"] = new JsonArray([.. toolCalls.Select(c => (JsonNode)new JsonObject
                    {
                        ["id"] = c.Id,
                        ["type"] = "function",
                        ["function"] = new JsonObject { ["name"] = c.Name, ["arguments"] = c.Input.GetRawText() },
                    })]);
                }
                messages.Add(message);
                continue;
            }

            // Tool results are their own "tool" messages; whatever else the user turn holds follows them.
            foreach (var r in m.Parts.OfType<ToolResultPart>())
                messages.Add(new JsonObject { ["role"] = "tool", ["tool_call_id"] = r.CallId, ["content"] = r.Content });
            var rest = m.Parts.Where(p => p is not ToolResultPart and not OpaquePart).ToList();
            if (rest.Count == 0)
                continue;
            messages.Add(new JsonObject
            {
                ["role"] = "user",
                ["content"] = rest is [TextPart only] ? only.Text : new JsonArray([.. rest.Select(UserPart)]),
            });
        }

        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["messages"] = messages,
            // OpenAI itself rejects max_tokens on newer models; Ollama and other shims read only max_tokens.
            [IsOpenAI(baseUrl) ? "max_completion_tokens" : "max_tokens"] = request.MaxTokens,
            ["stream"] = true,
            ["stream_options"] = new JsonObject { ["include_usage"] = true },
        };
        if (request.Tools is { Count: > 0 } tools)
        {
            body["tools"] = new JsonArray([.. tools.Select(t => (JsonNode)new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = JsonNode.Parse(t.InputSchema.GetRawText()),
                },
            })]);
        }
        return body;
    }

    // What a local Ollama said about each model, kept for the process: base URL and model id, then the answer.
    private static readonly ConcurrentDictionary<string, ModelInfo> Described = new();

    /// <summary>
    /// What a local Ollama says about a model: vision from its capabilities (Ollama 0.6 and later) and the context it
    /// runs the model with: the num_ctx its Modelfile sets, else its default of 4,096 (or the model's own length when
    /// that is shorter), since the OpenAI endpoint cannot ask for more. Asked only of a loopback endpoint, with a 3 s
    /// limit; an answer is kept for the process, a miss is asked again. Null for any other server, an unknown model, or an
    /// Ollama that does not answer.
    /// </summary>
    public async Task<ModelInfo?> DescribeAsync(string model, CancellationToken ct = default)
    {
        if (!Uri.TryCreate(_baseUrl, UriKind.Absolute, out var uri) || !uri.IsLoopback)
            return null;
        string cacheKey = _baseUrl + "\n" + model;
        if (Described.TryGetValue(cacheKey, out var known))
            return known;

        string root = _baseUrl.EndsWith("/v1", StringComparison.OrdinalIgnoreCase) ? _baseUrl[..^3] : _baseUrl;
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(ct);
        limit.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            using var doc = await ProviderHttp.GetJsonAsync(http, ProviderHttp.Post($"{root}/api/show", new JsonObject { ["model"] = model }), key, limit.Token);
            var show = doc.RootElement;
            bool? vision = show.TryGetProperty("capabilities", out var caps) && caps.ValueKind == JsonValueKind.Array
                ? caps.EnumerateArray().Any(c => c.GetString() == "vision") : null;
            int? trained = show.TryGetProperty("model_info", out var info) && info.ValueKind == JsonValueKind.Object
                ? info.EnumerateObject().Where(p => p.Name.EndsWith(".context_length", StringComparison.Ordinal) && p.Value.ValueKind == JsonValueKind.Number)
                    .Select(p => p.Value.TryGetInt32(out int n) ? n : (int?)null).FirstOrDefault()
                : null;
            int context = show.TryGetProperty("parameters", out var parameters) && parameters.ValueKind == JsonValueKind.String
                && NumCtx().Match(parameters.GetString()!) is { Success: true } set
                    ? int.Parse(set.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture)
                    : Math.Min(trained ?? OllamaDefaultContext, OllamaDefaultContext);
            var described = new ModelInfo(model, context, vision);
            Described[cacheKey] = described;
            return described;
        }
        catch (InvalidOperationException)
        {
            // An answer in a shape this does not know (a capability that is not a string): the plain answer.
            return new ModelInfo(model, OllamaDefaultContext, null);
        }
        catch (Exception e) when (e is ProviderException or OperationCanceledException && !ct.IsCancellationRequested)
        {
            // Another server's 404, a model not pulled yet, Ollama not running or too slow: asked again next time.
            return null;
        }
    }

    // The context Ollama runs a model with when nothing sets num_ctx.
    private const int OllamaDefaultContext = 4096;

    [System.Text.RegularExpressions.GeneratedRegex(@"^num_ctx\s+(\d+)", System.Text.RegularExpressions.RegexOptions.Multiline)]
    private static partial System.Text.RegularExpressions.Regex NumCtx();

    private static JsonNode UserPart(ContentPart part) => part switch
    {
        TextPart t => new JsonObject { ["type"] = "text", ["text"] = t.Text },
        ImagePart i => new JsonObject
        {
            ["type"] = "image_url",
            ["image_url"] = new JsonObject { ["url"] = $"data:{i.MediaType};base64,{Convert.ToBase64String(i.Data)}" },
        },
        _ => throw new NotSupportedException($"{part.GetType().Name} is not supported by OpenAI-compatible endpoints."),
    };

    private static bool IsOpenAI(string baseUrl) =>
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) && uri.Host.Equals("api.openai.com", StringComparison.OrdinalIgnoreCase);

    private HttpRequestMessage Authorize(HttpRequestMessage request)
    {
        if (!string.IsNullOrEmpty(key))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        return request;
    }
}
