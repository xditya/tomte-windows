using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Tomte.Core.Storage;

namespace Tomte.Core;

/// <summary>Where a conversation with the AI started.</summary>
public enum AiSource { Palette, Voice, CircleToAsk, DropZone, Job }

/// <summary>
/// One turn of a conversation: what the user typed or said, the names of the context chips that were on (never what
/// they attached), one line per tool call, the answer's text, and the error line of a turn that failed or was cancelled.
/// </summary>
public sealed record AiTurn(string Prompt, IReadOnlyList<string> Chips, IReadOnlyList<string> Steps, string Answer, string? Error = null);

/// <summary>
/// One conversation as the history keeps it: every turn in order, the provider and model of its last send, what all of
/// it cost, and when it last changed. A drop zone's result is a conversation of one turn.
/// </summary>
public sealed record AiExchange(string Key, AiSource Source, DateTimeOffset At, string Provider, string Model, decimal CostUsd, IReadOnlyList<AiTurn> Turns)
{
    /// <summary>The first question, which names the conversation in the list.</summary>
    public string Question => Turns.Count == 0 ? "" : Turns[0].Prompt;

    /// <summary>What "Copy answer" copies: the last turn's answer.</summary>
    public string LastAnswer => Turns.Count == 0 ? "" : Turns[^1].Answer;

    public static string NewKey() => Guid.NewGuid().ToString("N");
}

/// <summary>
/// The AI history in tomte.db: every finished conversation from the palette, circle-to-ask, voice commands and jobs,
/// read together with the drop zones' kept results (<see cref="KeptResults"/>), which are not stored twice. At most
/// <see cref="MaxItems"/> conversations and results together, none older than <see cref="MaxDays"/> days, pruned on
/// every write and read. Off, nothing is written, and a read clears both tables. Nothing touches the database until
/// something reads or writes; the work runs in order on the pool, never on the island's thread, and trouble with the
/// disk or the database is logged and loses only that one write.
/// </summary>
public sealed class AiHistory(TomteDb db, Func<bool> on, Action<string>? log = null)
{
    public const int MaxItems = 500, MaxDays = 90;

    // A drop zone result's key: this and its row id in results_kept.
    private const string KeptKey = "kept:";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly Lock gate = new();
    private Task queue = Task.CompletedTask;

    /// <summary>Whether the setting keeps a history now; a read of settings, not of the database.</summary>
    public bool On => on();

    /// <summary>Writes the conversation under its key: a new row the first time, the same row with every turn after that.</summary>
    public Task Keep(AiExchange exchange) => Enqueue(() =>
    {
        if (!on() || exchange.Source == AiSource.DropZone)
        {
            return;
        }

        Logged("keep a conversation", () =>
        {
            using var connection = db.Open(KeptResults.Feature, KeptResults.Schema);
            using var transaction = connection.BeginTransaction();
            Run(connection, """
                INSERT INTO results_history(key, source, provider, model, cost_usd, at, turns) VALUES ($key, $source, $provider, $model, $cost, $at, $turns)
                ON CONFLICT(key) DO UPDATE SET provider = excluded.provider, model = excluded.model, cost_usd = excluded.cost_usd, at = excluded.at, turns = excluded.turns
                """,
                ("$key", exchange.Key), ("$source", exchange.Source.ToString()), ("$provider", exchange.Provider), ("$model", exchange.Model),
                ("$cost", exchange.CostUsd.ToString(CultureInfo.InvariantCulture)), ("$at", exchange.At.ToUnixTimeMilliseconds()),
                ("$turns", JsonSerializer.Serialize(exchange.Turns, Json)));
            Prune(connection, exchange.At);
            transaction.Commit();
        });
    });

