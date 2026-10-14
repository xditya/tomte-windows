using Tomte.AI.Palette;
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

    /// <summary>How long the settings page waits for a test call or a model list.</summary>
    public static readonly TimeSpan TestLimit = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The id a provider's key is stored under and its spend is counted by: the host and port the key is sent to. A
    /// key is only ever sent to the server it was typed for, and two kinds of API at one host share it.
    /// </summary>
    public static string IdFor(ProviderKind kind, string? baseUrl) => kind switch
    {
        ProviderKind.Anthropic => "api.anthropic.com",
        ProviderKind.Gemini => "generativelanguage.googleapis.com",
        _ => Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) ? uri.Authority : "",
    };

    /// <summary>
    /// Null when the settings form can be tested, with the key to test in <paramref name="key"/>: the typed one, or the
    /// saved one while the provider stays at the same host. Otherwise what to fix, in plain words.
    /// </summary>
    /// <param name="saved">The provider settings hold now, if any.</param>
    public static string? Check(ProviderConfig? saved, ProviderConfig config, string typed, Func<string, string?> read, out string? key)
    {
        key = typed.Trim() is { Length: > 0 } trimmed ? trimmed
            : saved is not null && string.Equals(saved.Id, config.Id, StringComparison.OrdinalIgnoreCase) ? read(saved.Id)
            : null;
        if (config.Kind == ProviderKind.OpenAICompatible)
        {
            if (!Uri.TryCreate(config.BaseUrl, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
                return "Enter the server's address with http:// or https://, for example http://localhost:11434/v1.";
            if (key is not null && uri.Scheme == "http" && !uri.IsLoopback)
                return "Tomte sends a key only over https or to a server on this PC. Use an https address, or clear the key if the server needs none.";
        }
        else if (key is null)
        {
            return "Enter the key.";
        }

        // Keys are printable ASCII with no spaces; anything else is a paste gone wrong, and HTTP headers refuse most of it.
        return key is not null && key.Any(c => c is <= ' ' or > '~')
            ? "The key has a space or a character that keys do not have. Copy it again from the provider's site."
            : null;
    }

    /// <summary>
    /// The test call on save: one tiny request with <paramref name="key"/>. Only once it succeeds is the key saved under
    /// the provider's id and the key of the provider it replaces deleted, so a typo never replaces a working key.
    /// Without a default model it uses the first one the endpoint lists. Returns the provider for settings, with the model
    /// the call used. Throws <see cref="ProviderException"/> with the provider's message otherwise.
    /// </summary>
    /// <param name="saved">The provider settings hold now, if any.</param>
    public static async Task<ProviderConfig> TestAndSaveAsync(ProviderConfig? saved, ProviderConfig config, string? key, HttpClient http, Action<string, string> save, Action<string> delete, CancellationToken ct = default)
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
            save(config.Id, key);
        // Credential Manager ignores case in its names, so the same id in another case is the entry just written.
        if (saved is not null && !string.Equals(saved.Id, config.Id, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                delete(saved.Id);
            }
            catch (InvalidOperationException)
            {
                // The new provider is set up either way; a key left behind is only ever sent to the host it was typed for.
            }
        }

        return config with { DefaultModel = model };
    }

    /// <summary>
    /// Deletes the provider's key. Null when it is gone; otherwise a message naming the entry Windows would not
    /// delete, so the user can delete it in Credential Manager. Settings drop the provider either way.
    /// </summary>
    public static string? Remove(ProviderConfig config, Action<string> delete)
    {
        try
        {
            delete(config.Id);
            return null;
        }
        catch (InvalidOperationException)
        {
            return $"The provider is removed, but Windows Credential Manager could not delete {CredentialStore.Prefix}{config.Id}. Delete it there, under Windows Credentials.";
        }
    }

    /// <summary>
    /// What went wrong with a test call or a model list, in plain words for the settings page, with
    /// <paramref name="key"/> hidden. The adapters already keep the key out of their messages; this is the backstop.
    /// </summary>
    public static string Explain(Exception e, string? key) => ProviderHttp.Scrub(e switch
    {
        OperationCanceledException => $"The provider did not answer within {TestLimit.TotalSeconds:0} seconds.",
        ProviderException { Status: 401 or 403 } => $"The provider did not accept the key. {e.Message}",
        ProviderException { Status: 404 } => $"Nothing was found at that address, or there is no such model. {e.Message}",
        ProviderException { Status: 429 } => $"The provider is limiting requests, or the account is out of credit. {e.Message}",
        // The JSON had no field the adapter reads: an answer from something that is not this kind of API.
        KeyNotFoundException => "The server's answer is not in the shape Tomte expects. Check the address.",
        _ => e.Message,
    }, key);

    /// <summary>
    /// The palette's provider, with the key <paramref name="readKey"/> returns for its id, and the palette's knobs from
    /// settings. Null when no provider is set up, so the palette says the AI layer is off.
    /// </summary>
    public static (IChatProvider Provider, ProviderConfig Config, PaletteOptions Options)? ForPalette(TomteSettings settings, HttpClient http, Func<string, string?> readKey) =>
        settings.Provider is { DefaultModel.Length: > 0 } config
            ? (Create(config, http, readKey(config.Id)), config, new PaletteOptions { DailyCapUsd = config.DailyCapUsd })
            : null;
}
