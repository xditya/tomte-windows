using Tomte.Core;
using Xunit;

namespace Tomte.Tests;

// The island moved along the top edge: where each shape's centre goes, and the press that moves it.
public class IslandPlaceTests
{
    private static readonly IslandGeometry[] Shapes =
        [IslandGeometry.Peek, IslandGeometry.Compact, IslandGeometry.ShelfDrop, IslandGeometry.Panel(560, 260), IslandGeometry.Panel(640, 150)];

    [Theory]
    [InlineData(1.0, false)]
    [InlineData(1.25, false)]
    [InlineData(1.5, false)]
    [InlineData(1.5, true)]
    public void EveryShapeKeepsItsWindowOnTheScreenAndTheTopGapFromBothSides(double scale, bool attached)
    {
        const int screen = 2560;
        var (side, _, _) = IslandOutline.Insets(scale, attached);
        int gap = IslandOutline.GapPixels(scale);
        foreach (var shape in Shapes)
        {
            var g = attached ? shape.Attached() : shape;

            // Anywhere in the middle stretch the centre goes where it was asked to.
            Assert.Equal(-100, IslandPlace.Clamp(-100, g, scale, screen, attached));
            Assert.Equal(screen / 2 + (int)Math.Round(-100 * scale), IslandPlace.Center(-100, g, scale, screen, attached));

            // Dragged as far as it goes, whichever shape opens there: the whole window, shadow room and the region on
            // it, stays on this monitor, so it takes no clicks from one beside it; the outline stays a gap in.
            foreach (double far in new[] { -5000.0, 5000.0 })
            {
                var window = IslandOutline.WindowBounds(g, scale, IslandPlace.Center(far, g, scale, screen, attached), attached);
                Assert.True(window.Left >= 0 && window.Right <= screen, $"{g} at {far}: window {window}");
                Assert.True(window.Left + side >= gap && screen - (window.Right - side) >= gap, $"{g} at {far}: outline in {window}");
                // The stored offset is the same place: centring on it again lands on the same column.
                double kept = IslandPlace.Clamp(far, g, scale, screen, attached);
                Assert.Equal(IslandPlace.Center(far, g, scale, screen, attached), IslandPlace.Center(kept, g, scale, screen, attached));
            }
        }

        // A screen too narrow for the shape centres it.
        Assert.Equal(150, IslandPlace.Center(-80, IslandGeometry.Panel(640, 150), scale, 300, attached));
        Assert.Equal(0, IslandPlace.Clamp(-80, IslandGeometry.Panel(640, 150), scale, 300, attached));
    }

    // A drag on a wide panel starts from where that panel rests, not from the further-out place a narrower pill was
    // dragged to, so the first DIP inward moves it.
    [Fact]
    public void ADragOnAShapeClampedFurtherInMovesAtOnce()
    {
        const int screen = 2560;
        var panel = IslandGeometry.Panel(560, 260);
        double corner = IslandPlace.Clamp(-5000, IslandGeometry.Peek, 1.5, screen, attached: false);
        double edge = IslandPlace.Clamp(corner, panel, 1.5, screen, attached: false);
        Assert.True(edge > corner);
        Assert.Equal(edge + 1, IslandPlace.Clamp(edge + 1, panel, 1.5, screen, attached: false), 6);
        Assert.Equal(edge, IslandPlace.Clamp(corner + 1, panel, 1.5, screen, attached: false));
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
    [InlineData(true, 0, 0, 100, true)]
    [InlineData(true, 2, -2, 500, true)]
    [InlineData(true, 3, 0, 100, false)]
    [InlineData(true, 0, 0, 501, false)]
    // The tomte, a label or a control: never part of a double click, however quick.
    [InlineData(false, 0, 0, 100, false)]
    public void ASecondClickCountsOnThePillInsideTheDoubleClickBoxAndTime(bool onPill, int dx, int dy, long ms, bool twice) =>
        Assert.Equal(twice, IslandPlace.IsDoubleClick(onPill, dx, dy, ms, boxX: 4, boxY: 4, timeMs: 500));
}
