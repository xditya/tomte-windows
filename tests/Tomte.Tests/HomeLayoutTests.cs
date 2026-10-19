using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Text;
using Xunit;

namespace Tomte.Tests;

public class HomeLayoutTests
{
    private static readonly Vector2 Body = new((float)PanelSize.Home.W, (float)PanelSize.Home.H - PanelSpace.Head);
    private static readonly HomeLayout Layout = new(Body);

    [Fact]
    public void HomeIsTheFirstTabAndHoldsBothRowsAtItsSize()
    {
        Assert.Equal(PanelId.Home, Enum.GetValues<PanelId>()[0]);
        // The tab row, 12, the two rows 10 apart, 12, the 48 DIP Ask bar, 8, its line and 16 under it.
        Assert.Equal(52 + 12 + 174 + 10 + 190 + 12 + 48 + 8 + 14 + 16, HomeLayout.Height);
        Assert.Equal(2, Layout.Rows);
    }

    [Theory]
    // A 1920 x 1080 monitor at 125% caps the panel at 432 tall: the second row goes. Shorter still, the first.
    [InlineData(432 - 52, 1)]
    // At 480 tall (1920 x 1080 at 112.5%) the second row stays, shorter.
    [InlineData(480 - 52, 2)]
    [InlineData(250, 0)]
    [InlineData(536 - 52, 2)]
    public void AShortBodyKeepsTheAskBarAndLosesRowsFromTheBottom(float body, int rows)
    {
        var layout = new HomeLayout(new(760, body));
        Assert.Equal(rows, layout.Rows);
        Assert.Equal(body - 16, layout.FootLine.Bottom, 0.01);
        Assert.Equal(48, layout.Ask.Height);
        if (rows >= 1)
        {
            Assert.True(layout.Shelf.Bottom <= layout.Ask.Top - 12 + 0.01);
        }

        if (rows == 2)
        {
            Assert.True(layout.Scratchpad.Bottom <= layout.Ask.Top - 12 + 0.01);
        }
    }

    [Fact]
    public void AShorterSecondRowShowsOneRowOfShortcutsAndFewerLinesOfTheNote()
    {
        Assert.Equal((2, 4), (Layout.ShortcutRowsShown, Layout.NoteLinesShown));
        var short_ = new HomeLayout(new(760, 480 - 52));
        Assert.Equal(480 - 52 - 12 - 98 - 174 - 10, short_.SecondHeight, 0.01);
        Assert.Equal((1, 2), (short_.ShortcutRowsShown, short_.NoteLinesShown));
        Assert.True(short_.ShortcutTile(2).Bottom <= short_.Shortcuts.Bottom - 14 + 0.01);
        Assert.True(short_.NoteLineAt(short_.NoteLinesShown - 1, box: true).Text.Bottom <= short_.Scratchpad.Bottom - 14 + 0.01);
        Assert.Equal(3, short_.Targets([], [(1, "a"), (2, "b"), (3, "c"), (4, "d")], false, false).Count(t => t.Id.StartsWith("shortcut:", StringComparison.Ordinal)));
    }

    [Fact]
    public void TheSectionsAreTwoColumnsTheLeftOneWider()
    {
        Assert.Equal(16, Layout.Shelf.X);
        Assert.Equal(12, Layout.Shelf.Y);
        Assert.Equal(Layout.Shelf.Right + 10, Layout.NowPlaying.X, 0.01);
        Assert.Equal(760 - 16, Layout.NowPlaying.Right, 0.01);
        Assert.Equal(1.55, Layout.Shelf.Width / Layout.NowPlaying.Width, 2);
        Assert.Equal((Layout.Shelf.X, Layout.Shelf.Width), (Layout.Scratchpad.X, Layout.Scratchpad.Width));
        Assert.Equal((Layout.NowPlaying.X, Layout.NowPlaying.Width), (Layout.Shortcuts.X, Layout.Shortcuts.Width));
        Assert.Equal(Layout.Shelf.Bottom + 10, Layout.Scratchpad.Y);
        Assert.Equal((174f, 190f), (Layout.Shelf.Height, Layout.Scratchpad.Height));
        Assert.Equal(Layout.Ask.X, Layout.Shelf.X);
        Assert.Equal(Layout.Ask.Right, Layout.NowPlaying.Right, 0.01);
    }

