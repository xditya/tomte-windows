using Microsoft.Data.Sqlite;
using Tomte.Core.Rambler;
using Tomte.Core.Storage;

namespace Tomte.Core;

/// <summary>
/// The one quick-capture note, kept in tomte.db. The rambler, the palette's tools and chip and the
/// Scratchpad panel share one instance, so in this process there is one copy of the text and no
/// write can overwrite another. The text lives in memory once loaded; loading and saving run on a
/// worker thread, so reading or editing the note never waits for the disk. Members may be called
/// from any thread; <see cref="Changed"/> is raised on whichever thread made the change.
/// An editor binds to <see cref="Text"/>, <see cref="Caret"/>, <see cref="Edit"/> and <see cref="Changed"/>.
/// </summary>
public sealed class ScratchpadNote : IScratchpad
{
    /// <summary>Typing is saved this long after the last change.</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    // The whole history, oldest first. Never edit or reorder a step that has merged; add at the end.
    private static readonly string[] Schema = ["CREATE TABLE scratchpad_note(id INTEGER PRIMARY KEY CHECK (id = 1), text TEXT NOT NULL)"];

    private readonly TomteDb db;
    private readonly string? carryOver;
    private readonly TimeProvider time;
    private readonly Lock gate = new();
    private readonly SemaphoreSlim writing = new(1, 1);
    private string text = "";
    private string saved = "";
    private int caret;
    private bool loaded, dirty;
    private ITimer? timer;

