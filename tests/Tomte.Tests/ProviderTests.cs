using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tomte.AI;
using Tomte.Core;
using Xunit;

namespace Tomte.Tests;

public class ProviderTests
{
    private const string Key = "sk-test-SECRET";

    private static readonly ToolDefinition Weather = new(
        "get_weather",
        "Get current weather for a city",
        JsonDocument.Parse("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""").RootElement);

    private static readonly ChatRequest Hello = new("m", [ChatMessage.User("Hi")], System: "Be brief.");

    internal static async Task<List<ChatEvent>> Collect(IAsyncEnumerable<ChatEvent> events)
    {
        var list = new List<ChatEvent>();
        await foreach (var e in events)
            list.Add(e);
        return list;
    }

    internal static string Text(List<ChatEvent> events) => string.Concat(events.OfType<TextDelta>().Select(t => t.Text));

    /// <summary>The second request of a tool loop: question, the model's call, and the tool's answer.</summary>
    private static ChatRequest RoundTrip(ToolCallPart call, params ContentPart[] before) => new(
        "m",
        [
            ChatMessage.User("Weather in Paris?"),
            new(ChatRole.Assistant, [.. before, call]),
            new(ChatRole.User, [new ToolResultPart(call.Id, call.Name, "18 C and sunny")]),
        ],
        Tools: [Weather]);

    [Fact]
    public void CapabilityFlagsMatchWhatEachApiAccepts()
    {
        using var http = new HttpClient();
        var all = ProviderCapabilities.Streaming | ProviderCapabilities.Vision | ProviderCapabilities.ToolUse;
        Assert.Equal(all | ProviderCapabilities.Pdf, new AnthropicProvider(http, Key).Capabilities);
        Assert.Equal(all | ProviderCapabilities.Pdf, new GeminiProvider(http, Key).Capabilities);
        Assert.Equal(all, new OpenAICompatibleProvider(http, "http://localhost:11434/v1", null).Capabilities);
    }

    // Anthropic

    [Fact]
    public async Task AnthropicStreamsText()
    {
        using var http = RecordedHandler.Client(out var handler, "anthropic-text.sse");
        var events = await Collect(new AnthropicProvider(http, Key).StreamAsync(Hello));

        Assert.Equal("Hello, world", Text(events));
        Assert.Equal(new TurnDone("end_turn", 14, 12), events[^1]);
        Assert.Equal("https://api.anthropic.com/v1/messages", handler.Request!.RequestUri!.ToString());
        Assert.Equal(Key, handler.Request.Headers.GetValues("x-api-key").Single());
        Assert.Equal("2023-06-01", handler.Request.Headers.GetValues("anthropic-version").Single());
        var body = JsonNode.Parse(handler.Body!)!;
        Assert.True((bool)body["stream"]!);
        Assert.Equal("Be brief.", (string?)body["system"]);
        Assert.Equal("Hi", (string?)body["messages"]![0]!["content"]![0]!["text"]);
    }

    [Fact]
    public async Task AnthropicToolCallArrivesWholeWithThinkingBlockToEcho()
    {
        using var http = RecordedHandler.Client(out _, "anthropic-tool.sse");
        var events = await Collect(new AnthropicProvider(http, Key).StreamAsync(Hello with { Tools = [Weather] }));

        var thinking = Assert.Single(events.OfType<OpaqueEvent>()).Part;
        Assert.Equal("sig-part-1-part-2", thinking.Raw.GetProperty("signature").GetString());
        var call = Assert.Single(events.OfType<ToolCallEvent>()).Call;
        Assert.Equal(("toolu_01", "get_weather", "Paris"), (call.Id, call.Name, call.Input.GetProperty("city").GetString()));
        Assert.Equal("Let me check.", Text(events));
        Assert.Equal("tool_use", ((TurnDone)events[^1]).StopReason);
    }

