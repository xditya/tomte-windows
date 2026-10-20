using System.Security.Cryptography;
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

    /// <summary>Pins the items, or unpins them when every one is pinned. A pinned item stays on the shelf after a drag out and a Clear shelf.</summary>
    Pin,
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

    /// <summary>Stays on the shelf after it is dragged out, whatever the setting says, and when the shelf is cleared.</summary>
    public bool Pinned { get; init; }
}

/// <summary>
/// Something to put on the shelf: a path for a reference or a copy, and for content either its bytes or a file already
/// in the shelf folder (a virtual file received during a drop).
/// </summary>
public sealed record ShelfEntry(ShelfKind Kind, string Name, string? Path = null, byte[]? Content = null);

/// <summary>
/// What one drop carried, read inside the drop while the source still holds it. Files are virtual files already written
/// into the shelf folder by <see cref="ShelfStore.Receive"/>. KeepCopy is Ctrl at the drop.
/// </summary>
public sealed record DroppedItems(IReadOnlyList<string> Paths, IReadOnlyList<(string Name, string Path)> Files, string? Text, bool KeepCopy);

/// <summary>
/// The shelf: items in tomte.db (feature "shelf"), copies and content as files under <see cref="Folder"/>, one folder
/// per item so names never collide on disk. Every database and disk operation runs in order on one worker task chain;
/// the island's thread only reads <see cref="Items"/>, which the worker replaces through <c>post</c>. Nothing is
/// opened until the first call that needs the shelf.
/// Removing an item marks its row; its files stay, because a browser or an editor given their path by a drag may read
/// them minutes later. The first load in the next process deletes the folders of marked rows, then the rows. Nothing
/// else in the shelf folder is ever deleted: a folder no row names may be the only copy the user has, for example after
/// a damaged database was set aside.
/// </summary>
public sealed class ShelfStore : IShelf
{
    public const string FallbackName = "Item";

    /// <summary>
    /// A temporary folder's file up to this size is copied inside the drop, which holds the island and the source for
    /// as long as the copy takes. A larger file is copied off the island's thread from a handle opened in the drop.
    /// </summary>
    public const long InDropCopyBytesDefault = 64L << 20;

    /// <summary>A virtual file larger than this is refused.</summary>
    public const long MaxReceivedBytesDefault = 256L << 20;

    private const string Feature = "shelf";

    // ReadText gives up above this size; text extraction for large and binary files is the AI files task's.
    private const int MaxTextBytes = 1 << 20;

    // The whole history, oldest first. Never edit or reorder a step that has merged; add at the end.
    private static readonly string[] Schema =
    [
        "CREATE TABLE shelf_items(id INTEGER PRIMARY KEY, name TEXT NOT NULL, path TEXT NOT NULL, kind INTEGER NOT NULL, added INTEGER NOT NULL, removed INTEGER NOT NULL DEFAULT 0)",
        "ALTER TABLE shelf_items ADD COLUMN pinned INTEGER NOT NULL DEFAULT 0",
    ];

