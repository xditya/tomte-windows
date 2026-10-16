using System.Runtime.CompilerServices;
using Tomte.Core;
using Tomte.Core.Device;
using Tomte.Core.Search;
using Xunit;

namespace Tomte.Tests;

public sealed class SearchPlacesTests
{
    private static readonly Dictionary<string, Launches> None = [];

    private static readonly SearchItem[] StartApps =
    [
        App("Settings"), App("Sound Recorder"), App("Remote Mouse"), App("Camara Studio"), App("Visual Studio Code"), App("Services", @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\services.msc"),
    ];

    private static IReadOnlyList<SearchItem> AllPlaces => Places.Rows(StartApps);

    private static SearchItem App(string name, string? id = null) => new(SearchKind.App, name, @"shell:AppsFolder\" + (id ?? name.Replace(" ", "", StringComparison.Ordinal)));

    private static IReadOnlyList<SearchItem> Search(string typed) => SearchRank.Order(StartApps.Concat(AllPlaces), SearchRank.Query(typed), None);

    [Fact]
    public void EveryWindowsPageIsASettingsLinkAndThereAreEnough()
    {
        var pages = AllPlaces.Where(p => p.Target.StartsWith("ms-settings:", StringComparison.Ordinal)).ToList();
        Assert.All(pages, p => Assert.True(Apps.IsSettingsUri(p.Target), p.Target));
        Assert.All(pages, p => Assert.Equal("Settings", SearchLayout.KindLabel(p)));
        Assert.InRange(pages.Count, 60, 120);
    }

    [Fact]
    public void NoTwoPlacesShareATargetOrANameInTheSamePlace()
    {
        var rows = Places.Rows([]);
        Assert.Equal(rows.Count, rows.Select(r => r.Target).Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(rows.Count, rows.Select(r => (r.Name.ToLowerInvariant(), r.Detail)).Distinct().Count());
        // Windows' own pages and tools never share a name: one word for one thing.
        var windows = rows.Where(r => !r.Target.StartsWith("tomte:", StringComparison.Ordinal)).Select(r => r.Name).ToList();
        Assert.Equal(windows.Count, windows.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(rows, r => Assert.Equal(SearchKind.Place, r.Kind));
    }

    [Theory]
    [InlineData("open display settings", "display settings")]
    [InlineData("  Go to   downloads", "downloads")]
    [InlineData("show bluetooth", "bluetooth")]
    [InlineData("open", "open")]
    [InlineData("open ", "open ")]
    [InlineData("openvpn", "openvpn")]
    [InlineData("showcase", "showcase")]
    [InlineData("go tomte", "go tomte")]
    public void ALeadingVerbIsDropped(string typed, string query) => Assert.Equal(query, SearchRank.Query(typed));

    [Fact]
    public void WithOrWithoutTheVerbTheRowsAreTheSame()
    {
        Assert.Equal(Search("display settings"), Search("open display settings"));
        Assert.Equal(Search("tomte settings"), Search("go to tomte settings"));
        Assert.Equal(Search("bluetooth"), Search("show bluetooth"));
    }

    [Fact]
    public void DisplaySettingsPutsDisplayFirstAndNotTheSettingsApp()
    {
        var rows = Search("display settings");
        Assert.Equal(("Display", "ms-settings:display"), (rows[0].Name, rows[0].Target));
        int app = rows.ToList().FindIndex(r => r.Name == "Settings" && r.Kind == SearchKind.App);
        Assert.True(app is -1 or > 0);
        // "settings" alone is the Settings app, which matches it whole.
        Assert.Equal((SearchKind.App, "Settings"), (Search("settings")[0].Kind, Search("settings")[0].Name));
    }

    [Fact]
    public void APlaceMatchingEveryWordGoesAboveAppsThatMatchLoosely()
    {
        // Camara Studio matches "camera" only with a typo; the Camera pages match it whole.
        var rows = Search("camera");
        Assert.Equal(SearchKind.Place, rows[0].Kind);
        Assert.Contains(rows, r => r.Name == "Camara Studio");
        // An app that matches every word stays first, with the places after it.
        rows = Search("sound");
        Assert.Equal(["Sound Recorder", "Sound"], rows.Take(2).Select(r => r.Name));
        Assert.Equal([SearchKind.App, SearchKind.Place], rows.Take(2).Select(r => r.Kind));
    }

    [Theory]
    [InlineData("wifi", "Wi-Fi")] // the Firewall app matches only by its initials
    [InlineData("wi-fi", "Wi-Fi")]
    [InlineData("Wi Fi", "Wi-Fi")]
    [InlineData("wi", "WinDbg")] // an app that starts with the text stays first
    [InlineData("vpn", "HMA VPN")] // an app with the text as a whole word stays first
    [InlineData("bt", "Bluetooth & devices")]
    [InlineData("tom", "Tomte settings")] // Power Automate holds "tom" only inside a word
    [InlineData("uninstall", "Installed apps")] // another program's uninstaller never comes first
    [InlineData("taskbar", "Taskbar")] // SmartTaskbar is no whole word "taskbar"
    [InlineData("edg", "Microsoft Edge")] // a word start in a place's long word list does not beat an app's
    [InlineData("man", "Task Manager")]
    [InlineData("record", "Sound Recorder")]
    [InlineData("edit", "Registry Editor")]
    public void APlaceWithTheTextWholeGoesAboveAppsThatOnlyHoldIt(string typed, string first)
    {
        SearchItem[] apps =
        [
            App("Windows Defender Firewall with Advanced Security"), App("WinDbg"), App("HMA VPN"), App("qBittorrent"),
            App("Power Automate"), App("Uninstall"), App("Uninstall Node.js"), App("SmartTaskbar"),
            App("Microsoft Edge"), App("Task Manager"), App("Sound Recorder"), App("Registry Editor"),
        ];
        var rows = SearchRank.Order(apps.Concat(Places.Rows(apps)), SearchRank.Query(typed), None);
        Assert.Equal(first, rows[0].Name);
        Assert.True(SearchMatch.WholeWords("Wi-Fi", typed) || typed is not ("wifi" or "wi-fi" or "Wi Fi"));
        Assert.False(SearchMatch.WholeWords("SmartTaskbar", "taskbar"));
        Assert.False(SearchMatch.WholeWords("QuickTime Player", "time"));
    }

    [Fact]
    public void TomteSettingsAndHotkeysFindTomtesOwnPages()
    {
        var rows = Search("open tomte settings");
        Assert.Equal(("Tomte settings", "tomte:settings"), (rows[0].Name, rows[0].Target));
        Assert.Equal(SearchRank.MaxPlaces, rows.Count(r => r.Kind == SearchKind.Place));
        Assert.All(rows.Where(r => r.Kind == SearchKind.Place), r => Assert.StartsWith("tomte:settings", r.Target, StringComparison.Ordinal));

        var hotkeys = Search("hotkeys")[0];
        Assert.Equal(("Hotkeys", "tomte:settings:Hotkeys", "Tomte settings", "Tomte"), (hotkeys.Name, hotkeys.Target, hotkeys.Detail, SearchLayout.KindLabel(hotkeys)));
        Assert.True(Places.TomteSettingsAt(hotkeys.Target, out string? section));
        Assert.Equal("Hotkeys", section);
        Assert.Equal("tomte:settings:Search", Search("file search")[0].Target);
    }

    [Fact]
    public void TheSearchPageIsOneOfTomtesPlaces()
    {
        var page = Places.Rows([]).Single(r => r.Target == "tomte:settings:Search");
        Assert.Equal(("Search", "Tomte settings"), (page.Name, page.Detail));
        Assert.True(Places.TomteSettingsAt(page.Target, out string? section));
        Assert.Equal("Search", section);
        Assert.Equal(page, Search("open everything settings")[0]);
        Assert.Equal(page, Search("web search engine")[0]);
        // A search engine's name is a web search, not Tomte's settings.
        Assert.All(new[] { "bing", "google", "duckduckgo" }, engine => Assert.DoesNotContain(Search(engine), r => r.Target == "tomte:settings:Search"));
        Assert.Contains("everything", Places.TomteWords("Search"), StringComparison.Ordinal);
        Assert.Equal("", Places.TomteWords("Nowhere"));
    }

    [Fact]
    public void TheIslandsTabsAreFoundAndOpenTheirTab()
    {
        var shelf = Search("shelf").First(r => r.Detail == "Tomte island");
        Assert.Equal(PanelId.Shelf, Places.Tab(shelf.Target));
        Assert.Equal(Enum.GetValues<PanelId>().Length, Places.Rows([]).Count(r => Places.Tab(r.Target) is not null));
        Assert.Null(Places.Tab("tomte:tab:9"));
        Assert.Null(Places.Tab("ms-settings:display"));
    }

    [Fact]
    public void ToolsStartAlreadyListsAreNotListedTwice()
    {
        // Device Manager is not in the test's Start list; Disk Management is there under another name, by its program.
        var apps = new[] { App("Device Manager"), App("Datenträgerverwaltung", @"{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\diskmgmt.msc") };
        Assert.Contains(Places.Rows([]), r => r.Name == "Device Manager");
        Assert.Contains(Places.Rows([]), r => r.Name == "Disk Management");
        var rows = Places.Rows(apps);
        Assert.DoesNotContain(rows, r => r.Name is "Device Manager" or "Disk Management");
        Assert.Equal(["Device Manager"], SearchRank.Order(apps.Concat(rows), "device manager", None).Where(r => r.Name == "Device Manager").Select(r => r.Name));
    }

    [Fact]
    public void OnlyThePagesAndToolsInTheTableOpen()
    {
        Assert.True(Places.Open("ms-settings:display", dryRun: true).Ok);
        Assert.True(Places.Open("rundll32.exe sysdm.cpl,EditEnvironmentVariables", dryRun: true).Ok);
        Assert.False(Places.Open("cmd.exe", dryRun: true).Ok);
        Assert.False(Places.Open("rundll32.exe shell32.dll,Control_RunDLL", dryRun: true).Ok);
        Assert.False(Places.Open("ms-settings:display & calc", dryRun: true).Ok);
        Assert.False(Places.TomteSettingsAt("tomte:settings:Nowhere", out _));
        Assert.True(Places.TomteSettingsAt("tomte:settings", out string? first));
        Assert.Null(first);
        Assert.Equal("Tool", SearchLayout.KindLabel(Search("device manager")[0]));
    }

    [Fact]
    public void PlacesComeSecondUnderOneHeadingAndThreeAtMost()
    {
        var items = new[]
        {
            new SearchItem(SearchKind.Ask, "Ask Tomte", "sound"),
            new SearchItem(SearchKind.GetEverything, "Search every drive", ""),
            new SearchItem(SearchKind.File, "sound.wav", @"D:\sound.wav", @"D:\", Location: @"D:\sound.wav"),
            new SearchItem(SearchKind.Shelf, "sound notes.txt", @"C:\Shelf\sound notes.txt"),
            new SearchItem(SearchKind.Shortcut, "Sound folder", "3"),
            App("Sound Recorder"),
        }.Concat(AllPlaces);
        var rows = SearchRank.Order(items, "sound", None);
        Assert.Equal([SearchKind.App, SearchKind.Place, SearchKind.Shortcut, SearchKind.Shelf, SearchKind.File, SearchKind.GetEverything, SearchKind.Ask], rows.Select(r => r.Kind).Distinct());
        Assert.Equal(SearchRank.MaxPlaces, rows.Count(r => r.Kind == SearchKind.Place));
        Assert.Equal(["Apps", "Settings and tools", "Shortcuts and shelf", "Files and folders"], SearchLayout.Lines(rows).Select(l => l.Heading).OfType<string>());
    }

    [Fact]
    public void EveryTomteSectionIsTextTheSettingsWindowShows()
    {
        string window = string.Concat(Directory.GetFiles(Path.Combine(RepoRoot(), "src", "Tomte.Panels"), "SettingsWindow*.cs").Select(File.ReadAllText));
        foreach (var row in Places.Rows([]))
        {
            if (Places.TomteSettingsAt(row.Target, out string? section) && section is not null)
            {
                Assert.Contains($"\"{section}\"", window, StringComparison.Ordinal);
            }
        }
    }

    [Fact]
    public void RankingTheAppsAndPlacesIsQuick()
    {
        var apps = Enumerable.Range(0, 300).Select(i => App($"Some App Name {i}")).ToList();
        var candidates = apps.Concat(Places.Rows(apps)).ToList();
        SearchRank.Order(candidates, "d", None);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        foreach (string typed in new[] { "d", "di", "dis", "disp", "displ", "displa", "display", "display s", "display settings" })
        {
            SearchRank.Order(candidates, typed, None);
        }

        Assert.True(watch.ElapsedMilliseconds < 200, $"{watch.ElapsedMilliseconds} ms for nine keystrokes");
    }

    private static string RepoRoot([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}

public sealed unsafe class SettingsSectionTests
{
    [Fact]
    public void OnlyTomtesMessageWithAShortTextIsRead()
    {
        Assert.Equal("Hotkeys", Read(SettingsSection.Tag, "Hotkeys"));
        Assert.Equal("Opening and closing", Read(SettingsSection.Tag, "Opening and closing"));
        Assert.Null(Read(SettingsSection.Tag + 1, "Hotkeys")); // another program's WM_COPYDATA
        Assert.Null(Read(SettingsSection.Tag, new string('x', SettingsSection.MaxLength + 1)));
        Assert.Null(Read(SettingsSection.Tag, ""));
        Assert.False(SettingsSection.TryRead(0, out _));

        // An odd byte count is no UTF-16 text.
        fixed (char* text = "Hotkeys")
        {
            var data = new Windows.Win32.System.DataExchange.COPYDATASTRUCT { dwData = SettingsSection.Tag, cbData = 5, lpData = text };
            Assert.False(SettingsSection.TryRead((nint)(&data), out _));
        }
    }

    [Fact]
    public void NothingIsSentWithoutAWindowOrWithATooLongText()
    {
        Assert.False(SettingsSection.Send(0, "Hotkeys"));
        Assert.False(SettingsSection.Send(1, new string('x', SettingsSection.MaxLength + 1)));
    }

    private static string? Read(nuint tag, string section)
    {
        fixed (char* text = section)
        {
            var data = new Windows.Win32.System.DataExchange.COPYDATASTRUCT { dwData = tag, cbData = (uint)(section.Length * sizeof(char)), lpData = text };
            return SettingsSection.TryRead((nint)(&data), out string read) ? read : null;
        }
    }
}
