namespace Tomte.Core.Triggers;

/// <summary>
/// User settings for the reveal triggers. Every number starts at the default; the edge-push
/// thresholds are starting values until the day-long accidental-reveal log tunes them. Sizes are in
/// DIPs at 100% and are scaled per monitor by the caller.
/// </summary>
public sealed record TriggerSettings
{
    public bool EdgePush { get; init; } = true;

    /// <summary>Fraction of the monitor's top edge, centred, that counts as the reveal zone.</summary>
    public double ZoneFraction { get; init; } = 0.30;

    /// <summary>Upward raw counts that reveal when they arrive inside <see cref="PushWindowMs"/>.</summary>
    public int PushCounts { get; init; } = 60;

    public int PushWindowMs { get; init; } = 400;

    /// <summary>Fallback: resting on the top row inside the zone this long also reveals.</summary>
    public int DwellMs { get; init; } = 350;

    public bool DragApproaching { get; init; } = true;

    /// <summary>A drag this close to the top edge, inside the zone, shows the drop surface.</summary>
    public double DragBand { get; init; } = 48;

    /// <summary>No OLE DragEnter within this time after showing the surface means it was not a drag.</summary>
    public int DragTimeoutMs { get; init; } = 600;

    public double RetractMargin { get; init; } = 24;

    public int RetractDelayMs { get; init; } = 400;

    /// <summary>Resting the pointer on Compact this long opens Panels.</summary>
    public int HoverDwellMs { get; init; } = 350;

    public int EventHoldMs { get; init; } = 4000;

    public bool DesktopPeek { get; init; } = true;

    public int PeekDebounceMs { get; init; } = 500;

    public int FirstRunHintMs { get; init; } = 10000;

    /// <summary>Executable names (for example "game.exe") where the island never reveals.</summary>
    public IReadOnlyList<string> DenyApps { get; init; } = [];

    /// <summary>When not empty, the island only reveals while one of these executables is in front.</summary>
    public IReadOnlyList<string> AllowApps { get; init; } = [];

    public IReadOnlyDictionary<HotkeyAction, Chord> Hotkeys { get; init; } = Chord.Defaults;
}
