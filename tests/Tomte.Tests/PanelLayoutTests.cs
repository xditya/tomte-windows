using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Clipboard;
using Tomte.Core.Interaction;
using Tomte.Core.Text;
using Xunit;

namespace Tomte.Tests;

public sealed class PanelLayoutTests
{
    // The Clipboard panel's body: 560 x 360 less the 42 DIP tab strip.
    private static readonly ClipboardPanelLayout Clipboard = new(new Vector2(560, 318));

    private static readonly long[] Three = [7, 8, 9];

    [Fact]
    public void ThePartsSitInsideTheBody()
    {
        var list = Clipboard.List;
        Assert.Equal(14, list.X);
        Assert.Equal(40, list.Y);
        Assert.Equal(532, list.Width);
        Assert.Equal(318 - 12 - 28 - 10, list.Bottom);
        Assert.Equal(560 - 14, Clipboard.Switch.Right);
        Assert.True(Clipboard.Foot.Bottom <= 318 - 12);
        // Five whole rows still fit with the taller footer.
        Assert.True(5 * ClipboardPanelLayout.RowPitch - ClipboardPanelLayout.RowGap <= list.Height);
    }

    [Fact]
    public void APressFindsTheRowItsRemoveButtonTheSwitchAndClearAll()
    {
        var targets = Clipboard.Targets(on: true, Three, 0, asking: false);
        float rowMid = Clipboard.List.Y + ClipboardPanelLayout.RowHeight / 2;
        Assert.Equal("row:7", HitId(targets, new(100, rowMid)));
        Assert.Equal("row:8", HitId(targets, new(100, rowMid + ClipboardPanelLayout.RowPitch)));
        // The gap between rows hits nothing; nor does the space under the last row.
        Assert.Null(HitId(targets, new(100, Clipboard.List.Y + ClipboardPanelLayout.RowHeight + 3)));
        Assert.Null(HitId(targets, new(100, rowMid + 3 * ClipboardPanelLayout.RowPitch)));

        var remove = ClipboardPanelLayout.RemoveIn(Clipboard.List.Width);
        Assert.Equal("remove:7", HitId(targets, new(Clipboard.List.X + remove.X + remove.Width / 2, rowMid)));
        // The remove button's edge, outside a smaller drawn circle, is still the button.
        Assert.Equal("remove:7", HitId(targets, new(Clipboard.List.X + remove.X + 1, Clipboard.List.Y + remove.Y + 1)));

        // Just outside the drawn switch, inside its larger area.
        var sw = Clipboard.Switch;
        Assert.Equal("switch", HitId(Clipboard.Targets(on: false, [], 0, asking: false), new(sw.X - 2, sw.Y - 2)));
        Assert.Equal("switch", HitId(targets, new(sw.Right + 2, sw.Bottom + 2)));

        var clear = Clipboard.ClearAll(asking: false);
        Assert.Equal("clear", HitId(targets, new(clear.X + 5, clear.Y + 5)));
        // Asking, the button is wider: its new left part is Clear all too, and only then.
        var ask = Clipboard.ClearAll(asking: true);
        Assert.Equal("clear", HitId(Clipboard.Targets(on: true, Three, 0, asking: true), new(ask.X + 5, ask.Y + 5)));
        Assert.Null(HitId(targets, new(ask.X + 5, ask.Y + 5)));

        // Off or empty there are no rows and no Clear all to press, only the switch.
        Assert.Equal(["switch"], Clipboard.Targets(on: false, Three, 0, asking: false).Select(t => t.Id));
        Assert.Equal(["switch"], Clipboard.Targets(on: true, [], 0, asking: false).Select(t => t.Id));
    }

