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
    [InlineData("Spotify", "spotifyy", SearchMatch.Typo)]
    [InlineData("Calculator", "calculator", SearchMatch.Exact)]
    public void EachKindOfMatchLandsInItsTier(string name, string query, int tier)
    {
        int score = SearchMatch.Score(name, query);
        Assert.InRange(score, tier, tier + 99);
    }

    [Theory]
    [InlineData("Google Chrome", "crh")] // a typo needs four letters
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
            "install --id voidtools.Everything -e --silent --scope machine --accept-package-agreements --accept-source-agreements",
            string.Join(' ', EverythingSetup.WingetArguments));
}

public sealed class SearchHistoryTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "tomte-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose() => Directory.Delete(dir, recursive: true);

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
