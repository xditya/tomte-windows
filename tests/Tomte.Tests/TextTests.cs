using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Media;
using Tomte.Core.Shortcuts;
using Tomte.Core.Text;
using Xunit;

namespace Tomte.Tests;

public class TextTests
{
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
        Assert.Equal(MediaPanelLayout.Elapsed.Width, MediaPanelLayout.Total.Width);
        // Both clear of the bar between them.
        Assert.True(MediaPanelLayout.Elapsed.Right <= MediaPanelLayout.Bar.Left);
        Assert.True(MediaPanelLayout.Total.Left >= MediaPanelLayout.Bar.Right);
    }

    [Fact]
    public void TheCompactLinesFitThe220Pill()
    {
        // The pill is 220 wide; the tomte and its gap take 5.5 + 26 + 9 on the left, 12 stay free on the right,
        // and the timer's ring takes 18 + 9 more on its done pill.
        var free = CompactLayout.Line(mascot: true, ring: false);
        var afterRing = CompactLayout.Line(mascot: true, ring: true);
        using var hint = Lay("Push your cursor up here", CompactLayout.LineStyle, free.Width);
        using var done = Lay("Timer done · 25:00", CompactLayout.LineStyle, afterRing.Width);

        Assert.Equal(new RectangleF(-69.5f, 0, 167.5f, 38), free);
        Assert.Equal(new RectangleF(-42.5f, 0, 140.5f, 38), afterRing);
        Assert.Equal(new RectangleF(-102, 0, 200, 38), CompactLayout.Line(mascot: false, ring: false));
        Assert.False(hint.Trimmed);
        Assert.False(done.Trimmed);
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