    /// <summary>Newest first, conversations and drop zone results together. Off, there are none, and anything still stored is cleared.</summary>
    public Task<IReadOnlyList<AiExchange>> All(DateTimeOffset now) => Enqueue<IReadOnlyList<AiExchange>>(() =>
    {
        if (!on())
        {
            Logged("clear the history", ClearNow);
            return [];
        }

        IReadOnlyList<AiExchange> all = [];
        Logged("read the history", () =>
        {
            using var connection = db.Open(KeptResults.Feature, KeptResults.Schema);
            Prune(connection, now);
            var list = new List<AiExchange>();
            using (var history = Command(connection, "SELECT key, source, provider, model, cost_usd, at, turns FROM results_history ORDER BY at DESC, id DESC LIMIT $max", ("$max", MaxItems)))
            using (var row = history.ExecuteReader())
            {
                while (row.Read())
                {
                    list.Add(new AiExchange(row.GetString(0), Enum.Parse<AiSource>(row.GetString(1)), At(row.GetInt64(5)), row.GetString(2), row.GetString(3),
                        Cost(row.GetString(4)), JsonSerializer.Deserialize<List<AiTurn>>(row.GetString(6), Json) ?? []));
                }
            }

            using (var kept = Command(connection, "SELECT id, zone, skill, source_name, provider, model, cost_usd, at, answer FROM results_kept ORDER BY at DESC, id DESC LIMIT $max", ("$max", MaxItems)))
            using (var row = kept.ExecuteReader())
            {
                while (row.Read())
                {
                    string zone = DropZoneLayout.Label(Enum.Parse<DropZone>(row.GetString(1)), row.IsDBNull(2) ? null : row.GetString(2));
                    string name = row.GetString(3);
                    list.Add(new AiExchange(KeptKey + row.GetInt64(0).ToString(CultureInfo.InvariantCulture), AiSource.DropZone, At(row.GetInt64(7)), row.GetString(4),
                        row.GetString(5), Cost(row.GetString(6)), [new AiTurn($"{zone}: {name}", [name], [], row.GetString(8))]));
                }
            }

            all = [.. list.OrderByDescending(e => e.At).Take(MaxItems)];
        });
        return all;
    });

    /// <summary>Deletes every conversation and every kept drop zone result.</summary>
    public Task Clear() => Enqueue(() => Logged("clear the history", ClearNow));

    /// <summary>
    /// Drops what is older than <see cref="MaxDays"/> days before <paramref name="now"/>, then all but the newest
    /// <see cref="MaxItems"/> of both tables together. Simplification: rows that share the cut-off time all stay, so a few
    /// more than the cap can remain.
    /// </summary>
    internal static void Prune(SqliteConnection connection, DateTimeOffset now)
    {
        long cutoff = now.AddDays(-MaxDays).ToUnixTimeMilliseconds();
        using (var oldest = Command(connection, "SELECT at FROM (SELECT at FROM results_history UNION ALL SELECT at FROM results_kept) ORDER BY at DESC LIMIT 1 OFFSET $last", ("$last", MaxItems - 1)))
        {
            if (oldest.ExecuteScalar() is long kept)
            {
                cutoff = Math.Max(cutoff, kept);
            }
        }

        Run(connection, "DELETE FROM results_history WHERE at < $cutoff", ("$cutoff", cutoff));
        Run(connection, "DELETE FROM results_kept WHERE at < $cutoff", ("$cutoff", cutoff));
    }

    private void ClearNow()
    {
        using var connection = db.Open(KeptResults.Feature, KeptResults.Schema);
        Run(connection, "DELETE FROM results_history; DELETE FROM results_kept;");
    }

    // The exception's type and message, never the conversation.
    private void Logged(string what, Action work)
    {
        try
        {
            work();
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException or JsonException or ArgumentException or FormatException)
        {
            log?.Invoke($"history: could not {what} ({e.GetType().Name}: {e.Message})");
        }
    }

    private static DateTimeOffset At(long ms) => DateTimeOffset.FromUnixTimeMilliseconds(ms);

    private static decimal Cost(string text) => decimal.Parse(text, CultureInfo.InvariantCulture);

    private static void Run(SqliteConnection connection, string sql, params ReadOnlySpan<(string Name, object Value)> parameters)
    {
        using var command = Command(connection, sql, parameters);
        command.ExecuteNonQuery();
    }

    private static SqliteCommand Command(SqliteConnection connection, string sql, params ReadOnlySpan<(string Name, object Value)> parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }

    private Task Enqueue(Action work) => Enqueue(() =>
    {
        work();
        return true;
    });

    private Task<T> Enqueue<T>(Func<T> work)
    {
        lock (gate)
        {
            var next = queue.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            queue = next;
            return next;
        }
    }
}
