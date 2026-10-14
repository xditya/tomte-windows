using System.Numerics;
using Tomte.Core;
using Xunit;

namespace Tomte.Tests;

public class MascotTests
{
    private const double FrameDt = 1.0 / 165;
    private double now;

    private Mascot NewMascot(bool reduce = false) => new(() => now, () => reduce);

    // Steps the mascot like the island's tick does; returns the largest |value| the probe saw.
    private double Run(Mascot m, double seconds, Func<Mascot, double> probe)
    {
        double max = 0;
        for (double t = 0; t < seconds; t += FrameDt)
        {
            now += FrameDt;
            m.Step(FrameDt);
            max = Math.Max(max, Math.Abs(probe(m)));
        }

        return max;
    }

    [Fact]
    public void StateTableCoversEveryState()
    {
        foreach (var state in Enum.GetValues<MascotState>())
        {
            Assert.True(MascotStates.Table.ContainsKey(state), $"{state} has no row");
        }

        Assert.Equal(7, MascotStates.Table.Count);
    }

    [Fact]
    public void EveryIslandStateMapsToAMascotState()
    {
        var expected = new Dictionary<IslandState, MascotState>
        {
            [IslandState.Hidden] = MascotState.Hidden,
            [IslandState.Capture] = MascotState.Hidden,
            [IslandState.Compact] = MascotState.Compact,
            [IslandState.Panels] = MascotState.Compact,
            [IslandState.Palette] = MascotState.Compact,
            [IslandState.ShelfDrop] = MascotState.ShelfDrop,
        };
        foreach (var island in Enum.GetValues<IslandState>())
        {
            Assert.Equal(expected[island], MascotStates.For(island));
        }
    }

    [Fact]
    public void PartsAreClosedFiguresInsideTheViewBox()
    {
        OutlineSegment[][] paths = [MascotArt.Beard, MascotArt.BeardShade, MascotArt.CapCone, MascotArt.TipCurl, MascotArt.Brim];
        foreach (var path in paths)
        {
            Assert.Equal(OutlineSegmentKind.Move, path[0].Kind);
            Assert.All(path.Skip(1), s => Assert.NotEqual(OutlineSegmentKind.Move, s.Kind));
            // Closed back to the start (the SVG's Z), or a closing line from the last point is implied.
            Assert.All(path, s => Assert.True(InViewBox(s.End), $"{s.End} outside the viewBox"));
        }

        Assert.Equal(MascotArt.TipCurl[0].End, MascotArt.TipCurl[^1].End);
        Assert.Equal(MascotArt.Brim[0].End, MascotArt.Brim[^1].End);
    }

    [Fact]
    public void PivotsSitWhereTheContractSays()
    {
        Assert.Equal(new Vector2(32, 61), MascotArt.RootPivot);
        Assert.Equal(MascotArt.RootPivot, MascotArt.BodyPivot);
        // The root pivot is the beard's lowest y, so sway and nod rock on the bottom edge.
        Assert.Equal(MascotArt.Beard.Max(s => s.End.Y), MascotArt.RootPivot.Y);
        // The cap pivot is on the brim, the tip pivot on the cone's top edge (y = 10, x between 27 and 34).
        Assert.InRange(MascotArt.CapPivot.Y, MascotArt.Brim[0].End.Y, MascotArt.Brim[3].End.Y);
        Assert.Equal(MascotArt.CapCone[2].End.Y, MascotArt.TipPivot.Y);
        Assert.InRange(MascotArt.TipPivot.X, MascotArt.CapCone[1].End.X, MascotArt.CapCone[2].End.X);
        // The nose peeks from under the brim: its top is above the brim's bottom edge.
        Assert.True(MascotArt.Nose.Center.Y - MascotArt.Nose.Radius < MascotArt.Brim[3].End.Y);
    }

    [Fact]
    public void BrimIsTheCapAtSeventySixPercent()
    {
        Assert.Equal(0x982624u, MascotArt.Shade(MascotArt.CapRed));
        Assert.Equal(0xC2C2C2u, MascotArt.Shade(0xFFFFFF));
    }

