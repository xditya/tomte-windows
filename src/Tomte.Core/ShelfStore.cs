using Microsoft.Data.Sqlite;
using Tomte.Core.Storage;
using Windows.Win32;

namespace Tomte.Core;

public enum ShelfKind
{
    /// <summary>The user's own file or folder, where it is. Tomte never moves or deletes it.</summary>
    Reference,

    /// <summary>A file or folder copied into the shelf folder: it came from a temporary folder or a removable drive, or the user asked for a copy.</summary>
    Copy,

    /// <summary>Text, a link or an image that arrived without a file of its own; the shelf keeps it as a file in its folder.</summary>
    Content,
}

/// <summary>The item actions, in the order the view shows them.</summary>
public enum ShelfAction
{
    Open,
    Reveal,
    CopyPath,
    Share,
    Remove,
}

/// <summary>Where a dropped path goes: stays where it is, or is copied in now (inside the drop) or on the worker.</summary>
public enum DropDecision
{
    Reference,
    CopyNow,
    CopyLater,
}

/// <summary>
/// One item. Name is unique on the shelf and is what the view labels and the AI tools address; for a reference it can
/// differ from the file's own name when two files share one. Missing, Size and IsFolder are what the last check of
/// the disk found and are not stored.
/// </summary>
public sealed record ShelfItem(long Id, string Name, string Path, ShelfKind Kind, DateTimeOffset Added)
{
    public bool Missing { get; init; }

    /// <summary>Bytes; null for a folder or a missing item.</summary>
    public long? Size { get; init; }

    public bool IsFolder { get; init; }
}

/// <summary>Something to put on the shelf: a path for a reference or a copy, bytes for content.</summary>
public sealed record ShelfEntry(ShelfKind Kind, string Name, string? Path = null, byte[]? Content = null);

/// <summary>What one drop carried, read inside the drop while the source still holds it. KeepCopy is Ctrl at the drop.</summary>
public sealed record DroppedItems(IReadOnlyList<string> Paths, IReadOnlyList<(string Name, byte[] Bytes)> Files, string? Text, bool KeepCopy);

/// <summary>
/// The shelf: items in tomte.db (feature "shelf"), copies and content as files under <see cref="Folder"/>, one folder
/// per item so names never collide on disk. Every database and disk operation runs in order on one worker task chain;
/// the island's thread only reads <see cref="Items"/>, which the worker replaces through <c>post</c>. Nothing is
/// opened until the first call that needs the shelf.
/// Files of a removed copy are not deleted when it is removed: a browser or an editor given its path by a drag may read
/// it minutes later. The first load in the next process deletes every folder no item holds.
/// </summary>
public sealed class ShelfStore : IShelf
{
    public const string FallbackName = "Item";
    private const string Feature = "shelf";

    // ReadText gives up above this size; text extraction for large and binary files is the AI files task's.
    private const int MaxTextBytes = 1 << 20;

    // The whole history, oldest first. Never edit or reorder a step that has merged; add at the end.
    private static readonly string[] Schema =
    [
        "CREATE TABLE shelf_items(id INTEGER PRIMARY KEY, name TEXT NOT NULL, path TEXT NOT NULL, kind INTEGER NOT NULL, added INTEGER NOT NULL)",
    ];

    private static readonly Lazy<string[]> TempFolders = new(() =>
        [.. new[]
        {
            Path.GetTempPath(),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Temp"),
            Environment.GetFolderPath(Environment.SpecialFolder.InternetCache),
        }.Where(p => p.Length > 0).Select(LongPath).Distinct(StringComparer.OrdinalIgnoreCase)]);

    private readonly TomteDb db;
    private readonly Action<Action> post;
    private readonly Action<string>? log;
    private readonly Lock gate = new();
    private readonly HashSet<string> slotsMadeHere = new(StringComparer.OrdinalIgnoreCase);
    private Task chain = Task.CompletedTask;

    // Worker only.
    private List<ShelfItem> rows = [];
    private bool loaded;

    /// <param name="post">Runs an action on the island's thread; tests pass one that runs it at once.</param>
    public ShelfStore(TomteDb db, string folder, Action<Action> post, Action<string>? log = null)
    {
        this.db = db;
        this.post = post;
        this.log = log;
        Folder = Path.GetFullPath(folder);
    }

    public static string DefaultFolder { get; } = Path.Combine(Path.GetDirectoryName(TomteDb.DefaultPath)!, "Shelf");

    public string Folder { get; }

    /// <summary>Folders whose files are copied in during the drop, because their owner may delete them right after it.</summary>
    public IReadOnlyList<string> TempRoots { get; init; } = [];

