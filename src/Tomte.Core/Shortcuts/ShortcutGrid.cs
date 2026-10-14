using System.Drawing;
using System.Numerics;
using Tomte.Core.Text;

namespace Tomte.Core.Shortcuts;

/// <summary>
/// The Shortcuts panel body in DIPs, origin at its top-left corner under the tab strip: a head line, then four
/// columns of 80 DIP tiles with a 34 DIP icon, as many rows as the body holds (two at the panel's 520 x 300). The
/// cells run on past the last row: tile after tile, then the add tile, and the wheel scrolls them by whole rows.
/// Cell and At count from the first row that shows.
/// </summary>
public static class ShortcutGrid
{
    public const float Inset = 14, Top = 12, HeadHeight = 17, HeadGap = 10, Gap = 8, TileHeight = 80, Corner = 8;
    public const float IconSize = 34, IconTop = 12, LabelTop = 54, LabelHeight = 16, LabelInset = 8;
    public const int Columns = 4;

    public static float GridTop => Top + HeadHeight + HeadGap;

    /// <summary>Rows that fit with the top inset kept below the last one too.</summary>
    public static int Rows(Vector2 body) => Math.Max(0, (int)((body.Y - GridTop - Top + Gap) / (TileHeight + Gap)));

    /// <summary>The last first row there is: the add tile, after every tile, ends up in the bottom row.</summary>
    public static int MaxFirstRow(int tiles, Vector2 body) => Math.Max(0, (int)Math.Ceiling((tiles + 1) / (double)Columns) - Rows(body));

    /// <summary>A first row asked for (by the wheel, or kept from before), held to the rows there are.</summary>
    public static int FirstRow(int wanted, int tiles, Vector2 body) => Math.Clamp(wanted, 0, MaxFirstRow(tiles, body));

    /// <summary>The cells that show from this first row: the first one's number, and how many, the add tile counted.</summary>
    public static (int First, int Count) Visible(int firstRow, int tiles, Vector2 body)
    {
        int first = firstRow * Columns;
        return (first, Math.Clamp(tiles + 1 - first, 0, Columns * Rows(body)));
    }

    public static RectangleF Cell(int index, Vector2 body)
    {
        float width = (body.X - 2 * Inset - (Columns - 1) * Gap) / Columns;
        return new(Inset + index % Columns * (width + Gap), GridTop + index / Columns * (TileHeight + Gap), width, TileHeight);
    }

    /// <summary>The cell under a point, among the first <paramref name="cells"/>; null over a gap or past the last.</summary>
    public static int? At(Vector2 point, Vector2 body, int cells)
    {
        for (int i = 0; i < cells; i++)
        {
            if (Cell(i, body).Contains(point.X, point.Y))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>The icon's size in pixels at this scale: what the shell is asked to draw.</summary>
    public static int IconPixels(double scale) => (int)Math.Round(IconSize * scale, MidpointRounding.AwayFromZero);

    /// <summary>
    /// The icon's square in a cell, in body DIPs, placed on whole pixels at this scale so each of the shell's pixels
    /// lands on one screen pixel. <paramref name="bodyTop"/> is how far the body starts below the island's top edge
    /// (the tab strip, 42 DIP, which is 52.5 pixels at 125%); the snap is to the island's pixels. Horizontally the
    /// body starts on a whole pixel at 520 DIP wide, so no offset is needed there.
    /// </summary>
    public static RectangleF Icon(RectangleF cell, double scale, float bodyTop)
    {
        int pixels = IconPixels(scale);
        float x = (float)(Math.Round((cell.X + (cell.Width - IconSize) / 2) * scale) / scale);
        float y = (float)(Math.Round((bodyTop + cell.Y + IconTop) * scale) / scale) - bodyTop;
        return new(x, y, (float)(pixels / scale), (float)(pixels / scale));
    }

    /// <summary>Where a tile's name goes, centred under the icon.</summary>
    public static RectangleF Label(RectangleF cell) => new(cell.X + LabelInset, cell.Y + LabelTop, cell.Width - 2 * LabelInset, LabelHeight);

    /// <summary>The head line: the panel's title, then what a tile can be.</summary>
    public static RectangleF Head(Vector2 body) => new(Inset, Top, body.X - 2 * Inset, HeadHeight);

    /// <summary>The panel's title, then what a tile can be, on the head line.</summary>
    public static IReadOnlyList<TextSlot> HeadText(Vector2 body)
    {
        const string title = "Shortcuts";
        var head = Head(body);
        float after = TextSlot.Measure(title, TextStyle.Title, head.Width) + 10;
        return
        [
            new(title, TextStyle.Title, head),
            new("apps, files, folders, web pages, scripts", TextStyle.Secondary, new(head.X + after, head.Y, head.Width - after, head.Height)),
        ];
    }

    /// <summary>A tile's name under its icon and, when it would not open, why, across its top; in the cell's own coordinates.</summary>
    public static IReadOnlyList<TextSlot> TileText(RectangleF cell, string name, ShortcutOutcome? why)
    {
        var label = new TextSlot(name, NameStyle, InCell(Label(cell), cell));
        return why is { } reason ? [label, new(BadgeText(reason), BadgeStyle, InCell(BadgeSlot(cell), cell))] : [label];
    }

    /// <summary>The add tile's word, where a name would be; in the cell's own coordinates.</summary>
    public static TextSlot AddText(RectangleF cell) => new("Add", AddStyle, InCell(Label(cell), cell));

    private static RectangleF InCell(RectangleF slot, RectangleF cell) => new(slot.X - cell.X, slot.Y - cell.Y, slot.Width, slot.Height);

    /// <summary>A tile's name, the add tile's word, and why a tile would not open, in the warn colour.</summary>
    public static readonly TextStyle NameStyle = new(12, Color: 0xFFE9E9EE, Align: TextAlign.Center);
    public static readonly TextStyle AddStyle = NameStyle with { Color = TextStyle.TertiaryColor };
    public static readonly TextStyle BadgeStyle = new(10, Color: TextStyle.WarnColor, Align: TextAlign.Trailing);

    /// <summary>The band across a tile's top where why it would not open reads, right-aligned, clear of the card's corners.</summary>
    public static RectangleF BadgeSlot(RectangleF cell) => new(cell.X + 4, cell.Y + 1, cell.Width - 10, 12);

    /// <summary>Why a tile would not open, in a few words.</summary>
    public static string BadgeText(ShortcutOutcome why) => why switch
    {
        ShortcutOutcome.ScriptsOff => "Scripts off",
        ShortcutOutcome.NeedsConfirm => "Confirm in Settings",
        ShortcutOutcome.Changed => "Changed, confirm again",
        ShortcutOutcome.Missing => "Moved or deleted",
        _ => "Can't open",
    };
}
