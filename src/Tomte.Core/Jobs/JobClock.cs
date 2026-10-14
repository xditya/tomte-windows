namespace Tomte.Core.Jobs;

/// <summary>
/// The in-app runner's one-shot, worked out from the wall clock. The shell sets one SetTimer for <see cref="Wait"/>
/// on the island window and asks again after a resume from sleep and after a clock change, because the one-shot is an
/// interval that neither follows a clock that was set nor counts time across a sleep. Nothing ever ticks.
/// </summary>
public sealed class JobClock(Func<DateTimeOffset>? now = null)
{
    private readonly Func<DateTimeOffset> now = now ?? (() => DateTimeOffset.Now);

    /// <summary>The next run the one-shot is for, or null when nothing is armed.</summary>
    public DateTimeOffset? Next { get; private set; }

    /// <summary>Sets the next run and returns the wait for it (see <see cref="Wait"/>).</summary>
    public uint? Arm(DateTimeOffset? due)
    {
        Next = due;
        return Wait();
    }

    /// <summary>
    /// Milliseconds to the next run from the wall clock now, 0 when it has passed, null when nothing is armed. At most
    /// about 24.8 days, SetTimer's limit; an early fire asks again.
    /// </summary>
    public uint? Wait() => Next is { } due ? (uint)Math.Clamp(Math.Ceiling((due - now()).TotalMilliseconds), 0, int.MaxValue) : null;

    /// <summary>The one-shot fired. True when the run is due, and disarms; false when it fired early: wait again.</summary>
    public bool Fired()
    {
        if (Next is { } due && now() >= due)
        {
            Next = null;
            return true;
        }

        return false;
    }
}
