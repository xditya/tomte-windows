using System.Text.RegularExpressions;
using Tomte.Core;

namespace Tomte.AI.Palette;

/// <summary>
/// List prices in US dollars per million input and output tokens for common cloud models, as of <see cref="AsOf"/>:
/// Anthropic's from its model table of 2026-10-06, Gemini's and OpenAI's as last published before then, so check
/// them. Long prompts past a model's long-context threshold cost more than this. ModelPrices in the settings file
/// replace any entry and add others.
/// </summary>
public static partial class PriceTable
{
    public static readonly DateOnly AsOf = new(2026, 10, 14);

    public static readonly IReadOnlyDictionary<string, ModelPrice> Models = new Dictionary<string, ModelPrice>
    {
        ["claude-fable-5-1"] = new(10m, 50m),
        ["claude-fable-5"] = new(10m, 50m),
        ["claude-opus-5-5"] = new(4m, 20m),
        ["claude-opus-5"] = new(5m, 25m),
        ["claude-opus-4-8"] = new(5m, 25m),
        ["claude-opus-4-7"] = new(5m, 25m),
        ["claude-opus-4-6"] = new(5m, 25m),
        ["claude-sonnet-5-5"] = new(2m, 10m),
        ["claude-sonnet-5"] = new(2m, 10m),
        ["claude-sonnet-4-6"] = new(3m, 15m),
        ["claude-haiku-5-5"] = new(0.10m, 0.50m),
        ["claude-haiku-4-5"] = new(1m, 5m),
        ["gemini-2.5-pro"] = new(1.25m, 10m),
        ["gemini-2.5-flash"] = new(0.30m, 2.50m),
        ["gemini-2.5-flash-lite"] = new(0.10m, 0.40m),
        ["gpt-5"] = new(1.25m, 10m),
        ["gpt-5-mini"] = new(0.25m, 2m),
        ["gpt-5-nano"] = new(0.05m, 0.40m),
        ["gpt-4.1"] = new(2m, 8m),
        ["gpt-4.1-mini"] = new(0.40m, 1.60m),
        ["gpt-4.1-nano"] = new(0.10m, 0.40m),
        ["gpt-4o"] = new(2.50m, 10m),
        ["gpt-4o-mini"] = new(0.15m, 0.60m),
        ["o3"] = new(2m, 8m),
        ["o4-mini"] = new(1.10m, 4.40m),
    };

    /// <summary>The highest input and the highest output price above: what a cloud model the table lacks counts at.</summary>
    public static readonly ModelPrice Highest = new(Models.Values.Max(p => p.InputPerMillion), Models.Values.Max(p => p.OutputPerMillion));

    /// <summary>
    /// The table with <paramref name="overrides"/> on top; for a server on this PC, the overrides alone, so a local
    /// model that shares a cloud model's name costs nothing unless the user priced it.
    /// </summary>
    public static IReadOnlyDictionary<string, ModelPrice> With(IReadOnlyDictionary<string, ModelPrice> overrides, bool local)
    {
        var merged = new Dictionary<string, ModelPrice>();
        foreach (var (key, price) in local ? overrides : Models.Concat(overrides))
            merged[Plain(key)] = price;
        return merged;
    }

    /// <summary>
    /// The price for a model id: the entry of that name, or the one it is a dated release or a tag of
    /// (claude-haiku-4-5-20251001, gpt-4o-2024-08-06, gemini-2.5-flash-001, name@version, name:tag), ignoring case,
    /// dots against dashes, and a "vendor/" prefix as OpenRouter writes it. A variant with its own name (gpt-4o-mini,
    /// a -pro) never takes its family's price. Null when nothing fits.
    /// </summary>
    public static ModelPrice? Find(IReadOnlyDictionary<string, ModelPrice> prices, string model)
    {
        string name = Plain(model);
        foreach (var (key, price) in prices)
        {
            string entry = Plain(key);
            if (name == entry || (name.StartsWith(entry, StringComparison.Ordinal) && Release().IsMatch(name[entry.Length..])))
                return price;
        }

        return null;
    }

    private static string Plain(string id) => id[(id.LastIndexOf('/') + 1)..].ToLowerInvariant().Replace('.', '-');

    [GeneratedRegex(@"^(-\d{8}|-\d{4}-\d{2}-\d{2}|-\d{3}|-latest|[@:].*)$")]
    private static partial Regex Release();
}
