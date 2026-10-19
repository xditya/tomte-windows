using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using Tomte.Core.Storage;

namespace Tomte.Core.Clipboard;

/// <summary>
/// How much history is kept: the newest pieces of text and, counted apart, the newest images; none older than the
/// age; no text longer than the size. An image is read only up to <see cref="MaxImageReadBytes"/> on the clipboard
/// and as a bitmap (a full 5K screen fits), and kept only up to <see cref="MaxImageBytes"/> as a PNG.
/// </summary>
public sealed record ClipboardRetention(int MaxItems = 50, int MaxDays = 7, int MaxChars = 65536, int MaxImages = 50, int MaxImageBytes = 20 << 20, int MaxImageReadBytes = 64 << 20);

/// <summary>
/// One history row as the panel shows it: never the whole item, only its start, on one line. An image row has its
/// size for the text and its stored thumbnail. A pinned row stays through Clear all, the age limit and the count
/// limits. Source is the app it was copied from, when the listener knew it; Chars the whole text's length.
/// </summary>
public sealed record ClipboardRow(long Id, string Display, DateTimeOffset CopiedAt, byte[]? Thumb = null)
{
    public const int DisplayChars = 120;

    /// <summary>Rows newer than this are "Just now".</summary>
    public static readonly TimeSpan JustNow = TimeSpan.FromMinutes(10);

    public bool Pinned { get; init; }

    public string? Source { get; init; }

    public int Chars { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public bool IsImage => Thumb is not null;

    /// <summary>"Image · 1920 × 1080".</summary>
    public static string ImageLabel(int width, int height) => $"Image · {width} × {height}";

    /// <summary>The colour a row of only "#RGB" or "#RRGGBB" names, opaque; null for any other row.</summary>
    public uint? Colour
    {
        get
        {
            var hex = Display.AsSpan();
            if (IsImage || hex is not ['#', .. var digits] || digits.Length is not (3 or 6) || !uint.TryParse(digits, System.Globalization.NumberStyles.AllowHexSpecifier, null, out uint rgb))
            {
                return null;
            }

            if (digits.Length == 3)
            {
                rgb = (rgb >> 8 & 0xF) * 0x110000 | (rgb >> 4 & 0xF) * 0x1100 | (rgb & 0xF) * 0x11;
            }

            return 0xFF000000 | rgb;
        }
    }

    /// <summary>
    /// The heading of the group the row is in, in capitals: "JUST NOW", "EARLIER TODAY", "YESTERDAY", then the weekday
    /// within a week and the date before that. Days are <paramref name="now"/>'s own, in its offset.
    /// </summary>
    public string Group(DateTimeOffset now)
    {
        if (now - CopiedAt < JustNow)
        {
            return "JUST NOW";
        }

        var day = CopiedAt.ToOffset(now.Offset).Date;
        int days = (now.Date - day).Days;
        return days <= 0 ? "EARLIER TODAY" : days == 1 ? "YESTERDAY"
            : (days < 7 ? day.ToString("dddd", System.Globalization.CultureInfo.InvariantCulture) : day.ToString("d MMM", System.Globalization.CultureInfo.InvariantCulture)).ToUpperInvariant();
    }

    /// <summary>The row's age at its right end: minutes while it is just now, the clock time it was copied after that.</summary>
    public string When(DateTimeOffset now) => now - CopiedAt < JustNow ? Ago(now) : CopiedAt.ToOffset(now.Offset).ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The preview's line about the item: "Text · 84 chars · from Outlook", "Link · 33 chars", "Image · 1600 × 900".</summary>
    public string Meta()
    {
        if (IsImage)
        {
            return Display;
        }

        string kind = Colour is not null ? "Colour" : IsLink ? "Link" : "Text";
        string meta = $"{kind} · {Chars} char{(Chars == 1 ? "" : "s")}";
        return AppName(Source) is { } app ? $"{meta} · from {app}" : meta;
    }

    /// <summary>An executable's name as an app's: "OUTLOOK.EXE" as "Outlook"; a name in mixed case stays as it is. Null for none.</summary>
    public static string? AppName(string? exe)
    {
        string name = Path.GetFileNameWithoutExtension(exe ?? "");
        if (name.Length == 0)
        {
            return null;
        }

        bool oneCase = name.Equals(name.ToUpperInvariant(), StringComparison.Ordinal) || name.Equals(name.ToLowerInvariant(), StringComparison.Ordinal);
        return oneCase ? char.ToUpperInvariant(name[0]) + name[1..].ToLowerInvariant() : name;
    }

    /// <summary>Whether the filter's words are in the row's text or its source app, ignoring case. An empty filter keeps every row.</summary>
    public bool Matches(string filter) => string.IsNullOrWhiteSpace(filter)
        || Display.Contains(filter.Trim(), StringComparison.CurrentCultureIgnoreCase)
        || (AppName(Source)?.Contains(filter.Trim(), StringComparison.CurrentCultureIgnoreCase) ?? false);

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
        // Images: empty text, a width, the PNG's SHA-256, and the PNG last, so a query of the other columns never walks
        // the PNG's pages.
        """
        ALTER TABLE clipboard_entries ADD COLUMN width INTEGER;
        ALTER TABLE clipboard_entries ADD COLUMN height INTEGER;
        ALTER TABLE clipboard_entries ADD COLUMN thumb BLOB;
        ALTER TABLE clipboard_entries ADD COLUMN sha BLOB;
        ALTER TABLE clipboard_entries ADD COLUMN png BLOB;
        """,
        // A pin, and the executable the item was copied from.
        """
        ALTER TABLE clipboard_entries ADD COLUMN pinned INTEGER NOT NULL DEFAULT 0;
        ALTER TABLE clipboard_entries ADD COLUMN source TEXT;
        """,
    ];