    [Fact]
    public void TheTargetsAreBigEnoughNamedAndInTabOrder()
    {
        var targets = Clipboard.Targets(on: true, Three, 0, asking: false);
        Assert.Equal(["switch", "row:7", "remove:7", "row:8", "remove:8", "row:9", "remove:9", "clear"], targets.Select(t => t.Id));
        Assert.All(targets, t => Assert.True(t.Bounds.Width >= StateLook.MinTarget && t.Bounds.Height >= StateLook.MinTarget, t.Id));

        var sw = targets[0];
        Assert.Equal((40f, 24f), (sw.Bounds.Width, sw.Bounds.Height));
        Assert.True(sw.Bounds.Contains(Clipboard.Switch));
        Assert.False(sw.Bounds.IntersectsWith(Clipboard.List));
        Assert.Equal((TargetKind.Toggle, true, "Keep clipboard history: on"), (sw.Kind, sw.Checked, sw.Tip));
        Assert.Equal("Keep clipboard history: off", Clipboard.Targets(on: false, [], 0, asking: false)[0].Tip);

        Assert.Equal((TargetKind.Row, "Copy again", 1), (targets[1].Kind, targets[1].Tip, targets[1].Group));
        Assert.Equal((TargetKind.Button, "Remove", 1), (targets[2].Kind, targets[2].Tip, targets[2].Group));
        Assert.Equal(("Copy again (Enter)", "Remove (Delete)"), (targets[1].TipWith(keys: true), targets[2].TipWith(keys: true)));
        Assert.Equal((24f, 24f), (targets[2].Bounds.Width, targets[2].Bounds.Height));

        // Clear all is a panel action, as tall as one, alone in its Tab stop, and grows to the left when it asks.
        var clear = targets[^1];
        Assert.True(clear.Bounds.Height >= StateLook.ActionHeight);
        Assert.Equal(0, clear.Group);
        var ask = Clipboard.ClearAll(asking: true);
        Assert.Equal(clear.Bounds.Right, ask.Right);
        Assert.True(ask.Width > clear.Bounds.Width);
        Assert.Equal(clear.Bounds.Height, ask.Height);
    }

    [Fact]
    public void RowsOutOfViewHaveNoHeightAndFocusCanScrollThemIn()
    {
        long[] ten = [.. Enumerable.Range(1, 10).Select(i => (long)i)];
        var targets = Clipboard.Targets(on: true, ten, 0, asking: false);
        RectangleF Row(IReadOnlyList<Target> all, long id) => all.First(t => t.Id == ClipboardPanelLayout.RowId(id)).Bounds;

        // Five rows show; the sixth keeps its place below them with no height, so nothing hits it.
        Assert.Equal(ClipboardPanelLayout.RowHeight, Row(targets, 5).Height);
        var sixth = Row(targets, 6);
        Assert.Equal((0f, Clipboard.List.Y + 5 * ClipboardPanelLayout.RowPitch), (sixth.Height, sixth.Y));
        Assert.Null(HitId(targets, new(100, Clipboard.Foot.Y + 5)));

        // Down from the fifth row finds the sixth all the same.
        int fifth = targets.ToList().FindIndex(t => t.Id == "row:5");
        Assert.Equal("row:6", targets[Target.Arrow(targets, fifth, new(0, 1))].Id);

        // Focus on it scrolls just far enough to show it whole; then the first row is the one out of view.
        float scroll = Clipboard.ScrolledTo(5, 0, ten.Length);
        Assert.Equal(5 * ClipboardPanelLayout.RowPitch + ClipboardPanelLayout.RowHeight - Clipboard.List.Height, scroll);
        var scrolled = Clipboard.Targets(on: true, ten, scroll, asking: false);
        Assert.Equal(ClipboardPanelLayout.RowHeight, Row(scrolled, 6).Height);
        Assert.Equal(0, Row(scrolled, 1).Height);
        Assert.Equal(0, Clipboard.ScrolledTo(0, scroll, ten.Length));
        Assert.Equal(scroll, Clipboard.ScrolledTo(3, scroll, ten.Length));

        // A row half out of view is cut to the list, so its target never covers the head or the foot.
        var half = Clipboard.Targets(on: true, ten, ClipboardPanelLayout.RowHeight / 2, asking: false);
        Assert.Equal(Clipboard.List.Y, Row(half, 1).Y);
        Assert.Equal(ClipboardPanelLayout.RowHeight / 2, Row(half, 1).Height);
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
        long[] ids = [.. Enumerable.Range(1, rows).Select(i => (long)i)];
        Assert.Equal("row:2", HitId(Clipboard.Targets(on: true, ids, down, asking: false), new(100, top)));
    }

    public static TheoryData<double> Scales => [1.0, 1.25, 1.5];

    [Theory]
    [MemberData(nameof(Scales))]
    public void APressHitsTheSameTargetAtEveryScale(double scale)
    {
        long[] five = [1, 2, 3, 4, 5];
        var targets = Clipboard.Targets(on: true, five, 0, asking: false);
        float row1 = Clipboard.List.Y + ClipboardPanelLayout.RowPitch + ClipboardPanelLayout.RowHeight / 2;
        var remove = ClipboardPanelLayout.RemoveIn(Clipboard.List.Width);
        Assert.Equal("row:2", HitId(targets, BodyPoint(100, row1, scale)));
        Assert.Equal("remove:2", HitId(targets, BodyPoint(Clipboard.List.X + remove.X + remove.Width / 2, row1, scale)));
        var sw = Clipboard.SwitchArea;
        Assert.Equal("switch", HitId(targets, BodyPoint(sw.X + sw.Width / 2, sw.Y + sw.Height / 2, scale)));
        var clear = Clipboard.ClearAll(asking: false);
        Assert.Equal("clear", HitId(targets, BodyPoint(clear.X + clear.Width / 2, clear.Y + clear.Height / 2, scale)));
    }

