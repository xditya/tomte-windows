using System.Drawing;
using System.Numerics;
using Tomte.Core.Interaction;
using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>Which items the shelf's grid shows.</summary>
public enum ShelfFilter
{
    All,
    Files,
    Text,
    Links,
}

/// <summary>What a tile's preview square shows for an item.</summary>
public enum ShelfPreview
{
    /// <summary>The picture itself, cut to fill the square.</summary>
    Image,

    /// <summary>The first lines of the text.</summary>
    Text,

    /// <summary>The link glyph over the address's host.</summary>
    Link,

    /// <summary>The folder glyph.</summary>
    Folder,

    /// <summary>A sketch of a page.</summary>
    Document,

    /// <summary>A sketch of a sheet of cells.</summary>
    Sheet,
}

/// <summary>
/// What the shelf read about an item for its tile, beyond its name and size: the start of its text and how long it is,
/// a link's address, a picture's size in pixels, how many entries a folder has. Each is null when it was not read.
/// </summary>
public sealed record ShelfPeek(string? Text = null, int? Chars = null, Uri? Link = null, (int W, int H)? Pixels = null, int? Entries = null);

/// <summary>
/// Where the shelf's parts sit, in DIPs. The panel body's origin is its top-left corner under the
/// tab strip; the drop surface is in island space (x = 0 on the island's centre line, y = 0 at its top edge). Also
/// the text each part shows and where it goes.
/// </summary>
public static class ShelfLayout
{
    // Below this width the grid has three columns instead of four.
    public const float NarrowBelow = 640;
    public const float PadX = 16, Top = 14, HeadHeight = 32, HeadGap = 14, Gap = 10, FootGap = 14, FootHeight = 24, PadBottom = 16;

    // A tile: its padding, the preview at its top, then its name and its meta line, inset a little.
    public const float TilePad = 8, Preview = 92, PreviewCorner = 10, TileCorner = 14, PreviewGap = 8, NameLine = 17, MetaGap = 3, MetaLine = 14, TextInset = 4;
    public const float TileHeight = TilePad + Preview + PreviewGap + NameLine + MetaGap + MetaLine + TextInset + TilePad;

    /// <summary>The selected tile's ring, drawn outside the tile.</summary>
    public const float RingWidth = 2;

    // The band in the head row holds the title and the count, or the action buttons of the tile they are for.
    public const float ActionHeight = StateLook.ActionHeight, BandTop = Top + (HeadHeight - ActionHeight) / 2;
    public const float ActionCorner = 10, ActionPadX = 8, ActionIcon = 16, ActionIconGap = 5, ActionGap = 4;
    public const float IconOnlyWidth = 32, RemoveApart = 8;

    // The filter at the head row's right end: a capsule track with the segments inside it, the band kept clear of it.
    public const float SegmentHeight = 26, SegmentPadX = 12, SegmentGap = 2, SegmentInset = 3, FilterApart = 8;

    // The footer: the hint, then each key and what it does, Clear shelf at the right end.
    public const float FootInset = 4, FootApart = 16, ClearPadX = 8;

    // The drop target leaves the mascot its column on the left: its inset, its size and a gap.
    public const float DropLeft = 8 + 26 + 10, DropRight = 14, DropPadY = 12;

    public const string Title = "Shelf";
    public const string Hint = "Drag out into any app";
    public const string Empty = "Drop files, folders, text, images or links here.\nDragging toward the top edge opens this surface.";
    public const string DropSlot = "Drop files, text or links here";
    public const string DropHere = "Drop to shelf";
    public const string ConfirmRemove = "Remove? Click again";
    public const string ClearText = "Clear shelf", ClearAsk = "Clear shelf? Click again";
    public const string ClearId = "clear";

    /// <summary>The keys the footer names, each with what it does to the focused tile.</summary>
    public static readonly (string Keys, string Does)[] FootKeys = [("Ctrl C", "copy"), ("Del", "remove")];

    public static float GridTop => Top + HeadHeight + HeadGap;

    public static int Columns(Vector2 body) => body.X < NarrowBelow ? 3 : 4;

    public static float TileWidth(Vector2 body) => (body.X - 2 * PadX - (Columns(body) - 1) * Gap) / Columns(body);

    /// <summary>Where the grid ends: above the footer.</summary>
    public static float GridBottom(Vector2 body) => body.Y - PadBottom - FootHeight - FootGap;

    /// <summary>Whole rows of tiles that fit the body; at least one.</summary>
    public static int Rows(Vector2 body) => Math.Max(1, (int)((GridBottom(body) - GridTop + Gap) / (TileHeight + Gap)));