    /// <param name="carryOver">The file an earlier build kept the note in. Its text becomes the note the first time this database has none, then it is renamed to .imported.</param>
    public ScratchpadNote(TomteDb db, string? carryOver = null, TimeProvider? time = null)
    {
        this.db = db;
        this.carryOver = carryOver;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Something other than the editor changed the text or moved the caret: a rambler note, an AI write or undo, or another process.</summary>
    public event Action? Changed;

    public bool IsLoaded
    {
        get
        {
            lock (gate)
            {
                return loaded;
            }
        }
    }

    /// <summary>The whole note. Setting it replaces the note (the AI tool and its undo) and saves after <see cref="SaveDelay"/>.</summary>
    /// <exception cref="InvalidOperationException">Before the first <see cref="LoadAsync"/>: an empty note read then would overwrite the saved one.</exception>
    public string Text
    {
        get
        {
            lock (gate)
            {
                ThrowIfNotLoaded();
                return text;
            }
        }

        set
        {
            lock (gate)
            {
                ThrowIfNotLoaded();
                if (value == text)
                {
                    return;
                }

                text = value;
                caret = Math.Min(caret, text.Length);
                MarkDirty();
            }

            Changed?.Invoke();
        }
    }

    /// <summary>Where the editor's caret belongs, as an index into <see cref="Text"/>: the end of the note at first, then wherever the editor left it.</summary>
    public int Caret
    {
        get
        {
            lock (gate)
            {
                return caret;
            }
        }
    }

    /// <summary>The editor's whole text and caret after the user typed. Raises nothing; saves after <see cref="SaveDelay"/>.</summary>
    public void Edit(string newText, int newCaret)
    {
        lock (gate)
        {
            ThrowIfNotLoaded();
            caret = Math.Clamp(newCaret, 0, newText.Length);
            if (newText != text)
            {
                text = newText;
                MarkDirty();
            }
        }
    }

    /// <summary>
    /// Reads the saved note. The first call loads it (and carries an earlier build's file over); later
    /// calls pick up a change another process saved, unless this one holds edits not yet saved, in
    /// which case the save merges the two.
    /// </summary>
    /// <exception cref="SqliteException">The database stayed locked, or could not be read.</exception>
    /// <exception cref="IOException">The earlier build's file could not be read; nothing is written then, so the next load tries again.</exception>
    public async Task LoadAsync()
    {
        // In turn with saves: a read that started before a save finished would put the older text back.
        await writing.WaitAsync().ConfigureAwait(false);
        bool changed;
        try
        {
            string stored = await Task.Run(Read).ConfigureAwait(false);
            lock (gate)
            {
                changed = !dirty && (!loaded || text != stored);
                if (!dirty)
                {
                    caret = loaded ? Math.Min(caret, stored.Length) : stored.Length;
                    text = saved = stored;
                }

                loaded = true;
            }
        }
        finally
        {
            writing.Release();
        }

        if (changed)
        {
            Changed?.Invoke();
        }
    }

    /// <summary>A rambler note at the end of the note under a time heading, saved at once. The editor's caret stays where it is.</summary>
    public async Task AppendAsync(string note, DateTime at)
    {
        await LoadAsync().ConfigureAwait(false);
        lock (gate)
        {
            text = RamblerNotes.Append(text, note, at);
            MarkDirty();
        }

        Changed?.Invoke();
        await FlushAsync().ConfigureAwait(false);
    }

    /// <summary>Saves now if anything is unsaved: when the island hides and when Tomte quits.</summary>
    public async Task FlushAsync()
    {
        await writing.WaitAsync().ConfigureAwait(false);
        try
        {
            string local, basis;
            lock (gate)
            {
                timer?.Dispose();
                timer = null;
                if (!dirty)
                {
                    return;
                }

                (local, basis, dirty) = (text, saved, false);
            }

            (string stored, bool merged) result;
            try
            {
                result = await Task.Run(() => Write(local, basis)).ConfigureAwait(false);
            }
            catch
            {
                lock (gate)
                {
                    dirty = true;
                }

                throw;
            }

            lock (gate)
            {
                saved = result.stored;
                if (result.merged)
                {
                    // What the other process added follows the local text, and also follows anything typed during the save.
                    text += result.stored[local.Length..];
                    dirty |= text != saved;
                }
            }

            if (result.merged)
            {
                Changed?.Invoke();
            }
        }
        finally
        {
            writing.Release();
        }
    }

    private void ThrowIfNotLoaded()
    {
        if (!loaded)
        {
            throw new InvalidOperationException("The scratchpad is not loaded yet.");
        }
    }

    // Called under the lock. One timer while a save is pending, none otherwise.
    private void MarkDirty()
    {
        dirty = true;
        if (timer is null)
        {
            timer = time.CreateTimer(_ => _ = SaveFromTimer(), null, SaveDelay, Timeout.InfiniteTimeSpan);
        }
        else
        {
            timer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
        }
    }

    private async Task SaveFromTimer()
    {
        try
        {
            await FlushAsync().ConfigureAwait(false);
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            // Still marked unsaved: the next change, hide or quit tries again.
        }
    }

    private string Read()
    {
        using var connection = db.Open("scratchpad", Schema);
        if (Stored(connection) is { } stored)
        {
            return stored;
        }

        // A database without the note: the first run of this build, or one set aside as damaged.
        using var transaction = connection.BeginTransaction();
        if (Stored(connection) is { } raced)
        {
            return raced;
        }

        string carried = carryOver is not null && File.Exists(carryOver) ? File.ReadAllText(carryOver) : "";
        Save(connection, carried);
        transaction.Commit();
        if (carried.Length > 0)
        {
            try
            {
                // Never over an earlier .imported: that one is the only copy of what an earlier import took.
                File.Move(carryOver!, carryOver + ".imported", overwrite: false);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The note is in the database now and is not imported again from here; the old file stays where it was.
            }
        }

        return carried;
    }

    private (string Stored, bool Merged) Write(string local, string basis)
    {
        using var connection = db.Open("scratchpad", Schema);
        using var transaction = connection.BeginTransaction();
        string? current = Stored(connection);
        bool merged = current is not null && current != basis;
        string stored = merged ? Merge(local, basis, current!) : local;
        Save(connection, stored);
        transaction.Commit();
        return (stored, merged);
    }

    // Another process saved the note after this one last read it. What it added at the end goes after the local text;
    // anything else it did is kept whole below it. Nothing is lost either way, and the result starts with the local text.
    private static string Merge(string local, string basis, string current)
    {
        string added = basis.Length > 0 && current.StartsWith(basis, StringComparison.Ordinal) ? current[basis.Length..] : current;
        return local.Length == 0 || added.StartsWith('\n') ? local + added : local + "\n\n" + added;
    }

    private static string? Stored(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT text FROM scratchpad_note WHERE id = 1";
        return command.ExecuteScalar() as string;
    }

    private static void Save(SqliteConnection connection, string note)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO scratchpad_note(id, text) VALUES (1, $text) ON CONFLICT(id) DO UPDATE SET text = excluded.text";
        command.Parameters.AddWithValue("$text", note);
        command.ExecuteNonQuery();
    }
}
