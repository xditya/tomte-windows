using System.Numerics;
using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>The tabs of the Panels state. The enum order is the tab order.</summary>
public enum PanelId
{
    Shelf,
    Shortcuts,
    Media,
    Scratchpad,
    Clipboard,
    History,
}

/// <summary>
/// Which panels exist and which one Panels shows. Tabs sit in <see cref="PanelId"/> order whatever order
/// features register in. Until something is selected the first tab is current; after that the choice
/// sticks, so reopening Panels lands on the last-used tab.
/// the last-used tab lives in memory; write it to the store if it should survive a restart.
/// </summary>
public sealed class PanelTabs
{
    private readonly SortedSet<PanelId> registered = [];
    private PanelId? selected;

    public IReadOnlyCollection<PanelId> Order => registered;

    public PanelId? Current => selected ?? (registered.Count > 0 ? registered.Min : null);

    public void Register(PanelId id)
    {
        if (!registered.Add(id))
        {
            throw new InvalidOperationException($"The {id} panel is already registered.");
        }
    }

    /// <summary>False when the panel is not registered or is already current; nothing changes then.</summary>
    public bool Select(PanelId id)
    {
        if (!registered.Contains(id) || Current == id)
        {
            return false;
        }

        selected = id;
        return true;
    }

    /// <summary>The next tab (+1) or the previous one (-1), wrapping at both ends. False with fewer than two panels.</summary>
    public bool Step(int direction)
    {
        if (registered.Count < 2 || direction == 0)
        {
            return false;
        }

        var order = registered.ToArray();
        int i = Array.IndexOf(order, Current!.Value) + Math.Sign(direction);
        selected = order[(i + order.Length) % order.Length];
        return true;
    }
}

/// <summary>
/// The tab labels in the 42 DIP strip, in DIPs from the panel's left edge and top: after the tomte (or the left
/// padding with it off), each label centred in a pill 10 DIP wider than its text on each side (less, down to 6, on a
/// strip too short for that), 2 DIP apart, the current one white, the others at 60%. The Ask pill, which opens the
/// palette, always ends at the right padding, with the search button just before it; a label that would still run into
/// them is cut to the room left, and one with almost no room is left out.
/// </summary>
public static class TabStrip
{
    public const float PadX = 10, MinPadX = 6, Gap = 2, Height = 28, Corner = 8, AfterMascot = 6, LeftPad = 8, RightPad = 10, MinWidth = 32;

    /// <summary>The sparkle before the Ask pill's word: this tall, this far before the word.</summary>
    public const float GlyphHeight = 11, GlyphGap = 5;

    /// <summary>The Ask pill's word and sparkle are light blue, so it reads as an action and not a tab; its card is the same blue, faint.</summary>
    public const uint AskColor = 0xFFBCD3FF, AskCard = 0x1FBCD3FF;

    public const string AskText = "Ask";

    /// <summary>The magnifier's box in the search button, in DIPs.</summary>
    public const float SearchGlyphSize = 14;

    /// <summary>The magnifier in its 14 DIP box: a ring stroked round (5.75, 5.75) and a handle off it to the lower right.</summary>
    public static readonly GlyphFigure[] SearchGlyph =
    [
        new(
        [
            OutlineSegment.Move(new(9.75f, 5.75f)),
            OutlineSegment.Cubic(new(9.75f, 7.96f), new(7.96f, 9.75f), new(5.75f, 9.75f)),
            OutlineSegment.Cubic(new(3.54f, 9.75f), new(1.75f, 7.96f), new(1.75f, 5.75f)),
            OutlineSegment.Cubic(new(1.75f, 3.54f), new(3.54f, 1.75f), new(5.75f, 1.75f)),
            OutlineSegment.Cubic(new(7.96f, 1.75f), new(9.75f, 3.54f), new(9.75f, 5.75f)),
        ], Stroked: true),
        new(Polygon(8.93f, 7.87f, 13.13f, 12.07f, 12.07f, 13.13f, 7.87f, 8.93f)),
    ];