    [Fact]
    public void RevealPeeksFromAboveAndLeadsTheIsland()
    {
        var m = NewMascot();
        Assert.False(m.Visible);
        m.Follow(IslandState.Hidden, IslandState.Compact);
        Assert.True(m.Visible);
        Assert.Equal(MascotStates.PeekFromY, m.RootY);
        Assert.Equal(MascotStates.IslandLeadSeconds, m.TakeIslandLead());
        Assert.Equal(0, m.TakeIslandLead());
        Run(m, 0.6, x => x.RootY);
        Assert.InRange(m.RootY, -0.25, 0.25);
    }

    [Fact]
    public void AReactionSetDuringTheRevealKeepsTheDropInSnappy()
    {
        var m = NewMascot();
        m.Follow(IslandState.Hidden, IslandState.Palette);
        m.Set(MascotState.Thinking);
        Run(m, 0.35, x => 0);
        Assert.InRange(m.RootY, -1, 1); // gentle would still be about 5 units up
    }

    [Fact]
    public void RestingSchedulesASwayFourToSevenSecondsOut()
    {
        var m = NewMascot();
        m.Follow(IslandState.Hidden, IslandState.Compact);
        Run(m, 1, x => 0);
        Assert.InRange(m.WakeIn ?? -1, 3, 7);
        double peak = Run(m, 8.5, x => x.RootRotate);
        Assert.InRange(peak, 2, 3.5);
    }

    [Fact]
    public void LeanFollowsThePointerAndClamps()
    {
        var m = NewMascot();
        m.Follow(IslandState.Hidden, IslandState.ShelfDrop);
        m.Lean(400);
        Run(m, 0.6, x => 0);
        Assert.InRange(m.RootRotate, 11.75, 12.25);
        Assert.InRange(m.RootX, 1.25, 1.75);
        m.Lean(-20);
        Run(m, 0.6, x => 0);
        Assert.InRange(m.RootRotate, -6.25, -5.75);
        Assert.InRange(m.RootX, -1.75, -1.25);
    }

    [Fact]
    public void DropNodsOnceAndComesBackToRest()
    {
        var m = NewMascot();
        m.Follow(IslandState.Hidden, IslandState.ShelfDrop);
        Run(m, 0.6, x => 0);
        m.Follow(IslandState.ShelfDrop, IslandState.Panels);
        Assert.Equal(MascotState.Compact, m.State);
        double peak = Run(m, 0.8, x => x.RootRotate);
        Assert.InRange(peak, 5, 12);
        Assert.InRange(m.RootRotate, -0.25, 0.25);
        Assert.InRange(m.WakeIn ?? -1, 3, 7); // sways again later
    }

    [Fact]
    public void ResultNodsThenStaysStill()
    {
        var m = NewMascot();
        m.Follow(IslandState.Hidden, IslandState.Palette);
        m.Set(MascotState.Result);
        double peak = Run(m, 1.2, x => x.RootRotate);
        Assert.InRange(peak, 5, 12);
        Assert.Null(m.WakeIn);
        Assert.False(m.Step(FrameDt));
    }

    [Fact]
    public void ThinkingFlopsTheTipFourteenDegrees()
    {
        var m = NewMascot();
        m.Follow(IslandState.Hidden, IslandState.Palette);
        Run(m, 0.6, x => 0);
        m.Set(MascotState.Thinking);
        Assert.Equal(0, m.TipRotate, 3); // starts at phase 0, no jump
        double peak = Run(m, 1.6, x => x.TipRotate);
        Assert.InRange(peak, 13.5, 14.25);
        Assert.True(m.Step(FrameDt));
    }

    [Fact]
    public void RamblerBouncesWithTheLevel()
    {
        var m = NewMascot();
        m.Follow(IslandState.Hidden, IslandState.Compact);
        m.Set(MascotState.Rambler);
        m.Level = 1;
        Run(m, 0.5, x => 0);
        Assert.InRange(m.ScaleY, 1.24, 1.25);
        Assert.InRange(m.ScaleX, 0.92, 0.921);
        Assert.InRange(m.TipRotate, -10, -9.9);
    }

    [Fact]
    public void ErrorDroopsAndStays()
    {
        var m = NewMascot();
        m.Follow(IslandState.Hidden, IslandState.Palette);
        m.Set(MascotState.Error);
        Run(m, 1.5, x => 0);
        Assert.InRange(m.TipRotate, 37.75, 38.25);
        Assert.InRange(m.CapRotate, 7.75, 8.25);
        Assert.InRange(m.RootY, 1.25, 1.75);
        Assert.Null(m.WakeIn);
    }

