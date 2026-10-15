using Tomte.Core;
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
    private static readonly SearchLayout Layout = new(640, mascot: true, 480);

    [Fact]
    public void EachGroupGetsItsHeadingAndAskNone()
    {
        SearchItem[] rows =
        [
            new(SearchKind.App, "Code", "a"), new(SearchKind.App, "Codex", "b"), new(SearchKind.File, "code.txt", @"C:\code.txt"),
            new(SearchKind.GetEverything, "Search every drive", ""), new(SearchKind.Ask, "Ask Tomte", "code"),
        ];
        var lines = SearchLayout.Lines(rows);
        Assert.Equal(["Apps", null, null, "Files and folders", null, null, null], lines.Select(l => l.Heading));
        Assert.Equal([-1, 0, 1, -1, 2, 3, 4], lines.Select(l => l.Row));
        Assert.Equal((2 * SearchLayout.HeadingHeight) + (5 * SearchLayout.RowHeight), lines[^1].Top + lines[^1].Height);
    }

    [Fact]
    public void TheBandSitsBetweenTheBoxAndTheFooterInsideTheIsland()
    {
        var band = Layout.Band;
        Assert.True(band.Top > Layout.Box.Bottom);
        Assert.True(band.Bottom + SearchLayout.FootHeight < Layout.Height);
        Assert.Equal(480, SearchLayout.HeightFor(1440));
        Assert.Equal(360, SearchLayout.HeightFor(720)); // 1080p at 150%
    }

    [Fact]
    public void TheBoxIsAsTallAsWhatItShows()
    {
        var app = new SearchItem(SearchKind.App, "Code", "a");
        Assert.Equal((float)Tomte.Core.PanelSize.PaletteMinHeight, SearchLayout.Fit(640, true, [], null, 480));
        float two = SearchLayout.Fit(640, true, [app, app with { Target = "b" }], null, 480);
        var tall = new SearchLayout(640, true, two);
        Assert.Equal(SearchLayout.HeadingHeight + (2 * SearchLayout.RowHeight), tall.Band.Height, 3);
        Assert.Equal(480, SearchLayout.Fit(640, true, [.. Enumerable.Range(0, 20).Select(i => app with { Target = $"{i}" })], null, 480));
        Assert.True(SearchLayout.Fit(640, true, [], new SearchCard("Title", "Body", [("ok", "OK")]), 480) > Tomte.Core.PanelSize.PaletteMinHeight);
    }

    [Fact]
    public void ARowKeepsItsPartsInsideTheBand()
    {
        var item = new SearchItem(SearchKind.File, new string('x', 200), @"C:\x", new string('y', 300));
        var (row, icon, name, detail, kind) = Layout.Row(36, item);
        Assert.True(icon.Right < name.Slot.Left && name.Slot.Right <= detail.Slot.Left && detail.Slot.Right < kind.Slot.Left);
        Assert.True(kind.Slot.Right <= row.Right);
        Assert.Equal("File", kind.Text);
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
