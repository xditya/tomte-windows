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
}
