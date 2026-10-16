using System.Numerics;
using Tomte.Core;
using Tomte.Core.Interaction;
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
    public void ThePinnedMarkSitsOnTheTomtesLowerRightWithItsPinInsideTheDisc()
    {
        // The pin's parts are on the disc, which sits in its ring over the art's lower right corner.
        Assert.All(MascotArt.Pin.SelectMany(part => part), s =>
            Assert.True(System.Numerics.Vector2.Distance(s.End, MascotArt.PinDisc.Center) < MascotArt.PinDisc.Radius, $"{s.End} off the disc"));
        Assert.Equal(MascotArt.PinRing.Center, MascotArt.PinDisc.Center);
        Assert.True(MascotArt.PinRing.Radius > MascotArt.PinDisc.Radius);
        Assert.True(MascotArt.PinRing.Center.X > MascotArt.ViewBox / 2 && MascotArt.PinRing.Center.Y > MascotArt.ViewBox / 2);
        // Never more than a few DIP past the tomte's box, short of the first tab label after it.
        float past = (MascotArt.PinRing.Center.X + MascotArt.PinRing.Radius - MascotArt.ViewBox) * MascotArt.SizeDips / MascotArt.ViewBox;
        Assert.True(past < TabStrip.AfterMascot, $"{past} DIP past the box");
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

    // The tomte opens and closes with the pill, which sizes and fades it: it has no move of its own on the way in or
    // out, and it is drawn until the island parks it once the pill has gone.
    [Fact]
    public void OpeningAndClosingHaveNoMoveOfTheirOwn()
    {
        var m = NewMascot();
        Assert.False(m.Visible);
        m.Follow(IslandState.Hidden, IslandState.Compact);
        Assert.True(m.Visible);
        Assert.Equal(0, Run(m, 0.6, x => x.RootY + x.RootX));
        m.Follow(IslandState.Compact, IslandState.Hidden);
        Assert.Equal(0, Run(m, 0.6, x => x.RootY + x.RootX));
        Assert.True(m.Visible);
        m.Park();
        Assert.False(m.Visible);
    }

    // Under the pointer the tomte lifts 1.5 DIP on the snappy spring, turns back from mid-flight when the pointer
    // leaves, and settles where it was; hiding drops the lift.
    [Fact]
    public void HoverLiftsTheTomteOnASpringAndLeavingSettlesItBack()
    {
        var m = NewMascot();
        m.Follow(IslandState.Hidden, IslandState.Compact);
        Run(m, 1, x => 0);
        double up = -MascotStates.HoverLiftDips * MascotArt.ViewBox / MascotArt.SizeDips;
        m.Hover(true);
        Run(m, 0.02, x => 0);
        double partWay = m.RootY;
        Assert.InRange(partWay, up + 0.1, -0.01);
        m.Hover(false);
        Run(m, 0.01, x => 0);
        Assert.True(m.RootY < 0, "a retarget keeps its velocity, so it does not jump back");
        Run(m, 0.5, x => 0);
        Assert.InRange(m.RootY, -0.25, 0.25);
        m.Hover(true);
        Run(m, 0.5, x => 0);
        Assert.InRange(m.RootY, up - 0.25, up + 0.25);
        m.Park();
        Assert.Equal(0, m.RootY);
    }

    // A click on the pill opens Panels and one on the tomte asks Tomte: the tomte sits on top, and the peek badge is all tomte.
    [Fact]
    public void ThePillAndItsTomteAreTargetsAndThePeekBadgeIsTheTomte()
    {
        var compact = CompactLayout.Targets(IslandGeometry.Compact, mascot: true);
        string On(Vector2 at) => compact[Target.Hit([(compact, p => p)], at)!.Value.Index].Id;
        Assert.Equal([CompactLayout.PillId, CompactLayout.TomteId], compact.Select(t => t.Id));
        Assert.Equal(CompactLayout.TomteId, On(new(-110 + MascotArt.InsetX + 13, 19)));
        Assert.Equal(CompactLayout.PillId, On(new(40, 19)));
        Assert.Equal(CompactLayout.PillId, On(new(-109, 2)));
        Assert.Equal([null, "Ask Tomte"], compact.Select(t => t.Tip));
        Assert.All(compact, t => Assert.True(t.Draggable && t.Kind == TargetKind.Button));

        var peek = Assert.Single(CompactLayout.Targets(IslandGeometry.Peek, mascot: true));
        Assert.Equal(CompactLayout.TomteId, peek.Id);
        Assert.Null(peek.Tip);
        Assert.Equal(new System.Drawing.RectangleF(-18.5f, 0, 37, 38), peek.Bounds);
        Assert.Equal(CompactLayout.PillId, Assert.Single(CompactLayout.Targets(IslandGeometry.Compact, mascot: false)).Id);
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
        Assert.False(m.Step(FrameDt));
        Assert.Null(m.WakeIn);
        m.Park();
        Assert.False(m.Visible);

        var off = NewMascot();
        off.Enabled = false;
        off.Follow(IslandState.Hidden, IslandState.Compact);
        Assert.False(off.Visible);
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

    // Shallower than the pill, the tomte is its resting self scaled about the shape's top-left corner, in step with the
    // depth, and as faint: half a pill deep, half its size and half seen; a hidden island, nothing.
    [Fact]
    public void BelowThePillsDepthTheTomteShrinksAndFadesWithIt()
    {
        Assert.Equal(1, MascotArt.Ride(IslandGeometry.Compact));
        Assert.Equal(1, MascotArt.Ride(IslandGeometry.Panel(PanelSize.Width, 140)));
        Assert.Equal(0, MascotArt.Ride(IslandGeometry.Hidden));
        var half = new IslandGeometry(170, IslandGeometry.Compact.Depth / 2, 9.5);
        Assert.Equal(0.5, MascotArt.Ride(half));
        var rest = MascotArt.Box(IslandGeometry.Compact);
        var box = MascotArt.Box(half);
        Assert.Equal(rest.X + 110, (box.X + 85) * 2, 3);
        Assert.Equal(rest.Y, box.Y * 2, 3);
        Assert.Equal(rest.Width, box.Width * 2, 3);
    }

    // Opening and closing every shape the tomte sits in, on the island's springs frame by frame: the tomte is in the live
    // outline at its left edge with room in proportion to its size, never beside a shape still on its way, and its size
    // goes up with an open and down with a close, to nothing by the time the pill has gone.
    [Fact]
    public void TheTomteOpensAndClosesWithThePill()
    {
        IslandGeometry[] shapes =
        [
            IslandGeometry.Compact, IslandGeometry.Peek, IslandGeometry.Compact with { Width = 262 }, IslandGeometry.Compact with { Width = 330 },
            IslandGeometry.ShelfDrop, IslandGeometry.Panel(PanelSize.Width, 140), IslandGeometry.Panel(PanelSize.PaletteWidth, PanelSize.PaletteMinHeight),
        ];
        foreach (var open in shapes)
        {
            foreach (var preset in new[] { SpringPreset.Snappy, SpringPreset.Gentle })
            {
                foreach (var (from, to) in new[] { (IslandGeometry.Hidden, open), (open, IslandGeometry.Hidden) })
                {
                    var frames = Frames(from, to, preset);
                    double last = MascotArt.Ride(from);
                    foreach (var live in frames)
                    {
                        double ride = MascotArt.Ride(live);
                        Assert.True(to.Depth > from.Depth ? ride >= last : ride <= last, $"{from} to {to}: the tomte turned back at {live}");
                        last = ride;
                        TomteIsInside(live);
                    }

                    Assert.InRange(Math.Abs(MascotArt.Ride(to) - MascotArt.Ride(frames[^1])), 0, 0.01);
                }
            }
        }
    }

    // A close turned round partway, as when the pointer comes back, keeps the tomte on the pill: from one frame to the
    // next it moves with the outline's left edge, also as the springs change course from Panels closing to Compact.
    [Fact]
    public void AClosingPillTurnedRoundKeepsItsTomte()
    {
        var panels = IslandGeometry.Panel(PanelSize.Width, 260);
        foreach (double turnAt in new[] { 0.05, 0.12, 0.2 })
        {
            var frames = Frames(panels, IslandGeometry.Hidden, SpringPreset.Snappy, turnAt, IslandGeometry.Compact);
            for (int i = 1; i < frames.Count; i++)
            {
                System.Drawing.RectangleF a = MascotArt.Box(frames[i - 1]), b = MascotArt.Box(frames[i]);
                double edgeMoved = (frames[i - 1].Width - frames[i].Width) / 2, deepened = Math.Abs(frames[i].Depth - frames[i - 1].Depth);
                Assert.True(
                    Math.Abs(b.X - a.X - edgeMoved) < 0.5 && Math.Abs(b.Y - a.Y) <= deepened / 2 + 0.01 && Math.Abs(b.Width - a.Width) <= deepened + 0.01,
                    $"turned at {turnAt}s: the tomte jumped from {a} to {b}");
                TomteIsInside(frames[i]);
            }

            Assert.InRange(MascotArt.Ride(frames[^1]), 0.99, 1);
        }
    }

    // The island's width, depth and corner springs stepped from one shape to another at 165 Hz until they settle, and
    // turned toward another shape after a while if asked.
    private static List<IslandGeometry> Frames(IslandGeometry from, IslandGeometry to, SpringPreset preset, double turnAt = 0, IslandGeometry? turnTo = null)
    {
        Spring width = new(), depth = new(), corner = new();
        Spring[] springs = [width, depth, corner];
        width.Jump(from.Width);
        depth.Jump(from.Depth);
        corner.Jump(from.Corner);
        Aim(to);
        var frames = new List<IslandGeometry>();
        for (double t = FrameDt; t < 3; t += FrameDt)
        {
            if (turnTo is { } next && t >= turnAt)
            {
                Aim(next);
                turnTo = null;
            }

            foreach (var spring in springs)
            {
                spring.Step(FrameDt);
            }

            frames.Add(new(width.Value, depth.Value, corner.Value));
            if (turnTo is null && Array.TrueForAll(springs, s => s.Settled))
            {
                break;
            }
        }

        return frames;

        void Aim(IslandGeometry g)
        {
            foreach (var spring in springs)
            {
                spring.Use(preset);
            }

            (width.Target, depth.Target, corner.Target) = (g.Width, g.Depth, g.Corner);
        }
    }

    // All of the tomte's art inside the live outline, with room around it in proportion to its size (6 DIP at rest).
    private static void TomteIsInside(in IslandGeometry live)
    {
        if (live.Depth < 1)
        {
            return;
        }

        var art = Art(live);
        var segments = new List<OutlineSegment>();
        var outline = new List<Vector2>();
        IslandOutline.Build(live, attached: false, segments);
        IslandOutline.Flatten(segments, outline);
        foreach (var p in art)
        {
            Assert.True(Inside(p, outline), $"{live}: {p} is outside the outline");
        }

        float room = art.Min(p => Distance(p, outline));
        Assert.True(room >= 6 * MascotArt.Ride(live) - 0.05, $"{live}: the art comes within {room:F2} DIP of the outline");
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
        float k = box.Width / MascotArt.ViewBox;
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
