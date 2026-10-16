using Tomte.Core.Text;
using Xunit;

namespace Tomte.Tests;

/// <summary>The Markdown that model answers use, read into blocks and bold spans.</summary>
public class MarkdownTests
{
    private static (MarkdownKind Kind, int Depth, string Marker, string Text)[] Shape(string text) =>
        [.. Markdown.Parse(text).Select(b => (b.Kind, b.Depth, b.Marker, b.Text))];

    [Fact]
    public void ABlankLineEndsAParagraphAndALineBreakStays()
    {
        Assert.Equal(
            [(MarkdownKind.Paragraph, 0, "", "One\ntwo"), (MarkdownKind.Paragraph, 0, "", "Three")],
            Shape("One\r\ntwo  \r\n\r\n\r\n  Three\n"));
        Assert.Empty(Markdown.Parse(""));
        Assert.Empty(Markdown.Parse("\n \n\t\n"));
    }

    [Fact]
    public void AHeadingIsABoldLineOfItsOwn()
    {
        var blocks = Markdown.Parse("## Q3 report\nRevenue went up.\n#hashtag\n####### seven");
        Assert.Equal([(MarkdownKind.Heading, 0, "", "Q3 report"), (MarkdownKind.Paragraph, 0, "", "Revenue went up.\n#hashtag\n####### seven")], blocks.Select(b => (b.Kind, b.Depth, b.Marker, b.Text)));
        Assert.Equal([(0, 9)], blocks[0].Bold);
        Assert.Empty(blocks[1].Bold);
    }

    [Fact]
    public void ListItemsKeepTheirMarkerAndTheirDepth()
    {
        Assert.Equal(
            [
                (MarkdownKind.Bullet, 0, "-", "a"),
                (MarkdownKind.Bullet, 1, "*", "b"),
                (MarkdownKind.Bullet, 2, "+", "c"),
                (MarkdownKind.Bullet, 1, "-", "d"),
                (MarkdownKind.Bullet, 0, "-", "e"),
            ],
            Shape("- a\n  * b\n    + c\n  - d\n- e"));
        // Four spaces a level reads the same.
        Assert.Equal([(MarkdownKind.Bullet, 0, "-", "a"), (MarkdownKind.Bullet, 1, "-", "b")], Shape("- a\n    - b"));
        Assert.Equal(
            [(MarkdownKind.Numbered, 0, "1.", "one"), (MarkdownKind.Numbered, 0, "2)", "two"), (MarkdownKind.Numbered, 0, "10.", "ten")],
            Shape("1. one\n2) two\n10. ten"));
        // Not items: no space after the marker, or a number without a dot.
        Assert.Equal([(MarkdownKind.Paragraph, 0, "", "-dash\n2024 was good\nbold start")], Shape("-dash\n2024 was good\n**bold** start"));
    }

    [Fact]
    public void AnItemTakesTheLinesUnderItAndTextAtTheMarginEndsTheList()
    {
        Assert.Equal([(MarkdownKind.Bullet, 0, "-", "first\nmore of it"), (MarkdownKind.Bullet, 0, "-", "second")], Shape("- first\n  more of it\n- second"));
        // After "Then" the old levels are gone: an indented item starts the first level again.
        Assert.Equal(
            [(MarkdownKind.Bullet, 0, "-", "a"), (MarkdownKind.Bullet, 1, "-", "b"), (MarkdownKind.Paragraph, 0, "", "Then"), (MarkdownKind.Bullet, 0, "-", "c")],
            Shape("- a\n  - b\n\nThen\n  - c"));
    }

    [Fact]
    public void BoldNeedsBothMarkersOnOneLineAgainstItsText()
    {
        var block = Assert.Single(Markdown.Parse("a **b c** d **e**"));
        Assert.Equal("a b c d e", block.Text);
        Assert.Equal([(2, 3), (8, 1)], block.Bold);
        Assert.Equal([false, true, false, true], block.Spans.Select(s => s.Bold));

        // Left as written: not closed, around spaces, no opening marker, and across a line break.
        foreach (string text in new[] { "**open", "a ** b ** c", "2**3", "**one\nline**" })
        {
            var plain = Assert.Single(Markdown.Parse(text));
            Assert.Equal(text, plain.Text);
            Assert.Empty(plain.Bold);
        }

        var item = Assert.Single(Markdown.Parse("- **Cost:** flat"));
        Assert.Equal(("Cost: flat", (0, 5)), (item.Text, Assert.Single(item.Bold)));
    }

