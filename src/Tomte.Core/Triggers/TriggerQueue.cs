namespace Tomte.Core.Triggers;

/// <summary>An internal event that may reveal the Compact pill: a timer done, a job result, an answer ready.</summary>
public sealed record IslandEvent(string Text, DateTimeOffset At);

/// <summary>
/// Events arrive while Focus, Do Not Disturb or a fullscreen app may be on, or while the island is already
/// showing something. Those wait here, in arrival order, and come out together once the wait ends so the island
/// can show them as one digest.
/// </summary>
public sealed class TriggerQueue
{
    private readonly Queue<IslandEvent> waiting = new();

    public int Count => waiting.Count;

    /// <summary>
    /// The island is showing something an event pill must not replace: anything but Hidden, except the desktop
    /// peek pill, which is ambient and gives way. An event that arrives now waits until the island hides.
    /// </summary>
    public static bool IslandBusy(IslandState state, IslandActivity activity) =>
        state != IslandState.Hidden && !activity.HasFlag(IslandActivity.Peek);

    /// <summary>True when the event should show now; otherwise it has been queued.</summary>
    public bool Offer(IslandEvent e, bool suppressed)
    {
        if (!suppressed)
        {
            return true;
        }

        waiting.Enqueue(e);
        return false;
    }

    /// <summary>Everything that waited, oldest first; the queue is empty afterwards.</summary>
    public IReadOnlyList<IslandEvent> Release()
    {
        var released = waiting.ToArray();
        waiting.Clear();
        return released;
    }
}