    /// <summary>The tile in the given place of the visible page, counted left to right, top to bottom.</summary>
    public static RectangleF Tile(int slot, Vector2 body)
    {
        float w = TileWidth(body);
        int columns = Columns(body);
        return new RectangleF(PadX + slot % columns * (w + Gap), GridTop + slot / columns * (TileHeight + Gap), w, TileHeight);
    }

    /// <summary>How many tiles show from <paramref name="first"/> on.</summary>
    public static int Visible(Vector2 body, int first, int count) => Math.Clamp(count - first, 0, Rows(body) * Columns(body));

    /// <summary>The slot of the dashed tile that asks for drops: after the last item, while the page has room for it; else null.</summary>
    public static int? DropSlotAt(Vector2 body, int first, int count) => count > 0 && count - first < Rows(body) * Columns(body) ? count - first : null;

    /// <summary>The preview square at a tile's top, in the tile's own DIPs.</summary>
    public static RectangleF PreviewIn(SizeF tile) => new(TilePad, TilePad, tile.Width - 2 * TilePad, Preview);

    public static string TileId(ShelfItem item) => TileId(item.Id);

    public static string TileId(long id) => FormattableString.Invariant($"tile:{id}");

    public static string ActionId(ShelfAction action) => $"act:{action}";

    public static string FilterId(ShelfFilter filter) => $"filter:{filter}";

    /// <summary>
    /// The shelf's clickable parts in Tab order: the visible tiles (group 1, draggable, the full name and folder as the
    /// tip), then, while <paramref name="owner"/> is on the page, the band's actions for it (group 2; an icon-only one
    /// carries its name as the tip, and each its key), then the filter's segments (group 3, the current one checked)
    /// and Clear shelf while there is anything to clear. An action that none of <paramref name="acting"/> allows is
    /// disabled. <paramref name="items"/> are the items the filter lets through.
    /// </summary>
    public static IReadOnlyList<Target> Targets(Vector2 body, int first, IReadOnlyList<ShelfItem> items, ShelfItem? owner, IReadOnlyList<ShelfItem> acting, bool confirming,
        ShelfFilter filter = ShelfFilter.All, bool clearing = false, bool any = false)
    {
        var targets = new List<Target>();
        int visible = Visible(body, first, items.Count);
        for (int slot = 0; slot < visible; slot++)
        {
            var item = items[first + slot];
            targets.Add(new(TileId(item), Tile(slot, body), TileCorner, TargetKind.Tile, Tip: Tip(item), Group: 1, Draggable: true));
        }

        if (owner is not null && targets.Any(t => t.Id == TileId(owner)))
        {
            foreach (var slot in Actions(body, confirming))
            {
                bool allowed = acting.Any(i => ShelfStore.Allows(slot.Action, i));
                targets.Add(new(ActionId(slot.Action), slot.Box, ActionCorner, TargetKind.Button, allowed, slot.Label is null ? Name(slot.Action) : null, Group: 2,
                    Key: ActionKey(slot.Action)));
            }
        }

        foreach (var segment in Filters(body, filter))
        {
            targets.Add(new(FilterId(segment.Filter), segment.Box, segment.Box.Height / 2, TargetKind.Button, Tip: $"Show {FilterName(segment.Filter).ToLowerInvariant()}", Group: 3,
                Checked: segment.Filter == filter));
        }

        if (any || items.Count > 0)
        {
            targets.Add(new(ClearId, Foot(body, clearing).Clear, 8, TargetKind.Button));
        }

        return targets;
    }

    /// <summary>One action button in the band: its box, its icon's square, and its name or null when it shows its icon only.</summary>
    public readonly record struct ActionSlot(ShelfAction Action, RectangleF Box, RectangleF Icon, TextSlot? Label);