    public ClipboardRetention Retention { get; } = retention ?? new();

    /// <summary>
    /// Keeps the text as the newest item, with the executable it came from; the same text copied again moves up instead
    /// of showing twice, and keeps its pin.
    /// </summary>
    public void Add(string text, DateTimeOffset at, string? source = null)
    {
        if (string.IsNullOrWhiteSpace(text) || text.Length > Retention.MaxChars)
        {
            return;
        }

        using var connection = db.Open("clipboard", Schema);
        using var transaction = connection.BeginTransaction();
        using (var moved = Command(connection, "UPDATE clipboard_entries SET copied_at = $at, source = $source WHERE width IS NULL AND text = $text", ("$at", at.ToUnixTimeMilliseconds()), ("$source", Nullable(source)), ("$text", text)))
        {
            if (moved.ExecuteNonQuery() == 0)
            {
                Run(connection, "INSERT INTO clipboard_entries(text, copied_at, source) VALUES ($text, $at, $source)", ("$text", text), ("$at", at.ToUnixTimeMilliseconds()), ("$source", Nullable(source)));
            }
        }

        Prune(connection, at);
        transaction.Commit();
    }

    /// <summary>
    /// Keeps the image as the newest image; the same PNG copied again moves its row up instead, without writing the
    /// PNG twice. One over the size is not kept.
    /// </summary>
    public void AddImage(KeptImage image, DateTimeOffset at, string? source = null)
    {
        if (image.Png.Length > Retention.MaxImageBytes)
        {
            return;
        }

        byte[] sha = SHA256.HashData(image.Png);
        using var connection = db.Open("clipboard", Schema);
        using var transaction = connection.BeginTransaction();
        using (var moved = Command(connection, "UPDATE clipboard_entries SET copied_at = $at, source = $source WHERE sha = $sha", ("$at", at.ToUnixTimeMilliseconds()), ("$source", Nullable(source)), ("$sha", sha)))
        {
            if (moved.ExecuteNonQuery() == 0)
            {
                Run(connection, "INSERT INTO clipboard_entries(text, copied_at, width, height, thumb, sha, png, source) VALUES ('', $at, $width, $height, $thumb, $sha, $png, $source)",
                    ("$at", at.ToUnixTimeMilliseconds()), ("$width", image.Width), ("$height", image.Height), ("$thumb", image.Thumb), ("$sha", sha), ("$png", image.Png), ("$source", Nullable(source)));
            }
        }

        Prune(connection, at);
        transaction.Commit();
    }

    /// <summary>Newest first, shortened for display. Only the first few hundred characters of each item are read, and an image's thumbnail.</summary>
    public IReadOnlyList<ClipboardRow> Rows(DateTimeOffset now)
    {
        using var connection = db.Open("clipboard", Schema);
        Prune(connection, now);
        using var command = Command(connection, "SELECT id, substr(text, 1, 400), copied_at, width, height, thumb, pinned, source, length(text) FROM clipboard_entries ORDER BY copied_at DESC, id DESC");
        using var reader = command.ExecuteReader();
        var rows = new List<ClipboardRow>();
        while (reader.Read())
        {
            var at = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2));
            var row = reader.IsDBNull(3)
                ? new ClipboardRow(reader.GetInt64(0), ClipboardRow.Shorten(reader.GetString(1)), at)
                : new ClipboardRow(reader.GetInt64(0), ClipboardRow.ImageLabel(reader.GetInt32(3), reader.GetInt32(4)), at, (byte[])reader.GetValue(5)) { Width = reader.GetInt32(3), Height = reader.GetInt32(4) };
            rows.Add(row with { Pinned = reader.GetInt64(6) != 0, Source = reader.IsDBNull(7) ? null : reader.GetString(7), Chars = reader.GetInt32(8) });
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

    /// <summary>Pins the item, so it stays through Clear all and every limit, or takes its pin off.</summary>
    public void Pin(long id, bool pinned)
    {
        using var connection = db.Open("clipboard", Schema);
        Run(connection, "UPDATE clipboard_entries SET pinned = $pinned WHERE id = $id", ("$pinned", pinned ? 1 : 0), ("$id", id));
    }