    [Fact]
    public void ReducedMotionJumpsToEndPoses()
    {
        var m = NewMascot(reduce: true);
        m.Follow(IslandState.Hidden, IslandState.Compact);
        Assert.Equal(0, m.RootY);
        Assert.Equal(0, m.TakeIslandLead());
        Assert.Null(m.WakeIn); // no sway
        m.Set(MascotState.Thinking);
        Run(m, 0.5, x => 0);
        Assert.Equal(8, m.TipRotate);
        Assert.False(m.Step(FrameDt));
        m.Set(MascotState.Rambler);
        m.Level = 1;
        Assert.False(m.Step(FrameDt)); // one frame for the new level, then the tick may park
        Assert.Equal(1.25, m.ScaleY, 6);
    }

    [Fact]
    public void HiddenAndOffCostNothing()
    {
        var m = NewMascot();
        m.Follow(IslandState.Hidden, IslandState.Compact);
        m.Set(MascotState.Thinking);
        m.Follow(IslandState.Palette, IslandState.Hidden);
        Run(m, 0.6, x => 0);
        Assert.False(m.Visible);
        Assert.False(m.Step(FrameDt));
        Assert.Null(m.WakeIn);

        var off = NewMascot();
        off.Enabled = false;
        off.Follow(IslandState.Hidden, IslandState.Compact);
        Assert.False(off.Visible);
        Assert.Equal(0, off.TakeIslandLead());
        Assert.False(off.Step(FrameDt));
    }

    // At rest in every shape it shows in: all of the art inside the outline and clear of its corners, centred down the
    // top row, and in the tab strip with as much room on its left as above it.
    [Fact]
    public void TheTomteSitsInsideEveryShapeWithRoomAroundIt()
    {
        IslandGeometry[] shapes =
        [
            IslandGeometry.Compact, IslandGeometry.Peek, IslandGeometry.Compact with { Width = 262 }, IslandGeometry.ShelfDrop,
            IslandGeometry.Panel(380, 140), IslandGeometry.Panel(560, 360), IslandGeometry.Panel(640, 150),
        ];
        foreach (var shape in shapes)
        {
            var art = Art(shape);
            var segments = new List<OutlineSegment>();
            var outline = new List<Vector2>();
            IslandOutline.Build(shape, attached: false, segments);
            IslandOutline.Flatten(segments, outline);
            Assert.All(art, p => Assert.True(Inside(p, outline), $"{shape}: {p} is outside the outline"));
            float room = art.Min(p => Distance(p, outline));
            Assert.True(room >= 6, $"{shape}: the art comes within {room:F2} DIP of the outline");
            float row = (float)Math.Min(shape.Depth, PanelSpace.TabStripHeight);
            Assert.Equal(row - art.Max(p => p.Y), art.Min(p => p.Y), 0.1);
        }

        var strip = Art(IslandGeometry.Panel(560, 260));
        Assert.Equal(strip.Min(p => p.Y), strip.Min(p => p.X), 0.1);

        // The peek pill holds the tomte alone, centred across it as well.
        var peek = Art(IslandGeometry.Peek);
        Assert.Equal(IslandGeometry.Peek.Width - peek.Max(p => p.X), peek.Min(p => p.X), 0.1);
    }

    // Through a reveal of the palette, frame by frame, the tomte stays on the growing outline rather than where the
    // palette's edge will be; the pill and the peek pill still open around a tomte that has dropped in; a retract keeps it.
    [Fact]
    public void ARevealWiderThanThePillCarriesTheTomteOnItsEdge()
    {
        var palette = IslandGeometry.Panel(PanelSize.PaletteWidth, PanelSize.PaletteMinHeight);
        Assert.Null(MascotArt.Anchor(IslandGeometry.Hidden, palette));
        for (double t = 0; t <= 1; t += 0.05)
        {
            var live = new IslandGeometry(120 + t * (palette.Width - 120), t * palette.Depth, 22);
            var box = MascotArt.Box(MascotArt.Anchor(IslandGeometry.Hidden, palette) ?? live);
            Assert.True(box.X >= -live.Width / 2, $"at {t:F2} the tomte is at {box.X}, left of the outline's {-live.Width / 2}");
        }

        Assert.Equal(IslandGeometry.Compact, MascotArt.Anchor(IslandGeometry.Hidden, IslandGeometry.Compact));
        Assert.Equal(IslandGeometry.Peek, MascotArt.Anchor(IslandGeometry.Hidden, IslandGeometry.Peek));
        Assert.Equal(palette, MascotArt.Anchor(palette, IslandGeometry.Hidden));
        Assert.Null(MascotArt.Anchor(IslandGeometry.Compact, palette));
    }

