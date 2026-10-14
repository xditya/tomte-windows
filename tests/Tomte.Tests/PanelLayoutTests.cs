using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Text;
using Xunit;

namespace Tomte.Tests;

public sealed class PanelLayoutTests
{
    // The Clipboard panel's body: 560 x 360 less the 42 DIP tab strip.
    private static readonly ClipboardPanelLayout Clipboard = new(new Vector2(560, 318));

    [Fact]
    public void ThePartsSitInsideTheBody()
    {
        var list = Clipboard.List;
        Assert.Equal(14, list.X);
        Assert.Equal(40, list.Y);
        Assert.Equal(532, list.Width);
        Assert.Equal(318 - 12 - 14 - 10, list.Bottom);
        Assert.Equal(560 - 14, Clipboard.Switch.Right);
        Assert.True(Clipboard.Foot.Bottom <= 318 - 12);
    }

    [Fact]
    public void APressFindsTheRowItsRemoveButtonTheSwitchAndClearAll()
    {
        float rowMid = Clipboard.List.Y + ClipboardPanelLayout.RowHeight / 2;
        Assert.Equal((ClipboardTarget.Row, 0), Clipboard.Hit(new(100, rowMid), 3, 0, on: true));
        Assert.Equal((ClipboardTarget.Row, 1), Clipboard.Hit(new(100, rowMid + ClipboardPanelLayout.RowPitch), 3, 0, on: true));
        // The gap between rows hits nothing; nor does the space under the last row.
        Assert.Equal((ClipboardTarget.None, -1), Clipboard.Hit(new(100, Clipboard.List.Y + ClipboardPanelLayout.RowHeight + 3), 3, 0, on: true));
        Assert.Equal((ClipboardTarget.None, -1), Clipboard.Hit(new(100, rowMid + 3 * ClipboardPanelLayout.RowPitch), 3, 0, on: true));

        var remove = ClipboardPanelLayout.RemoveIn(Clipboard.List.Width);
        var onRemove = new Vector2(Clipboard.List.X + remove.X + remove.Width / 2, rowMid);
        Assert.Equal((ClipboardTarget.RemoveRow, 0), Clipboard.Hit(onRemove, 3, 0, on: true));

        var sw = Clipboard.Switch;
        Assert.Equal((ClipboardTarget.Switch, -1), Clipboard.Hit(new(sw.X + 5, sw.Y + 5), 0, 0, on: false));
        var clear = Clipboard.ClearAll;
        Assert.Equal((ClipboardTarget.ClearAll, -1), Clipboard.Hit(new(clear.X + 5, clear.Y + 5), 3, 0, on: true));
        // Off or empty there are no rows and no Clear all to press.
        Assert.Equal((ClipboardTarget.None, -1), Clipboard.Hit(new(100, rowMid), 3, 0, on: false));
        Assert.Equal((ClipboardTarget.None, -1), Clipboard.Hit(new(clear.X + 5, clear.Y + 5), 0, 0, on: true));
    }

    [Fact]
    public void TheWheelScrollsOneRowANotchWithinTheList()
    {
        int rows = 50;
        float max = Clipboard.MaxScroll(rows);
        Assert.Equal(rows * ClipboardPanelLayout.RowPitch - ClipboardPanelLayout.RowGap - Clipboard.List.Height, max);
        Assert.Equal(0, Clipboard.MaxScroll(3));

        float down = Clipboard.Scrolled(0, -120, rows);
        Assert.Equal(ClipboardPanelLayout.RowPitch, down);
        Assert.Equal(0, Clipboard.Scrolled(down, 240, rows));
        Assert.Equal(max, Clipboard.Scrolled(max - 1, -120, rows));

        // Scrolled by one row, the top of the list is the second row.
        float top = Clipboard.List.Y + ClipboardPanelLayout.RowHeight / 2;
        Assert.Equal((ClipboardTarget.Row, 1), Clipboard.Hit(new(100, top), rows, down, on: true));
    }

