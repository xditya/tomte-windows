using System.Text.Json;

namespace Tomte.AI.Palette;

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

    /// <summary>
    /// Estimated from the usage fields and the model's price (<see cref="PaletteOptions.PriceFor"/>), which counts an
    /// unknown cloud model at the highest known price; a model with no price costs 0 (a server on this PC).
    /// </summary>
    public static decimal Estimate(PaletteOptions options, string model, int inputTokens, int outputTokens) =>
        options.PriceFor(model) is { } p ? (inputTokens * p.InputPerMillion + outputTokens * p.OutputPerMillion) / 1_000_000m : 0m;
}