    // Names Windows keeps for devices, with or without an extension.
    private static readonly HashSet<string> DeviceNames = new(
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(0, 10).SelectMany(n => new[] { $"COM{n}", $"LPT{n}" }), "COM¹", "COM²", "COM³", "LPT¹", "LPT²", "LPT³"],
        StringComparer.OrdinalIgnoreCase);

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
    private Task chain = Task.CompletedTask;

    // Folders Slot made in this process that no item has taken yet, the only ones a duplicate's may be deleted from.
    // Made inside a drop on the island's thread and on the worker, so under its own lock.
    private readonly HashSet<string> unclaimed = new(StringComparer.OrdinalIgnoreCase);

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

    /// <summary>Folders whose files are taken in during the drop, because their owner may delete them right after it.</summary>
    public IReadOnlyList<string> TempRoots { get; init; } = [];

    /// <summary>The drive type of a path; injected by tests. Removable and optical drives are copied in.</summary>
    public Func<string, DriveType> DriveTypeOf { get; init; } = DriveTypeOfPath;

    /// <summary>A temporary file on the shelf's volume becomes a hard link, instant at any size; tests turn it off to reach the copies.</summary>
    public bool LinkTemporaryFiles { get; init; } = true;

    public long InDropCopyBytes { get; init; } = InDropCopyBytesDefault;

    public long MaxReceivedBytes { get; init; } = MaxReceivedBytesDefault;

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
    /// Writes one virtual file into a new folder of the shelf as it is read. Called inside the drop, where the stream is
    /// valid. Null when a read fails, when it is larger than <see cref="MaxReceivedBytes"/> or when it cannot be written;
    /// nothing is left behind then.
    /// </summary>
    public string? Receive(string name, ReadChunk read)
    {
        try
        {
            string? received = IntoSlot(SafeName(Path.GetFileName(name)), path =>
            {
                using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16);
                // the island's thread waits for the whole read inside the drop, up to the cap.
                // IDataObjectAsyncCapability would let the source be read after the drop returns.
                return ShelfFormats.CopyCapped(read, file, MaxReceivedBytes);
            });
            if (received is null)
            {
                log?.Invoke($"shelf: refused {name}: a read failed or it is larger than {MaxReceivedBytes >> 20} MB");
            }

            return received;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"shelf: could not receive {name}: {e.Message}");
            return null;
        }
    }

    /// <summary>
    /// Takes a drop. Called inside the drop on the island's thread: a path in a temporary folder is taken here and now,
    /// because its source may delete it as soon as the drop returns. Everything else is queued. The task completes when
    /// every item, a large temporary copy included, is on the shelf, with the items as they were added.
    /// </summary>
    public Task<IReadOnlyList<ShelfItem>> Accept(DroppedItems drop)
    {
        var entries = new List<ShelfEntry>();
        var later = new List<Task<IReadOnlyList<ShelfItem>>>();
        var temp = TempRoots.Count > 0 ? TempRoots : TempFolders.Value;
        foreach (string path in drop.Paths)
        {
            string name = NameOf(path);
            switch (Decide(path, drop.KeepCopy, temp, DriveTypeOf))
            {
                case DropDecision.CopyNow:
                    try
                    {
                        if (TakeTemporary(path, name) is { } done)
                        {
                            entries.Add(done);
                        }
                        else
                        {
                            later.Add(TakeLargeTemporary(path, name));
                        }
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        log?.Invoke($"shelf: could not take {path} in, keeping a reference: {e.Message}");
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

        foreach (var (name, received) in drop.Files)
        {
            entries.Add(new ShelfEntry(ShelfKind.Content, Path.GetFileName(name), received));
        }

        if (drop.Text is { Length: > 0 } text)
        {
            var (name, bytes) = ShelfFormats.FromText(text);
            entries.Add(new ShelfEntry(ShelfKind.Content, name, Content: bytes));
        }

        if (entries.Count > 0)
        {
            later.Add(Add(entries));
        }

        return All(later);

        static async Task<IReadOnlyList<ShelfItem>> All(List<Task<IReadOnlyList<ShelfItem>>> tasks) => [.. (await Task.WhenAll(tasks)).SelectMany(added => added)];
    }

    /// <summary>
    /// Queued; the task gives the items as they were added, none when the disk or the database refused them. An entry
    /// that is an item already (see <see cref="Already"/>) gives that item, brought to the front, and adds none.
    /// </summary>
    public async Task<IReadOnlyList<ShelfItem>> Add(IReadOnlyList<ShelfEntry> entries)
    {
        IReadOnlyList<ShelfItem> added = [];
        await Run(() => added = AddAll(entries));
        return added;
    }

    /// <summary>
    /// A copy of a file as a new item called <paramref name="name"/> (made unique), in a folder of the shelf's own. The
    /// file it came from is only read: a copy, never a link, so changing one never changes the other. Another copy of the
    /// same bytes under that name already on the shelf comes to the front instead; the item the copy is made from never
    /// counts, even when it is a file of the shelf's own. Null when the copy failed; nothing is left behind then.
    /// </summary>
    public async Task<ShelfItem?> AddCopy(string source, string name)
    {
        IReadOnlyList<ShelfItem> added = [];
        await Run(() =>
        {
            string from = Normal(source);
            if (Already(new ShelfEntry(ShelfKind.Copy, name, source), [.. rows.Where(r => !Same(Normal(r.Path), from))], byPath: false) is { } same)
            {
                // That copy is on the shelf already; its path is that item, so this brings it to the front.
                added = AddAll([new ShelfEntry(ShelfKind.Copy, same.Name, same.Path)]);
                return;
            }

            string unique = Unique(SafeName(name), rows.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase));
            string copy = IntoSlot(unique, target =>
            {
                File.Copy(source, target);
                return true;
            })!;
            added = AddAll([new ShelfEntry(ShelfKind.Copy, unique, copy)], refuseDuplicates: false);
        });
        return added.Count > 0 ? added[0] : null;
    }

    public Task Remove(IReadOnlyList<ShelfItem> items)
    {
        var ids = items.Select(i => i.Id).ToHashSet();
        return Run(() => RemoveAll(ids));
    }

    /// <summary>Pins or unpins the items. Queued; returns at once.</summary>
    public Task SetPinned(IReadOnlyList<ShelfItem> items, bool pinned)
    {
        var ids = items.Select(i => i.Id).ToHashSet();
        return Run(() =>
        {
            using (var connection = db.Open(Feature, Schema))
            using (var transaction = connection.BeginTransaction())
            {
                foreach (long id in ids)
                {
                    using var update = connection.CreateCommand();
                    update.CommandText = "UPDATE shelf_items SET pinned = $pinned WHERE id = $id";
                    update.Parameters.AddWithValue("$pinned", pinned ? 1 : 0);
                    update.Parameters.AddWithValue("$id", id);
                    update.ExecuteNonQuery();
                }

                transaction.Commit();
            }

            rows = rows.ConvertAll(r => ids.Contains(r.Id) ? r with { Pinned = pinned } : r);
        });
    }

    /// <summary>The "Remove from the shelf after dragging out" setting, read as each drag ends.</summary>
    public Func<bool> RemoveAfterDragOut { get; set; } = () => true;

    /// <summary>
    /// After a drag out: what <see cref="ShelfFormats.LeaveAfterDrag"/> says leaves the shelf does. The disk is looked at
    /// on the worker.
    /// </summary>
    public Task AfterDragOut(IReadOnlyList<ShelfItem> dragged, ShelfEffect returned, ShelfEffect performed)
    {
        bool removeAfter = RemoveAfterDragOut();
        return Run(() =>
        {
            var gone = ShelfFormats.LeaveAfterDrag(dragged, returned, performed, removeAfter, p => File.Exists(p) || Directory.Exists(p));
            if (gone.Count > 0)
            {
                log?.Invoke($"shelf: {gone.Count} off the shelf after the drag out");
                RemoveAll(gone.Select(i => i.Id).ToHashSet());
            }
        });
    }

    /// <summary>The item's text: a file of at most 1 MB that is UTF-8, or Unicode with a byte order mark.</summary>
    public string? ReadText(string name) =>
        Items.FirstOrDefault(i => Same(i.Name, name)) is { } item ? ReadTextFile(item.Path) : null;

    public string? PathOf(string name) => Items.FirstOrDefault(i => Same(i.Name, name))?.Path;

    /// <summary>
    /// A text file on the shelf as content. Replaces an item of the same name, and is an item of that name even when
    /// another item holds the same bytes, so the tool's undo removes what it made. Queued; returns at once.
    /// </summary>
    public void CreateFile(string name, string content)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(content);
        Run(() =>
        {
            RemoveAll(rows.Where(r => Same(r.Name, name)).Select(r => r.Id).ToHashSet());
            AddAll([new ShelfEntry(ShelfKind.Content, name, Content: bytes)], refuseDuplicates: false);
        });
    }

    /// <summary>Queued; returns at once.</summary>
    public void Remove(string name) => Run(() => RemoveAll(rows.Where(r => Same(r.Name, name)).Select(r => r.Id).ToHashSet()));

    /// <summary>A temporary folder's files are taken in during the drop; Ctrl, a removable or an optical drive copy on the worker.</summary>
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

    /// <summary>
    /// A name that is a valid file name: no path or reserved characters, no trailing dots, at most 120 characters, and no
    /// device name (CON, NUL, COM1 and the rest), which Windows reads as the device with any extension.
    /// </summary>
    public static string SafeName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        string safe = new string([.. name.Select(c => c < ' ' || invalid.Contains(c) ? '_' : c)]).Trim().TrimEnd('.', ' ');
        if (safe.Length > 120)
        {
            string ext = Path.GetExtension(safe);
            safe = ext.Length < 20 ? safe[..(120 - ext.Length)] + ext : safe[..120];
        }

        if (DeviceNames.Contains(safe.Split('.')[0].TrimEnd(' ')))
        {
            safe = "_" + safe;
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

    /// <summary>
    /// The folder of an item that the sweep may delete: a direct child of the shelf folder with the 12 hex digits the
    /// shelf names its folders with, and no link. Null for anything else.
    /// </summary>
    public static string? OwnSlot(string folder, string itemPath)
    {
        string? slot = Path.GetDirectoryName(itemPath);
        if (slot is null || !string.Equals(Path.GetDirectoryName(slot), Path.TrimEndingDirectorySeparator(folder), StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        string name = Path.GetFileName(slot);
        if (name.Length != 12 || !name.All(char.IsAsciiHexDigit))
        {
            return null;
        }

        return Directory.Exists(slot) && new DirectoryInfo(slot).Attributes.HasFlag(FileAttributes.ReparsePoint) ? null : slot;
    }

    public static string? ReadTextFile(string path)
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

    private static unsafe bool HardLink(string link, string existing)
    {
        fixed (char* l = link)
        fixed (char* e = existing)
        {
            return PInvoke.CreateHardLink(l, e, null);
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

    private static long TreeSize(string folder) =>
        new DirectoryInfo(folder).EnumerateFiles("*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 }).Sum(f => f.Length);

    /// <summary>
    /// A folder's files and subfolders. Links (symbolic links and junctions) are skipped, not followed, so a junction
    /// loop cannot fill the disk; other reparse points, OneDrive's online-only files among them, are copied. With
    /// <paramref name="link"/>, a file becomes a hard link where it can. A file that is copied costs its size from
    /// <paramref name="budget"/>, and the copy stops, part done, before the budget would go below 0. Returns what is
    /// left of the budget, negative when it stopped.
    /// </summary>
    private static long CopyTree(string from, string to, bool link, long budget)
    {
        Directory.CreateDirectory(to);
        foreach (var entry in new DirectoryInfo(from).EnumerateFileSystemInfos())
        {
            if (entry.LinkTarget is not null)
            {
                continue;
            }

            string target = Path.Combine(to, entry.Name);
            if (entry is DirectoryInfo)
            {
                budget = CopyTree(entry.FullName, target, link, budget);
            }
            else if (!link || !HardLink(target, entry.FullName))
            {
                budget -= ((FileInfo)entry).Length;
                if (budget >= 0)
                {
                    File.Copy(entry.FullName, target);
                }
            }

            if (budget < 0)
            {
                return budget;
            }
        }

        return budget;
    }

    private static void TryDelete(string slot)
    {
        try
        {
            Directory.Delete(slot, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
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
        {
            using (var command = connection.CreateCommand())
            {
                command.CommandText = "SELECT id, name, path, kind, added, pinned FROM shelf_items WHERE removed = 0 ORDER BY added DESC, id DESC";
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var added = DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4));
                    loadedRows.Add(Probe(new ShelfItem(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), (ShelfKind)reader.GetInt32(3), added) { Pinned = reader.GetInt32(5) != 0 }));
                }
            }

            Sweep(connection, loadedRows);
        }

        rows = loadedRows;
        loaded = true;
        log?.Invoke($"shelf: loaded {rows.Count} items ({rows.Count(r => r.Missing)} missing) in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}ms");
    }

    /// <summary>
    /// Deletes what items removed in an earlier process left behind, then their rows. Only on that evidence: a marked
    /// row's folder, when <see cref="OwnSlot"/> accepts it and the shelf folder itself is no link. A folder that is in
    /// use keeps its row for the next process. A folder that a <paramref name="live"/> row still names stays: a shelf
    /// file Ctrl-dropped back onto the shelf is kept at its path, so two items can name one folder. Two processes
    /// sweeping at once delete the same folders; the second finds them gone.
    /// </summary>
    private void Sweep(SqliteConnection connection, List<ShelfItem> live)
    {
        var marked = new List<(long Id, string Path, ShelfKind Kind)>();
        using (var command = connection.CreateCommand())
        {
            command.CommandText = "SELECT id, path, kind FROM shelf_items WHERE removed = 1";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                marked.Add((reader.GetInt64(0), reader.GetString(1), (ShelfKind)reader.GetInt32(2)));
            }
        }

        if (marked.Count == 0)
        {
            return;
        }

        bool folderIsLink = Directory.Exists(Folder) && new DirectoryInfo(Folder).Attributes.HasFlag(FileAttributes.ReparsePoint);
        var done = new List<long>();
        foreach (var (id, path, kind) in marked)
        {
            if (kind != ShelfKind.Reference && !folderIsLink && OwnSlot(Folder, path) is { } slot && Directory.Exists(slot)
                && !live.Any(r => Same(r.Path, slot) || IsUnder(r.Path, slot)))
            {
                try
                {
                    Directory.Delete(slot, recursive: true);
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    // Something still has a file open, or another process got there first and it is half gone.
                    continue;
                }
            }

            done.Add(id);
        }

        using var transaction = connection.BeginTransaction();
        foreach (long id in done)
        {
            using var delete = connection.CreateCommand();
            delete.CommandText = "DELETE FROM shelf_items WHERE id = $id";
            delete.Parameters.AddWithValue("$id", id);
            delete.ExecuteNonQuery();
        }

        transaction.Commit();
    }

    private List<ShelfItem> AddAll(IReadOnlyList<ShelfEntry> entries, bool refuseDuplicates = true)
    {
        long started = System.Diagnostics.Stopwatch.GetTimestamp();
        // The shelf is ordered by this time, so a tile brought to the front must not share a millisecond with the one before.
        var now = DateTimeOffset.FromUnixTimeMilliseconds(Math.Max(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(), rows.Count == 0 ? 0 : rows.Max(r => r.Added.ToUnixTimeMilliseconds()) + 1));
        var taken = rows.Select(r => r.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = new List<ShelfItem>();
        using var connection = db.Open(Feature, Schema);
        using var transaction = connection.BeginTransaction();
        foreach (var entry in entries)
        {
            if (refuseDuplicates && Already(entry, [.. added, .. rows]) is { } same)
            {
                // The same file again: its tile comes to the front instead of a second one.
                Forget(entry);
                if (!added.Contains(same))
                {
                    using var bump = connection.CreateCommand();
                    bump.CommandText = "UPDATE shelf_items SET added = $added WHERE id = $id";
                    bump.Parameters.AddWithValue("$added", now.ToUnixTimeMilliseconds());
                    bump.Parameters.AddWithValue("$id", same.Id);
                    bump.ExecuteNonQuery();
                    added.Add(Probe(same with { Added = now }));
                }

                log?.Invoke($"shelf: {same.Name} is on the shelf already, brought to the front");
                continue;
            }

            string name = Unique(entry.Kind == ShelfKind.Reference ? entry.Name : SafeName(entry.Name), taken);
            string path;
            try
            {
                path = entry switch
                {
                    { Kind: ShelfKind.Content, Content: { } bytes } => Store(name, bytes),
                    { Kind: ShelfKind.Content } => Rename(entry.Path!, name),
                    { Kind: ShelfKind.Copy } when !IsUnder(entry.Path!, Folder) => CopyIn(entry.Path!),
                    _ => entry.Path!,
                };
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"shelf: could not keep {entry.Name}: {e.Message}");
                continue;
            }

            taken.Add(name);
            Claim(Path.GetDirectoryName(path));
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
        rows.RemoveAll(r => added.Any(a => a.Id == r.Id));
        rows.InsertRange(0, added);
        log?.Invoke($"shelf: added {string.Join(", ", added.Select(i => $"{i.Name} ({i.Kind})"))} in {System.Diagnostics.Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}ms");
        return added;
    }

    /// <summary>
    /// The item that already holds what <paramref name="entry"/> brings, or null. A path is one file: an entry that
    /// would sit at an item's path (a reference, or a shelf file dropped back) is that item, ignoring case. A file the
    /// shelf keeps itself (a copy or content) is also the same as one of the shelf's own files with its name, or that
    /// name made unique, and its size and bytes; <paramref name="byPath"/> false asks that alone. Folders the shelf
    /// keeps are compared by path only.
    /// </summary>
    private ShelfItem? Already(ShelfEntry entry, IReadOnlyList<ShelfItem> items, bool byPath = true)
    {
        if (byPath && entry.Path is { } at && (entry.Kind != ShelfKind.Copy || IsUnder(at, Folder)))
        {
            string path = Normal(at);
            if (items.FirstOrDefault(i => Same(Normal(i.Path), path)) is { } here)
            {
                return here;
            }
        }

        if (entry.Kind == ShelfKind.Reference)
        {
            return null;
        }

        try
        {
            long size = entry.Content?.Length ?? (File.Exists(entry.Path) ? new FileInfo(entry.Path!).Length : -1);
            string name = SafeName(entry.Name);
            byte[]? hash = null;
            foreach (var item in items.Where(i => i.Kind != ShelfKind.Reference && SameName(i.Name, name) && File.Exists(i.Path)))
            {
                if (new FileInfo(item.Path).Length != size)
                {
                    continue;
                }

                hash ??= entry.Content is { } bytes ? SHA256.HashData(bytes) : HashOf(entry.Path!);
                if (hash.AsSpan().SequenceEqual(HashOf(item.Path)))
                {
                    return item;
                }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A file that cannot be read is taken as new.
        }

        return null;

        static byte[] HashOf(string path)
        {
            using var file = File.OpenRead(path);
            return SHA256.HashData(file);
        }
    }

    /// <summary>
    /// A duplicate's own folder goes again, and only when <see cref="Slot"/> made it in this process and no item has
    /// taken it: a removed item's folder stays until the next process, as every removed item's does.
    /// </summary>
    private void Forget(ShelfEntry entry)
    {
        if (entry.Path is { } path && OwnSlot(Folder, path) is { } slot && Claim(slot))
        {
            TryDelete(slot);
        }
    }

    /// <summary>The folder is an item's, or gone, from now on; true when it was one <see cref="Slot"/> made that nothing had taken.</summary>
    private bool Claim(string? slot)
    {
        lock (unclaimed)
        {
            return slot is not null && unclaimed.Remove(slot);
        }
    }

    /// <summary>The item's name is <paramref name="name"/>, or the "name (2).ext" form <see cref="Unique"/> makes of it.</summary>
    private static bool SameName(string item, string name)
    {
        string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
        return Same(item, name) || (item.Length > stem.Length + ext.Length + 3
            && item.StartsWith(stem + " (", StringComparison.OrdinalIgnoreCase) && item.EndsWith(")" + ext, StringComparison.OrdinalIgnoreCase)
            && int.TryParse(item[(stem.Length + 2)..^(ext.Length + 1)], out _));
    }

    private static string Normal(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return path;
        }
    }

    /// <summary>Marks the rows removed. Their files stay until the next process sweeps them.</summary>
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
                using var mark = connection.CreateCommand();
                mark.CommandText = "UPDATE shelf_items SET removed = 1 WHERE id = $id";
                mark.Parameters.AddWithValue("$id", id);
                mark.ExecuteNonQuery();
            }

            transaction.Commit();
        }

        rows.RemoveAll(r => ids.Contains(r.Id));
    }

    private string Slot()
    {
        string slot = Path.Combine(Folder, Guid.NewGuid().ToString("N")[..12]);
        Directory.CreateDirectory(slot);
        lock (unclaimed)
        {
            unclaimed.Add(slot);
        }

        return slot;
    }

    /// <summary>
    /// A new folder of the shelf with <paramref name="name"/> in it, written by <paramref name="write"/>. The folder goes
    /// again when the write throws or returns false, so a failure leaves nothing behind; null when it returned false.
    /// </summary>
    private string? IntoSlot(string name, Func<string, bool> write)
    {
        string slot = Slot();
        string path = Path.Combine(slot, name);
        try
        {
            if (write(path))
            {
                return path;
            }
        }
        catch
        {
            TryDelete(slot);
            throw;
        }

        TryDelete(slot);
        return null;
    }

    private string Store(string name, byte[] content) => IntoSlot(name, path =>
    {
        File.WriteAllBytes(path, content);
        return true;
    })!;

    /// <summary>A received file takes its shelf name inside its own folder, so a drag out gives it the name the tile shows.</summary>
    private static string Rename(string path, string name)
    {
        string target = Path.Combine(Path.GetDirectoryName(path)!, name);
        if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
        {
            File.Move(path, target);
        }

        return target;
    }

    /// <summary>
    /// A temporary file or folder taken in inside the drop, or null when it is too large to copy here. A file on the
    /// shelf's volume becomes a hard link, which is instant and keeps the data when the source deletes its name; a
    /// folder on that volume becomes a tree of hard links. What cannot be linked is copied, up to
    /// <see cref="InDropCopyBytes"/> in all. Nothing is left behind when it fails or is too large.
    /// </summary>
    private ShelfEntry? TakeTemporary(string path, string name)
    {
        bool sameVolume = string.Equals(Path.GetPathRoot(path), Path.GetPathRoot(Folder), StringComparison.OrdinalIgnoreCase);
        string? taken = IntoSlot(SafeName(name), target =>
        {
            if (File.Exists(path))
            {
                if (LinkTemporaryFiles && HardLink(target, path))
                {
                    return true;
                }

                if (new FileInfo(path).Length > InDropCopyBytes)
                {
                    return false;
                }

                // up to InDropCopyBytes is copied here, holding the island and the source for the copy's
                // length; IDataObjectAsyncCapability would let the source wait while the copy runs elsewhere.
                File.Copy(path, target);
                return true;
            }

            if (!Directory.Exists(path))
            {
                throw new FileNotFoundException("Nothing to take in.", path);
            }

            // sizing a folder walks the whole tree inside the drop.
            bool link = LinkTemporaryFiles && sameVolume;
            return (link || TreeSize(path) <= InDropCopyBytes) && CopyTree(path, target, link, InDropCopyBytes) >= 0;
        });
        return taken is null ? null : new ShelfEntry(ShelfKind.Copy, name, taken);
    }

    /// <summary>
    /// A large temporary file or folder, copied off the island's thread and then put on the shelf. A file is opened here,
    /// inside the drop, with delete sharing: its source can still delete it, but the data stays readable until the copy
    /// is done. It is opened before the shelf folder is made, so a file that cannot be read leaves no folder behind.
    /// </summary>
    private Task<IReadOnlyList<ShelfItem>> TakeLargeTemporary(string path, string name)
    {
        FileStream? source = File.Exists(path) ? new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16) : null;
        string target = Path.Combine(Slot(), SafeName(name));
        return Task.Run(() =>
        {
            try
            {
                if (source is not null)
                {
                    using (source)
                    using (var copy = new FileStream(target, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16))
                    {
                        source.CopyTo(copy);
                    }
                }
                else
                {
                    // a large folder is copied after the drop returns, and a source that deletes it at once
                    // wins; IDataObjectAsyncCapability would let it wait.
                    CopyTree(path, target, link: false, long.MaxValue);
                }

                return Add([new ShelfEntry(ShelfKind.Copy, name, target)]);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                log?.Invoke($"shelf: could not copy {path} in, keeping a reference: {e.Message}");
                TryDelete(Path.GetDirectoryName(target)!);
                return Add([new ShelfEntry(ShelfKind.Reference, name, path)]);
            }
        });
    }

    private string CopyIn(string source) => IntoSlot(SafeName(NameOf(source)), target =>
    {
        if (File.Exists(source))
        {
            File.Copy(source, target);
        }
        else if (Directory.Exists(source))
        {
            CopyTree(source, target, link: false, long.MaxValue);
        }
        else
        {
            throw new FileNotFoundException("Nothing to copy.", source);
        }

        return true;
    })!;
}
