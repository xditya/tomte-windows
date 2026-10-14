using System.Drawing;
using Tomte.Core.Interaction;
using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>
/// Where things sit on the 220 x 38 Compact pill, in island space: x from the pill's centre line, y from its top.
/// The tomte on the left, then the timer's ring, the now-playing row (art, title, four bars) or a line of words;
/// 12 DIPs stay free on the right.
/// </summary>
public static class CompactLayout
{
    /// <summary>The gap after the tomte and after the ring, the room before the first thing with the tomte off, and the room kept free on the right.</summary>
    public const float Gap = 9, LeftPad = 8, RightPad = 12;

    public const float RingSize = 18, RingStroke = 3.5f;

    /// <summary>The now-playing row's four bars: where they start and the band they breathe in.</summary>
    public const float BarsLeft = 80, BarsTop = 9, BarsHeight = 20;

    /// <summary>How far each of the four bars reaches, as a share of the waveform's swing.</summary>
    public static readonly float[] BarReach = [.35f, .62f, .85f, 1f];

    public static readonly RectangleF Art = new(-69.5f, 8, 22, 22);
    public const float ArtCorner = 6;
    public static readonly RectangleF Title = new(-38.5f, 9, 109.5f, 20);

    /// <summary>The pill's one line is 13 DIP semibold white; the timer's countdown keeps its digits still.</summary>
    public static readonly TextStyle TitleStyle = new(13, 600);
    public static readonly TextStyle LineStyle = TitleStyle;
    public static readonly TextStyle ClockStyle = LineStyle with { Tabular = true };

    private static float Width => (float)IslandGeometry.Compact.Width;

    public const string PillId = "pill", TomteId = "tomte", AskTip = "Ask Tomte";

    /// <summary>
    /// What a click on a pill of Compact's depth does, in island space for <paramref name="shape"/>: the whole pill opens
    /// Panels and the tomte on it, which comes later and so sits on top, asks Tomte. On the peek badge, where the tomte
    /// is all there is, the badge is the tomte. A drag on either moves the island.
    /// </summary>
    public static IReadOnlyList<Target> Targets(in IslandGeometry shape, bool mascot)
    {
        var pill = new RectangleF(-(float)shape.Width / 2, 0, (float)shape.Width, (float)shape.Depth);
        var ask = new Target(TomteId, pill, (float)shape.Corner, TargetKind.Button, Tip: AskTip, Draggable: true);
        var open = new Target(PillId, pill, (float)shape.Corner, TargetKind.Button, Draggable: true);
        return !mascot ? [open]
            : shape.Width <= IslandGeometry.Peek.Width ? [ask]
            : [open, ask with { Bounds = MascotArt.Box(shape), Corner = MascotArt.SizeDips / 2 }];
    }

    /// <summary>Where the ring starts, from the pill's left edge: after the tomte, or at the left padding when it is off.</summary>
    public static float RingLeft(bool mascot) => mascot ? MascotArt.InsetX + MascotArt.SizeDips + Gap : LeftPad;

    /// <summary>The line's slot: where the ring would start, or after the ring when it shows, to the right padding.</summary>
    public static RectangleF Line(bool mascot, bool ring)
    {
        float x = -Width / 2 + RingLeft(mascot) + (ring ? RingSize + Gap : 0);
        return new RectangleF(x, 0, Width / 2 - RightPad - x, (float)IslandGeometry.Compact.Depth);
    }
}
