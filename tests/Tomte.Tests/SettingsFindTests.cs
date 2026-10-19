using Tomte.Core.Search;
using Xunit;

namespace Tomte.Tests;

public sealed class SettingsFindTests
{
    // Rows as the settings window shows them, in its order (a few per page).
    private static readonly SettingRow[] Rows =
    [
        new("Opening and closing", "", "Dwell", "Resting in the reveal zone this many milliseconds also reveals it. 0 turns it off."),
        new("Opening and closing", "Advanced", "Retract margin", "How far outside the island the cursor can go before it starts to retract, in pixels at 100% scale."),
        new("Opening and closing", "Advanced", "Retract delay", "Milliseconds outside that margin before the island retracts."),
        new("Opening and closing", "General", "Start with Windows", "Open Tomte when you sign in. It stays hidden until you call it."),
        new("Opening and closing", "General", "Desktop peek", "Show the tomte at the top of the screen when no window is there. Point at it to open the island."),
        new("Opening and closing", "General", "Island position", "Drag the island along the top edge to move it."),
        new("Opening and closing", "Appearance", "Theme", "Follow the taskbar's light or dark look, or keep the island dark or light."),
        new("Opening and closing", "Appearance", "Accent colour", "The colour of the selection, the AI and the recording dot, on the island and here. On an accent-coloured taskbar the island takes the Windows accent."),
        new("Hotkeys", "", "Summon island", ""),
        new("Hotkeys", "", "Scratchpad", ""),
        new("Hotkeys", "", "Search", ""),
        new("Search", "", "Search shortcut", "Win+Alt+Space opens the search box."),
        new("Search", "", "File search", "The Windows Search index: only the folders Windows indexes. Get Everything (free, from voidtools) to search every drive."),
        new("Search", "", "Web search", "What Open in browser in the search box searches with: Google, Bing or DuckDuckGo."),
        new("Clipboard", "", "Keep history", "Keeps the last 50 pieces of text and 10 images you copy, on this PC, for up to 7 days."),
        new("Provider and spend", "", "Key", "Kept in Windows Credential Manager, not in the settings file."),
        new("Provider and spend", "", "Daily spend cap", "Estimated from token counts and list prices. A server on this PC is free."),
        new("Provider and spend", "AI history", "Undo after closing", "After you close an AI answer, Undo on the island brings it back for this long."),
        new("Provider and spend", "AI history", "Keep history", "Keeps your last 100 AI conversations, on this PC. Nothing leaves this PC."),
        new("Provider and spend", "AI history", "Clear history", ""),
        new("Tools and permissions", "All tools", "copy_to_clipboard", "Copies text to the clipboard.", false),
        new("About", "", "Version", "", false),
        new("About", "", "License", "Tomte is free and open source.", false),
        new("About", "", "Third-party notices", "Copyright and license text for the libraries that come with Tomte."),
        new("About", "Support", "Donate", "Tomte is free, and support keeps it going."),
        new("About", "Support", "UPI ID", "botzhub@ybl"),
        new("About", "Support", "USDT on BNB Smart Chain (BEP20)", "0xc8cbfa55be28b0836a4ab830ea85c64516dffaf3"),
        new("About", "Support", "USDT on Tron (TRC20)", "THDKzV4ZKeDBsrFRSbL5XZgqVQUEkPfWcW"),
    ];

    private static List<string> Find(string query) => [.. SettingsFind.Rank(Rows, query).Select(i => $"{Rows[i].Title}, {Rows[i].Page}")];

    [Fact]
    public void ATitleMatchComesBeforeAnExplanationMatch()
    {
        // "island" is the title of Island position and only the explanation of Desktop peek and the retract rows.
        var found = Find("island");
        Assert.Equal(["Island position, Opening and closing", "Summon island, Hotkeys"], found.Take(2));
        Assert.Contains("Desktop peek, Opening and closing", found.Skip(2));
        Assert.Equal("Undo after closing, Provider and spend", Find("undo")[0]);
    }

    [Theory]
    [InlineData("theme", "Theme")]
    [InlineData("light", "Theme")]
    [InlineData("dark", "Theme")]
    public void TheIslandsLookIsFoundByTheWordsPeopleUse(string query, string title) =>
        Assert.Equal($"{title}, Opening and closing", Find(query)[0]);

    [Fact]
    public void OneTypoInALongWordAndInitialsStillFind()
    {
        Assert.Equal("Keep history, Clipboard", Find("histroy")[0]);
        // A typo in a tool's name is a title match, but a tool has nothing to change: the setting comes first.
        Assert.Equal(["Keep history, Clipboard", "copy_to_clipboard, Tools and permissions"], Find("clipbaord"));
        Assert.Equal(["Start with Windows, Opening and closing"], Find("sww"));
        Assert.Equal(["Keep history, Clipboard", "Keep history, Provider and spend"], Find("kh").Take(2));
        Assert.Empty(Find("srch2"));
    }

    [Fact]
    public void PrivacyAndMemoryFindTheHistoryRows()
    {
        foreach (string query in new[] { "privacy", "memory" })
        {
            var found = Find(query);
            Assert.Equal("Keep history, Clipboard", found[0]);
            Assert.Contains("Keep history, Provider and spend", found);
            Assert.Contains("Clear history, Provider and spend", found);
            Assert.DoesNotContain(found, row => row.StartsWith("Desktop peek", StringComparison.Ordinal) || row.StartsWith("Start with Windows", StringComparison.Ordinal));
        }
    }

    [Fact]
    public void APageOrItsWordsFindItsRowsInTheWindowsOrder()
    {
        Assert.Equal(["Summon island, Hotkeys", "Scratchpad, Hotkeys", "Search, Hotkeys"], Find("hotkey"));
        Assert.Equal("File search, Search", Find("everything")[0]);
        Assert.Equal(["Web search, Search"], Find("bing"));
        // "startup" is only in the words for the General section.
        Assert.Equal("Start with Windows, Opening and closing", Find("startup")[0]);
        // The words for Opening and closing name its retract rows, but rows with the word themselves leave the rest out.
        Assert.Equal(["Retract delay, Opening and closing", "Retract margin, Opening and closing"], Find("retract"));
    }

    [Fact]
    public void TheSpendCapAndTheAccentAreFoundOnTheirPages()
    {
        Assert.Equal("Daily spend cap, Provider and spend", Find("spend")[0]);
        Assert.Equal("Accent colour, Opening and closing", Find("colour")[0]);
        // "theme" is a row's title now, which beats the words for the Appearance section.
        Assert.Equal(["Theme, Opening and closing"], Find("theme"));
    }

    [Fact]
    public void TheAboutPageAndItsRowsAreFoundByTheirWords()
    {
        Assert.Equal(["Version, About"], Find("version"));
        // Rows with something to click come before rows of text, so the notices come first.
        Assert.Equal(["Third-party notices, About", "License, About"], Find("license"));
        Assert.Equal(["Donate, About"], Find("donate"));
        Assert.Equal(["Donate, About", "UPI ID, About", "USDT on BNB Smart Chain (BEP20), About", "USDT on Tron (TRC20), About"], Find("support"));
        Assert.Equal(["USDT on Tron (TRC20), About"], Find("tron"));
        // "sponsor" is only in the words for the page, so it lists the page's rows.
        foreach (string query in new[] { "about", "sponsor" })
        {
            Assert.Equal(7, Find(query).Count);
            Assert.All(Find(query), row => Assert.EndsWith(", About", row, StringComparison.Ordinal));
        }
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
