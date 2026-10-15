namespace Tomte.Core.Search;

/// <summary>One file or folder a file search found.</summary>
public readonly record struct FileHit(string Path, bool Folder)
{
    /// <summary>The row for it: its name, the folder it is in as the dim text, and the path to open, show and draw the icon of.</summary>
    public SearchItem Item()
    {
        string name = System.IO.Path.GetFileName(Path.TrimEnd('\\'));
        return new(Folder ? SearchKind.Folder : SearchKind.File, name.Length > 0 ? name : Path, Path, System.IO.Path.GetDirectoryName(Path) ?? "", Path, Path);
    }
}

/// <summary>
/// What a file search asks, beyond the words that must each be in the name: the folder the hits are under (a full
/// path), files only (false) or folders only (true), the extensions a file may have (without the dot), and the days of
/// last change, local and both included. With <paramref name="InPath"/> a word may also be in the names of the folders
/// above (Everything only; the index keeps to names). AI search fills these in; the search box asks with words only.
/// </summary>
public sealed record FileFilter(IReadOnlyList<string> Words, string? Folder = null, bool? Folders = null, IReadOnlyList<string>? Extensions = null, DateOnly? From = null, DateOnly? To = null, bool InPath = false)
{
    /// <summary>Asks nothing that narrows the search: no words, folder, extension or day.</summary>
    public bool Empty => Words.Count == 0 && Folder is null && Extensions is not { Count: > 0 } && From is null && To is null;
}

/// <summary>
/// What the two file searches are asked. Neither matches fuzzily, so the typed text is split into words and a name must
/// hold every word, in any order: Everything gets each word quoted, so its operators and functions are typed text; the
/// Windows index gets each word as the start of a word in the name, since a match inside a word scans the whole index
/// for seconds. Both leave out Windows, the program folders and app data, where a person's own files are not.
/// </summary>
public static class FileQuery
{
    /// <summary>How many hits a file search returns, before the ranking keeps the best few.</summary>
    public const int Fetch = 60;

    /// <summary>More typed words than this are dropped: a sentence is a question for Ask, not a file name.</summary>
    public const int MaxWords = 8;

