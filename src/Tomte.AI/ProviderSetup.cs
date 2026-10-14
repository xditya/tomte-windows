using Tomte.Core;

namespace Tomte.AI;

public static class ProviderSetup
{
    public static IChatProvider Create(ProviderConfig config, HttpClient http, string? key) => config.Kind switch
    {
        ProviderKind.Anthropic => new AnthropicProvider(http, key ?? ""),
        ProviderKind.Gemini => new GeminiProvider(http, key ?? ""),
        ProviderKind.OpenAICompatible => new OpenAICompatibleProvider(
            http, config.BaseUrl ?? throw new ArgumentException("An OpenAI-compatible provider needs a base URL."), key),
        _ => throw new ArgumentOutOfRangeException(nameof(config)),
    };

    /// <summary>
    /// What the configured model is, where the endpoint says so without a cloud call: a local Ollama's vision and context.
    /// Null for everything else, which then keeps the provider's defaults.
    /// </summary>
    public static async Task<ModelInfo?> DescribeAsync(IChatProvider provider, ProviderConfig config, CancellationToken ct = default) =>
        provider is OpenAICompatibleProvider local && config.DefaultModel is { } model ? await local.DescribeAsync(model, ct) : null;

    /// <summary>Builds the provider with the key from Credential Manager, or none for a local endpoint.</summary>
    public static IChatProvider Load(ProviderConfig config, HttpClient http) =>
        Create(config, http, CredentialStore.Read(config.Id));

    /// <summary>
    /// The "test call on save": one tiny request with the new key. The key is stored only if the
    /// call succeeds, so a typo never replaces a working key. Throws <see cref="ProviderException"/>
    /// with the provider's message otherwise. Returns the model the call used.
    /// </summary>
    public static async Task<string> TestAndSaveAsync(ProviderConfig config, string? key, HttpClient http, CancellationToken ct = default)
    {
        var provider = Create(config, http, key);
        var model = config.DefaultModel;
        if (string.IsNullOrEmpty(model))
        {
            var models = await provider.ListModelsAsync(ct);
            model = models.Count > 0 ? models[0].Id : throw new ProviderException("This endpoint lists no models. Type a model id.");
        }

        var request = new ChatRequest(model, [ChatMessage.User("Reply with the word OK.")], MaxTokens: 64);
        await foreach (var _ in provider.StreamAsync(request, ct))
        {
        }

        if (!string.IsNullOrEmpty(key))
            CredentialStore.Save(config.Id, key);
        return model;
    }
}
