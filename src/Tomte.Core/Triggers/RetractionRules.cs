namespace Tomte.Core.Triggers;

/// <summary>Things that hold the island open after the pointer has left it.</summary>
[Flags]
public enum IslandActivity
{
    None = 0,
    DragInProgress = 1,
    Typing = 2,
    AiTask = 4,
    Pinned = 8,

    /// <summary>Desktop peek retracts when a window enters the zone, not when the pointer leaves.</summary>
    Peek = 16,
    Rambling = 32,

    /// <summary>A drop zone's finished result: it was paid for, so it stays until the user dismisses it.</summary>
    Result = 64,
}

public static class RetractionRules
{
    /// <summary>
    /// Called when the pointer has stayed outside the island's bounds plus the margin for the retract delay.
    /// Hidden has nothing to retract and Capture keeps the island hidden under its overlay.
    /// </summary>
    public static bool ShouldRetract(IslandState state, IslandActivity activity) =>
        state is not (IslandState.Hidden or IslandState.Capture) && activity == IslandActivity.None;

    /// <summary>A hover dwell or a scroll on the pill opens Panels from Compact, unless the rambler holds the pill or a drag is moving it.</summary>
    public static bool OpensPanels(IslandState state, IslandActivity activity) =>
        state == IslandState.Compact && !activity.HasFlag(IslandActivity.Rambling) && !activity.HasFlag(IslandActivity.DragInProgress);

    /// <summary>
    /// An event pill or the first-run hint stays past its time while the pointer is on it or a drag is moving it, and
    /// is looked at again a little later.
    /// </summary>
    public static bool HoldsPastItsTime(bool pointerInside, IslandActivity activity) =>
        pointerInside || activity.HasFlag(IslandActivity.DragInProgress);

    /// <summary>
    /// What is left when a state the user asked for takes the island over: the peek pill's "retract when a window
    /// comes under" rule and the rambler's hold on the pill end, so a hold that ends later cannot retract it, and a
    /// drop zone's result, which the new state replaces, no longer holds the island.
    /// </summary>
    public static IslandActivity TakenOver(IslandActivity activity) => activity & ~(IslandActivity.Peek | IslandActivity.Rambling | IslandActivity.Result);

    /// <summary>
    /// Whether Esc is taken as a global hotkey: in the states the user opened on purpose, except that a drop zone's
    /// result, which can stay up a long time, takes it only while the pointer is on the island, so Esc in the user's
    /// own app after Alt+Tab stays theirs.
    /// </summary>
    public static bool TakesEscape(IslandState state, IslandActivity activity, bool pointerInside) =>
        state is IslandState.Panels or IslandState.Palette && (!activity.HasFlag(IslandActivity.Result) || pointerInside);

    /// <summary>A press outside the island dismisses a drop zone's result; anywhere else it is the user's own click.</summary>
    public static bool DismissedByClickOutside(IslandState state, IslandActivity activity) =>
        state == IslandState.Palette && activity.HasFlag(IslandActivity.Result);

    /// <summary>Pointer inside a window rectangle grown by the margin on every side, in physical pixels.</summary>
    public static bool Inside(int x, int y, int left, int top, int right, int bottom, int margin) =>
        x >= left - margin && x <= right + margin && y >= top - margin && y <= bottom + margin;
}

/// <summary>
/// The reveal zone of one monitor in physical pixels: a fraction of its top edge centred under the island, moved in
/// to stay on the monitor when the island is near a side, plus the drag band below it. Each monitor measures it in
/// its own DPI.
/// </summary>
public readonly record struct RevealZone(int Left, int Right, int Top, int DragBandBottom)
{
    public static RevealZone For(int monitorLeft, int monitorTop, int monitorRight, int centerX, double fraction, double dragBandDip, double scale)
    {
        int width = monitorRight - monitorLeft;
        int zone = Math.Min(width, width - 2 * (int)Math.Round(width * (1 - fraction) / 2));
        int left = Math.Clamp(centerX - zone / 2, monitorLeft, monitorRight - zone);
        return new RevealZone(left, left + zone, monitorTop, monitorTop + (int)Math.Round(dragBandDip * scale));
    }

    public bool ContainsX(int x) => x >= Left && x <= Right;

    public bool InDragBand(int x, int y) => ContainsX(x) && y >= Top && y <= DragBandBottom;
}
