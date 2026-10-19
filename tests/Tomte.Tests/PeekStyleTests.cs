using Tomte.Core;
using Xunit;

namespace Tomte.Tests;

public sealed class PeekStyleTests
{
    private static System.Drawing.Rectangle Window(IslandGeometry g, double scale) => IslandOutline.WindowBounds(g, scale, 1280, attached: false);

    private static List<System.Drawing.Point> Region(IslandGeometry g, double scale)
    {
        var region = new List<System.Drawing.Point>();
        IslandOutline.Region(g, scale, attached: false, [], [], region);
        return region;
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    [InlineData(2.0)]
    public void ThePerchedTomteSitsOnTheTopEdgeAndOnlyItsBoxTakesThePointer(double scale)
    {
        var g = IslandGeometry.Perched;
        Assert.Equal(1, g.Bare);
        // The tomte's box starts on the screen's top edge.
        Assert.Equal(0, MascotArt.Box(g).Y - (g.Lift - IslandOutline.Gap), 3);

        var window = Window(g, scale);
        var hover = IslandOutline.PointerBounds(window, g, scale, attached: false);
        int size = (int)Math.Ceiling(MascotArt.SizeDips * scale);
        Assert.Equal(0, hover.Top);
        Assert.InRange(hover.Width, size, size + 1);
        Assert.InRange(hover.Height, size - 1, size + 1);
        // Centred on the island's place.
        Assert.InRange(hover.Left + hover.Width / 2.0 - 1280, -1, 1);

        // The window lets clicks through everywhere but that box.
        var region = Region(g, scale);
        Assert.Equal(4, region.Count);
        Assert.Equal(new System.Drawing.Rectangle(hover.Left - window.Left, hover.Top, hover.Width, hover.Height), System.Drawing.Rectangle.FromLTRB(region[0].X, region[0].Y, region[2].X, region[2].Y));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void TheLipHangsSixFromTheEdgeAndTakesNothingBelowIt(double scale)
    {
        var g = IslandGeometry.Lip;
        var window = Window(g, scale);
        var hover = IslandOutline.PointerBounds(window, g, scale, attached: false);
        Assert.Equal(System.Drawing.Rectangle.FromLTRB(window.Left + window.Width / 2 - hover.Width / 2, 0, window.Left + window.Width / 2 - hover.Width / 2 + hover.Width, hover.Bottom), hover);
        Assert.Equal((int)Math.Ceiling(72 * scale), hover.Width);
        Assert.InRange(hover.Bottom, Math.Round(IslandGeometry.LipDepth * scale), Math.Round(IslandGeometry.LipDepth * scale) + 1);

        // Its region is the lip itself: from the edge down to its rounded bottom, no shadow room around it.
        var region = Region(g, scale);
        Assert.Equal(0, region.Min(p => p.Y));
        Assert.InRange(region.Max(p => p.Y), hover.Bottom - 1, hover.Bottom + 1);
        Assert.InRange(region.Min(p => p.X) + window.Left, hover.Left - 1, hover.Left);
        Assert.InRange(region.Max(p => p.X) + window.Left, hover.Right, hover.Right + 1);
    }

    [Fact]
    public void TheCornerPillHoldsATwentyTomteAtItsInsetAndTheTimeAfterIt()
    {
        float width = CompactLayout.CornerWidth;
        var g = IslandGeometry.CornerPill(width);
        var seat = CompactLayout.CornerSeat;
        var box = MascotArt.Box(g, seat.Shift, seat.Grow);
        Assert.Equal(-width / 2 + CompactLayout.CornerInset, box.X, 3);
        Assert.Equal(CompactLayout.CornerTomte, box.Width, 3);
        // The art's middle row on the pill's middle.
        Assert.Equal(IslandGeometry.CornerDepth / 2, box.Y + MascotArt.MiddleY * box.Height / MascotArt.ViewBox, 3);

        var clock = CompactLayout.CornerClock(width);
        Assert.Equal(box.Right + CompactLayout.CornerGap, clock.X, 3);
        Assert.Equal(width / 2 - CompactLayout.CornerEnd + 1, clock.Right, 3);
        Assert.True(clock.Width >= Tomte.Core.Text.TextSlot.Measure("22:58", CompactLayout.CornerTimeStyle, 4096));
    }

    [Fact]
    public void TheLipHasNoTomteAndThePerchedTomteAndThePillKeepTheirs()
    {
        var lip = CompactLayout.PeekSeat(PeekStyle.Lip)!.Value;
        Assert.Equal(0, MascotArt.Box(IslandGeometry.Lip, lip.Shift, lip.Grow).Width);
        Assert.Null(CompactLayout.PeekSeat(PeekStyle.Perched));
        Assert.Null(CompactLayout.PeekSeat(PeekStyle.Pill));
        Assert.Equal(IslandGeometry.Compact, CompactLayout.PeekShape(PeekStyle.Pill));
    }

    [Theory]
    [InlineData(PeekStyle.Perched, true, false, PeekStyle.Perched)]
    [InlineData(PeekStyle.Perched, false, false, PeekStyle.Lip)]
    [InlineData(PeekStyle.Perched, true, true, PeekStyle.Pill)]
    [InlineData(PeekStyle.Lip, true, true, PeekStyle.Pill)]
    [InlineData(PeekStyle.Corner, false, true, PeekStyle.Corner)]
    public void ThePerchedTomteNeedsTheTomteAndTheGap(PeekStyle style, bool tomte, bool attached, PeekStyle resting) =>
        Assert.Equal(resting, CompactLayout.Resting(style, tomte, attached));

    [Theory]
    [InlineData(1.0, 1920, 0)]
    [InlineData(1.25, 2560, 140)]
    [InlineData(1.5, 2560, -300)]
    public void TheCornerShiftTakesThePillToEitherEndWhereverTheIslandSits(double scale, int screen, double offset)
    {
        var g = IslandGeometry.CornerPill(CompactLayout.CornerWidth);
        var (side, _, _) = IslandOutline.Insets(scale, attached: false);
        // The window keeps on the screen, so the centre goes no further than half the window from either end.
        int window = (int)Math.Ceiling(g.Width * scale) + 2 * side, half = window / 2;
        double right = IslandPlace.ToCorner(offset, g, scale, screen, attached: false, left: false);
        double left = IslandPlace.ToCorner(offset, g, scale, screen, attached: false, left: true);
        Assert.Equal(screen - (window - half), IslandPlace.Center(offset + right, g, scale, screen, false));
        Assert.Equal(half, IslandPlace.Center(offset + left, g, scale, screen, false));
    }
}
