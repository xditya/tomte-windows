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
/// The island outline in DIPs. Floating, the default: a pill whose top-left corner is the origin and sits
/// on the gap line, <see cref="Gap"/> below the screen's top edge, with four corners of radius
/// min(corner, depth / 2) whose curvature ramps up from the straight edges. Attached: the notch that grows
/// out of the screen's top edge, origin at the left end of the left ramp, top edge 4 DIPs above the screen,
/// concave cubic fillets with handles at 0.72 of the ramp, straight sides, bottom corners as quadratic
/// Beziers whose control point is the sharp corner. One construction feeds both the drawn path (real curves)
/// and the hit-test region (flattened), so the two always agree.
/// </summary>
public static class IslandOutline
{
    /// <summary>The floating pill hangs this far below the screen's top edge.</summary>
    public const double Gap = 10;

    /// <summary>How far down the pill's drop shadow falls.</summary>
    public const double ShadowOffsetY = 6;

    /// <summary>The shadow's softness: a Gaussian blur of sigma 7 DIP.</summary>
    public const double ShadowSigma = 7;

    /// <summary>Room the shadow gets: two sigma, past which under 1% black is left of it.</summary>
    public const double ShadowReach = 2 * ShadowSigma;

    public const float TopOverhang = 4;
    private const int CubicSteps = 8;
    private const int QuadraticSteps = 12;
    private const float HandleFraction = 0.72f;

    // How far a corner's curve runs on past its radius along each edge, as a fraction of the radius. At 0 the corner
    // is a circular arc, whose curvature jumps where it meets the edge; 0.6 is the usual figure for a continuous one.
    private const double CornerSmoothing = 0.6;

    public static void Build(in IslandGeometry g, bool attached, List<OutlineSegment> into)
    {
        if (attached)
        {
            BuildAttached(g, into);
        }
        else
        {
            BuildPill(g, into);
        }
    }

    private static void BuildPill(in IslandGeometry g, List<OutlineSegment> into)
    {
        into.Clear();

        // Under half a DIP of depth nothing shows, as with the notch: a line on the gap line with no area, which
        // is where a reveal starts and a retract ends.
        double w = Math.Max(0, g.Width), d = g.Depth <= 0.5 ? 0 : g.Depth;

        // What one corner may use of an edge. At half the depth the two curves of a short side meet: a capsule.
        double room = Math.Min(w, d) / 2;
        var corner = new Corner(Math.Min(Math.Max(0, g.Corner), room), room);
        float right = (float)w, bottom = (float)d, reach = corner.Reach;

        // Clockwise from the top edge.
        into.Add(OutlineSegment.Move(new(reach, 0)));
        Edge(into, new(right - reach, 0));
        corner.Add(into, new(right, 0), Vector2.UnitX, Vector2.UnitY);
        Edge(into, new(right, bottom - reach));
        corner.Add(into, new(right, bottom), Vector2.UnitY, -Vector2.UnitX);
        Edge(into, new(reach, bottom));
        corner.Add(into, new(0, bottom), -Vector2.UnitX, -Vector2.UnitY);
        Edge(into, new(0, reach));
        corner.Add(into, new(0, 0), -Vector2.UnitY, Vector2.UnitX);
    }

    /// <summary>A straight run to <paramref name="to"/>, left out when two corners meet and nothing is left of it.</summary>
    private static void Edge(List<OutlineSegment> into, Vector2 to)
    {
        if (Vector2.DistanceSquared(into[^1].End, to) > 1e-6f)
        {
            into.Add(OutlineSegment.Line(to));
        }
    }

    private static void BuildAttached(in IslandGeometry g, List<OutlineSegment> into)
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

    /// <summary>The gap in whole pixels, so the pill's top edge lands on a pixel row: 10, 13 and 15 at 100%, 125% and 150%.</summary>
    public static int GapPixels(double scale) => (int)Math.Round(Gap * scale, MidpointRounding.AwayFromZero);

    /// <summary>
    /// Pixels between the window's edges and the outline's box. Floating: room for the shadow at the sides and
    /// below, the gap above. Attached: one pixel all round for the anti-aliased edge.
    /// </summary>
    public static (int Side, int Top, int Bottom) Insets(double scale, bool attached) => attached
        ? (1, 1, 1)
        : ((int)Math.Ceiling(ShadowReach * scale), GapPixels(scale), (int)Math.Ceiling((ShadowOffsetY + ShadowReach) * scale));

    /// <summary>Window rectangle of a geometry in physical pixels, centred on <paramref name="centerX"/>, its top on the screen's top edge.</summary>
    public static System.Drawing.Rectangle WindowBounds(in IslandGeometry g, double scale, int centerX, bool attached)
    {
        var (side, top, bottom) = Insets(scale, attached);
        int w = (int)Math.Ceiling(g.OuterWidth * scale) + 2 * side;
        int h = (int)Math.Ceiling(Math.Max(0, g.Depth) * scale) + top + bottom;
        return new System.Drawing.Rectangle(centerX - w / 2, 0, w, h);
    }

    /// <summary>
    /// The part of a window rectangle the pointer is tracked against for retraction: the outline's box and, for
    /// the floating pill, the strip between it and the screen edge, so a pointer still resting on the edge after
    /// a push is on the island. The shadow's room is not part of it.
    /// </summary>
    public static System.Drawing.Rectangle PointerBounds(System.Drawing.Rectangle window, double scale, bool attached)
    {
        if (attached)
        {
            return window;
        }

        var (side, _, bottom) = Insets(scale, attached);
        return System.Drawing.Rectangle.FromLTRB(window.Left + side, window.Top, window.Right - side, window.Bottom - bottom);
    }

