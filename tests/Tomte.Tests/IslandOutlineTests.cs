using System.Numerics;
using Tomte.Core;
using Tomte.Core.Triggers;
using Xunit;

namespace Tomte.Tests;

public class IslandOutlineTests
{
    private static readonly IslandGeometry[] Resting =
    [
        IslandGeometry.Compact,
        IslandGeometry.ShelfDrop,
        IslandGeometry.Panel(PanelSize.Shelf.W, PanelSize.Shelf.H),
        IslandGeometry.Panel(PanelSize.Media.W, PanelSize.Media.H),
        IslandGeometry.Panel(PanelSize.PaletteWidth, PanelSize.PaletteMaxHeight),
    ];

    public static TheoryData<IslandGeometry> Pills =>
    [
        .. Resting,
        new(140, 6, 2), // early in a reveal
        new(200, 30, 16), // mid-reveal: the depth is under two corners
    ];

    public static TheoryData<IslandGeometry> Notches =>
    [
        .. Resting.Select(g => g.Attached()),
        new(140, 6, 2, 1.5, 2), // early in a reveal: corner taller than the depth left under the fillet
        new(200, 30, 16, 10, 12), // mid-reveal: fillet lands on the corner curve
    ];

    private static List<OutlineSegment> Segments(IslandGeometry g, bool attached)
    {
        var segments = new List<OutlineSegment>();
        IslandOutline.Build(g, attached, segments);
        return segments;
    }

    private static List<Vector2> Flat(IslandGeometry g, bool attached)
    {
        var points = new List<Vector2>();
        IslandOutline.Flatten(Segments(g, attached), points);
        return points;
    }

    private static List<System.Drawing.Point> Region(IslandGeometry g, double scale, bool attached)
    {
        var region = new List<System.Drawing.Point>();
        IslandOutline.Region(g, scale, attached, [], [], region);
        return region;
    }

    /// <summary>Even-odd ray cast; the region's edges run between pixel corners.</summary>
    private static bool Holds(List<System.Drawing.Point> polygon, double x, double y)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if (a.Y > y != b.Y > y && x < a.X + (double)(b.X - a.X) * (y - a.Y) / (b.Y - a.Y))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    [Fact]
    public void TheStateTableKeepsItsSizesAndHasNoRamps()
    {
        Assert.Equal(new IslandGeometry(120, 0, 0), IslandGeometry.Hidden);
        Assert.Equal(new IslandGeometry(220, 38, 19), IslandGeometry.Compact);
        Assert.Equal(new IslandGeometry(420, 96, 20), IslandGeometry.ShelfDrop);
        Assert.Equal(new IslandGeometry(560, 260, 22), IslandGeometry.Panel(PanelSize.Shelf.W, PanelSize.Shelf.H));
        Assert.All(Resting, g => Assert.Equal((0.0, 0.0, g.Width), (g.RampW, g.RampH, g.OuterWidth)));
    }

    [Theory]
    [MemberData(nameof(Pills))]
    public void ThePillFillsItsBoxBelowTheGapLineAndIsSymmetric(IslandGeometry g)
    {
        var points = Flat(g, attached: false);
        float w = (float)g.Width, d = (float)g.Depth;

        Assert.Equal(0f, points.Min(p => p.X), 3);
        Assert.Equal(w, points.Max(p => p.X), 3);
        Assert.Equal(0f, points.Min(p => p.Y), 3); // the top edge is the gap line
        Assert.Equal(d, points.Max(p => p.Y), 3);

        // Mirror every point about both mid-lines and expect it on the outline (within flattening error).
        foreach (var p in points)
        {
            foreach (var mirrored in new[] { new Vector2(w - p.X, p.Y), new Vector2(p.X, d - p.Y) })
            {
                Assert.True(points.Any(q => Vector2.Distance(q, mirrored) < 0.01f), $"no mirror for {p} in {g}");
            }
        }
    }

    [Theory]
    [InlineData(220, 38, 19, 19)] // Compact
    [InlineData(220, 38, 16, 16)] // the notch's Compact corner, with room to spare
    [InlineData(420, 96, 20, 20)] // Shelf-drop
    [InlineData(380, 140, 22, 22)] // Media panel
    [InlineData(640, 480, 22, 22)] // Palette, grown
    [InlineData(200, 30, 16, 15)] // clamped: half the depth
    [InlineData(160, 20, 16, 10)]
    [InlineData(140, 6, 2, 2)]
    public void TheCornerRadiusIsTheCornerOrHalfTheDepth(double width, double depth, double corner, double radius)
    {
        var points = Flat(new(width, depth, corner), attached: false);

        // A corner of radius r passes r * (sqrt 2 - 1) from the sharp corner at its nearest, whatever its ramps do.
        double expected = radius * (Math.Sqrt(2) - 1);
        foreach (var sharp in new[] { new Vector2(0, 0), new((float)width, 0), new((float)width, (float)depth), new(0, (float)depth) })
        {
            Assert.Equal(expected, points.Min(p => Vector2.Distance(p, sharp)), 2);
        }
    }

