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
}
