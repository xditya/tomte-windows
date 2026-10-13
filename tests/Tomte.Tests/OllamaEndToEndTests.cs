using System.Text.Json;
using Tomte.AI;
using Tomte.Core;
using Xunit;

namespace Tomte.Tests;

/// <summary>Runs only when Ollama is listening on 127.0.0.1:11434 with the model pulled; skipped otherwise.</summary>
public sealed class OllamaFactAttribute : FactAttribute
{
    public const string Model = "llama3.2:3b";

    public OllamaFactAttribute()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(1) };
            if (!http.GetStringAsync("http://127.0.0.1:11434/api/tags").GetAwaiter().GetResult().Contains($"\"{Model}\""))
                Skip = $"Ollama is running but {Model} is not pulled (ollama pull {Model}).";
        }
        catch (Exception e) when (e is HttpRequestException or TaskCanceledException)
        {
            Skip = "Ollama is not reachable on 127.0.0.1:11434.";
        }
    }
}

public class OllamaEndToEndTests
{
    private static readonly ProviderConfig Config = new("ollama-e2e", ProviderKind.OpenAICompatible, "http://127.0.0.1:11434/v1", OllamaFactAttribute.Model);

    [OllamaFact]
    public async Task ListsModelsAndPassesTheTestCall()
    {
        using var http = new HttpClient();
        var provider = ProviderSetup.Create(Config, http, null);

        Assert.Contains((await provider.ListModelsAsync()).Select(m => m.Id), id => id == OllamaFactAttribute.Model);
        Assert.Equal(OllamaFactAttribute.Model, await ProviderSetup.TestAndSaveAsync(Config, null, http));
    }

    [OllamaFact]
    public async Task OutputTokenCapIsHonoured()
    {
        using var http = new HttpClient();
        var provider = ProviderSetup.Create(Config, http, null);
        var events = await ProviderTests.Collect(provider.StreamAsync(new(OllamaFactAttribute.Model,
            [ChatMessage.User("Write a 500-word essay about the history of bread.")], MaxTokens: 8)));
        Assert.InRange(((TurnDone)events[^1]).OutputTokens, 1, 8);
    }

    [OllamaFact]
    public async Task ToolCallRoundTrip()
    {
        using var http = new HttpClient();
        var provider = ProviderSetup.Create(Config, http, null);
        var weather = new ToolDefinition("get_weather", "Get current weather for a city",
            JsonDocument.Parse("""{"type":"object","properties":{"city":{"type":"string"}},"required":["city"]}""").RootElement);
        var question = ChatMessage.User("What is the weather in Paris? Use the tool.");

        var first = await ProviderTests.Collect(provider.StreamAsync(new(OllamaFactAttribute.Model, [question], Tools: [weather])));
        var call = Assert.Single(first.OfType<ToolCallEvent>()).Call;
        Assert.Equal("get_weather", call.Name);

        var second = await ProviderTests.Collect(provider.StreamAsync(new(OllamaFactAttribute.Model,
            [question, new(ChatRole.Assistant, [call]), new(ChatRole.User, [new ToolResultPart(call.Id, call.Name, "18 C and sunny")])],
            Tools: [weather])));
        Assert.Contains("18", ProviderTests.Text(second));
        Assert.True(((TurnDone)second[^1]).OutputTokens > 0);
    }
}