    /// <summary>
    /// The strip between the screen edge and a resting Shelf-drop surface, over the surface's width, in window
    /// pixels. A drag pushed up to the top row is released there, and OLE finds its drop target through the
    /// region, so the strip belongs to the surface. Empty for every other state, where the gap passes clicks
    /// through, and for the notch, which has no gap.
    /// </summary>
    // keyed by the Shelf-drop geometry, which no other state has; pass the state in if one ever does.
    public static System.Drawing.Rectangle DropStrip(in IslandGeometry g, double scale, bool attached)
    {
        if (attached || g != IslandGeometry.ShelfDrop)
        {
            return System.Drawing.Rectangle.Empty;
        }

        var (side, top, _) = Insets(scale, attached);
        return new System.Drawing.Rectangle(side, 0, (int)Math.Ceiling(g.Width * scale), top);
    }

    /// <summary>
    /// Window-pixel points of the region for a settled geometry. The region clips pixels as well as clicks.
    /// Attached: the outline itself. Floating: the shadow's footprint, which is the pill moved down by the shadow's
    /// offset and grown by its blur, cut off one pixel above the pill's top edge so the gap passes clicks through.
    /// Each point is rounded away from the shape, so the region never cuts into the outline.
    /// </summary>
    public static void Region(in IslandGeometry g, double scale, bool attached, List<OutlineSegment> segments, List<Vector2> flattened, List<System.Drawing.Point> into)
    {
        var (side, top, _) = Insets(scale, attached);
        var shape = attached ? g : new IslandGeometry(g.Width + 2 * ShadowReach, g.Depth + 2 * ShadowReach, g.Corner + ShadowReach);
        double dx = attached ? 0 : -ShadowReach * scale, dy = attached ? 0 : (ShadowOffsetY - ShadowReach) * scale;
        Build(shape, attached, segments);
        Flatten(segments, flattened);

        into.Clear();
        double cx = shape.OuterWidth / 2 * scale, cy = attached ? 0 : shape.Depth / 2 * scale;
        int highest = attached ? int.MinValue : -1;
        foreach (var p in flattened)
        {
            double x = p.X * scale, y = p.Y * scale;
            int px = (int)(x < cx ? Math.Floor(x + dx) : Math.Ceiling(x + dx));
            int py = (int)(y <= cy ? Math.Floor(y + dy) : Math.Ceiling(y + dy));
            into.Add(new(px + side, Math.Max(py, highest) + top));
        }
    }

    /// <summary>
    /// One right-angle corner: an arc of the radius with a cubic ramp on each side. A ramp's first three points lie
    /// on the edge, so its curvature starts at zero and rises to the arc's. The ramps shorten as the room runs out
    /// and are gone when the room is the radius, which leaves a plain quarter circle.
    /// </summary>
    private readonly struct Corner
    {
        // Along the edge: two ramp handles (a, b), then the ramp's end (c along, d across), then the arc.
        private readonly float a, b, c, d, arc, handle, cos, sin;

        public Corner(double radius, double room)
        {
            if (radius <= 0)
            {
                return;
            }

            double smoothing = Math.Clamp(room / radius - 1, 0, CornerSmoothing);
            double reach = (1 + smoothing) * radius;
            double sweep = Math.PI / 2 * (1 - smoothing), tilt = Math.PI / 4 * smoothing;
            double arcSpan = Math.Sin(sweep / 2) * radius * Math.Sqrt(2);
            double rampEnd = radius * Math.Tan(tilt / 2);
            double along = rampEnd * Math.Cos(tilt), across = rampEnd * Math.Sin(tilt);
            double third = Math.Max(0, (reach - arcSpan - along - across) / 3);
            (a, b, c, d) = ((float)(2 * third), (float)third, (float)along, (float)across);
            (arc, handle) = ((float)arcSpan, (float)(4.0 / 3 * Math.Tan(sweep / 4) * radius));
            (cos, sin) = ((float)Math.Cos(tilt), (float)Math.Sin(tilt));
            Reach = (float)reach;
        }

        /// <summary>How far from the sharp corner the curve leaves each edge.</summary>
        public float Reach { get; }

        /// <summary>Round the corner at <paramref name="at"/>, arriving along <paramref name="u"/> and leaving along <paramref name="v"/>.</summary>
        public void Add(List<OutlineSegment> into, Vector2 at, Vector2 u, Vector2 v)
        {
            if (Reach <= 0)
            {
                return;
            }

            var start = at - Reach * u;
            var arcStart = start + (a + b + c) * u + d * v;
            var arcEnd = arcStart + arc * (u + v);
            bool ramps = a > 0;
            if (ramps)
            {
                into.Add(OutlineSegment.Cubic(start + a * u, start + (a + b) * u, arcStart));
            }

            into.Add(OutlineSegment.Cubic(arcStart + handle * (cos * u + sin * v), arcEnd - handle * (sin * u + cos * v), arcEnd));
            if (ramps)
            {
                into.Add(OutlineSegment.Cubic(arcEnd + d * u + c * v, arcEnd + d * u + (b + c) * v, at + Reach * v));
            }
        }
    }
}
