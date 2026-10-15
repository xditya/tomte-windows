using System.Globalization;
using System.Text;
using System.Text.Json;
using Tomte.Core.Search;

namespace Tomte.AI.Palette;

/// <summary>
/// A file find_files returned: its path, whether it is a folder, its size in bytes (null for a folder, or a file that
/// could not be read) and when it last changed (null when it could not be read).
/// </summary>
public sealed record FoundFile(string Path, bool Folder, long? Size, DateTime? Modified);

/// <summary>
/// AI search from the search box. The typed sentence goes to the AI box's provider with two tools: find_files runs the
/// local file search (Everything, else the Windows index) and answers with names, folders, sizes and dates, never what
/// is in a file, each numbered; show_results ends the search with the numbers of the picked files and one line on why.
/// Only files find_files returned in this search can be shown, so a path the model makes up never reaches the list.
/// One per search, run by <see cref="PaletteSession"/>, one call at a time.
/// </summary>
/// <param name="find">The local search: a filter and the most hits to return; null when no search can be asked.</param>
public sealed class FileFinder(Func<FileFilter, int, CancellationToken, IReadOnlyList<FoundFile>?> find)
{
    public const string FindFiles = "find_files", ShowResults = "show_results";

    /// <summary>Hits per find_files, finds per search, picks shown, words, extensions.</summary>
    public const int PerCall = 50, MaxCalls = 3, MaxPicks = 8, MaxWords = 6, MaxExtensions = 10;

    private const int MaxReason = 200, FirstYear = 1980;

