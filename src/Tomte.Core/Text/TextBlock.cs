namespace Tomte.Core.Text;

/// <summary>
/// Text drawn at the display scale shows 1:1 only when its surface starts on a whole device pixel and is a
/// whole number of pixels large; anything else is resampled and blurs.
/// </summary>
public static class PixelGrid
{
    /// <summary>
    /// The offset nearest <paramref name="dips"/>, inside a parent that sits <paramref name="parent"/> DIPs from
    /// a whole-pixel origin, at which a child lands on a whole pixel at <paramref name="scale"/>.
    /// </summary>
    public static float Snap(float parent, float dips, double scale) =>
        (float)(Math.Round((parent + dips) * scale, MidpointRounding.AwayFromZero) / scale - parent);

    /// <summary>The whole pixels that cover a length in DIPs at a scale.</summary>
    public static int Cover(float dips, double scale) => Math.Max(0, (int)Math.Ceiling(dips * scale - 0.001));
}

/// <summary>
/// What one text visual shows: its run, laid out, and the scale it was drawn at. <see cref="Set"/> lays the run
/// out again only when the run changes and says to draw again only when the run or the scale changes; moving
/// the visual is neither. The surface is <see cref="Pad"/> pixels larger than the text on every side, so
/// anti-aliasing at the edges is never cut.
/// </summary>
public sealed class TextBlock : IDisposable
{
    public const int Pad = 1;

    public TextRun? Run { get; private set; }

    /// <summary>Null for an empty run, which draws nothing.</summary>
    public TextLayout? Layout { get; private set; }

    public double Scale { get; private set; }

    /// <summary>The run's size in DIPs as laid out; zero when empty.</summary>
    public (float Width, float Height) Size => Layout is { } l ? (l.Width, l.Height) : (0, 0);

    /// <summary>
    /// The surface in pixels: the text box plus the pad. A leading run is as wide as its text; a centred or
    /// trailing one is as wide as its run's width, inside which DirectWrite places it.
    /// </summary>
    public (int Width, int Height) Pixels => Layout is { } l
        ? (PixelGrid.Cover(l.Run.Style.Align == TextAlign.Leading ? l.Width : l.Run.MaxWidth, Scale) + 2 * Pad, PixelGrid.Cover(l.Height, Scale) + 2 * Pad)
        : (0, 0);

    /// <summary>True when the pixels must be drawn again.</summary>
    public bool Set(TextRun run, double scale)
    {
        if (run == Run && scale == Scale)
        {
            return false;
        }

        if (run != Run)
        {
            Layout?.Dispose();
            Layout = run.Text.Length == 0 ? null : TextLayout.Of(run);
            Run = run;
        }

        Scale = scale;
        return true;
    }

    public void Dispose()
    {
        Layout?.Dispose();
        Layout = null;
        Run = null;
    }
}
