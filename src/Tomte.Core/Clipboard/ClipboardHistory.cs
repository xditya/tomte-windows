using Microsoft.Data.Sqlite;
using Tomte.Core.Storage;

namespace Tomte.Core.Clipboard;

/// <summary>
/// How much history is kept: the newest pieces of text and, counted apart, the newest images; none older than the
/// age; no text longer than the size, and no image over its size on the clipboard or as a bitmap.
/// </summary>
public sealed record ClipboardRetention(int MaxItems = 50, int MaxDays = 7, int MaxChars = 65536, int MaxImages = 50, int MaxImageBytes = 20 << 20);

/// <summary>
/// One history row as the panel shows it: never the whole item, only its start, on one line. An image row has its
/// size for the text and its stored thumbnail.
/// </summary>
public sealed record ClipboardRow(long Id, string Display, DateTimeOffset CopiedAt, byte[]? Thumb = null)
{
    public const int DisplayChars = 120;

    public bool IsImage => Thumb is not null;

    /// <summary>"Image · 1920 x 1080".</summary>
    public static string ImageLabel(int width, int height) => $"Image · {width} x {height}";

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
    private static readonly string[] Schema =
    [
        "CREATE TABLE clipboard_entries(id INTEGER PRIMARY KEY, text TEXT NOT NULL, copied_at INTEGER NOT NULL)",
        // Images: empty text, a width, and the PNG last, so a query of the other columns never walks the PNG's pages.
        """
        ALTER TABLE clipboard_entries ADD COLUMN width INTEGER;
        ALTER TABLE clipboard_entries ADD COLUMN height INTEGER;
        ALTER TABLE clipboard_entries ADD COLUMN thumb BLOB;
        ALTER TABLE clipboard_entries ADD COLUMN png BLOB;
        """,
    ];

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

    /// <summary>Keeps the image as the newest image. One over the size is not kept.</summary>
    public void AddImage(KeptImage image, DateTimeOffset at)
    {
        if (image.Png.Length > Retention.MaxImageBytes)
        {
            return;
        }

        using var connection = db.Open("clipboard", Schema);
        using var transaction = connection.BeginTransaction();
        Run(connection, "INSERT INTO clipboard_entries(text, copied_at, width, height, thumb, png) VALUES ('', $at, $width, $height, $thumb, $png)",
            ("$at", at.ToUnixTimeMilliseconds()), ("$width", image.Width), ("$height", image.Height), ("$thumb", image.Thumb), ("$png", image.Png));
        Prune(connection, at);
        transaction.Commit();
    }

    /// <summary>Newest first, shortened for display. Only the first few hundred characters of each item are read, and an image's thumbnail.</summary>
    public IReadOnlyList<ClipboardRow> Rows(DateTimeOffset now)
    {
        using var connection = db.Open("clipboard", Schema);
        Prune(connection, now);
        using var command = Command(connection, "SELECT id, substr(text, 1, 400), copied_at, width, height, thumb FROM clipboard_entries ORDER BY copied_at DESC, id DESC");
        using var reader = command.ExecuteReader();
        var rows = new List<ClipboardRow>();
        while (reader.Read())
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
            rows.Add(reader.IsDBNull(3)
                ? new ClipboardRow(reader.GetInt64(0), ClipboardRow.Shorten(reader.GetString(1)), at)
                : new ClipboardRow(reader.GetInt64(0), ClipboardRow.ImageLabel(reader.GetInt32(3), reader.GetInt32(4)), at, (byte[])reader.GetValue(5)));
        }

        return rows;
    }

    /// <summary>The whole item, to put back on the clipboard; null when it is gone. An image's text is empty.</summary>
    public string? Text(long id)
    {
        using var connection = db.Open("clipboard", Schema);
        using var command = Command(connection, "SELECT text FROM clipboard_entries WHERE id = $id", ("$id", id));
        return command.ExecuteScalar() as string;
    }

    /// <summary>An image item's PNG; null when it is gone or is text.</summary>
    public byte[]? Image(long id)
    {
        using var connection = db.Open("clipboard", Schema);
        using var command = Command(connection, "SELECT png FROM clipboard_entries WHERE id = $id", ("$id", id));
        return command.ExecuteScalar() as byte[];
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
        // Text and images are counted apart; a text row has no width.
        Run(connection, "DELETE FROM clipboard_entries WHERE width IS NULL AND id NOT IN (SELECT id FROM clipboard_entries WHERE width IS NULL ORDER BY copied_at DESC, id DESC LIMIT $max)", ("$max", Retention.MaxItems));
        Run(connection, "DELETE FROM clipboard_entries WHERE width IS NOT NULL AND id NOT IN (SELECT id FROM clipboard_entries WHERE width IS NOT NULL ORDER BY copied_at DESC, id DESC LIMIT $max)", ("$max", Retention.MaxImages));
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

    /// <summary>Puts an image on the clipboard as Tomte's own write: the PNG and the same image as a packed bitmap.</summary>
    void Put(byte[] png, byte[] dib);
}

