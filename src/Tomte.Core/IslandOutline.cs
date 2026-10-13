using System.Numerics;

namespace Tomte.Core;

public enum OutlineSegmentKind
{
    Move,
    Line,
    Quadratic,
    Cubic,
}

/// <summary>One path segment. Control points are unused for Move and Line; Quadratic uses Control1 only.</summary>
public readonly record struct OutlineSegment(OutlineSegmentKind Kind, Vector2 Control1, Vector2 Control2, Vector2 End)
{
    public static OutlineSegment Move(Vector2 to) => new(OutlineSegmentKind.Move, default, default, to);

    public static OutlineSegment Line(Vector2 to) => new(OutlineSegmentKind.Line, default, default, to);

    public static OutlineSegment Quadratic(Vector2 control, Vector2 to) => new(OutlineSegmentKind.Quadratic, control, default, to);

    public static OutlineSegment Cubic(Vector2 control1, Vector2 control2, Vector2 to) => new(OutlineSegmentKind.Cubic, control1, control2, to);
}

/// <summary>
/// The island outline in DIPs, origin at the left end of the left ramp on the screen's top edge.
/// Top edge 4 DIPs above the screen, concave cubic fillets into the bezel with handles at 0.72 of
/// the ramp, straight sides, bottom corners as quadratic Beziers whose control point is the sharp
/// corner. One construction feeds both the drawn path (real curves) and the hit-test region
/// (flattened), so the two always agree.
/// </summary>
public static class IslandOutline
{
    public const float TopOverhang = 4;
    private const int CubicSteps = 8;
    private const int QuadraticSteps = 12;
    private const float HandleFraction = 0.72f;

    public static void Build(in IslandGeometry g, List<OutlineSegment> into)
    {
        into.Clear();
        double w = Math.Max(0, g.Width), d = Math.Max(0, g.Depth), c = Math.Max(0, g.Corner);
        double rw = Math.Max(0, g.RampW), rh = Math.Max(0, g.RampH);
        float width = (float)(w + 2 * rw), left = (float)rw;
        into.Add(OutlineSegment.Move(new(0, -TopOverhang)));
        if (d <= 0.5)
        {
            // A strip entirely above the screen: nothing visible, but the path stays well formed for the morph.
            into.Add(OutlineSegment.Line(new(width, -TopOverhang)));
            into.Add(OutlineSegment.Line(new(width, 0)));
            into.Add(OutlineSegment.Line(new(0, 0)));
            return;
        }

        double radius = Math.Min(c, Math.Min(w / 2, d));
        double filletDepth = Math.Min(rh, d - 0.5);
        float handle = (float)(HandleFraction * Math.Min(rw, filletDepth));
        float depth = (float)d, fd = (float)filletDepth, r = (float)radius;
        float cornerTop = depth - r;

        // When the corner reaches up past the fillet the side vanishes: the fillet lands on the corner curve
        // at parameter a0 and only the rest of the curve is drawn (de Casteljau split of the quadratic).
        bool split = filletDepth > d - radius && radius > 0;
        float a0 = split ? (float)(1 - Math.Sqrt((d - filletDepth) / radius)) : 0;
        float filletX = split ? left + r * a0 * a0 : left;
        float ctrlX = split ? left + a0 * r : left;

        into.Add(OutlineSegment.Line(new(0, 0)));
        into.Add(OutlineSegment.Cubic(new(HandleFraction * left, 0), new(filletX, fd - handle), new(filletX, fd)));
        if (!split)
        {
            into.Add(OutlineSegment.Line(new(left, cornerTop)));
        }

        if (radius > 0)
        {
            into.Add(OutlineSegment.Quadratic(new(ctrlX, depth), new(left + r, depth)));
        }

        into.Add(OutlineSegment.Line(new(width - left - r, depth)));
        if (radius > 0)
        {
            into.Add(OutlineSegment.Quadratic(new(width - ctrlX, depth), split ? new(width - filletX, fd) : new(width - left, cornerTop)));
        }

        if (!split)
        {
            into.Add(OutlineSegment.Line(new(width - left, fd)));
        }

        into.Add(OutlineSegment.Cubic(new(width - filletX, fd - handle), new(width - HandleFraction * left, 0), new(width, 0)));
        into.Add(OutlineSegment.Line(new(width, -TopOverhang)));
    }

    /// <summary>Flatten the segments to a polygon, for the hit-test region and for tests.</summary>
    public static void Flatten(List<OutlineSegment> segments, List<Vector2> into)
    {
        into.Clear();
        Vector2 at = default;
        foreach (var s in segments)
        {
            switch (s.Kind)
            {
                case OutlineSegmentKind.Quadratic:
                    for (int i = 1; i <= QuadraticSteps; i++)
                    {
                        float t = i / (float)QuadraticSteps, u = 1 - t;
                        into.Add(u * u * at + 2 * u * t * s.Control1 + t * t * s.End);
                    }

                    break;
                case OutlineSegmentKind.Cubic:
                    for (int i = 1; i <= CubicSteps; i++)
                    {
                        float t = i / (float)CubicSteps, u = 1 - t;
                        into.Add(u * u * u * at + 3 * u * u * t * s.Control1 + 3 * u * t * t * s.Control2 + t * t * t * s.End);
                    }

                    break;
                default:
                    into.Add(s.End);
                    break;
            }

            at = s.End;
        }
    }

    /// <summary>
    /// Pixel points for the window region: the flattened outline scaled to the monitor's DPI, each
    /// point rounded away from the shape so the anti-aliased edge is never cut and the outset stays
    /// under 1 px. <paramref name="centerX"/> is the outline's mid-line in DIPs and
    /// <paramref name="margin"/> the pixel inset of the outline inside the window.
    /// </summary>
    public static void RegionPoints(List<Vector2> outline, double scale, double centerX, int margin, List<System.Drawing.Point> into)
    {
        into.Clear();
        double cx = centerX * scale;
        foreach (var p in outline)
        {
            double x = p.X * scale, y = p.Y * scale;
            int px = (int)(x < cx ? Math.Floor(x) : Math.Ceiling(x));
            int py = (int)(y <= 0 ? Math.Floor(y) : Math.Ceiling(y));
            into.Add(new(px + margin, py + margin));
        }
    }
}