    [Fact]
    public void AnthropicToolRoundTripRequestShape()
    {
        var thinking = new OpaquePart(JsonDocument.Parse("""{"type":"thinking","thinking":"","signature":"s"}""").RootElement);
        var call = new ToolCallPart("toolu_01", "get_weather", JsonDocument.Parse("""{"city":"Paris"}""").RootElement);
        var body = AnthropicProvider.BuildBody(RoundTrip(call, thinking));

        var assistant = body["messages"]![1]!["content"]!.AsArray();
        Assert.Equal("thinking", (string?)assistant[0]!["type"]);
        Assert.Equal("s", (string?)assistant[0]!["signature"]);
        Assert.Equal("tool_use", (string?)assistant[1]!["type"]);
        Assert.Equal("Paris", (string?)assistant[1]!["input"]!["city"]);
        var result = body["messages"]![2]!["content"]![0]!;
        Assert.Equal("tool_result", (string?)result["type"]);
        Assert.Equal("toolu_01", (string?)result["tool_use_id"]);
        Assert.Equal("18 C and sunny", (string?)result["content"]);
        var tool = body["tools"]![0]!;
        Assert.Equal("get_weather", (string?)tool["name"]);
        Assert.Equal("object", (string?)tool["input_schema"]!["type"]);
    }

    [Fact]
    public async Task AnthropicListsModelsWithContextSize()
    {
        using var http = RecordedHandler.Client(out var handler, "anthropic-models.json");
        var models = await new AnthropicProvider(http, Key).ListModelsAsync();

        Assert.Equal([new ModelInfo("claude-opus-5-5", 1_000_000), new ModelInfo("claude-haiku-5-5", 1_000_000)], models);
        Assert.StartsWith("https://api.anthropic.com/v1/models", handler.Request!.RequestUri!.ToString());
    }

    [Fact]
    public async Task ErrorsCarryTheProviderMessageButNeverTheKey()
    {
        using var http = RecordedHandler.Client(out _, "anthropic-401.json", HttpStatusCode.Unauthorized);
        var e = await Assert.ThrowsAsync<ProviderException>(() => Collect(new AnthropicProvider(http, Key).StreamAsync(Hello)));

        Assert.Equal(401, e.Status);
        Assert.Contains("invalid x-api-key", e.Message);
        Assert.DoesNotContain(Key, e.Message);
    }

    [Fact]
    public async Task AnthropicStreamErrorThrows()
    {
        using var http = RecordedHandler.Client(out _, "anthropic-stream-error.sse");
        var e = await Assert.ThrowsAsync<ProviderException>(() => Collect(new AnthropicProvider(http, Key).StreamAsync(Hello)));
        Assert.Equal("Overloaded", e.Message);
    }

    // Gemini

    [Fact]
    public async Task GeminiStreamsText()
    {
        using var http = RecordedHandler.Client(out var handler, "gemini-text.sse");
        var events = await Collect(new GeminiProvider(http, Key).StreamAsync(Hello with { Model = "gemini-2.5-flash" }));

        Assert.Equal("Hello, world", Text(events));
        Assert.Equal(new TurnDone("STOP", 9, 4), events[^1]);
        var uri = handler.Request!.RequestUri!.ToString();
        Assert.Equal("https://generativelanguage.googleapis.com/v1beta/models/gemini-2.5-flash:streamGenerateContent?alt=sse", uri);
        Assert.Equal(Key, handler.Request.Headers.GetValues("x-goog-api-key").Single());
        var body = JsonNode.Parse(handler.Body!)!;
        Assert.Equal("Be brief.", (string?)body["systemInstruction"]!["parts"]![0]!["text"]);
        Assert.Equal("user", (string?)body["contents"]![0]!["role"]);
    }

    [Fact]
    public async Task GeminiToolCallKeepsItsThoughtSignature()
    {
        using var http = RecordedHandler.Client(out _, "gemini-tool.sse");
        var events = await Collect(new GeminiProvider(http, Key).StreamAsync(Hello with { Tools = [Weather] }));

        var call = Assert.Single(events.OfType<ToolCallEvent>()).Call;
        Assert.Equal(("get_weather", "Paris", "c2lnbmF0dXJl"), (call.Name, call.Input.GetProperty("city").GetString(), call.Signature));
    }

    [Fact]
    public async Task GeminiStreamErrorThrows()
    {
        using var http = RecordedHandler.Client(out _, "gemini-stream-error.sse");
        var e = await Assert.ThrowsAsync<ProviderException>(() => Collect(new GeminiProvider(http, Key).StreamAsync(Hello)));
        Assert.Contains("overloaded", e.Message);
    }

