using System.Drawing;
using System.Numerics;
using Tomte.Core.Interaction;
using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>
/// Where the shelf's parts sit, in DIPs. The panel body's origin is its top-left corner under the
/// tab strip; the drop surface is in island space (x = 0 on the island's centre line, y = 0 at its top edge). Also
/// the text each part shows and where it goes.
/// </summary>
public static class ShelfLayout
{
    public const int Columns = 4;
    public const float PadX = 14, PadBottom = 8, HeadHeight = 16, Gap = 8, TileHeight = 80;
    public const float TilePad = 10, Icon = 30, TileCorner = 8;

    // The band over the grid holds the head row, or the action buttons of the tile they are for.
    public const float BandTop = 6, ActionHeight = StateLook.ActionHeight, ActionCorner = 8, ActionPadX = 8, ActionIcon = 16, ActionIconGap = 6, ActionGap = 4;
    public const float IconOnlyWidth = 32, RemoveApart = 12;

    // The drop target leaves the mascot its column on the left: its inset, its size and a gap.
    public const float DropLeft = 8 + 26 + 10, DropRight = 14, DropPadY = 12;

    public const string Title = "Shelf";
    public const string Hint = "Drag out to any app";
    public const string Empty = "Drop files, folders, text, images or links here.\nDragging toward the top edge opens this surface.";
    public const string DropHere = "Drop to shelf";
    public const string ConfirmRemove = "Remove? Click again";

    public static float GridTop => BandTop + ActionHeight + Gap;

    public static float TileWidth(float bodyWidth) => (bodyWidth - 2 * PadX - (Columns - 1) * Gap) / Columns;

    /// <summary>Whole rows of tiles that fit the body; at least one.</summary>
    public static int Rows(Vector2 body) => Math.Max(1, (int)((body.Y - GridTop - PadBottom + Gap) / (TileHeight + Gap)));

    /// <summary>The tile in the given place of the visible page, counted left to right, top to bottom.</summary>
    public static RectangleF Tile(int slot, Vector2 body)
    {
        float w = TileWidth(body.X);
        return new RectangleF(PadX + slot % Columns * (w + Gap), GridTop + slot / Columns * (TileHeight + Gap), w, TileHeight);
    }

    /// <summary>How many tiles show from <paramref name="first"/> on.</summary>
    public static int Visible(Vector2 body, int first, int count) => Math.Clamp(count - first, 0, Rows(body) * Columns);

    public static string TileId(ShelfItem item) => TileId(item.Id);

    public static string TileId(long id) => FormattableString.Invariant($"tile:{id}");

    public static string ActionId(ShelfAction action) => $"act:{action}";

    /// <summary>
    /// The shelf's clickable parts in Tab order: the visible tiles (group 1, draggable, the full name and folder as the
    /// tip), then, while <paramref name="owner"/> is on the page, the band's actions for it (group 2; an icon-only one
    /// carries its name as the tip, and each its key). An action that none of <paramref name="acting"/> allows is
    /// disabled.
    /// </summary>
    public static IReadOnlyList<Target> Targets(Vector2 body, int first, IReadOnlyList<ShelfItem> items, ShelfItem? owner, IReadOnlyList<ShelfItem> acting, bool confirming)
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