    /// <summary>The typed words: split at white space, double quotes and control characters taken out, each word once.</summary>
    public static IReadOnlyList<string> Words(string text) =>
        [.. text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(word => new string([.. word.Where(c => c != '"' && !char.IsControl(c))]))
            .Where(word => word.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(MaxWords)];

    /// <summary>The folders left out of both searches, as full paths.</summary>
    public static IReadOnlyList<string> Excluded() =>
        [.. new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Windows),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "AppData"),
        }.Where(folder => folder.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase)];

    /// <summary>
    /// Everything's search: every word quoted, so "|", "!", "&lt;" and "name:" are only text, then each excluded folder
    /// and the recycle bins as a quoted path to leave out (a term with a backslash matches the full path). Empty for no words.
    /// </summary>
    public static string Everything(IReadOnlyList<string> words, IReadOnlyList<string> excluded) => Everything(new FileFilter(words), excluded);

    /// <summary>
    /// Everything's search for a filter: the words quoted (with path: when they may match the folders above too), the
    /// folder as a quoted path (which matches every path under
    /// it), file: or folder:, ext: with the extensions, dm: from the first day and before the day after the last (ISO
    /// dates), then the folders left out. Empty for an empty filter.
    /// </summary>
    public static string Everything(FileFilter filter, IReadOnlyList<string> excluded)
    {
        if (filter.Empty)
        {
            return "";
        }

        var terms = filter.Words.Select(word => filter.InPath ? $"path:\"{word}\"" : $"\"{word}\"").ToList();
        if (filter.Folder is { } folder)
        {
            terms.Add($"\"{folder.TrimEnd('\\')}\\\"");
        }

        if (filter.Folders is { } folders)
        {
            terms.Add(folders ? "folder:" : "file:");
        }

        if (filter.Extensions is { Count: > 0 } extensions)
        {
            terms.Add("ext:" + string.Join(';', extensions));
        }

        if (filter.From is { } from)
        {
            terms.Add($"dm:>={Iso(from)}");
        }

        if (filter.To is { } to)
        {
            terms.Add($"dm:<{Iso(to.AddDays(1))}");
        }

        return string.Join(' ', terms
            .Concat(excluded.Select(folder => $"!\"{folder.TrimEnd('\\')}\\\""))
            .Append("!\"\\$Recycle.Bin\\\""));
    }

    /// <summary>
    /// The Windows index's SQL: the newest <paramref name="top"/> files and folders whose name has a word starting with
    /// each typed word, outside the excluded folders. Quotes in a word are doubled for the SQL string; the words have no
    /// double quotes left (<see cref="Words"/>). Empty for no words.
    /// </summary>
    public static string IndexSql(IReadOnlyList<string> words, IReadOnlyList<string> excluded, int top = Fetch) => IndexSql(new FileFilter(words), excluded, top);

    /// <summary>
    /// The Windows index's SQL for a filter: the folder as the scope, each word as a word start in the name, the item
    /// type, the extensions, and the days as UTC times from the first local midnight to the one after the last day (the
    /// index keeps UTC). <paramref name="zone"/> is the local zone; tests set it. Empty for an empty filter.
    /// </summary>
    public static string IndexSql(FileFilter filter, IReadOnlyList<string> excluded, int top = Fetch, TimeZoneInfo? zone = null)
    {
        if (filter.Empty)
        {
            return "";
        }

        zone ??= TimeZoneInfo.Local;
        var where = new List<string> { $"SCOPE='file:{(filter.Folder is { } folder ? Sql(Slashed(folder)) : "")}'" };
        if (filter.Words.Count > 0)
        {
            where.Add($"CONTAINS(System.FileName, '{string.Join(" AND ", filter.Words.Select(word => $"\"{Sql(word)}*\""))}')");
        }

        if (filter.Folders is { } folders)
        {
            where.Add(folders ? "System.ItemType = 'Directory'" : "System.ItemType <> 'Directory'");
        }

        if (filter.Extensions is { Count: > 0 } extensions)
        {
            where.Add($"({string.Join(" OR ", extensions.Select(e => $"System.FileExtension = '.{Sql(e)}'"))})");
        }

        if (filter.From is { } from)
        {
            where.Add($"System.DateModified >= '{Utc(from, zone)}'");
        }

        if (filter.To is { } to)
        {
            where.Add($"System.DateModified < '{Utc(to.AddDays(1), zone)}'");
        }

        where.AddRange(excluded.Select(folder => $"NOT SCOPE='file:{Sql(Slashed(folder))}'"));
        return $"SELECT TOP {top} System.ItemPathDisplay, System.ItemType FROM SystemIndex WHERE {string.Join(" AND ", where)} ORDER BY System.DateModified DESC";
    }

    private static string Sql(string text) => text.Replace("'", "''", StringComparison.Ordinal);

    private static string Slashed(string folder) => folder.TrimEnd('\\').Replace('\\', '/');

    private static string Iso(DateOnly day) => day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture);

    // Local midnight in UTC; where a clock change skips midnight, the day starts an hour later.
    private static string Utc(DateOnly day, TimeZoneInfo zone)
    {
        var midnight = day.ToDateTime(TimeOnly.MinValue);
        midnight = zone.IsInvalidTime(midnight) ? midnight.AddHours(1) : midnight;
        return TimeZoneInfo.ConvertTimeToUtc(midnight, zone).ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
    }
}

/// <summary>A file search on this PC: Everything when it runs, else the Windows index. Blocks: worker threads only.</summary>
public static class FileSearch
{
    /// <summary>
    /// The hits, newest first, and whether Everything gave them. Null hits when the Windows index cannot be reached, or
    /// for a search cancelled before it went on to the index.
    /// </summary>
    public static (IReadOnlyList<FileHit>? Hits, bool Everything) Run(FileFilter filter, int max, CancellationToken cancel)
    {
        var hits = Everything.Running ? Everything.Search(FileQuery.Everything(filter, FileQuery.Excluded()), max, cancel) : null;
        // A search a newer keystroke replaced does not go on to the index.
        return hits is not null ? (hits, true)
            : cancel.IsCancellationRequested ? (null, false)
            : (WindowsIndex.Search(filter, max, cancel), false);
    }
}
