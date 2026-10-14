namespace Tomte.Core;

/// <summary>
/// The tray's pause. While paused no trigger reveals the island and clipboard history keeps nothing;
/// "Open island" from the tray still works. Kept in memory only, so "until restart" ends with the
/// process. Callers pass the time in, so nothing here runs on a timer: a trigger asks
/// <see cref="IsPaused"/> when it fires, and the tray's own one-shot calls <see cref="Resume"/> when a
/// timed pause runs out. The clipboard listener asks from its own thread.
/// </summary>
public sealed class PauseState
{
    private readonly Lock gate = new();

    /// <summary>A pause ended: resumed from the tray, or its hour ran out. Events that waited may show now.</summary>
    public event Action? Ended;

    public DateTimeOffset? Until { get; private set; }

    public bool UntilRestart { get; private set; }

    public bool IsPaused(DateTimeOffset now)
    {
        lock (gate)
        {
            return UntilRestart || now < Until;
        }
    }

    public void PauseFor(TimeSpan duration, DateTimeOffset now)
    {
        lock (gate)
        {
            UntilRestart = false;
            Until = now + duration;
        }
    }

    public void PauseUntilRestart()
    {
        lock (gate)
        {
            UntilRestart = true;
            Until = null;
        }
    }

    public void Resume()
    {
        bool was;
        lock (gate)
        {
            was = UntilRestart || Until is not null;
            UntilRestart = false;
            Until = null;
        }

        // Outside the lock: the handler asks IsPaused and shows the island.
        if (was)
        {
            Ended?.Invoke();
        }
    }

    /// <summary>For the tray tooltip: "Tomte", "Tomte, paused until 14:32" or "Tomte, paused until restart".</summary>
    public string Describe(DateTimeOffset now) =>
        UntilRestart ? "Tomte, paused until restart"
        : IsPaused(now) ? $"Tomte, paused until {Until!.Value.ToLocalTime():HH:mm}"
        : "Tomte";
}
