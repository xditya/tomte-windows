using System.Net;
using System.Text.Json;
using Tomte.AI;
using Tomte.AI.Jobs;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Device;
using Xunit;

namespace Tomte.Tests;

public sealed class WebToolsTests
{
    private static ToolCallPart Call(string json) => new("1", BuiltInTools.FetchUrl, JsonDocument.Parse(json).RootElement);

    [Fact]
    public void ThePageTextLeavesOutScriptsStylesAndTags()
    {
        const string html = """
            <html><head><title>Plan &amp; notes</title><style>body { color: red }</style>
            <script type="text/javascript">var secret = "<p>not text</p>";</script></head>
            <body><!-- hidden --><h1>Hello</h1><p>One   two<br>three</p><noscript>Turn on JS</noscript>
            <ul><li>a</li><li>b&nbsp;c</li></ul><SCRIPT>alert(1)</SCRIPT><span>end</span></body></html>
            """;

        Assert.Equal("Plan & notes\nHello\nOne two\nthree\na\nb c\nend", WebTools.ToText(html));
    }

    [Fact]
    public void FindGivesShortPiecesAroundEachMatchOnce()
    {
        // The first two matches sit in the same text, so they make one piece.
        string repeated = new string('-', 200) + "\"label\":\"1.23K Subscribers\"";
        string source = repeated + repeated + new string('-', 200) + "4 subscribers";

        string found = WebTools.Find(source, "subscribers");

        Assert.StartsWith("2 pieces around \"subscribers\", in page order:\n", found);
        Assert.Contains("1.23K Subscribers", found);
        Assert.EndsWith("4 subscribers ...", found);
        Assert.All(found.Split('\n').Skip(1), line => Assert.True(line.Length <= 2 * WebTools.SnippetRadius + "subscribers".Length + 8));
        Assert.Equal("\"likes\" is not in the page (6 characters).", WebTools.Find("abcdef", "likes"));
        // However many matches there are, the model gets at most MaxSnippets pieces.
        string many = string.Concat(Enumerable.Range(0, 50).Select(i => $"{new string('-', 200)}hit {i}"));
        Assert.Equal(WebTools.MaxSnippets, WebTools.Find(many, "hit").Split('\n').Length - 1);
    }

    [Fact]
    public void OnlyHttpAndHttpsAreFetched()
    {
        Assert.Null(BuiltInTools.Validate(BuiltInTools.FetchUrl, Call("""{"url":"https://www.youtube.com/@Google"}""").Input));
        Assert.Null(BuiltInTools.Validate(BuiltInTools.FetchUrl, Call("""{"url":"http://example.com","find":"x"}""").Input));
        foreach (var url in new[] { "file:///C:/Windows/win.ini", "ftp://example.com/a", "example.com", "" })
            Assert.NotNull(BuiltInTools.Validate(BuiltInTools.FetchUrl, Call($$"""{"url":"{{url}}"}""").Input));
        Assert.Equal("fetch_url: \"find\" must be a non-empty string.", BuiltInTools.Validate(BuiltInTools.FetchUrl, Call("""{"url":"https://a.b","find":3}""").Input));
    }

    [Fact]
    public void FetchingAsksBeforeItRunsAndMayRunInAJob()
    {
        Assert.Equal(ActionTier.ExternalEffect, BuiltInTools.Tiers[BuiltInTools.FetchUrl]);
        Assert.True(PermissionGrants.CanAlwaysAllow(BuiltInTools.Tiers[BuiltInTools.FetchUrl]));
        Assert.True(BuiltInTools.AllowedInScheduledJobs(BuiltInTools.FetchUrl));
        Assert.True(BuiltInTools.RunsOffThread(BuiltInTools.FetchUrl));
        Assert.Contains(JobPlanner.JobTools, t => t.Name == BuiltInTools.FetchUrl);
        Assert.Contains(BuiltInTools.Definitions, t => t.Name == BuiltInTools.FetchUrl);
    }

    [Fact]
    public void AYouTubeChannelPageGivesItsSubscriberCount()
    {
        using var http = RecordedHandler.Client(out var handler, "youtube-channel.html");
        var tools = new BuiltInTools(new InMemoryShelf(), new InMemoryScratchpad(), _ => { }, _ => { }, web: http);

        var (result, undo) = tools.Run(Call("""{"url":"https://www.youtube.com/@Google","find":"subscribers"}"""));

        Assert.False(result.IsError);
        Assert.Null(undo);
        Assert.StartsWith(WebTools.Untrusted, result.Content);
        // The channel's own count, from the script, with YouTube's invisible direction marks taken out.
        Assert.Contains("@Google • 14.6M subscribers", result.Content);
        Assert.Contains("\"content\":\"14.6M subscribers\"", result.Content);
        Assert.Contains("30.4K subscribers", result.Content);
        Assert.Equal(HttpMethod.Get, handler.Request!.Method);
        Assert.Contains("Mozilla/5.0", handler.Request.Headers.UserAgent.ToString());
        Assert.Equal("en", handler.Request.Headers.AcceptLanguage.ToString());
        Assert.Equal("CONSENT=YES+", Assert.Single(handler.Request.Headers.GetValues("Cookie")));

        // Without find, the text of the page: none of its script.
        var (text, _) = tools.Run(Call("""{"url":"https://www.youtube.com/@Google"}"""));
        Assert.Equal(WebTools.Untrusted + "\n\nGoogle - YouTube\nGoogle", text.Content);
    }

    [Fact]
    public void AFailedFetchIsAnErrorForTheModel()
    {
        using var http = new HttpClient(new RecordedHandler("html-page.html", HttpStatusCode.NotFound));
        var tools = new BuiltInTools(new InMemoryShelf(), new InMemoryScratchpad(), _ => { }, _ => { }, web: http);

        var (result, _) = tools.Run(Call("""{"url":"https://example.com/gone"}"""));

        Assert.True(result.IsError);
        Assert.Equal("example.com answered HTTP 404.", result.Content);
        Assert.Equal("Reading web pages is not available here.",
            new BuiltInTools(new InMemoryShelf(), new InMemoryScratchpad(), _ => { }, _ => { }).Run(Call("""{"url":"https://example.com"}""")).Result.Content);
    }
}