    // A click on the tomte opens the palette, so its box must be where it is drawn and apart from what else takes clicks.
    [Fact]
    public void TheTomtesBoxIsApartFromTheLabelsAndTheLine()
    {
        // Island space: x from the shape's centre line. Compact's box ends before its line of words starts.
        var compact = MascotArt.Box(IslandGeometry.Compact);
        Assert.Equal(-110 + MascotArt.InsetX, compact.X);
        Assert.True(compact.Right <= CompactLayout.Line(mascot: true, ring: false).X);

        // The peek pill is little more than the box, centred on its centre line.
        var peek = MascotArt.Box(IslandGeometry.Peek);
        Assert.Equal(0, peek.X + peek.Width / 2, 0.01);

        // In the strip the box ends before the first label, so a press on it is never a tab's.
        PanelId[] tabs = [PanelId.Shelf, PanelId.Shortcuts, PanelId.Media];
        var box = MascotArt.Box(IslandGeometry.Panel(380, 140));
        float left = box.X + 380 / 2f;
        Assert.True(left + box.Width <= TabStrip.Labels(tabs, PanelId.Media, 380, mascot: true)[0].Slot.X);
        Assert.Null(TabStrip.At(left + box.Width / 2, tabs, PanelId.Media, 380, mascot: true));
        Assert.InRange(box.Y + box.Height / 2, 0, PanelSpace.TabStripHeight);
    }

    // The art's flattened outline and the edge of its two circles, in DIPs from the shape's top-left corner.
    private static List<Vector2> Art(in IslandGeometry shape)
    {
        var box = MascotArt.Box(shape);
        var at = new Vector2(box.X + (float)shape.Width / 2, box.Y);
        float k = MascotArt.SizeDips / MascotArt.ViewBox;
        var points = new List<Vector2>();
        var flat = new List<Vector2>();
        foreach (var path in new[] { MascotArt.Beard, MascotArt.BeardShade, MascotArt.CapCone, MascotArt.TipCurl, MascotArt.Brim })
        {
            IslandOutline.Flatten([.. path], flat);
            points.AddRange(flat.Select(p => at + p * k));
        }

        foreach (var (centre, radius) in new[] { MascotArt.Nose, MascotArt.TipBulb })
        {
            for (int i = 0; i < 32; i++)
            {
                float a = i * MathF.Tau / 32;
                points.Add(at + (centre + radius * new Vector2(MathF.Cos(a), MathF.Sin(a))) * k);
            }
        }

        return points;
    }

    private static bool Inside(Vector2 p, List<Vector2> polygon)
    {
        bool inside = false;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (a, b) = (polygon[i], polygon[j]);
            if ((a.Y > p.Y) != (b.Y > p.Y) && p.X < a.X + (p.Y - a.Y) / (b.Y - a.Y) * (b.X - a.X))
            {
                inside = !inside;
            }
        }

        return inside;
    }

    private static float Distance(Vector2 p, List<Vector2> polygon)
    {
        float best = float.MaxValue;
        for (int i = 0, j = polygon.Count - 1; i < polygon.Count; j = i++)
        {
            var (a, b) = (polygon[j], polygon[i]);
            float t = Math.Clamp(Vector2.Dot(p - a, b - a) / Math.Max(1e-6f, (b - a).LengthSquared()), 0, 1);
            best = Math.Min(best, Vector2.Distance(p, a + t * (b - a)));
        }

        return best;
    }

    private static bool InViewBox(Vector2 p) => p.X is >= 0 and <= MascotArt.ViewBox && p.Y is >= 0 and <= MascotArt.ViewBox;
}
