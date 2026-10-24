using System.Net;
using System.Text;
using Tomte.AI.Palette;
using Xunit;

namespace Tomte.Tests;

public sealed class MyMemoryTests
{
    private static string Fixture(string name) => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", name));

    [Fact]
    public void AnAnswerIsItsTranslatedText()
    {
        Assert.Equal(new MyMemory.Answer("Guten Morgen, wie geht es dir?", null), MyMemory.Parse(Fixture("mymemory-text.json")));
        Assert.Equal("第一行\n第二行", MyMemory.Parse(Fixture("mymemory-lines.json")).Text);
        Assert.Equal("It's", MyMemory.Parse("""{"responseData":{"translatedText":"It&#39;s"},"responseStatus":200}""").Text);
    }

    [Fact]
    public void ErrorsAndTheDailyLimitArePlainSentences()
    {
        var quota = MyMemory.Parse(Fixture("mymemory-quota.json"));
        Assert.Null(quota.Text);
        Assert.Equal(MyMemory.QuotaUsed, quota.Error);
        Assert.Equal(MyMemory.QuotaUsed, MyMemory.Parse("{}", HttpStatusCode.TooManyRequests).Error);

        Assert.Equal(new MyMemory.Answer(null, null, SameLanguage: true), MyMemory.Parse(Fixture("mymemory-same-language.json")));
        Assert.Equal("MyMemory said: Query length limit exceeded. max allowed query : 500 chars", MyMemory.Parse(Fixture("mymemory-too-long.json")).Error);
        Assert.StartsWith("MyMemory said: 'xx' is an invalid target language", MyMemory.Parse(Fixture("mymemory-bad-language.json")).Error);
        Assert.Equal("MyMemory answered with an error (HTTP 502).", MyMemory.Parse("<html>Bad gateway</html>", HttpStatusCode.BadGateway).Error);
        Assert.Equal("MyMemory brought nothing back.", MyMemory.Parse("""{"responseData":{"translatedText":""},"responseStatus":200}""").Error);
    }

    [Fact]
    public void TextGoesAParagraphAtATimeInPiecesThatFit()
    {
        var pieces = MyMemory.Pieces("Dear Sam,\nthanks.\n\n\nBest, Ana").ToList();
        Assert.Equal([("Dear Sam,\nthanks.", true), ("\n\n\n", false), ("Best, Ana", true)], pieces);

        // A long paragraph is cut after a sentence where one fits, else at a space, never inside a character.
        string sentences = string.Concat(Enumerable.Repeat("This is one sentence of the text. ", 40)).Trim();
        var cut = MyMemory.Pieces(sentences).ToList();
        Assert.All(cut.Where(p => p.Send), p => Assert.True(Encoding.UTF8.GetByteCount(p.Text) <= MyMemory.MaxBytes));
        Assert.All(cut.Where(p => p.Send), p => Assert.EndsWith(".", p.Text));
        Assert.Equal(sentences, string.Concat(cut.Select(p => p.Text)));

        string chinese = new('你', 400);
        var chunks = MyMemory.Pieces(chinese).ToList();
        Assert.Equal([166, 166, 68], chunks.Select(p => p.Text.Length));
        Assert.Equal(chinese, string.Concat(chunks.Select(p => p.Text)));
    }

    [Fact]
    public async Task EachParagraphIsOneRequestAndTheBlankLinesStay()
    {
        using var http = RecordedHandler.Client(out var handler, "mymemory-text.json");
        var answer = await MyMemory.TranslateAsync(http, "Good morning, how are you?", "de", CancellationToken.None);
        Assert.Equal("Guten Morgen, wie geht es dir?", answer.Text);
        Assert.Equal("https://api.mymemory.translated.net/get?q=Good%20morning%2C%20how%20are%20you%3F&langpair=autodetect%7Cde", handler.Request!.RequestUri!.AbsoluteUri);

        using var two = new HttpClient(new RecordedHandler("mymemory-text.json", then: "mymemory-same-language.json"));
        var mixed = await MyMemory.TranslateAsync(two, "Good morning, how are you?\n\nGuten Tag", "de", CancellationToken.None);
        Assert.Equal("Guten Morgen, wie geht es dir?\n\nGuten Tag", mixed.Text);

        using var same = new HttpClient(new RecordedHandler("mymemory-same-language.json"));
        Assert.True((await MyMemory.TranslateAsync(same, "Guten Tag", "de", CancellationToken.None)).SameLanguage);

        // The limit ends it at once: the second paragraph is not sent.
        var quota = new RecordedHandler("mymemory-quota.json");
        using var limited = new HttpClient(quota);
        Assert.Equal(MyMemory.QuotaUsed, (await MyMemory.TranslateAsync(limited, "One.\n\nTwo.", "de", CancellationToken.None)).Error);
        Assert.Single(quota.Bodies);
    }

    [Fact]
    public async Task NoConnectionAndNoAnswerInTimeSaySo()
    {
        using var down = new HttpClient(new Failing(new HttpRequestException("no route")));
        Assert.Equal("MyMemory could not be reached. Check the connection.", (await MyMemory.TranslateAsync(down, "Hello", "de", CancellationToken.None)).Error);
        using var slow = new HttpClient(new Failing(new TaskCanceledException()));
        Assert.Equal("MyMemory did not answer in time.", (await MyMemory.TranslateAsync(slow, "Hello", "de", CancellationToken.None)).Error);
    }

    private sealed class Failing(Exception e) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct) => Task.FromException<HttpResponseMessage>(e);
    }
}
