using Tomte.Core;
using Tomte.Core.Triggers;
using Xunit;

namespace Tomte.Tests;

public class TriggerQueueTests
{
    private static IslandEvent At(int second, string text) => new(text, DateTimeOffset.UnixEpoch.AddSeconds(second));

    [Fact]
    public void ShowsAtOnceWhenNothingSuppresses()
    {
        var queue = new TriggerQueue();
        Assert.True(queue.Offer(At(0, "Timer done"), suppressed: false));
        Assert.Equal(0, queue.Count);
    }

    [Fact]
    public void QueuesInArrivalOrderAndReleasesOnce()
    {
        var queue = new TriggerQueue();
        Assert.False(queue.Offer(At(0, "Timer done"), suppressed: true));
        Assert.False(queue.Offer(At(1, "Job finished"), suppressed: true));
        Assert.False(queue.Offer(At(2, "Answer ready"), suppressed: true));
        Assert.Equal(3, queue.Count);

        var released = queue.Release();

        Assert.Equal(["Timer done", "Job finished", "Answer ready"], released.Select(e => e.Text));
        Assert.Equal(0, queue.Count);
        Assert.Empty(queue.Release());
    }

    [Theory]
    [InlineData(IslandState.Hidden, IslandActivity.None, false)]
    [InlineData(IslandState.Compact, IslandActivity.Peek, false)] // the peek pill gives way to an event
    [InlineData(IslandState.Compact, IslandActivity.Undo, false)] // so does the undo pill, which comes back after it
    [InlineData(IslandState.Compact, IslandActivity.None, true)] // an edge push, the first-run hint, another event
    [InlineData(IslandState.Compact, IslandActivity.Rambling, true)]
    [InlineData(IslandState.ShelfDrop, IslandActivity.DragInProgress, true)]
    [InlineData(IslandState.Panels, IslandActivity.None, true)]
    [InlineData(IslandState.Palette, IslandActivity.AiTask, true)]
    [InlineData(IslandState.Capture, IslandActivity.None, true)] // the island is hidden under the overlay, but an event waits
    public void AnIslandThatIsShowingSomethingIsBusyExceptForThePeekAndUndoPills(IslandState state, IslandActivity activity, bool busy) =>
        Assert.Equal(busy, TriggerQueue.IslandBusy(state, activity));

    [Fact]
    public void AnEventThatArrivesWhileTheIslandIsBusyWaitsAndIsNotLost()
    {
        var queue = new TriggerQueue();
        bool busy = TriggerQueue.IslandBusy(IslandState.Panels, IslandActivity.None);

        Assert.False(queue.Offer(At(0, "Timer done"), busy));

        // Still there when the panel closes.
        Assert.False(TriggerQueue.IslandBusy(IslandState.Hidden, IslandActivity.None));
        Assert.Equal(["Timer done"], queue.Release().Select(e => e.Text));
    }

    [Theory]
    [InlineData(false, true, IslandState.Hidden, IslandActivity.None, false)]
    [InlineData(true, true, IslandState.Hidden, IslandActivity.None, true)] // the tray pause holds events like Focus
    [InlineData(true, true, IslandState.Compact, IslandActivity.Peek, true)] // even where the peek pill would give way
    [InlineData(false, false, IslandState.Hidden, IslandActivity.None, true)] // Focus, Do Not Disturb, fullscreen
    [InlineData(false, true, IslandState.Panels, IslandActivity.None, true)]
    public void AnEventWaitsWhilePausedSuppressedOrBusy(bool paused, bool allowed, IslandState state, IslandActivity activity, bool waits) =>
        Assert.Equal(waits, TriggerQueue.Waits(paused, allowed, state, activity));

    [Theory]
    [InlineData(false)] // resumed from the tray
    [InlineData(true)] // the hour ran out: the tray's one-shot resumes too
    public void AnEventRaisedWhilePausedWaitsAndShowsWhenThePauseEnds(bool hourRanOut)
    {
        var now = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        var pause = new PauseState();
        var queue = new TriggerQueue();
        IReadOnlyList<IslandEvent> shown = [];
        // What the controller does when a pause ends: release, unless something else still holds events.
        pause.Ended += () =>
        {
            if (!TriggerQueue.Waits(pause.IsPaused(now), allowed: true, IslandState.Hidden, IslandActivity.None))
            {
                shown = queue.Release();
            }
        };

        pause.PauseFor(TimeSpan.FromHours(1), now);
        Assert.False(queue.Offer(At(0, "Timer done"), TriggerQueue.Waits(pause.IsPaused(now), allowed: true, IslandState.Hidden, IslandActivity.None)));
        // The tray's pause hides the island, and the hide's release must not take them out into the pause.
        Assert.True(TriggerQueue.Waits(pause.IsPaused(now), allowed: true, IslandState.Hidden, IslandActivity.None));
        Assert.Equal(1, queue.Count);

        if (hourRanOut)
        {
            now = now.AddHours(1);
        }

        pause.Resume();

        Assert.Equal(["Timer done"], shown.Select(e => e.Text));
        Assert.Equal(0, queue.Count);
    }
}
