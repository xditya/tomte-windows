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

    /// <summary>
    /// A press on one of the island's targets holds the mouse, or a view's drag from it runs. Unlike a drag coming in, it
    /// does not keep the island over a fullscreen app or the lock screen.
    /// </summary>
    Pressing = 128,

    /// <summary>The pill after a closed AI view, with Undo: it holds the island for the undo time, wherever the pointer is, and a rest on it opens nothing.</summary>
    Undo = 256,

    /// <summary>
    /// The pill kept while music plays: the pointer leaving does not retract it; playback stopping does, after
    /// <see cref="RetractionRules.KeptAfterPlayback"/>.
    /// </summary>
    Playing = 512,
}

/// <summary>What a pointer report does to the hover dwell: nothing, start it, or cancel it.</summary>
public enum HoverStep
{
    None,
    Start,
    Cancel,
}

public static class RetractionRules
{
    /// <summary>
    /// Called when the pointer has stayed outside the island's bounds plus the margin for the retract delay.
    /// Hidden has nothing to retract and Capture keeps the island hidden under its overlay.
    /// </summary>
    public static bool ShouldRetract(IslandState state, IslandActivity activity) =>
        state is not (IslandState.Hidden or IslandState.Capture) && activity == IslandActivity.None;

    /// <summary>
    /// What still holds the island when the retract delay is up: a press on one of its targets whose button is up was let
    /// go outside the island, where the island, not being the foreground window, does not hear the release, so it holds
    /// nothing any more.
    /// </summary>
    public static IslandActivity Released(IslandActivity activity, bool buttonDown) =>
        buttonDown ? activity : activity & ~IslandActivity.Pressing;

    /// <summary>
    /// What holds the island once a drag out of it is under way. Nothing on the island takes its own drag, so the press
    /// that started it holds nothing more: the island retracts as for any pointer that left it, and is gone before the
    /// drop, which can keep the island's thread for seconds (a target copying a file) with the island frozen until then.
    /// A drop zone's result keeps its own hold.
    /// </summary>
    public static IslandActivity DraggingOut(IslandActivity activity) => activity & ~IslandActivity.Pressing;

    /// <summary>A hover dwell or a scroll on the pill opens Panels from Compact, unless the rambler or the undo pill holds the pill, a drag is moving it or a press holds the mouse.</summary>
    public static bool OpensPanels(IslandState state, IslandActivity activity) =>
        state == IslandState.Compact && (activity & (IslandActivity.Rambling | IslandActivity.Undo | IslandActivity.DragInProgress | IslandActivity.Pressing)) == 0;

    /// <summary>
    /// What one pointer report does to the hover dwell that opens Panels from Compact: coming onto the pill starts it,
    /// leaving the pill cancels it, and a report that keeps the pointer on the pill (a move, or the same place reported
    /// again for new bounds) lets it run, so moving about the pill never starts it over. The margin around the pill
    /// that holds it open is not on it.
    /// </summary>
    public static HoverStep Hover(bool wasOnPill, bool onPill) =>
        onPill == wasOnPill ? HoverStep.None : onPill ? HoverStep.Start : HoverStep.Cancel;

    /// <summary>
    /// An event pill or the first-run hint stays past its time while the pointer is on it or a drag is moving it, and
    /// is looked at again a little later.
    /// </summary>
    public static bool HoldsPastItsTime(bool pointerInside, IslandActivity activity) =>
        pointerInside || activity.HasFlag(IslandActivity.DragInProgress);

    /// <summary>
    /// What is left when a state the user asked for takes the island over: the peek pill's "retract when a window
    /// comes under" rule, the hold of the pill kept while music plays and the rambler's hold on the pill end, so a hold
    /// that ends later cannot retract it, and a drop zone's result, which the new state replaces, no longer holds the
    /// island.
    /// </summary>
    public static IslandActivity TakenOver(IslandActivity activity) =>
        activity & ~(IslandActivity.Peek | IslandActivity.Rambling | IslandActivity.Result | IslandActivity.Playing);