/// <summary>
/// The history switch and the one worker queue every history read and write goes through, in order.
/// Off means no listener at all: no window, no thread, nothing opened. Turning it off stops capture at
/// once (an update the listener is still reading is dropped) and clears the history.
/// </summary>
public sealed class ClipboardWatch(ClipboardHistory history, Func<Action<ClipboardRead, DateTimeOffset>, IClipboardListener> start, Action<string>? log = null) : IDisposable
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
                listener = start((read, at) => Captured(current, read, at));
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

    /// <summary>
    /// Newest first. Off, there are none, and anything still stored is cleared: history turned off while
    /// Tomte was not running, or a clear that failed, does not stay behind in the file.
    /// </summary>
    public Task<IReadOnlyList<ClipboardRow>> RowsAsync(DateTimeOffset now) => Enqueue<IReadOnlyList<ClipboardRow>>(() =>
    {
        if (On)
        {
            return history.Rows(now);
        }

        history.Clear();
        return [];
    });

    /// <summary>
    /// Puts the item back on the clipboard. It does not paste, and it does not come back as a new item. True when the
    /// item was there and went to the listener; the write itself happens on the listener's thread, which only logs a
    /// clipboard that stayed busy.
    /// </summary>
    public Task<bool> PickAsync(long id) => Enqueue(() =>
    {
        if (history.Image(id) is { } png)
        {
            var dib = ClipboardImages.Dib(png);
            lock (gate)
            {
                if (listener is null)
                {
                    return false;
                }

                listener.Put(png, dib);
            }

            log?.Invoke($"clipboard: picked an image of {png.Length} bytes");
            return true;
        }

        if (history.Text(id) is not { } text)
        {
            return false;
        }

        lock (gate)
        {
            if (listener is null)
            {
                return false;
            }

            listener.Put(text);
        }

        log?.Invoke($"clipboard: picked an item of {text.Length} characters");
        return true;
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
    private void Captured(int from, ClipboardRead read, DateTimeOffset at)
    {
        lock (gate)
        {
            if (from != generation || listener is null)
            {
                return;
            }

            if (read.Image is { } image)
            {
                _ = Enqueue(() => Logged("store an image", () => StoreImage(image, at)));
                // A step of its own, so the copy is no longer held by the step that stored it.
                _ = Enqueue(GiveBackImageMemory);
            }
            else
            {
                _ = Enqueue(() => Logged("store an item", () => history.Add(read.Text!, at)));
            }
        }
    }

    /// <summary>
    /// After image work, which holds the image several times over in managed and native memory: the imaging objects
    /// let go of theirs only when their finalizers run, so one collection runs those, then a full, compacting one
    /// returns what is free to Windows. Without it the memory stays committed, tens of megabytes, for as long as the
    /// hidden shell allocates nothing more. Every thread pauses for a few milliseconds.
    /// </summary>
    public static void GiveBackImageMemory()
    {
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    }

    // The decode and the encode run here, on the worker, never on the listener's thread.
    private void StoreImage(CopiedImage image, DateTimeOffset at)
    {
        KeptImage? kept;
        try
        {
            kept = ClipboardImages.Keep(image, history.Retention.MaxImageBytes);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log?.Invoke($"clipboard: an image could not be read ({e.GetType().Name})");
            return;
        }

        if (kept is null)
        {
            log?.Invoke($"clipboard: skipped: an image over the {history.Retention.MaxImageBytes >> 20} MB kept as a bitmap");
            return;
        }

        history.AddImage(kept, at);
    }

    // For work nobody awaits. The exception's type only: its message is never checked for what it quotes.
    private void Logged(string what, Action work)
    {
        try
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            work();
            log?.Invoke($"clipboard: {what} took {System.Diagnostics.Stopwatch.GetElapsedTime(start).TotalMilliseconds:F1} ms");
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