    public static TheoryData<double> Scales => [1.0, 1.25, 1.5];

    [Theory]
    [MemberData(nameof(Scales))]
    public void APressHitsTheSameTargetAtEveryScale(double scale)
    {
        float row1 = Clipboard.List.Y + ClipboardPanelLayout.RowPitch + ClipboardPanelLayout.RowHeight / 2;
        var remove = ClipboardPanelLayout.RemoveIn(Clipboard.List.Width);
        Assert.Equal((ClipboardTarget.Row, 1), Clipboard.Hit(BodyPoint(100, row1, scale), 5, 0, on: true));
        Assert.Equal((ClipboardTarget.RemoveRow, 1), Clipboard.Hit(BodyPoint(Clipboard.List.X + remove.X + remove.Width / 2, row1, scale), 5, 0, on: true));
        var sw = Clipboard.Switch;
        Assert.Equal((ClipboardTarget.Switch, -1), Clipboard.Hit(BodyPoint(sw.X + sw.Width / 2, sw.Y + sw.Height / 2, scale), 5, 0, on: true));
        var clear = Clipboard.ClearAll;
        Assert.Equal((ClipboardTarget.ClearAll, -1), Clipboard.Hit(BodyPoint(clear.X + clear.Width / 2, clear.Y + clear.Height / 2, scale), 5, 0, on: true));
    }

    [Fact]
    public void EveryPartIsInsideTheBodyAndClearOfItsRoundedCorners()
    {
        // The body's bottom corners are the shape's, radius 22; the tab strip keeps the top ones away.
        const float radius = 22;
        var body = Clipboard.Body;
        RectangleF[] parts = [Clipboard.Head, Clipboard.Switch, Clipboard.List, Clipboard.Foot, Clipboard.ClearAll];
        foreach (var part in parts)
        {
            foreach (var (x, y) in new[] { (part.Left, part.Top), (part.Right, part.Top), (part.Left, part.Bottom), (part.Right, part.Bottom) })
            {
                Assert.True(x >= 0 && y >= 0 && x <= body.X && y <= body.Y, part.ToString());
                if (y > body.Y - radius && (x < radius || x > body.X - radius))
                {
                    var corner = new Vector2(x < radius ? radius : body.X - radius, body.Y - radius);
                    Assert.True(Vector2.Distance(new Vector2(x, y), corner) <= radius, $"{part} at its corner {x},{y}");
                }
            }
        }
    }

    // A press arrives in window pixels: island space starts one pixel in plus half the outline's width and carries
    // the monitor's scale; the body starts under the tab strip. This is the host's mapping, as the media tests use it.
    private static Vector2 BodyPoint(float bodyX, float bodyY, double scale)
    {
        var size = PanelSize.Clipboard;
        double originX = 1 + IslandGeometry.Panel(size.W, size.H).OuterWidth / 2 * scale;
        int x = (int)Math.Round(originX + (bodyX - size.W / 2) * scale);
        int y = (int)Math.Round(1 + (bodyY + PanelSpace.TabStripHeight) * scale);
        return PanelSpace.ToBody(PanelSpace.FromPixels(x, y, (float)originX, 1, scale), size.W);
    }

    [Fact]
    public void TheNoteIsTypedInsideItsCard()
    {
        // 320 tall less the strip: a 202 DIP card, the text box 12 in from its sides and 10 from its ends.
        var layout = new ScratchpadPanelLayout(new Vector2(520, 278));
        Assert.Equal(202, layout.Note.Height, 0.01);
        Assert.Equal(RectangleF.FromLTRB(layout.Note.X + 12, layout.Note.Y + 10, layout.Note.Right - 12, layout.Note.Bottom - 10), layout.Editor);
    }

