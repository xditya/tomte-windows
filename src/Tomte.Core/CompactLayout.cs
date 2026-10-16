using System.Drawing;
using Tomte.Core.Interaction;
using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>
/// Where things sit on the 220 x 38 Compact pill, in island space: x from the pill's centre line, y from its top.
/// The tomte on the left, then the timer's ring, the now-playing row (art, title, four bars) or a line of words. Words
/// that nothing follows end the pill at its trailing end, as far in as the tomte starts at the leading end.
/// </summary>
public static class CompactLayout
{
    /// <summary>The gap after the tomte, after the ring and after the art.</summary>
    public const float Gap = 9;

    /// <summary>
    /// The room at either end of the pill: the tomte's art starts this far in at the leading end, words that end the
    /// pill stop this far in at the trailing end, and with the tomte off the first thing starts this far in.
    /// </summary>
    public const float EndInset = MascotArt.InsetX + (MascotArt.ArtLeft * MascotArt.SizeDips / MascotArt.ViewBox);

    public const float RingSize = 18, RingStroke = 3.5f;

    /// <summary>The now-playing row's four bars: where they start and the band they breathe in.</summary>
    public const float BarsLeft = 80, BarsTop = 9, BarsHeight = 20;

    /// <summary>How far each of the four bars reaches, as a share of the waveform's swing.</summary>
    public static readonly float[] BarReach = [.35f, .62f, .85f, 1f];

    public const float ArtCorner = 6;

    /// <summary>The pill's one line is 13 DIP semibold white; the timer's countdown keeps its digits still.</summary>
    public static readonly TextStyle TitleStyle = new(13, 600);
    public static readonly TextStyle LineStyle = TitleStyle;
    public static readonly TextStyle ClockStyle = LineStyle with { Tabular = true };

    private static float Width => (float)IslandGeometry.Compact.Width;

    /// <summary>The pill's trailing end in island space; what is laid out here sits this far left in a frame measured from that end.</summary>
    public static float End => Width / 2;

    public const string PillId = "pill", TomteId = "tomte", AskTip = "Ask Tomte";

    /// <summary>
    /// What a click on a pill of Compact's depth does, in island space for <paramref name="shape"/>: the whole pill opens
    /// Panels and the tomte on it, which comes later and so sits on top, asks Tomte. On the peek badge, where the tomte
    /// is all there is, the badge is the tomte, and a rest on it opens Panels as on the pill; it has no tip, since a card
    /// kept inside the badge has no room for a word. A drag on either moves the island.
    /// </summary>
    public static IReadOnlyList<Target> Targets(in IslandGeometry shape, bool mascot)
    {
        var pill = new RectangleF(-(float)shape.Width / 2, 0, (float)shape.Width, (float)shape.Depth);
        var ask = new Target(TomteId, pill, (float)shape.Corner, TargetKind.Button, Tip: AskTip, Draggable: true);
        var open = new Target(PillId, pill, (float)shape.Corner, TargetKind.Button, Draggable: true);
        return !mascot ? [open]
            : shape.Width <= IslandGeometry.Peek.Width ? [ask with { Tip = null }]
            : [open, ask with { Bounds = MascotArt.Box(shape), Corner = MascotArt.SizeDips / 2 }];
    }

    /// <summary>Where the ring or the art starts, from the pill's left edge: after the tomte, or at the end inset when it is off.</summary>
    public static float RingLeft(bool mascot) => mascot ? MascotArt.InsetX + MascotArt.SizeDips + Gap : EndInset;

    /// <summary>The now-playing row's art, where the ring would be.</summary>
    public static RectangleF Art(bool mascot) => new(-Width / 2 + RingLeft(mascot), 8, 22, 22);

    /// <summary>The title's slot after the art: up to the bars, or to the end inset on a pill that shows no bars.</summary>
    public static RectangleF Title(bool mascot, bool bars)
    {
        float x = Art(mascot).Right + Gap;
        return new(x, 9, (bars ? BarsLeft - Gap : (Width / 2) - EndInset) - x, 20);
    }

    /// <summary>
    /// How words sit in their slot when nothing follows them on the pill: at the trailing end, across the pill from the
    /// tomte, ring or art that leads them, or centred when nothing leads them either, so neither end of the pill is
    /// left empty.
    /// </summary>
    public static TextStyle Ending(TextStyle style, bool led) => style with { Align = led ? TextAlign.Trailing : TextAlign.Center };

    /// <summary>The undo pill's button sits this far inside the pill on the right, above and below, and pads its words this much.</summary>
    public const float UndoInset = 6, UndoPadX = 12, UndoKeyGap = 6;

    /// <summary>
    /// The pill after a closed AI view, from the measured widths of its line and of what its button says: as wide as
    /// they need and never narrower than Compact. The line starts where the ring would and ends before the button, which
    /// sits at the right end. In island space, like the rest.
    /// </summary>
    public static (float Width, RectangleF Line, RectangleF Button) Undo(bool mascot, float line, float button)
    {
        float depth = (float)IslandGeometry.Compact.Depth, pad = button + 2 * UndoPadX;
        float width = Math.Max(Width, RingLeft(mascot) + line + Gap + pad + UndoInset);
        float left = -width / 2 + RingLeft(mascot);
        var box = new RectangleF((width / 2) - UndoInset - pad, UndoInset, pad, depth - (2 * UndoInset));
        return (width, new RectangleF(left, 0, box.X - Gap - left, depth), box);
    }

    /// <summary>The line's slot when nothing leads it but the tomte: from where the ring would start to the end inset. The words sit in it as <see cref="Ending"/> says.</summary>
    public static RectangleF Line(bool mascot)
    {
        float x = -Width / 2 + RingLeft(mascot);
        return new RectangleF(x, 0, Width / 2 - EndInset - x, (float)IslandGeometry.Compact.Depth);
    }

    /// <summary>
    /// A lead (the timer's ring, a track's art) and its words as one group, in island space: ending at the end inset with
    /// the tomte on, centred on the pill with it off, the lead a gap before the words. Words wider than the room are cut
    /// to it. Returns where the lead starts and the words' slot across.
    /// </summary>
    public static (float Lead, float WordsX, float WordsWidth) Group(bool mascot, float lead, float words)
    {
        float left = -Width / 2 + RingLeft(mascot), right = Width / 2 - EndInset;
        words = Math.Min(words, right - left - lead - Gap);
        float group = lead + Gap + words, end = mascot ? right : group / 2;
        return (end - group, end - words, words);
    }
}
