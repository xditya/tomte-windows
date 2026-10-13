namespace Tomte.Core;

/// <summary>
/// The tray's pause. While paused no trigger reveals the island; "Open island" from the tray still
/// does. Kept in memory only, so "until restart" ends with the process. Callers pass the time in,
/// so nothing here runs on a timer: a trigger asks <see cref="IsPaused"/> when it fires.
/// </summary>
public sealed class PauseState
{
    public DateTimeOffset? Until { get; private set; }

    public bool UntilRestart { get; private set; }

    public bool IsPaused(DateTimeOffset now) => UntilRestart || now < Until;

    public void PauseFor(TimeSpan duration, DateTimeOffset now)
    {
        UntilRestart = false;
        Until = now + duration;
    }

    public void PauseUntilRestart()
    {
        UntilRestart = true;
        Until = null;
    }

    public void Resume()
    {
        UntilRestart = false;
        Until = null;
    }

    /// <summary>For the tray tooltip: "Tomte", "Tomte, paused until 14:32" or "Tomte, paused until restart".</summary>
    public string Describe(DateTimeOffset now) =>
        UntilRestart ? "Tomte, paused until restart"
        : IsPaused(now) ? $"Tomte, paused until {Until!.Value.ToLocalTime():HH:mm}"
        : "Tomte";
}
