namespace Tomte.Core;

public enum ProviderKind { Anthropic, Gemini, OpenAICompatible }

/// <summary>
/// One configured AI provider as settings store it. The key is not here: it lives in Credential
/// Manager under <see cref="Id"/>. <see cref="BaseUrl"/> is only used by OpenAI-compatible endpoints.
/// </summary>
public sealed record ProviderConfig(
    string Id,
    ProviderKind Kind,
    string? BaseUrl = null,
    string? DefaultModel = null,
    string? FastModel = null);