    [Theory]
    [InlineData(0, 0, 0)]
    [InlineData(3, 3, 0)]
    [InlineData(4, 4, 0)]
    [InlineData(8, 3, 5)]
    public void TheShelfShowsItsFirstTilesAndCountsTheRest(int items, int tiles, int more) =>
        Assert.Equal((tiles, more), HomeLayout.ShelfShows(items));

    [Fact]
    public void ShelfTilesFillTheGridFourAcross()
    {
        var grid = Layout.ShelfGrid;
        Assert.Equal(HomeLayout.Head(Layout.Shelf).Bottom + 12, grid.Y);
        Assert.Equal(grid.X, Layout.ShelfTile(0).X);
        Assert.Equal(grid.Right, Layout.ShelfTile(3).Right, 0.01);
        Assert.Equal(Layout.ShelfTile(0).Right + 8, Layout.ShelfTile(1).X, 0.01);
        Assert.Equal(Layout.Shelf.Bottom - 14, Layout.ShelfTile(2).Bottom, 0.01);
        // The preview, then the name and the size, all inside the tile.
        var tile = Layout.ShelfTile(1);
        var text = HomeLayout.TileText(tile, "hero-banner@2x.png", "224 KB");
        Assert.All(text, line => Assert.True(line.Slot.Bottom <= tile.Height && line.Slot.Right <= tile.Width, line.Text));
        Assert.True(HomeLayout.Preview(tile).Bottom < text[0].Slot.Top);
        Assert.Equal("+5", HomeLayout.MoreText(tile, 5)[0].Text);
    }

