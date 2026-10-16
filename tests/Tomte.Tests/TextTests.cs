using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Media;
using Tomte.Core.Shortcuts;
using Tomte.Core.Text;
using Tomte.Core.Triggers;
using Xunit;

namespace Tomte.Tests;

public class TextTests
{
    private static readonly MediaPanelLayout MediaLayout = new((float)PanelSize.Media.W);

    private const string Long = "A very long title that will not fit in the slot at all, not even close, and goes on for a while";
    private static readonly TextStyle Title = new(14, 600);
    private static readonly double[] Scales = [1, 1.25, 1.5];

    private static TextLayout Lay(string text, TextStyle style, float width = 1000, int lines = 1) => TextLayout.Of(new TextRun(text, style, width, lines));

    [Fact]
    public void ALineThatFitsIsMeasuredAndNotCut()
    {
        using var layout = Lay("Nightfall", Title, 272);

        Assert.False(layout.Trimmed);
        Assert.Equal(1, layout.LineCount);
        Assert.InRange(layout.Width, 30, 100);
        // One line of 14 DIP type is about 1.33 times the size tall.
        Assert.InRange(layout.Height, 16, 22);
    }

    [Fact]
    public void ALineTooWideEndsInAnEllipsisInsideItsWidth()
    {
        using var natural = Lay(Long, Title);
        using var cut = Lay(Long, Title, 272);

        Assert.True(natural.Width > 272);
        Assert.True(cut.Trimmed);
        Assert.Equal(1, cut.LineCount);
        Assert.InRange(cut.Width, 240, 272);
        Assert.Equal(natural.Height, cut.Height);
    }

    [Fact]
    public void TheUiFamilyIsInstalledUnderTheNameUsed()
    {
        // A family DirectWrite cannot find falls back to Segoe UI without a word, so compare against that.
        const string sample = "Push your cursor up here 0123456789";
        using var ui = Lay(sample, new TextStyle(13, 600));
        using var fallback = Lay(sample, new TextStyle(13, 600, Family: "No Such Family"));

        Assert.NotEqual(fallback.Width, ui.Width, 0.001);
    }

    [Fact]
    public void AOneLineRunStaysOneLineAcrossLineBreaks()
    {
        // A device event can be a list of names, one per line.
        using var broken = Lay("Speakers\nHeadphones (default)\r\nMonitor", Title);
        using var plain = Lay("Speakers Headphones (default) Monitor", Title);

        Assert.Equal(1, broken.LineCount);
        Assert.Equal(plain.Width, broken.Width, 0.01);
        Assert.Equal(plain.Height, broken.Height, 0.01);
    }

    [Fact]
    public void WrappedTextStopsAtItsLineLimit()
    {
        using var one = Lay("Nightfall", Title);
        using var two = Lay(Long, Title, 120, lines: 2);
        using var room = Lay("Short enough", Title, 120, lines: 3);

        Assert.True(two.Trimmed);
        Assert.Equal(2, two.LineCount);
        Assert.Equal(2 * one.Height, two.Height, 0.01);
        Assert.InRange(two.Width, 1, 120);
        // Room for more lines than the text needs: nothing is cut and the run is one line tall.
        Assert.False(room.Trimmed);
        Assert.Equal(1, room.LineCount);
        Assert.Equal(one.Height, room.Height, 0.01);
    }

    [Fact]
    public void MeasuringFollowsTheTextAndTheStyle()
    {
        using var word = Lay("Night", Title);
        using var longer = Lay("Nightfall", Title);
        using var smaller = Lay("Nightfall", new TextStyle(12));
        using var regular = Lay("Nightfall", new TextStyle(14));

        Assert.True(longer.Width > word.Width);
        Assert.True(smaller.Width < regular.Width);
        Assert.True(smaller.Height < regular.Height);
        Assert.True(longer.Width >= regular.Width);
    }

