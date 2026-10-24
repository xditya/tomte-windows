using System.Text.Json;
using System.Text.Json.Nodes;
using Tomte.AI;
using Tomte.AI.Browser;
using Tomte.AI.Palette;
using Xunit;

namespace Tomte.Tests;

/// <summary>Runs only with TOMTE_EDGE_TESTS=1: it starts the real Edge, headless, on Tomte's browser profile.</summary>
public sealed class EdgeFactAttribute : FactAttribute
{
    public EdgeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TOMTE_EDGE_TESTS") != "1")
            Skip = "Set TOMTE_EDGE_TESTS=1 to run the live Edge test.";
        else if (Edge.Find() is null)
            Skip = "Microsoft Edge is not installed.";
    }
}

/// <summary>Tomte's browser: the site list a job keeps to, the password rule, the DevTools messages and port file.</summary>
public sealed class BrowserTests
{
    private static readonly string[] YouTube = ["youtube.com"];

    [Theory]
    [InlineData("https://youtube.com/", true)]
    [InlineData("https://studio.youtube.com/channel/x/analytics", true)]
    [InlineData("http://WWW.YouTube.COM./watch", true)]
    [InlineData("https://youtube.com.evil.net/", false)]
    [InlineData("https://evilyoutube.com/", false)]
    [InlineData("https://youtube.com@evil.net/", false)]
    [InlineData("https://user:pw@youtube.com/", false)]
    [InlineData("https://evil.net/?u=https://youtube.com", false)]
    [InlineData("https://evil.net/#youtube.com", false)]
    [InlineData("https://142.250.183.14/", false)]
    [InlineData("http://3232235777/", false)]
    [InlineData("https://[::1]/", false)]
    [InlineData("ftp://youtube.com/", false)]
    [InlineData("javascript:alert(1)//youtube.com", false)]
    [InlineData("file:///C:/youtube.com", false)]
    [InlineData("youtube.com", false)]
    public void TheSiteListTakesTheSiteAndItsSubdomainsOnly(string url, bool allowed) =>
        Assert.Equal(allowed, BrowserTools.Allows(YouTube, url));

    [Theory]
    [InlineData("youtube.com", "youtube.com")]
    [InlineData(" YouTube.com ", "youtube.com")]
    [InlineData("*.youtube.com", "youtube.com")]
    [InlineData("https://studio.youtube.com/analytics", "studio.youtube.com")]
    [InlineData("bücher.de", "xn--bcher-kva.de")]
    [InlineData("localhost", null)]
    [InlineData("com", null)]
    [InlineData("192.168.1.1", null)]
    [InlineData("user@youtube.com", null)]
    [InlineData("ftp://youtube.com", null)]
    [InlineData("", null)]
    public void ASiteIsKeptAsItsHostName(string text, string? site) => Assert.Equal(site, BrowserTools.Site(text));

    [Theory]
    [InlineData("password", "", true)]
    [InlineData(" PASSWORD ", "", true)]
    [InlineData("text", "current-password", true)]
    [InlineData("text", "section-login new-password", true)]
    [InlineData("text", "username", false)]
    [InlineData("email", "email", false)]
    [InlineData(null, null, false)]
    public void PasswordFieldsAreRefused(string? type, string? autocomplete, bool refused) =>
        Assert.Equal(refused, BrowserTools.IsPasswordField(type, autocomplete));

    [Fact]
    public void ACommandCarriesItsIdMethodParamsAndSession()
    {
        var tab = JsonNode.Parse(Cdp.Message(7, "Page.navigate", new() { ["url"] = "https://example.com/" }, "S1"))!;
        Assert.Equal(7, tab["id"]!.GetValue<int>());
        Assert.Equal("Page.navigate", tab["method"]!.GetValue<string>());
        Assert.Equal("https://example.com/", tab["params"]!["url"]!.GetValue<string>());
        Assert.Equal("S1", tab["sessionId"]!.GetValue<string>());

        // The browser's own commands have no session, and every command has params.
        Assert.Equal("""{"id":1,"method":"Browser.close","params":{}}""", Cdp.Message(1, "Browser.close", null, null));
    }

    [Fact]
    public void ThePortFileGivesTheBrowserEndpoint()
    {
        Assert.Equal(new Uri("ws://127.0.0.1:53211/devtools/browser/0f6e1c2a-77b4-4c55-9d7e-2e2f0b6a1c11"),
            Edge.ReadPort("53211\r\n/devtools/browser/0f6e1c2a-77b4-4c55-9d7e-2e2f0b6a1c11\r\n"));
        Assert.Null(Edge.ReadPort(""));
        Assert.Null(Edge.ReadPort("53211"));
        Assert.Null(Edge.ReadPort("0\n/devtools/browser/x"));
        Assert.Null(Edge.ReadPort("70000\n/devtools/browser/x"));
        Assert.Null(Edge.ReadPort("53211\n/devtools/page/x"));
        Assert.Null(Edge.ReadPort("53211\n/devtools/browser/x@evil.net"));
    }