    /// <summary>How long the pill kept while music plays stays once playback pauses or stops, so it does not come and go between tracks.</summary>
    public static readonly TimeSpan KeptAfterPlayback = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Whether the pill kept while music plays shows: the setting is on and something plays (<paramref name="stoppedFor"/>
    /// null), or the pill is up (<paramref name="kept"/>) and playback stopped less than <see cref="KeptAfterPlayback"/>
    /// ago. Never while Tomte is paused, over a fullscreen app, on the lock screen or a secure surface, or while an app
    /// with a deny rule is in front.
    /// </summary>
    public static bool KeepsPill(bool on, bool kept, TimeSpan? stoppedFor, bool paused, SuppressionState desktop, IReadOnlyList<AppRule> rules) =>
        on && (stoppedFor is null || (kept && stoppedFor < KeptAfterPlayback)) && !paused && !desktop.Fullscreen
        && Suppression.Allows(RevealTrigger.Peek, desktop, rules);

    /// <summary>
    /// How long ago playback stopped, for <see cref="KeepsPill"/>: null while something plays, and as good as forever once
    /// the app that played is gone (it quit, or another app's paused session took its place), so the pill goes at once.
    /// </summary>
    public static TimeSpan? StoppedFor(bool playing, bool sameApp, TimeSpan since) => playing ? null : sameApp ? since : TimeSpan.MaxValue;

    /// <summary>
    /// The kept pill whose time after playback has run out stays while the pointer is on it or a drag moves it, as an
    /// event pill does, but only when nothing else refuses it: a deny rule coming to the front, the pause or the setting
    /// turned off hide it at once.
    /// </summary>
    public static bool HoldsKeptPill(bool on, bool paused, SuppressionState desktop, IReadOnlyList<AppRule> rules, bool pointerInside, IslandActivity activity) =>
        KeepsPill(on, kept: true, TimeSpan.Zero, paused, desktop, rules) && HoldsPastItsTime(pointerInside, activity);

    /// <summary>
    /// An edge push shows the push pill, with its ring, countdown and glance line, where the island is hidden or shows an
    /// ambient pill (peek, the pill kept while music plays, the launch arrival's): those give way to it and come back once
    /// it retracts. Elsewhere the island is already up and the push does nothing.
    /// </summary>
    public static bool PushShows(IslandState state, IslandActivity activity, CompactReason reason) =>
        state == IslandState.Hidden || (activity & (IslandActivity.Peek | IslandActivity.Playing)) != 0 || reason == CompactReason.Arrival;

    /// <summary>
    /// The voice key turns the island into the rambler's pill from every state but Shelf-drop, where a file is being
    /// dragged onto it and a drop on the pill would open the shelf over the recording. From an open state the state
    /// passes through Hidden (what shows leaves as on a retract) on its way to Compact.
    /// </summary>
    public static bool VoiceOpens(IslandState state) => state != IslandState.ShelfDrop;

    /// <summary>
    /// Whether Esc is taken as a global hotkey: in the states the user opened on purpose, except that a drop zone's
    /// result or a pinned island, which can stay up a long time, takes it only while the pointer is on the island, so Esc
    /// in the user's own app after Alt+Tab stays theirs. While the island has the keys Esc reaches it as a key, where an
    /// input method can still use it, so it is no hotkey then.
    /// </summary>
    public static bool TakesEscape(IslandState state, IslandActivity activity, bool pointerInside) =>
        state is IslandState.Panels or IslandState.Palette && !activity.HasFlag(IslandActivity.Typing)
        && ((activity & (IslandActivity.Result | IslandActivity.Pinned)) == 0 || pointerInside);

    /// <summary>
    /// The palette retracts when it loses the keys while nothing is being sent, unless the island is pinned (Ctrl+P),
    /// which only Esc, the hotkey that opened it or Ctrl+P again ends. A pin also keeps the island when the pointer leaves
    /// (<see cref="ShouldRetract"/>), not over a fullscreen app or the lock screen.
    /// </summary>
    public static bool RetractsOnFocusLoss(IslandActivity activity) => !activity.HasFlag(IslandActivity.Pinned);

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
