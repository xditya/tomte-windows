using Tomte.Core;
using Xunit;

namespace Tomte.Tests;

public sealed class TimerTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 9, 0, 0, TimeSpan.FromHours(2));
    private readonly string dir = Path.Combine(Path.GetTempPath(), "tomte-tests-" + Guid.NewGuid().ToString("N"));

    private string File => Path.Combine(dir, "timer.json");

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void EndTimeIsAbsoluteAndProgressMapsOntoTheRing()
    {
        var run = TimerRun.Starting(T0, TimeSpan.FromMinutes(10));
        Assert.Equal(T0.AddMinutes(10), run.EndsAt);
        Assert.Equal(0, run.Progress(T0));
        Assert.Equal(0.25, run.Progress(T0.AddMinutes(2.5)), 9);
        Assert.Equal(1, run.Progress(run.EndsAt));
        Assert.Equal(1, run.Progress(T0.AddHours(1)));
        Assert.Equal(0, run.Progress(T0.AddMinutes(-5))); // the clock was set back
        Assert.Equal(TimeSpan.FromMinutes(7.5), run.Remaining(T0.AddMinutes(2.5)));
        Assert.Equal(TimeSpan.Zero, run.Remaining(T0.AddHours(1)));
    }

    [Fact]
    public void ARestartResumesTheSameEndTime()
    {
        new IslandTimer(File).Run = TimerRun.Starting(T0, TimeSpan.FromMinutes(25));
        var resumed = new IslandTimer(File);
        Assert.Equal(T0.AddMinutes(25), resumed.Run!.EndsAt);
        Assert.Equal(TimeSpan.FromMinutes(15), resumed.DueIn(T0.AddMinutes(10)));
        Assert.False(resumed.TryComplete(T0.AddMinutes(10)));
    }

    [Fact]
    public void AnEndTimeThatPassedWhileTheAppWasDownFiresAtOnceAndOnce()
    {
        new IslandTimer(File).Run = TimerRun.Starting(T0, TimeSpan.FromMinutes(1));
        var later = T0.AddHours(3);
        var resumed = new IslandTimer(File);
        int changes = 0;
        resumed.Changed += () => changes++;
        Assert.Equal(TimeSpan.Zero, resumed.DueIn(later));
        Assert.True(resumed.TryComplete(later));
        Assert.False(resumed.TryComplete(later));
        Assert.Null(resumed.DueIn(later));
        Assert.Equal(1, changes);
        Assert.Null(new IslandTimer(File).Run);
    }

    [Fact]
    public void ANewTimerReplacesTheOldOneAndCancellingRemovesTheFile()
    {
        var timer = new IslandTimer(File);
        timer.Run = TimerRun.Starting(T0, TimeSpan.FromMinutes(25));
        timer.Run = TimerRun.Starting(T0, TimeSpan.FromMinutes(5));
        Assert.Equal(T0.AddMinutes(5), new IslandTimer(File).Run!.EndsAt);
        timer.Run = null;
        Assert.False(System.IO.File.Exists(File));
        Assert.Null(new IslandTimer(File).Run);
    }

    [Fact]
    public void NoFileOrADamagedFileMeansNoTimer()
    {
        Assert.Null(new IslandTimer(File).Run);
        Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(File, "{not json");
        Assert.Null(new IslandTimer(File).Run);
    }
}
