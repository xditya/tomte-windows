using System.Globalization;
using System.Numerics;

namespace Tomte.Core;

/// <summary>
/// Reads SVG path data (M L H V C S A Z, upper or lower case, implicit repeated coordinates, numbers split by
/// commas, spaces or signs) into glyph figures, one per sub-path. Arcs become cubics of at most a quarter turn.
/// </summary>
public static class SvgPath
{
    public static GlyphFigure[] Parse(string d, bool stroked)
    {
        var figures = new List<GlyphFigure>();
        var segs = new List<OutlineSegment>();
        bool closed = false;
        Vector2 cur = default, start = default, lastCtrl = default;
        char cmd = ' ', prev = ' ';
        int i = 0;

        void Flush()
        {
            if (segs.Count > 1)
            {
                figures.Add(new GlyphFigure([.. segs], stroked, !closed));
            }

            segs.Clear();
            closed = false;
        }

        while (true)
        {
            SkipSep(d, ref i);
            if (i >= d.Length)
            {
                break;
            }

            if (char.IsLetter(d[i]))
            {
                cmd = d[i++];
                if (cmd is 'Z' or 'z')
                {
                    cur = start;
                    closed = true;
                    Flush();
                    prev = 'Z';
                    continue;
                }
            }
            else if (cmd is ' ' or 'Z' or 'z')
            {
                throw new FormatException($"Path data has numbers with no command at {i}.");
            }

            bool rel = char.IsLower(cmd);
            char up = char.ToUpperInvariant(cmd);
            Vector2 o = rel ? cur : default;
            switch (up)
            {
                case 'M':
                    Flush();
                    cur = start = o + new Vector2(Num(d, ref i), Num(d, ref i));
                    segs.Add(OutlineSegment.Move(cur));
                    cmd = rel ? 'l' : 'L';
                    break;
                case 'L':
                    cur = o + new Vector2(Num(d, ref i), Num(d, ref i));
                    segs.Add(OutlineSegment.Line(cur));
                    break;
                case 'H':
                    cur = new Vector2((rel ? cur.X : 0) + Num(d, ref i), cur.Y);
                    segs.Add(OutlineSegment.Line(cur));
                    break;
                case 'V':
                    cur = new Vector2(cur.X, (rel ? cur.Y : 0) + Num(d, ref i));
                    segs.Add(OutlineSegment.Line(cur));
                    break;
                case 'C':
                case 'S':
                    {
                        Vector2 c1 = up == 'C' ? o + new Vector2(Num(d, ref i), Num(d, ref i)) : prev is 'C' or 'S' ? 2 * cur - lastCtrl : cur;
                        var c2 = o + new Vector2(Num(d, ref i), Num(d, ref i));
                        var end = o + new Vector2(Num(d, ref i), Num(d, ref i));
                        segs.Add(OutlineSegment.Cubic(c1, c2, end));
                        lastCtrl = c2;
                        cur = end;
                        break;
                    }

                case 'A':
                    {
                        float rx = Num(d, ref i), ry = Num(d, ref i), rot = Num(d, ref i);
                        bool large = Num(d, ref i) != 0, sweep = Num(d, ref i) != 0;
                        var end = o + new Vector2(Num(d, ref i), Num(d, ref i));
                        Arc(segs, cur, rx, ry, rot, large, sweep, end);
                        cur = end;
                        break;
                    }

                default:
                    throw new FormatException($"Unsupported path command '{cmd}'.");
            }

            prev = up;
        }

        Flush();
        return [.. figures];
    }

    /// <summary>A rounded rectangle as path data, clockwise from the top edge.</summary>
    public static string Rect(float x, float y, float w, float h, float r = 0) => r <= 0
        ? $"M{F(x)} {F(y)}h{F(w)}v{F(h)}h{F(-w)}z"
        : $"M{F(x + r)} {F(y)}h{F(w - 2 * r)}a{F(r)} {F(r)} 0 0 1 {F(r)} {F(r)}v{F(h - 2 * r)}a{F(r)} {F(r)} 0 0 1 {F(-r)} {F(r)}h{F(2 * r - w)}a{F(r)} {F(r)} 0 0 1 {F(-r)} {F(-r)}v{F(2 * r - h)}a{F(r)} {F(r)} 0 0 1 {F(r)} {F(-r)}z";