    [Fact]
    public void GeminiToolRoundTripRequestShape()
    {
        var call = new ToolCallPart("call_0", "get_weather", JsonDocument.Parse("""{"city":"Paris"}""").RootElement, "c2ln");
        var body = GeminiProvider.BuildBody(RoundTrip(call));

        var model = body["contents"]![1]!;
        Assert.Equal("model", (string?)model["role"]);
        Assert.Equal("get_weather", (string?)model["parts"]![0]!["functionCall"]!["name"]);
        Assert.Equal("c2ln", (string?)model["parts"]![0]!["thoughtSignature"]);
        var response = body["contents"]![2]!["parts"]![0]!["functionResponse"]!;
        Assert.Equal("get_weather", (string?)response["name"]);
        Assert.Equal("18 C and sunny", (string?)response["response"]!["result"]);
        Assert.Equal("get_weather", (string?)body["tools"]![0]!["functionDeclarations"]![0]!["name"]);
    }

    [Fact]
    public async Task GeminiListsOnlyChatModels()
    {
        using var http = RecordedHandler.Client(out _, "gemini-models.json");
        var models = await new GeminiProvider(http, Key).ListModelsAsync();
        Assert.Equal([new ModelInfo("gemini-2.5-flash", 1_048_576)], models);
    }

    // OpenAI-compatible (fixtures recorded from Ollama 0.5.7, plus one hand-built OpenAI-style chunked stream)

    [Fact]
    public async Task OpenAIStreamsTextFromOllamaRecording()
    {
        using var http = RecordedHandler.Client(out var handler, "openai-ollama-text.sse");
        var events = await Collect(new OpenAICompatibleProvider(http, "http://localhost:11434/v1/", null).StreamAsync(Hello));

        Assert.Equal("OK", Text(events));
        Assert.Equal(new TurnDone("stop", 28, 2), events[^1]);
        Assert.Equal("http://localhost:11434/v1/chat/completions", handler.Request!.RequestUri!.ToString());
        Assert.Null(handler.Request.Headers.Authorization);
        var body = JsonNode.Parse(handler.Body!)!;
        Assert.Equal("system", (string?)body["messages"]![0]!["role"]);
        Assert.Equal("Hi", (string?)body["messages"]![1]!["content"]);
    }

    [Fact]
    public async Task OpenAISendsBearerKeyWhenSet()
    {
        using var http = RecordedHandler.Client(out var handler, "openai-ollama-text.sse");
        await Collect(new OpenAICompatibleProvider(http, "https://api.openai.com/v1", Key).StreamAsync(Hello));
        Assert.Equal($"Bearer {Key}", handler.Request!.Headers.Authorization!.ToString());
    }

    [Fact]
    public async Task OpenAIToolCallFromOllamaRecording()
    {
        using var http = RecordedHandler.Client(out _, "openai-ollama-tool.sse");
        var events = await Collect(new OpenAICompatibleProvider(http, "http://localhost:11434/v1", null).StreamAsync(Hello with { Tools = [Weather] }));

        var call = Assert.Single(events.OfType<ToolCallEvent>()).Call;
        Assert.Equal(("call_27wnmgwi", "get_weather", "Paris"), (call.Id, call.Name, call.Input.GetProperty("city").GetString()));
    }

    [Fact]
    public async Task OpenAIChunkedToolCallsAssembleByIndex()
    {
        using var http = RecordedHandler.Client(out _, "openai-chunked-tool.sse");
        var events = await Collect(new OpenAICompatibleProvider(http, "https://api.openai.com/v1", Key).StreamAsync(Hello with { Tools = [Weather] }));

        var calls = events.OfType<ToolCallEvent>().Select(e => e.Call).ToList();
        Assert.Equal(["call_a", "call_b"], calls.Select(c => c.Id));
        Assert.Equal("Paris", calls[0].Input.GetProperty("city").GetString());
        Assert.Equal(new TurnDone("tool_calls", 50, 20), events[^1]);
    }

    [Fact]
    public async Task OpenAIToolResultTurnFromOllamaRecording()
    {
        var call = new ToolCallPart("call_27wnmgwi", "get_weather", JsonDocument.Parse("""{"city":"Paris"}""").RootElement);
        using var http = RecordedHandler.Client(out var handler, "openai-ollama-tool-result.sse");
        var events = await Collect(new OpenAICompatibleProvider(http, "http://localhost:11434/v1", null).StreamAsync(RoundTrip(call)));

        Assert.Contains("18", Text(events));
        var messages = JsonNode.Parse(handler.Body!)!["messages"]!.AsArray();
        Assert.Equal("assistant", (string?)messages[1]!["role"]);
        Assert.Null(messages[1]!["content"]);
        Assert.Equal("""{"city":"Paris"}""", (string?)messages[1]!["tool_calls"]![0]!["function"]!["arguments"]);
        Assert.Equal("tool", (string?)messages[2]!["role"]);
        Assert.Equal("call_27wnmgwi", (string?)messages[2]!["tool_call_id"]);
        Assert.Equal(3, messages.Count);
    }