    [Fact]
    public void APreviewShowsAGlyphForAFolderOrALinkAndTheExtensionOtherwise()
    {
        static ShelfItem Item(string name, bool folder = false) => new(1, name, @"C:\x\" + name, ShelfKind.Reference, DateTimeOffset.Now) { IsFolder = folder };
        Assert.Equal((Glyphs.Folder, ""), HomeLayout.PreviewOf(Item("Brand kit", folder: true)));
        Assert.Equal((Glyphs.Link, ""), HomeLayout.PreviewOf(Item("page.url")));
        Assert.Equal((null, "PDF"), HomeLayout.PreviewOf(Item("Launch plan Q4.pdf")));
        Assert.Equal((null, "FILE"), HomeLayout.PreviewOf(Item("README")));
    }

    [Fact]
    public void NowPlayingIsTheMediaCardStackedInItsSection()
    {
        var card = Layout.NowPlayingCard;
        Assert.Equal(Layout.NowPlaying, card.Card);
        Assert.Equal(Layout.NowPlaying.Bottom - 14, card.PlayPause.Bottom, 0.01);
        Assert.Equal(Layout.NowPlaying.X + (Layout.NowPlaying.Width / 2), card.PlayPause.X + (card.PlayPause.Width / 2), 0.01);
        Assert.True(card.Next.Right <= Layout.NowPlaying.Right - 14);
    }

    [Fact]
    public void TheNoteShowsItsTitleAndFourLinesWithTheirBoxes()
    {
        var (title, lines) = HomeLayout.NotePreview("# Launch week\n\n- [x] Send the hero banner to Priya\n- [X] Book the review room\n- [ ] Record the 30 second demo\n- [ ] **Thu 3 pm** pricing review\nA fifth line\n");
        Assert.Equal("Launch week", title);
        Assert.Equal(
            [("Send the hero banner to Priya", (bool?)true), ("Book the review room", true), ("Record the 30 second demo", false), ("Thu 3 pm pricing review", false)],
            lines);
        Assert.Equal(("", []), HomeLayout.NotePreview("  \n"));
        Assert.Equal([("plain", (bool?)null)], HomeLayout.NotePreview("Title\nplain").Lines);

        // Four lines fit under the title inside the card; a line with a box starts after it.
        var last = Layout.NoteLineAt(3, box: true);
        Assert.True(last.Text.Bottom <= Layout.Scratchpad.Bottom - 14);
        Assert.Equal(last.Box.Right + 10, last.Text.X);
        Assert.Equal(Layout.NoteHeadingBox.X, Layout.NoteLineAt(0, box: false).Text.X);
    }

    [Fact]
    public void ShortcutsAreThreeAcrossAndTwoDown()
    {
        var first = Layout.ShortcutTile(0);
        Assert.Equal(64, first.Height);
        Assert.Equal(Layout.Shortcuts.X + 14, first.X);
        Assert.Equal(Layout.Shortcuts.Right - 14, Layout.ShortcutTile(2).Right, 0.01);
        Assert.Equal(first.Bottom + 8, Layout.ShortcutTile(3).Y);
        Assert.Equal(Layout.Shortcuts.Bottom - 14, Layout.ShortcutTile(5).Bottom, 0.01);
        var (icon, name) = HomeLayout.ShortcutParts(first, "Code");
        Assert.Equal(24, icon.Width);
        Assert.Equal(first.X + (first.Width / 2), icon.X + 12, 0.01);
        Assert.True(name.Slot.Bottom <= first.Bottom);
        Assert.True(Layout.AddButton.Right <= Layout.Shortcuts.Right - 10);
    }

    [Fact]
    public void TheAskBarRunsSparklePromptChipsMic()
    {
        var ask = Layout.Ask;
        Assert.Equal(ask.X + 14, Layout.SparkleBox.X);
        Assert.Equal(ask.Right - 8, Layout.MicButton.Right);
        Assert.Equal(34, Layout.MicButton.Width);
        var chips = Layout.Chips();
        Assert.Equal(["Region", "Window", "Shelf"], chips.Select(c => c.Label));
        Assert.All(chips, c => Assert.Equal((30f, ask.Y + 9), (c.Box.Height, c.Box.Y)));
        Assert.Equal(Layout.MicButton.X - 10, chips[^1].Box.Right, 0.01);
        Assert.Equal(chips[0].Box.Right + 10, chips[1].Box.X, 0.01);
        Assert.Equal(Layout.SparkleBox.Right + 10, Layout.Prompt.X);
        Assert.Equal(chips[0].Box.X - 10, Layout.Prompt.Right, 0.01);
        Assert.Equal(ask.Y + (ask.Height / 2), Layout.Prompt.Y + (Layout.Prompt.Height / 2), 0.01);
    }

    [Fact]
    public void TheLineUnderTheAskBarNamesTheProviderOrSaysTheAiIsOff()
    {
        var (none, off) = Layout.FootText(null, null);
        Assert.Null(none);
        Assert.Equal([PaletteLayout.Off, HomeLayout.SendsNothing], off.Select(l => l.Text));

        var (dot, on) = Layout.FootText("anthropic · claude-haiku-4-5", "$0.12 of $10.00 today");
        Assert.Equal(6, dot!.Value.Width);
        Assert.Equal(["anthropic · claude-haiku-4-5 · ", "$0.12 of $10.00 today", HomeLayout.SendsNothing], on.Select(l => l.Text));
        Assert.Equal(TextStyle.Mono, on[1].Style.Family);
        Assert.Equal(on[0].Slot.Right, on[1].Slot.X, 0.01);
        Assert.Equal(Layout.FootLine.Right, on[2].Slot.Right, 0.01);

        Assert.Equal("$0.12 of $10.00 today", HomeLayout.Spend(0.12m, 10m));
        Assert.Equal("$3.50 today", HomeLayout.Spend(3.5m, null));
    }

    [Fact]
    public void TargetsRunSectionBySectionThenTheAskBar()
    {
        Target[] media = [new("previous", default, 18, TargetKind.Button), new("playpause", default, 22, TargetKind.Button)];
        var targets = Layout.Targets(media, [(7, "Code"), (8, "Figma")], window: true, shelf: false);
        Assert.Equal(
            ["shelf", "media", "previous", "playpause", "note", "shortcuts", "add", "shortcut:7", "shortcut:8", "ask", "region", "window", "shelfchip", "mic"],
            targets.Select(t => t.Id));
        Assert.Equal(TargetKind.Box, targets.Single(t => t.Id == "ask").Kind);
        Assert.True(targets.Single(t => t.Id == "window").Checked);
        Assert.False(targets.Single(t => t.Id == "shelfchip").Checked);
        Assert.True(targets.Single(t => t.Id == "mic").Draggable);
        Assert.All(targets.Where(t => t.Id.StartsWith("shortcut:", StringComparison.Ordinal)), t => Assert.Equal(2, t.Group));
        Assert.All(targets.Except(media), t => Assert.False(string.IsNullOrEmpty(t.Tip) && t.Kind != TargetKind.Box, t.Id));

        // With no room for the sections only the Ask bar is left.
        var bare = new HomeLayout(new(760, 120)).Targets(media, [], false, false);
        Assert.Equal(["ask", "region", "window", "shelfchip", "mic"], bare.Select(t => t.Id));
    }
}