    [Fact]
    public void APillNoDeeperThanTwoCornersIsACapsule()
    {
        var g = new IslandGeometry(200, 30, 16);
        var segments = Segments(g, attached: false);

        // Two straight runs, top and bottom, and four quarter circles with no ramps.
        Assert.Equal(2, segments.Count(s => s.Kind == OutlineSegmentKind.Line));
        Assert.Equal(4, segments.Count(s => s.Kind == OutlineSegmentKind.Cubic));
        var ends = Flat(g, attached: false).Where(p => p.X < 15 || p.X > 185).ToList();
        Assert.NotEmpty(ends);
        Assert.All(ends, p => Assert.Equal(15f, Vector2.Distance(p, new(p.X < 15 ? 15 : 185, 15)), 2));
    }

    [Fact]
    public void CompactIsACapsule()
    {
        var segments = Segments(IslandGeometry.Compact, attached: false);

        // Corner 19 in a depth of 38: each end is a half circle of radius 19, with no ramps and no straight run.
        Assert.Equal(new Vector2(19, 0), segments[0].End);
        Assert.Equal(2, segments.Count(s => s.Kind == OutlineSegmentKind.Line));
        Assert.Equal(4, segments.Count(s => s.Kind == OutlineSegmentKind.Cubic));
        Assert.All(segments.Where(s => s.Kind == OutlineSegmentKind.Line), s => Assert.True(s.End.Y is 0 or 38));
        var ends = Flat(IslandGeometry.Compact, attached: false).Where(p => p.X < 19 || p.X > 201).ToList();
        Assert.All(ends, p => Assert.Equal(19f, Vector2.Distance(p, new(p.X < 19 ? 19 : 201, 19)), 2));
    }

    [Fact]
    public void ACornerOf16OnCompactLeavesNoStraightRunOnTheEnds()
    {
        var segments = Segments(new IslandGeometry(220, 38, 16), attached: false);

        // Radius 16 in a depth of 38: the two curves of each end meet at mid-height, 19 from either corner.
        Assert.Equal(new Vector2(19, 0), segments[0].End);
        Assert.Equal(2, segments.Count(s => s.Kind == OutlineSegmentKind.Line));
        Assert.Contains(segments, s => s.End == new Vector2(220, 19));
        Assert.Contains(segments, s => s.End == new Vector2(0, 19));
    }

    [Fact]
    public void ACornerLeavesTheEdgeWithNoCurvature()
    {
        var segments = Segments(IslandGeometry.Panel(PanelSize.Shelf.W, PanelSize.Shelf.H), attached: false);

        // With room to spare the curve starts 1.6 radii from the corner, and the ramp's first three points lie
        // on the edge: collinear control points are what zero curvature at the join means for a cubic.
        Assert.Equal(1.6f * 22, segments[0].End.X, 3);
        Assert.Equal(560 - 1.6f * 22, segments[1].End.X, 3);
        var ramp = segments[2];
        Assert.Equal(OutlineSegmentKind.Cubic, ramp.Kind);
        Assert.Equal((0f, 0f), (ramp.Control1.Y, ramp.Control2.Y));
        Assert.True(segments[1].End.X < ramp.Control1.X && ramp.Control1.X < ramp.Control2.X && ramp.Control2.X < ramp.End.X);
        Assert.True(ramp.End.Y > 0);
        Assert.Equal(3 * 4, segments.Count(s => s.Kind == OutlineSegmentKind.Cubic));
    }

    [Fact]
    public void ARevealStartsAsALineOnTheGapLine()
    {
        var points = Flat(IslandGeometry.Hidden, attached: false);
        Assert.All(points, p => Assert.Equal(0f, p.Y));
        Assert.Equal((0f, 120f), (points.Min(p => p.X), points.Max(p => p.X)));

        // Up to half a DIP of depth there is still no area to draw, the same cut-off the notch has.
        Assert.All(Flat(new(124, 0.5, 0.2), attached: false), p => Assert.Equal(0f, p.Y));
        Assert.Contains(Flat(new(124, 0.6, 0.2), attached: false), p => p.Y > 0.5f);
    }

