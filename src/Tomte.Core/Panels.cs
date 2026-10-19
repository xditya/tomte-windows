using System.Drawing;
using System.Numerics;
using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>The tabs of the Panels state. The enum order is the tab order.</summary>
public enum PanelId
{
    Home,
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
/// sticks until <see cref="Reset"/>, which the island calls when it hides, so it opens on the first tab again.
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

    /// <summary>Back to the first tab, as if nothing had been selected.</summary>
    public void Reset() => selected = null;

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
/// The row at the top of every panel, in DIPs from the panel's left edge and top. The tomte sits at the left padding;
/// after it comes the rail, a capsule holding one tab per panel in tab order, and at the right end the search button and
/// the settings button. The current tab shows its icon and its name on a lighter capsule; the others show the icon alone
/// and give their name as the tooltip. The Shelf tab carries a count of what is on the shelf. The search button shows
/// its word and the search shortcut when the row has room for them, and is a round icon button when it has not; the
/// current tab's name gives way next, cut to the room left and dropped when almost none is.
/// </summary>
public static class TabStrip
{
    public const float Pad = 16, MascotSize = 28, AfterMascot = 12;
    public const float Inset = 3, TabHeight = 34, TabWidth = 38, Gap = 2;
    public const float Icon = 18, IconGap = 6, LabelLeft = 11, LabelRight = 14, MinLabel = 24;
    public const float Badge = 15, BadgeTop = 3, BadgeAfterIcon = 6, BadgePadX = 4;
    public const float Button = 36, ButtonGap = 12, SearchLeft = 12, SearchRight = 8, SearchGap = 8;

    public const string SearchText = "Search", SettingsText = "Settings";

    /// <summary>The current tab's name.</summary>
    public static readonly TextStyle Label = new(13, 500);

    public static readonly TextStyle SearchLabel = new(13, Color: Theme.Ink2);

    /// <summary>The Shelf tab's count, on the accent.</summary>
    public static TextStyle BadgeLabel => new(10, 700, Theme.OnAccent, TextAlign.Center);

    /// <summary>The middle of the row, from the panel's top.</summary>
    public static float Middle => PanelSpace.TopPad + (PanelSpace.TabStripHeight / 2);

    /// <summary>Where the tomte sits on an open panel: its box at the left padding, centred down the row, a little larger than on the pill.</summary>
    public static MascotSeat Seat => new(Middle, Pad + (MascotArt.ArtLeft * MascotSize / MascotArt.ViewBox), MascotSize);

    /// <summary>One tab: its capsule, its icon's box, the name when it shows, and the count's capsule and words when it has one.</summary>
    public readonly record struct Tab(PanelId Id, RectangleF Bounds, RectangleF Icon, TextSlot? Label, RectangleF? Badge, string? Count);

    /// <summary>The whole row. <see cref="SearchLabel"/> and <see cref="Keycap"/> are null while the search button is a round one.</summary>
    public sealed record Row(RectangleF Rail, IReadOnlyList<Tab> Tabs, RectangleF Search, RectangleF SearchIcon, TextSlot? SearchLabel,
        RectangleF? Keycap, string? KeycapText, RectangleF Settings, RectangleF SettingsIcon);

    public static string Name(PanelId id) => id switch
    {
        PanelId.Home => "Home",
        PanelId.Shelf => "Shelf",
        PanelId.Shortcuts => "Shortcuts",
        PanelId.Media => "Media",
        PanelId.Scratchpad => "Scratchpad",
        PanelId.History => "History",
        _ => "Clipboard",
    };

    public static GlyphFigure[] Glyph(PanelId id) => id switch
    {
        PanelId.Home => Glyphs.Home,
        PanelId.Shelf => Glyphs.Shelf,
        PanelId.Shortcuts => Glyphs.Shortcuts,
        PanelId.Media => Glyphs.Media,
        PanelId.Scratchpad => Glyphs.Scratchpad,
        PanelId.History => Glyphs.History,
        _ => Glyphs.Clipboard,
    };

