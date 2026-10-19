using System.Drawing;
using Tomte.Core.Interaction;
using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>
/// Where things sit on the 220 x 40 Compact pill, in island space: x from the pill's centre line, y from its top.
/// The tomte on the left, then the timer's ring, the now-playing row (art, title, artist, four bars), the push pill's
/// time and glance, or a line of words. Words that nothing follows end the pill at its trailing end, as far in as the
/// tomte starts at the leading end.
/// </summary>
public static class CompactLayout
{
    /// <summary>The gap after the tomte, after the ring and the art, and between the pieces of a row.</summary>
    public const float Gap = 10;

    /// <summary>
    /// The room at either end of the pill: the tomte's art starts this far in at the leading end, words that end the
    /// pill stop this far in at the trailing end, and with the tomte off the first thing starts this far in.
    /// </summary>
    public const float EndInset = MascotArt.InsetX + MascotArt.ArtLeftDips;

    public const float RingSize = 22, RingStroke = 3;

    /// <summary>The now-playing row's four bars: where they start, at the trailing end, and the band they rise in from its foot.</summary>
    public const float BarsLeft = 78, BarsTop = 13, BarsHeight = 14;

    /// <summary>How far each of the four bars reaches, as a share of the band.</summary>
    public static readonly float[] BarReach = [.57f, 1f, .36f, .79f];

    public const float ArtSize = 26, ArtCorner = 7;

    /// <summary>The pill's words: 13 DIP medium. A track's title is the same, its artist quieter beside it.</summary>
    public static TextStyle TitleStyle => new(13, 500);
    public static TextStyle ArtistStyle => new(13, Color: Theme.Ink3);
    public static TextStyle LineStyle => TitleStyle;

    /// <summary>A running timer: the word, quiet, then the time left in mono so its digits keep still.</summary>
    public static TextStyle ClockStyle => new(14, 500, Tabular: true, Family: TextStyle.Mono);
    public static TextStyle TimerLabelStyle => new(13, Color: Theme.Ink2);
    public const string TimerLabel = "Timer";

    /// <summary>The push pill's glance: the time of day in mono, a short rule, then the one more thing it mentions.</summary>
    public static TextStyle TimeStyle => new(13, 500, Tabular: true, Family: TextStyle.Mono);
    public static TextStyle GlanceStyle => new(13, Color: Theme.Ink2);
    public const float RuleHeight = 14;

    /// <summary>What separates the time from the rest in a glance's line.</summary>
    public const string GlanceSeparator = " · ";

    /// <summary>The narrowest the artist is cut to before it is left out.</summary>
    public const float MinArtist = 32;

    /// <summary>The undo pill's button sits this far inside the pill on the right, above and below, and pads its words this much.</summary>
    public const float UndoInset = 6, UndoPadX = 12, UndoKeyGap = 6, UndoGlyph = 14;

    /// <summary>What closed, on the undo pill: quieter than the button that brings it back.</summary>
    public static TextStyle UndoLineStyle => new(13, Color: Theme.Ink2);

    /// <summary>The listening pill's accent dot and how far its soft halo reaches past it; the time held is mono.</summary>
    public const float DotSize = 8, Halo = 4;
    public static TextStyle ElapsedStyle => new(13, Tabular: true, Family: TextStyle.Mono);

    public const string PillId = "pill", TomteId = "tomte", AskTip = "Ask Tomte";

    private static float Width => (float)IslandGeometry.Compact.Width;

    private static float Depth => (float)IslandGeometry.Compact.Depth;

    /// <summary>The pill's trailing end in island space; what is laid out here sits this far left in a frame measured from that end.</summary>
    public static float End => Width / 2;

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

    /// <summary>
    /// The peek style as it can be drawn: the perched tomte needs the tomte, and the perched tomte and the edge lip hang
    /// from the screen's edge through the floating pill's gap. Without the tomte the perched one is the lip; on the
    /// notch, which has no gap, both are the pill.
    /// </summary>
    public static PeekStyle Resting(PeekStyle style, bool tomte, bool attached) => style switch
    {
        PeekStyle.Perched or PeekStyle.Lip when attached => PeekStyle.Pill,
        PeekStyle.Perched when !tomte => PeekStyle.Lip,
        _ => style,
    };

    /// <summary>The shape desktop peek rests as for a style from <see cref="Resting"/>; the corner pill still needs its shift.</summary>
    public static IslandGeometry PeekShape(PeekStyle resting) => resting switch
    {
        PeekStyle.Perched => IslandGeometry.Perched,
        PeekStyle.Lip => IslandGeometry.Lip,
        PeekStyle.Corner => IslandGeometry.CornerPill(CornerWidth),
        _ => IslandGeometry.Compact,
    };

