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
    public static string Everything(IReadOnlyList<string> words, IReadOnlyList<string> excluded) =>
        words.Count == 0 ? "" : string.Join(' ', words.Select(word => $"\"{word}\"")
            .Concat(excluded.Select(folder => $"!\"{folder.TrimEnd('\\')}\\\""))
            .Append("!\"\\$Recycle.Bin\\\""));

    /// <summary>
    /// The Windows index's SQL: the newest <paramref name="top"/> files and folders whose name has a word starting with
    /// each typed word, outside the excluded folders. Quotes in a word are doubled for the SQL string; the words have no
    /// double quotes left (<see cref="Words"/>). Empty for no words.
    /// </summary>
    public static string IndexSql(IReadOnlyList<string> words, IReadOnlyList<string> excluded, int top = Fetch)
    {
        if (words.Count == 0)
        {
            return "";
        }

        string contains = string.Join(" AND ", words.Select(word => $"\"{Sql(word)}*\""));
        string outside = string.Concat(excluded.Select(folder => $" AND NOT SCOPE='file:{Sql(folder.TrimEnd('\\').Replace('\\', '/'))}'"));
        return $"SELECT TOP {top} System.ItemPathDisplay, System.ItemType FROM SystemIndex WHERE SCOPE='file:' AND CONTAINS(System.FileName, '{contains}'){outside} ORDER BY System.DateModified DESC";
    }

    private static string Sql(string text) => text.Replace("'", "''", StringComparison.Ordinal);
}
