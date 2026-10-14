using System.Drawing;
using System.Numerics;
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
    public const float PadX = 14, PadY = 12, HeadHeight = 16, HeadGap = 10, Gap = 8, TileHeight = 80;
    public const float TilePad = 10, Icon = 30, Button = 22, ButtonGap = 2, ButtonInset = 6;

    // The drop target leaves the mascot its column on the left: its inset, its size and a gap.
    public const float DropLeft = 8 + 26 + 10, DropRight = 14, DropPadY = 12;

    public const string Title = "Shelf";
    public const string Hint = "Drag out to any app";
    public const string Empty = "Drop files, folders, text, images or links here.\nDragging toward the top edge opens this surface.";
    public const string DropHere = "Drop to shelf";

    public static float GridTop => PadY + HeadHeight + HeadGap;

    public static float TileWidth(float bodyWidth) => (bodyWidth - 2 * PadX - (Columns - 1) * Gap) / Columns;

    /// <summary>Whole rows of tiles that fit the body; at least one.</summary>
    public static int Rows(Vector2 body) => Math.Max(1, (int)((body.Y - GridTop - PadY + Gap) / (TileHeight + Gap)));

    /// <summary>The tile in the given place of the visible page, counted left to right, top to bottom.</summary>
    public static RectangleF Tile(int slot, Vector2 body)
    {
        float w = TileWidth(body.X);
        return new RectangleF(PadX + slot % Columns * (w + Gap), GridTop + slot / Columns * (TileHeight + Gap), w, TileHeight);
    }

    /// <summary>An action's button on a selected tile: a row at its top right, in the enum's order.</summary>
    public static RectangleF ActionButton(ShelfAction action, RectangleF tile)
    {
        const int count = 5;
        int i = (int)action;
        float x = tile.Right - ButtonInset - (count - i) * Button - (count - 1 - i) * ButtonGap;
        return new RectangleF(x, tile.Top + ButtonInset, Button, Button);
    }

    /// <summary>What a press lands on: an item's index (or -1) and, on a selected tile, one of its action buttons.</summary>
    public static (int Index, ShelfAction? Action) At(Vector2 point, Vector2 body, int first, int count, Func<int, bool> selected)
    {
        int visible = Rows(body) * Columns;
        for (int slot = 0; slot < visible && first + slot < count; slot++)
        {
            var tile = Tile(slot, body);
            if (!tile.Contains(point.X, point.Y))
            {
                continue;
            }

            int index = first + slot;
            if (selected(index))
            {
                foreach (var action in Enum.GetValues<ShelfAction>())
                {
                    if (ActionButton(action, tile).Contains(point.X, point.Y))
                    {
                        return (index, action);
                    }
                }
            }

            return (index, null);
        }

        return (-1, null);
    }

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
    public static readonly TextStyle EmptyStyle = new(13, Color: TextStyle.TertiaryColor, Align: TextAlign.Center);
    public static readonly TextStyle DropStyle = new(13, 600, Color: TextStyle.SecondaryColor);

    /// <summary>The head row in body DIPs: the title, the count beside it, and the hint at the right end.</summary>
    public static IReadOnlyList<TextSlot> HeadText(Vector2 body, int count)
    {
        float after = PadX + TextSlot.Measure(Title, TextStyle.Title, body.X) + 10;
        float hint = MathF.Ceiling(TextSlot.Measure(Hint, TextStyle.Secondary, body.X));
        float hintLeft = body.X - PadX - hint;
        return
        [
            new(Title, TextStyle.Title, new(PadX, PadY, body.X - 2 * PadX, HeadHeight)),
            new(Head(count), TextStyle.Secondary, new(after, PadY, Math.Max(0, hintLeft - 10 - after), HeadHeight)),
            new(Hint, TextStyle.Secondary with { Align = TextAlign.Trailing }, new(hintLeft, PadY, hint, HeadHeight)),
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
        new(Meta(item), item.Missing ? MetaStyle with { Color = TextStyle.WarnColor } : MetaStyle, new(TilePad, TilePad + Icon + 24, tile.Width - 2 * TilePad, 12)),
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
        ShelfAction.Reveal => "Reveal in Explorer",
        ShelfAction.CopyPath => "Copy path",
        ShelfAction.Share => "Share",
        _ => "Remove",
    };

    /// <summary>The icon square's colour (RRGGBB) by kind of file.</summary>
    public static uint KindColor(ShelfItem item) => item.IsFolder ? 0xD9A441u : Path.GetExtension(item.Name).ToLowerInvariant() switch
    {
        ".pdf" => 0xC0392Bu,
        ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" or ".csv" => 0x2E8B57,
        ".doc" or ".docx" => 0x2C6FBF,
        ".url" => 0x5A4FCF,
        _ => 0x5D6D7E,
    };
}
