namespace Tomte.Core;

/// <summary>
/// Where the island sits along the top edge, and the press that moves it there. The place is an offset in DIPs from
/// the middle of the top edge to the island's centre; 0 is the middle. Each shape keeps its outline at least the top
/// gap away from both sides of the screen, so a wide panel opened from a pill moved to a corner shifts back inward.
/// </summary>
public static class IslandPlace
{
    /// <summary>The offset as far as <paramref name="shape"/> may go on a screen this many pixels wide; 0 on a screen too narrow for it.</summary>
    public static double Clamp(double offset, in IslandGeometry shape, double scale, int screenWidth)
    {
        double room = Math.Max(0, screenWidth / scale / 2 - shape.OuterWidth / 2 - IslandOutline.Gap);
        return Math.Clamp(offset, -room, room);
    }

    /// <summary>The pixel column the shape's centre goes on.</summary>
    public static int Center(double offset, in IslandGeometry shape, double scale, int screenWidth) =>
        screenWidth / 2 + (int)Math.Round(Clamp(offset, shape, scale, screenWidth) * scale);

    /// <summary>A press turns into a drag once it has moved the system's drag distance (SM_CXDRAG, SM_CYDRAG) either way.</summary>
    public static bool IsDrag(int dx, int dy, int dragX, int dragY) => Math.Abs(dx) >= dragX || Math.Abs(dy) >= dragY;

    /// <summary>
    /// A second click within the double-click time and inside the rectangle (SM_CXDOUBLECLK by SM_CYDOUBLECLK) centred
    /// on the first one, as Windows counts it.
    /// </summary>
    public static bool IsDoubleClick(int dx, int dy, long elapsedMs, int boxX, int boxY, uint timeMs) =>
        elapsedMs <= timeMs && Math.Abs(dx) <= boxX / 2 && Math.Abs(dy) <= boxY / 2;
}