    /// <summary>
    /// The action buttons in the band, in the enum's order from the left padding, with Remove on its own at the band's
    /// right end, before the filter, so its question grows away from the others. Every button shows its icon and name
    /// when all the names fit with Remove asking; otherwise every one is icon only, its name in its tooltip, except Remove
    /// while it asks. So the others never move when Remove asks.
    /// </summary>
    public static IReadOnlyList<ActionSlot> Actions(Vector2 body, bool confirming)
    {
        var actions = Enum.GetValues<ShelfAction>();
        var row = BandRow(body);
        float left = row.Left, right = row.Right;
        float Labelled(string name) => 2 * ActionPadX + ActionIcon + ActionIconGap + MathF.Ceiling(TextSlot.Measure(name, ActionStyle, Math.Max(1, right - left)));
        float widest = actions.Sum(a => Labelled(a == ShelfAction.Remove ? ConfirmRemove : Name(a))) + (actions.Length - 2) * ActionGap + RemoveApart;
        bool names = widest <= right - left;
        var slots = new List<ActionSlot>();
        foreach (var action in actions)
        {
            bool remove = action == ShelfAction.Remove, label = names || (remove && confirming);
            string name = remove && confirming ? ConfirmRemove : Name(action);
            float w = label ? Labelled(name) : IconOnlyWidth, x = left;
            if (remove)
            {
                // Never over Share: on a band too narrow for it the question is cut short.
                x = Math.Max(right - w, left - ActionGap + RemoveApart);
                w = Math.Max(Math.Min(w, right - x), IconOnlyWidth);
            }

            var box = new RectangleF(x, BandTop, w, ActionHeight);
            var icon = new RectangleF(label ? x + ActionPadX : x + (w - ActionIcon) / 2, BandTop + (ActionHeight - ActionIcon) / 2, ActionIcon, ActionIcon);
            float textX = icon.Right + ActionIconGap;
            TextSlot? text = label ? new(name, ActionStyle, new(textX, BandTop, Math.Max(0, box.Right - ActionPadX - textX), ActionHeight)) : null;
            slots.Add(new(action, box, icon, text));
            left = box.Right + ActionGap;
        }

        return slots;
    }

    /// <summary>One segment of the filter: its box, inside the track, and its word.</summary>
    public readonly record struct FilterSlot(ShelfFilter Filter, RectangleF Box, TextSlot Label);

    /// <summary>
    /// The filter's capsule track at the head row's right end, and its segments in the enum's order. Each segment is as
    /// wide as its word in the current segment's weight and the padding, so a segment never changes width as the
    /// current one moves.
    /// </summary>
    public static (RectangleF Track, IReadOnlyList<FilterSlot> Segments) Filter(Vector2 body, ShelfFilter current)
    {
        var filters = Enum.GetValues<ShelfFilter>();
        var widths = filters.Select(f => MathF.Ceiling(TextSlot.Measure(FilterName(f), FilterOn, body.X)) + 2 * SegmentPadX).ToList();
        float trackW = widths.Sum() + (filters.Length - 1) * SegmentGap + 2 * SegmentInset, trackH = SegmentHeight + 2 * SegmentInset;
        var track = new RectangleF(body.X - PadX - trackW, Top + (HeadHeight - trackH) / 2, trackW, trackH);
        float x = track.X + SegmentInset;
        var segments = new List<FilterSlot>();
        for (int i = 0; i < filters.Length; i++)
        {
            var box = new RectangleF(x, track.Y + SegmentInset, widths[i], SegmentHeight);
            segments.Add(new(filters[i], box, new(FilterName(filters[i]), filters[i] == current ? FilterOn : FilterOff, box)));
            x += widths[i] + SegmentGap;
        }

        return (track, segments);
    }

    public static IReadOnlyList<FilterSlot> Filters(Vector2 body, ShelfFilter current) => Filter(body, current).Segments;

    public static string FilterName(ShelfFilter filter) => filter switch
    {
        ShelfFilter.Files => "Files",
        ShelfFilter.Text => "Text",
        ShelfFilter.Links => "Links",
        _ => "All",
    };

    /// <summary>Whether the filter lets the item through: links are .url shortcuts, text is text the shelf keeps itself, files are the rest.</summary>
    public static bool Matches(ShelfItem item, ShelfFilter filter) => filter switch
    {
        ShelfFilter.Links => IsLink(item),
        ShelfFilter.Text => ShelfFormats.IsText(item),
        ShelfFilter.Files => !IsLink(item) && !ShelfFormats.IsText(item),
        _ => true,
    };