    [Fact]
    public void EveryPartIsInsideTheBodyAndClearOfItsRoundedCorners()
    {
        // The body's bottom corners are the shape's, radius 22; the tab strip keeps the top ones away.
        const float radius = 22;
        var body = Clipboard.Body;
        RectangleF[] parts = [Clipboard.Head, Clipboard.SwitchArea, Clipboard.List, Clipboard.Foot, Clipboard.ClearAll(false), Clipboard.ClearAll(true)];
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

    private static string? HitId(IReadOnlyList<Target> targets, Vector2 at) =>
        Target.Hit([(targets, Same)], at) is { } hit ? targets[hit.Index].Id : null;

    private static readonly Func<Vector2, Vector2> Same = p => p;

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
        var head = Clipboard.HeadText(on: true, new ClipboardRetention());
        float title = TextSlot.Measure("Clipboard history", TextStyle.Title, 500);

        Assert.Equal("last 50 pieces of text and 50 images", head[1].Text);
        Assert.True(TextSlot.Measure(head[1].Text, TextStyle.Secondary, 1000) <= head[1].Slot.Width, "the note is cut");
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
        Assert.Equal(ClipboardPanelLayout.IconIn.Right + ClipboardPanelLayout.Gap, row[1].Slot.X);

        // An image row's thumbnail starts where the icon does and fits the same 40 DIP row with room above and below.
        var thumb = ClipboardPanelLayout.ThumbIn;
        Assert.Equal((ClipboardPanelLayout.IconIn.X, 48f, 32f), (thumb.X, thumb.Width, thumb.Height));
        Assert.Equal(ClipboardPanelLayout.RowHeight - thumb.Bottom, thumb.Top);
        Assert.True(thumb.Top >= 4);

        // The footer: the note ends before Clear all at its widest, so asking never moves it; the button's word fits.
        foreach (bool asking in new[] { false, true })
        {
            var foot = Clipboard.FootText(asking);
            Assert.True(foot[0].Slot.Right <= Clipboard.ClearAll(true).X - ClipboardPanelLayout.Gap, "the note runs into Clear all");
            Assert.Equal(Clipboard.ClearAll(asking), foot[1].Slot);
            Assert.Equal(asking ? "Clear all? Click again" : "Clear all", foot[1].Text);
            Assert.Equal(asking ? Theme.Warn : Theme.Ink2, foot[1].Style.Color);
            using var word = TextLayout.Of(new TextRun(foot[1].Text, foot[1].Style, foot[1].Slot.Width));
            Assert.False(word.Trimmed, foot[1].Text);
        }

        // Off: two centred lines in the middle of the list's box.
        var box = Clipboard.BoxText(on: false, new ClipboardRetention());
        Assert.Equal(2, box.Count);
        Assert.Equal("History is off. Turn it on to keep 50 pieces of text and 50 images on this PC.", box[0].Text);
        Assert.All(box, line => Assert.True(TextSlot.Measure(line.Text, line.Style, 1000) <= line.Slot.Width, line.Text));
        Assert.All(box, line => Assert.Equal(TextAlign.Center, line.Style.Align));
        Assert.Equal(Clipboard.List.Y + Clipboard.List.Height / 2, (box[0].Slot.Top + box[1].Slot.Bottom) / 2, 0.01);
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public void AnImageRowHasItsThumbnailLeftAndItsLabelAfterIt(double scale)
    {
        // The label starts after the thumbnail, whole, and ends before the age.
        var thumb = ClipboardPanelLayout.ThumbIn;
        float width = Clipboard.List.Width;
        string label = ClipboardRow.ImageLabel(1920, 1080);
        var row = ClipboardPanelLayout.RowText(label, "2 min", width, image: true);
        Assert.Equal("Image · 1920 x 1080", row[1].Text);
        Assert.Equal(thumb.Right + ClipboardPanelLayout.Gap, row[1].Slot.X);
        Assert.True(TextSlot.Measure(label, TextStyle.Primary, 1000) <= row[1].Slot.Width);
        Assert.True(row[1].Slot.Right < row[0].Slot.Right);

        // The slot is whole pixels at the scale, so the thumbnail decoded for it shows one to one; the stored one is
        // large enough for it.
        Assert.Equal(0, thumb.Width * scale % 1, 3);
        Assert.Equal(0, thumb.Height * scale % 1, 3);
        Assert.True(ClipboardImages.ThumbWidth >= thumb.Width * scale && ClipboardImages.ThumbHeight >= thumb.Height * scale);
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
        Assert.Equal(Theme.Warn, lines[1].Style.Color);
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