    public static readonly TextStyle Label = new(12.5f, Color: TextStyle.SecondaryColor, Align: TextAlign.Center);
    public static readonly TextStyle Current = Label with { Color = TextStyle.WhiteColor };
    public static readonly TextStyle AskLabel = Label with { Color = AskColor };

    /// <summary>The sparkle's box, in the units of <see cref="Sparkle"/>.</summary>
    public static readonly Vector2 SparkleBox = new(17.5f, 19);

    /// <summary>The sparkle: a large four-pointed star and a small one below it to the right, one closed figure each.</summary>
    public static readonly OutlineSegment[][] Sparkle =
    [
        Polygon(7, 0, 8.8f, 5.2f, 14, 7, 8.8f, 8.8f, 7, 14, 5.2f, 8.8f, 0, 7, 5.2f, 5.2f),
        Polygon(14, 12, 14.9f, 14.6f, 17.5f, 15.5f, 14.9f, 16.4f, 14, 19, 13.1f, 16.4f, 10.5f, 15.5f, 13.1f, 14.6f),
    ];

    public static string Name(PanelId id) => id switch
    {
        PanelId.Shelf => "Shelf",
        PanelId.Shortcuts => "Shortcuts",
        PanelId.Media => "Media",
        PanelId.Scratchpad => "Scratchpad",
        PanelId.History => "History",
        _ => "Clipboard",
    };

    /// <summary>One label per tab, in tab order; the current one's slot is also where its pill goes.</summary>
    public static IReadOnlyList<TextSlot> Labels(IEnumerable<PanelId> tabs, PanelId? current, float panelWidth, bool mascot)
    {
        float x = mascot ? MascotArt.InsetX + MascotArt.SizeDips + AfterMascot : LeftPad;
        float y = (PanelSpace.TabStripHeight - Height) / 2;
        float end = SearchButton(panelWidth).Card.Left - Gap;
        var words = tabs.Select(id => (Text: Name(id), Style: id == current ? Current : Label)).ToList();
        var widths = words.Select(w => TextSlot.Measure(w.Text, w.Style, panelWidth)).ToList();
        // Short of room, every label's padding gives way first, down to MinPadX, before the last ones are cut or left out.
        float pad = Math.Clamp((end - x - widths.Sum() - (Gap * (words.Count - 1))) / (2 * Math.Max(1, words.Count)), MinPadX, PadX);
        var labels = new List<TextSlot>();
        for (int i = 0; i < words.Count; i++)
        {
            float room = end - x;
            if (room < MinWidth)
            {
                break;
            }

            float width = widths[i] + (2 * pad);
            labels.Add(new(words[i].Text, words[i].Style, new(x, y, Math.Min(width, room), Height)));
            x += width + Gap;
        }

        return labels;
    }

    /// <summary>The tab whose label is under <paramref name="x"/>, in DIPs from the panel's left edge; null between or past them.</summary>
    public static PanelId? At(float x, IReadOnlyCollection<PanelId> tabs, PanelId? current, float panelWidth, bool mascot)
    {
        var labels = Labels(tabs, current, panelWidth, mascot);
        int i = 0;
        foreach (var id in tabs.Take(labels.Count))
        {
            var slot = labels[i++].Slot;
            if (x >= slot.Left && x < slot.Right)
            {
                return id;
            }
        }

        return null;
    }

    /// <summary>
    /// The Ask pill at the strip's right padding, as tall as a tab's: its card, the sparkle's box and the word, with
    /// <see cref="PadX"/> before the sparkle and after the word.
    /// </summary>
    public static (System.Drawing.RectangleF Card, System.Drawing.RectangleF Glyph, TextSlot Label) AskPill(float panelWidth)
    {
        float text = TextSlot.Measure(AskText, AskLabel, panelWidth);
        float glyph = GlyphHeight * SparkleBox.X / SparkleBox.Y;
        float width = PadX + glyph + GlyphGap + text + PadX;
        float x = panelWidth - RightPad - width, y = (PanelSpace.TabStripHeight - Height) / 2;
        return (new(x, y, width, Height),
            new(x + PadX, (PanelSpace.TabStripHeight - GlyphHeight) / 2, glyph, GlyphHeight),
            new(AskText, AskLabel, new(x + PadX + glyph + GlyphGap, y, text, Height)));
    }