    [Fact]
    public void TabularFiguresKeepAClockOneWidth()
    {
        var clock = new TextStyle(11, Tabular: true);
        using var ones = Lay("11:11", clock);
        using var zeros = Lay("00:00", clock);
        using var proportionalOnes = Lay("11:11", new TextStyle(11));
        using var proportionalZeros = Lay("00:00", new TextStyle(11));

        Assert.Equal(zeros.Width, ones.Width, 0.01);
        Assert.NotEqual(proportionalZeros.Width, proportionalOnes.Width, 0.01);
    }

    [Fact]
    public void TheClockSlotsHoldTheLongestClock()
    {
        // Two digits of hours is the longest a clock gets short of 100 hours; tabular, so any digits measure the same.
        using var longest = Lay(NowPlaying.Clock(TimeSpan.FromHours(99) + TimeSpan.FromSeconds(3599)), MediaPanelLayout.ElapsedStyle, MediaPanelLayout.Elapsed.Width);
        using var natural = Lay("88:88:88", MediaPanelLayout.ElapsedStyle);

        Assert.Equal("99:59:59", longest.Run.Text);
        Assert.False(longest.Trimmed, $"needs {natural.Width} DIP");
        Assert.Equal(MediaPanelLayout.Elapsed.Width, MediaLayout.Total.Width);
        // Both clear of the bar between them.
        Assert.True(MediaPanelLayout.Elapsed.Right <= MediaLayout.Bar.Left);
        Assert.True(MediaLayout.Total.Left >= MediaLayout.Bar.Right);
    }

    [Fact]
    public void TheCompactLinesFitThe220Pill()
    {
        // The pill is 220 wide; the tomte and its gap take 5.5 + 26 + 9 on the left, as much stays free on the right as
        // the tomte's art leaves on the left (8.8), and the timer's ring takes 18 + 9 before its words on its done pill.
        var free = CompactLayout.Line(mascot: true);
        using var hint = Lay("Push your cursor up here", CompactLayout.LineStyle, free.Width);
        float doneWidth = TextSlot.Measure("Timer done · 25:00", CompactLayout.LineStyle, 4096);
        var (_, _, room) = CompactLayout.Group(mascot: true, CompactLayout.RingSize, doneWidth);
        using var done = Lay("Timer done · 25:00", CompactLayout.LineStyle, room);

        Assert.Equal(8.79, CompactLayout.EndInset, 2);
        Assert.Equal((-69.5f, 0f, 38f), (free.X, free.Y, free.Height));
        Assert.Equal(110 - CompactLayout.EndInset, free.Right, 3);
        Assert.Equal(doneWidth, room);
        var alone = CompactLayout.Line(mascot: false);
        Assert.Equal(CompactLayout.EndInset - 110, alone.X, 3);
        Assert.Equal(110 - CompactLayout.EndInset, alone.Right, 3);
        Assert.False(hint.Trimmed);
        Assert.False(done.Trimmed);
    }