    [Fact]
    public void AnAnswerReadsItalicAndCodeButNotTheNotesOtherMarkers()
    {
        var block = Assert.Single(Markdown.Parse("a *b* `c **d**` ***e***"));
        Assert.Equal("a b c **d** e", block.Text);
        Assert.Equal([(2, 1), (12, 1)], block.Italic);
        Assert.Equal([(4, 7)], block.Code);
        Assert.Equal([(12, 1)], block.Bold);

        // Left as written: a * before a space, a * never closed, and what only the note reads.
        foreach (string text in new[] { "5 * 3", "2*", "_x_ snake_case ~~y~~ [a](b)" })
        {
            var plain = Assert.Single(Markdown.Parse(text));
            Assert.Equal(text, plain.Text);
            Assert.All(plain.Spans, s => Assert.Equal(MarkdownLook.Plain, s.Look));
        }
    }

    [Theory]
    // A marker not closed yet waits with what comes after it on its line.
    [InlineData("The total is **$3,", "The total is ")]
    [InlineData("Run `npm i", "Run ")]
    [InlineData("- **Cost", "- ")]
    [InlineData("# **Ti", "# ")]
    // A run at the very end may be half of a marker, an opener or a closer.
    [InlineData("The total is **", "The total is ")]
    [InlineData("**bold*", "")]
    [InlineData("5 *", "5 ")]
    // A list item's marker before its text would show as a dash or a number for a moment.
    [InlineData("Costs:\n-", "Costs:\n")]
    [InlineData("Steps:\n  12.", "Steps:\n")]
    [InlineData("Costs:\n- r", "Costs:\n- r")]
    // ** falls back to * while its closer has not come: the * left as text waits, with the run it starts.
    [InlineData("**Do *not* delete this file.**", "")]
    [InlineData("**a*b", "")]
    [InlineData("# **a*b** c", "# ")]
    // So bold around a * that is not a marker waits for its line to end.
    [InlineData("**5*3** and", "")]
    [InlineData("**5*3** and\nmore", "**5*3** and\nmore")]
    // Closed, or never a marker: shown as it is.
    [InlineData("The total is **$3,720** for", "The total is **$3,720** for")]
    [InlineData("5 * 3", "5 * 3")]
    // An earlier line is done: what it left open stays as text.
    [InlineData("a **b\nc", "a **b\nc")]
    // Code shows as it comes; only a fence coming in at the end waits.
    [InlineData("```\nx = 2*y", "```\nx = 2*y")]
    [InlineData("```\nx\n``", "```\nx\n")]
    public void AStreamingAnswerShowsAMarkerOnlyOnceItIsClosed(string text, string shown) => Assert.Equal(shown, Markdown.Settled(text));

    [Fact]
    public void CodeIsKeptAsItIsAndRulesGo()
    {
        Assert.Equal(
            [(MarkdownKind.Paragraph, 0, "", "Two tables."), (MarkdownKind.Code, 0, "", "A,B\n- not a bullet\n\n# not a heading"), (MarkdownKind.Paragraph, 0, "", "after")],
            Shape("Two tables.\n```csv\nA,B\n- not a bullet\n\n# not a heading\n```\nafter"));
        // A fence still streaming in is code to the end.
        Assert.Equal([(MarkdownKind.Code, 0, "", "x,y")], Shape("```\nx,y"));
        Assert.Equal([(MarkdownKind.Paragraph, 0, "", "a"), (MarkdownKind.Paragraph, 0, "", "b")], Shape("a\n---\nb\n* * *\n___"));
    }

    [Fact]
    public void EverySpanIsTheSourcesOwnTextAtItsPlace()
    {
        const string text = "# **Title**\r\n\r\nIntro with **bold** words\r\nand a second line.\r\n\r\n- one\r\n  - two **2**\r\n3. three\r\n\r\n```\r\ncode line\r\n```\r\n";
        var blocks = Markdown.Parse(text);
        Assert.Equal([MarkdownKind.Heading, MarkdownKind.Paragraph, MarkdownKind.Bullet, MarkdownKind.Bullet, MarkdownKind.Numbered, MarkdownKind.Code], blocks.Select(b => b.Kind));
        foreach (var block in blocks)
        {
            Assert.True(block.Source == 0 || text[block.Source - 1] == '\n', $"{block.Kind} starts mid-line");
            foreach (var span in block.Spans)
            {
                Assert.Equal(span.Text, text.Substring(span.Source, span.Text.Length));
            }
        }

        Assert.Equal(["Title", "Intro with bold words\nand a second line.", "one", "two 2", "three"], blocks.Take(5).Select(b => b.Text));
    }

