namespace Tomte.Core.Triggers;

/// <summary>
/// The edge-push rule with no Win32 in it, so it runs on fake timestamps. Idle: the cursor is not pinned
/// at the top row inside the zone. Pushing: it is, and upward raw counts accumulate in a sliding window.
/// Revealed: the reveal fired; nothing more until the cursor leaves the top row, which resets the count.
/// The caller owns the dwell timer: arm it on EnterZone, kill it on LeaveZone or Reveal, and call
/// <see cref="DwellElapsed"/> when it fires. Runs on the input thread: no allocation after construction.
/// </summary>
public sealed class EdgePushDetector(int countsToReveal, int windowMs)
{
    public enum Event
    {
        None,
        EnterZone,
        LeaveZone,
        Reveal,
    }

    private enum State
    {
        Idle,
        Pushing,
        Revealed,
    }

    // fixed ring of (time, counts); 2048 samples covers 400 ms of an 8 kHz mouse
    private readonly double[] times = new double[2048];
    private readonly int[] counts = new int[2048];
    private int head, tail, length;
    private State state;

    public int WindowCounts { get; private set; }

    /// <summary>One raw mouse sample. <paramref name="dy"/> is the raw vertical delta (negative is up).</summary>
    public Event Feed(double nowMs, int dy, bool atTopRow, bool inZone)
    {
        bool pinned = atTopRow && inZone;
        switch (state)
        {
            case State.Idle:
                if (!pinned)
                {
                    return Event.None;
                }

                state = State.Pushing;
                head = tail = length = WindowCounts = 0;
                Push(nowMs, dy);
                return Event.EnterZone;

            case State.Pushing:
                if (!pinned)
                {
                    state = State.Idle;
                    return Event.LeaveZone;
                }

                Push(nowMs, dy);
                if (WindowCounts < countsToReveal)
                {
                    return Event.None;
                }

                state = State.Revealed;
                return Event.Reveal;

            default:
                if (!pinned)
                {
                    state = State.Idle;
                }

                return Event.None;
        }
    }

    /// <summary>The dwell fallback. A no-op unless the cursor is still pinned where the timer was armed.</summary>
    public Event DwellElapsed()
    {
        if (state != State.Pushing)
        {
            return Event.None;
        }

        state = State.Revealed;
        return Event.Reveal;
    }

    private void Push(double nowMs, int dy)
    {
        int up = dy < 0 ? -dy : 0;
        if (length == times.Length)
        {
            Evict();
        }

        times[head] = nowMs;
        counts[head] = up;
        head = (head + 1) % times.Length;
        length++;
        WindowCounts += up;
        while (length > 0 && times[tail] < nowMs - windowMs)
        {
            Evict();
        }
    }

    private void Evict()
    {
        WindowCounts -= counts[tail];
        tail = (tail + 1) % times.Length;
        length--;
    }
}