    /// <summary>
    /// Where the tomte sits while peek rests, or null for its place on the pill: nowhere on the lip (a seat of size 0, so it
    /// grows in as the lip grows into the pill) and smaller, at the corner pill's inset, on the corner pill.
    /// </summary>
    public static MascotSeat? PeekSeat(PeekStyle resting) => resting switch
    {
        PeekStyle.Lip => new MascotSeat(PanelSpace.TabStripHeight / 2, MascotArt.InsetX, 0),
        PeekStyle.Corner => CornerSeat,
        _ => null,
    };

    /// <summary>The corner pill: its tomte's size and inset, the gap after it, the room after the time, and the time's style.</summary>
    public const float CornerTomte = 20, CornerInset = 7, CornerGap = 8, CornerEnd = 12;

    public static readonly TextStyle CornerTimeStyle = new(12, 500, Tabular: true, Family: TextStyle.Mono);

    /// <summary>
    /// The corner pill's seat, worked back through <see cref="MascotArt.Box"/>: a pill shallower than Compact draws the tomte
    /// smaller by <see cref="MascotArt.Ride"/>, so the seat makes up the rest to put a <see cref="CornerTomte"/> box
    /// <see cref="CornerInset"/> from the left edge, centred down the pill.
    /// </summary>
    public static MascotSeat CornerSeat
    {
        get
        {
            float k = (float)MascotArt.Ride(IslandGeometry.CornerPill(0)), size = CornerTomte / k;
            return new MascotSeat((float)IslandGeometry.CornerDepth / 2, (CornerInset / k) + (MascotArt.ArtLeft * size / MascotArt.ViewBox), size);
        }
    }

    /// <summary>
    /// How wide the corner pill is: the tomte, the gap, the time and the end, with the time measured at its widest, a
    /// two-digit hour in the user's culture, so the pill keeps one width all day.
    /// </summary>
    public static float CornerWidth =>
        CornerInset + CornerTomte + CornerGap + TextSlot.Measure(Triggers.CompactRules.TimeOfDay(new DateTimeOffset(new DateTime(2000, 1, 1, 22, 58, 0, DateTimeKind.Local))), CornerTimeStyle, 4096) + CornerEnd;

    /// <summary>The time's slot on a corner pill this wide, in island space.</summary>
    public static RectangleF CornerClock(float width)
    {
        float x = -width / 2 + CornerInset + CornerTomte + CornerGap;
        return new(x, 0, width / 2 - CornerEnd - x + 1, (float)IslandGeometry.CornerDepth);
    }

    /// <summary>The edge lip's dot while something waits, in island space: 4 x 2, 2 below the screen's edge, centred.</summary>
    public static RectangleF LipDot => new(-2, (float)(IslandGeometry.Lip.Lift - IslandOutline.Gap) + 2, 4, 2);

    /// <summary>Where the ring or the art starts, from the pill's left edge: after the tomte, or at the end inset when it is off.</summary>
    public static float RingLeft(bool mascot) => mascot ? MascotArt.InsetX + MascotArt.SizeDips + Gap : EndInset;

    /// <summary>The now-playing row's art, where the ring would be, centred down the pill.</summary>
    public static RectangleF Art(bool mascot) => new(-Width / 2 + RingLeft(mascot), (Depth - ArtSize) / 2, ArtSize, ArtSize);

    /// <summary>The words' room after the art, up to a gap before the bars. On a pill with no bars the art and the words are a <see cref="Group"/>.</summary>
    public static RectangleF Title(bool mascot)
    {
        float x = Art(mascot).Right + Gap;
        return new(x, 0, BarsLeft - Gap - x, Depth);
    }

    /// <summary>
    /// A track's title and artist across <paramref name="room"/>, in island space: the title first, as wide as it needs
    /// up to the whole room, and the artist a gap after it in what is left. An artist with less than
    /// <see cref="MinArtist"/> left drops out (width 0), so the title is never cut for a sliver of the name.
    /// </summary>
    public static (RectangleF Title, RectangleF Artist) Track(RectangleF room, float title, float artist)
    {
        float t = Math.Min(title, room.Width);
        float left = room.Width - t - Gap;
        float a = artist > 0 && left >= Math.Min(artist, MinArtist) ? Math.Min(artist, left) : 0;
        return (new(room.X, 0, t, Depth), new(room.X + t + Gap, 0, a, Depth));
    }

    /// <summary>How wide a title and its artist are side by side, for a <see cref="Group"/>.</summary>
    public static float TrackWidth(float title, float artist) => artist > 0 ? title + Gap + artist : title;

