using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Text;
using Xunit;

namespace Tomte.Tests;

public sealed class ScreenTextTests : IDisposable
{
    private static readonly ProviderConfig Provider = new("p", ProviderKind.OpenAICompatible, "http://localhost", "m");
    private readonly string logPath = Path.Combine(Path.GetTempPath(), $"tomte-screen-text-{Guid.NewGuid():N}", "send-log.jsonl");

    public void Dispose()
    {
        if (Directory.Exists(Path.GetDirectoryName(logPath)))
            Directory.Delete(Path.GetDirectoryName(logPath)!, recursive: true);
    }

    [Fact]
    public void LinesKeepTheirBreaksAndAWideGapIsABlankLine()
    {
        // 20 tall lines 6 apart are one paragraph; the 30 gap before the third starts another.
        string text = ScreenText.Join([(" Dear Sam, ", 0, 20), ("thanks for the files.", 26, 46), ("Best, Ana", 76, 96)]);
        Assert.Equal("Dear Sam,\nthanks for the files.\n\nBest, Ana", text);
        Assert.Equal("", ScreenText.Join([]));
    }

    [Theory]
    [InlineData(400, 60, 1.0, 2.0)]
    [InlineData(400, 60, 1.5, 2 / 1.5)]
    [InlineData(400, 60, 2.0, 1.0)]
    [InlineData(3000, 200, 1.0, 2.0)]
    [InlineData(8000, 200, 1.0, 1.25)]
    [InlineData(20000, 200, 1.0, 0.5)]
    public void SmallTextIsScaledUpButNeverPastWhatTheEngineTakes(int width, int height, double scale, double factor) =>
        Assert.Equal(factor, ScreenText.Factor(width, height, scale, 10000), 6);

    [Fact]
    public void NothingIsSentWithTheSettingOffOrNoProvider()
    {
        Assert.False(ScreenText.CleansUp(new TomteSettings { Provider = Provider }));
        Assert.False(ScreenText.CleansUp(new TomteSettings { CleanUpScreenText = true }));
        Assert.True(ScreenText.CleansUp(new TomteSettings { CleanUpScreenText = true, Provider = Provider }));
    }

    [Fact]
    public async Task TheCleanupSendsOnlyTheTextWithNoTools()
    {
        var provider = new ScriptedProvider();
        provider.Turns.Add(ScriptedProvider.Text(10, 5, "```\nHello world\n```"));
        var session = ScreenText.Session("He11o wor1d", provider, Provider, new PaletteOptions(), new SendLog(logPath));

        await session.SendAsync();

        var request = Assert.Single(provider.Requests);
        Assert.Empty(request.Tools ?? []);
        var part = Assert.IsType<TextPart>(Assert.Single(Assert.Single(request.Messages).Parts));
        Assert.Contains("<text>\nHe11o wor1d\n</text>", part.Text);
        Assert.Equal("Hello world", ScreenText.Cleaned(session.AnswerText));
        Assert.StartsWith("Sent to", session.ProviderIndicator);
        Assert.Single(new SendLog(logPath).ReadAll());
    }

    [Fact]
    public async Task TheTranslationSendsOnlyTheTextWithNoTools()
    {
        var provider = new ScriptedProvider();
        provider.Turns.Add(ScriptedProvider.Text(10, 5, "Hallo Welt"));
        var session = ScreenText.Session("Hello world", provider, Provider, new PaletteOptions(), new SendLog(logPath), translateTo: "German");

        await session.SendAsync();

        var request = Assert.Single(provider.Requests);
        Assert.Empty(request.Tools ?? []);
        var part = Assert.IsType<TextPart>(Assert.Single(Assert.Single(request.Messages).Parts));
        Assert.StartsWith("Translate the text below", part.Text);
        Assert.Contains("into German", part.Text);
        Assert.EndsWith("<text>\nHello world\n</text>", part.Text);
        Assert.Equal("Hallo Welt", ScreenText.Cleaned(session.AnswerText));
    }