    public static bool IsLink(ShelfItem item) => Path.GetExtension(item.Name).Equals(".url", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// How long the band waits before it goes once the pointer left, and how long the pointer must rest on another tile
    /// before the band follows it there.
    /// </summary>
    public const int BandGraceMs = 300;

    /// <summary>
    /// How long the band waits once the pointer left the targets for empty grid on its way to the buttons
    /// (<see cref="Aiming"/>): long enough to cross the panel, once. A pointer that stops short of them loses the band.
    /// </summary>
    public const int BandAimMs = 800;

    /// <summary>
    /// What the hovered target (or the focused one) changing does to the band showing <paramref name="shown"/>'s actions:
    /// a first tile shows at once; another tile takes over after <see cref="BandGraceMs"/> (<see cref="Step"/> starts that
    /// wait over while the pointer moves on the tile toward the band, so only a rest there or a move another way takes the
    /// band); the band's own buttons or the tile it is for keep it, and cancel what was waiting; nothing takes it after the
    /// wait (<see cref="Wait"/>), unless a target is hovered by then. The owner is an item id.
    /// </summary>
    public static (BandChange Change, long? Owner) Hover(long? shown, string? hovered)
    {
        if (hovered?.StartsWith("tile:", StringComparison.Ordinal) == true && long.TryParse(hovered.AsSpan(5), System.Globalization.CultureInfo.InvariantCulture, out long id))
        {
            return shown is null ? (BandChange.Now, id) : shown == id ? (BandChange.Keep, id) : (BandChange.Later, id);
        }

        return hovered is null ? (BandChange.Later, null) : (BandChange.Keep, shown);
    }

    /// <summary>How long a Later change waits: one aim's worth while the pointer heads for the buttons, else the grace.</summary>
    public static int Wait(bool aiming) => aiming ? BandAimMs : BandGraceMs;

    /// <summary>The band's row, in body DIPs: where its buttons sit, with the gaps between them, up to the filter.</summary>
    public static RectangleF BandRow(Vector2 body) => RectangleF.FromLTRB(PadX, BandTop, Math.Max(PadX, Filter(body, ShelfFilter.All).Track.Left - FilterApart), BandTop + ActionHeight);

    /// <summary>
    /// Whether a pointer at <paramref name="at"/> is on its way to the band's buttons from <paramref name="from"/>, where it
    /// was last seen on a tile or a button: on the band's row, or inside the triangle from <paramref name="from"/> to the
    /// row's two bottom corners. A pointer heading straight for a button stays inside it, across tiles and empty grid alike.
    /// </summary>
    public static bool Aiming(Vector2 from, Vector2 at, RectangleF band)
    {
        if (band.Contains(at.X, at.Y))
        {
            return true;
        }

        Vector2 left = new(band.Left, band.Bottom), right = new(band.Right, band.Bottom);
        static float Side(Vector2 a, Vector2 b, Vector2 p) => ((b.X - a.X) * (p.Y - a.Y)) - ((b.Y - a.Y) * (p.X - a.X));
        float d1 = Side(from, left, at), d2 = Side(left, right, at), d3 = Side(right, from, at);
        bool negative = d1 < 0 || d2 < 0 || d3 < 0, positive = d1 > 0 || d2 > 0 || d3 > 0;
        return !(negative && positive);
    }

    /// <summary>
    /// The band after the pointer, last seen on a target at <paramref name="last"/>, was on <paramref name="was"/> and
    /// is on <paramref name="id"/> (null: no target; the same id: a move on it) at <paramref name="at"/>, at
    /// <paramref name="now"/> ms. A change due by then is made first, unless it takes the band away while a target is
    /// hovered. A new target asks <see cref="Hover"/>: Later waits (<see cref="Wait"/>, the aim's when the pointer left the
    /// targets heading for the band), anything else cancels what waited. A move on the tile the band waits to go to
    /// starts the wait over when it heads for the band from <paramref name="last"/>; sideways or down it does not, so
    /// the band goes there after the grace. Gives the item the band is for, the one waiting to take it and when it is
    /// due, in ms.
    /// </summary>
    public static (long? Shown, long? Pending, long? Due) Step(long? shown, long? pending, long? due, long now, string? was, string? id, Vector2? last, Vector2 at, Vector2 body)
    {
        if (due <= now)
        {
            (shown, pending, due) = (pending is null && was is not null ? shown : pending, null, null);
        }

        if (id != was)
        {
            var (change, owner) = Hover(shown, id);
            bool aiming = id is null && last is { } from && Aiming(from, at, BandRow(body));
            return change switch
            {
                BandChange.Now => (owner, null, null),
                BandChange.Later => (shown, owner, now + Wait(aiming)),
                _ => (shown, null, null),
            };
        }

        bool restart = due is not null && pending is long next && id == TileId(next) && last is { } prev && Aiming(prev, at, BandRow(body));
        return (shown, pending, restart ? now + BandGraceMs : due);
    }

    /// <summary>A tile's tooltip: the item's full name, and the folder it is in or that the shelf keeps it itself.</summary>
    public static string Tip(ShelfItem item) =>
        item.Name + "\n" + (item.Kind == ShelfKind.Reference ? Path.GetDirectoryName(item.Path) ?? item.Path : "Kept on the shelf");

    /// <summary>
    /// The first visible item after the wheel moved by whole notches (120 a notch, positive away from the user, which
    /// scrolls up): one row a notch, never before the first row or past the last full page.
    /// </summary>
    public static int Scroll(int first, int notches, int count, Vector2 body)
    {
        int columns = Columns(body), rows = (count + columns - 1) / columns;
        int row = Math.Clamp(first / columns - notches, 0, Math.Max(0, rows - Rows(body)));
        return row * columns;
    }

    /// <summary>The drop surface's target in island space, for a shape this wide and deep.</summary>
    public static RectangleF DropTarget(double width, double depth) =>
        new(-(float)width / 2 + DropLeft, DropPadY, (float)width - DropLeft - DropRight, (float)depth - 2 * DropPadY);


    /// <summary>A tile's name and meta line, the head row's count, the empty box's two lines and the drop target's word.</summary>
    public static TextStyle NameStyle => TextStyle.Primary with { Weight = 500 };
    public static TextStyle MetaStyle => new(11, Color: Theme.Ink3, Family: TextStyle.Mono);
    public static TextStyle CountStyle => TextStyle.Primary with { Color = Theme.Ink3 };
    public static TextStyle EmptyStyle => new(13, Color: Theme.Ink3, Align: TextAlign.Center);
    public static TextStyle DropStyle => new(13, 600, Color: Theme.Ink2);
    public static TextStyle ActionStyle => new(12, 500);

    /// <summary>The filter's words: the current segment's, and the others'.</summary>
    public static TextStyle FilterOn => new(12, 500, Align: TextAlign.Center);
    public static TextStyle FilterOff => FilterOn with { Weight = 400, Color = Theme.Ink2 };

    /// <summary>A text item's first lines in its preview, the host under a link's glyph, and the dashed tile's words.</summary>
    public static TextStyle PeekStyle => new(12, Color: Theme.Ink2, LineHeight: 17.4f);
    public static TextStyle HostStyle => new(12, Color: Theme.Ink2);
    public static TextStyle DropSlotStyle => new(12, Color: Theme.Ink2, Align: TextAlign.Center);

    /// <summary>The footer's hint and the words after its keycaps; Clear shelf at rest and while it asks.</summary>
    public static TextStyle FootStyle => TextStyle.Secondary with { Color = Theme.Ink3 };
    public static TextStyle ClearStyle => TextStyle.Secondary with { Align = TextAlign.Center };
    public static TextStyle ClearAskStyle => ClearStyle with { Color = Theme.Warn };

    /// <summary>The head row in body DIPs: the title, and the count after it up to the band's end.</summary>
    public static IReadOnlyList<TextSlot> HeadText(Vector2 body, int count)
    {
        float after = PadX + MathF.Ceiling(TextSlot.Measure(Title, TextStyle.PageTitle, body.X)) + 10;
        var row = new RectangleF(PadX, Top, body.X - 2 * PadX, HeadHeight);
        return
        [
            new(Title, TextStyle.PageTitle, row),
            new(Head(count), CountStyle, RectangleF.FromLTRB(after, Top, Math.Max(after, BandRow(body).Right), Top + HeadHeight)),
        ];
    }

    /// <summary>The empty shelf's two lines, centred one under the other in the dashed box, in the box's own DIPs.</summary>
    public static IReadOnlyList<TextSlot> EmptyText(SizeF box)
    {
        const float line = 19.5f;
        var lines = Empty.Split('\n');
        float top = (box.Height - lines.Length * line) / 2;
        return [.. lines.Select((text, i) => new TextSlot(text, EmptyStyle, new(20, top + i * line, box.Width - 40, line)))];
    }

    /// <summary>A tile's name under its preview and its meta line under that, in the tile's own DIPs.</summary>
    public static IReadOnlyList<TextSlot> TileText(ShelfItem item, SizeF tile, ShelfPeek? peek = null)
    {
        float x = TilePad + TextInset, y = TilePad + Preview + PreviewGap, w = tile.Width - 2 * x;
        return
        [
            new(DisplayName(item, peek), NameStyle, new(x, y, w, NameLine)),
            new(Meta(item, peek), item.Missing ? MetaStyle with { Color = Theme.Warn } : MetaStyle, new(x, y + NameLine + MetaGap, w, MetaLine)),
        ];
    }

    /// <summary>What a tile's preview shows for the item.</summary>
    public static ShelfPreview PreviewOf(ShelfItem item) => item.IsFolder ? ShelfPreview.Folder
        : IsLink(item) ? ShelfPreview.Link
        : ShelfFormats.IsText(item) ? ShelfPreview.Text
        : Path.GetExtension(item.Name).ToLowerInvariant() switch
        {
            ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" => ShelfPreview.Image,
            ".xlsx" or ".xls" or ".csv" or ".ods" => ShelfPreview.Sheet,
            _ => ShelfPreview.Document,
        };

    /// <summary>A text item's first lines, from the preview's top-left corner inset, as many as fit, in the preview's own DIPs.</summary>
    public static (TextRun Run, Vector2 At) PeekText(string text, SizeF preview)
    {
        const float padX = 12, padY = 10;
        int lines = Math.Max(1, (int)((preview.Height - 2 * padY) / PeekStyle.LineHeight));
        return (new TextRun(text, PeekStyle, preview.Width - 2 * padX, lines), new(padX, padY));
    }

    /// <summary>A link's glyph and its host under it, at the preview's bottom left, in the preview's own DIPs.</summary>
    public static (RectangleF Glyph, TextSlot Host) LinkPreview(string host, SizeF preview)
    {
        const float pad = 12, glyph = 18, line = 16, gap = 4;
        float y = preview.Height - pad - line;
        return (new(pad, y - gap - glyph, glyph, glyph), new(host, HostStyle, new(pad, y, preview.Width - 2 * pad, line)));
    }

    /// <summary>The folder glyph, centred in the preview.</summary>
    public static RectangleF FolderGlyph(SizeF preview) => new((preview.Width - 34) / 2, (preview.Height - 34) / 2, 34, 34);

    /// <summary>A page's sketch: its heading bar, then four lines, from the top inset, in the preview's own DIPs.</summary>
    public static IReadOnlyList<(RectangleF Bar, bool Heading)> PageSketch(SizeF preview)
    {
        const float pad = 10, gap = 5;
        float w = preview.Width - 2 * pad, y = pad;
        var bars = new List<(RectangleF, bool)> { (new(pad, y, w * 0.6f, 6), true) };
        y += 6 + gap;
        foreach (float part in (ReadOnlySpan<float>)[0.9f, 0.8f, 0.85f, 0.5f])
        {
            bars.Add((new(pad, y, w * part, 4), false));
            y += 4 + gap;
        }

        return bars;
    }

    /// <summary>A sheet's sketch: three rows of three cells, the first row the header, in the preview's own DIPs.</summary>
    public static IReadOnlyList<(RectangleF Cell, bool Header)> SheetSketch(SizeF preview)
    {
        const float pad = 12, gap = 2;
        float w = (preview.Width - 2 * pad - 2 * gap) / 3, h = (preview.Height - 2 * pad - 2 * gap) / 3;
        return [.. Enumerable.Range(0, 9).Select(i => (new RectangleF(pad + i % 3 * (w + gap), pad + i / 3 * (h + gap), w, h), i < 3))];
    }

    /// <summary>The dashed tile's plus and its words under it, centred together, in the tile's own DIPs.</summary>
    public static (RectangleF Plus, TextRun Words, Vector2 At) DropSlotText(SizeF tile)
    {
        const float plus = 22, gap = 8, line = 16, pad = 12;
        float width = tile.Width - 2 * pad;
        int lines = TextSlot.Measure(DropSlot, DropSlotStyle, 1000) <= width ? 1 : 2;
        float top = (tile.Height - plus - gap - lines * line) / 2;
        return (new((tile.Width - plus) / 2, top, plus, plus), new TextRun(DropSlot, DropSlotStyle, width, lines), new(pad, top + plus + gap));
    }

    /// <summary>The footer: the hint, each key's cap and what it does, and Clear shelf at the right end.</summary>
    public readonly record struct FootSlots(TextSlot Hint, IReadOnlyList<(RectangleF Cap, TextSlot Keys, TextSlot Does)> Keys, RectangleF Clear, TextSlot ClearLabel);

    /// <summary>
    /// The footer row in body DIPs. Clear shelf is as wide as its word or question and the padding, at the right end;
    /// the keys follow the hint as far as they fit before Clear shelf at its widest, so they never move as it asks.
    /// </summary>
    public static FootSlots Foot(Vector2 body, bool clearing)
    {
        float y = body.Y - PadBottom - FootHeight, x = PadX + FootInset;
        float Width(string text, TextStyle style) => MathF.Ceiling(TextSlot.Measure(text, style, body.X));
        string clearText = clearing ? ClearAsk : ClearText;
        float clearW = Width(clearText, clearing ? ClearAskStyle : ClearStyle) + 2 * ClearPadX;
        var clear = new RectangleF(body.X - PadX - clearW, y, clearW, FootHeight);
        float stop = body.X - PadX - (Width(ClearAsk, ClearAskStyle) + 2 * ClearPadX) - FootApart;
        float hint = Width(Hint, FootStyle);
        var hintSlot = new TextSlot(Hint, FootStyle, new(x, y, Math.Max(0, Math.Min(hint, stop - x)), FootHeight));
        var keys = new List<(RectangleF, TextSlot, TextSlot)>();
        x += hint + FootApart;
        foreach (var (key, does) in FootKeys)
        {
            float cap = Keycap.Width(key), word = Width(does, FootStyle);
            if (x + cap + Keycap.WordGap + word > stop)
            {
                break;
            }

            var capBox = new RectangleF(x, y + (FootHeight - Keycap.Height) / 2, cap, Keycap.Height);
            keys.Add((capBox, new(key, Keycap.Style, capBox), new(does, FootStyle, new(capBox.Right + Keycap.WordGap, y, word, FootHeight))));
            x += cap + Keycap.WordGap + word + FootApart;
        }

        return new(hintSlot, keys, clear, new(clearText, clearing ? ClearAskStyle : ClearStyle, clear));
    }

    /// <summary>The drop target's folder glyph and its word, centred together in the target, in the target's own DIPs.</summary>
    public static (float GlyphX, TextSlot Label) DropLabel(SizeF target)
    {
        const float glyph = 16, gap = 8;
        float label = MathF.Ceiling(TextSlot.Measure(DropHere, DropStyle, target.Width));
        float x = (target.Width - glyph - gap - label) / 2;
        return (x, new(DropHere, DropStyle, new(x + glyph + gap, target.Height / 2 - 8, label, 16)));
    }

    public static string Head(int count) => FormattableString.Invariant($"{count} item{(count == 1 ? "" : "s")} · kept as references");

    /// <summary>
    /// The tile's name: a link's path (its host when it has none), "Text" for text the shelf keeps itself, else the item's
    /// own name.
    /// </summary>
    public static string DisplayName(ShelfItem item, ShelfPeek? peek = null)
    {
        if (IsLink(item))
        {
            return peek?.Link is { } link ? (link.AbsolutePath.Length > 1 ? link.AbsolutePath : link.Host) : Path.GetFileNameWithoutExtension(item.Name);
        }

        return item.Kind == ShelfKind.Content && ShelfFormats.IsText(item) ? "Text" : item.Name;
    }

    /// <summary>
    /// The tile's meta line: "File moved or deleted"; "Folder · 14 items"; "Link"; "84 characters"; a picture's pixels
    /// and its size; or its kind and size, as "PDF · 39 KB".
    /// </summary>
    public static string Meta(ShelfItem item, ShelfPeek? peek = null)
    {
        if (item.Missing)
        {
            return "File moved or deleted";
        }

        if (item.IsFolder)
        {
            return peek?.Entries is int n ? FormattableString.Invariant($"Folder · {n} item{(n == 1 ? "" : "s")}") : "Folder";
        }

        if (IsLink(item))
        {
            return "Link";
        }

        if (ShelfFormats.IsText(item) && peek?.Chars is int chars)
        {
            return FormattableString.Invariant($"{chars} character{(chars == 1 ? "" : "s")}");
        }

        string size = Size(item.Size ?? 0);
        return peek?.Pixels is var (w, h) ? FormattableString.Invariant($"{w} × {h} · {size}") : $"{KindName(item)} · {size}";
    }

    /// <summary>A file's kind in a word: PDF, Sheet, Document, Text, the extension in capitals, or File without one.</summary>
    public static string KindName(ShelfItem item) => Path.GetExtension(item.Name).ToLowerInvariant() switch
    {
        ".pdf" => "PDF",
        ".xlsx" or ".xls" or ".csv" or ".ods" => "Sheet",
        ".doc" or ".docx" or ".odt" or ".rtf" => "Document",
        ".txt" or ".md" => "Text",
        "" => "File",
        var ext => ext[1..].ToUpperInvariant(),
    };

    public static string Size(long bytes) =>
        bytes < 1024 * 1024 ? FormattableString.Invariant($"{Math.Max(1, (bytes + 512) / 1024)} KB")
        : bytes < 1L << 30 ? FormattableString.Invariant($"{bytes / 1048576.0:0.0} MB")
        : FormattableString.Invariant($"{bytes / 1073741824.0:0.0} GB");

    public static string Name(ShelfAction action) => action switch
    {
        ShelfAction.Open => "Open",
        ShelfAction.Reveal => "Show in folder",
        ShelfAction.CopyPath => "Copy path",
        ShelfAction.Share => "Share",
        _ => "Remove",
    };

    /// <summary>The key that does an action on a focused tile, for the button's tooltip while the island has the keys; null for Share.</summary>
    public static string? ActionKey(ShelfAction action) => action switch
    {
        ShelfAction.Open => "Enter",
        ShelfAction.Reveal => "Ctrl+Shift+E",
        ShelfAction.CopyPath => "Ctrl+C",
        ShelfAction.Remove => "Delete",
        _ => null,
    };

    /// <summary>
    /// The first visible item after an arrow from <paramref name="index"/> by <paramref name="rows"/> rows (Down +1, Up
    /// -1): a row further when the row it heads for has items and is off the page, so the arrow finds a tile there;
    /// unchanged otherwise.
    /// </summary>
    public static int ScrollFor(int first, int index, int rows, int count, Vector2 body)
    {
        int columns = Columns(body), row = (index / columns) + rows, firstRow = first / columns;
        bool off = row < firstRow || row >= firstRow + Rows(body);
        return off && row >= 0 && row <= (count - 1) / columns ? Scroll(first, -rows, count, body) : first;
    }

    /// <summary>
    /// The first visible item after Left (-1) or Right (+1) from <paramref name="index"/>, which go on to the row before
    /// or after at the ends of a row: a row further when that row is off the page, so the arrow finds the tile there.
    /// </summary>
    public static int ScrollSideways(int first, int index, int step, int count, Vector2 body) =>
        ScrollFor(first, index, ((index + step) / Columns(body)) - (index / Columns(body)), count, body);

    /// <summary>One stroke of an action's icon, in its 16 DIP square: a line from A to B, or with a corner, an outlined box from A to B.</summary>
    public readonly record struct GlyphStroke(Vector2 A, Vector2 B, float Corner = -1);

    public const float GlyphStrokeWidth = 1.3f;

    private static readonly GlyphStroke[] OpenGlyph = [new(new(2, 5), new(11, 14), 1.5f), new(new(7, 9), new(13, 3)), new(new(9, 3), new(13, 3)), new(new(13, 3), new(13, 7))];
    private static readonly GlyphStroke[] FolderGlyphStrokes = [new(new(2, 3), new(8, 6), 1), new(new(2, 5), new(14, 14), 1.5f)];
    private static readonly GlyphStroke[] CopyGlyph = [new(new(2, 5), new(10, 14), 1.5f), new(new(5, 5), new(5, 2)), new(new(5, 2), new(13, 2)), new(new(13, 2), new(13, 11)), new(new(13, 11), new(10, 11))];
    private static readonly GlyphStroke[] ShareGlyph = [new(new(8, 2), new(8, 10)), new(new(5, 5), new(8, 2)), new(new(8, 2), new(11, 5)), new(new(3, 8), new(3, 13)), new(new(3, 13), new(13, 13)), new(new(13, 13), new(13, 8))];
    private static readonly GlyphStroke[] RemoveGlyph = [new(new(2, 4), new(14, 4)), new(new(6, 2), new(10, 2)), new(new(4, 5), new(12, 14), 1.5f)];

    /// <summary>An action's line icon: Open an arrow out of a box, Show in folder a folder, Copy path two sheets, Share an arrow out of a tray, Remove a bin.</summary>
    public static IReadOnlyList<GlyphStroke> Glyph(ShelfAction action) => action switch
    {
        ShelfAction.Open => OpenGlyph,
        ShelfAction.Reveal => FolderGlyphStrokes,
        ShelfAction.CopyPath => CopyGlyph,
        ShelfAction.Share => ShareGlyph,
        _ => RemoveGlyph,
    };

    /// <summary>A square's colour by kind of file, for the drop result's tiles.</summary>
    public static uint KindColor(ShelfItem item) => item.IsFolder ? Theme.Folder : Path.GetExtension(item.Name).ToLowerInvariant() switch
    {
        ".pdf" => Theme.KindPdf,
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".csv" => Theme.KindPicture,
        ".doc" or ".docx" => Theme.KindDocument,
        ".url" => Theme.KindLink,
        _ => Theme.KindOther,
    };
}

/// <summary>
/// Remove's question on the shelf's band. The first click asks about the item the band is for; a second within
/// <see cref="AskMs"/> removes it. A second click sooner than the double-click time is the first one doubled, and a
/// key held down repeats; neither counts, as neither is a second decision. The question goes when the band moves to
/// another item or its time is up. Times are milliseconds on one clock.
/// </summary>
public sealed class RemoveQuestion
{
    public const int AskMs = 3000;
    private long askedAt;

    /// <summary>The item it asks about, or null; the caller drops it when its time is up.</summary>
    public long? Item { get; private set; }

    public RemoveStep Click(long item, long now, long doubleClickMs, bool repeat = false)
    {
        if (repeat)
        {
            return RemoveStep.Ignored;
        }

        if (Asks(item, now))
        {
            if (now - askedAt < doubleClickMs)
            {
                return RemoveStep.Ignored;
            }

            Item = null;
            return RemoveStep.Remove;
        }

        (Item, askedAt) = (item, now);
        return RemoveStep.Ask;
    }

    /// <summary>Whether it asks about <paramref name="item"/> at <paramref name="now"/>.</summary>
    public bool Asks(long? item, long now) => item is not null && Item == item && now - askedAt < AskMs;

    public void Drop() => Item = null;
}

/// <summary>What a click on Remove did; see <see cref="RemoveQuestion.Click"/>.</summary>
public enum RemoveStep
{
    Ask,
    Remove,
    Ignored,
}

/// <summary>What a change of the hovered target does to the shelf's band; see <see cref="ShelfLayout.Hover"/>.</summary>
public enum BandChange
{
    Keep,
    Now,
    Later,
}
