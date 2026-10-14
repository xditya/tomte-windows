using System.Text.Json;
using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Device;
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
    public void TheWaitIsWorkedOutAgainFromTheAbsoluteEndAfterASleepOrAClockChange()
    {
        var timer = new IslandTimer(File) { Run = TimerRun.Starting(T0, TimeSpan.FromMinutes(10)) };
        Assert.Equal(TimeSpan.FromMinutes(10), timer.DueIn(T0));

        // Woke six minutes in: four left, whatever interval was set before the sleep.
        Assert.Equal(TimeSpan.FromMinutes(4), timer.DueIn(T0.AddMinutes(6)));
        Assert.False(timer.TryComplete(T0.AddMinutes(6)));

        // The clock was set back five minutes: the end has not moved, the wait is longer, an early fire completes nothing.
        Assert.Equal(TimeSpan.FromMinutes(15), timer.DueIn(T0.AddMinutes(-5)));
        Assert.False(timer.TryComplete(T0.AddMinutes(-5)));
        Assert.Equal(0, timer.Run!.Progress(T0.AddMinutes(-5)));

        // Slept, or the clock was set, past the end: due now, done once.
        Assert.Equal(TimeSpan.Zero, timer.DueIn(T0.AddHours(8)));
        Assert.Equal(1, timer.Run.Progress(T0.AddHours(8)));
        Assert.True(timer.TryComplete(T0.AddHours(8)));
        Assert.False(timer.TryComplete(T0.AddHours(8)));
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
    public void AFileThatCannotBeWrittenOrDeletedDoesNotStopTheTimer()
    {
        // The folder name is taken by a file, so the timer file cannot be created.
        Directory.CreateDirectory(dir);
        string blocker = Path.Combine(dir, "blocker");
        System.IO.File.WriteAllText(blocker, "");
        var unwritable = new IslandTimer(Path.Combine(blocker, "timer.json"));
        int changes = 0;
        unwritable.Changed += () => changes++;
        unwritable.Run = TimerRun.Starting(T0, TimeSpan.FromMinutes(1));
        Assert.NotNull(unwritable.Run);
        Assert.Equal(1, changes);

        // A read-only file cannot be deleted: the completion still happens, once, and is still announced.
        var timer = new IslandTimer(File) { Run = TimerRun.Starting(T0, TimeSpan.FromMinutes(1)) };
        timer.Changed += () => changes++;
        System.IO.File.SetAttributes(File, FileAttributes.ReadOnly);
        try
        {
            Assert.True(timer.TryComplete(T0.AddMinutes(2)));
            Assert.False(timer.TryComplete(T0.AddMinutes(2)));
            Assert.Null(timer.Run);
            Assert.Equal(2, changes);
        }
        finally
        {
            System.IO.File.SetAttributes(File, FileAttributes.Normal);
        }
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
    [InlineData("""{}""")]
    [InlineData("""{"minutes":"soon"}""")]
    [InlineData("""{"minutes":0}""")]
    [InlineData("""{"minutes":-3}""")]
    [InlineData("""{"minutes":0.01}""")]
    [InlineData("""{"minutes":1441}""")]
    [InlineData("""{"minutes":"NaN"}""")]
    [InlineData("""{"minutes":1e400}""")]
    [InlineData("""[5]""")]
    public void SetTimerRejectsBadMinutes(string json)
    {
        var timer = new IslandTimer(File);
        Assert.NotNull(BuiltInTools.Validate(BuiltInTools.SetTimer, JsonDocument.Parse(json).RootElement));
        var (result, undo) = Tools(timer).Run(Call(json));
        Assert.True(result.IsError);
        Assert.Null(undo);
        Assert.Null(timer.Run);
    }

    [Fact]
    public void SetTimerStartsAndItsUndoPutsBackThePreviousTimer()
    {
        // Small models send the number as text; that is taken, as for the device tools.
        Assert.Null(BuiltInTools.Validate(BuiltInTools.SetTimer, JsonDocument.Parse("""{"minutes":"0.5"}""").RootElement));
        Assert.Equal(ActionTier.AppLocalWrite, BuiltInTools.Tiers[BuiltInTools.SetTimer]);
        Assert.Equal("Set a timer for 25 minutes", BuiltInTools.Describe(BuiltInTools.SetTimer, JsonDocument.Parse("""{"minutes":25}""").RootElement));
        var timer = new IslandTimer(File);
        var tools = Tools(timer);

        var (first, cancel) = tools.Run(Call("""{"minutes":25}"""));
        Assert.False(first.IsError);
        var firstRun = timer.Run!;
        Assert.Equal(TimeSpan.FromMinutes(25), firstRun.Duration);

        var (_, restore) = tools.Run(Call("""{"minutes":5}"""));
        Assert.Equal(TimeSpan.FromMinutes(5), timer.Run!.Duration);
        Assert.Equal("The previous timer is back.", restore!().Message);
        Assert.Equal(firstRun, timer.Run);
        Assert.Equal("Timer cancelled.", cancel!().Message);
        Assert.Null(timer.Run);
        Assert.Null(new IslandTimer(File).Run);
    }

    [Fact]
    public void SetTimerWithoutATimerReportsAnError()
    {
        var (result, undo) = new BuiltInTools(new InMemoryShelf(), new InMemoryScratchpad(), _ => { }, _ => { }).Run(Call("""{"minutes":5}"""));
        Assert.True(result.IsError);
        Assert.Null(undo);
    }

    private static BuiltInTools Tools(IslandTimer timer) => new(new InMemoryShelf(), new InMemoryScratchpad(), _ => { }, _ => { }, timer: timer);

    private static ToolCallPart Call(string json) => new("id", BuiltInTools.SetTimer, JsonDocument.Parse(json).RootElement);

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