    [Fact]
    public void TranslateIsOfferedWithAProviderAndMarksTheAIsText()
    {
        var offered = ScreenText.Offer(new TomteSettings { Provider = Provider }, ScreenText.Copied("one"));
        Assert.Equal([ScreenText.Translate, ScreenText.Copy, DropResult.Close], offered.Choices);
        Assert.Equal(ScreenText.Copied("one").Line, offered.Line);

        var translated = ScreenText.Translated("eins", "German", "Sent to p · m");
        Assert.Equal("Text from screen · AI translated", translated.Title);
        Assert.Contains("translated into German by AI", translated.Line);
        Assert.Equal([ScreenText.CopyOriginal, ScreenText.Copy, DropResult.Close], translated.Choices);

        Assert.Equal("German", ScreenText.LanguageName("de"));
        Assert.Equal(System.Globalization.CultureInfo.CurrentUICulture.EnglishName, ScreenText.LanguageName(""));
        Assert.Equal("", new TomteSettings().TranslateScreenTextTo);
    }

    [Fact]
    public void WithoutAProviderTranslateGoesToMyMemoryAndTheCardSaysSoFirst()
    {
        var offered = ScreenText.Offer(new TomteSettings(), ScreenText.Copied("Guten Tag"));
        Assert.Equal([ScreenText.Translate, ScreenText.Copy, DropResult.Close], offered.Choices);
        Assert.EndsWith("· Translate sends it to MyMemory", offered.Line);
        Assert.Equal("Nothing sent", offered.Indicator);

        Assert.Equal(ScreenText.Translator.Provider, ScreenText.TranslatorFor(new TomteSettings { Provider = Provider }, new string('a', 9000)));
        Assert.Equal(ScreenText.Translator.MyMemory, ScreenText.TranslatorFor(new TomteSettings(), new string('a', MyMemory.DailyChars)));
        Assert.Equal(ScreenText.Translator.Browser, ScreenText.TranslatorFor(new TomteSettings(), new string('a', MyMemory.DailyChars + 1)));

        // Past MyMemory's daily allowance the browser is offered instead, and the card says what goes to Google.
        var longer = ScreenText.Offer(new TomteSettings(), ScreenText.Copied(new string('a', MyMemory.DailyChars + 1)));
        Assert.Equal([ScreenText.TranslateInBrowser, ScreenText.Copy, DropResult.Close], longer.Choices);
        Assert.Contains("· Translate in browser sends the first part to Google", longer.Line);

        var failed = ScreenText.NotTranslated("Guten Tag", "Sent to MyMemory", MyMemory.QuotaUsed);
        Assert.Equal([ScreenText.TranslateInBrowser, ScreenText.Copy, DropResult.Close], failed.Choices);
        Assert.Contains("Google", failed.Line);

        var translated = ScreenText.Translated("Good day", "English", "Sent to MyMemory", MyMemory.Name);
        Assert.Equal("Text from screen · translated by MyMemory", translated.Title);
        Assert.Contains("translated into English by MyMemory", translated.Line);
        Assert.Contains("keeps the text", ScreenText.TranslatingWithMyMemory("Guten Tag", "English").Line);
    }

    [Fact]
    public void TheBrowserAddressCarriesTheLanguageAndTheEscapedText()
    {
        var (url, cut) = ScreenText.BrowserTranslation("Fish & chips = 5 €?\nYes #1", "de");
        Assert.False(cut);
        Assert.Equal("https://translate.google.com/?sl=auto&tl=de&text=Fish%20%26%20chips%20%3D%205%20%E2%82%AC%3F%0AYes%20%231&op=translate", url);
        Assert.Contains("&tl=zh-CN&", ScreenText.BrowserTranslation("x", "zh-Hans").Url);
        Assert.Contains("&tl=zh-TW&", ScreenText.BrowserTranslation("x", "zh-Hant").Url);
        Assert.Contains("&tl=no&", ScreenText.BrowserTranslation("x", "nb").Url);
        Assert.Contains("&tl=pt&", ScreenText.BrowserTranslation("x", "pt").Url);
    }

    [Fact]
    public void LongTextIsCutForTheBrowserAtAWordAndTheCardSaysSo()
    {
        // 6,000 characters of words: cut before 5,000, at the end of a word.
        string text = string.Concat(Enumerable.Repeat("word ", 1200));
        var (url, cut) = ScreenText.BrowserTranslation(text, "fr");
        Assert.True(cut);
        string sent = Uri.UnescapeDataString(url.Split("&text=")[1].Split("&op=")[0]);
        Assert.True(sent.Length <= ScreenText.BrowserMaxChars);
        Assert.EndsWith("word", sent);
        Assert.StartsWith("word word", sent);
        Assert.Contains("sends the first part to Google", ScreenText.Offer(new TomteSettings(), ScreenText.Copied(text)).Line);

        // Without spaces, as in Chinese, the cut is at the limit of the escaped address, never inside a character.
        string chinese = new('你', 3000);
        (url, cut) = ScreenText.BrowserTranslation(chinese, "en");
        sent = Uri.UnescapeDataString(url.Split("&text=")[1].Split("&op=")[0]);
        Assert.True(cut);
        Assert.Equal(ScreenText.BrowserMaxEscaped / 9, sent.Length);

        Assert.False(ScreenText.BrowserTranslation(new string('a', ScreenText.BrowserMaxChars), "en").Cut);
    }

