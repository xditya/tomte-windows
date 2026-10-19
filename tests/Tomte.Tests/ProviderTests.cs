using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;
using Xunit;

namespace Tomte.Tests;

/// <summary>
/// Runs only with TOMTE_VAULT_TESTS=1 in the environment: the test writes to and deletes from the real Windows
/// Credential Manager, which a default run on a shared PC must not touch.
/// </summary>
public sealed class VaultFactAttribute : FactAttribute
{
    public VaultFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TOMTE_VAULT_TESTS") != "1")
            Skip = "Writes the real Credential Manager. Set TOMTE_VAULT_TESTS=1 to run it.";
    }
}

public class ProviderTests
{
    private const string Key = "sk-test-SECRET";

    private static readonly ToolDefinition Weather = new(
        "get_weather",
        "Get current weather for a city",
        JsonDocument.Parse("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""").RootElement);

    private static readonly ToolDefinition LockPc = new(
        "lock_pc",
        "Lock the PC",
        JsonDocument.Parse("""{"type":"object","properties":{},"required":[]}""").RootElement);

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
        Assert.Empty(handler.Request.Headers.Accept);
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
        Assert.Equal("call_0", (string?)response["id"]);
        Assert.Equal("call_0", (string?)model["parts"]![0]!["functionCall"]!["id"]);
        var synthetic = GeminiProvider.BuildBody(RoundTrip(call with { Id = "tomte_call_0" }));
        Assert.Null(synthetic["contents"]![1]!["parts"]![0]!["functionCall"]!["id"]);
        Assert.Null(synthetic["contents"]![2]!["parts"]![0]!["functionResponse"]!["id"]);
        Assert.Equal("18 C and sunny", (string?)response["response"]!["result"]);
        Assert.Equal("get_weather", (string?)body["tools"]![0]!["functionDeclarations"]![0]!["name"]);
    }

    [Fact]
    public void GeminiDeclaresANoArgumentToolWithoutParameters()
    {
        var body = GeminiProvider.BuildBody(Hello with { Tools = [Weather, LockPc] });
        var declarations = body["tools"]![0]!["functionDeclarations"]!;
        Assert.Equal("object", (string?)declarations[0]!["parameters"]!["type"]);
        Assert.Equal("lock_pc", (string?)declarations[1]!["name"]);
        Assert.Null(declarations[1]!["parameters"]);
    }

    // Dropping the schema is Gemini's quirk alone: the other two adapters send a no-argument tool's empty object schema as it is.
    [Fact]
    public void AnthropicAndOpenAIKeepTheSchemaOfANoArgumentTool()
    {
        var request = Hello with { Tools = [LockPc] };
        Assert.Equal(LockPc.InputSchema.GetRawText(), AnthropicProvider.BuildBody(request)["tools"]![0]!["input_schema"]!.ToJsonString());
        Assert.Equal(LockPc.InputSchema.GetRawText(), OpenAICompatibleProvider.BuildBody(request)["tools"]![0]!["function"]!["parameters"]!.ToJsonString());
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
        var body = JsonNode.Parse(handler.Body!)!.AsObject();
        Assert.True(body.ContainsKey("max_completion_tokens"));
        Assert.False(body.ContainsKey("max_tokens"));
        Assert.True(OpenAICompatibleProvider.BuildBody(Hello, "http://127.0.0.1:11434/v1").ContainsKey("max_tokens"));
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
    public async Task HtmlInsteadOfJsonIsAProviderError()
    {
        var config = Compatible("http://192.168.1.1/v1", model: null);
        using var http = RecordedHandler.Client(out _, "html-page.html");
        var vault = new FakeVault();

        var e = await Assert.ThrowsAsync<ProviderException>(() => ProviderSetup.TestAndSaveAsync(null, config, Key, http, vault.Save, vault.Delete));
        Assert.StartsWith("The endpoint did not answer with JSON: <!doctype html>", e.Message);
        Assert.Empty(vault.Log);
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
    public async Task EventsSplitAcrossSevenByteReadsStillParse()
    {
        using var http = RecordedHandler.Client(out _, "anthropic-tool.sse", readSize: 7);
        var events = await Collect(new AnthropicProvider(http, Key).StreamAsync(Hello with { Tools = [Weather] }));

        Assert.Equal("Let me check.", Text(events));
        Assert.Equal("Paris", Assert.Single(events.OfType<ToolCallEvent>()).Call.Input.GetProperty("city").GetString());
        Assert.Equal(new TurnDone("tool_use", 120, 40), events[^1]);
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

    [VaultFact]
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

    // The settings page's provider section: ids, the form check, test then save, remove, errors, and what the palette gets.
    // Credential Manager is a dictionary here; only CredentialRoundTrip above touches the real one, with TOMTE_VAULT_TESTS=1.

    private static readonly ProviderConfig Anthropic = new(ProviderSetup.IdFor(ProviderKind.Anthropic, null), ProviderKind.Anthropic, DefaultModel: "claude-opus-5-5");

    private static ProviderConfig Compatible(string url, string? model = "m") =>
        new(ProviderSetup.IdFor(ProviderKind.OpenAICompatible, url), ProviderKind.OpenAICompatible, url, model);

    [Fact]
    public void ProviderIdsAreTheHostTheKeyGoesTo()
    {
        Assert.Equal("api.anthropic.com", ProviderSetup.IdFor(ProviderKind.Anthropic, "http://localhost:11434/v1"));
        Assert.Equal("generativelanguage.googleapis.com", ProviderSetup.IdFor(ProviderKind.Gemini, null));
        Assert.Equal("openrouter.ai", ProviderSetup.IdFor(ProviderKind.OpenAICompatible, "https://OpenRouter.ai/api/v1"));
        Assert.Equal("localhost:11434", ProviderSetup.IdFor(ProviderKind.OpenAICompatible, "http://localhost:11434/v1"));
    }

    [Fact]
    public void ASavedKeyShowsOnlyItsLastFourAndAShortOneNothing()
    {
        Assert.Equal("\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u20223fQa", ProviderSetup.Masked("sk-ant-api03-abcdefgh3fQa"));
        Assert.DoesNotContain("sk-", ProviderSetup.Masked("sk-ant-api03-abcdefgh3fQa"), StringComparison.Ordinal);
        Assert.Equal("\u2022\u2022\u2022\u2022\u2022\u2022\u2022\u2022", ProviderSetup.Masked("short-key-12"));
    }

    [Fact]
    public void CheckSaysWhatTheFormLacks()
    {
        var vault = new FakeVault();
        Assert.Equal("Enter the key.", ProviderSetup.Check(null, Anthropic, "  ", vault.Read, out _));
        Assert.StartsWith("Enter the server's address", ProviderSetup.Check(null, Compatible("localhost:11434/v1"), "", vault.Read, out _));
        Assert.StartsWith("Enter the server's address", ProviderSetup.Check(null, Compatible("ftp://example.com/v1"), "", vault.Read, out _));
        Assert.StartsWith("Tomte sends a key only over https", ProviderSetup.Check(null, Compatible("http://192.168.1.20:1234/v1"), Key, vault.Read, out _));
        Assert.StartsWith("The key has a space", ProviderSetup.Check(null, Anthropic, "sk-ant one", vault.Read, out _));
        Assert.StartsWith("The key has a space", ProviderSetup.Check(null, Anthropic, "sk-ant-é", vault.Read, out _));

        // A local server with or without a key, a server on the network without one, and a pasted key's line break trimmed.
        Assert.Null(ProviderSetup.Check(null, Compatible("http://localhost:11434/v1"), "", vault.Read, out var none));
        Assert.Null(none);
        Assert.Null(ProviderSetup.Check(null, Compatible("http://127.0.0.1:1234/v1"), Key, vault.Read, out _));
        Assert.Null(ProviderSetup.Check(null, Compatible("http://192.168.1.20:1234/v1"), "", vault.Read, out _));
        Assert.Null(ProviderSetup.Check(null, Anthropic, Key + "\r\n", vault.Read, out var typed));
        Assert.Equal(Key, typed);
    }

    [Fact]
    public void AnEmptyKeyBoxKeepsTheSavedKeyOnlyForTheSameHost()
    {
        var vault = new FakeVault();
        var openRouter = Compatible("https://openrouter.ai/api/v1");
        vault.Keys[openRouter.Id] = Key;

        Assert.Null(ProviderSetup.Check(openRouter, openRouter with { DefaultModel = "other" }, "", vault.Read, out var kept));
        Assert.Equal(Key, kept);
        // Another host never gets it: a local server is tested with no key, a cloud API asks for its own.
        Assert.Null(ProviderSetup.Check(openRouter, Compatible("http://localhost:11434/v1"), "", vault.Read, out var none));
        Assert.Null(none);
        Assert.Equal("Enter the key.", ProviderSetup.Check(openRouter, Anthropic, "", vault.Read, out _));
        // The same host over plain http would carry the saved key in the clear. The box is empty, so the way out is Remove.
        Assert.Equal(
            "The saved key would go over plain http to another PC. Use an https address, or press Remove and set the server up again without a key.",
            ProviderSetup.Check(openRouter, Compatible("http://openrouter.ai/api/v1"), "", vault.Read, out _));
    }

    [Fact]
    public async Task AKeyUnderAnOldIdIsFoundOnceAndMovedOnTheNextSave()
    {
        // Ids were the address's authority before they were plain ASCII; an international name shows the difference.
        var international = Compatible("https://bücher.example:8443/v1");
        string old = new Uri(international.BaseUrl!).Authority;
        Assert.Equal("xn--bcher-kva.example:8443", international.Id);
        Assert.NotEqual(old, international.Id);
        var vault = new FakeVault();
        vault.Keys[old] = Key;
        var saved = international with { Id = old };

        Assert.Null(ProviderSetup.Check(saved, international, "", vault.Read, out var found));
        Assert.Equal(Key, found);
        using var plain = new HttpClient();
        Assert.NotNull(ProviderSetup.ForPalette(new TomteSettings { Provider = saved }, plain, vault.Read));

        using var http = RecordedHandler.Client(out _, "openai-ollama-text.sse");
        await ProviderSetup.TestAndSaveAsync(saved, international, found, http, vault.Save, vault.Delete);
        Assert.Equal([$"save {international.Id}", $"delete {old}"], vault.Log);
    }

    [Fact]
    public void ThePaletteSendsNoKeyToAHostItWasNotFiledFor()
    {
        using var http = new HttpClient();
        var read = new List<string>();
        var logged = new List<string>();
        string? Vault(string id)
        {
            read.Add(id);
            return Key;
        }

        // A copied or hand-edited file: OpenRouter's address under Anthropic's id, whose key it would get.
        var copied = Compatible("https://openrouter.ai/api/v1") with { Id = Anthropic.Id };
        Assert.Null(ProviderSetup.ForPalette(new TomteSettings { Provider = copied }, http, Vault, logged.Add));
        Assert.Empty(read);
        Assert.Contains("filed under api.anthropic.com, but its address is openrouter.ai", Assert.Single(logged));

        // A key that would go over plain http to another PC; the same server with no key saved is fine.
        logged.Clear();
        var lan = Compatible("http://192.168.1.20:1234/v1");
        Assert.Null(ProviderSetup.ForPalette(new TomteSettings { Provider = lan }, http, Vault, logged.Add));
        Assert.Contains("plain http address on another PC", Assert.Single(logged));
        Assert.NotNull(ProviderSetup.ForPalette(new TomteSettings { Provider = lan }, http, _ => null));
    }

    [Fact]
    public void TheCapBoxNeverOverflows()
    {
        Assert.Null(ProviderSetup.CapFrom(double.NaN));
        Assert.Equal(1000m, ProviderSetup.CapFrom(1e30));
        Assert.Equal(1000m, ProviderSetup.CapFrom(double.PositiveInfinity));
        Assert.Equal(0m, ProviderSetup.CapFrom(-5));
        Assert.Equal(2.35m, ProviderSetup.CapFrom(2.346));
    }

    [Fact]
    public async Task AKeyNeverFollowsARedirect()
    {
        var probe = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        int port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        using var listener = new HttpListener { Prefixes = { $"http://localhost:{port}/" } };
        listener.Start();
        using var http = ProviderSetup.NewHttp();

        var listing = new OpenAICompatibleProvider(http, $"http://localhost:{port}/v1", Key).ListModelsAsync();
        var context = await listener.GetContextAsync();
        context.Response.StatusCode = 307;
        context.Response.RedirectLocation = $"http://localhost:{port}/elsewhere";
        context.Response.Close();

        // Following it would ask the listener again, which never answers: the wait would time out.
        var e = await Assert.ThrowsAsync<ProviderException>(() => listing.WaitAsync(TimeSpan.FromSeconds(10)));
        Assert.Equal(307, e.Status);
        Assert.StartsWith("The server sent Tomte to another address", ProviderSetup.Explain(e, Key));
    }

    [Fact]
    public void PricesComeFromTheTableAndAnUnknownCloudModelCountsHigh()
    {
        var cloud = ProviderSetup.OptionsFor(new TomteSettings(), Anthropic);
        Assert.Equal(new ModelPrice(4m, 20m), cloud.PriceFor("claude-opus-5-5"));
        Assert.Equal(new ModelPrice(1m, 5m), cloud.PriceFor("claude-haiku-4-5-20251001"));
        Assert.Equal(new ModelPrice(2.50m, 10m), cloud.PriceFor("gpt-4o-2024-08-06"));
        Assert.Equal(new ModelPrice(0.15m, 0.60m), cloud.PriceFor("openai/gpt-4o-mini"));
        Assert.Equal(new ModelPrice(0.30m, 2.50m), cloud.PriceFor("google/gemini-2.5-flash"));

        // An unknown model, and a variant with its own name, count at the table's highest price: $10 and $50 a million.
        Assert.Equal(new ModelPrice(10m, 50m), PriceTable.Highest);
        Assert.Equal(PriceTable.Highest, cloud.PriceFor("gpt-5-pro"));
        Assert.Equal(1.5m, SendLog.Estimate(cloud, "a-model-from-next-year", 100_000, 10_000));

        // A server on this PC is free unless the settings file prices a model; an override also replaces a table entry.
        var settings = new TomteSettings { ModelPrices = new Dictionary<string, ModelPrice> { ["llama3.2:3b"] = new(0.01m, 0.02m), ["claude-opus-5-5"] = new(1m, 2m) } };
        var local = ProviderSetup.OptionsFor(settings, Compatible("http://127.0.0.1:11434/v1"));
        Assert.Null(local.PriceFor("gpt-4o"));
        Assert.Equal(0m, SendLog.Estimate(local, "qwen2.5-coder:7b", 1_000_000, 1_000_000));
        Assert.Equal(new ModelPrice(0.01m, 0.02m), local.PriceFor("llama3.2:3b"));
        Assert.Equal(new ModelPrice(1m, 2m), ProviderSetup.OptionsFor(settings, Anthropic).PriceFor("claude-opus-5-5"));
    }

    [Fact]
    public async Task TheCapStopsTheNextSendOnceTheEstimatePassesIt()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"tomte-cap-{Guid.NewGuid():N}");
        try
        {
            var log = new SendLog(Path.Combine(dir, "send-log.jsonl"));
            using var http = RecordedHandler.Client(out var handler, "anthropic-text.sse");
            // Priced so that one short send costs more than the $1 cap.
            var settings = new TomteSettings
            {
                Provider = Anthropic with { DailyCapUsd = 1m },
                ModelPrices = new Dictionary<string, ModelPrice> { ["claude-opus-5-5"] = new(1_000_000m, 0m) },
            };
            var tools = new BuiltInTools(new InMemoryShelf(), new InMemoryScratchpad(), _ => { }, _ => { });
            PaletteSession Open()
            {
                var (provider, config, options) = ProviderSetup.ForPalette(settings, http, _ => Key)!.Value;
                return new PaletteSession(provider, config, options, log, tools, new PermissionGrants(), []) { Prompt = "hi" };
            }

            var first = Open();
            await first.SendAsync();
            Assert.Equal(PaletteStatus.Done, first.Status);
            Assert.Equal("Sent to Anthropic · claude-opus-5-5", first.ProviderIndicator);
            Assert.True(Assert.Single(log.ReadAll()).CostUsd > 1m);

            var second = Open();
            await second.SendAsync();
            Assert.Equal(PaletteStatus.Failed, second.Status);
            Assert.StartsWith("Daily cap of $1.00 reached for Anthropic.", second.Error);
            Assert.Single(handler.Bodies);
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TheFastModelServesOnlyVoiceCommands()
    {
        var config = Anthropic with { FastModel = "claude-haiku-5-5" };
        var tools = new BuiltInTools(new InMemoryShelf(), new InMemoryScratchpad(), _ => { }, _ => { });
        var log = new SendLog(Path.Combine(Path.GetTempPath(), $"tomte-fast-{Guid.NewGuid():N}.jsonl"));
        PaletteSession Session(ProviderConfig c, bool voice = false, bool scheduling = false) =>
            new(new ScriptedProvider(), c, new PaletteOptions(), log, tools, new PermissionGrants(), [])
            {
                Voice = voice,
                Scheduling = scheduling ? new Tomte.AI.Jobs.JobPlanner(null!, null!) : null,
            };

        Assert.Equal("claude-opus-5-5", Session(config).ModelId);
        Assert.Equal("claude-haiku-5-5", Session(config, voice: true).ModelId);
        // The AI box can make a job in any send, so it keeps its model; a job made by voice is read by the voice model.
        Assert.Equal("claude-opus-5-5", Session(config, scheduling: true).ModelId);
        Assert.Equal("claude-haiku-5-5", Session(config, voice: true, scheduling: true).ModelId);
        Assert.Equal("claude-opus-5-5", Session(Anthropic, voice: true).ModelId);
    }

    [Fact]
    public async Task FailedTestCallSavesAndDeletesNothing()
    {
        var vault = new FakeVault();
        var before = Compatible("https://openrouter.ai/api/v1");
        vault.Keys[before.Id] = "sk-or-old-key";
        using var http = RecordedHandler.Client(out _, "anthropic-401.json", HttpStatusCode.Unauthorized);

        var e = await Assert.ThrowsAsync<ProviderException>(() => ProviderSetup.TestAndSaveAsync(before, Anthropic, Key, http, vault.Save, vault.Delete));
        Assert.Empty(vault.Log);
        Assert.Equal("sk-or-old-key", vault.Read(before.Id));
        Assert.Equal("The provider did not accept the key. HTTP 401: invalid x-api-key: [key]", ProviderSetup.Explain(e, Key));
    }

    [Fact]
    public async Task PassingTestCallSavesTheKeyThenDropsTheOneItReplaces()
    {
        var vault = new FakeVault();
        var before = Compatible("https://openrouter.ai/api/v1");
        vault.Keys[before.Id] = "sk-or-old-key";
        using var http = RecordedHandler.Client(out var handler, "anthropic-text.sse");

        // The save asserts the test request went out first.
        var saved = await ProviderSetup.TestAndSaveAsync(before, Anthropic, Key, http, (id, key) =>
        {
            Assert.NotNull(handler.Body);
            vault.Save(id, key);
        }, vault.Delete);

        Assert.Equal(Anthropic, saved);
        Assert.Equal(["save api.anthropic.com", "delete openrouter.ai"], vault.Log);
        Assert.Equal(Key, vault.Read(Anthropic.Id));
        Assert.Equal(Key, handler.Request!.Headers.GetValues("x-api-key").Single());
        Assert.Equal(64, (int)JsonNode.Parse(handler.Body!)!["max_tokens"]!);
        Assert.DoesNotContain(Key, SettingsStore.Serialize(new TomteSettings { Provider = saved }));
    }

    [Fact]
    public async Task SavingTheSameProviderAgainKeepsItsKey()
    {
        var vault = new FakeVault();
        vault.Keys[Anthropic.Id] = Key;
        using var http = RecordedHandler.Client(out var handler, "anthropic-text.sse");

        Assert.Null(ProviderSetup.Check(Anthropic, Anthropic, "", vault.Read, out var key));
        await ProviderSetup.TestAndSaveAsync(Anthropic, Anthropic with { FastModel = "claude-haiku-5" }, key, http, vault.Save, vault.Delete);
        Assert.Equal(["save api.anthropic.com"], vault.Log);
        Assert.Equal(Key, handler.Request!.Headers.GetValues("x-api-key").Single());
    }

    [Fact]
    public async Task WithNoDefaultModelTheFirstListedOneIsTestedAndKept()
    {
        var vault = new FakeVault();
        var handler = new RecordedHandler("openai-ollama-models.json", then: ["openai-ollama-text.sse"]);
        using var http = new HttpClient(handler);

        var saved = await ProviderSetup.TestAndSaveAsync(null, Compatible("http://localhost:11434/v1", model: null), null, http, vault.Save, vault.Delete);
        Assert.Equal("llama3.2:3b", saved.DefaultModel);
        Assert.Equal("llama3.2:3b", (string?)JsonNode.Parse(handler.Body!)!["model"]);
        Assert.Empty(vault.Log);
    }

    [Fact]
    public void RemoveDeletesTheKeyOrNamesTheEntryWindowsKept()
    {
        var vault = new FakeVault();
        vault.Keys[Anthropic.Id] = Key;
        Assert.Null(ProviderSetup.Remove(Anthropic, vault.Delete));
        Assert.Null(vault.Read(Anthropic.Id));

        var left = ProviderSetup.Remove(Anthropic, _ => throw new InvalidOperationException("CredDelete failed: Access is denied."));
        Assert.Contains("could not delete Tomte/ai/api.anthropic.com", left);
    }

    [Fact]
    public void ErrorsAreExplainedInPlainWordsWithTheKeyHidden()
    {
        Assert.Equal("The provider did not answer within 60 seconds.", ProviderSetup.Explain(new TaskCanceledException(), Key));
        Assert.StartsWith("Nothing was found at that address", ProviderSetup.Explain(new ProviderException("HTTP 404: model: x", 404), Key));
        Assert.StartsWith("The provider is limiting requests", ProviderSetup.Explain(new ProviderException("HTTP 429: slow down", 429), Key));
        Assert.StartsWith("The server's answer is not in the shape", ProviderSetup.Explain(new KeyNotFoundException(), Key));
        Assert.Equal("echoed [key] back", ProviderSetup.Explain(new InvalidOperationException($"echoed {Key} back"), Key));
    }

    [Theory]
    [InlineData(ProviderKind.Anthropic, typeof(AnthropicProvider))]
    [InlineData(ProviderKind.Gemini, typeof(GeminiProvider))]
    [InlineData(ProviderKind.OpenAICompatible, typeof(OpenAICompatibleProvider))]
    public void ThePaletteGetsTheConfiguredProviderWithItsKeyAndCap(ProviderKind kind, Type adapter)
    {
        var config = new ProviderConfig(ProviderSetup.IdFor(kind, "http://localhost:1234/v1"), kind, "http://localhost:1234/v1", "m", DailyCapUsd: 0.5m);
        var asked = new List<string>();
        using var http = new HttpClient();

        var (provider, used, options) = ProviderSetup.ForPalette(new TomteSettings { Provider = config }, http, id =>
        {
            asked.Add(id);
            return Key;
        })!.Value;
        Assert.IsType(adapter, provider);
        Assert.Equal(config, used);
        Assert.Equal(0.5m, options.DailyCapUsd);
        Assert.Equal([config.Id], asked);
    }

    [Fact]
    public void WithNoProviderThePaletteGetsNoneAndReadsNoKey()
    {
        using var http = new HttpClient();
        Func<string, string?> read = _ => throw new InvalidOperationException("no key should be read");
        Assert.Null(ProviderSetup.ForPalette(new TomteSettings(), http, read));
        Assert.Null(ProviderSetup.ForPalette(new TomteSettings { Provider = Anthropic with { DefaultModel = null } }, http, read));
    }

    [Fact]
    public async Task TheCapInSettingsStopsTheSendBeforeAnythingLeaves()
    {
        string dir = Path.Combine(Path.GetTempPath(), $"tomte-cap-{Guid.NewGuid():N}");
        try
        {
            var log = new SendLog(Path.Combine(dir, "send-log.jsonl"));
            log.Append(new SendLogEntry(DateTimeOffset.Now, Anthropic.Id, "claude-opus-5-5", 1, 1, 0.5m, [], null));
            using var http = RecordedHandler.Client(out var handler, "anthropic-text.sse");
            var tools = new BuiltInTools(new InMemoryShelf(), new InMemoryScratchpad(), _ => { }, _ => { });
            PaletteSession Open(decimal? cap)
            {
                var (provider, config, options) = ProviderSetup.ForPalette(new TomteSettings { Provider = Anthropic with { DailyCapUsd = cap } }, http, _ => Key)!.Value;
                return new PaletteSession(provider, config, options, log, tools, new PermissionGrants(), []) { Prompt = "hi" };
            }

            var capped = Open(0.5m);
            await capped.SendAsync();
            Assert.Equal(PaletteStatus.Failed, capped.Status);
            Assert.Contains("Daily cap of $0.50", capped.Error);
            Assert.Null(handler.Request);

            // No cap: the same spend sends.
            var open = Open(null);
            await open.SendAsync();
            Assert.Equal(PaletteStatus.Done, open.Status);
            Assert.Equal(Key, handler.Request!.Headers.GetValues("x-api-key").Single());
        }
        finally
        {
            if (Directory.Exists(dir))
                Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>Credential Manager as a dictionary, with what was saved and deleted in order. Names ignore case, as the vault's do.</summary>
    private sealed class FakeVault
    {
        public Dictionary<string, string> Keys { get; } = new(StringComparer.OrdinalIgnoreCase);

        public List<string> Log { get; } = [];

        public string? Read(string id) => Keys.GetValueOrDefault(id);

        public void Save(string id, string key)
        {
            Log.Add($"save {id}");
            Keys[id] = key;
        }

        public void Delete(string id)
        {
            Log.Add($"delete {id}");
            Keys.Remove(id);
        }
    }
}