    // No Compact pill leaves one end empty. What ends a pill stops as far in from its trailing end as the tomte's art
    // starts from the leading end; words the tomte leads sit at the trailing end of their slot, and words alone are
    // centred on the pill. With the tomte off the row's art starts at that inset too.
    [Fact]
    public void EveryCompactPillUsesBothEnds()
    {
        var flat = new List<Vector2>();
        float artLeft = new[] { MascotArt.Beard, MascotArt.BeardShade, MascotArt.CapCone, MascotArt.TipCurl, MascotArt.Brim }.Min(path =>
        {
            IslandOutline.Flatten([.. path], flat);
            return flat.Min(p => p.X);
        });
        float tomteLeft = MascotArt.Box(IslandGeometry.Compact).X + 110 + (artLeft * MascotArt.SizeDips / MascotArt.ViewBox);
        Assert.InRange(Math.Abs(tomteLeft - CompactLayout.EndInset), 0, 0.05);

        foreach (bool mascot in new[] { true, false })
        {
            Assert.Equal(110 - CompactLayout.EndInset, CompactLayout.Line(mascot).Right, 3);
            Assert.Equal(mascot ? CompactLayout.RingLeft(true) - 110 : CompactLayout.EndInset - 110, CompactLayout.Art(mascot).X, 3);
        }

        Assert.Equal(TextAlign.Trailing, CompactLayout.Ending(CompactLayout.LineStyle, led: true).Align);
        Assert.Equal(TextAlign.Center, CompactLayout.Ending(CompactLayout.ClockStyle, led: false).Align);
        Assert.True(CompactLayout.Ending(CompactLayout.ClockStyle, led: true).Tabular);
        var alone = CompactLayout.Line(mascot: false);
        Assert.Equal(0, alone.X + (alone.Width / 2), 3);

        // The push pill's row keeps its title after the art and up to the bars, as it was.
        Assert.Equal(new RectangleF(-69.5f, 8, 22, 22), CompactLayout.Art(mascot: true));
        Assert.Equal(new RectangleF(-38.5f, 9, 109.5f, 20), CompactLayout.Title(mascot: true));
    }

    // On the pills with no bars (kept while music plays, peek with the tomte off) the art and its title are one group,
    // like the ring and its countdown: the art a gap before the title, ending at the end inset or centred.
    [Fact]
    public void TheArtAndItsTitleAreOneGroupWithoutBars()
    {
        float title = TextSlot.Measure("Nightfall", CompactLayout.TitleStyle, 4096);
        var (art, x, width) = CompactLayout.Group(mascot: true, CompactLayout.Art(true).Width, title);
        Assert.Equal(art + 22 + CompactLayout.Gap, x, 3);
        Assert.Equal(110 - CompactLayout.EndInset, x + width, 3);
        var (alone, aloneX, aloneWidth) = CompactLayout.Group(mascot: false, CompactLayout.Art(false).Width, title);
        Assert.Equal(0, (alone + aloneX + aloneWidth) / 2, 3);
    }

    // The timer's ring and its countdown are one group: the ring a gap before the words, the group ending at the end
    // inset with the tomte on and centred on the pill with it off. The ring moves with the words' width, as the countdown
    // goes from 1:00:00 to 59:59, and words wider than the room are cut to it, never pushing the ring onto the tomte.
    [Fact]
    public void TheRingAndItsWordsAreOneGroup()
    {
        float Width(string words) => TextSlot.Measure(words, CompactLayout.ClockStyle, 4096);
        string hour = CompactRules.Countdown(TimeSpan.FromHours(1)), under = CompactRules.Countdown(TimeSpan.FromSeconds(3599));
        Assert.Equal(("1:00:00", "59:59"), (hour, under));
        foreach (bool mascot in new[] { true, false })
        {
            foreach (string words in new[] { "25:00", under, hour, "Timer done · 25:00" })
            {
                var (ring, x, width) = CompactLayout.Group(mascot, CompactLayout.RingSize, Width(words));
                Assert.Equal(ring + CompactLayout.RingSize + CompactLayout.Gap, x, 3);
                Assert.Equal(Width(words), width);
                float end = x + width, start = ring;
                if (mascot)
                {
                    Assert.Equal(110 - CompactLayout.EndInset, end, 3);
                }
                else
                {
                    Assert.Equal(0, (start + end) / 2, 3);
                }
            }
        }

        var (atHour, _, _) = CompactLayout.Group(mascot: true, CompactLayout.RingSize, Width(hour));
        var (atUnder, _, _) = CompactLayout.Group(mascot: true, CompactLayout.RingSize, Width(under));
        Assert.True(atUnder > atHour, "the ring follows the shorter countdown toward the end");

        var (cutRing, cutX, cut) = CompactLayout.Group(mascot: true, CompactLayout.RingSize, 500);
        Assert.Equal(CompactLayout.RingLeft(true) - 110, cutRing, 3);
        Assert.Equal(110 - CompactLayout.EndInset, cutX + cut, 3);
    }