    [Theory]
    [InlineData("https://example.com/a_b?c=1", true)]
    [InlineData("HTTP://EXAMPLE.COM", true)]
    [InlineData("WIFI:S:home;T:WPA;P:secret;;", false)]
    [InlineData("javascript:alert(1)", false)]
    [InlineData("file:///C:/Windows/notepad.exe", false)]
    [InlineData("example.com", false)]
    [InlineData("just some text", false)]
    public void OnlyAWebLinkInAQrCodeCanBeOpened(string content, bool link)
    {
        var card = ScreenText.QrCode(content);
        Assert.Equal("QR code", card.Title);
        Assert.Equal(content, card.Body);
        Assert.Equal(link, card.Choices.Contains(ScreenText.Open));
        Assert.Contains(ScreenText.Copy, card.Choices);
        // Close stays the default, so Enter never opens a link.
        Assert.Equal(DropResult.Close, card.Choices[^1]);
    }

    [Fact]
    public void AQrCodeInThePixelsIsRead()
    {
        // Drawn 4 pixels a module with a quiet zone, as BGRA on white.
        var matrix = new ZXing.QrCode.QRCodeWriter().encode("https://example.com/hello", ZXing.BarcodeFormat.QR_CODE, 200, 200);
        var pixels = new byte[4 * matrix.Width * matrix.Height];
        for (int y = 0; y < matrix.Height; y++)
        {
            for (int x = 0; x < matrix.Width; x++)
            {
                byte v = matrix[x, y] ? (byte)0 : (byte)255;
                pixels.AsSpan(4 * (y * matrix.Width + x), 3).Fill(v);
                pixels[(4 * (y * matrix.Width + x)) + 3] = 255;
            }
        }

        Assert.Equal("https://example.com/hello", ScreenText.DecodeQr(pixels, matrix.Width, matrix.Height));
        Assert.Null(ScreenText.DecodeQr([.. Enumerable.Repeat((byte)255, 4 * 64 * 64)], 64, 64));
    }

    [Fact]
    public void TheCardsSayWhereTheTextWentAndMarkTheAIsText()
    {
        var copied = ScreenText.Copied("one\n\ntwo").Closable();
        Assert.Equal(("Nothing sent", "Copied · 2 lines · read on this PC"), (copied.Indicator, copied.Line));
        Assert.Equal([ScreenText.Copy, DropResult.Close], copied.Choices);
        Assert.EndsWith("AI cleanup needs a provider in Settings", ScreenText.Copied("one", noProvider: true).Line);

        var cleaned = ScreenText.CleanedUp("one", "Sent to p · m");
        Assert.Equal("Text from screen · AI cleaned", cleaned.Title);
        Assert.Contains("cleaned up by AI", cleaned.Line);
        Assert.Equal([ScreenText.CopyOriginal, ScreenText.Copy, DropResult.Close], cleaned.Choices);

        // No text: the problem takes the answer's place and the card offers Close.
        var none = ScreenText.NoText.Closable();
        Assert.True(none.Problem);
        Assert.Equal([DropResult.Close], none.Choices);
    }

    [Fact]
    public void TheCardShowsEveryLineWithItsChoicesBelow()
    {
        var layout = new DropResultLayout(ScreenText.CleanedUp("first line\nsecond line\nthird line", "Sent to p · m"), (float)PanelSize.PaletteWidth, mascot: true);

        using var body = TextLayout.Of(Assert.Single(layout.Body).Run);
        Assert.Equal(3, body.LineCount);
        Assert.Equal(3, layout.Choices.Count);
        Assert.True(layout.Band.Bottom <= layout.Choices[0].Y);
        Assert.Equal(["choice:0", "choice:1", "choice:2"], layout.Targets(files: 0).Select(t => t.Id));
        Assert.InRange(layout.Height, PanelSize.PaletteMinHeight, PanelSize.PaletteMaxHeight);
    }
}