    [Theory]
    [InlineData(1.0, 10)]
    [InlineData(1.25, 13)]
    [InlineData(1.5, 15)]
    [InlineData(2.0, 20)]
    public void TheGapIsTenDipsInWholePixels(double scale, int pixels)
    {
        Assert.Equal(10, IslandOutline.Gap);
        Assert.Equal(pixels, IslandOutline.GapPixels(scale));
    }

    [Theory]
    [InlineData(1.0, 10, 14, 20)]
    [InlineData(1.25, 13, 18, 25)]
    [InlineData(1.5, 15, 21, 30)]
    public void TheWindowIsTheGapAndTheDepthWithRoomForTheShadow(double scale, int gap, int side, int below)
    {
        const int centerX = 1280;
        var g = IslandGeometry.Compact;
        var window = IslandOutline.WindowBounds(g, scale, centerX, attached: false);
        int width = (int)Math.Ceiling(g.Width * scale), depth = (int)Math.Ceiling(g.Depth * scale);

        Assert.Equal((side, gap, below), IslandOutline.Insets(scale, attached: false));
        Assert.Equal(new System.Drawing.Rectangle(centerX - (width + 2 * side) / 2, 0, width + 2 * side, gap + depth + below), window);

        // Retraction watches the pill and the strip above it: a pointer left on the top row by a push is on the
        // island even with no retract margin, and the shadow's room is not.
        var pointer = IslandOutline.PointerBounds(window, scale, attached: false);
        Assert.Equal(System.Drawing.Rectangle.FromLTRB(window.Left + side, 0, window.Right - side, gap + depth), pointer);
        Assert.True(RetractionRules.Inside(centerX, 0, pointer.Left, pointer.Top, pointer.Right, pointer.Bottom, margin: 0));
        Assert.False(RetractionRules.Inside(centerX, gap + depth + 1, pointer.Left, pointer.Top, pointer.Right, pointer.Bottom, margin: 0));
        Assert.False(RetractionRules.Inside(pointer.Left - 1, gap + 1, pointer.Left, pointer.Top, pointer.Right, pointer.Bottom, margin: 0));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void TheRegionHoldsThePillAndItsShadowAndLeavesTheGap(double scale)
    {
        var (side, top, _) = IslandOutline.Insets(scale, attached: false);
        foreach (var g in Resting)
        {
            var region = Region(g, scale, attached: false);
            var window = IslandOutline.WindowBounds(g, scale, 0, attached: false);

            // Never inward: every point of the drawn outline is inside the region.
            Assert.All(Flat(g, attached: false), p => Assert.True(Holds(region, p.X * scale + side, p.Y * scale + top + 0.01), $"{p} of {g} is cut at {scale}"));

            // One pixel above the pill's top edge for its anti-aliased row and no higher: the gap is not in the region.
            Assert.Equal(top - 1, region.Min(p => p.Y));
            double midX = g.Width / 2 * scale + side;
            Assert.False(Holds(region, midX, top - 1.5));
            Assert.False(Holds(region, midX, 0.5));

            // The shadow falls 6 DIPs down and is 14 DIPs soft, so it reaches 20 below the pill and 14 beside it.
            double below = (g.Depth + IslandOutline.ShadowOffsetY + IslandOutline.ShadowReach) * scale + top;
            double middle = (g.Depth / 2 + IslandOutline.ShadowOffsetY) * scale + top;
            Assert.True(Holds(region, midX, below - 0.5));
            Assert.False(Holds(region, midX, below + 1.5));
            Assert.True(Holds(region, side - IslandOutline.ShadowReach * scale + 0.5, middle));
            Assert.True(Holds(region, side + (g.Width + IslandOutline.ShadowReach) * scale - 0.5, middle));

            // And it fits the window.
            Assert.All(region, p => Assert.True(p.X >= 0 && p.X <= window.Width && p.Y <= window.Height, $"{p} outside {window.Size}"));
        }
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void OnlyTheShelfDropSurfaceTakesTheGapAboveIt(double scale)
    {
        var (side, top, _) = IslandOutline.Insets(scale, attached: false);
        var strip = IslandOutline.DropStrip(IslandGeometry.ShelfDrop, scale, attached: false);

        // From the screen's top row to the region's first row, over the surface's width.
        Assert.Equal(new System.Drawing.Rectangle(side, 0, (int)Math.Ceiling(420 * scale), top), strip);
        Assert.Equal(top - 1, Region(IslandGeometry.ShelfDrop, scale, attached: false).Min(p => p.Y));
        Assert.True(strip.Bottom > top - 1);

        // Everywhere else the gap still passes clicks, and the notch has no gap.
        Assert.True(IslandOutline.DropStrip(IslandGeometry.Compact, scale, attached: false).IsEmpty);
        Assert.True(IslandOutline.DropStrip(IslandGeometry.Panel(PanelSize.Shelf.W, PanelSize.Shelf.H), scale, attached: false).IsEmpty);
        Assert.True(IslandOutline.DropStrip(IslandGeometry.Panel(PanelSize.PaletteWidth, PanelSize.PaletteMinHeight), scale, attached: false).IsEmpty);
        Assert.True(IslandOutline.DropStrip(IslandGeometry.ShelfDrop.Attached(), scale, attached: true).IsEmpty);
    }

    [Fact]
    public void AttachedPutsTheNotchsCornerAndRampsBack()
    {
        Assert.Equal(new IslandGeometry(220, 38, 16, 10, 12), IslandGeometry.Compact.Attached());
        Assert.Equal(new IslandGeometry(420, 96, 20, 16, 18), IslandGeometry.ShelfDrop.Attached());
        Assert.Equal(new IslandGeometry(380, 140, 22, 24, 24), IslandGeometry.Panel(380, 140).Attached());
        Assert.Equal(new IslandGeometry(262, 38, 16, 10, 12), (IslandGeometry.Compact with { Width = 262 }).Attached()); // the rambler's pill
        Assert.Equal(19, IslandGeometry.Compact.Corner); // the notch's Compact corner is 16; the pill is a capsule
        Assert.Equal(IslandGeometry.Hidden, IslandGeometry.Hidden.Attached());
    }

    [Theory]
    [MemberData(nameof(Notches))]
    public void TheNotchStartsAndEndsAboveTheScreenAndIsSymmetric(IslandGeometry g)
    {
        var points = Flat(g, attached: true);
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
    public void TheHiddenNotchIsAStripAboveTheScreen()
    {
        var points = Flat(IslandGeometry.Hidden, attached: true);
        Assert.Equal(4, points.Count);
        Assert.All(points, p => Assert.InRange(p.Y, -IslandOutline.TopOverhang, 0));
    }

    [Fact]
    public void FilletHandlesSitAtSeventyTwoPercentOfTheRamp()
    {
        var fillet = Segments(IslandGeometry.Compact.Attached(), attached: true)[2];
        Assert.Equal(OutlineSegmentKind.Cubic, fillet.Kind);
        Assert.Equal(0.72f * 10, fillet.Control1.X, 3);
        Assert.Equal(0, fillet.Control1.Y);
        Assert.Equal(10, fillet.End.X, 3); // lands on the straight side
        Assert.Equal(12, fillet.End.Y, 3); // at the ramp height
        Assert.Equal(12 - 0.72f * 10, fillet.Control2.Y, 3);
    }

    [Fact]
    public void TheNotchsBottomCornersAreQuadraticsWithTheSharpCornerAsControl()
    {
        var corner = Segments(IslandGeometry.Compact.Attached(), attached: true).First(s => s.Kind == OutlineSegmentKind.Quadratic);
        Assert.Equal(new Vector2(10, 38), corner.Control1);
        Assert.Equal(new Vector2(10 + 16, 38), corner.End);
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void TheNotchsRegionAgreesWithItsOutlineWithinOnePixelOutward(double scale)
    {
        foreach (var g in new[] { IslandGeometry.Compact.Attached(), IslandGeometry.Panel(PanelSize.Clipboard.W, PanelSize.Clipboard.H).Attached() })
        {
            var points = Flat(g, attached: true);
            var region = Region(g, scale, attached: true);

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

    [Theory]
    [InlineData(1.0, 610, 262)]
    [InlineData(1.25, 762, 327)]
    [InlineData(1.5, 914, 392)]
    public void TheNotchsWindowIsItsOutlineAndOnePixelAround(double scale, int width, int height)
    {
        var g = IslandGeometry.Panel(PanelSize.Shelf.W, PanelSize.Shelf.H).Attached();
        var window = IslandOutline.WindowBounds(g, scale, 1280, attached: true);
        Assert.Equal(new System.Drawing.Rectangle(1280 - width / 2, 0, width, height), window);
        Assert.Equal(window, IslandOutline.PointerBounds(window, scale, attached: true));
    }
}
