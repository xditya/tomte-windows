using System.Text.Json;

namespace Tomte.AI.Palette;

/// <summary>Price per million tokens, as the user's editable table stores it.</summary>
public sealed record ModelPrice(decimal InputPerMillion, decimal OutputPerMillion);

/// <summary>
/// One send: time, provider, model, tokens, cost and the attached item names. Content only when the user turned that on.
/// Job is the scheduled job's name when a job sent it, null when the user did.
/// </summary>
public sealed record SendLogEntry(
    DateTimeOffset Time,
    string Provider,
    string Model,
    int InputTokens,
    int OutputTokens,
    decimal CostUsd,
    IReadOnlyList<string> Attached,
    string? Content,
    string? Job = null);

/// <summary>
/// The local send log, one JSON line per send under the app's data folder. Read only when the
/// palette needs today's spend, written once per send; nothing is held open.
/// </summary>
public sealed class SendLog(string path)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public string Path { get; } = path;

    public void Append(SendLogEntry entry)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(Path)!);
        File.AppendAllText(Path, JsonSerializer.Serialize(entry, Json) + Environment.NewLine);
    }

    public IReadOnlyList<SendLogEntry> ReadAll()
    {
        if (!File.Exists(Path))
            return [];
        return [.. File.ReadLines(Path).Where(l => l.Length > 0).Select(l => JsonSerializer.Deserialize<SendLogEntry>(l, Json)!)];
    }

    /// <summary>Spend since local midnight for one provider.</summary>
    public decimal SpentToday(string provider, DateTimeOffset now)
    {
        var start = new DateTimeOffset(now.Date, now.Offset);
        return ReadAll().Where(e => e.Provider == provider && e.Time >= start).Sum(e => e.CostUsd);
    }

    public void Clear()
    {
        if (File.Exists(Path))
            File.Delete(Path);
    }

    /// <summary>Estimated from the usage fields and the price table; a model with no price costs 0 (local models).</summary>
    public static decimal Estimate(IReadOnlyDictionary<string, ModelPrice> prices, string model, int inputTokens, int outputTokens) =>
        prices.TryGetValue(model, out var p) ? (inputTokens * p.InputPerMillion + outputTokens * p.OutputPerMillion) / 1_000_000m : 0m;
}