    [Fact]
    public void PlacementLandsOnWholePixelsAtEachScale()
    {
        // Hosts: island space itself, the media body (x from the centre line, under the 42 DIP tab strip), a
        // body capped to a fractional width.
        (float X, float Y)[] hosts = [(0, 0), (-190, 42), (-175.3f, 42)];
        float[] points = [0, 9, 13.37f, 49.5f, 94, -36, -40.25f];
        foreach (double scale in Scales)
        {
            foreach (var (hx, hy) in hosts)
            {
                foreach (float p in points)
                {
                    float x = PixelGrid.Snap(hx, p, scale), y = PixelGrid.Snap(hy, p, scale);
                    AssertWhole((hx + x) * scale);
                    AssertWhole((hy + y) * scale);
                    // Never further from the asked-for point than half a pixel.
                    Assert.InRange(x - p, -0.5 / scale - 1e-4, 0.5 / scale + 1e-4);
                }
            }
        }

        static void AssertWhole(double pixels) => Assert.Equal(Math.Round(pixels), pixels, 0.002);
    }

    [Fact]
    public void TheSurfaceIsWholePixelsThatCoverTheText()
    {
        foreach (double scale in Scales)
        {
            using var block = new TextBlock();
            block.Set(new TextRun("Nightfall", Title, 272), scale);
            var (width, height) = block.Pixels;

            Assert.Equal((int)Math.Ceiling(block.Size.Width * scale) + 2 * TextBlock.Pad, width);
            Assert.Equal((int)Math.Ceiling(block.Size.Height * scale) + 2 * TextBlock.Pad, height);
            Assert.True((width - 2 * TextBlock.Pad) / scale >= block.Size.Width);
        }

        // A centred or trailing run is drawn inside its whole width; DirectWrite places it there.
        using var total = new TextBlock();
        total.Set(new TextRun("03:15", new TextStyle(11, Align: TextAlign.Trailing, Tabular: true), 40), 1.25);
        Assert.Equal(50 + 2 * TextBlock.Pad, total.Pixels.Width);
    }

    [Fact]
    public void ItDrawsAgainOnlyWhenTheRunOrTheScaleChanges()
    {
        using var block = new TextBlock();
        var run = new TextRun("Nightfall", Title, 272);

        Assert.True(block.Set(run, 1.25));
        var layout = block.Layout;
        // The same run again, even a copy of it: nothing to draw, nothing laid out.
        Assert.False(block.Set(run, 1.25));
        Assert.False(block.Set(run with { }, 1.25));
        Assert.Same(layout, block.Layout);
        // Another scale draws again from the same layout, which is in DIPs.
        Assert.True(block.Set(run, 1.5));
        Assert.Same(layout, block.Layout);
        // Each part of the run lays it out again.
        Assert.True(block.Set(run with { Text = "Daybreak" }, 1.5));
        Assert.NotSame(layout, block.Layout);
        Assert.True(block.Set(run with { Text = "Daybreak", Style = Title with { Color = TextStyle.WarnColor } }, 1.5));
        Assert.True(block.Set(run with { Text = "Daybreak", Style = Title with { Color = TextStyle.WarnColor }, MaxWidth = 200 }, 1.5));
        Assert.True(block.Set(run with { Text = "Daybreak", Style = Title with { Color = TextStyle.WarnColor }, MaxWidth = 200, Lines = 2 }, 1.5));
        Assert.False(block.Set(run with { Text = "Daybreak", Style = Title with { Color = TextStyle.WarnColor }, MaxWidth = 200, Lines = 2 }, 1.5));
    }

    [Fact]
    public void AnEmptyRunDrawsNothing()
    {
        using var block = new TextBlock();

        Assert.True(block.Set(new TextRun("", Title, 272), 1.5));
        Assert.Null(block.Layout);
        Assert.Equal((0, 0), block.Pixels);
        Assert.False(block.Set(new TextRun("", Title, 272), 1.5));
    }