    [Fact]
    public async Task OpenAIListsModels()
    {
        using var http = RecordedHandler.Client(out _, "openai-ollama-models.json");
        var models = await new OpenAICompatibleProvider(http, "http://localhost:11434/v1", null).ListModelsAsync();
        Assert.Equal(["llama3.2:3b", "llama3.2:latest", "qwen2.5-coder:7b"], models.Select(m => m.Id));
    }

    [Fact]
    public async Task OpenAIEndpointWithoutModelListReturnsEmpty()
    {
        using var http = RecordedHandler.Client(out _, "anthropic-401.json", HttpStatusCode.NotFound);
        Assert.Empty(await new OpenAICompatibleProvider(http, "http://localhost:1234/v1", null).ListModelsAsync());
    }

    [Fact]
    public void ImagesGoAsDataUrlsAndPdfsAreRefused()
    {
        var image = new ChatRequest("m", [new(ChatRole.User, [new TextPart("What is this?"), new ImagePart("image/png", [1, 2, 3])])]);
        var part = OpenAICompatibleProvider.BuildBody(image)["messages"]![0]!["content"]![1]!;
        Assert.Equal("data:image/png;base64,AQID", (string?)part["image_url"]!["url"]);

        var pdf = new ChatRequest("m", [new(ChatRole.User, [new PdfPart([1])])]);
        Assert.Throws<NotSupportedException>(() => OpenAICompatibleProvider.BuildBody(pdf));
    }

    [Fact]
    public async Task StreamCutOffBeforeTheFinishReasonThrows()
    {
        using var openAi = RecordedHandler.Client(out _, "openai-truncated.sse");
        await Assert.ThrowsAsync<ProviderException>(() => Collect(new OpenAICompatibleProvider(openAi, "http://127.0.0.1:11434/v1", null).StreamAsync(Hello)));
        using var gemini = RecordedHandler.Client(out _, "gemini-truncated.sse");
        await Assert.ThrowsAsync<ProviderException>(() => Collect(new GeminiProvider(gemini, Key).StreamAsync(Hello)));
    }

    [Fact]
    public async Task SseParserHandlesCrlfAndMultiLineData()
    {
        var response = new HttpResponseMessage { Content = new StringContent("data: a\r\ndata: b\r\n\r\n: comment\r\ndata: c\r\n\r\n") };
        var payloads = new List<string>();
        await foreach (var data in ProviderHttp.ReadSseAsync(response, default))
            payloads.Add(data);
        Assert.Equal(["a\nb", "c"], payloads);
    }

    // Key storage and the test call on save

    [Fact]
    public void CredentialRoundTrip()
    {
        var id = $"test-{Guid.NewGuid():N}";
        try
        {
            Assert.Null(CredentialStore.Read(id));
            CredentialStore.Save(id, "sk-round-trip-é");
            Assert.Equal("sk-round-trip-é", CredentialStore.Read(id));
        }
        finally
        {
            CredentialStore.Delete(id);
        }
        Assert.Null(CredentialStore.Read(id));
    }

    [Fact]
    public async Task FailedTestCallDoesNotStoreTheKey()
    {
        var config = new ProviderConfig($"test-{Guid.NewGuid():N}", ProviderKind.Anthropic, DefaultModel: "claude-opus-5-5");
        using var http = RecordedHandler.Client(out _, "anthropic-401.json", HttpStatusCode.Unauthorized);

        await Assert.ThrowsAsync<ProviderException>(() => ProviderSetup.TestAndSaveAsync(config, Key, http));
        Assert.Null(CredentialStore.Read(config.Id));
    }

    [Fact]
    public async Task PassingTestCallStoresTheKey()
    {
        var config = new ProviderConfig($"test-{Guid.NewGuid():N}", ProviderKind.Anthropic, DefaultModel: "claude-opus-5-5");
        using var http = RecordedHandler.Client(out var handler, "anthropic-text.sse");
        try
        {
            Assert.Equal("claude-opus-5-5", await ProviderSetup.TestAndSaveAsync(config, Key, http));
            Assert.Equal(Key, CredentialStore.Read(config.Id));
            Assert.Equal(64, (int)JsonNode.Parse(handler.Body!)!["max_tokens"]!);
        }
        finally
        {
            CredentialStore.Delete(config.Id);
        }
    }
}
