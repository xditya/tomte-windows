namespace Tomte.Core;

/// <summary>
/// Where the island sits along the top edge, and the press that moves it there. The place is an offset in DIPs from
/// the middle of the top edge to the island's centre; 0 is the middle. Each shape keeps its whole window on the screen,
/// shadow room included, so it never takes clicks meant for a monitor beside it, and its outline at least the top gap
/// from both sides. A wide panel opened from a pill moved to a corner therefore shifts back inward.
/// </summary>
public static class IslandPlace
{
    /// <summary>The offset as far as <paramref name="shape"/> may go on a screen this many pixels wide; 0 on a screen too narrow for it.</summary>
    public static double Clamp(double offset, in IslandGeometry shape, double scale, int screenWidth, bool attached)
    {
        var (lo, hi) = Span(shape, scale, screenWidth, attached);
        return lo > hi ? 0 : Math.Clamp(offset, (lo - screenWidth / 2) / scale, (hi - screenWidth / 2) / scale);
    }

    /// <summary>The pixel column the shape's centre goes on.</summary>
    public static int Center(double offset, in IslandGeometry shape, double scale, int screenWidth, bool attached)
    {
        var (lo, hi) = Span(shape, scale, screenWidth, attached);
        return lo > hi ? screenWidth / 2 : Math.Clamp(screenWidth / 2 + (int)Math.Round(offset * scale), lo, hi);
    }

    // The columns the centre may take: the window (IslandOutline.WindowBounds) on the screen, and the outline a gap in
    // from the sides, which the floating pill's shadow room already gives.
    private static (int Lo, int Hi) Span(in IslandGeometry shape, double scale, int screenWidth, bool attached)
    {
        var (side, _, _) = IslandOutline.Insets(scale, attached);
        int width = (int)Math.Ceiling(shape.OuterWidth * scale) + 2 * side;
        int margin = Math.Max(0, IslandOutline.GapPixels(scale) - side);
        return (width / 2 + margin, screenWidth - (width - width / 2) - margin);
    }

    /// <summary>A press turns into a drag once it has moved the system's drag distance (SM_CXDRAG, SM_CYDRAG) either way.</summary>
    public static bool IsDrag(int dx, int dy, int dragX, int dragY) => Math.Abs(dx) >= dragX || Math.Abs(dy) >= dragY;

    /// <summary>
    /// A second click within the double-click time and inside the rectangle (SM_CXDOUBLECLK by SM_CYDOUBLECLK) centred
    /// on the first one, as Windows counts it. Only presses that <see cref="CountsForDoubleClick"/> count; the first
    /// click was recorded only when it was one, and it waits out the double-click time before it opens anything.
    /// </summary>
    public static bool IsDoubleClick(bool onPill, int dx, int dy, long elapsedMs, int boxX, int boxY, uint timeMs) =>
        onPill && elapsedMs <= timeMs && Math.Abs(dx) <= boxX / 2 && Math.Abs(dy) <= boxY / 2;

    /// <summary>
    /// A press that may be half of a double click: on the pill's own surface (<paramref name="grabs"/>), not a label or
    /// a control. The tomte opens the palette at once wherever it does, so it counts only on the peek badge, where it
    /// is all there is.
    /// </summary>
    public static bool CountsForDoubleClick(bool grabs, bool onTomte, bool peekBadge) => grabs && (!onTomte || peekBadge);
}