    [Fact]
    public void ALineHeightSpacesEveryLineAlike()
    {
        var note = new TextStyle(13, LineHeight: 19.5f);
        using var three = Lay("one\ntwo\nthree", note, 300, lines: 5);
        using var cut = Lay("one\ntwo\nthree", note, 300, lines: 2);

        Assert.Equal(3, three.LineCount);
        Assert.Equal(3 * 19.5f, three.Height, 0.01);
        Assert.True(cut.Trimmed);
        Assert.Equal(2 * 19.5f, cut.Height, 0.01);
    }

    [Fact]
    public void TheShortcutBadgesFitTheirBand()
    {
        // The Shortcuts body at the panel's 520 x 300, less the tab strip.
        var cell = ShortcutGrid.Cell(0, new Vector2(520, 300 - PanelSpace.TabStripHeight));
        var band = ShortcutGrid.BadgeSlot(cell);
        foreach (var why in new[] { ShortcutOutcome.ScriptsOff, ShortcutOutcome.NeedsConfirm, ShortcutOutcome.Changed, ShortcutOutcome.Missing, ShortcutOutcome.Blocked })
        {
            using var badge = Lay(ShortcutGrid.BadgeText(why), ShortcutGrid.BadgeStyle, band.Width);
            using var natural = Lay(ShortcutGrid.BadgeText(why), ShortcutGrid.BadgeStyle);
            Assert.False(badge.Trimmed, $"{ShortcutGrid.BadgeText(why)} needs {natural.Width} of {band.Width}");
        }

        // Right-aligned 6 DIP in from the tile's edge, and above the name.
        Assert.Equal(cell.Right - 6, band.Right, 0.01);
        Assert.True(band.Bottom < ShortcutGrid.Label(cell).Top);
    }

    [Fact]
    public void TheDeviceLivesWhileTextShowsAndGoesATurnAfterTheLastRun()
    {
        var turns = new Queue<Action>();
        int made = 0;
        var device = new SharedDevice<Fake>(() => Task.FromResult(new Fake(++made)), turns.Enqueue);

        // A panel shows with three runs: one device.
        device.Add();
        device.Add();
        device.Add();
        var first = device.Value!;
        Assert.Equal(1, made);
        // It rebuilds its rows in one turn, every run going and new ones coming: the same device stays.
        device.Remove();
        device.Remove();
        device.Remove();
        device.Add();
        device.Add();
        Run(turns);
        Assert.Same(first, device.Value);
        Assert.False(first.Disposed);
        // It hides: every run goes, and a turn later so does the device.
        device.Remove();
        device.Remove();
        Assert.False(first.Disposed);
        Run(turns);
        Assert.True(first.Disposed);
        Assert.Null(device.Value);
        Assert.Equal(0, device.Users);
        // The next show makes a new one.
        device.Add();
        Assert.Equal(2, device.Value!.Number);
        Assert.Throws<InvalidOperationException>(() =>
        {
            device.Remove();
            device.Remove();
        });

        static void Run(Queue<Action> turns)
        {
            while (turns.TryDequeue(out var turn))
            {
                turn();
            }
        }
    }

    [Fact]
    public void ADeviceThatLandsAfterTheLastRunWentIsReleasedAtOnce()
    {
        var saved = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(null);
        try
        {
            var landing = new TaskCompletionSource<Fake>();
            var device = new SharedDevice<Fake>(() => landing.Task, _ => { });
            int ready = 0;
            device.Ready += () => ready++;
            device.Add();
            device.Remove();
            var late = new Fake(1);
            landing.SetResult(late);

            Assert.True(late.Disposed);
            Assert.Null(device.Value);
            Assert.Equal(0, ready);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(saved);
        }
    }

    private sealed class Fake(int number) : IDisposable
    {
        public int Number => number;

        public bool Disposed { get; private set; }

        public void Dispose() => Disposed = true;
    }
}
