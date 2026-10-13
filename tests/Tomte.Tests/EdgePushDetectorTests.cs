using Tomte.Core.Triggers;
using Xunit;
using static Tomte.Core.Triggers.EdgePushDetector;

namespace Tomte.Tests;

public class EdgePushDetectorTests
{
    private static readonly TriggerSettings Spec = new();

    private static EdgePushDetector New() => new(Spec.PushCounts, Spec.PushWindowMs);

    [Fact]
    public void SustainedPushRevealsInsideTheWindow()
    {
        var d = New();
        Assert.Equal(Event.EnterZone, d.Feed(0, -5, atTopRow: true, inZone: true));

        // 20 counts per 100 ms reaches 60 well inside 400 ms.
        var e = Event.None;
        double t = 10;
        for (; t <= 300 && e == Event.None; t += 10)
        {
            e = d.Feed(t, -2, true, true);
        }

        Assert.Equal(Event.Reveal, e);
        Assert.True(d.WindowCounts >= Spec.PushCounts);
        Assert.InRange(t, 0, Spec.PushWindowMs);
    }

    [Fact]
    public void SlowDriftNeverFillsTheWindow()
    {
        var d = New();
        d.Feed(0, -1, true, true);
        for (double t = 20; t <= 1200; t += 20)
        {
            Assert.Equal(Event.None, d.Feed(t, -1, true, true));
        }

        Assert.True(d.WindowCounts < Spec.PushCounts);
    }

    [Fact]
    public void LeavingTheTopRowResetsTheCount()
    {
        var d = New();
        d.Feed(0, -20, true, true);
        d.Feed(10, -20, true, true);
        Assert.Equal(Event.LeaveZone, d.Feed(20, 5, atTopRow: false, inZone: true));
        Assert.Equal(Event.EnterZone, d.Feed(30, -25, true, true));
        Assert.Equal(Event.None, d.Feed(40, -25, true, true));
        Assert.Equal(50, d.WindowCounts);
    }

    [Fact]
    public void SidewaysAndDownwardMotionDoNotCount()
    {
        var d = New();
        d.Feed(0, 0, true, true);
        for (double t = 10; t <= 200; t += 10)
        {
            Assert.Equal(Event.None, d.Feed(t, 30, true, true));
        }

        Assert.Equal(0, d.WindowCounts);
    }

    [Fact]
    public void DwellRevealsOnceAndOnlyWhileStillPinned()
    {
        var d = New();
        Assert.Equal(Event.None, d.DwellElapsed());
        d.Feed(0, 0, true, true);
        Assert.Equal(Event.Reveal, d.DwellElapsed());
        Assert.Equal(Event.None, d.DwellElapsed());

        // A hard push right after the dwell reveal must not reveal a second time.
        Assert.Equal(Event.None, d.Feed(400, -100, true, true));

        // Only after the cursor leaves the top row can the next session start.
        Assert.Equal(Event.None, d.Feed(500, 10, false, true));
        Assert.Equal(Event.EnterZone, d.Feed(600, -10, true, true));
    }

    [Fact]
    public void OutsideTheZoneNeverStartsASession()
    {
        var d = New();
        Assert.Equal(Event.None, d.Feed(0, -50, atTopRow: true, inZone: false));
        Assert.Equal(Event.None, d.Feed(10, -50, atTopRow: false, inZone: true));
    }
}