    // The folder names the model may give instead of a path.
    private static readonly Dictionary<string, Func<string>> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Desktop"] = () => Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
        ["Documents"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        // the usual place; a Downloads folder moved elsewhere needs SHGetKnownFolderPath.
        ["Downloads"] = () => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads"),
        ["Pictures"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
        ["Music"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        ["Videos"] = () => Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
    };

    private readonly List<FoundFile> _found = [];
    private readonly Dictionary<string, int> _numbers = new(StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyList<ToolDefinition> Definitions =
    [
        new(FindFiles,
            "Search file and folder names on this PC. Every word must be in the name or, where Everything runs, in a folder above it. Returns up to 50 matches, newest first, numbered, with folder, size and date modified.",
            JsonDocument.Parse("""
                {"type":"object","properties":{
                "words":{"type":"array","items":{"type":"string"},"description":"Words that must all match. Short stems match more: invoice, not invoices."},
                "folder":{"type":"string","description":"Only under this folder: a full path, or Desktop, Documents, Downloads, Pictures, Music or Videos."},
                "kind":{"type":"string","enum":["file","folder","any"]},
                "extensions":{"type":"array","items":{"type":"string"},"description":"Extensions without the dot, such as pdf."},
                "modified_from":{"type":"string","description":"First day modified, YYYY-MM-DD."},
                "modified_to":{"type":"string","description":"Last day modified, YYYY-MM-DD."}}}
                """).RootElement),
        new(ShowResults,
            "Show the user the files that fit, by their numbers from find_files, best first, with a plain reason under 60 characters. Ends the search.",
            JsonDocument.Parse("""
                {"type":"object","properties":{
                "numbers":{"type":"array","items":{"type":"integer"}},
                "reason":{"type":"string"}},"required":["numbers","reason"]}
                """).RootElement),
    ];

    /// <summary>find_files calls that ran a search.</summary>
    public int Calls { get; private set; }

    /// <summary>Files returned so far; numbered from 1 in this order.</summary>
    public IReadOnlyList<FoundFile> Found => _found;

    /// <summary>What show_results picked, among the files returned; null until it was called.</summary>
    public IReadOnlyList<FoundFile>? Picked { get; private set; }

    /// <summary>show_results' line on why, on one line.</summary>
    public string Reason { get; private set; } = "";

    /// <summary>The system prompt: what to do, the limits, and today's date for "last week" and the like.</summary>
    public static string Prompt(DateTimeOffset now) =>
        "You find files on the user's Windows PC from what they describe. Call find_files to search file and folder names; " +
        "you see names, folders, sizes and dates, never what is in a file. Search at most 3 times, then call show_results " +
        $"once with the numbers of the files that fit best, best first, at most {MaxPicks}, and a plain reason under 60 characters. " +
        "If none fit, call show_results with no numbers and say so. " +
        $"Today is {now.ToString("dddd d MMMM yyyy", CultureInfo.InvariantCulture)}.";

    /// <summary>Runs one call: its line for the steps, what goes back to the model, and whether that is an error.</summary>
    public (string Summary, string Result, bool IsError) Run(ToolCallPart call, CancellationToken ct)
    {
        switch (call.Name)
        {
            case FindFiles:
                if (Read(call.Input, out var filter) is { } bad)
                {
                    return (FindFiles, bad, true);
                }

                if (Calls >= MaxCalls)
                {
                    return ("Searched files: " + Describe(filter!), $"No searches left after {MaxCalls}. Call show_results now with what you found.", true);
                }

                Calls++;
                var hits = find(filter!, PerCall, ct);
                return ("Searched files: " + Describe(filter!), hits is null
                    ? "File search is not available on this PC: the Windows Search service is off and Everything is not running."
                    : List(hits), hits is null);
            case ShowResults:
                if (call.Input.ValueKind != JsonValueKind.Object || !call.Input.TryGetProperty("numbers", out var numbers) || numbers.ValueKind != JsonValueKind.Array)
                {
                    return (ShowResults, "show_results needs \"numbers\", an array of numbers from find_files.", true);
                }

                Picked = Pick(numbers);
                Reason = call.Input.TryGetProperty("reason", out var why) && why.ValueKind == JsonValueKind.String ? Line(why.GetString()!) : "";
                return ($"Showed {Picked.Count} file{(Picked.Count == 1 ? "" : "s")}", "Shown.", false);
            default:
                return (call.Name, $"There is no tool named {call.Name}.", true);
        }
    }

    /// <summary>
    /// find_files' arguments as a filter. Null when they can be searched; otherwise what to fix, for the model. Words are
    /// cleaned as typed words are; a folder is a full local or network path or one of the named folders; extensions are
    /// letters and digits; days are YYYY-MM-DD.
    /// </summary>
    public static string? Read(JsonElement input, out FileFilter? filter)
    {
        filter = null;
        if (input.ValueKind != JsonValueKind.Object)
        {
            return "Arguments must be a JSON object.";
        }

        var words = FileQuery.Words(string.Join(' ', Strings(input, "words"))).Take(MaxWords).ToList();
        string? folder = null;
        if (input.TryGetProperty("folder", out var f) && f.ValueKind == JsonValueKind.String && f.GetString()!.Trim() is { Length: > 0 } given)
        {
            folder = Folder(given);
            if (folder is null)
            {
                return "folder must be a path on a local drive, such as D:\\Work, or one of Desktop, Documents, Downloads, Pictures, Music, Videos.";
            }
        }

        bool? folders;
        switch (input.TryGetProperty("kind", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString()!.Trim().ToLowerInvariant() : null)
        {
            case null or "" or "any":
                folders = null;
                break;
            case "file" or "files":
                folders = false;
                break;
            case "folder" or "folders":
                folders = true;
                break;
            default:
                return "kind must be file, folder or any.";
        }

        var extensions = Strings(input, "extensions").SelectMany(e => e.Split([',', ';', ' '], StringSplitOptions.RemoveEmptyEntries))
            .Select(e => e.TrimStart('*', '.').ToLowerInvariant()).Distinct().ToList();
        if (extensions.Count > MaxExtensions || extensions.Any(e => e.Length is 0 or > 10 || !e.All(char.IsAsciiLetterOrDigit)))
        {
            return $"extensions must be at most {MaxExtensions} plain extensions without the dot, such as pdf.";
        }

        if (Day(input, "modified_from", out var from) is { } badFrom)
        {
            return badFrom;
        }

        if (Day(input, "modified_to", out var to) is { } badTo)
        {
            return badTo;
        }

        if (from > to)
        {
            return "modified_from is after modified_to.";
        }

        filter = new FileFilter(words, folder, extensions.Count > 0 ? false : folders, extensions, from, to, InPath: true);
        if (filter.Empty)
        {
            filter = null;
            return "Give at least one of words, folder, extensions, modified_from or modified_to.";
        }

        return null;
    }

    /// <summary>The files as the model reads them: under each folder, a line per file with its number, name, size and date.</summary>
    public string List(IReadOnlyList<FoundFile> hits)
    {
        if (hits.Count == 0)
        {
            return "No files found. Try other or fewer words, or fewer filters.";
        }

        var text = new StringBuilder(string.Create(CultureInfo.InvariantCulture,
            $"{hits.Count} found, newest first{(hits.Count >= PerCall ? $" (only the first {PerCall}; narrow the search for others)" : "")}:\n"));
        foreach (var folder in hits.GroupBy(h => Path.GetDirectoryName(h.Path.TrimEnd('\\')) ?? "", StringComparer.OrdinalIgnoreCase))
        {
            text.Append(folder.Key).Append('\n');
            foreach (var hit in folder)
            {
                if (!_numbers.TryGetValue(hit.Path, out int number))
                {
                    _found.Add(hit);
                    number = _numbers[hit.Path] = _found.Count;
                }

                string name = Path.GetFileName(hit.Path.TrimEnd('\\'));
                text.Append(CultureInfo.InvariantCulture, $"{number}. {(name.Length > 0 ? name : hit.Path)}, {(hit.Folder ? "folder" : Size(hit.Size))}, {hit.Modified?.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) ?? "date unknown"}\n");
            }
        }

        // Small local models answer in prose unless told what comes next.
        return text.Append("Call show_results with the numbers that fit, or search again.").ToString();
    }

    /// <summary>The local search: Everything when it runs, else the Windows index, each hit's size and date read from disk.</summary>
    // one stat per hit on the caller's thread; a hit on a share that went offline can take seconds.
    public static IReadOnlyList<FoundFile>? Local(FileFilter filter, int max, CancellationToken ct) =>
        FileSearch.Run(filter, max, ct).Hits?.Select(hit =>
        {
            try
            {
                FileSystemInfo info = hit.Folder ? new DirectoryInfo(hit.Path) : new FileInfo(hit.Path);
                return info.Exists ? new FoundFile(hit.Path, hit.Folder, info is FileInfo file ? file.Length : null, info.LastWriteTime) : new FoundFile(hit.Path, hit.Folder, null, null);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
            {
                return new FoundFile(hit.Path, hit.Folder, null, null);
            }
        }).ToList();

    private static string Describe(FileFilter filter) => string.Join(", ", new[]
    {
        filter.Words.Count > 0 ? string.Join(' ', filter.Words) : null,
        filter.Folder is { } folder ? "in " + folder : null,
        filter.Extensions is { Count: > 0 } e ? string.Join(' ', e) : filter.Folders is { } folders ? folders ? "folders" : "files" : null,
        filter.From is { } from ? "from " + from.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
        filter.To is { } to ? "to " + to.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
    }.OfType<string>());

    // The numbers given, as files returned in this search, each once, in the order given. A number given as text and a
    // path find_files returned count too; anything else is dropped.
    private List<FoundFile> Pick(JsonElement numbers)
    {
        var picked = new List<FoundFile>();
        foreach (var n in numbers.EnumerateArray())
        {
            int number = n.ValueKind == JsonValueKind.Number && n.TryGetInt32(out int i) ? i
                : n.ValueKind == JsonValueKind.String && int.TryParse(n.GetString(), NumberStyles.None, CultureInfo.InvariantCulture, out int s) ? s
                : n.ValueKind == JsonValueKind.String && _numbers.TryGetValue(n.GetString()!, out int p) ? p
                : 0;
            if (number >= 1 && number <= _found.Count && !picked.Contains(_found[number - 1]) && picked.Count < MaxPicks)
            {
                picked.Add(_found[number - 1]);
            }
        }

        return picked;
    }

    // A named folder's path, or a path from a drive letter on a fixed drive (a drive letter alone is its root). Never a
    // share, a device or a \\?\ path: the model's words do not get to point the search off this PC's own disks. Null for
    // anything else.
    private static string? Folder(string given)
    {
        if (Named.TryGetValue(given, out var named))
        {
            return named() is { Length: > 0 } path ? path : null;
        }

        given = given.Length == 2 ? given + "\\" : given;
        if (given.Length < 3 || !char.IsAsciiLetter(given[0]) || given[1] != ':' || given[2] is not ('\\' or '/') || given.Any(c => c == '"' || char.IsControl(c)))
        {
            return null;
        }

        try
        {
            string full = Path.GetFullPath(given);
            return full.StartsWith(given[..2], StringComparison.OrdinalIgnoreCase) && new DriveInfo(full[..1]).DriveType == DriveType.Fixed ? full : null;
        }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException or IOException)
        {
            return null;
        }
    }

    // A string or an array of strings. A small local model sometimes sends the array as JSON inside a string.
    private static IEnumerable<string> Strings(JsonElement input, string name)
    {
        if (!input.TryGetProperty(name, out var value))
        {
            return [];
        }

        if (value.ValueKind == JsonValueKind.String && value.GetString()!.TrimStart().StartsWith('['))
        {
            try
            {
                using var inner = JsonDocument.Parse(value.GetString()!);
                value = inner.RootElement.Clone();
            }
            catch (JsonException)
            {
                // Plain text that starts with a bracket: taken as it is.
            }
        }

        return value.ValueKind == JsonValueKind.String ? [value.GetString()!]
            : value.ValueKind == JsonValueKind.Array ? [.. value.EnumerateArray().Where(v => v.ValueKind == JsonValueKind.String).Select(v => v.GetString()!)]
            : [];
    }

    private static string? Day(JsonElement input, string name, out DateOnly? day)
    {
        day = null;
        if (!input.TryGetProperty(name, out var value) || value.ValueKind == JsonValueKind.Null || (value.ValueKind == JsonValueKind.String && value.GetString()!.Length == 0))
        {
            return null;
        }

        // Years outside 1980 to next year are no file's date, and the day after 9999-12-31 cannot be asked.
        if (value.ValueKind == JsonValueKind.String && DateOnly.TryParseExact(value.GetString(), "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            && parsed.Year >= FirstYear && parsed.Year <= DateTime.Today.Year + 1)
        {
            day = parsed;
            return null;
        }

        return $"{name} must be a day as YYYY-MM-DD, from {FirstYear} to next year.";
    }

    private static string Size(long? bytes) => bytes switch
    {
        null => "size unknown",
        < 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes} B"),
        < 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1024.0:0} KB"),
        < 1024L * 1024 * 1024 => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1048576.0:0.#} MB"),
        _ => string.Create(CultureInfo.InvariantCulture, $"{bytes / 1073741824.0:0.#} GB"),
    };

    private static string Line(string text)
    {
        string line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= MaxReason ? line : line[..(MaxReason - 1)].TrimEnd() + "…";
    }
}
