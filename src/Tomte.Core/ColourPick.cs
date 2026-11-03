using System.Globalization;

namespace Tomte.Core;

/// <summary>A colour picked from the screen, and the ways the card writes it out.</summary>
public readonly record struct PickedColour(byte R, byte G, byte B)
{
    /// <summary>The colour of the pixel at <paramref name="x"/>, <paramref name="y"/> of a BGRA frame <paramref name="width"/> pixels wide.</summary>
    public static PickedColour At(ReadOnlySpan<byte> bgra, int width, int x, int y)
    {
        int i = 4 * ((y * width) + x);
        return new(bgra[i + 2], bgra[i + 1], bgra[i]);
    }

    /// <summary>0xRRGGBB.</summary>
    public uint Rgb => (uint)((R << 16) | (G << 8) | B);

    public string Hex => $"#{R:X2}{G:X2}{B:X2}";

    public string RgbText => $"rgb({R}, {G}, {B})";

    /// <summary>Hue in degrees, saturation and lightness in percent, each rounded to a whole number.</summary>
    public (int H, int S, int L) Hsl
    {
        get
        {
            double r = R / 255.0, g = G / 255.0, b = B / 255.0;
            double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min, l = (max + min) / 2;
            if (d == 0)
                return (0, 0, (int)Math.Round(l * 100));
            double s = d / (1 - Math.Abs((2 * l) - 1));
            double h = max == r ? ((g - b) / d) + (g < b ? 6 : 0) : max == g ? ((b - r) / d) + 2 : ((r - g) / d) + 4;
            return ((int)Math.Round(h * 60) % 360, (int)Math.Round(s * 100), (int)Math.Round(l * 100));
        }
    }

    public string HslText
    {
        get
        {
            var (h, s, l) = Hsl;
            return string.Create(CultureInfo.InvariantCulture, $"hsl({h}, {s}%, {l}%)");
        }
    }

    public const string Title = "Colour from screen";

    /// <summary>
    /// The card: the colour as a square beside its hex, RGB and HSL, each a row that copies itself, then Close. The hex
    /// is put on the clipboard first; <paramref name="copied"/> is what the clipboard last took, null when nothing did.
    /// </summary>
    public DropResult Card(string? copied) => new(
        Title,
        "Nothing sent",
        copied is not null ? $"Copied {copied} · click a value to copy it" : "Another app has the clipboard open, so nothing was copied. Click the hex to try again.",
        false,
        "",
        [],
        [DropResult.Close])
    { Swatch = Rgb, Values = [Hex, RgbText, HslText] };

    /// <summary>
    /// Black or white as 0xAARRGGBB, whichever has more contrast on <paramref name="rgb"/> by WCAG's relative luminance:
    /// black wins once (L + 0.05)² passes 1.05 × 0.05, at L of about 0.18.
    /// </summary>
    public static uint TextOn(uint rgb)
    {
        static double Channel(uint c) => c / 255.0 is var v && v <= 0.04045 ? v / 12.92 : Math.Pow((v + 0.055) / 1.055, 2.4);
        double l = (0.2126 * Channel((rgb >> 16) & 0xFF)) + (0.7152 * Channel((rgb >> 8) & 0xFF)) + (0.0722 * Channel(rgb & 0xFF));
        return (l + 0.05) * (l + 0.05) > 1.05 * 0.05 ? 0xFF000000 : 0xFFFFFFFF;
    }
}

/// <summary>
/// The magnifier by the pointer on the colour picker's overlay, in the monitor's physical pixels: a square of
/// <see cref="Cells"/> by <see cref="Cells"/> screen pixels, each drawn as a block whole pixels wide, with the hex value
/// of the centre one on a label under it. It sits below and right of the pointer and flips to the other side where the
/// monitor's edge would cut it.
/// </summary>
public static class Loupe
{
    /// <summary>Odd, so one cell is the centre: the pixel under the pointer.</summary>
    public const int Cells = 11;

    public const double CellDips = 10, OffsetDips = 24, LabelGapDips = 6, LabelHeightDips = 28, CornerDips = 12;

    /// <summary>One cell's side in whole pixels at <paramref name="scale"/>, so the blocks never blur.</summary>
    public static int Cell(double scale) => Math.Max(1, (int)Math.Round(CellDips * scale, MidpointRounding.AwayFromZero));

    /// <summary>The magnified square and its label, both in the monitor's pixels, for a pointer at <paramref name="x"/>, <paramref name="y"/>.</summary>
    public static (PixelRect Zoom, PixelRect Label) Place(int x, int y, int monitorWidth, int monitorHeight, double scale)
    {
        int side = Cells * Cell(scale), offset = (int)Math.Round(OffsetDips * scale);
        int gap = (int)Math.Round(LabelGapDips * scale), label = (int)Math.Round(LabelHeightDips * scale), height = side + gap + label;
        int left = x + offset + side <= monitorWidth ? x + offset : x - offset - side;
        int top = y + offset + height <= monitorHeight ? y + offset : y - offset - height;
        left = Math.Clamp(left, 0, Math.Max(0, monitorWidth - side));
        top = Math.Clamp(top, 0, Math.Max(0, monitorHeight - height));
        return (new PixelRect(left, top, side, side), new PixelRect(left, top + side + gap, side, label));
    }

    /// <summary>The screen pixel a cell shows: the pointer's pixel at the centre, held to the monitor at its edges.</summary>
    public static (int X, int Y) Source(int x, int y, int column, int row, int monitorWidth, int monitorHeight) =>
        (Math.Clamp(x + column - (Cells / 2), 0, monitorWidth - 1), Math.Clamp(y + row - (Cells / 2), 0, monitorHeight - 1));
}