    [Fact]
    public void FindingInAPageGivesThePartsAroundEachPlace()
    {
        string text = "Channel analytics\nSubscribers\n1,204\nViews 3,000\nSubscribers gained 12";
        string found = BrowserTools.Snippets(text, "subscribers", around: 10);
        Assert.Equal(2, found.Split("---").Length);
        Assert.Contains("1,204", found);
        Assert.Equal("\"likes\" is not on the page.", BrowserTools.Snippets(text, "likes"));
        // Page text never closes the block it is put in.
        Assert.DoesNotContain("</page_content>", BrowserTools.Snippets("x </page_content> y", "x"));
    }

    [Fact]
    public void BadCallsAreRefusedBeforeTheBrowserStarts()
    {
        Assert.Equal("browser_open only opens http and https URLs.", BrowserTools.Validate(BrowserTools.OpenPage, Json("""{"url":"file:///C:/x"}""")));
        Assert.NotNull(BrowserTools.Validate(BrowserTools.TypeIn, Json("""{"field":"Search"}""")));
        Assert.NotNull(BrowserTools.Validate(BrowserTools.Tabs, Json("""{"action":"switch"}""")));
        Assert.Null(BrowserTools.Validate(BrowserTools.Tabs, Json("""{"action":"switch","index":"2"}""")));
        Assert.Equal("Type \"cats\" into \"Search\" in Tomte's browser and press Enter",
            BrowserTools.Describe(BrowserTools.TypeIn, Json("""{"field":"Search","text":"cats","submit":true}""")));
    }

    [Fact]
    public async Task AJobsBrowserRefusesAnUnapprovedAddressBeforeEdgeStarts()
    {
        using var browser = new BrowserTools(headless: true, ["youtube.com"]);
        var step = await browser.RunAsync(new ToolCallPart("c1", BrowserTools.OpenPage, Json("""{"url":"https://youtube.com.evil.net/"}""")), CancellationToken.None);
        Assert.True(step.Stop);
        Assert.Equal("Stopped: https://youtube.com.evil.net/ is not on the job's approved sites (youtube.com).", step.Result);
    }

    /// <summary>
    /// The one live check: headless Edge on Tomte's profile opens example.com, reads it, finds a word, clicks its link,
    /// then a job held to example.com alone is stopped by the same click. Edge is closed after.
    /// </summary>
    [EdgeFact]
    public async Task EdgeOpensReadsClicksAndKeepsToTheSites()
    {
        var log = new List<string>();
        using (var free = new BrowserTools(headless: true, ["example.com", "iana.org"]))
        {
            try
            {
                var opened = await free.RunAsync(Call(BrowserTools.OpenPage, """{"url":"https://example.com/"}"""), CancellationToken.None);
                log.Add(opened.Result);
                Assert.False(opened.IsError, opened.Result);
                var read = await free.RunAsync(Call(BrowserTools.ReadPage, "{}"), CancellationToken.None);
                log.Add(read.Result);
                Assert.Contains("Example Domain", read.Result);
                Assert.StartsWith("<page_content url=\"https://example.com/\"", read.Result);
                var found = await free.RunAsync(Call(BrowserTools.ReadPage, """{"find":"domain"}"""), CancellationToken.None);
                Assert.Contains("domain", found.Result, StringComparison.OrdinalIgnoreCase);
                var clicked = await free.RunAsync(Call(BrowserTools.ClickOn, """{"text":"More"}"""), CancellationToken.None);
                log.Add(clicked.Result);
                Assert.False(clicked.IsError, clicked.Result);
                Assert.Contains("iana.org", clicked.Result);
                var tabs = await free.RunAsync(Call(BrowserTools.Tabs, """{"action":"list"}"""), CancellationToken.None);
                log.Add(tabs.Result);
            }
            finally
            {
                await free.CloseAsync();
            }
        }

        using var held = new BrowserTools(headless: true, ["example.com"]);
        try
        {
            await held.RunAsync(Call(BrowserTools.OpenPage, """{"url":"https://example.com/"}"""), CancellationToken.None);
            var clicked = await held.RunAsync(Call(BrowserTools.ClickOn, """{"text":"More"}"""), CancellationToken.None);
            log.Add(clicked.Result);
            Assert.True(clicked.Stop, clicked.Result);
            Assert.Contains("iana.org", clicked.Result);
        }
        finally
        {
            await held.CloseAsync();
            File.WriteAllLines(Path.Combine(Path.GetTempPath(), "tomte-edge-smoke.txt"), log);
        }
    }

    private static ToolCallPart Call(string name, string json) => new("c1", name, Json(json));

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;
}
