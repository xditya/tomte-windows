using System.Drawing;
using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>
/// Where things sit on the 220 x 38 Compact pill, in island space: x from the pill's centre line, y from its top.
/// The tomte on the left, then the timer's ring, the now-playing row (art, title, four bars) or a line of words;
/// 12 DIPs stay free on the right.
/// </summary>
public static class CompactLayout
{
    /// <summary>The gap after the tomte and after the ring, and the room kept free on the right.</summary>
    public const float Gap = 9, RightPad = 12;

    public const float RingSize = 18, RingStroke = 3.5f;

    /// <summary>The now-playing row's four bars: where they start and the band they breathe in.</summary>
    public const float BarsLeft = 80, BarsTop = 9, BarsHeight = 20;

    /// <summary>How far each of the four bars reaches, as a share of the waveform's swing.</summary>
    public static readonly float[] BarReach = [.35f, .62f, .85f, 1f];

    public static readonly RectangleF Art = new(-67, 8, 22, 22);
    public const float ArtCorner = 6;
    public static readonly RectangleF Title = new(-36, 9, 107, 20);

    /// <summary>The pill's one line is 13 DIP semibold white; the timer's countdown keeps its digits still.</summary>
    public static readonly TextStyle TitleStyle = new(13, 600);
    public static readonly TextStyle LineStyle = TitleStyle;
    public static readonly TextStyle ClockStyle = LineStyle with { Tabular = true };

    private static float Width => (float)IslandGeometry.Compact.Width;

    /// <summary>Where the ring starts, from the pill's left edge: after the tomte, or at its inset when it is off.</summary>
    public static float RingLeft(bool mascot) => mascot ? MascotArt.InsetX + MascotArt.SizeDips + Gap : MascotArt.InsetX;

    /// <summary>The line's slot: where the ring would start, or after the ring when it shows, to the right padding.</summary>
    public static RectangleF Line(bool mascot, bool ring)
    {
        float x = -Width / 2 + RingLeft(mascot) + (ring ? RingSize + Gap : 0);
        return new RectangleF(x, 0, Width / 2 - RightPad - x, (float)IslandGeometry.Compact.Depth);
    }
}
