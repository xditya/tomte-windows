using System.Numerics;
using Tomte.Core;
using Xunit;

namespace Tomte.Tests;

public class IslandOutlineTests
{
    public static TheoryData<IslandGeometry> VisibleStates =>
    [
        IslandGeometry.Compact,
        IslandGeometry.ShelfDrop,
        IslandGeometry.Panel(PanelSize.Shelf.W, PanelSize.Shelf.H),
        IslandGeometry.Panel(PanelSize.Media.W, PanelSize.Media.H),
        IslandGeometry.Panel(PanelSize.PaletteWidth, PanelSize.PaletteMaxHeight),
        new(140, 6, 2, 1.5, 2), // early in a reveal: corner taller than the depth left under the fillet
        new(200, 30, 16, 10, 12), // mid-reveal: fillet lands on the corner curve
    ];

    private static List<Vector2> Flat(IslandGeometry g)
    {
        var segments = new List<OutlineSegment>();
        var points = new List<Vector2>();
        IslandOutline.Build(g, segments);
        IslandOutline.Flatten(segments, points);
        return points;
    }

    [Theory]
    [MemberData(nameof(VisibleStates))]
    public void OutlineStartsAndEndsAboveTheScreenAndIsSymmetric(IslandGeometry g)
    {
        var points = Flat(g);
        float width = (float)g.OuterWidth;

        Assert.Equal(new Vector2(0, -IslandOutline.TopOverhang), points[0]);
        Assert.Equal(new Vector2(width, -IslandOutline.TopOverhang), points[^1]);
        Assert.Equal((float)g.Depth, points.Max(p => p.Y), 3);
        Assert.All(points, p => Assert.InRange(p.X, -0.001f, width + 0.001f));

        // Mirror every point about the mid-line and expect it on the outline (within flattening error).
        foreach (var p in points)
        {
            var mirrored = new Vector2(width - p.X, p.Y);
            Assert.True(points.Any(q => Vector2.Distance(q, mirrored) < 0.01f), $"no mirror for {p} in {g}");
        }
    }

    [Fact]
    public void HiddenIsAStripAboveTheScreen()
    {
        var points = Flat(IslandGeometry.Hidden);
        Assert.Equal(4, points.Count);
        Assert.All(points, p => Assert.InRange(p.Y, -IslandOutline.TopOverhang, 0));
    }

    [Fact]
    public void FilletHandlesSitAtSeventyTwoPercentOfTheRamp()
    {
        var segments = new List<OutlineSegment>();
        IslandOutline.Build(IslandGeometry.Compact, segments);
        var fillet = segments[2];
        Assert.Equal(OutlineSegmentKind.Cubic, fillet.Kind);
        Assert.Equal(0.72f * 10, fillet.Control1.X, 3);
        Assert.Equal(0, fillet.Control1.Y);
        Assert.Equal(10, fillet.End.X, 3); // lands on the straight side
        Assert.Equal(12, fillet.End.Y, 3); // at the ramp height
        Assert.Equal(12 - 0.72f * 10, fillet.Control2.Y, 3);
    }

    [Fact]
    public void BottomCornersAreQuadraticsWithTheSharpCornerAsControl()
    {
        var segments = new List<OutlineSegment>();
        IslandOutline.Build(IslandGeometry.Compact, segments);
        var corner = segments.First(s => s.Kind == OutlineSegmentKind.Quadratic);
        Assert.Equal(new Vector2(10, 38), corner.Control1);
        Assert.Equal(new Vector2(10 + 16, 38), corner.End);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void RegionAgreesWithTheOutlineWithinOnePixelOutward(double scale)
    {
        foreach (var g in new[] { IslandGeometry.Compact, IslandGeometry.Panel(PanelSize.Clipboard.W, PanelSize.Clipboard.H) })
        {
            var points = Flat(g);
            var region = new List<System.Drawing.Point>();
            IslandOutline.RegionPoints(points, scale, g.OuterWidth / 2, margin: 1, region);

            Assert.Equal(points.Count, region.Count);
            double cx = g.OuterWidth / 2 * scale;
            for (int i = 0; i < points.Count; i++)
            {
                double x = points[i].X * scale, y = points[i].Y * scale;
                double dx = region[i].X - 1 - x, dy = region[i].Y - 1 - y;
                Assert.InRange(x < cx ? -dx : dx, 0, 1); // outward in x, never inward
                Assert.InRange(y > 0 ? dy : -dy, 0, 1); // downward below the edge, never into the shape
            }
        }
    }
}