    /// <summary>The drive type of a path; injected by tests. Removable and optical drives are copied in.</summary>
    public Func<string, DriveType> DriveTypeOf { get; init; } = DriveTypeOfPath;

    /// <summary>Newest first, as last published; read on the island's thread.</summary>
    public IReadOnlyList<ShelfItem> Items { get; private set; } = [];

    public IReadOnlyList<string> ItemNames { get; private set; } = [];

    /// <summary>Raised through <c>post</c> after every change of <see cref="Items"/>.</summary>
    public event Action? Changed;

    /// <summary>Loads the shelf once per process; later calls only wait for the work already queued.</summary>
    public Task Load() => Run(static () => { });

    /// <summary>Looks at every item on disk again: the island opened, so moved and deleted files are flagged now.</summary>
    public Task Check() => Run(() =>
    {
        rows = rows.ConvertAll(Probe);
        log?.Invoke($"shelf: checked {rows.Count} items, {rows.Count(r => r.Missing)} missing");
    });

    /// <summary>
    /// Takes a drop. Called inside the drop on the island's thread: a path in a temporary folder is copied here and
    /// now, because its source may delete it as soon as the drop returns. Everything else is queued.
    /// </summary>
    public Task Accept(DroppedItems drop)
    {
        var entries = new List<ShelfEntry>();
        var temp = TempRoots.Count > 0 ? TempRoots : TempFolders.Value;
        foreach (string path in drop.Paths)
        {
            string name = NameOf(path);
            switch (Decide(path, drop.KeepCopy, temp, DriveTypeOf))
            {
                case DropDecision.CopyNow:
                    try
                    {
                        entries.Add(new ShelfEntry(ShelfKind.Copy, name, CopyIn(path)));
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        log?.Invoke($"shelf: could not copy {path} in, keeping a reference: {e.Message}");
                        entries.Add(new ShelfEntry(ShelfKind.Reference, name, path));
                    }

                    break;
                case DropDecision.CopyLater:
                    entries.Add(new ShelfEntry(ShelfKind.Copy, name, path));
                    break;
                default:
                    entries.Add(new ShelfEntry(ShelfKind.Reference, name, path));
                    break;
            }
        }

        foreach (var (name, bytes) in drop.Files)
        {
            entries.Add(new ShelfEntry(ShelfKind.Content, Path.GetFileName(name), Content: bytes));
        }

        if (drop.Text is { Length: > 0 } text)
        {
            var (name, bytes) = ShelfFormats.FromText(text);
            entries.Add(new ShelfEntry(ShelfKind.Content, name, Content: bytes));
        }

        return entries.Count == 0 ? Task.CompletedTask : Run(() => AddAll(entries));
    }

    public Task Add(IReadOnlyList<ShelfEntry> entries) => Run(() => AddAll(entries));

    public Task Remove(IReadOnlyList<ShelfItem> items)
    {
        var ids = items.Select(i => i.Id).ToHashSet();
        return Run(() => RemoveAll(ids));
    }

    /// <summary>The item's text: a file of at most 1 MB that is UTF-8, or Unicode with a byte order mark.</summary>
    public string? ReadText(string name) =>
        Items.FirstOrDefault(i => Same(i.Name, name)) is { } item ? ReadTextFile(item.Path) : null;

    /// <summary>A text file on the shelf as content. Replaces an item of the same name. Queued; returns at once.</summary>
    public void CreateFile(string name, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        Run(() =>
        {
            RemoveAll(rows.Where(r => Same(r.Name, name)).Select(r => r.Id).ToHashSet());
            AddAll([new ShelfEntry(ShelfKind.Content, name, Content: bytes)]);
        });
    }

    /// <summary>Queued; returns at once.</summary>
    public void Remove(string name) => Run(() => RemoveAll(rows.Where(r => Same(r.Name, name)).Select(r => r.Id).ToHashSet()));

    /// <summary>A temporary folder's files are copied in during the drop; Ctrl, a removable or an optical drive copy on the worker.</summary>
    public static DropDecision Decide(string path, bool keepCopy, IReadOnlyList<string> tempRoots, Func<string, DriveType> driveType)
    {
        if (tempRoots.Any(root => IsUnder(path, root)))
        {
            return DropDecision.CopyNow;
        }

        return keepCopy || driveType(path) is DriveType.Removable or DriveType.CDRom ? DropDecision.CopyLater : DropDecision.Reference;
    }

