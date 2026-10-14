namespace Tomte.Core;

public enum ProviderKind { Anthropic, Gemini, OpenAICompatible }

/// <summary>
/// One configured AI provider as settings store it. The key is not here: it lives in Credential
/// Manager under <see cref="Id"/>. <see cref="BaseUrl"/> is only used by OpenAI-compatible endpoints.
/// <see cref="DailyCapUsd"/> is the most the palette spends with this provider in a day; null means no cap.
/// </summary>
public sealed record ProviderConfig(
    string Id,
    ProviderKind Kind,
    string? BaseUrl = null,
    string? DefaultModel = null,
    string? FastModel = null,
    decimal? DailyCapUsd = 2m);

/// <summary>Price per million tokens in US dollars, as the built-in table and the settings file's overrides store it.</summary>
public sealed record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion);
