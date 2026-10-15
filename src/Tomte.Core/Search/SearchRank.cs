namespace Tomte.Core.Search;

/// <summary>
/// What a search row is. Rows are grouped in this order: apps, then tiles and shelf items, then files and folders, then
/// AI search, then Ask.
/// </summary>
public enum SearchKind
{
    App,
    Shortcut,
    Shelf,
    Folder,
    File,

    /// <summary>The row that offers to get Everything, last among the files.</summary>
    GetEverything,

    /// <summary>The row that asks the AI provider to pick files for the typed words; a group of its own, so Tab reaches it.</summary>
    AiSearch,

    /// <summary>The row that hands the typed words to the AI box.</summary>
    Ask,
}

/// <summary>
/// One row. <paramref name="Target"/> is what opening it uses: a Start app's shell:AppsFolder path, a tile's id, or a
/// path. <paramref name="Detail"/> is the dim text after the name (a file's folder). <paramref name="Icon"/> is the shell
/// item its icon is read from, null for none. <paramref name="Location"/> is the file or folder Ctrl+Enter shows in its
/// folder, null when the row has none.
/// </summary>
public sealed record SearchItem(SearchKind Kind, string Name, string Target, string Detail = "", string? Icon = null, string? Location = null)
{
    /// <summary>The group the row is listed in, 0 first.</summary>
    public int Group => Kind switch
    {
        SearchKind.App => 0,
        SearchKind.Shortcut or SearchKind.Shelf => 1,
        SearchKind.Folder or SearchKind.File or SearchKind.GetEverything => 2,
        SearchKind.AiSearch => 3,
        _ => 4,
    };

    /// <summary>What the launch counts are kept under: the kind and the target, case ignored, as Windows treats paths.</summary>
    public string Key => $"{Kind}:{Target.ToLowerInvariant()}";
}

/// <summary>How often and when a row was last opened from search.</summary>
public readonly record struct Launches(int Count, long LastTicks);

/// <summary>
/// How well a name matches what was typed, in tiers: the whole name, its start, the start of each typed word in any
/// order, its initials or the starts of its words run together ("vsc", "vscode"), each word somewhere in it, its letters
/// in order and close together, and one typo in a word of five letters or more.
/// A tier is worth 100; inside it shorter names and earlier matches score up to 59 more, which leaves room for the
/// launch boost without reaching the next tier. 0 is no match.
/// </summary>
public static class SearchMatch
{
    public const int Exact = 700, Prefix = 600, WordStarts = 500, Initials = 400, Substring = 300, Subsequence = 200, Typo = 100;

    /// <summary>
    /// Typed words shorter than this must match exactly; one this long may have one letter wrong, missing, extra or swapped.
    /// Five, not four: at four one wrong letter is a quarter of the word, and "repo" found Remote Mouse.
    /// </summary>
    public const int TypoLength = 5;

    public static int Score(string name, string query)
    {
        string q = Collapse(query);
        if (q.Length == 0)
        {
            return 0;
        }

        string n = Collapse(name);
        var nameWords = Words(name);
        var typed = q.Split(' ');
        string qJoined = q.Replace(" ", "", StringComparison.Ordinal);
        int extra = Math.Clamp(40 - (n.Length - q.Length), 0, 40);
        if (n == q)
        {
            return Exact + 50;
        }

        if (n.StartsWith(q, StringComparison.Ordinal))
        {
            return Prefix + extra;
        }

        if (Assign(typed, nameWords, (w, word) => word.StartsWith(w, StringComparison.Ordinal)))
        {
            return WordStarts + extra;
        }

        if (qJoined.Length >= 2 && Pieces(qJoined, 0, nameWords, 0))
        {
            return Initials + extra;
        }

        if (typed.All(w => n.Contains(w, StringComparison.Ordinal)))
        {
            return Substring + Math.Max(0, 20 - n.IndexOf(typed[0], StringComparison.Ordinal)) + (extra / 2);
        }

        // Letters in order count only when they sit close together: "chrm" is Chrome, but "report" is not Mixed Reality
        // Portal, whose r, e, p, o, r and t are spread over two words.
        if (qJoined.Length >= 2 && InOrder(string.Concat(nameWords), qJoined, WordStartsOf(nameWords)) is var (gaps, starts) && gaps <= Math.Max(1, qJoined.Length / 2))
        {
            return Subsequence + Math.Clamp(40 - (4 * gaps) + (5 * starts), 0, 49) + (extra / 4);
        }

        bool typo = typed.Any(w => w.Length >= TypoLength)
            && Assign(typed, nameWords, (w, word) => w.Length >= TypoLength ? NearPrefix(w, word) : word.StartsWith(w, StringComparison.Ordinal));
        return typo ? Typo + (extra / 4) : 0;
    }