    /// <summary>Copy path and Remove work on a missing item; the others need the file.</summary>
    public static bool Allows(ShelfAction action, ShelfItem item) => action is ShelfAction.CopyPath or ShelfAction.Remove || !item.Missing;

    /// <summary>What Copy path puts on the clipboard: one path per line, unquoted.</summary>
    public static string PathsText(IReadOnlyList<ShelfItem> items) => string.Join(Environment.NewLine, items.Select(i => i.Path));

    /// <summary>A name that is a valid file name: no path or reserved characters, no trailing dots, at most 120 characters.</summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new string([.. name.Select(c => c < ' ' || invalid.Contains(c) ? '_' : c)]).Trim().TrimEnd('.', ' ');
        if (safe.Length > 120)
        {
            string ext = Path.GetExtension(safe);
            safe = ext.Length < 20 ? safe[..(120 - ext.Length)] + ext : safe[..120];
        }

        return safe.Length == 0 ? FallbackName : safe;
    }

    /// <summary>The name, or "name (2).ext", "name (3).ext" and so on, whichever is not taken. Case does not count.</summary>
    public static string Unique(string name, IReadOnlySet<string> taken)
    {
        if (!taken.Contains(name))
        {
            return name;
        }

        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        for (int n = 2; ; n++)
        {
            string candidate = $"{stem} ({n}){ext}";
            if (!taken.Contains(candidate))
            {
                return candidate;
            }
        }
    }

    internal static string? ReadTextFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || info.Length > MaxTextBytes)
            {
                return null;
            }

            byte[] bytes = File.ReadAllBytes(path);
            bool bom = bytes is [0xFF, 0xFE, ..] or [0xFE, 0xFF, ..] or [0xEF, 0xBB, 0xBF, ..];
            if (!bom && bytes.AsSpan().Contains((byte)0))
            {
                return null;
            }

            using var reader = new StreamReader(new MemoryStream(bytes), System.Text.Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
            return reader.ReadToEnd();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return null;
        }
    }

    private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    private static string NameOf(string path)
    {
        string name = Path.GetFileName(Path.TrimEndingDirectorySeparator(path));
        return name.Length > 0 ? name : path;
    }

    private static bool IsUnder(string path, string root)
    {
        string r = Path.TrimEndingDirectorySeparator(root);
        return path.Length > r.Length && path.StartsWith(r, StringComparison.OrdinalIgnoreCase) && path[r.Length] is '\\' or '/';
    }

    private static DriveType DriveTypeOfPath(string path)
    {
        try
        {
            return Path.GetPathRoot(path) is { Length: > 0 } root ? new DriveInfo(root).DriveType : DriveType.Unknown;
        }
        catch (ArgumentException)
        {
            // A share path DriveInfo does not take: a network location, kept as a reference.
            return DriveType.Network;
        }
    }

    // Path.GetTempPath can hand back an 8.3 name ("ADITYA~1") when the user name has a space; dropped paths are long.
    private static unsafe string LongPath(string path)
    {
        Span<char> buffer = stackalloc char[1024];
        fixed (char* p = buffer)
        fixed (char* shortPath = path)
        {
            uint length = PInvoke.GetLongPathName(shortPath, p, (uint)buffer.Length);
            return length > 0 && length < buffer.Length ? new string(buffer[..(int)length]) : path;
        }
    }

    private static ShelfItem Probe(ShelfItem item)
    {
        try
        {
            var file = new FileInfo(item.Path);
            if (file.Exists)
            {
                return item with { Missing = false, Size = file.Length, IsFolder = false };
            }

            if (Directory.Exists(item.Path))
            {
                return item with { Missing = false, Size = null, IsFolder = true };
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        return item with { Missing = true, Size = null };
    }

    private static void CopyTree(string from, string to)
    {
        Directory.CreateDirectory(to);
        foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            // links inside a copied folder are skipped, not followed, so a junction loop cannot fill the disk.
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
            {
                continue;
            }

            string target = Path.Combine(to, entry.Name);
            if (entry is DirectoryInfo)
            {
                CopyTree(entry.FullName, target);
            }
            else
            {
                File.Copy(entry.FullName, target);
            }
        }
    }

    /// <summary>The work, after the first load of this process, then a publish. Disk and database trouble is logged and the last state kept.</summary>
    private Task Run(Action work)
    {
        lock (gate)
        {
            return chain = chain.ContinueWith(_ => Guarded(work), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    private void Guarded(Action work)
    {
        try
        {
            if (!loaded)
            {
                LoadRows();
            }

            work();
            var snapshot = rows.ToArray();
            var names = Array.ConvertAll(snapshot, r => r.Name);
            post(() =>
            {
                Items = snapshot;
                ItemNames = names;
                Changed?.Invoke();
            });
        }
        catch (Exception e) when (e is SqliteException or IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"shelf: {e.Message}");
        }
    }

    private void LoadRows()
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var loadedRows = new List<ShelfItem>();
        using (var connection = db.Open(Feature, Schema))
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, name, path, kind, added FROM shelf_items ORDER BY added DESC, id DESC";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var added = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4));
                loadedRows.Add(Probe(new ShelfItem(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), (ShelfKind)reader.GetInt32(3), added)));
            }
        }

        rows = loadedRows;
        loaded = true;
        Sweep();
        log?.Invoke($"shelf: loaded {rows.Count} items ({rows.Count(r => r.Missing)} missing) in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}ms");
    }

    /// <summary>Deletes the folders of items removed in an earlier process. Folders this process made are never touched.</summary>
    private void Sweep()
    {
        if (!Directory.Exists(Folder))
        {
            return;
        }

        var held = rows.Where(r => r.Kind != ShelfKind.Reference).Select(r => Path.GetDirectoryName(r.Path)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (string slot in Directory.EnumerateDirectories(Folder))
        {
            bool ours;
            lock (slotsMadeHere)
            {
                ours = slotsMadeHere.Contains(slot);
            }

            if (ours || held.Contains(slot))
            {
                continue;
            }

            try
            {
                Directory.Delete(slot, recursive: true);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Something still has a file open; the next process tries again.
            }
        }
    }

    private void AddAll(IReadOnlyList<ShelfEntry> entries)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        var now = DateTimeOffset.UtcNow;
        var taken = rows.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<ShelfItem>();
        using var connection = db.Open(Feature, Schema);
        using var transaction = connection.BeginTransaction();
        foreach (var entry in entries)
        {
            string name = Unique(entry.Kind == ShelfKind.Reference ? entry.Name : SafeName(entry.Name), taken);
            string path;
            try
            {
                path = entry.Kind switch
                {
                    ShelfKind.Content => Store(name, entry.Content ?? []),
                    ShelfKind.Copy when !IsUnder(entry.Path!, Folder) => CopyIn(entry.Path!),
                    _ => entry.Path!,
                };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"shelf: could not keep {entry.Name}: {e.Message}");
                continue;
            }

            taken.Add(name);
            using var insert = connection.CreateCommand();
            insert.CommandText = "INSERT INTO shelf_items(name, path, kind, added) VALUES ($name, $path, $kind, $added) RETURNING id";
            insert.Parameters.AddWithValue("$name", name);
            insert.Parameters.AddWithValue("$path", path);
            insert.Parameters.AddWithValue("$kind", (int)entry.Kind);
            insert.Parameters.AddWithValue("$added", now.ToUnixTimeMilliseconds());
            added.Add(Probe(new ShelfItem((long)insert.ExecuteScalar()!, name, path, entry.Kind, now)));
        }

        transaction.Commit();
        added.Reverse();
        rows.InsertRange(0, added);
        log?.Invoke($"shelf: added {string.Join(", ", added.Select(i => $"{i.Name} ({i.Kind})"))} in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}ms");
    }

    private void RemoveAll(HashSet<long> ids)
    {
        if (ids.Count == 0)
        {
            return;
        }

        using (var connection = db.Open(Feature, Schema))
        using (var transaction = connection.BeginTransaction())
        {
            foreach (long id in ids)
            {
                using var delete = connection.CreateCommand();
                delete.CommandText = "DELETE FROM shelf_items WHERE id = $id";
                delete.Parameters.AddWithValue("$id", id);
                delete.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        rows.RemoveAll(r => ids.Contains(r.Id));
    }

    private string Slot()
    {
        string slot = Path.Combine(Folder, Guid.NewGuid().ToString("N")[..12]);
        lock (slotsMadeHere)
        {
            slotsMadeHere.Add(slot);
        }

        Directory.CreateDirectory(slot);
        return slot;
    }

    private string Store(string name, byte[] content)
    {
        string path = Path.Combine(Slot(), name);
        File.WriteAllBytes(path, content);
        return path;
    }

    private string CopyIn(string source)
    {
        string target = Path.Combine(Slot(), SafeName(NameOf(source)));
        if (File.Exists(source))
        {
            File.Copy(source, target);
        }
        else if (Directory.Exists(source))
        {
            CopyTree(source, target);
        }
        else
        {
            throw new FileNotFoundException("Nothing to copy.", source);
        }

        return target;
    }
}
