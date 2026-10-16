using Tomte.Core.Search;
using Xunit;

namespace Tomte.Tests;

public sealed class SettingsFindTests
{
    // Rows as the settings window shows them, in its order (a few per page).
    private static readonly SettingRow[] Rows =
    [
        new("General", "", "Start with Windows", "Open Tomte when you sign in. It stays hidden until you call it."),
        new("General", "", "Desktop peek", "Show the tomte at the top of the screen when no window is there. Point at it to open the island."),
        new("General", "", "Island position", "Drag the island along the top edge to move it."),
        new("Opening and closing", "", "Dwell", "Resting in the reveal zone this many milliseconds also reveals it. 0 turns it off."),
        new("Opening and closing", "Advanced", "Retract margin", "How far outside the island the cursor can go before it starts to retract, in pixels at 100% scale."),
        new("Opening and closing", "Advanced", "Retract delay", "Milliseconds outside that margin before the island retracts."),
        new("Hotkeys", "", "Summon island", ""),
        new("Hotkeys", "", "Scratchpad", ""),
        new("Hotkeys", "", "Search", ""),
        new("Search", "", "Search shortcut", "Win+Alt+Space opens the search box."),
        new("Search", "", "File search", "The Windows Search index: only the folders Windows indexes. Get Everything (free, from voidtools) to search every drive."),
        new("Search", "", "Web search", "What Open in browser in the search box searches with: Google, Bing or DuckDuckGo."),
        new("Clipboard", "", "Keep history", "Keeps the last 50 pieces of text and 10 images you copy, on this PC, for up to 7 days."),
        new("AI", "AI provider", "Key", "Kept in Windows Credential Manager, not in the settings file."),
        new("AI", "AI history", "Undo after closing", "After you close an AI answer, Undo on the island brings it back for this long."),
        new("AI", "AI history", "Keep history", "Keeps your last 100 AI conversations, on this PC. Nothing leaves this PC."),
        new("AI", "AI history", "Clear history", ""),
    ];

    private static List<string> Find(string query) => [.. SettingsFind.Rank(Rows, query).Select(i => $"{Rows[i].Title}, {Rows[i].Page}")];

    [Fact]
    public void ATitleMatchComesBeforeAnExplanationMatch()
    {
        // "island" is the title of Island position and only the explanation of Desktop peek and the retract rows.
        var found = Find("island");
        Assert.Equal(["Island position, General", "Summon island, Hotkeys"], found.Take(2));
        Assert.Contains("Desktop peek, General", found.Skip(2));
        Assert.Equal("Undo after closing, AI", Find("undo")[0]);
    }

    [Fact]
    public void OneTypoInALongWordAndInitialsStillFind()
    {
        Assert.Equal("Keep history, Clipboard", Find("histroy")[0]);
        Assert.Equal(["Keep history, Clipboard"], Find("clipbaord"));
        Assert.Equal(["Start with Windows, General"], Find("sww"));
        Assert.Equal(["Keep history, Clipboard", "Keep history, AI"], Find("kh").Take(2));
        Assert.Empty(Find("srch2"));
    }

    [Fact]
    public void PrivacyAndMemoryFindTheHistoryRows()
    {
        foreach (string query in new[] { "privacy", "memory" })
        {
            var found = Find(query);
            Assert.Equal("Keep history, Clipboard", found[0]);
            Assert.Contains("Keep history, AI", found);
            Assert.Contains("Clear history, AI", found);
            Assert.DoesNotContain(found, row => row.EndsWith("General", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void APageOrItsWordsFindItsRowsInTheWindowsOrder()
    {
        Assert.Equal(["Summon island, Hotkeys", "Scratchpad, Hotkeys", "Search, Hotkeys"], Find("hotkey"));
        Assert.Equal("File search, Search", Find("everything")[0]);
        Assert.Equal(["Web search, Search"], Find("bing"));
        // "startup" is only in the words for the General page.
        Assert.Equal("Start with Windows, General", Find("startup")[0]);
        // The words for Opening and closing name its retract rows, but rows with the word themselves leave the rest out.
        Assert.Equal(["Retract delay, Opening and closing", "Retract margin, Opening and closing"], Find("retract"));
    }

    [Fact]
    public void NothingTypedOrNothingMatchingListsNothing()
    {
        Assert.Empty(Find(""));
        Assert.Empty(Find("   "));
        Assert.Empty(Find("xyzzy"));
    }

    [Fact]
    public void ListsTenAtMost()
    {
        var many = Enumerable.Range(0, 30).Select(i => new SettingRow("Page", "", $"Setting {i}", "")).ToList();
        Assert.Equal(SettingsFind.MaxResults, SettingsFind.Rank(many, "setting").Count);
    }
}