    /// <summary>A circle as path data, started at its right-hand point.</summary>
    public static string Circle(float cx, float cy, float r) =>
        $"M{F(cx + r)} {F(cy)}a{F(r)} {F(r)} 0 1 1 {F(-2 * r)} 0a{F(r)} {F(r)} 0 1 1 {F(2 * r)} 0z";

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);

    private static void SkipSep(string d, ref int i)
    {
        while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ','))
        {
            i++;
        }
    }

    private static float Num(string d, ref int i)
    {
        SkipSep(d, ref i);
        int s = i;
        if (i < d.Length && (d[i] == '+' || d[i] == '-'))
        {
            i++;
        }

        bool dot = false;
        while (i < d.Length && (char.IsAsciiDigit(d[i]) || (d[i] == '.' && !dot)))
        {
            dot |= d[i] == '.';
            i++;
        }

        if (i == s || !float.TryParse(d.AsSpan(s, i - s), NumberStyles.Float, CultureInfo.InvariantCulture, out float v))
        {
            throw new FormatException($"Expected a number at {s} in path data.");
        }

        return v;
    }

    // Endpoint to centre parameterisation, then one cubic per quarter turn at most.
    private static void Arc(List<OutlineSegment> into, Vector2 p0, float rx, float ry, float rotDeg, bool large, bool sweep, Vector2 p1)
    {
        if (p0 == p1)
        {
            return;
        }

        if (rx == 0 || ry == 0)
        {
            into.Add(OutlineSegment.Line(p1));
            return;
        }

        double rX = Math.Abs(rx), rY = Math.Abs(ry);
        double phi = rotDeg * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
        double dx = (p0.X - p1.X) / 2, dy = (p0.Y - p1.Y) / 2;
        double x1 = cos * dx + sin * dy, y1 = -sin * dx + cos * dy;
        double lambda = x1 * x1 / (rX * rX) + y1 * y1 / (rY * rY);
        if (lambda > 1)
        {
            rX *= Math.Sqrt(lambda);
            rY *= Math.Sqrt(lambda);
        }

        double num = rX * rX * rY * rY - rX * rX * y1 * y1 - rY * rY * x1 * x1;
        double den = rX * rX * y1 * y1 + rY * rY * x1 * x1;
        double k = Math.Sqrt(Math.Max(0, num / den)) * (large == sweep ? -1 : 1);
        double cxp = k * rX * y1 / rY, cyp = -k * rY * x1 / rX;
        double cx = cos * cxp - sin * cyp + (p0.X + p1.X) / 2, cy = sin * cxp + cos * cyp + (p0.Y + p1.Y) / 2;
        double a0 = Math.Atan2((y1 - cyp) / rY, (x1 - cxp) / rX);
        double delta = Math.Atan2((-y1 - cyp) / rY, (-x1 - cxp) / rX) - a0;
        if (sweep && delta < 0)
        {
            delta += 2 * Math.PI;
        }
        else if (!sweep && delta > 0)
        {
            delta -= 2 * Math.PI;
        }

        int n = Math.Max(1, (int)Math.Ceiling(Math.Abs(delta) / (Math.PI / 2) - 1e-9));
        double step = delta / n, t = 4.0 / 3 * Math.Tan(step / 4);

        // The ellipse point at angle a, pushed along its tangent by da.
        Vector2 At(double a, double da)
        {
            double ex = rX * (Math.Cos(a) - da * Math.Sin(a)), ey = rY * (Math.Sin(a) + da * Math.Cos(a));
            return new((float)(cx + cos * ex - sin * ey), (float)(cy + sin * ex + cos * ey));
        }

        for (int s = 0; s < n; s++)
        {
            double a = a0 + s * step, b = a + step;
            into.Add(OutlineSegment.Cubic(At(a, t), At(b, -t), s == n - 1 ? p1 : At(b, 0)));
        }
    }
}
