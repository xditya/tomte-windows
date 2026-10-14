using Microsoft.Data.Sqlite;
using Tomte.Core.Storage;

namespace Tomte.Core.Clipboard;

/// <summary>How much history is kept: the newest items, none older than the age, none longer than the size.</summary>
public sealed record ClipboardRetention(int MaxItems = 50, int MaxDays = 7, int MaxChars = 65536);

/// <summary>One history row as the panel shows it: never the whole item, only its start, on one line.</summary>
public sealed record ClipboardRow(long Id, string Display, DateTimeOffset CopiedAt)
{
    public const int DisplayChars = 120;

    /// <summary>A web address gets the link icon.</summary>
    public bool IsLink => Display.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || Display.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || Display.StartsWith("www.", StringComparison.OrdinalIgnoreCase);

    /// <summary>Line breaks and runs of spaces become one space; longer than <see cref="DisplayChars"/> ends in an ellipsis.</summary>
    public static string Shorten(string start)
    {
        var line = string.Join(' ', start.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= DisplayChars ? line : line[..(DisplayChars - 1)].TrimEnd() + "…";
    }

    /// <summary>"now", "4 min", "2 h", "3 d".</summary>
    public string Ago(DateTimeOffset now)
    {
        var age = now - CopiedAt;
        return age.TotalMinutes < 1 ? "now" : age.TotalHours < 1 ? $"{(int)age.TotalMinutes} min" : age.TotalDays < 1 ? $"{(int)age.TotalHours} h" : $"{(int)age.TotalDays} d";
    }
}

/// <summary>
/// The clipboard history table in tomte.db. Every method opens the database, so call them only on a
/// worker thread; <see cref="ClipboardWatch"/> runs them in order on one. Old and surplus items are
/// pruned on every add and every read: nothing runs on a timer to do it. A deleted item is
/// overwritten in the file (the database deletes securely) once the last connection closes.
/// </summary>
public sealed class ClipboardHistory(TomteDb db, ClipboardRetention? retention = null)
{
    // The whole history, oldest first. Never edit or reorder a step that has merged; add at the end.
    private static readonly string[] Schema = ["CREATE TABLE clipboard_entries(id INTEGER PRIMARY KEY, text TEXT NOT NULL, copied_at INTEGER NOT NULL)"];

    public ClipboardRetention Retention { get; } = retention ?? new();

    /// <summary>Keeps the text as the newest item; the same text copied again moves up instead of showing twice.</summary>
    public void Add(string text, DateTimeOffset at)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > Retention.MaxChars)
        {
            return;
        }

        using var connection = db.Open("clipboard", Schema);
        using var transaction = connection.BeginTransaction();
        Run(connection, "DELETE FROM clipboard_entries WHERE text = $text", ("$text", text));
        Run(connection, "INSERT INTO clipboard_entries(text, copied_at) VALUES ($text, $at)", ("$text", text), ("$at", at.ToUnixTimeMilliseconds()));
        Prune(connection, at);
        transaction.Commit();
    }

    /// <summary>Newest first, shortened for display. Only the first few hundred characters of each item are read.</summary>
    public IReadOnlyList<ClipboardRow> Rows(DateTimeOffset now)
    {
        using var connection = db.Open("clipboard", Schema);
        Prune(connection, now);
        using var command = Command(connection, "SELECT id, substr(text, 1, 400), copied_at FROM clipboard_entries ORDER BY copied_at DESC, id DESC");
        using var reader = command.ExecuteReader();
        var rows = new List<ClipboardRow>();
        while (reader.Read())
        {
            rows.Add(new ClipboardRow(reader.GetInt64(0), ClipboardRow.Shorten(reader.GetString(1)), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2))));
        }

        return rows;
    }

    /// <summary>The whole item, to put back on the clipboard; null when it is gone.</summary>
    public string? Text(long id)
    {
        using var connection = db.Open("clipboard", Schema);
        using var command = Command(connection, "SELECT text FROM clipboard_entries WHERE id = $id", ("$id", id));
        return command.ExecuteScalar() as string;
    }

    public void Remove(long id)
    {
        using var connection = db.Open("clipboard", Schema);
        Run(connection, "DELETE FROM clipboard_entries WHERE id = $id", ("$id", id));
    }

    public void Clear()
    {
        using var connection = db.Open("clipboard", Schema);
        Run(connection, "DELETE FROM clipboard_entries");
    }

    private void Prune(SqliteConnection connection, DateTimeOffset now)
    {
        Run(connection, "DELETE FROM clipboard_entries WHERE copied_at < $cutoff", ("$cutoff", now.AddDays(-Retention.MaxDays).ToUnixTimeMilliseconds()));
        Run(connection, "DELETE FROM clipboard_entries WHERE id NOT IN (SELECT id FROM clipboard_entries ORDER BY copied_at DESC, id DESC LIMIT $max)", ("$max", Retention.MaxItems));
    }

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
}

