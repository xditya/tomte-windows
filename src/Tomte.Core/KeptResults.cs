using System.Globalization;
using Microsoft.Data.Sqlite;
using Tomte.Core.Storage;

namespace Tomte.Core;

/// <summary>
/// A drop zone's finished result as kept: the zone (and the skill, for the skill slot), the file it was about, where
/// it went and what it cost, when, the model's whole answer, and the shelf paths of the files it left there.
/// </summary>
public sealed record KeptResult(DropZone Zone, string? Skill, string SourceName, string SourcePath, string Provider, string Model, decimal CostUsd, DateTimeOffset At, string Answer, IReadOnlyList<string> Files)
{
    /// <summary>The line the panel shows over a kept result: when it was made, by which model, at what cost.</summary>
    public string Line(IFormatProvider? culture = null) => string.Create(culture ?? CultureInfo.CurrentCulture,
        $"Kept from {At.ToLocalTime():d MMM, HH:mm} · {Model} · {Cost(CostUsd)}");

    private static string Cost(decimal usd) => usd == 0 ? "no cost" : string.Create(CultureInfo.InvariantCulture, $"${usd:0.00##}");
}

/// <summary>
/// Every finished drop zone result, in tomte.db (feature "results"), so a result is paid for once: a later drop of the
/// same file on the same zone shows the kept one first. The same file is the same path, ignoring case, with the size
/// and the last write time it had just before it was sent; a file changed since is a new file. Work runs on the pool,
/// never on the island's thread; trouble with the disk or the database is logged and loses only that one result.
/// </summary>
public sealed class KeptResults(TomteDb db, Action<string>? log = null)
{
    private const string Feature = "results";

    // The whole history, oldest first. Never edit or reorder a step that has merged; add at the end.
    private static readonly string[] Schema =
    [
        "CREATE TABLE results_kept(id INTEGER PRIMARY KEY, zone TEXT NOT NULL, skill TEXT, source_name TEXT NOT NULL, source_path TEXT NOT NULL, " +
        "source_stamp TEXT, provider TEXT NOT NULL, model TEXT NOT NULL, cost_usd TEXT NOT NULL, at INTEGER NOT NULL, answer TEXT NOT NULL, files TEXT NOT NULL)",
    ];

    /// <param name="stamp">The file's <see cref="Stamp"/> from just before the send, so an edit during the send makes it a new file.</param>
    public Task Keep(KeptResult result, string? stamp) => Task.Run(() =>
    {
        try
        {
            using var connection = db.Open(Feature, Schema);
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO results_kept(zone, skill, source_name, source_path, source_stamp, provider, model, cost_usd, at, answer, files)
                VALUES ($zone, $skill, $name, $path, $stamp, $provider, $model, $cost, $at, $answer, $files)
                """;
            insert.Parameters.AddWithValue("$zone", result.Zone.ToString());
            insert.Parameters.AddWithValue("$skill", (object?)result.Skill ?? DBNull.Value);
            insert.Parameters.AddWithValue("$name", result.SourceName);
            insert.Parameters.AddWithValue("$path", result.SourcePath);
            insert.Parameters.AddWithValue("$stamp", (object?)stamp ?? DBNull.Value);
            insert.Parameters.AddWithValue("$provider", result.Provider);
            insert.Parameters.AddWithValue("$model", result.Model);
            insert.Parameters.AddWithValue("$cost", result.CostUsd.ToString(CultureInfo.InvariantCulture));
            insert.Parameters.AddWithValue("$at", result.At.ToUnixTimeMilliseconds());
            insert.Parameters.AddWithValue("$answer", result.Answer);
            insert.Parameters.AddWithValue("$files", string.Join('\n', result.Files));
            insert.ExecuteNonQuery();
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"results: could not keep the {result.Zone} result for {result.SourceName}: {e.Message}");
        }
    });

    /// <summary>The newest result kept for this zone (and skill) and this file as it is now, or null.</summary>
    public Task<KeptResult?> Last(DropZone zone, string? skill, string sourcePath) => Task.Run(() =>
    {
        try
        {
            using var connection = db.Open(Feature, Schema);
            using var select = connection.CreateCommand();
            select.CommandText = """
                SELECT skill, source_name, source_path, source_stamp, provider, model, cost_usd, at, answer, files FROM results_kept
                WHERE zone = $zone AND skill IS $skill AND source_path = $path COLLATE NOCASE ORDER BY at DESC, id DESC LIMIT 1
                """;
            select.Parameters.AddWithValue("$zone", zone.ToString());
            select.Parameters.AddWithValue("$skill", (object?)skill ?? DBNull.Value);
            select.Parameters.AddWithValue("$path", sourcePath);
            using var row = select.ExecuteReader();
            if (!row.Read() || row.IsDBNull(3) || row.GetString(3) != Stamp(sourcePath))
            {
                return null;
            }

            return new KeptResult(zone, row.IsDBNull(0) ? null : row.GetString(0), row.GetString(1), row.GetString(2), row.GetString(4), row.GetString(5),
                decimal.Parse(row.GetString(6), CultureInfo.InvariantCulture), DateTimeOffset.FromUnixTimeMilliseconds(row.GetInt64(7)), row.GetString(8),
                row.GetString(9).Split('\n', StringSplitOptions.RemoveEmptyEntries));
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or FormatException)
        {
            log?.Invoke($"results: could not look for a kept {zone} result: {e.Message}");
            return null;
        }
    });

    /// <summary>
    /// What says a file is still the one a result was made from: its size and last write time; null when it is gone.
    /// It reads the disk, which can be a network share: call it off the island's thread.
    /// </summary>
    public static string? Stamp(string path)
    {
        try
        {
            var file = new FileInfo(path);
            if (file.Exists)
            {
                return string.Create(CultureInfo.InvariantCulture, $"{file.Length}:{file.LastWriteTimeUtc.Ticks}");
            }

            var folder = new DirectoryInfo(path);
            return folder.Exists ? string.Create(CultureInfo.InvariantCulture, $"folder:{folder.LastWriteTimeUtc.Ticks}") : null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }
}