    /// <summary>The count on the Shelf tab: none for an empty shelf, and 99+ past 99.</summary>
    public static string? Count(int items) => items <= 0 ? null : items > 99 ? "99+" : items.ToString(System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// The row for these tabs on a panel this wide. <paramref name="searchKey"/> is the search shortcut as set in
    /// Settings ("Win+Alt+Space"), which the wide search button shows on a keycap; <paramref name="shelfCount"/> is what is
    /// on the shelf.
    /// </summary>
    public static Row Layout(IReadOnlyCollection<PanelId> tabs, PanelId? current, float panelWidth, bool mascot, string searchKey = "", int shelfCount = 0)
    {
        float mid = Middle;
        var settings = Round(panelWidth - Pad - Button, mid);
        float searchEnd = settings.Left - ButtonGap, railLeft = mascot ? Pad + MascotSize + AfterMascot : Pad;

        // Every tab at its icon width, then what the current one's name adds on top of that.
        float iconsOnly = (tabs.Count * TabWidth) + (Math.Max(0, tabs.Count - 1) * Gap) + (2 * Inset);
        const float NamedExtra = LabelLeft + Icon + IconGap + LabelRight - TabWidth;
        float name = current is { } c && tabs.Contains(c) ? TextSlot.Measure(Name(c), Label, panelWidth) : 0;

        string? cap = Keycap.Words(searchKey);
        float word = MathF.Ceiling(TextSlot.Measure(SearchText, SearchLabel, panelWidth));
        float capWidth = cap is null ? 0 : Keycap.Width(cap);
        float wide = SearchLeft + Icon + SearchGap + word + (cap is null ? 0 : SearchGap + capWidth) + SearchRight;
        bool roomy = railLeft + iconsOnly + (name > 0 ? NamedExtra + name : 0) + ButtonGap <= searchEnd - wide;
        float searchLeft = searchEnd - (roomy ? wide : Button);

        float labelWidth = Math.Min(name, searchLeft - ButtonGap - railLeft - iconsOnly - NamedExtra);
        bool named = labelWidth >= MinLabel;
        var list = new List<Tab>(tabs.Count);
        float x = railLeft + Inset, top = mid - (TabHeight / 2);
        foreach (var id in tabs)
        {
            bool showsName = named && id == current;
            float width = showsName ? LabelLeft + Icon + IconGap + labelWidth + LabelRight : TabWidth;
            var icon = new RectangleF(showsName ? x + LabelLeft : x + ((TabWidth - Icon) / 2), mid - (Icon / 2), Icon, Icon);
            TextSlot? label = showsName ? new(Name(id), Label, new(icon.Right + IconGap, top, labelWidth, TabHeight)) : null;
            RectangleF? badge = null;
            string? count = id == PanelId.Shelf ? Count(shelfCount) : null;
            if (count is not null)
            {
                float w = Math.Max(Badge, MathF.Ceiling(TextSlot.Measure(count, BadgeLabel, panelWidth)) + (2 * BadgePadX));
                badge = new RectangleF(icon.Right + BadgeAfterIcon - w, top + BadgeTop, w, Badge);
            }

            list.Add(new(id, new(x, top, width, TabHeight), icon, label, badge, count));
            x += width + Gap;
        }

        var rail = new RectangleF(railLeft, PanelSpace.TopPad, (tabs.Count == 0 ? 2 * Inset : x - Gap + Inset) - railLeft, PanelSpace.TabStripHeight);
        if (!roomy)
        {
            var round = Round(searchLeft, mid);
            return new(rail, list, round, Centered(round), null, null, null, settings, Centered(settings));
        }

        var search = new RectangleF(searchLeft, mid - (Button / 2), wide, Button);
        var lens = new RectangleF(searchLeft + SearchLeft, mid - (Icon / 2), Icon, Icon);
        var text = new TextSlot(SearchText, SearchLabel, new(lens.Right + SearchGap, search.Y, word, Button));
        RectangleF? key = cap is null ? null : new RectangleF(text.Slot.Right + SearchGap, mid - (Keycap.Height / 2), capWidth, Keycap.Height);
        return new(rail, list, search, lens, text, key, cap, settings, Centered(settings));
    }

    /// <summary>The search button's tooltip: the search shortcut as set in Settings, or Search alone when none is set.</summary>
    public static string SearchTip(string chord) => Hotkey.TryParse(chord, out var key, out _) ? $"Search ({key})" : "Search";

    private static RectangleF Round(float x, float mid) => new(x, mid - (Button / 2), Button, Button);

    private static RectangleF Centered(RectangleF button) => new(button.X + ((button.Width - Icon) / 2), button.Y + ((button.Height - Icon) / 2), Icon, Icon);

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
    /// <summary>The padding above the tab row, and the row itself.</summary>
    public const float TopPad = 12, TabStripHeight = 40;

    /// <summary>The host's part at the top of a panel, the padding and the row; the body starts under it.</summary>
    public const float Head = TopPad + TabStripHeight;

    /// <summary>A point in window pixels to island space; the origin is where island space starts in the window.</summary>
    public static Vector2 FromPixels(int x, int y, float originX, float originY, double scale) =>
        new((float)((x - originX) / scale), (float)((y - originY) / scale));

    public static Vector2 ToBody(Vector2 island, double panelWidth) =>
        new(island.X + (float)panelWidth / 2, island.Y - Head);

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
