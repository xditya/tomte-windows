using Tomte.Core;
using Xunit;

namespace Tomte.Tests;

// The island moved along the top edge: where each shape's centre goes, and the press that moves it.
public class IslandPlaceTests
{
    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void EveryShapeKeepsTheTopGapFromBothSidesOfTheScreen(double scale)
    {
        const int screen = 2560;
        var (side, _, _) = IslandOutline.Insets(scale, attached: false);
        double gap = IslandOutline.Gap * scale;
        foreach (var g in new[] { IslandGeometry.Peek, IslandGeometry.Compact, IslandGeometry.ShelfDrop, IslandGeometry.Panel(560, 260), IslandGeometry.Panel(640, 150) })
        {
            // Anywhere in the middle stretch the centre goes where it was asked to.
            Assert.Equal(-100, IslandPlace.Clamp(-100, g, scale, screen));
            Assert.Equal(screen / 2 + (int)Math.Round(-100 * scale), IslandPlace.Center(-100, g, scale, screen));

            // Dragged as far as it goes, the outline stops a gap from the side, whichever shape opens there.
            var left = IslandOutline.WindowBounds(g, scale, IslandPlace.Center(-5000, g, scale, screen), attached: false);
            var right = IslandOutline.WindowBounds(g, scale, IslandPlace.Center(5000, g, scale, screen), attached: false);
            Assert.InRange(left.Left + side, gap - 1, gap + 1);
            Assert.InRange(screen - (right.Right - side), gap - 1, gap + 1);
        }

        // A wide panel opened from a pill moved to the corner shifts back in; the pill keeps its place.
        double corner = IslandPlace.Clamp(-5000, IslandGeometry.Peek, scale, screen);
        Assert.True(IslandPlace.Clamp(corner, IslandGeometry.Panel(560, 260), scale, screen) > corner);
        Assert.Equal(corner, IslandPlace.Clamp(corner, IslandGeometry.Peek, scale, screen));

        // A screen too narrow for the shape centres it.
        Assert.Equal(150, IslandPlace.Center(-80, IslandGeometry.Panel(640, 150), scale, 300));
    }

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(3, -3, false)]
    [InlineData(4, 0, true)]
    [InlineData(0, -4, true)]
    [InlineData(-30, 2, true)]
    public void APressBecomesADragAtTheSystemsDragDistance(int dx, int dy, bool drag) =>
        Assert.Equal(drag, IslandPlace.IsDrag(dx, dy, dragX: 4, dragY: 4));

    [Theory]
    [InlineData(0, 0, 100, true)]
    [InlineData(2, -2, 500, true)]
    [InlineData(3, 0, 100, false)]
    [InlineData(0, 0, 501, false)]
    public void ASecondClickCountsInsideTheDoubleClickBoxAndTime(int dx, int dy, long ms, bool twice) =>
        Assert.Equal(twice, IslandPlace.IsDoubleClick(dx, dy, ms, boxX: 4, boxY: 4, timeMs: 500));
}