        return targets;
    }

    /// <summary>One action button in the band: its box, its icon's square, and its name or null when it shows its icon only.</summary>
    public readonly record struct ActionSlot(ShelfAction Action, RectangleF Box, RectangleF Icon, TextSlot? Label);

    /// <summary>
    /// The action buttons in the band over the grid, in the enum's order from the grid's left edge, with Remove on its
    /// own at the right end so its question grows away from the others. Every button shows its icon and name when all
    /// the names fit with Remove asking; otherwise every one is icon only, its name in its tooltip, except Remove while
    /// it asks. So the others never move when Remove asks.
    /// </summary>
    public static IReadOnlyList<ActionSlot> Actions(Vector2 body, bool confirming)
    {
        var actions = Enum.GetValues<ShelfAction>();
        float left = PadX, right = body.X - PadX;
        float Labelled(string name) => 2 * ActionPadX + ActionIcon + ActionIconGap + MathF.Ceiling(TextSlot.Measure(name, ActionStyle, right - left));
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

    /// <summary>The band's row, in body DIPs: where its buttons sit, with the gaps between them.</summary>
    public static RectangleF BandRow(Vector2 body) => new(PadX, BandTop, body.X - 2 * PadX, ActionHeight);

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
        int rows = (count + Columns - 1) / Columns;
        int row = Math.Clamp(first / Columns - notches, 0, Math.Max(0, rows - Rows(body)));
        return row * Columns;
    }

    /// <summary>The drop surface's target in island space, for a shape this wide and deep.</summary>
    public static RectangleF DropTarget(double width, double depth) =>
        new(-(float)width / 2 + DropLeft, DropPadY, (float)width - DropLeft - DropRight, (float)depth - 2 * DropPadY);

    /// <summary>A tile's name, its meta line, the empty box's two lines and the drop target's word.</summary>
    public static readonly TextStyle NameStyle = new(12.5f, 600);
    public static readonly TextStyle MetaStyle = TextStyle.Small;
    public static readonly TextStyle EmptyStyle = new(13, Color: Theme.Ink3, Align: TextAlign.Center);
    public static readonly TextStyle DropStyle = new(13, 600, Color: Theme.Ink2);
    public static readonly TextStyle ActionStyle = new(12, 500);

    /// <summary>The head row in body DIPs: the title, the count beside it, and the hint at the right end.</summary>
    public static IReadOnlyList<TextSlot> HeadText(Vector2 body, int count)
    {
        float after = PadX + TextSlot.Measure(Title, TextStyle.Title, body.X) + 10;
        float hint = MathF.Ceiling(TextSlot.Measure(Hint, TextStyle.Secondary, body.X));
        float hintLeft = body.X - PadX - hint, y = BandTop + (ActionHeight - HeadHeight) / 2;
        return
        [
            new(Title, TextStyle.Title, new(PadX, y, body.X - 2 * PadX, HeadHeight)),
            new(Head(count), TextStyle.Secondary, new(after, y, Math.Max(0, hintLeft - 10 - after), HeadHeight)),
            new(Hint, TextStyle.Secondary with { Align = TextAlign.Trailing }, new(hintLeft, y, hint, HeadHeight)),
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

    /// <summary>A tile's name under its icon and its size, kind or "File moved or deleted" under that, in the tile's own DIPs.</summary>
    public static IReadOnlyList<TextSlot> TileText(ShelfItem item, SizeF tile) =>
    [
        new(item.Name, NameStyle, new(TilePad, TilePad + Icon + 6, tile.Width - 2 * TilePad, 14)),
        new(Meta(item), item.Missing ? MetaStyle with { Color = Theme.Warn } : MetaStyle, new(TilePad, TilePad + Icon + 24, tile.Width - 2 * TilePad, 12)),
    ];

    /// <summary>The drop target's folder glyph and its word, centred together in the target, in the target's own DIPs.</summary>
    public static (float GlyphX, TextSlot Label) DropLabel(SizeF target)
    {
        const float glyph = 16, gap = 8;
        float label = MathF.Ceiling(TextSlot.Measure(DropHere, DropStyle, target.Width));
        float x = (target.Width - glyph - gap - label) / 2;
        return (x, new(DropHere, DropStyle, new(x + glyph + gap, target.Height / 2 - 8, label, 16)));
    }

    public static string Head(int count) => FormattableString.Invariant($"{count} item{(count == 1 ? "" : "s")} · references by default");

    public static string Meta(ShelfItem item) => item.Missing ? "File moved or deleted" : item.IsFolder ? "Folder" : Size(item.Size ?? 0);

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
        int row = (index / Columns) + rows, firstRow = first / Columns;
        bool off = row < firstRow || row >= firstRow + Rows(body);
        return off && row >= 0 && row <= (count - 1) / Columns ? Scroll(first, -rows, count, body) : first;
    }

    /// <summary>
    /// The first visible item after Left (-1) or Right (+1) from <paramref name="index"/>, which go on to the row before
    /// or after at the ends of a row: a row further when that row is off the page, so the arrow finds the tile there.
    /// </summary>
    public static int ScrollSideways(int first, int index, int step, int count, Vector2 body) =>
        ScrollFor(first, index, ((index + step) / Columns) - (index / Columns), count, body);

    /// <summary>One stroke of an action's icon, in its 16 DIP square: a line from A to B, or with a corner, an outlined box from A to B.</summary>
    public readonly record struct GlyphStroke(Vector2 A, Vector2 B, float Corner = -1);

    public const float GlyphStrokeWidth = 1.3f;

    private static readonly GlyphStroke[] OpenGlyph = [new(new(2, 5), new(11, 14), 1.5f), new(new(7, 9), new(13, 3)), new(new(9, 3), new(13, 3)), new(new(13, 3), new(13, 7))];
    private static readonly GlyphStroke[] FolderGlyph = [new(new(2, 3), new(8, 6), 1), new(new(2, 5), new(14, 14), 1.5f)];
    private static readonly GlyphStroke[] CopyGlyph = [new(new(2, 5), new(10, 14), 1.5f), new(new(5, 5), new(5, 2)), new(new(5, 2), new(13, 2)), new(new(13, 2), new(13, 11)), new(new(13, 11), new(10, 11))];
    private static readonly GlyphStroke[] ShareGlyph = [new(new(8, 2), new(8, 10)), new(new(5, 5), new(8, 2)), new(new(8, 2), new(11, 5)), new(new(3, 8), new(3, 13)), new(new(3, 13), new(13, 13)), new(new(13, 13), new(13, 8))];
    private static readonly GlyphStroke[] RemoveGlyph = [new(new(2, 4), new(14, 4)), new(new(6, 2), new(10, 2)), new(new(4, 5), new(12, 14), 1.5f)];

    /// <summary>An action's line icon: Open an arrow out of a box, Show in folder a folder, Copy path two sheets, Share an arrow out of a tray, Remove a bin.</summary>
    public static IReadOnlyList<GlyphStroke> Glyph(ShelfAction action) => action switch
    {
        ShelfAction.Open => OpenGlyph,
        ShelfAction.Reveal => FolderGlyph,
        ShelfAction.CopyPath => CopyGlyph,
        ShelfAction.Share => ShareGlyph,
        _ => RemoveGlyph,
    };

    /// <summary>The icon square's colour by kind of file.</summary>
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
