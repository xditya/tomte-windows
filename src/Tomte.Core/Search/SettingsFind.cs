namespace Tomte.Core.Search;

/// <summary>
/// One row of the settings window as its Find a setting box sees it: the page it is on, the section header above it (""
/// for none), its title and its explanation ("" for none).
/// </summary>
public sealed record SettingRow(string Page, string Section, string Title, string Note);

/// <summary>
/// Ranks the settings window's rows for the text typed in its Find a setting box, with the search box's matcher. Pure, so
/// it runs on every keystroke and is tested without a window.
/// </summary>
public static class SettingsFind
{
    /// <summary>The most rows listed for one query.</summary>
    public const int MaxResults = 10;

    // A match only in the words people type for the row's page or section (the search box's words for Tomte's places).
    private const int ByWords = 1000;

    /// <summary>
    /// The indexes of the rows that match <paramref name="query"/>, best first, at most <see cref="MaxResults"/>. A match
    /// in the title (word starts, initials, letters in order, one typo in a word of five letters or more) beats one that
    /// needs the section or page, which beats one in the explanation. Inside the title tier the matcher's order holds;
    /// inside the others the window's order does. The words people type for a page or section ("privacy" for Clipboard)
    /// count only when no row matches otherwise, so "retract" lists the Retract rows and not every row on their page.
    /// </summary>
    public static IReadOnlyList<int> Rank(IReadOnlyList<SettingRow> rows, string query)
    {
        var scored = rows.Select((row, i) => (i, Score: Score(row, query))).Where(r => r.Score > 0).ToList();
        if (scored.Any(r => r.Score > ByWords))
        {
            scored.RemoveAll(r => r.Score == ByWords);
        }

        return [.. scored.OrderByDescending(r => r.Score).ThenBy(r => r.i).Take(MaxResults).Select(r => r.i)];
    }

    private static int Score(SettingRow row, string query)
    {
        if (SearchMatch.Score(row.Title, query) is > 0 and var title)
        {
            return 3000 + title;
        }

        string where = $"{row.Title} {row.Section} {row.Page}";
        // The explanation is a sentence or two, and the page's words a long list: only the starts of their words count,
        // since letters in order and typos over that much text would match nearly anything.
        int[] tiers =
        [
            SearchMatch.Score(where, query),
            SearchMatch.Score($"{where} {row.Note}", query, wordStarts: true),
            SearchMatch.Score($"{where} {row.Note} {Places.TomteWords(row.Page)} {Places.TomteWords(row.Section)}", query, wordStarts: true),
        ];
        int tier = Array.FindIndex(tiers, s => s > 0);
        return tier < 0 ? 0 : ByWords * (3 - tier);
    }
}