    /// <summary>Deletes every item, or every item but the pinned ones.</summary>
    public void Clear(bool keepPinned = false)
    {
        using var connection = db.Open("clipboard", Schema);
        Run(connection, keepPinned ? "DELETE FROM clipboard_entries WHERE pinned = 0" : "DELETE FROM clipboard_entries");
    }

    // Pinned items are never pruned and do not count toward the limits.
    private void Prune(SqliteConnection connection, DateTimeOffset now)
    {
        Run(connection, "DELETE FROM clipboard_entries WHERE pinned = 0 AND copied_at < $cutoff", ("$cutoff", now.AddDays(-Retention.MaxDays).ToUnixTimeMilliseconds()));
        // Text and images are counted apart; a text row has no width.
        Run(connection, "DELETE FROM clipboard_entries WHERE pinned = 0 AND width IS NULL AND id NOT IN (SELECT id FROM clipboard_entries WHERE pinned = 0 AND width IS NULL ORDER BY copied_at DESC, id DESC LIMIT $max)", ("$max", Retention.MaxItems));
        Run(connection, "DELETE FROM clipboard_entries WHERE pinned = 0 AND width IS NOT NULL AND id NOT IN (SELECT id FROM clipboard_entries WHERE pinned = 0 AND width IS NOT NULL ORDER BY copied_at DESC, id DESC LIMIT $max)", ("$max", Retention.MaxImages));
    }

    private static object Nullable(string? value) => (object?)value ?? DBNull.Value;

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
    private (CopiedImage Image, DateTimeOffset At, string? Source)? pendingImage;

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
                pendingImage?.Image.Dispose();
                pendingImage = null;
                _ = Enqueue(() => Logged("clear the history", () => history.Clear()));
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

    /// <summary>Clear all: every item but the pinned ones.</summary>
    public Task ClearAsync() => Enqueue(() =>
    {
        history.Clear(keepPinned: true);
        Changed?.Invoke();
    });

    public Task PinAsync(long id, bool pinned) => Enqueue(() =>
    {
        history.Pin(id, pinned);
        Changed?.Invoke();
    });

    /// <summary>The whole text of an item, for the preview; null when it is gone.</summary>
    public Task<string?> TextAsync(long id) => Enqueue(() => history.Text(id));

    /// <summary>An image item's PNG, for the preview or the shelf; null when it is gone or is text.</summary>
    public Task<byte[]?> ImageAsync(long id) => Enqueue(() => history.Image(id));

    // On the listener's thread, after the read. Only the listener started last may add, and only while it is on.
    private void Captured(int from, ClipboardRead read, DateTimeOffset at)
    {
        lock (gate)
        {
            if (from != generation || listener is null)
            {
                read.Image?.Dispose();
                return;
            }

            if (read.Image is { } image)
            {
                // One slot: copies that land while the worker is busy replace each other, so only the latest is
                // decoded and at most two copies are held, the one being decoded and the one waiting.
                bool waiting = pendingImage is not null;
                pendingImage?.Image.Dispose();
                pendingImage = (image, at, read.Source);
                if (!waiting)
                {
                    _ = Enqueue(() => Logged("store an image", StoreImage));
                    // A step of its own, so the PNG is no longer held by the step that stored it.
                    _ = Enqueue(GiveBackImageMemory);
                }
            }
            else
            {
                _ = Enqueue(() => Logged("store an item", () => history.Add(read.Text!, at, read.Source)));
            }
        }
    }

    /// <summary>
    /// After image work: the copy and the codec's buffers are native and already freed, but the PNG's managed bytes
    /// (up to 20 MB for a PNG copied as one) would stay committed for as long as the hidden shell allocates nothing
    /// more. A full, compacting collection returns them to Windows; every thread pauses for about a millisecond.
    /// </summary>
    private static void GiveBackImageMemory() => GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);

    // The decode and the encode run here, on the worker, never on the listener's thread. Takes the waiting copy.
    private void StoreImage()
    {
        CopiedImage image;
        DateTimeOffset at;
        string? source;
        lock (gate)
        {
            if (pendingImage is not { } pending)
            {
                return;
            }

            ((image, at, source), pendingImage) = (pending, null);
        }

        KeptImage? kept;
        try
        {
            kept = ClipboardImages.Keep(image, history.Retention.MaxImageReadBytes);
        }
        catch (Exception e)
        {
            log?.Invoke($"clipboard: an image could not be read ({e.GetType().Name})");
            return;
        }
        finally
        {
            // The copy goes as soon as the PNG is made, before the database is written.
            image.Dispose();
        }

        if (kept is null)
        {
            log?.Invoke($"clipboard: skipped: an image over the {history.Retention.MaxImageReadBytes >> 20} MB read as a bitmap");
            return;
        }

        if (kept.Png.Length > history.Retention.MaxImageBytes)
        {
            log?.Invoke($"clipboard: skipped: an image of {kept.Png.Length} bytes as a PNG, over the {history.Retention.MaxImageBytes} kept");
            return;
        }

        history.AddImage(kept, at, source);
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
