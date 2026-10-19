using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Search;
using Xunit;

namespace Tomte.Tests;

public sealed class SearchTests
{
    private static readonly Dictionary<string, Launches> None = [];

    private static readonly string[] AppNames =
    [
        "Visual Studio Code", "Visual Studio 2022", "VLC media player", "Google Chrome", "Chrome Remote Desktop", "Character Map",
        "Terminal", "Windows Terminal", "Spotify", "Calculator", "Settings", "Snipping Tool", "PowerShell", "Microsoft Edge",
        "Steam", "Paint", "Notepad", "OBS Studio", "File Explorer", "7-Zip File Manager",
    ];

    private static SearchItem App(string name) => new(SearchKind.App, name, @"shell:AppsFolder\" + name.Replace(" ", "", StringComparison.Ordinal));

    private static SearchItem File(string name) => new(SearchKind.File, name, @"D:\Work\" + name, @"D:\Work", Location: @"D:\Work\" + name);

    private static IReadOnlyList<SearchItem> Apps => [.. AppNames.Select(App)];

    private static string First(string query, IReadOnlyDictionary<string, Launches>? launches = null) =>
        SearchRank.Order(Apps, query, launches ?? None)[0].Name;

    [Theory]
    [InlineData("term", "Terminal")]
    [InlineData("vsc", "Visual Studio Code")]
    [InlineData("chrm", "Google Chrome")]
    [InlineData("code studio", "Visual Studio Code")]
    [InlineData("crhome", "Google Chrome")]
    [InlineData("spotfy", "Spotify")]
    [InlineData("calcu", "Calculator")]
    [InlineData("powersh", "PowerShell")]
    [InlineData("shell", "PowerShell")]
    [InlineData("obs", "OBS Studio")]
    [InlineData("zip", "7-Zip File Manager")]
    [InlineData("Settings", "Settings")]
    public void TheAppAUserMeansComesFirst(string query, string expected) => Assert.Equal(expected, First(query));

    [Theory]
    [InlineData("Terminal", "term", SearchMatch.Prefix)]
    [InlineData("Windows Terminal", "term", SearchMatch.WordStarts)]
    [InlineData("Visual Studio Code", "vsc", SearchMatch.Initials)]
    [InlineData("Visual Studio Code", "code studio", SearchMatch.WordStarts)]
    [InlineData("Snipping Tool", "ppin", SearchMatch.Substring)]
    [InlineData("Google Chrome", "chrm", SearchMatch.Subsequence)]
    [InlineData("Google Chrome", "crhome", SearchMatch.Typo)]
    [InlineData("Spotify", "spotfy", SearchMatch.Subsequence)] // a missing letter is still the letters in order
    [InlineData("Spotify", "sptoify", SearchMatch.Typo)]
    [InlineData("Google Chrome", "crhom", SearchMatch.Typo)] // five letters, one swap
    [InlineData("Remote Mouse", "rmeote", SearchMatch.Typo)]
    [InlineData("Spotify", "spotifyy", SearchMatch.Typo)]
    [InlineData("Calculator", "calculator", SearchMatch.Exact)]
    public void EachKindOfMatchLandsInItsTier(string name, string query, int tier)
    {
        int score = SearchMatch.Score(name, query);
        Assert.InRange(score, tier, tier + 99);
    }

    [Theory]
    [InlineData("Visual Studio Code", "vscode", SearchMatch.Initials)]
    [InlineData("Google Chrome", "gchrome", SearchMatch.Initials)]
    public void TheStartsOfWordsInOrderMatchAsInitialsDo(string name, string query, int tier) =>
        Assert.InRange(SearchMatch.Score(name, query), tier, tier + 99);

    [Fact]
    public void ALongNameOfShortWordsIsQuick()
    {
        string name = string.Join(' ', Enumerable.Repeat("a", 60));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        Assert.Equal(0, SearchMatch.Score(name, new string('a', 40) + "b"));
        Assert.True(watch.ElapsedMilliseconds < 200, $"{watch.ElapsedMilliseconds} ms");
    }

    [Theory]
    [InlineData("Google Chrome", "crh")] // a typo needs five letters
    [InlineData("Remote Mouse", "repo")] // four letters: no typo
    [InlineData("Recovery Drive", "repo")]
    [InlineData("Google Chrome", "crho")]
    [InlineData("Mixed Reality Portal", "report")] // letters in order, but spread over two words
    [InlineData("Visit Java.com", "vsc")]
    [InlineData("Windows Defender Firewall with Advanced Security", "vsc")]
    [InlineData("Google Chrome", "xyz")]
    [InlineData("Spotify", "sptofyx")] // two edits
    [InlineData("Terminal", "")]
    [InlineData("Terminal", "  ")]
    public void NoMatchScoresZero(string name, string query) => Assert.Equal(0, SearchMatch.Score(name, query));

    [Fact]
    public void ABetterTierAlwaysBeatsAWorseOne()
    {
        // The longest name with the better tier still beats the shortest with the worse one.
        Assert.True(SearchMatch.Score("Terminal emulator for very long names indeed", "term") > SearchMatch.Score("A term", "term"));
        Assert.True(SearchMatch.Score("Visual Studio Code", "vsc") > SearchMatch.Score("vxsxc", "vsc"));
    }

    [Theory]
    [InlineData("PowerShell", new[] { "power", "shell" })]
    [InlineData("7-Zip File Manager", new[] { "7", "zip", "file", "manager" })]
    [InlineData("Win11Debloat", new[] { "win", "11", "debloat" })]
    [InlineData("  ", new string[0])]
    public void NamesSplitIntoWords(string name, string[] words) => Assert.Equal(words, SearchMatch.Words(name));

    [Theory]
    [InlineData("chrome", "crhome", 1)]
    [InlineData("spotify", "spotfy", 1)]
    [InlineData("abc", "abc", 0)]
    [InlineData("kitten", "sitting", 3)]
    public void DistanceCountsASwapAsOne(string a, string b, int expected) => Assert.Equal(expected, SearchMatch.Distance(a, b));

    [Fact]
    public void AppsComeFirstWheneverAnyAppMatches()
    {
        // The file's name matches better than the app's, and is still listed after it.
        var items = new[] { File("Terminal notes.txt"), App("Windows Terminal") };
        var ordered = SearchRank.Order(items, "terminal", None);
        Assert.Equal(["Windows Terminal", "Terminal notes.txt"], ordered.Select(i => i.Name));
    }

    [Fact]
    public void GroupsComeInOrderWithTheOfferAndAskLast()
    {
        var items = new[]
        {
            new SearchItem(SearchKind.Ask, "Ask Tomte", "report"),
            new SearchItem(SearchKind.GetEverything, "Search every drive", ""),
            File("report.pdf"),
            new SearchItem(SearchKind.Shelf, "report draft.docx", @"C:\Shelf\report draft.docx"),
            new SearchItem(SearchKind.Shortcut, "Reports folder", "3"),
            App("Report Builder"),
        };
        var kinds = SearchRank.Order(items, "report", None).Select(i => i.Kind);
        Assert.Equal([SearchKind.App, SearchKind.Shortcut, SearchKind.Shelf, SearchKind.File, SearchKind.GetEverything, SearchKind.Ask], kinds);
    }

    [Fact]
    public void GroupsAreCappedSoFilesStillShow()
    {
        var items = Enumerable.Range(0, 9).Select(i => App($"Code tool {i}"))
            .Concat(Enumerable.Range(0, 12).Select(i => File($"code {i}.txt")))
            .Append(new SearchItem(SearchKind.GetEverything, "Search every drive", ""));
        var ordered = SearchRank.Order(items, "code", None);
        Assert.Equal(SearchRank.MaxApps, ordered.Count(i => i.Kind == SearchKind.App));
        Assert.Equal(SearchRank.MaxFiles, ordered.Count(i => i.Kind == SearchKind.File));
        Assert.Equal(SearchKind.GetEverything, ordered[^1].Kind);
    }

    [Fact]
    public void ARowOpenedBeforeLeadsItsTierButNotABetterOne()
    {
        // "c" is a prefix of Calculator and Character Map, and starts a word in Google Chrome.
        var chrome = App("Google Chrome");
        var map = App("Character Map");
        var launches = new Dictionary<string, Launches> { [chrome.Key] = new(3, 1), [map.Key] = new(1, 1) };
        var ordered = SearchRank.Order(Apps, "c", launches).Select(i => i.Name).ToList();
        Assert.True(ordered.IndexOf("Character Map") < ordered.IndexOf("Calculator"));
        Assert.True(ordered.IndexOf("Calculator") < ordered.IndexOf("Google Chrome"));
        // A name that starts with "chr" still beats one where "chr" only starts a later word, however often that was opened.
        Assert.Equal("Chrome Remote Desktop", First("chr", launches));
    }

    [Fact]
    public void AProgramFileOpenedOftenLeadsTheApps()
    {
        // A portable app Start does not list: opened from search before, it passes Arc; never opened, it stays with the files.
        var ayu = File("AyuGram.exe");
        var arc = App("Arc");
        var launches = new Dictionary<string, Launches> { [ayu.Key] = new(7, 1), [arc.Key] = new(2, 1) };
        Assert.Equal("AyuGram.exe", SearchRank.Order([arc, ayu], "a", launches)[0].Name);
        Assert.Equal("Arc", SearchRank.Order([arc, ayu], "a", None)[0].Name);
        Assert.Equal("Arc", SearchRank.Order([arc, File("Agenda.pdf")], "a", new Dictionary<string, Launches> { [File("Agenda.pdf").Key] = new(9, 1) })[0].Name);
    }

    [Fact]
    public void FilesTheFileSearchFoundStayEvenWhenTheMatcherMissesThem()
    {
        var ordered = SearchRank.Order([File("Résumé.pdf")], "resume", None);
        Assert.Single(ordered);
    }

    [Fact]
    public void AnEmptyBoxListsWhatWasOpenedMostThenLatest()
    {
        var chrome = App("Google Chrome");
        var code = App("Visual Studio Code");
        var paint = App("Paint");
        var notes = File("notes.txt");
        var launches = new Dictionary<string, Launches>
        {
            [code.Key] = new(5, 10),
            [chrome.Key] = new(2, 30),
            [paint.Key] = new(2, 20),
            [notes.Key] = new(9, 40),
        };
        var recent = SearchRank.Recent([notes, paint, chrome, code, App("Steam")], launches);
        Assert.Equal(["Visual Studio Code", "Google Chrome", "Paint", "notes.txt"], recent.Select(i => i.Name));
    }

    [Fact]
    public void KeysIgnoreCaseAsWindowsPathsDo() =>
        Assert.Equal(File("A.TXT").Key, new SearchItem(SearchKind.File, "a.txt", @"d:\work\a.txt").Key);
}

public sealed class SearchLayoutTests
{
    private static readonly SearchLayout Layout = new((float)PanelSize.SearchWidth, mascot: true, 480);

    [Fact]
    public void EachGroupGetsItsHeadingAndTheAiRowsARule()
    {
        SearchItem[] rows =
        [
            new(SearchKind.App, "Code", "a"), new(SearchKind.App, "Codex", "b"), new(SearchKind.File, "code.txt", @"C:\code.txt"),
            new(SearchKind.GetEverything, "Search every drive", ""), new(SearchKind.Ask, "Ask Tomte", "code"),
        ];
        var lines = SearchLayout.Lines(rows);
        Assert.Equal(["APPS", null, null, "FILES", null, null, null, null], lines.Select(l => l.Heading));
        Assert.Equal([-1, 0, 1, -1, 2, 3, -1, 4], lines.Select(l => l.Row));
        Assert.True(lines[6].Rule);
        // Each line 2 apart, a heading after a row 4 more.
        float expected = (2 * SearchLayout.HeadingHeight) + SearchLayout.HeadingAbove + SearchLayout.RuleHeight + (5 * SearchLayout.RowHeight) + (7 * SearchLayout.LineGap);
        Assert.Equal(expected, lines[^1].Top + lines[^1].Height, 3);
    }

    [Fact]
    public void ASearchWithTextLeadsWithItsTopHitThenItsGroupUnderItsHeading()
    {
        SearchItem[] rows = [new FileHit(@"D:\Work\Pricing review notes.docx", false).Item(), new FileHit(@"D:\pricing-2027.xlsx", false).Item()];
        var lines = SearchLayout.Lines(rows, hit: true);
        Assert.Equal([SearchLayout.TopHit, null, "FILES", null], lines.Select(l => l.Heading));
        Assert.Equal([false, true, false, false], lines.Select(l => l.Hit));
        Assert.Equal(SearchLayout.HitHeight, lines[1].Height);
        Assert.Equal(SearchLayout.RowHeight, lines[3].Height);
        // The AI rows never stand as the top hit.
        Assert.DoesNotContain(SearchLayout.Lines([new(SearchKind.Ask, "Ask Tomte about \u201cx\u201d", "x")], hit: true), l => l.Hit);
    }

    [Fact]
    public void TheBandSitsBetweenTheBoxAndTheFooterInsideTheIsland()
    {
        var band = Layout.Band;
        Assert.Equal(SearchLayout.BoxHeight, Layout.Box.Height);
        Assert.True(band.Top > Layout.Box.Bottom);
        Assert.True(band.Bottom + SearchLayout.FootHeight < Layout.Height);
        Assert.Equal(480, SearchLayout.HeightFor(1440));
        Assert.Equal(360, SearchLayout.HeightFor(720)); // 1080p at 150%
    }

    [Fact]
    public void TheBoxIsAsTallAsWhatItShows()
    {
        var app = new SearchItem(SearchKind.App, "Code", "a");
        float w = (float)PanelSize.SearchWidth;
        // With no rows the hint's room shows under the field, and the footer under it.
        float below = SearchLayout.Gap + SearchLayout.FootHeight + SearchLayout.Bottom;
        Assert.Equal(new SearchLayout(w, true, 300).Band.Y + SearchLayout.EmptyRoom + below, SearchLayout.Fit(w, true, [], null, 480));
        float two = SearchLayout.Fit(w, true, [app, app with { Target = "b" }], null, 480);
        var tall = new SearchLayout(w, true, two);
        Assert.Equal(SearchLayout.HeadingHeight + (2 * SearchLayout.RowHeight) + (2 * SearchLayout.LineGap), tall.Band.Height, 3);
        Assert.Equal(480, SearchLayout.Fit(w, true, [.. Enumerable.Range(0, 20).Select(i => app with { Target = $"{i}" })], null, 480));
        Assert.True(SearchLayout.Fit(w, true, [], new SearchCard("Title", "Body", [("ok", "OK")]), 480) > PanelSize.PaletteMinHeight);
    }

    [Fact]
    public void ARowKeepsItsPartsInsideTheBand()
    {
        var item = new SearchItem(SearchKind.File, new string('x', 200) + ".pdf", @"C:\x", new string('y', 300), Location: @"C:\x");
        var row = Layout.Row(new SearchLine(36, SearchLayout.RowHeight, null, 0), item, selected: false);
        Assert.True(row.Badge.Right < row.Name.Slot.Left && row.Name.Slot.Right <= row.Detail.Slot.Left);
        Assert.True(row.Detail.Slot.Right <= row.Row.Right - SearchLayout.RowPadX);
        Assert.Equal(SearchLayout.RowBadge, row.Badge.Width);
        Assert.Null(row.Key);
        Assert.Equal("PDF", SearchLayout.BadgeText(item));
        Assert.Equal(Theme.KindPdf, SearchLayout.BadgeFill(item));
    }

    [Fact]
    public void TheTopHitShowsItsPathUnderItsNameAndEnterWhileSelected()
    {
        var item = new FileHit(@"D:\Work\Launch\Pricing review notes.docx", false).Item();
        var line = new SearchLine(26, SearchLayout.HitHeight, null, 0, Hit: true);
        var row = Layout.Row(line, item, selected: true);
        Assert.Equal(SearchLayout.HitBadge, row.Badge.Width);
        Assert.Equal(@"D:\Work\Launch\Pricing review notes.docx", row.Detail.Text);
        Assert.True(row.Name.Slot.Bottom <= row.Detail.Slot.Top);
        Assert.Equal("Enter", row.Key);
        Assert.True(row.KeyCap.Right <= row.Row.Right && row.Name.Slot.Right < row.KeyCap.Left);
        Assert.Null(Layout.Row(line, item, selected: false).Key);
        Assert.Equal(("DOCX", Theme.KindDocument), (SearchLayout.BadgeText(item), SearchLayout.BadgeFill(item)));
    }

    [Fact]
    public void OnlyKeysThatWorkAreHinted()
    {
        var ask = new SearchItem(SearchKind.Ask, "Ask Tomte about \u201cpri\u201d", "pri");
        var ai = new SearchItem(SearchKind.AiSearch, SearchLayout.AiRow, "pri");
        var app = new SearchItem(SearchKind.App, "Paint", "p");
        Assert.Equal("Enter", SearchLayout.KeyFor(ask, hit: false, selected: true));
        Assert.Null(SearchLayout.KeyFor(ask, hit: false, selected: false));
        Assert.Equal("Ctrl Enter", SearchLayout.KeyFor(ai, hit: false, selected: false));
        Assert.Null(SearchLayout.KeyFor(app, hit: false, selected: true));
        var (left, right) = SearchLayout.Keys(ai: false, typed: true, back: false);
        Assert.Equal(["↑ ↓", "Enter", "Ctrl Shift Enter", "Tab"], left.Select(k => k.Keys));
        Assert.Equal(("Esc", "clear"), right);
        Assert.Equal("close", SearchLayout.Keys(false, typed: false, back: false).Right.Word);
        Assert.Equal("back", SearchLayout.Keys(false, typed: false, back: true).Right.Word);
        Assert.DoesNotContain(SearchLayout.Keys(ai: true, typed: true, back: false).Left, k => k.Keys == "Tab");
    }

    [Theory]
    [InlineData("Pricing review notes.docx", "pri", 0, 3)]
    [InlineData("Printers & scanners", "PRI", 0, 3)]
    [InlineData("deck pricing", "pri", 5, 3)]
    [InlineData("open sprint", "open pri", 6, 3)]
    public void TheTypedLettersAreMarkedWhereAWordStarts(string name, string typed, int start, int length) =>
        Assert.Equal([(start, length)], SearchLayout.Matched(name, typed));

    [Fact]
    public void MatchedLettersMergeAndALooseMatchMarksNothing()
    {
        Assert.Equal([(0, 7)], SearchLayout.Matched("pricing", "pri pricing"));
        Assert.Equal([(0, 4), (5, 6)], SearchLayout.Matched("code review", "review code"));
        Assert.Empty(SearchLayout.Matched("Chrome", "crhome"));
    }

    [Fact]
    public void TheSourceDotSaysWhetherFilesAreFound()
    {
        Assert.Equal(Theme.Ok, SearchLayout.SourceDot("Everything"));
        Assert.Equal(Theme.Ok, SearchLayout.SourceDot("Windows index"));
        Assert.Equal(Theme.Ok, SearchLayout.SourceDot("Sent to Anthropic · claude-haiku-5-5"));
        Assert.Equal(Theme.Warn, SearchLayout.SourceDot(SearchLayout.IndexOff));
        Assert.Equal(Theme.Ink3, SearchLayout.SourceDot("Starting Everything"));
        var (dot, line) = Layout.Source("Everything");
        Assert.True(dot.Right < line.Slot.Left && line.Slot.Right <= Layout.Box.Right - SearchLayout.BoxPadX + 0.01f);
        Assert.True(Layout.EditorFor("Everything").Right < dot.Left && Layout.EditorFor("Everything").Left > Layout.Glass.Right);
    }

    [Fact]
    public void FilesAndFoldersGetTheFolderButtonAndNothingElseDoes()
    {
        SearchItem[] with =
        [
            new FileHit(@"D:\Work\notes.txt", false).Item(),
            new FileHit(@"D:\Work", true).Item(),
            new(SearchKind.Shelf, "draft.docx", @"C:\Shelf\draft.docx", @"C:\Shelf", @"C:\Shelf\draft.docx", @"C:\Shelf\draft.docx"),
            new(SearchKind.Shortcut, "Projects", "4", @"D:\Projects", null, @"D:\Projects"),
        ];
        SearchItem[] without =
        [
            new(SearchKind.App, "Paint", @"shell:AppsFolder\Paint"),
            new(SearchKind.Shortcut, "Mail", "5", "https://mail.example.com"),
            new(SearchKind.Place, "Display", "ms-settings:display", "System"),
            new(SearchKind.Place, "Hotkeys", "tomte:settings:Hotkeys", "Tomte settings"),
            new(SearchKind.Ask, "Ask Tomte", "x"),
            new(SearchKind.AiSearch, SearchLayout.AiRow, "x"),
            new(SearchKind.GetEverything, "Search every drive", ""),
        ];
        Assert.All(with, item => Assert.True(SearchLayout.HasFolder(item), item.Name));
        Assert.All(without, item => Assert.False(SearchLayout.HasFolder(item), item.Name));
    }

    [Fact]
    public void TheFolderButtonSitsAtTheRowsRightEndInsideIt()
    {
        var line = new SearchLine(36, SearchLayout.RowHeight, null, 0);
        var item = new SearchItem(SearchKind.File, new string('x', 200), @"C:\x", new string('y', 300), Location: @"C:\x");
        var row = Layout.Row(line, item, selected: false);
        var (card, glyph) = Layout.Folder(line);
        Assert.True(card.Right <= row.Row.Right && card.Left > row.Name.Slot.Right);
        Assert.Equal((row.Row.Top + row.Row.Bottom) / 2, (card.Top + card.Bottom) / 2);
        Assert.True(card.Contains(glyph) && card.Height >= StateLook.MinTarget);
        Assert.Equal("Show in folder (Ctrl+Shift+Enter)", new Target("folder:0", card, SearchLayout.FolderCorner, TargetKind.Button, Tip: SearchLayout.FolderTip, Key: SearchLayout.FolderKey).TipWith(keys: true));
    }

    [Fact]
    public void TheListScrollsAsLittleAsItCanToShowALine()
    {
        float band = Layout.Band.Height;
        Assert.Equal(0, Layout.ScrollTo(0, 36, 72, 1000));
        Assert.Equal(1000 - band - 100, Layout.ScrollTo(0, 1000 - 136, 1000 - 100, 1000), 3);
        Assert.Equal(100, Layout.ScrollTo(300, 100, 136, 1000));
        Assert.Equal(0, Layout.ScrollTo(50, 0, 36, 20));
    }

    [Fact]
    public void ACardsFocusedButtonStandsOutAndFocusNeverMovesAButton()
    {
        var offer = new SearchCard("Title", "Body", [("notnow", "Not now"), ("install", "Install")]);
        var card = Layout.Card(offer);
        Assert.Equal(["notnow", "install"], card.Buttons.Select(b => b.Id));
        Assert.True(card.Buttons[0].Box.Right < card.Buttons[1].Box.Left);
        Assert.True(card.Card.Contains(card.Buttons[1].Box));
        // Enter presses the safe button until Tab moves on: it is the focused one, drawn as the main one.
        Assert.Equal(PaletteLayout.RunButton, card.Buttons[0].Label.Style);
        Assert.Equal(PaletteLayout.Button, card.Buttons[1].Label.Style);
        var tabbed = Layout.Card(offer, 1);
        Assert.Equal(PaletteLayout.RunButton, tabbed.Buttons[1].Label.Style);
        Assert.Equal(card.Buttons.Select(b => b.Box), tabbed.Buttons.Select(b => b.Box));
    }
}

public sealed class FileQueryTests
{
    private static readonly string[] Excluded = [@"C:\Windows", @"C:\Users\O'Brien\AppData\"];

    [Theory]
    [InlineData("  quarterly   report ", new[] { "quarterly", "report" })]
    [InlineData("\"tax\" Tax 2025", new[] { "tax", "2025" })]
    [InlineData("a\tb\u0001c", new[] { "a", "bc" })]
    [InlineData("\"\"", new string[0])]
    [InlineData("1 2 3 4 5 6 7 8 9 10", new[] { "1", "2", "3", "4", "5", "6", "7", "8" })]
    public void TypedTextSplitsIntoWords(string text, string[] words) => Assert.Equal(words, FileQuery.Words(text));

    [Fact]
    public void EverythingGetsEachWordQuotedAndTheFoldersLeftOut() =>
        Assert.Equal(
            "\"tax|2025\" \"!report\" !\"C:\\Windows\\\" !\"C:\\Users\\O'Brien\\AppData\\\" !\"\\$Recycle.Bin\\\"",
            FileQuery.Everything(["tax|2025", "!report"], Excluded));

    [Fact]
    public void TheIndexGetsWordStartsAndEscapedQuotes() =>
        Assert.Equal(
            "SELECT TOP 60 System.ItemPathDisplay, System.ItemType FROM SystemIndex WHERE SCOPE='file:' AND CONTAINS(System.FileName, '\"o''neil*\" AND \"cv*\"')"
            + " AND NOT SCOPE='file:C:/Windows' AND NOT SCOPE='file:C:/Users/O''Brien/AppData' ORDER BY System.DateModified DESC",
            FileQuery.IndexSql(["o'neil", "cv"], Excluded));

    [Fact]
    public void NoWordsAskNothing()
    {
        Assert.Equal("", FileQuery.Everything([], Excluded));
        Assert.Equal("", FileQuery.IndexSql([], Excluded));
    }

    [Fact]
    public void TheExcludedFoldersAreFullPaths() =>
        Assert.All(FileQuery.Excluded(), folder => Assert.True(Path.IsPathFullyQualified(folder), folder));

    [Fact]
    public void AHitBecomesARowNamedAfterItsFile()
    {
        var file = new FileHit(@"D:\Work\Taxes\return 2025.pdf", false).Item();
        Assert.Equal((SearchKind.File, "return 2025.pdf", @"D:\Work\Taxes"), (file.Kind, file.Name, file.Detail));
        Assert.Equal(@"D:\Work\Taxes\return 2025.pdf", file.Location);
        var drive = new FileHit(@"D:\", true).Item();
        Assert.Equal((SearchKind.Folder, @"D:\"), (drive.Kind, drive.Name));
    }

    [Fact]
    public void EverythingsQueryIsLaidOutAsItsSdkSays()
    {
        byte[] query = Everything.Query(0x1234, "ab", 60);
        uint[] fields = [.. Enumerable.Range(0, 7).Select(i => BitConverter.ToUInt32(query, i * 4))];
        // Reply window, the reply's tag ("TOMT"), flags, offset, max, full path and name, newest change first.
        Assert.Equal([0x1234u, 0x544F4D54u, 0u, 0u, 60u, 4u, 14u], fields);
        Assert.Equal("ab\0", System.Text.Encoding.Unicode.GetString(query, 28, query.Length - 28));
    }

    [Fact]
    public void EverythingsAnswerIsRead()
    {
        // EVERYTHING_IPC_LIST2 with two items: a folder and a file, each its full path; then one item cut short.
        var data = new List<byte>();
        void U32(uint v) => data.AddRange(BitConverter.GetBytes(v));
        void Text(string s)
        {
            U32((uint)s.Length);
            data.AddRange(System.Text.Encoding.Unicode.GetBytes(s + "\0"));
        }

        U32(3); // total
        U32(3); // in this answer
        U32(0);
        U32(4); // full path and name
        U32(14);
        const int Items = 20 + (3 * 8);
        string folder = @"D:\Work", file = @"D:\Work\notes.txt";
        U32(1);
        U32(Items);
        U32(0);
        U32((uint)(Items + 4 + ((folder.Length + 1) * 2)));
        U32(0);
        U32(100_000);
        Text(folder);
        Text(file);
        Assert.Equal([new FileHit(folder, true), new FileHit(file, false)], Everything.Parse(data.ToArray()));
        Assert.Empty(Everything.Parse(new byte[8]));

        // A relative name and a path with a quote are left out; the full path after them is read.
        data.Clear();
        string[] paths = ["notes.txt", "C:\\a\"b.txt", @"\\server\share\c.txt"];
        U32(3);
        U32(3);
        U32(0);
        U32(4);
        U32(14);
        int at = 20 + (3 * 8);
        foreach (string p in paths)
        {
            U32(0);
            U32((uint)at);
            at += 4 + ((p.Length + 1) * 2);
        }

        foreach (string p in paths)
        {
            Text(p);
        }

        Assert.Equal([new FileHit(@"\\server\share\c.txt", false)], Everything.Parse(data.ToArray()));
    }

    [Theory]
    [InlineData("\"C:\\Program Files\\Everything\\Everything.exe\" -startup", @"C:\Program Files\Everything\Everything.exe")]
    [InlineData("C:\\Tools\\Everything.exe -svc", @"C:\Tools\Everything.exe")]
    [InlineData("\"C:\\Tools\\other.cmd\"", null)]
    [InlineData("", null)]
    [InlineData(null, null)]
    public void TheExeComesFromACommandLine(string? commandLine, string? exe) => Assert.Equal(exe, EverythingSetup.ExeFrom(commandLine));

    [Fact]
    public void WingetInstallsTheExactPackageQuietly() =>
        Assert.Equal(
            "install --id voidtools.Everything -e --source winget --silent --scope machine --accept-package-agreements --accept-source-agreements",
            string.Join(' ', EverythingSetup.WingetArguments));

    [Fact]
    public void WingetIsFoundByItsFullPathOrNotAtAll()
    {
        string? winget = EverythingSetup.WingetExe();
        Assert.True(winget is null || (Path.IsPathFullyQualified(winget) && winget.EndsWith("winget.exe", StringComparison.OrdinalIgnoreCase)), winget);
    }

    [Fact]
    public void EverythingIsLookedForMachineWideFirstAndInTheUsersOwnEntryLast()
    {
        var places = EverythingSetup.Candidates(
            name => name == "Everything" ? "\"C:\\Program Files\\Everything\\Everything.exe\" -startup" : null,
            name => name == "Everything" ? "\"D:\\Service\\Everything.exe\" -svc" : null,
            name => name == "Everything" ? "C:\\Users\\me\\Everything.exe -startup" : null,
            ["C:\\Program Files", ""]).ToList();
        Assert.Equal(
            [
                @"C:\Program Files\Everything\Everything.exe", @"D:\Service\Everything.exe", @"C:\Program Files\Everything\Everything.exe",
                @"C:\Program Files\Everything 1.5a\Everything64.exe", @"C:\Program Files\Everything 1.5a\Everything.exe", @"C:\Users\me\Everything.exe",
            ],
            places);
    }

    [Theory]
    [InlineData(null, false)] // no service
    [InlineData(2, true)] // automatic
    [InlineData(3, true)] // manual
    [InlineData(4, false)] // disabled
    public void OnlyAServiceThatCanRunLetsTomteStartEverything(int? start, bool starts) => Assert.Equal(starts, EverythingSetup.ServiceStarts(start));

    [Theory]
    [InlineData(FileSource.Index, false, true)]
    [InlineData(FileSource.Index, true, false)] // "Not now"
    [InlineData(FileSource.Everything, false, false)]
    [InlineData(FileSource.EverythingStopped, false, false)]
    [InlineData(FileSource.EverythingManual, false, false)]
    public void TheOfferShowsOnlyOnTheIndexUntilNotNow(FileSource source, bool dismissed, bool offers) =>
        Assert.Equal(offers, EverythingSetup.Offers(source, dismissed));
}

public sealed class SearchHistoryTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "tomte-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(dir, recursive: true);

    [Fact]
    public void NotNowIsKeptInTheSettingsFile()
    {
        string file = Path.Combine(dir, "settings.json");
        using (var store = new SettingsStore(file))
        {
            Assert.False(store.Current.EverythingOfferDismissed);
            Assert.True(EverythingSetup.Offers(FileSource.Index, store.Current.EverythingOfferDismissed));
            store.Save(store.Current with { EverythingOfferDismissed = true });
        }

        var again = SettingsStore.Load(file);
        Assert.True(again.EverythingOfferDismissed);
        Assert.False(EverythingSetup.Offers(FileSource.Index, again.EverythingOfferDismissed));
        Assert.Contains("\"everythingOfferDismissed\": true", File.ReadAllText(file));
    }

    [Fact]
    public void OpeningARowCountsItAndKeepsTheNewestRows()
    {
        var history = new SearchHistory(new Tomte.Core.Storage.TomteDb(Path.Combine(dir, "tomte.db")));
        var code = new SearchItem(SearchKind.App, "Visual Studio Code", @"shell:AppsFolder\Code");
        var start = new DateTimeOffset(2026, 10, 15, 9, 0, 0, TimeSpan.Zero);
        history.Record(code, start);
        history.Record(code with { Name = "Code" }, start.AddMinutes(1));
        Assert.Equal(new Launches(2, start.AddMinutes(1).UtcTicks), history.Load().Launches[code.Key]);
        Assert.Equal("Code", Assert.Single(history.Load().Items).Name);

        for (int i = 0; i < SearchHistory.MaxRows; i++)
        {
            history.Record(new SearchItem(SearchKind.File, $"f{i}.txt", $@"D:\f{i}.txt"), start.AddMinutes(2 + i));
        }

        var (items, launches) = history.Load();
        Assert.Equal(SearchHistory.MaxRows, items.Count);
        Assert.False(launches.ContainsKey(code.Key));
    }
}

public sealed class FileFilterQueryTests
{
    private static readonly string[] Excluded = [@"C:\Windows"];

    // India: a day starts at 18:30 UTC the day before.
    private static readonly TimeZoneInfo India = TimeZoneInfo.CreateCustomTimeZone("test", TimeSpan.FromHours(5.5), "test", "test");

    private static readonly FileFilter Full = new(["invoice"], @"D:\Work\", false, ["pdf", "docx"], new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30));

    [Fact]
    public void EverythingGetsTheFolderKindExtensionsAndDays() =>
        Assert.Equal("\"invoice\" \"D:\\Work\\\" file: ext:pdf;docx dm:>=2026-09-01 dm:<2026-10-01 !\"C:\\Windows\\\" !\"\\$Recycle.Bin\\\"",
            FileQuery.Everything(Full, Excluded));

    [Fact]
    public void WordsInThePathAreForEverythingOnly()
    {
        var inPath = Full with { InPath = true };
        Assert.StartsWith("path:\"invoice\" \"D:\\Work\\\" file:", FileQuery.Everything(inPath, Excluded));
        // The index keeps to names: a path match there scans the whole index.
        Assert.Equal(FileQuery.IndexSql(Full, Excluded, 50, India), FileQuery.IndexSql(inPath, Excluded, 50, India));
    }

    [Fact]
    public void TheIndexGetsTheScopeTypeExtensionsAndUtcDays() =>
        Assert.Equal("SELECT TOP 50 System.ItemPathDisplay, System.ItemType FROM SystemIndex WHERE SCOPE='file:D:/Work' AND CONTAINS(System.FileName, '\"invoice*\"')"
            + " AND System.ItemType <> 'Directory' AND (System.FileExtension = '.pdf' OR System.FileExtension = '.docx')"
            + " AND System.DateModified >= '2026-08-31 18:30:00' AND System.DateModified < '2026-09-30 18:30:00' AND NOT SCOPE='file:C:/Windows' ORDER BY System.DateModified DESC",
            FileQuery.IndexSql(Full, Excluded, 50, India));

    [Fact]
    public void FoldersOnlyAndNoWords()
    {
        var folders = new FileFilter([], @"C:\Users\o'brien", true);
        Assert.Equal("\"C:\\Users\\o'brien\\\" folder: !\"C:\\Windows\\\" !\"\\$Recycle.Bin\\\"", FileQuery.Everything(folders, Excluded));
        Assert.Equal("SELECT TOP 60 System.ItemPathDisplay, System.ItemType FROM SystemIndex WHERE SCOPE='file:C:/Users/o''brien' AND System.ItemType = 'Directory'"
            + " AND NOT SCOPE='file:C:/Windows' ORDER BY System.DateModified DESC", FileQuery.IndexSql(folders, Excluded));
        Assert.Equal("", FileQuery.IndexSql(new FileFilter([], Folders: true), Excluded));
    }
}

public sealed class AiSearchRowTests
{
    private static readonly SearchItem Ai = new(SearchKind.AiSearch, SearchLayout.AiRow, "x");
    private static readonly SearchItem Ask = new(SearchKind.Ask, "Ask Tomte", "x");

    [Fact]
    public void ASentenceWithNoGoodLocalMatchPutsTheAiRowsFirstWithAskOnTop()
    {
        string typed = "the invoices I got last month";
        var app = new SearchItem(SearchKind.App, "Invoice Ninja", "a");
        var file = new FileHit(@"D:\Bills\invoices.pdf", false).Item();
        var ranked = SearchRank.Order([Ai, app, Ask], typed, new Dictionary<string, Launches>());

        Assert.Equal([Ask, Ai], ranked);
        Assert.Equal([Ask, Ai], SearchRank.AiFirst(ranked, typed));
        // The AI rows are one section, the last, Ask Tomte first.
        Assert.Equal((5, 5), (Ask.Group, Ai.Group));
        Assert.Equal([file, Ask, Ai], SearchRank.Order([Ai, Ask, file], "invoices", new Dictionary<string, Launches>()));
    }

    [Fact]
    public void AiSearchMovesUpOnlyForASentenceWithoutAGoodMatch()
    {
        var app = new SearchItem(SearchKind.App, "Visual Studio Code", "a");
        var file = new FileHit(@"D:\Notes\notes from the meeting.txt", false).Item();
        var none = new Dictionary<string, Launches>();

        // A name that matches only loosely (one typo, the other words nowhere) is no good match.
        var chrome = new SearchItem(SearchKind.App, "Chrome", "c");
        Assert.Equal([Ask, Ai, chrome], SearchRank.AiFirst([chrome, Ask, Ai], "crhome web browser"));
        // Two words read as a name.
        Assert.Equal(Ask, SearchRank.AiFirst([app, Ask, Ai], "tax return")[1]);
        Assert.Equal(app, SearchRank.AiFirst(SearchRank.Order([app, Ai, Ask], "visual studio code", none), "visual studio code")[0]);
        Assert.Equal(file, SearchRank.AiFirst(SearchRank.Order([file, Ai, Ask], "notes from the meeting", none), "notes from the meeting")[0]);
    }

    [Fact]
    public void TheWhyLineLeadsTheRowsWithoutGroupHeadings()
    {
        var rows = new[] { new FileHit(@"D:\a.pdf", false).Item(), new FileHit(@"D:\b", true).Item() };

        var lines = SearchLayout.Lines(rows, "AI · Two files.");

        Assert.Equal(["AI · Two files.", null, null], lines.Select(l => l.Heading));
        Assert.Equal([-1, 0, 1], lines.Select(l => l.Row));
        Assert.Equal(SearchLayout.HeadingHeight + (2 * SearchLayout.RowHeight) + (2 * SearchLayout.LineGap), lines[^1].Top + lines[^1].Height, 3);
        // AI search's picks are no top hit: the line on why leads them.
        Assert.DoesNotContain(SearchLayout.Lines(rows, "AI · Two files.", hit: true), l => l.Hit);
        Assert.Empty(SearchLayout.Lines([], "AI · nothing"));
        Assert.Equal(("AI", "AI"), (SearchLayout.KindLabel(Ask), SearchLayout.KindLabel(Ai)));
    }

    [Theory]
    [InlineData(false, false, SearchEnter.Open)]
    [InlineData(false, true, SearchEnter.Open)]
    [InlineData(true, false, SearchEnter.SearchWithAi)]
    [InlineData(true, true, SearchEnter.ShowInFolder)]
    public void CtrlEnterSearchesWithAiAndCtrlShiftEnterShowsTheFolder(bool ctrl, bool shift, SearchEnter does) =>
        Assert.Equal(does, SearchLayout.Enter(ctrl, shift));

    // Typing never makes the box shorter, so a letter does not move the shape; clearing the field lets it settle back once
    // the rows for the empty field are the ones on show.
    [Theory]
    [InlineData("", "", true)]
    [InlineData("", "code", false)]
    [InlineData("", null, false)]
    [InlineData("c", "c", false)]
    [InlineData("code", "cod", false)]
    public void OnlyAClearedBoxSettlesBack(string typed, string? shownFor, bool settles) => Assert.Equal(settles, SearchLayout.Settles(typed, shownFor));

    // The search box's parts share their edges and centres: the field ends where the rows do, the footer starts as far in
    // as the rows' badges, a badge, a row's words and the text in the field are centred on their row or field.
    [Theory]
    [InlineData((float)PanelSize.SearchWidth)]
    [InlineData(SearchLayout.NarrowestWidth)]
    public void TheSearchBoxSharesItsEdgesAndCentres(float width)
    {
        var layout = new SearchLayout(width, mascot: true, 300);
        var band = layout.Band;
        var line = new SearchLine(0, SearchLayout.RowHeight, null, 0);
        var row = layout.Row(line, new SearchItem(SearchKind.App, "Notepad", "notepad", "", "notepad"), selected: false);
        Assert.Equal(band.Right, layout.Box.Right, 3);
        Assert.Equal(band.Width, row.Row.Width, 3);
        var (left, right) = SearchLayout.Keys(ai: false, typed: true, back: false);
        var foot = layout.Footer(left, right);
        Assert.Equal(band.X + row.Badge.X, foot[0].Cap.X, 3);
        Assert.Equal(band.Right - SearchLayout.FootInset, foot[^1].Word.Slot.Right, 3);
        var editor = layout.EditorFor("Everything");
        Assert.Equal(layout.Box.Y + (layout.Box.Height / 2), editor.Y + (editor.Height / 2), 3);
        Assert.Equal(row.Row.Y + (row.Row.Height / 2), row.Badge.Y + (row.Badge.Height / 2), 3);
        Assert.Equal(row.Row.Y + (row.Row.Height / 2), row.Name.Slot.Y + (row.Name.Slot.Height / 2), 3);
    }

    [Fact]
    public void TheFooterKeepsEscAndDropsHintsFromTheEndWhenNarrow()
    {
        foreach (bool ai in new[] { false, true })
        {
            var (left, right) = SearchLayout.Keys(ai, typed: true, back: false);
            var wide = new SearchLayout((float)PanelSize.SearchWidth, mascot: true, 300).Footer(left, right);
            var narrow = new SearchLayout(SearchLayout.NarrowestWidth, mascot: true, 300).Footer(left, right);
            // The full width shows the three the box is known by and Esc; the narrowest keeps Esc and the first ones.
            Assert.True(wide.Count >= 4);
            Assert.Equal("Esc", narrow[^1].Keys);
            Assert.Equal(left.Take(narrow.Count - 1).Select(k => k.Keys), narrow.SkipLast(1).Select(k => k.Keys));
            for (int i = 1; i < narrow.Count; i++)
            {
                Assert.True(narrow[i - 1].Word.Slot.Right <= narrow[i].Cap.Left);
            }
        }
    }
}
