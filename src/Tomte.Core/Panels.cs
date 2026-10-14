using System.Numerics;

namespace Tomte.Core;

/// <summary>The tabs of the Panels state. The enum order is the tab order.</summary>
public enum PanelId
{
    Shelf,
    Shortcuts,
    Media,
    Scratchpad,
    Clipboard,
}

/// <summary>
/// Which panels exist and which one Panels shows. Tabs sit in <see cref="PanelId"/> order whatever order
/// features register in. Until something is selected the first tab is current; after that the choice
/// sticks, so reopening Panels lands on the last-used tab.
/// the last-used tab lives in memory; write it to the store if it should survive a restart.
/// </summary>
public sealed class PanelTabs
{
    private readonly SortedSet<PanelId> registered = [];
    private PanelId? selected;

    public IReadOnlyCollection<PanelId> Order => registered;

    public PanelId? Current => selected ?? (registered.Count > 0 ? registered.Min : null);

    public void Register(PanelId id)
    {
        if (!registered.Add(id))
        {
            throw new InvalidOperationException($"The {id} panel is already registered.");
        }
    }

    /// <summary>False when the panel is not registered or is already current; nothing changes then.</summary>
    public bool Select(PanelId id)
    {
        if (!registered.Contains(id) || Current == id)
        {
            return false;
        }

        selected = id;
        return true;
    }

    /// <summary>The next tab (+1) or the previous one (-1), wrapping at both ends. False with fewer than two panels.</summary>
    public bool Step(int direction)
    {
        if (registered.Count < 2 || direction == 0)
        {
            return false;
        }

        var order = registered.ToArray();
        int i = Array.IndexOf(order, Current!.Value) + Math.Sign(direction);
        selected = order[(i + order.Length) % order.Length];
        return true;
    }
}

/// <summary>
/// The two coordinate spaces island content uses, both in DIPs. Island space has x = 0 on the island's
/// centre line and y = 0 at the island's top edge. A panel body's space starts at the body's top-left
/// corner, under the tab strip; the strip row belongs to the host, and the mascot sits in it.
/// </summary>
public static class PanelSpace
{
    public const float TabStripHeight = 42;

    /// <summary>A point in window pixels to island space; the origin is where island space starts in the window.</summary>
    public static Vector2 FromPixels(int x, int y, float originX, float originY, double scale) =>
        new((float)((x - originX) / scale), (float)((y - originY) / scale));

    public static Vector2 ToBody(Vector2 island, double panelWidth) =>
        new(island.X + (float)panelWidth / 2, island.Y - TabStripHeight);

    /// <summary>
    /// Whether an island point is on the shape itself: no further than half its width from the centre line,
    /// and between its top and bottom edges. The window also covers the shadow beside and below the pill and
    /// a row above it, and a press or a wheel there is nobody's.
    /// </summary>
    public static bool OnShape(Vector2 island, in IslandGeometry shape) =>
        Math.Abs(island.X) <= shape.Width / 2 && island.Y >= 0 && island.Y <= shape.Depth;

    /// <summary>
    /// The wheel over the tab strip as a tab step: -1 for a notch (120) away from the user, +1 toward, else 0.
    /// A touchpad's small deltas add up in <paramref name="carry"/>. While the new panel's content has not
    /// started to show, the strip ignores the wheel, so the rest of the scroll that opened Panels or switched a
    /// tab does not switch another.
    /// </summary>
    public static int TabStep(ref int carry, int delta, bool settling)
    {
        carry = settling ? 0 : carry + delta;
        if (Math.Abs(carry) < 120)
        {
            return 0;
        }

        int step = carry > 0 ? -1 : 1;
        carry = 0;
        return step;
    }
}
