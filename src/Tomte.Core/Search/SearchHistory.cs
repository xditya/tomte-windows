using Microsoft.Data.Sqlite;
using Tomte.Core.Storage;

namespace Tomte.Core.Search;

/// <summary>
/// What was opened from search, in tomte.db: per row its kind, name, target, how often and when last. It ranks rows
/// opened before a little higher and fills the empty box. Only the newest <see cref="MaxRows"/> are kept; nothing else
/// about a search (what was typed, what was shown) is stored. Every method opens the database: worker threads only.
/// </summary>
public sealed class SearchHistory(TomteDb db)
{
    public const int MaxRows = 200;

    private const string Feature = "search";

    // The whole history, oldest first. Never edit or reorder a step that has merged; add at the end.
    private static readonly string[] Schema =
    [
        "CREATE TABLE search_launches(key TEXT PRIMARY KEY, kind INTEGER NOT NULL, name TEXT NOT NULL, target TEXT NOT NULL, count INTEGER NOT NULL, last INTEGER NOT NULL) WITHOUT ROWID",
    ];

    /// <summary>Every row opened before, with its counts by <see cref="SearchItem.Key"/>.</summary>
    public (IReadOnlyList<SearchItem> Items, IReadOnlyDictionary<string, Launches> Launches) Load()
    {
        using var connection = db.Open(Feature, Schema);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT kind, name, target, count, last FROM search_launches";
        using var reader = command.ExecuteReader();
        var items = new List<SearchItem>();
        var launches = new Dictionary<string, Launches>();
        while (reader.Read())
        {
            if (!Enum.IsDefined((SearchKind)reader.GetInt32(0)))
            {
                continue;
            }

            var item = new SearchItem((SearchKind)reader.GetInt32(0), reader.GetString(1), reader.GetString(2));
            items.Add(item);
            launches[item.Key] = new(reader.GetInt32(3), reader.GetInt64(4));
        }

        return (items, launches);
    }

    /// <summary>The row was opened now: one more for it, and the oldest rows past <see cref="MaxRows"/> go.</summary>
    public void Record(SearchItem item, DateTimeOffset now)
    {
        using var connection = db.Open(Feature, Schema);
        using var transaction = connection.BeginTransaction();
        Run(connection, """
            INSERT INTO search_launches(key, kind, name, target, count, last) VALUES ($key, $kind, $name, $target, 1, $last)
            ON CONFLICT(key) DO UPDATE SET name = excluded.name, count = count + 1, last = excluded.last
            """, ("$key", item.Key), ("$kind", (int)item.Kind), ("$name", item.Name), ("$target", item.Target), ("$last", now.UtcTicks));
        Run(connection, "DELETE FROM search_launches WHERE key NOT IN (SELECT key FROM search_launches ORDER BY last DESC LIMIT $max)", ("$max", MaxRows));
        transaction.Commit();
    }

    private static void Run(SqliteConnection connection, string sql, params ReadOnlySpan<(string Name, object Value)> parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        command.ExecuteNonQuery();
    }
}