    /// <summary>
    /// The words of a name, lower case: split at anything that is not a letter or digit, where a small letter meets a
    /// capital ("PowerShell") and where letters meet digits ("Win11").
    /// </summary>
    public static IReadOnlyList<string> Words(string name)
    {
        var words = new List<string>();
        int start = -1;
        for (int i = 0; i <= name.Length; i++)
        {
            bool part = i < name.Length && char.IsLetterOrDigit(name[i]);
            bool split = !part || (start >= 0 && i > start
                && ((char.IsLower(name[i - 1]) && char.IsUpper(name[i])) || (char.IsDigit(name[i - 1]) != char.IsDigit(name[i]))));
            if (split && start >= 0)
            {
                words.Add(name[start..i].ToLowerInvariant());
                start = -1;
            }

            if (part && start < 0)
            {
                start = i;
            }
        }

        return words;
    }

    // Lower case, with every run of characters that are not letters or digits turned into one space.
    private static string Collapse(string text)
    {
        var collapsed = new System.Text.StringBuilder(text.Length);
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c))
            {
                collapsed.Append(char.ToLowerInvariant(c));
            }
            else if (collapsed.Length > 0 && collapsed[^1] != ' ')
            {
                collapsed.Append(' ');
            }
        }

        return collapsed.ToString().TrimEnd();
    }

    // Each typed word matched to a different name word, in any order; tries the typed words longest first, which is
    // enough for the few words a search has.
    private static bool Assign(string[] typed, IReadOnlyList<string> words, Func<string, string, bool> fits)
    {
        var used = new bool[words.Count];
        foreach (string w in typed.OrderByDescending(w => w.Length))
        {
            int at = -1;
            for (int i = 0; i < words.Count && at < 0; i++)
            {
                if (!used[i] && fits(w, words[i]))
                {
                    at = i;
                }
            }

            if (at < 0)
            {
                return false;
            }

            used[at] = true;
        }

        return true;
    }

    private static HashSet<int> WordStartsOf(IReadOnlyList<string> words)
    {
        var starts = new HashSet<int>();
        int at = 0;
        foreach (string word in words)
        {
            starts.Add(at);
            at += word.Length;
        }

        return starts;
    }

    // The typed text as the starts of the name's words in order, from its first word, later words skipped allowed: "vsc"
    // and "vscode" are Visual Studio Code ("v" + "s" + "c", "v" + "s" + "code"), "gchrome" is Google Chrome. Each
    // (letter, word) pair that led nowhere is remembered, so a long name of many short words costs letters times words,
    // not every way to split it.
    private static bool Pieces(string typed, int at, IReadOnlyList<string> words, int word, HashSet<(int, int)>? failed = null)
    {
        if (at == typed.Length)
        {
            return true;
        }

        failed ??= [];
        if (failed.Contains((at, word)))
        {
            return false;
        }

        // The first piece starts the name ("re" + "port" is not Mixed Reality Portal); later ones may skip words.
        for (int w = word; w < words.Count && (at > 0 || w == word); w++)
        {
            for (int take = Math.Min(words[w].Length, typed.Length - at); take >= 1; take--)
            {
                if (string.CompareOrdinal(typed, at, words[w], 0, take) == 0 && Pieces(typed, at + take, words, w + 1, failed))
                {
                    return true;
                }
            }
        }

        failed.Add((at, word));
        return false;
    }

    // The typed letters in order in the name's letters, in the tightest place they fit (each start tried, then the first
    // match of each letter after it): how many letters lie between the first and the last one found that were not typed,
    // and how many found letters start a word. Null when they are not all there.
    private static (int Gaps, int Starts)? InOrder(string letters, string typed, HashSet<int> starts)
    {
        (int Gaps, int Starts)? best = null;
        for (int first = letters.IndexOf(typed[0], StringComparison.Ordinal); first >= 0; first = letters.IndexOf(typed[0], first + 1))
        {
            int found = 1, last = first, onStarts = starts.Contains(first) ? 1 : 0;
            for (int i = first + 1; i < letters.Length && found < typed.Length; i++)
            {
                if (letters[i] == typed[found])
                {
                    last = i;
                    onStarts += starts.Contains(i) ? 1 : 0;
                    found++;
                }
            }

            if (found < typed.Length)
            {
                break;
            }

            int gaps = last - first + 1 - typed.Length;
            if (best is not { } b || gaps < b.Gaps || (gaps == b.Gaps && onStarts > b.Starts))
            {
                best = (gaps, onStarts);
            }
        }

        return best;
    }

    // One edit or one swap away from the start of the word, a letter shorter, as long or a letter longer.
    private static bool NearPrefix(string typed, string word)
    {
        for (int length = typed.Length - 1; length <= typed.Length + 1; length++)
        {
            if (length <= word.Length && length > 0 && Distance(typed, word[..length]) <= 1)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Edits between two words, a swap of neighbours counting as one (optimal string alignment).</summary>
    public static int Distance(string a, string b)
    {
        var d = new int[a.Length + 1, b.Length + 1];
        for (int i = 0; i <= a.Length; i++)
        {
            d[i, 0] = i;
        }

        for (int j = 0; j <= b.Length; j++)
        {
            d[0, j] = j;
        }

        for (int i = 1; i <= a.Length; i++)
        {
            for (int j = 1; j <= b.Length; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1), d[i - 1, j - 1] + cost);
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                {
                    d[i, j] = Math.Min(d[i, j], d[i - 2, j - 2] + 1);
                }
            }
        }

        return d[a.Length, b.Length];
    }
}

