using Tomte.Core;
using Tomte.Core.Search;
using Xunit;

namespace Tomte.Tests;

public sealed class WebSearchTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "tomte-tests-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData("example.com", "https://example.com/")]
    [InlineData("Example.COM/docs?q=1", "https://example.com/docs?q=1")]
    [InlineData("docs.example.co.uk/a/b", "https://docs.example.co.uk/a/b")]
    [InlineData("https://example.com/search?q=a b", null)] // a space: words, not an address
    [InlineData("https://example.com/search?q=a%20b", "https://example.com/search?q=a%20b")]
    [InlineData("HTTP://example.org", "http://example.org/")]
    [InlineData("localhost:3000", "http://localhost:3000/")]
    [InlineData("localhost:3000/api", "http://localhost:3000/api")]
    [InlineData("http://localhost:5173", "http://localhost:5173/")]
    [InlineData("192.168.1.1:8080/admin", "http://192.168.1.1:8080/admin")]
    [InlineData("c#", null)]
    [InlineData("a.b c", null)]
    [InlineData("file.txt", null)] // txt is no web ending
    [InlineData("readme.md", null)] // md is Moldova, and a file far more often
    [InlineData("node.js", null)]
    [InlineData("v1.2", null)]
    [InlineData("1.2.3", null)]
    [InlineData("999.1.1.1", null)]
    [InlineData("user@example.com", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("ftp://example.com", null)]
    [InlineData("ms-settings:display", null)]
    [InlineData("example.com:abc", null)]
    [InlineData("-bad.com", null)]
    [InlineData("https://", null)]
    [InlineData("", null)]
    public void OnlyAWebAddressOpensAndEverythingElseIsSearched(string typed, string? opens) =>
        Assert.Equal(opens, WebSearch.Address(typed)?.AbsoluteUri);

    [Theory]
    [InlineData(WebSearchEngine.Google, "https://www.google.com/search?q=c%23%20%26%20m%C3%A1s%3F")]
    [InlineData(WebSearchEngine.Bing, "https://www.bing.com/search?q=c%23%20%26%20m%C3%A1s%3F")]
    [InlineData(WebSearchEngine.DuckDuckGo, "https://duckduckgo.com/?q=c%23%20%26%20m%C3%A1s%3F")]
    public void EachEngineGetsTheTextAsOneEscapedValue(WebSearchEngine engine, string url) =>
        Assert.Equal(url, WebSearch.SearchUri(engine, "  c# & más? ").AbsoluteUri);

    [Fact]
    public void TheRowSaysWhetherItSearchesOrOpens()
    {
        var search = WebSearch.Row("c#", WebSearchEngine.DuckDuckGo);
        Assert.Equal((SearchKind.Web, "Search DuckDuckGo", "https://duckduckgo.com/?q=c%23", "\"c#\""), (search.Kind, search.Name, search.Target, search.Detail));
        var open = WebSearch.Row("open example.com", WebSearchEngine.Google);
        Assert.Equal(("Open in browser", "https://example.com/"), (open.Name, open.Target));
        Assert.Equal("Web", SearchLayout.KindLabel(open));
        Assert.False(SearchLayout.HasFolder(open));
    }

    [Fact]
    public void OnlyWebAddressesOpen()
    {
        Assert.False(WebSearch.Open("file:///C:/Windows/notepad.exe").Ok);
        Assert.False(WebSearch.Open("calc").Ok);
        Assert.False(WebSearch.Open("ms-settings:display").Ok);
    }

    [Fact]
    public void GoogleIsTheDefaultAndAChoiceIsKept()
    {
        Assert.Equal(WebSearchEngine.Google, new TomteSettings().WebSearch);
        string file = Path.Combine(dir, "settings.json");
        using (var store = new SettingsStore(file))
        {
            store.Save(store.Current with { WebSearch = WebSearchEngine.Bing });
        }

        Assert.Contains("\"webSearch\": \"Bing\"", File.ReadAllText(file), StringComparison.Ordinal);
        Assert.Equal(WebSearchEngine.Bing, SettingsStore.Load(file).WebSearch);
    }

    [Fact]
    public void OpenInBrowserComesAfterFilesAndBeforeTheAiRows()
    {
        var items = new[]
        {
            new SearchItem(SearchKind.AiSearch, SearchLayout.AiRow, "report"),
            new SearchItem(SearchKind.Ask, "Ask Tomte", "report"),
            WebSearch.Row("report", WebSearchEngine.Google),
            new SearchItem(SearchKind.GetEverything, "Search every drive", ""),
            new FileHit(@"D:\report.pdf", false).Item(),
            new SearchItem(SearchKind.Shortcut, "Reports folder", "3"),
            new SearchItem(SearchKind.Place, "Report a problem", "ms-settings:troubleshoot", "System"),
            new SearchItem(SearchKind.App, "Report Builder", @"shell:AppsFolder\rb"),
        };
        var kinds = SearchRank.Order(items, "report", new Dictionary<string, Launches>()).Select(r => r.Kind);
        Assert.Equal([SearchKind.App, SearchKind.Place, SearchKind.Shortcut, SearchKind.File, SearchKind.GetEverything, SearchKind.Web, SearchKind.Ask, SearchKind.AiSearch], kinds);
    }

    [Fact]
    public void AWebAddressPutsOpenInBrowserFirst()
    {
        var none = new Dictionary<string, Launches>();
        var app = new SearchItem(SearchKind.App, "Example.com Helper", @"shell:AppsFolder\em");
        var file = new FileHit(@"C:\go\mod\example.com_v1.0.0.txt", false).Item();
        var rows = SearchRank.Order([app, file, WebSearch.Row("example.com", WebSearchEngine.Google)], SearchRank.Query("example.com"), none);
        Assert.Equal([SearchKind.Web, SearchKind.App, SearchKind.File], rows.Select(r => r.Kind));
        Assert.Equal(SearchKind.Web, SearchRank.Order([app, file, WebSearch.Row("open example.com", WebSearchEngine.Google)], SearchRank.Query("open example.com"), none)[0].Kind);
        // Words, not an address: the app stays first and the web row after the files.
        rows = SearchRank.Order([app, file, WebSearch.Row("example", WebSearchEngine.Google)], "example", none);
        Assert.Equal([SearchKind.App, SearchKind.File, SearchKind.Web], rows.Select(r => r.Kind));
        // A file named exactly what was typed stays first once the files arrive.
        var makefile = new FileHit(@"D:\src\Makefile.in", false).Item();
        rows = SearchRank.Order([makefile, WebSearch.Row("Makefile.in", WebSearchEngine.Google)], "Makefile.in", none);
        Assert.Equal([SearchKind.File, SearchKind.Web], rows.Select(r => r.Kind));
    }

    [Fact]
    public void TabGoesRoundTheSectionsAndShiftTabGoesBack()
    {
        var rows = SearchRank.Order(
            [
                new SearchItem(SearchKind.App, "Report Builder", "a"), new SearchItem(SearchKind.App, "Reporter", "b"),
                new FileHit(@"D:\report.pdf", false).Item(), WebSearch.Row("report", WebSearchEngine.Google),
                new SearchItem(SearchKind.Ask, "Ask Tomte", "report"), new SearchItem(SearchKind.AiSearch, SearchLayout.AiRow, "report"),
            ],
            "report",
            new Dictionary<string, Launches>());
        // Sections start at rows 0 (apps), 2 (files), 3 (Open in browser) and 4 (Ask Tomte, then Search with AI).
        Assert.Equal([2, 3, 4, 0], new[] { 0, 2, 3, 4 }.Select(at => SearchLayout.NextSection(rows, at, 1)));
        Assert.Equal(4, SearchLayout.NextSection(rows, 0, -1));
        Assert.Equal(3, SearchLayout.NextSection(rows, 4, -1));
        // From inside a section, Shift+Tab goes to that section's first row first.
        Assert.Equal(4, SearchLayout.NextSection(rows, 5, -1));
        Assert.Equal(0, SearchLayout.NextSection(rows, 1, -1));
        Assert.Equal(0, SearchLayout.NextSection(rows, 5, 1));
    }
}