    [Fact]
    public void ClipboardWordsStayClearOfTheSwitchAndTheButtons()
    {
        var head = Clipboard.HeadText(on: true, keeps: 50);
        float title = TextSlot.Measure("Clipboard history", TextStyle.Title, 500);

        Assert.Equal("last 50 items, this PC only", head[1].Text);
        Assert.Equal(Clipboard.Head.X + title + ClipboardPanelLayout.Gap, head[1].Slot.X, 0.01);
        Assert.True(head[1].Slot.Right <= head[2].Slot.Left, "the note runs into the switch's label");
        Assert.Equal(Clipboard.Switch.X - ClipboardPanelLayout.Gap, head[2].Slot.Right, 0.01);
        Assert.Equal(TextAlign.Trailing, head[2].Style.Align);

        // A row: the age ends before the remove button, the text ends before the age's measured width.
        float width = Clipboard.List.Width;
        var row = ClipboardPanelLayout.RowText("Meet at 10, not 9", "14 min", width);
        var remove = ClipboardPanelLayout.RemoveIn(width);
        Assert.Equal(remove.X - ClipboardPanelLayout.Gap, row[0].Slot.Right, 0.01);
        float age = TextSlot.Measure("14 min", TextStyle.Small, 48);
        Assert.Equal(row[0].Slot.Right - age - ClipboardPanelLayout.Gap, row[1].Slot.Right, 0.01);
        Assert.Equal(ClipboardPanelLayout.Gap + ClipboardPanelLayout.Icon + ClipboardPanelLayout.Gap, row[1].Slot.X);

        // Off: two centred lines in the middle of the list's box.
        var box = Clipboard.BoxText(on: false, keeps: 50);
        Assert.Equal(2, box.Count);
        Assert.All(box, line => Assert.Equal(TextAlign.Center, line.Style.Align));
        Assert.Equal(Clipboard.List.Y + Clipboard.List.Height / 2, (box[0].Slot.Top + box[1].Slot.Bottom) / 2, 0.01);
    }

    [Fact]
    public void ShelfWordsFitTheirPlaces()
    {
        var body = new Vector2(560, 260 - PanelSpace.TabStripHeight);
        var head = ShelfLayout.HeadText(body, 12);

        // The count runs up to the hint, which ends at the right padding; neither is cut.
        Assert.True(head[1].Slot.Right <= head[2].Slot.Left);
        Assert.Equal(body.X - ShelfLayout.PadX, head[2].Slot.Right, 0.01);
        foreach (var line in head.Skip(1))
        {
            using var laid = TextLayout.Of(new TextRun(line.Text, line.Style, line.Slot.Width));
            Assert.False(laid.Trimmed, line.Text);
        }

        // The drop target's glyph and word are centred together, and the word is whole.
        var target = ShelfLayout.DropTarget(IslandGeometry.ShelfDrop.Width, IslandGeometry.ShelfDrop.Depth);
        var (x, label) = ShelfLayout.DropLabel(target.Size);
        Assert.Equal(target.Width - label.Slot.Right, x, 0.5);
        using var word = TextLayout.Of(new TextRun(label.Text, label.Style, label.Slot.Width));
        Assert.False(word.Trimmed);

        // A missing item's meta line is in the warn colour; the lines stay inside the tile.
        var tile = ShelfLayout.Tile(0, body);
        var item = new ShelfItem(1, "Q3-report.pdf", @"C:\Users\me\Q3-report.pdf", ShelfKind.Reference, DateTimeOffset.Now) { Missing = true };
        var lines = ShelfLayout.TileText(item, tile.Size);
        Assert.Equal(TextStyle.WarnColor, lines[1].Style.Color);
        Assert.All(lines, line => Assert.True(line.Slot.Right <= tile.Width && line.Slot.Bottom <= tile.Height, line.Text));
    }

    [Fact]
    public void ScratchpadWordsFollowTheTitle()
    {
        var layout = new ScratchpadPanelLayout(new Vector2(520, 278));
        var frame = layout.FrameText();
        float title = TextSlot.Measure("Scratchpad", TextStyle.Title, 500);

        Assert.Equal(layout.Head.X + title + 10, frame[1].Slot.X, 0.01);
        Assert.Equal(layout.Foot.Right, frame[3].Slot.Right, 0.01);
    }
}
