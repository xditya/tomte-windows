using Tomte.Core.Text;
using Xunit;

namespace Tomte.Tests;

/// <summary>The scratchpad's Markdown styled in place: the looks of a note's lines, the lines an edit restyles, and its keys.</summary>
public class NoteMarkdownTests
{
    private const uint KeyDown = 0x0100, Char = 0x0102;
    private const MarkdownLook Marker = MarkdownLook.Marker, H1 = MarkdownLook.Heading1;

    // The runs as the text they cover and their look.
    private static (string, MarkdownLook)[] Runs(string text, int from, int to) =>
        [.. NoteMarkdown.Style(text, from, to).Runs.Select(r => (text.Substring(r.Start, r.Length), r.Look))];

    [Fact]
    public void EveryCharacterGetsALookAndMarkersStayOnTheirLine()
    {
        const string text = "# Hi **b**\n- [x] done\nplain";
        Assert.Equal(
            [("# ", Marker | H1), ("Hi ", H1), ("**", Marker | H1), ("b", MarkdownLook.Bold | H1), ("**", Marker | H1), ("\n", MarkdownLook.Plain),
             ("- [x] ", Marker), ("done", MarkdownLook.Done), ("\nplain", MarkdownLook.Plain)],
            Runs(text, 0, text.Length));
        var lines = NoteMarkdown.Style(text, 0, text.Length).Lines;
        Assert.Equal([new NoteLine(0, 0), new NoteLine(11, 6), new NoteLine(22, 0)], lines);
        // A heading's level, an indented item's hang, a rule's dashes.
        Assert.Equal([("### ", Marker | MarkdownLook.Heading3), ("c", MarkdownLook.Heading3)], Runs("### c", 0, 5));
        Assert.Equal(6, Assert.Single(NoteMarkdown.Style("  10. item", 0, 10).Lines).Hang);
        Assert.Equal([("---", Marker)], Runs("---", 0, 3));
    }

    [Fact]
    public void CodeFencesReachPastTheirLine()
    {
        const string text = "```\ncode **x**\n```\nafter **b**";
        Assert.Equal([("```", Marker | MarkdownLook.Code), ("\n", MarkdownLook.Plain), ("code **x**", MarkdownLook.Code), ("\n", MarkdownLook.Plain), ("```", Marker | MarkdownLook.Code), ("\nafter ", MarkdownLook.Plain), ("**", Marker), ("b", MarkdownLook.Bold), ("**", Marker)],
            Runs(text, 0, text.Length));
        // A line inside the block is code when styled on its own, and its line break comes with it.
        int code = text.IndexOf("code", StringComparison.Ordinal);
        Assert.Equal([("code **x**", MarkdownLook.Code), ("\n", MarkdownLook.Plain)], Runs(text, code, code + 10));
        int after = text.IndexOf("after", StringComparison.Ordinal);
        Assert.Equal([("after ", MarkdownLook.Plain), ("**", Marker), ("b", MarkdownLook.Bold), ("**", Marker)], Runs(text, after, text.Length));
    }

    [Fact]
    public void AnEditRestylesTheLinesItTouched()
    {
        // A key in a line, Enter in one, and Backspace that joins two.
        Assert.Equal((2, 5), NoteMarkdown.Changed("a\nbc\nd", "a\nbXc\nd"));
        Assert.Equal((0, 3), NoteMarkdown.Changed("ab\ncd", "a\nb\ncd"));
        Assert.Equal((0, 4), NoteMarkdown.Changed("ab\ncd\nef", "abcd\nef"));
        Assert.Null(NoteMarkdown.Changed("same", "same"));
        // A fence made or unmade changes every line after it.
        Assert.Equal((2, 15), NoteMarkdown.Changed("x\n``\ny\nz **b**", "x\n```\ny\nz **b**"));
        Assert.Equal((2, 14), NoteMarkdown.Changed("x\n```\ny\nz **b**", "x\n``\ny\nz **b**"));
    }

    [Fact]
    public void ALongNoteIsStyledInPiecesOfWholeLines()
    {
        const string text = "a\nbb\nc\nd\ne";
        Assert.Equal((0, 4), NoteMarkdown.Lines(text, 0, 2));
        Assert.Equal((2, 6), NoteMarkdown.Lines(text, 3, 2));
        Assert.Equal((7, 10), NoteMarkdown.Lines(text, 7, 9));
        Assert.Equal((9, 10), NoteMarkdown.Lines(text, 10, 1));
        // Around the caret: half the lines before its own.
        Assert.Equal((5, 10), NoteMarkdown.Around(text, 7, 3));
        Assert.Equal((0, 4), NoteMarkdown.Around(text, 0, 2));
        Assert.Equal((0, 10), NoteMarkdown.Around(text, 10, 300));
        Assert.Equal((0, 0), NoteMarkdown.Around("", 0, 300));
    }

    [Fact]
    public void TheNoteTakesClipboardAndFormattingKeys()
    {
        const nuint Insert = 0x2D, Delete = 0x2E, Left = 0x25, One = '1', Plus = 0xBB;
        Assert.Equal(NoteKey.Copy, NoteKeys.Of(KeyDown, 'C', ctrl: true, shift: false, alt: false));
        Assert.Equal(NoteKey.Copy, NoteKeys.Of(KeyDown, Insert, ctrl: true, shift: false, alt: false));
        Assert.Equal(NoteKey.Cut, NoteKeys.Of(KeyDown, 'X', ctrl: true, shift: false, alt: false));
        Assert.Equal(NoteKey.Cut, NoteKeys.Of(KeyDown, Delete, ctrl: false, shift: true, alt: false));
        Assert.Equal(NoteKey.Paste, NoteKeys.Of(KeyDown, 'V', ctrl: true, shift: false, alt: false));
        Assert.Equal(NoteKey.Paste, NoteKeys.Of(KeyDown, Insert, ctrl: false, shift: true, alt: false));
        foreach (nuint key in new nuint[] { 'B', 'I', 'U', 'E', 'L', 'R', 'J', One, Plus })
        {
            Assert.Equal(NoteKey.Ignore, NoteKeys.Of(KeyDown, key, ctrl: true, shift: key == 'L', alt: false));
        }

        // Undo, select all, moving by words, plain keys, AltGr, and characters pass.
        foreach (var (key, ctrl, alt) in new (nuint, bool, bool)[] { ('Z', true, false), ('A', true, false), (Left, true, false), ('B', false, false), ('E', true, true) })
        {
            Assert.Equal(NoteKey.Pass, NoteKeys.Of(KeyDown, key, ctrl, shift: false, alt));
        }

        Assert.Equal(NoteKey.Pass, NoteKeys.Of(Char, 0x02, ctrl: true, shift: false, alt: false));
    }
}
