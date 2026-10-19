using System.Drawing;
using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>
/// The line the capture overlay shows at the top of its monitor until the first press, on a dark capsule as tall as the
/// island's pill and the island's top gap below the edge, centred. In DIPs from the monitor's top-left.
/// </summary>
public static class LassoHint
{
    public const string Text = "Drag around what you want to ask about · Esc cancels";
    public const float Height = 40, Side = 18;
    public static readonly TextStyle Style = TextStyle.Primary;

    /// <summary>The capsule for a line <paramref name="textWidth"/> wide on a monitor <paramref name="monitorWidth"/> wide.</summary>
    public static RectangleF Card(float monitorWidth, float textWidth)
    {
        float width = MathF.Ceiling(textWidth) + 2 * Side;
        return new(MathF.Round((monitorWidth - width) / 2), (float)IslandOutline.Gap, width, Height);
    }

    /// <summary>Where the line goes in its card.</summary>
    public static RectangleF Line(RectangleF card) => new(card.X + Side, card.Y, card.Width - 2 * Side, card.Height);
}

/// <summary>A rectangle in physical pixels. A lasso box or crop is monitor-relative; a monitor's bounds are virtual-screen.</summary>
public readonly record struct PixelRect(int X, int Y, int Width, int Height);

/// <summary>
/// The freehand stroke on the capture overlay of one monitor. Points arrive in the overlay's client
/// coordinates, which under per-monitor DPI awareness are that monitor's physical pixels; points dragged
/// past the edge (the mouse is captured) clamp to it. <paramref name="scale"/> is that monitor's own
/// DPI scale, not the primary's, so DIP sizes stay right on mixed-scale setups.
/// </summary>
public sealed class Lasso(int monitorWidth, int monitorHeight, double scale)
{
    /// <summary>Smaller than this in either direction is a slip, not a lasso.</summary>
    public const double MinSizeDips = 20;

    private readonly List<(int X, int Y)> points = [];
    private int left, top, right, bottom;

    public IReadOnlyList<(int X, int Y)> Points => points;

    public bool Drawing { get; private set; }

    public double Scale => scale;

    /// <summary>The bounding box so far: inclusive of the outermost pixels.</summary>
    public PixelRect Box => points.Count == 0 ? default : new(left, top, right - left + 1, bottom - top + 1);

    public (double Width, double Height) BoxDips => (Box.Width / scale, Box.Height / scale);

    public void Down(int x, int y)
    {
        points.Clear();
        Drawing = true;
        (x, y) = Clamp(x, y);
        (left, top, right, bottom) = (x, y, x, y);
        points.Add((x, y));
    }

    /// <summary>False when nothing changed (not drawing, or the same pixel again).</summary>
    public bool Move(int x, int y)
    {
        if (!Drawing)
            return false;
        (x, y) = Clamp(x, y);
        if (points[^1] == (x, y))
            return false;
        points.Add((x, y));
        (left, top, right, bottom) = (Math.Min(left, x), Math.Min(top, y), Math.Max(right, x), Math.Max(bottom, y));
        return true;
    }

    /// <summary>The box to capture, or null when the stroke was too small; then the stroke is cleared for another try.</summary>
    public PixelRect? Up()
    {
        if (!Drawing)
            return null;
        Drawing = false;
        var (w, h) = BoxDips;
        if (w >= MinSizeDips && h >= MinSizeDips)
            return Box;
        points.Clear();
        return null;
    }

    private (int, int) Clamp(int x, int y) => (Math.Clamp(x, 0, monitorWidth - 1), Math.Clamp(y, 0, monitorHeight - 1));
}
