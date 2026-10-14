using System.Drawing;
using System.Numerics;

namespace Tomte.Core;

/// <summary>
/// Where the shelf's parts sit, in DIPs. The panel body's origin is its top-left corner under the
/// tab strip; the drop surface is in island space (x = 0 on the island's centre line, y = 0 at its top edge). Also
/// the strings each part would show: there is no text renderer on the island yet, so the views draw a neutral bar
/// where each one goes and these are what a renderer will draw.
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