    /// <summary>
    /// How words sit in their slot when nothing follows them on the pill: at the trailing end, across the pill from the
    /// tomte, ring or art that leads them, or centred when nothing leads them either, so neither end of the pill is
    /// left empty.
    /// </summary>
    public static TextStyle Ending(TextStyle style, bool led) => style with { Align = led ? TextAlign.Trailing : TextAlign.Center };

    /// <summary>
    /// The pill after a closed AI view, from the measured widths of its line and of what its button holds: as wide as
    /// they need and never narrower than Compact. The line starts where the ring would and ends before the button, which
    /// sits at the right end. In island space, like the rest.
    /// </summary>
    public static (float Width, RectangleF Line, RectangleF Button) Undo(bool mascot, float line, float button)
    {
        float pad = button + 2 * UndoPadX;
        float width = Math.Max(Width, RingLeft(mascot) + line + Gap + pad + UndoInset);
        float left = -width / 2 + RingLeft(mascot);
        var box = new RectangleF((width / 2) - UndoInset - pad, UndoInset, pad, Depth - (2 * UndoInset));
        return (width, new RectangleF(left, 0, box.X - Gap - left, Depth), box);
    }

    /// <summary>What the undo button says: Undo and the whole seconds left, rounded up so it never reads 0 while it still works.</summary>
    public static string UndoLabel(TimeSpan left) => $"Undo · {Math.Max(1, (int)Math.Ceiling(left.TotalSeconds))}s";

    /// <summary>The line's slot when nothing leads it but the tomte: from where the ring would start to the end inset. The words sit in it as <see cref="Ending"/> says.</summary>
    public static RectangleF Line(bool mascot)
    {
        float x = -Width / 2 + RingLeft(mascot);
        return new RectangleF(x, 0, Width / 2 - EndInset - x, Depth);
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

    /// <summary>
    /// The listening pill in island space: where the dot starts, where the bars start and the time held's slot, as one
    /// group centred in the room right of the tomte, or on the pill with the tomte off. The halo reaches into the gaps.
    /// </summary>
    public static (float Dot, float Bars, RectangleF Elapsed) Listening(bool mascot, float elapsed)
    {
        float bars = (Rambler.Waveform.Bars * Rambler.Waveform.BarWidth) + ((Rambler.Waveform.Bars - 1) * Rambler.Waveform.Gap);
        var slot = Line(mascot);
        float start = slot.X + ((slot.Width - (DotSize + Gap + bars + Gap + elapsed)) / 2);
        float barsX = start + DotSize + Gap;
        return (start, barsX, new(barsX + bars + Gap, 0, elapsed + 16, Depth));
    }

    /// <summary>How wide the words beside the ring are: a running timer's word and time left, or a line such as the done pill's.</summary>
    public static float RingWords(string words, bool countdown) => countdown
        ? TextSlot.Measure(TimerLabel, TimerLabelStyle, 4096) + Gap + TextSlot.Measure(words, ClockStyle, 4096)
        : TextSlot.Measure(words, LineStyle, 4096);

    /// <summary>A glance's line split at its first separator: the time of day, and what follows it (empty when nothing does).</summary>
    public static (string Time, string After) SplitGlance(string line)
    {
        int at = line.IndexOf(GlanceSeparator, StringComparison.Ordinal);
        return at < 0 ? (line, "") : (line[..at], line[(at + GlanceSeparator.Length)..]);
    }

    /// <summary>How wide a glance's line is drawn: the time, a gap, the rule, a gap and the rest.</summary>
    public static float GlanceWidth(string line)
    {
        var (time, rest) = SplitGlance(line);
        float w = TextSlot.Measure(time, TimeStyle, 4096);
        return rest.Length == 0 ? w : w + (2 * Gap) + 1 + TextSlot.Measure(rest, GlanceStyle, 4096);
    }

    /// <summary>
    /// The glance's pieces in island space: the time's slot, the rule's x and the rest's slot, as one group that ends
    /// at the end inset with the tomte on and is centred with it off, like words that end the pill.
    /// </summary>
    public static (RectangleF Time, float Rule, RectangleF After) Glance(bool mascot, float time, float rest)
    {
        var slot = Line(mascot);
        float group = rest > 0 ? time + (2 * Gap) + 1 + rest : time;
        float start = mascot ? slot.Right - group : -group / 2;
        float rule = start + time + Gap;
        return (new(start, 0, time + 1, Depth), rule, new(rule + 1 + Gap, 0, rest + 1, Depth));
    }
}