    /// <summary>The search button just before the Ask pill: a square card as tall as a tab's, with the magnifier centred in it.</summary>
    public static (System.Drawing.RectangleF Card, System.Drawing.RectangleF Glyph) SearchButton(float panelWidth)
    {
        var ask = AskPill(panelWidth).Card;
        float x = ask.Left - Gap - Height, inset = (Height - SearchGlyphSize) / 2;
        return (new(x, ask.Y, Height, Height), new(x + inset, ask.Y + inset, SearchGlyphSize, SearchGlyphSize));
    }

    /// <summary>The search button's tooltip: the search shortcut as set in Settings, or Search alone when none is set.</summary>
    public static string SearchTip(string chord) => Hotkey.TryParse(chord, out var key, out _) ? $"Search ({key})" : "Search";

    /// <summary>Whether <paramref name="x"/>, in DIPs from the panel's left edge, is on the Ask pill.</summary>
    public static bool OnAsk(float x, float panelWidth)
    {
        var card = AskPill(panelWidth).Card;
        return x >= card.Left && x < card.Right;
    }

    internal static OutlineSegment[] Polygon(params float[] xy) =>
        [OutlineSegment.Move(new(xy[0], xy[1])), .. Enumerable.Range(1, (xy.Length / 2) - 1).Select(i => OutlineSegment.Line(new(xy[2 * i], xy[(2 * i) + 1])))];
}

/// <summary>
/// The two coordinate spaces island content uses, both in DIPs. Island space has x = 0 on the island's
/// centre line and y = 0 at the island's top edge. A panel body's space starts at the body's top-left
/// corner, under the tab strip; the strip row belongs to the host, and the mascot sits in it.
/// </summary>
public static class PanelSpace
{
    public const float TabStripHeight = 42;

    /// <summary>A point in window pixels to island space; the origin is where island space starts in the window.</summary>
    public static Vector2 FromPixels(int x, int y, float originX, float originY, double scale) =>
        new((float)((x - originX) / scale), (float)((y - originY) / scale));

    public static Vector2 ToBody(Vector2 island, double panelWidth) =>
        new(island.X + (float)panelWidth / 2, island.Y - TabStripHeight);

    /// <summary>
    /// An island point's x from the tab strip's left edge, which is the shape's body edge, as <see cref="TabStrip"/>
    /// lays it out: the ramps an attached shape flares into the top edge with lie outside it, and the tomte sits inside.
    /// </summary>
    public static float ToStrip(Vector2 island, in IslandGeometry shape) => island.X + (float)shape.Width / 2;

    /// <summary>
    /// Whether an island point is on the shape itself: no further than half its width from the centre line,
    /// and between its top and bottom edges. The window also covers the shadow beside and below the pill and
    /// a row above it, and a press or a wheel there is nobody's.
    /// </summary>
    public static bool OnShape(Vector2 island, in IslandGeometry shape) =>
        Math.Abs(island.X) <= shape.Width / 2 && island.Y >= 0 && island.Y <= shape.Depth;

    /// <summary>
    /// The wheel over the tab strip as a tab step: -1 for a notch (120) away from the user, +1 toward, else 0.
    /// A touchpad's small deltas add up in <paramref name="carry"/>. While the new panel's content has not
    /// started to show, the strip ignores the wheel, so the rest of the scroll that opened Panels or switched a
    /// tab does not switch another.
    /// </summary>
    public static int TabStep(ref int carry, int delta, bool settling)
    {
        carry = settling ? 0 : carry + delta;
        if (Math.Abs(carry) < 120)
        {
            return 0;
        }

        int step = carry > 0 ? -1 : 1;
        carry = 0;
        return step;
    }
}