/// <summary>
/// The rows a search shows, in order: apps first whenever any app matches, then tiles and shelf items, then files and
/// folders, then Ask, each group capped. Inside a group the better match comes first; a row opened from search before
/// gains a little, enough to lead its tier, never enough to pass a better tier. Pure, so it runs on every keystroke.
/// </summary>
public static class SearchRank
{
    public const int MaxApps = 5, MaxPlaces = 3, MaxFiles = 8;

    /// <summary>
    /// The candidates that match <paramref name="query"/>, grouped, ordered and capped. Files and folders came from a file
    /// search that already matched every typed word, so one this matcher does not follow (accents, say) still shows, last.
    /// The offer, AI search and Ask rows are kept as they are, at the end of their groups.
    /// </summary>
    public static IReadOnlyList<SearchItem> Order(IEnumerable<SearchItem> candidates, string query, IReadOnlyDictionary<string, Launches> launches) =>
        Grouped(candidates.Select(item => (Item: item, Score: item.Kind switch
        {
            SearchKind.GetEverything or SearchKind.AiSearch or SearchKind.Ask => -1,
            SearchKind.File or SearchKind.Folder => Math.Max(1, SearchMatch.Score(item.Name, query)),
            _ => SearchMatch.Score(item.Name, query),
        }))
            .Where(scored => scored.Score != 0)
            .Select(scored => (scored.Item, Rank: scored.Score < 0 ? int.MinValue : scored.Score + Boost(launches, scored.Item))));

    /// <summary>Typed text of this many words or more reads as a sentence, which AI search may answer better than names do.</summary>
    public const int SentenceWords = 3;

    /// <summary>
    /// The rows with the AI search row moved first, so it is the one selected, when the text reads as a sentence and
    /// nothing local matches it well: no file or folder (a file search found every word in their names) and no app, tile
    /// or shelf item with every word in its name. Otherwise the rows as they are.
    /// </summary>
    public static IReadOnlyList<SearchItem> AiFirst(IReadOnlyList<SearchItem> ranked, string query)
    {
        int ai = ranked.ToList().FindIndex(r => r.Kind == SearchKind.AiSearch);
        bool matched = ranked.Any(r => r.Kind is SearchKind.File or SearchKind.Folder
            || (r.Kind is SearchKind.App or SearchKind.Shortcut or SearchKind.Shelf && SearchMatch.Score(r.Name, query) >= SearchMatch.Substring));
        return ai <= 0 || matched || FileQuery.Words(query).Count < SentenceWords ? ranked : [ranked[ai], .. ranked.Where((_, i) => i != ai)];
    }

    /// <summary>For an empty box: what was opened from search before, most often first, then most recently, grouped and capped.</summary>
    public static IReadOnlyList<SearchItem> Recent(IEnumerable<SearchItem> opened, IReadOnlyDictionary<string, Launches> launches)
    {
        var seen = opened.Where(item => launches.ContainsKey(item.Key))
            .OrderByDescending(item => launches[item.Key].Count)
            .ThenByDescending(item => launches[item.Key].LastTicks)
            .ToList();
        return Grouped(seen.Select((item, i) => (item, seen.Count - i)));
    }

    /// <summary>Up to 40 points for rows opened from search before: 10 a time, from the fourth time on the same.</summary>
    public static int Boost(IReadOnlyDictionary<string, Launches> launches, SearchItem item) =>
        launches.TryGetValue(item.Key, out var seen) ? 10 * Math.Min(4, seen.Count) : 0;

    private static IReadOnlyList<SearchItem> Grouped(IEnumerable<(SearchItem Item, int Rank)> ranked) =>
        [.. ranked
            .GroupBy(r => r.Item.Group)
            .OrderBy(g => g.Key)
            .SelectMany(g => Cap(g.Key, g
                .OrderByDescending(r => r.Rank)
                .ThenBy(r => r.Item.Name.Length)
                .ThenBy(r => r.Item.Name, StringComparer.CurrentCultureIgnoreCase)
                .Select(r => r.Item)))];

    // The fixed rows (the offer, Ask) are not counted against a group's cap.
    private static IEnumerable<SearchItem> Cap(int group, IEnumerable<SearchItem> items)
    {
        int cap = group switch { 0 => MaxApps, 1 => MaxPlaces, _ => MaxFiles };
        int shown = 0;
        foreach (var item in items)
        {
            bool fixedRow = item.Kind is SearchKind.GetEverything or SearchKind.AiSearch or SearchKind.Ask;
            if (fixedRow || shown++ < cap)
            {
                yield return item;
            }
        }
    }
}
