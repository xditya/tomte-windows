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
    [InlineData(IslandState.Compact, IslandActivity.None, true)] // an edge push, the first-run hint, another event
    [InlineData(IslandState.Compact, IslandActivity.Rambling, true)]
    [InlineData(IslandState.ShelfDrop, IslandActivity.DragInProgress, true)]
    [InlineData(IslandState.Panels, IslandActivity.None, true)]
    [InlineData(IslandState.Palette, IslandActivity.AiTask, true)]
    [InlineData(IslandState.Capture, IslandActivity.None, true)] // the island is hidden under the overlay, but an event waits
    public void AnIslandThatIsShowingSomethingIsBusyExceptForThePeekPill(IslandState state, IslandActivity activity, bool busy) =>
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
}