/// <summary>What the watch needs from the Win32 listener: stop listening, and put a picked item back on the clipboard.</summary>
public interface IClipboardListener : IDisposable
{
    /// <summary>Puts text on the clipboard as Tomte's own write, which history does not keep again.</summary>
    void Put(string text);
}

/// <summary>
/// The history switch and the one worker queue every history read and write goes through, in order.
/// Off means no listener at all: no window, no thread, nothing opened. Turning it off stops capture at
/// once (an update the listener is still reading is dropped) and clears the history.
/// </summary>
public sealed class ClipboardWatch(ClipboardHistory history, Func<Action<string, DateTimeOffset>, IClipboardListener> start, Action<string>? log = null) : IDisposable
{
    private readonly Lock gate = new();
    private IClipboardListener? listener;
    private int generation;
    private Task queue = Task.CompletedTask;

    /// <summary>The history or the switch changed. Raised on a worker thread or the caller's.</summary>
    public event Action? Changed;

    public bool On
    {
        get
        {
            lock (gate)
            {
                return listener is not null;
            }
        }
    }

    public ClipboardRetention Retention => history.Retention;

    /// <summary>Starts or stops the listener to match the setting. Nothing happens when it already matches.</summary>
    public void Apply(bool on)
    {
        IClipboardListener? stopping;
        lock (gate)
        {
            if (on == (listener is not null))
            {
                return;
            }

            generation++;
            stopping = listener;
            listener = null;
            if (on)
            {
                int current = generation;
                listener = start((text, at) => Captured(current, text, at));
            }
            else
            {
                _ = Enqueue(() => Logged("clear the history", history.Clear));
            }
        }

        stopping?.Dispose();
        log?.Invoke(on ? "clipboard: history on, listening" : "clipboard: history off, listener stopped, history cleared");
        Changed?.Invoke();
    }

    /// <summary>Tomte is quitting: stop listening and give what was captured a moment to be stored. The history stays.</summary>
    public void Dispose()
    {
        IClipboardListener? stopping;
        Task pending;
        lock (gate)
        {
            generation++;
            (stopping, listener, pending) = (listener, null, queue);
        }

        stopping?.Dispose();
        // Up to the database's own lock wait; a failed item has been logged already.
        try
        {
            pending.Wait(TimeSpan.FromSeconds(6));
        }
        catch (AggregateException)
        {
        }
    }

    public Task<IReadOnlyList<ClipboardRow>> RowsAsync(DateTimeOffset now) => Enqueue(() => history.Rows(now));

    /// <summary>Puts the item back on the clipboard. It does not paste, and it does not come back as a new item.</summary>
    public Task PickAsync(long id) => Enqueue(() =>
    {
        if (history.Text(id) is not { } text)
        {
            return;
        }

        lock (gate)
        {
            listener?.Put(text);
        }

        log?.Invoke($"clipboard: picked an item of {text.Length} characters");
    });

    public Task RemoveAsync(long id) => Enqueue(() =>
    {
        history.Remove(id);
        Changed?.Invoke();
    });

    public Task ClearAsync() => Enqueue(() =>
    {
        history.Clear();
        Changed?.Invoke();
    });

    // On the listener's thread, after the read. Only the listener started last may add, and only while it is on.
    private void Captured(int from, string text, DateTimeOffset at)
    {
        lock (gate)
        {
            if (from != generation || listener is null)
            {
                return;
            }

            _ = Enqueue(() => Logged("store an item", () => history.Add(text, at)));
        }
    }

    // For work nobody awaits. The exception's type only: its message is never checked for what it quotes.
    private void Logged(string what, Action work)
    {
        try
        {
            work();
            Changed?.Invoke();
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"clipboard: could not {what} ({e.GetType().Name})");
        }
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
