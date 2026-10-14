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
        string[] damaged =
        [
            "{not json",
            "null",
            "{}", // parses to a run that ended in year 1
            """{"StartedAt":"2026-10-14T09:00:00+02:00","EndsAt":"2026-10-14T09:00:00+02:00"}""", // no length
            """{"StartedAt":"2026-10-14T09:10:00+02:00","EndsAt":"2026-10-14T09:00:00+02:00"}""", // ends before it starts
            """{"StartedAt":"2026-10-14T09:00:00+02:00","EndsAt":"2031-01-01T00:00:00+02:00"}""", // years away
            """{"StartedAt":"yesterday","EndsAt":"tomorrow"}""",
        ];
        foreach (string text in damaged)
        {
            System.IO.File.WriteAllText(File, text);
            var timer = new IslandTimer(File);
            Assert.Null(timer.Run);
            Assert.Null(timer.DueIn(T0));
            Assert.False(timer.TryComplete(T0.AddYears(10)));
        }

        // The longest timer the app starts is still resumed.
        new IslandTimer(File).Run = TimerRun.Starting(T0, TimerRun.MaxDuration);
        Assert.NotNull(new IslandTimer(File).Run);
    }

    [Theory]
    [InlineData("25", 25 * 60)]
    [InlineData("0.5", 30)]
    [InlineData("1440", 24 * 3600)]
    public void MinutesAreParsedWithinOneSecondToOneDay(string text, double seconds)
    {
        Assert.True(TimerRun.TryParseMinutes(text, out var duration));
        Assert.Equal(seconds, duration.TotalSeconds, 6);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("--debug-log=x")]
    [InlineData("0")]
    [InlineData("-3")]
    [InlineData("0.01")]
    [InlineData("1441")]
    [InlineData("1e400")]
    [InlineData("NaN")]
    [InlineData("Infinity")]
    [InlineData("1,5")]
    public void MinutesOutsideTheRangeOrNotANumberAreRejected(string? text) =>
        Assert.False(TimerRun.TryParseMinutes(text, out _));
}
