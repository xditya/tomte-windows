using System.Drawing;
using Tomte.Core;
using Tomte.Core.Triggers;
using Xunit;

namespace Tomte.Tests;

// "Every monitor": which top edges reveal the island, and where it rests on each monitor at that monitor's scale.
public class MultiMonitorTests
{
    // The primary at the origin; one to its right, shorter and set 200 px lower; one to its left whose top is higher;
    // and one stacked above the primary's right half, so part of the primary's top edge leads on to it.
    private static readonly Rectangle Primary = new(0, 0, 2560, 1440);
    private static readonly Rectangle Right = new(2560, 200, 1920, 1080);
    private static readonly Rectangle Left = new(-1920, -300, 1920, 1200);
    private static readonly Rectangle Above = new(1500, -1080, 1920, 1080);

    private static readonly (Rectangle, double)[] Layout = [(Primary, 1.5), (Right, 1.0), (Left, 1.25), (Above, 1.0)];

    private static RevealEdges Edges(bool everyMonitor) =>
        RevealEdges.For(Layout, everyMonitor, (bounds, _) => bounds.Left + bounds.Width / 2, 0.30, 48);

    [Fact]
    public void EveryMonitorGetsAZoneOnItsOwnTopEdgeAtItsOwnScale()
    {
        var zones = Edges(everyMonitor: true).Zones;

        Assert.Equal(4, zones.Count);
        // 30% of each top edge, centred under the island's middle there; the drag band is 48 DIPs at that monitor's scale.
        Assert.Equal(new RevealZone(896, 1664, 0, 72), zones[0]);
        Assert.Equal(new RevealZone(3232, 3808, 200, 248), zones[1]);
        Assert.Equal(new RevealZone(-1248, -672, -300, -240), zones[2]);
        Assert.Equal(new RevealZone(2172, 2748, -1080, -1032), zones[3]);
    }

    [Fact]
    public void APushRevealsOnlyWhereTheTopIsARealScreenEdge()
    {
        var edges = Edges(everyMonitor: true);

        // The primary's zone left of the monitor stacked above it is an edge; under that monitor the pointer just moves on.
        Assert.Equal(0, edges.EdgeAt(1000, 0));
        Assert.Equal(-1, edges.EdgeAt(1600, 0));
        // The lower monitor's top row is its own edge, not the primary's row 0.
        Assert.Equal(1, edges.EdgeAt(3520, 200));
        Assert.Equal(-1, edges.EdgeAt(3520, 0));
        Assert.Equal(-1, edges.EdgeAt(3520, 201));
        // The higher one on the left, and the one stacked on top.
        Assert.Equal(2, edges.EdgeAt(-960, -300));
        Assert.Equal(3, edges.EdgeAt(2460, -1080));
        // Off the zone along the edge.
        Assert.Equal(-1, edges.EdgeAt(3000, 200));
    }

    [Fact]
    public void PrimaryOnlyKeepsTheOneZone()
    {
        var edges = Edges(everyMonitor: false);

        Assert.Equal(new[] { new RevealZone(896, 1664, 0, 72) }, edges.Zones);
        Assert.Equal(0, edges.EdgeAt(1000, 0));
        Assert.Equal(-1, edges.EdgeAt(3520, 200));
        Assert.False(edges.InDragBand(3520, 240));
        // A monitor above still blocks part of the primary's edge.
        Assert.Equal(-1, edges.EdgeAt(1600, 0));
    }

    [Fact]
    public void ADragReachesTheBandUnderAnyZone()
    {
        var edges = Edges(everyMonitor: true);

        Assert.True(edges.InDragBand(3520, 248));
        Assert.False(edges.InDragBand(3520, 249));
        Assert.True(edges.InDragBand(1280, 72));
        Assert.False(edges.InDragBand(1280, 73));
        Assert.False(RevealEdges.None.InDragBand(1280, 0));
        Assert.Equal(-1, RevealEdges.None.EdgeAt(1280, 0));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-120)]
    [InlineData(5000)]
    [InlineData(-5000)]
    public void TheIslandHangsFromItsMonitorsTopEdgeAndStaysOnIt(double offset)
    {
        foreach (var (monitor, scale) in Layout)
        {
            foreach (var shape in new[] { IslandGeometry.Compact, IslandGeometry.Panel(560, 260) })
            {
                var window = IslandPlace.Window(offset, shape, scale, monitor, attached: false);
                var onPrimaryAxes = IslandOutline.WindowBounds(shape, scale, IslandPlace.Center(offset, shape, scale, monitor.Width, false), false);

                // The same shape as on a monitor at the origin, moved to this one.
                Assert.Equal(onPrimaryAxes.Size, window.Size);
                Assert.Equal(monitor.Top, window.Top);
                Assert.Equal(monitor.Left + onPrimaryAxes.Left, window.Left);
                Assert.True(window.Left >= monitor.Left && window.Right <= monitor.Right, $"{shape} at {offset} on {monitor}: {window}");
                Assert.Equal(monitor.Left + IslandPlace.Center(offset, shape, scale, monitor.Width, false), IslandPlace.Column(offset, shape, scale, monitor, false));
            }
        }
    }

    [Fact]
    public void TheSamePlaceIsTheSameDipOffsetAtEachMonitorsScale()
    {
        // 100 DIPs right of the middle: 150 px at 150%, 100 px at 100%.
        Assert.Equal(1280 + 150, IslandPlace.Column(100, IslandGeometry.Compact, 1.5, Primary, false));
        Assert.Equal(3520 + 100, IslandPlace.Column(100, IslandGeometry.Compact, 1.0, Right, false));

        // The window is sized at the monitor's scale: taller and wider on the 150% one.
        var big = IslandPlace.Window(0, IslandGeometry.Compact, 1.5, Primary, false);
        var small = IslandPlace.Window(0, IslandGeometry.Compact, 1.0, Right, false);
        Assert.True(big.Width > small.Width && big.Height > small.Height);
        Assert.Equal(IslandOutline.WindowBounds(IslandGeometry.Compact, 1.0, 0, false).Size, small.Size);
    }
}
