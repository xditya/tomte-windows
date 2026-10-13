using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tomte.AI;

/// <summary>Anthropic Messages API over raw HTTP and SSE.</summary>
public sealed class AnthropicProvider(HttpClient http, string key) : IChatProvider
{
    private const string BaseUrl = "https://api.anthropic.com/v1";

    public ProviderCapabilities Capabilities =>
        ProviderCapabilities.Streaming | ProviderCapabilities.Vision | ProviderCapabilities.ToolUse | ProviderCapabilities.Pdf;

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default)
    {
        // one page of 1000; follow has_more/last_id if the list ever outgrows it.
        using var doc = await ProviderHttp.GetJsonAsync(http, Authorize(new(HttpMethod.Get, $"{BaseUrl}/models?limit=1000")), key, ct);
        return [.. doc.RootElement.GetProperty("data").EnumerateArray().Select(m => new ModelInfo(
            m.GetProperty("id").GetString()!,
            m.TryGetProperty("max_input_tokens", out var max) && max.ValueKind == JsonValueKind.Number ? max.GetInt32() : null))];
    }

    public async IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var response = await ProviderHttp.SendAsync(http, Authorize(ProviderHttp.Post($"{BaseUrl}/messages", BuildBody(request))), key, ct);

        // One open block at a time: the API finishes each block before starting the next.
        string? blockType = null, toolId = null, toolName = null;
        JsonObject? opaque = null;
        var json = new StringBuilder();
        string? stopReason = null;
        int input = 0, output = 0;

        await foreach (var data in ProviderHttp.ReadSseAsync(response, ct))
        {
            using var doc = JsonDocument.Parse(data);
            var e = doc.RootElement;
            switch (e.GetProperty("type").GetString())
            {
                case "message_start":
                    ReadUsage(e.GetProperty("message"), ref input, ref output);
                    break;
                case "content_block_start":
                    var block = e.GetProperty("content_block");
                    blockType = block.GetProperty("type").GetString();
                    json.Clear();
                    if (blockType == "text" && block.GetProperty("text").GetString() is { Length: > 0 } first)
                        yield return new TextDelta(first);
                    else if (blockType == "tool_use")
                        (toolId, toolName) = (block.GetProperty("id").GetString(), block.GetProperty("name").GetString());
                    else if (blockType is "thinking" or "redacted_thinking")
                        opaque = JsonNode.Parse(block.GetRawText())!.AsObject();
                    break;
                case "content_block_delta":
                    var delta = e.GetProperty("delta");
                    switch (delta.GetProperty("type").GetString())
                    {
                        case "text_delta":
                            yield return new TextDelta(delta.GetProperty("text").GetString()!);
                            break;
                        case "input_json_delta":
                            json.Append(delta.GetProperty("partial_json").GetString());
                            break;
                        case "thinking_delta" when opaque is not null:
                            opaque["thinking"] = (string?)opaque["thinking"] + delta.GetProperty("thinking").GetString();
                            break;
                        case "signature_delta" when opaque is not null:
                            opaque["signature"] = (string?)opaque["signature"] + delta.GetProperty("signature").GetString();
                            break;
                    }
                    break;
                case "content_block_stop":
                    if (blockType == "tool_use")
                        yield return new ToolCallEvent(new ToolCallPart(toolId!, toolName!, ProviderHttp.ParseArguments(json.ToString(), key)));
                    else if (opaque is not null)
                        yield return new OpaqueEvent(new OpaquePart(JsonSerializer.SerializeToElement(opaque)));
                    (blockType, opaque) = (null, null);
                    break;
                case "message_delta":
                    if (e.GetProperty("delta").TryGetProperty("stop_reason", out var stop) && stop.ValueKind == JsonValueKind.String)
                        stopReason = stop.GetString();
                    if (e.TryGetProperty("usage", out _))
                        ReadUsage(e, ref input, ref output);
                    break;
                case "message_stop":
                    yield return new TurnDone(stopReason, input, output);
                    yield break;
                case "error":
                    throw new ProviderException(ProviderHttp.Scrub(e.GetProperty("error").GetProperty("message").GetString() ?? "stream error", key));
            }
        }
        throw new ProviderException("The stream ended before the message finished.");
    }

    internal static JsonObject BuildBody(ChatRequest request)
    {
        var body = new JsonObject
        {
            ["model"] = request.Model,
            ["max_tokens"] = request.MaxTokens,
            ["stream"] = true,
            ["messages"] = new JsonArray([.. request.Messages.Select(m => (JsonNode)new JsonObject
            {
                ["role"] = m.Role == ChatRole.User ? "user" : "assistant",
                ["content"] = new JsonArray([.. m.Parts.Select(Block)]),
            })]),
        };
        if (request.System is not null)
            body["system"] = request.System;
        if (request.Tools is { Count: > 0 } tools)
        {
            body["tools"] = new JsonArray([.. tools.Select(t => (JsonNode)new JsonObject
            {
                ["name"] = t.Name,
                ["description"] = t.Description,
                ["input_schema"] = JsonNode.Parse(t.InputSchema.GetRawText()),
            })]);
        }
        return body;
    }

    private static JsonNode? Block(ContentPart part) => part switch
    {
        TextPart t => new JsonObject { ["type"] = "text", ["text"] = t.Text },
        ImagePart i => new JsonObject
        {
            ["type"] = "image",
            ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = i.MediaType, ["data"] = Convert.ToBase64String(i.Data) },
        },
        PdfPart p => new JsonObject
        {
            ["type"] = "document",
            ["source"] = new JsonObject { ["type"] = "base64", ["media_type"] = "application/pdf", ["data"] = Convert.ToBase64String(p.Data) },
        },
        ToolCallPart c => new JsonObject
        {
            ["type"] = "tool_use",
            ["id"] = c.Id,
            ["name"] = c.Name,
            ["input"] = JsonNode.Parse(c.Input.GetRawText()),
        },
        ToolResultPart r => new JsonObject
        {
            ["type"] = "tool_result",
            ["tool_use_id"] = r.CallId,
            ["content"] = r.Content,
            ["is_error"] = r.IsError,
        },
        OpaquePart o => JsonNode.Parse(o.Raw.GetRawText()),
        _ => throw new NotSupportedException(part.GetType().Name),
    };

    private HttpRequestMessage Authorize(HttpRequestMessage request)
    {
        request.Headers.Add("x-api-key", key);
        request.Headers.Add("anthropic-version", "2023-06-01");
        return request;
    }

    private static void ReadUsage(JsonElement owner, ref int input, ref int output)
    {
        if (!owner.TryGetProperty("usage", out var usage))
            return;
        if (usage.TryGetProperty("input_tokens", out var i) && i.ValueKind == JsonValueKind.Number)
            input = i.GetInt32();
        if (usage.TryGetProperty("output_tokens", out var o) && o.ValueKind == JsonValueKind.Number)
            output = o.GetInt32();
    }
}
