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
    // The Clipboard panel's body: 760 wide, its height less the tab strip.
    private static readonly ClipboardPanelLayout Clipboard = new(new Vector2(760, (float)PanelSize.Clipboard.H - PanelSpace.TabStripHeight));

    // A Tuesday afternoon, two hours ahead of UTC, and rows copied before it.
    private static readonly DateTimeOffset Now = new(2026, 10, 20, 15, 0, 0, TimeSpan.FromHours(2));

    private static List<ClipboardRow> Rows(params double[] minutesAgo) =>
        [.. minutesAgo.Select((m, i) => new ClipboardRow(i + 1, $"item {i + 1}", Now.AddMinutes(-m)))];

    private static List<ClipboardRow> Fresh(int count) => Rows([.. Enumerable.Range(0, count).Select(i => i * 0.1)]);

    private static IReadOnlyList<Target> Targets(IReadOnlyList<ClipboardRow> rows, float scroll = 0, bool asking = false, ClipboardRow? selected = null, ClipboardPanelLayout? layout = null) =>
        (layout ?? Clipboard).Targets(on: true, rows, ClipboardPanelLayout.Lines(rows, Now), scroll, asking, selected);

    [Fact]
    public void ThePartsSitInsideTheBody()
    {
        var body = Clipboard.Body;
        // The head row: the switch after its label at the left, the filter at the right padding.
        Assert.Equal(16, Clipboard.SwitchArea.X + 4);
        Assert.Equal(body.X - 16, Clipboard.Filter.Right);
        Assert.Equal((180f, 36f), (Clipboard.Filter.Width, Clipboard.Filter.Height));
        Assert.True(Clipboard.Filter.Contains(Clipboard.FilterBox) && Clipboard.Filter.Contains(Clipboard.FilterGlyphBox));
        Assert.True(Clipboard.FilterGlyphBox.Right <= Clipboard.FilterBox.Left);

        // The list takes 1.2 parts and the preview 1, 10 apart, between the head and the footer.
        Assert.True(Clipboard.HasPreview);
        Assert.Equal(16, Clipboard.List.X);
        Assert.Equal(14 + 36 + 14, Clipboard.List.Y);
        Assert.Equal(1.2f, Clipboard.List.Width / Clipboard.Preview.Width, 2);
        Assert.Equal(10, Clipboard.Preview.Left - Clipboard.List.Right, 2);
        Assert.Equal(body.X - 16, Clipboard.Preview.Right, 2);
        Assert.Equal(Clipboard.Foot.Y - 14, Clipboard.List.Bottom);
        Assert.Equal(body.Y - 16, Clipboard.Foot.Bottom);
        // Six rows show under a heading.
        Assert.True(ClipboardPanelLayout.Height(ClipboardPanelLayout.Lines(Fresh(6), Now)) <= Clipboard.List.Height);

        // Narrow, the list takes all of the room and the preview is gone.
        var narrow = new ClipboardPanelLayout(new Vector2(480, body.Y));
        Assert.False(narrow.HasPreview);
        Assert.Equal(480 - 32, narrow.List.Width);
        Assert.True(narrow.Preview.IsEmpty);
    }

    [Fact]
    public void RowsAreGroupedByWhenTheyWereCopied()
    {
        // Just now, earlier today, yesterday, a weekday, a date: each group under one heading.
        var rows = Rows(1, 9, 11, 60 * 10, 60 * 20, 60 * 24 * 3, 60 * 24 * 9);
        Assert.Equal(["JUST NOW", "JUST NOW", "EARLIER TODAY", "EARLIER TODAY", "YESTERDAY", "SATURDAY", "11 OCT"], rows.Select(r => r.Group(Now)));
        var lines = ClipboardPanelLayout.Lines(rows, Now);
        Assert.Equal(["JUST NOW", null, null, "EARLIER TODAY", null, null, "YESTERDAY", null, "SATURDAY", null, "11 OCT", null], lines.Select(l => l.Heading));
        Assert.Equal([-1, 0, 1, -1, 2, 3, -1, 4, -1, 5, -1, 6], lines.Select(l => l.Row));

        // The first heading is shorter than the later ones; every line is 2 below the one before.
        Assert.Equal((0f, ClipboardPanelLayout.HeadingHeight), (lines[0].Y, lines[0].Height));
        Assert.Equal(ClipboardPanelLayout.LaterHeadingHeight, lines[3].Height);
        for (int i = 1; i < lines.Count; i++)
        {
            Assert.Equal(lines[i - 1].Y + lines[i - 1].Height + ClipboardPanelLayout.LineGap, lines[i].Y);
        }

        // A heading's words sit at the bottom of its line, inside it.
        var words = ClipboardPanelLayout.HeadingSlot(lines[3], Clipboard.List.Width);
        Assert.Equal(TextStyle.Overline, words.Style);
        Assert.True(words.Slot.Top >= lines[3].Y && words.Slot.Bottom <= lines[3].Y + lines[3].Height);

        // The age: minutes while just now, then the clock time in the day's own offset.
        Assert.Equal("1 min", rows[0].When(Now));
        Assert.Equal("14:49", rows[2].When(Now));
    }

    [Fact]
    public void APressFindsTheRowTheSwitchTheFilterThePreviewAndClearAll()
    {
        var rows = Fresh(3);
        var targets = Targets(rows, selected: rows[0]);
        var lines = ClipboardPanelLayout.Lines(rows, Now);
        float rowMid = Clipboard.List.Y + lines[1].Y + ClipboardPanelLayout.RowHeight / 2;
        Assert.Equal("row:1", HitId(targets, new(100, rowMid)));
        Assert.Equal("row:2", HitId(targets, new(100, rowMid + ClipboardPanelLayout.RowPitch)));
        // The heading and the space under the last row hit nothing.
        Assert.Null(HitId(targets, new(100, Clipboard.List.Y + 5)));
        Assert.Null(HitId(targets, new(100, rowMid + 3 * ClipboardPanelLayout.RowPitch)));

        var sw = Clipboard.Switch;
        Assert.Equal("switch", HitId(targets, new(sw.X - 2, sw.Y - 2)));
        Assert.Equal("switch", HitId(targets, new(20, sw.Y + 5)));
        // The filter is a box: the press goes to its text box, never a target.
        Assert.Null(HitId(targets, new(Clipboard.Filter.X + 40, Clipboard.Filter.Y + 18)));

        foreach (var (action, box) in Clipboard.Actions())
        {
            Assert.Equal(ClipboardPanelLayout.ActionId(action), HitId(targets, new(box.X + box.Width / 2, box.Y + box.Height / 2)));
        }

        var clear = Clipboard.ClearAll(asking: false);
        Assert.Equal("clear", HitId(targets, new(clear.X + 5, clear.Y + 5)));
        // Asking, the button is wider: its new left part is Clear all too, and only then.
        var ask = Clipboard.ClearAll(asking: true);
        Assert.Equal("clear", HitId(Targets(rows, asking: true), new(ask.X + 5, ask.Y + 5)));
        Assert.Null(HitId(targets, new(ask.X + 5, ask.Y + 5)));

        // Off there is only the switch; on and empty the switch and the filter, with no Clear all to press.
        Assert.Equal(["switch"], Clipboard.Targets(on: false, rows, lines, 0, asking: false).Select(t => t.Id));
        Assert.Equal(["switch", "filter"], Clipboard.Targets(on: true, [], [], 0, asking: false).Select(t => t.Id));
        // A filter that hides every row keeps Clear all, which clears what it hides too.
        Assert.Equal(["switch", "filter", "clear"], Clipboard.Targets(on: true, [], [], 0, asking: false, any: true).Select(t => t.Id));
    }

    [Fact]
    public void TheTargetsAreBigEnoughNamedAndInTabOrder()
    {
        var rows = Fresh(3);
        var targets = Targets(rows, selected: rows[1]);
        Assert.Equal(["switch", "filter", "row:1", "row:2", "row:3", "preview:Paste", "preview:Pin", "preview:Shelf", "preview:Remove", "clear"], targets.Select(t => t.Id));
        Assert.All(targets, t => Assert.True(t.Bounds.Width >= StateLook.MinTarget && t.Bounds.Height >= StateLook.MinTarget, t.Id));

        var sw = targets[0];
        Assert.True(sw.Bounds.Contains(Clipboard.Switch));
        Assert.False(sw.Bounds.IntersectsWith(Clipboard.List));
        Assert.Equal((TargetKind.Toggle, true, "Keep clipboard history: on"), (sw.Kind, sw.Checked, sw.Tip));
        Assert.Equal("Keep clipboard history: off", Clipboard.Targets(on: false, [], [], 0, asking: false)[0].Tip);
        Assert.Equal(TargetKind.Box, targets[1].Kind);

        Assert.Equal((TargetKind.Row, "Copy again", 1), (targets[2].Kind, targets[2].Tip, targets[2].Group));
        Assert.Equal("Copy again (Enter)", targets[2].TipWith(keys: true));
        // The preview's buttons are a group of their own; Remove shows only its glyph, so its tip names it.
        Assert.All(targets.Where(t => t.Id.StartsWith("preview:", StringComparison.Ordinal)), t => Assert.Equal(2, t.Group));
        Assert.Equal("Remove (Delete)", targets[8].TipWith(keys: true));
        // Primary actions are at least 36 high.
        Assert.All(Clipboard.Actions(), a => Assert.Equal(36, a.Box.Height));

        // Clear all is alone in its Tab stop, and grows to the left when it asks.
        var clear = targets[^1];
        Assert.Equal(0, clear.Group);
        var ask = Clipboard.ClearAll(asking: true);
        Assert.Equal(clear.Bounds.Right, ask.Right);
        Assert.True(ask.Width > clear.Bounds.Width);

        // Without the preview's room there are no preview buttons.
        var narrow = new ClipboardPanelLayout(new Vector2(480, Clipboard.Body.Y));
        Assert.DoesNotContain(Targets(rows, selected: rows[1], layout: narrow), t => t.Id.StartsWith("preview:", StringComparison.Ordinal));
    }

    [Fact]
    public void ThePreviewHoldsItsMetaItsTextAndItsButtons()
    {
        var card = Clipboard.Preview;
        var actions = Clipboard.Actions();
        Assert.Equal([ClipboardPanelLayout.PreviewAction.Paste, ClipboardPanelLayout.PreviewAction.Pin, ClipboardPanelLayout.PreviewAction.Shelf, ClipboardPanelLayout.PreviewAction.Remove], actions.Select(a => a.Action));
        for (int i = 0; i < actions.Count; i++)
        {
            Assert.True(RectangleF.Inflate(card, -14, -14).Contains(actions[i].Box), $"{actions[i].Action} at {actions[i].Box}");
            if (i > 0)
            {
                Assert.Equal(actions[i - 1].Box.Right + ClipboardPanelLayout.ButtonGap, actions[i].Box.Left, 2);
            }
        }

        // Paste takes the room the others leave; Pin is as wide as Unpin, and every word fits its button.
        Assert.True(actions[0].Box.Width > actions[1].Box.Width);
        Assert.True(TextSlot.Measure("Unpin", ClipboardPanelLayout.SecondaryButtonStyle, 500) <= actions[1].Box.Width - 2 * ClipboardPanelLayout.ButtonPad);
        Assert.True(TextSlot.Measure("To shelf", ClipboardPanelLayout.SecondaryButtonStyle, 500) <= actions[2].Box.Width - 2 * ClipboardPanelLayout.ButtonPad + 0.01);

        // The meta line on top, the text between it and the buttons, line by line as many as fit.
        var meta = Clipboard.PreviewMeta("Text · 84 chars · from Outlook");
        var area = Clipboard.PreviewArea;
        Assert.True(meta.Slot.Bottom < area.Top && area.Bottom < actions[0].Box.Top);
        var run = Clipboard.PreviewText("Thanks Lea, the new copy is in the doc.");
        Assert.Equal((int)(area.Height / 22.4f), run.Lines);
        Assert.Equal(area.Width, run.MaxWidth);

        // A picture fits whole, centred, and is never drawn larger than it is.
        var wide = Clipboard.PreviewImage(1600, 900);
        Assert.True(area.Contains(RectangleF.Inflate(wide, -0.01f, -0.01f)));
        Assert.Equal(1600f / 900, wide.Width / wide.Height, 2);
        Assert.Equal(area.X + area.Width / 2, wide.X + wide.Width / 2, 2);
        Assert.Equal((64f, 64f), (Clipboard.PreviewImage(64, 64).Width, Clipboard.PreviewImage(64, 64).Height));
        Assert.True(Clipboard.PreviewImage(0, 10).IsEmpty);
    }

    [Fact]
    public void RowsOutOfViewHaveNoHeightAndFocusCanScrollThemIn()
    {
        var ten = Fresh(10);
        var lines = ClipboardPanelLayout.Lines(ten, Now);
        var targets = Targets(ten);
        RectangleF Row(IReadOnlyList<Target> all, long id) => all.First(t => t.Id == ClipboardPanelLayout.RowId(id)).Bounds;

        // Six rows show under the heading; the seventh is cut and the eighth keeps its place with no height.
        Assert.Equal(ClipboardPanelLayout.RowHeight, Row(targets, 6).Height);
        var eighth = Row(targets, 8);
        Assert.Equal((0f, Clipboard.List.Y + lines[8].Y), (eighth.Height, eighth.Y));
        Assert.Null(HitId(targets, new(100, Clipboard.Foot.Y + 5)));

        // Down from the sixth row finds the seventh all the same.
        int sixth = targets.ToList().FindIndex(t => t.Id == "row:6");
        Assert.Equal("row:7", targets[Target.Arrow(targets, sixth, new(0, 1))].Id);

        // Focus on the eighth scrolls just far enough to show it whole; the heading then is out of view.
        float scroll = Clipboard.ScrolledTo(lines, 7, 0);
        Assert.Equal(lines[8].Y + ClipboardPanelLayout.RowHeight - Clipboard.List.Height, scroll);
        var scrolled = Targets(ten, scroll);
        Assert.Equal(ClipboardPanelLayout.RowHeight, Row(scrolled, 8).Height);
        // Back to the first row brings its heading with it; a row in view does not scroll.
        Assert.Equal(0, Clipboard.ScrolledTo(lines, 0, scroll));
        Assert.Equal(scroll, Clipboard.ScrolledTo(lines, 4, scroll));

        // A row half out of view is cut to the list, so its target never covers the head or the foot.
        var half = Targets(ten, lines[1].Y + ClipboardPanelLayout.RowHeight / 2);
        Assert.Equal(Clipboard.List.Y, Row(half, 1).Y);
        Assert.Equal(ClipboardPanelLayout.RowHeight / 2, Row(half, 1).Height);
    }

    [Fact]
    public void TheWheelScrollsOneRowANotchWithinTheList()
    {
        var rows = Fresh(50);
        var lines = ClipboardPanelLayout.Lines(rows, Now);
        float max = Clipboard.MaxScroll(lines);
        Assert.Equal(ClipboardPanelLayout.HeadingHeight + 50 * ClipboardPanelLayout.RowPitch - Clipboard.List.Height, max);
        Assert.Equal(0, Clipboard.MaxScroll(ClipboardPanelLayout.Lines(Fresh(3), Now)));

        float down = Clipboard.Scrolled(0, -120, lines);
        Assert.Equal(ClipboardPanelLayout.RowPitch, down);
        Assert.Equal(0, Clipboard.Scrolled(down, 240, lines));
        Assert.Equal(max, Clipboard.Scrolled(max - 1, -120, lines));

        // Scrolled by one row, the top of the list shows the second row.
        float top = Clipboard.List.Y + ClipboardPanelLayout.RowHeight;
        Assert.Equal("row:2", HitId(Targets(rows, down), new(100, top)));
    }

    public static TheoryData<double> Scales => [1.0, 1.25, 1.5];

    [Theory]
    [MemberData(nameof(Scales))]
    public void APressHitsTheSameTargetAtEveryScale(double scale)
    {
        var five = Fresh(5);
        var targets = Targets(five, selected: five[0]);
        var lines = ClipboardPanelLayout.Lines(five, Now);
        float row2 = Clipboard.List.Y + lines[2].Y + ClipboardPanelLayout.RowHeight / 2;
        Assert.Equal("row:2", HitId(targets, BodyPoint(100, row2, scale)));
        var sw = Clipboard.SwitchArea;
        Assert.Equal("switch", HitId(targets, BodyPoint(sw.X + sw.Width / 2, sw.Y + sw.Height / 2, scale)));
        var pin = Clipboard.Actions()[1].Box;
        Assert.Equal("preview:Pin", HitId(targets, BodyPoint(pin.X + pin.Width / 2, pin.Y + pin.Height / 2, scale)));
        var clear = Clipboard.ClearAll(asking: false);
        Assert.Equal("clear", HitId(targets, BodyPoint(clear.X + clear.Width / 2, clear.Y + clear.Height / 2, scale)));
    }

    [Fact]
    public void EveryPartIsInsideTheBodyAndClearOfItsRoundedCorners()
    {
        // The body's bottom corners are the shape's, radius 28 at most; the tab strip keeps the top ones away.
        const float radius = 28;
        var body = Clipboard.Body;
        RectangleF[] parts = [Clipboard.Head, Clipboard.SwitchArea, Clipboard.Filter, Clipboard.List, Clipboard.Preview, Clipboard.Foot, Clipboard.ClearAll(false), Clipboard.ClearAll(true)];
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
        var size = (W: (double)Clipboard.Body.X, PanelSize.Clipboard.H);
        double originX = 1 + IslandGeometry.Panel(size.W, size.H).OuterWidth / 2 * scale;
        int x = (int)Math.Round(originX + (bodyX - size.W / 2) * scale);
        int y = (int)Math.Round(1 + (bodyY + PanelSpace.Head) * scale);
        return PanelSpace.ToBody(PanelSpace.FromPixels(x, y, (float)originX, 1, scale), size.W);
    }

    [Fact]
    public void TheNoteIsTypedInsideItsCard()
    {
        // 330 tall less the tab row: under the title line a 190 DIP card, the text box 14 in from its sides and 12 from its ends.
        var layout = new ScratchpadPanelLayout(new Vector2(520, 278));
        Assert.Equal(190, layout.Note.Height, 0.01);
        Assert.Equal(16, layout.Note.X);
        Assert.Equal(RectangleF.FromLTRB(layout.Note.X + 14, layout.Note.Y + 12, layout.Note.Right - 14, layout.Note.Bottom - 12), layout.Editor);
    }

    [Fact]
    public void ClipboardWordsStayClearOfTheirNeighbours()
    {
        float width = Clipboard.List.Width;
        var text = new ClipboardRow(1, "Meet at 10, not 9", Now.AddMinutes(-14));

        // At rest the age ends at the row's padding and the text before the age's measured width.
        var row = ClipboardPanelLayout.RowText(text, "14:46", ClipboardPanelLayout.RowEnd.Age, width);
        Assert.Equal(width - ClipboardPanelLayout.RowPad, row[0].Slot.Right, 0.01);
        Assert.Equal(TextAlign.Trailing, row[0].Style.Align);
        Assert.True(row[1].Slot.Right <= row[0].Slot.Left - ClipboardPanelLayout.IconGap + 0.01);
        Assert.Equal(ClipboardPanelLayout.IconIn.Right + ClipboardPanelLayout.IconGap, row[1].Slot.X);

        // Selected, the Enter keycap stands where the age was; pinned, the pin; the text stops before either.
        var selected = ClipboardPanelLayout.RowText(text, "14:46", ClipboardPanelLayout.RowEnd.Enter, width);
        Assert.Single(selected);
        var enter = ClipboardPanelLayout.EnterIn(width);
        Assert.Equal(width - ClipboardPanelLayout.RowPad, enter.Right, 0.01);
        Assert.True(selected[0].Slot.Right <= enter.Left);
        var pinned = ClipboardPanelLayout.RowText(text, "14:46", ClipboardPanelLayout.RowEnd.Pin, width);
        Assert.True(pinned[0].Slot.Right <= ClipboardPanelLayout.PinIn(width).Left);
        Assert.True(ClipboardPanelLayout.PinIn(width).Top >= 0 && ClipboardPanelLayout.PinIn(width).Bottom <= ClipboardPanelLayout.RowHeight);

        // A colour's value is in mono.
        var colour = ClipboardPanelLayout.RowText(new ClipboardRow(2, "#3D7A5A", Now), "now", ClipboardPanelLayout.RowEnd.Age, width);
        Assert.Equal(TextStyle.Mono, colour[1].Style.Family);

        // The footer: after the lock, the note ends before Clear all at its widest, so asking never moves it.
        var kept = new ClipboardRetention();
        foreach (bool asking in new[] { false, true })
        {
            var foot = Clipboard.FootText(asking, kept);
            Assert.Equal("Items a password manager marks private are never kept · last 50 text, 50 images", foot[0].Text);
            Assert.True(foot[0].Slot.Left >= Clipboard.LockBox.Right);
            Assert.True(foot[0].Slot.Right <= Clipboard.ClearAll(true).X, "the note runs into Clear all");
            using var note = TextLayout.Of(new TextRun(foot[0].Text, foot[0].Style, foot[0].Slot.Width));
            Assert.False(note.Trimmed);
            Assert.Equal(Clipboard.ClearAll(asking), foot[1].Slot);
            Assert.Equal(asking ? "Clear all? Click again" : "Clear all", foot[1].Text);
            Assert.Equal(asking ? Theme.Warn : Theme.Ink2, foot[1].Style.Color);
            using var word = TextLayout.Of(new TextRun(foot[1].Text, foot[1].Style, foot[1].Slot.Width));
            Assert.False(word.Trimmed, foot[1].Text);
        }

        // Off: two centred lines in the middle of the box; a filter that matches nothing says so.
        var box = Clipboard.BoxText(on: false, kept);
        Assert.Equal(2, box.Count);
        Assert.Equal("History is off. Turn it on to keep 50 pieces of text and 50 images on this PC.", box[0].Text);
        Assert.All(box, line => Assert.True(TextSlot.Measure(line.Text, line.Style, 1000) <= line.Slot.Width, line.Text));
        Assert.All(box, line => Assert.Equal(TextAlign.Center, line.Style.Align));
        Assert.Equal(Clipboard.Box.Y + Clipboard.Box.Height / 2, (box[0].Slot.Top + box[1].Slot.Bottom) / 2, 0.01);
        Assert.Equal("Nothing matches the filter.", Clipboard.BoxText(on: true, kept, filtered: true).Single().Text);
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public void AnImageRowHasItsThumbnailLeftAndItsLabelAfterIt(double scale)
    {
        // The label starts after the thumbnail, whole, and ends before the age.
        var thumb = ClipboardPanelLayout.ThumbIn;
        float width = Clipboard.List.Width;
        string label = ClipboardRow.ImageLabel(1920, 1080);
        var image = new ClipboardRow(1, label, Now, [1]) { Width = 1920, Height = 1080 };
        var row = ClipboardPanelLayout.RowText(image, "2 min", ClipboardPanelLayout.RowEnd.Age, width);
        Assert.Equal("Image · 1920 × 1080", row[1].Text);
        Assert.Equal(thumb.Right + ClipboardPanelLayout.IconGap, row[1].Slot.X);
        Assert.True(TextSlot.Measure(label, TextStyle.Primary, 1000) <= row[1].Slot.Width);
        Assert.True(row[1].Slot.Right < row[0].Slot.Right);
        Assert.Equal((32f, 20f), (thumb.Width, thumb.Height));
        Assert.Equal(ClipboardPanelLayout.RowHeight - thumb.Bottom, thumb.Top);

        // The slot is whole pixels at the scale, so the thumbnail decoded for it shows one to one; the stored one is
        // large enough for it.
        Assert.Equal(0, thumb.Width * scale % 1, 3);
        Assert.Equal(0, thumb.Height * scale % 1, 3);
        Assert.True(ClipboardImages.ThumbWidth >= thumb.Width * scale && ClipboardImages.ThumbHeight >= thumb.Height * scale);
    }

    [Fact]
    public void ShelfWordsFitTheirPlaces()
    {
        var body = new Vector2(760, (float)PanelSize.Shelf.H - PanelSpace.TabStripHeight);
        var head = ShelfLayout.HeadText(body, 12);

        // The count follows the title and ends before the filter; neither is cut.
        Assert.Equal(TextStyle.PageTitle, head[0].Style);
        Assert.Equal("12 items · kept as references", head[1].Text);
        var track = ShelfLayout.Filter(body, ShelfFilter.All).Track;
        Assert.True(head[1].Slot.Right <= track.Left);
        Assert.Equal(body.X - ShelfLayout.PadX, track.Right, 0.01);
        foreach (var line in head)
        {
            using var laid = TextLayout.Of(new TextRun(line.Text, line.Style, line.Slot.Width));
            Assert.False(laid.Trimmed, line.Text);
        }

        // The footer's hint and its keys fit before Clear shelf at its widest; its word fits its button.
        foreach (bool clearing in new[] { false, true })
        {
            var foot = ShelfLayout.Foot(body, clearing);
            Assert.Equal(ShelfLayout.FootKeys.Length, foot.Keys.Count);
            var asking = ShelfLayout.Foot(body, true).Clear;
            Assert.True(foot.Keys[^1].Does.Slot.Right <= asking.Left);
            Assert.True(foot.Hint.Slot.Right <= foot.Keys[0].Cap.Left);
            Assert.Equal(body.X - ShelfLayout.PadX, foot.Clear.Right, 0.01);
            Assert.Equal(clearing ? ShelfLayout.ClearAsk : ShelfLayout.ClearText, foot.ClearLabel.Text);
            using var clear = TextLayout.Of(new TextRun(foot.ClearLabel.Text, foot.ClearLabel.Style, foot.ClearLabel.Slot.Width));
            Assert.False(clear.Trimmed);
        }

        // The dashed tile's words fit it whole on at most two lines.
        var (_, words, _) = ShelfLayout.DropSlotText(ShelfLayout.Tile(0, body).Size);
        using (var laid = TextLayout.Of(words))
        {
            Assert.False(laid.Trimmed);
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
        float title = TextSlot.Measure("Scratchpad", TextStyle.PageTitle, 500);

        Assert.Equal(layout.Head.X + title + 10, frame[1].Slot.X, 0.01);
        Assert.Equal(layout.Foot.Right, frame[3].Slot.Right, 0.01);
    }
}