    // The note's spans on one line as text and look.
    private static (string, MarkdownLook)[] Note(string line) =>
        [.. Markdown.Note(line, 0, line.Length, fenced: false).SelectMany(b => b.Spans).Select(s => (s.Text, s.Look))];

    [Fact]
    public void TheNoteReadsEmphasisWithEitherMarker()
    {
        Assert.Equal([("a ", MarkdownLook.Plain), ("b", MarkdownLook.Italic), (" ", MarkdownLook.Plain), ("c", MarkdownLook.Italic), (" ", MarkdownLook.Plain), ("d", MarkdownLook.Bold), (" ", MarkdownLook.Plain), ("e", MarkdownLook.Bold), (" ", MarkdownLook.Plain), ("f", MarkdownLook.Strike)],
            Note("a *b* _c_ **d** __e__ ~~f~~"));
        Assert.Equal([("both", MarkdownLook.Bold | MarkdownLook.Italic)], Note("***both***"));
        // Bold inside italic, and an italic run that skips the bold markers inside it.
        Assert.Equal([("a ", MarkdownLook.Italic), ("b", MarkdownLook.Italic | MarkdownLook.Bold), (" c", MarkdownLook.Italic)], Note("*a **b** c*"));

        // Left as written: not closed, around spaces, _ inside a word, one ~, and across lines.
        foreach (string text in new[] { "*open", "a * b * c", "snake_case_name", "~one~", "**one\nline**", "2*" })
        {
            Assert.All(Markdown.Note(text, 0, text.Length, false).SelectMany(b => b.Spans), s => Assert.Equal(MarkdownLook.Plain, s.Look));
        }
    }

    [Fact]
    public void TheNoteReadsCodeLinksAndAddresses()
    {
        // Nothing inside code is read; a run of two backticks is closed by two.
        Assert.Equal([("run ", MarkdownLook.Plain), ("**x** _y_", MarkdownLook.Code), (" and ", MarkdownLook.Plain), ("a`b", MarkdownLook.Code)], Note("run `**x** _y_` and ``a`b``"));
        Assert.Equal([("see ", MarkdownLook.Plain), ("the ", MarkdownLook.Link), ("docs", MarkdownLook.Link | MarkdownLook.Bold)], Note("see [the **docs**](https://e.x/a_b)"));
        Assert.Equal([("at ", MarkdownLook.Plain), ("https://e.x/a_b?c=1", MarkdownLook.Link), (".", MarkdownLook.Plain)], Note("at https://e.x/a_b?c=1."));
        // Not links: no address, a space in it, brackets alone, and a scheme inside a word.
        foreach (string text in new[] { "[a]()", "[a](b c)", "[x] done", "xhttps://e.x" })
        {
            Assert.All(Markdown.Note(text, 0, text.Length, false).SelectMany(b => b.Spans), s => Assert.Equal(MarkdownLook.Plain, s.Look));
        }
    }

    [Fact]
    public void TheNoteReadsTaskBoxesAsPartOfTheMarker()
    {
        const string text = "- [ ] open\n- [x] done\n3. [X] third\n- [y] not a box\n- [ ]";
        var blocks = Markdown.Note(text, 0, text.Length, false);
        Assert.Equal([("- [ ]", "open"), ("- [x]", "done"), ("3. [X]", "third"), ("-", "[y] not a box")], blocks.Select(b => (b.Marker, b.Text)));
        // The answers keep reading a box as text.
        Assert.Equal("[x] done", Assert.Single(Markdown.Parse("- [x] done")).Text);
    }

    [Fact]
    public void TheNoteReadsARangeOfLinesWhereTheyAre()
    {
        const string text = "# One\n```\ncode **not bold**\n```\n**two**";
        int from = text.IndexOf("code", StringComparison.Ordinal), to = text.IndexOf("\n```", from, StringComparison.Ordinal);
        var inside = Assert.Single(Markdown.Note(text, from, to, fenced: true));
        Assert.Equal((MarkdownKind.Code, "code **not bold**", from), (inside.Kind, inside.Text, inside.Spans[0].Source));
        int last = text.LastIndexOf("**two", StringComparison.Ordinal);
        var bold = Assert.Single(Assert.Single(Markdown.Note(text, last, text.Length, fenced: false)).Spans);
        Assert.Equal(("two", MarkdownLook.Bold, last + 2), (bold.Text, bold.Look, bold.Source));
    }
}
