using System.Text.Json;
using Tomte.Core.Jobs;
using Xunit;

namespace Tomte.Tests;

/// <summary>Reading a schedule from the model's call, saying it in plain words, and working out the next run.</summary>
public sealed class JobScheduleTests
{
    private static readonly TimeZoneInfo India = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");

    // US Eastern: in 2026 the clock springs forward on 8 March at 02:00 and falls back on 1 November at 02:00.
    private static readonly TimeZoneInfo Eastern = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");

    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    private static JobSchedule Read(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.True(JobSchedule.TryRead(doc.RootElement, out var schedule, out var error), error);
        return schedule!;
    }

    private static string Refused(string json)
    {
        using var doc = JsonDocument.Parse(json);
        Assert.False(JobSchedule.TryRead(doc.RootElement, out _, out var error));
        return error!;
    }

    private static JobSchedule Daily(int hour, int minute) => new() { Time = new TimeOnly(hour, minute), Days = Enum.GetValues<DayOfWeek>() };

    [Fact]
    public void DaysAreReadTheWaysSmallModelsSendThem()
    {
        DayOfWeek[] weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];
        Assert.Equal(weekdays, Read("""{"time":"09:00","days":["mon","tue","wed","thu","fri"]}""").Days);
        Assert.Equal(weekdays, Read("""{"time":"09:00","days":["weekdays"]}""").Days);
        Assert.Equal(weekdays, Read("""{"time":"09:00","days":"[\"monday\", \"tuesday\", \"wednesday\", \"thursday\", \"friday\"]"}""").Days);
        Assert.Equal([DayOfWeek.Monday, DayOfWeek.Friday], Read("""{"time":"09:00","days":"Fri, Mon"}""").Days);
        Assert.Equal(7, Read("""{"time":"09:00","days":["daily"]}""").Days.Count);
        // A date sent as a date and time takes its day; the time comes from "time".
        Assert.Equal(new DateOnly(2026, 10, 15), Read("""{"time":"08:30","date":"2026-10-15T08:30:00","days":[]}""").Date);
        Assert.Equal(new TimeOnly(21, 15), Read("""{"time":"9:15 PM","days":["sun"]}""").Time);
        Assert.Equal(new DateOnly(2026, 10, 15), Read("""{"time":"08:30","date":"2026-10-15","days":"null"}""").Date);
    }

    [Fact]
    public void ABadOrMissingScheduleSaysWhatToFix()
    {
        Assert.Equal("time is missing. Ask the user at what time the job should run.", Refused("""{"days":["mon"]}"""));
        Assert.Equal("time is missing. Ask the user at what time the job should run.", Refused("""{"time":"","days":["mon"]}"""));
        Assert.Equal("time must be a 24-hour time such as 09:00, not \"25:00\".", Refused("""{"time":"25:00","days":["mon"]}"""));
        Assert.StartsWith("Say when the job runs", Refused("""{"time":"09:00","date":"","days":"[]"}"""));
        Assert.Equal("Give days for a repeating job or date for a one-off, not both.", Refused("""{"time":"09:00","date":"2026-10-15","days":["mon"]}"""));
        Assert.Equal("days takes mon, tue, wed, thu, fri, sat or sun, not \"month\".", Refused("""{"time":"09:00","days":["month"]}"""));
        Assert.Equal("date must be a day such as 2026-10-15.", Refused("""{"time":"09:00","date":"Wednesday"}"""));
        Assert.Equal("Arguments must be a JSON object.", Refused("""["09:00"]"""));
    }

    [Fact]
    public void TheScheduleIsSaidInPlainWords()
    {
        Assert.Equal("Every weekday at 09:00", Read("""{"time":"9:00","days":["weekdays"]}""").Describe());
        Assert.Equal("Every day at 07:05", Read("""{"time":"07:05","days":["daily"]}""").Describe());
        Assert.Equal("Every Sunday at 21:00", Read("""{"time":"21:00","days":["sun"]}""").Describe());
        Assert.Equal("Every Monday, Wednesday and Friday at 18:30", Read("""{"time":"18:30","days":["fri","mon","wed"]}""").Describe());
        Assert.Equal("On Thursday 15 October 2026 at 08:30", Read("""{"time":"08:30","date":"2026-10-15"}""").Describe());
    }

    [Fact]
    public void StoringAndReadingBackKeepsTheSchedule()
    {
        var weekly = Read("""{"time":"09:00","days":["fri","mon"]}""");
        Assert.Equal("""{"time":"09:00","days":["mon","fri"]}""", weekly.ToJson());
        Assert.Equal(weekly.Days, JobSchedule.FromJson(weekly.ToJson()).Days);
        var once = JobSchedule.FromJson(Read("""{"time":"08:30","date":"2026-10-15"}""").ToJson());
        Assert.Equal((new DateOnly(2026, 10, 15), new TimeOnly(8, 30)), (once.Date, once.Time));
        Assert.Empty(once.Days);
    }

    [Fact]
    public void WeekdaysSkipTheWeekend()
    {
        var schedule = Read("""{"time":"09:00","days":["weekdays"]}""");
        var friday = new DateTimeOffset(2026, 10, 16, 10, 0, 0, Ist);
        Assert.Equal(new DateTimeOffset(2026, 10, 19, 9, 0, 0, Ist), schedule.Next(friday, India));
        // Before nine on a weekday it is the same day.
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 9, 0, 0, Ist), schedule.Next(new DateTimeOffset(2026, 10, 14, 8, 59, 0, Ist), India));
        // Exactly at nine the run is the one at nine: the next is strictly after.
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 9, 0, 0, Ist), schedule.Next(new DateTimeOffset(2026, 10, 14, 9, 0, 0, Ist), India));
    }

    [Fact]
    public void ARunEarlierTodayOnAOneDayScheduleIsNextWeek()
    {
        var schedule = Read("""{"time":"09:00","days":["wed"]}""");
        Assert.Equal(new DateTimeOffset(2026, 10, 21, 9, 0, 0, Ist), schedule.Next(new DateTimeOffset(2026, 10, 14, 13, 44, 0, Ist), India));
    }

    [Fact]
    public void AOneOffInThePastHasNoRun()
    {
        var once = Read("""{"time":"06:00","date":"2026-10-14"}""");
        Assert.Null(once.Next(new DateTimeOffset(2026, 10, 14, 13, 44, 0, Ist), India));
        Assert.Equal(new DateTimeOffset(2026, 10, 14, 6, 0, 0, Ist), once.Next(new DateTimeOffset(2026, 10, 14, 5, 0, 0, Ist), India));
    }

    [Fact]
    public void ATimeTheClockSkipsRunsWhenTheClockGetsPastTheGap()
    {
        // 02:30 does not exist on 8 March: the clock goes from 02:00 EST to 03:00 EDT.
        var schedule = Daily(2, 30);
        var next = schedule.Next(new DateTimeOffset(2026, 3, 7, 12, 0, 0, TimeSpan.FromHours(-5)), Eastern);
        Assert.Equal(new DateTimeOffset(2026, 3, 8, 3, 0, 0, TimeSpan.FromHours(-4)), next);
        // The day after, 02:30 is back, in summer time.
        Assert.Equal(new DateTimeOffset(2026, 3, 9, 2, 30, 0, TimeSpan.FromHours(-4)), schedule.Next(next!.Value, Eastern));
    }

    [Fact]
    public void ATimeTheClockPassesTwiceRunsOnce()
    {
        // 01:30 happens twice on 1 November: first in summer time, then again an hour later in winter time.
        var schedule = Daily(1, 30);
        var first = schedule.Next(new DateTimeOffset(2026, 10, 31, 12, 0, 0, TimeSpan.FromHours(-4)), Eastern);
        Assert.Equal(new DateTimeOffset(2026, 11, 1, 1, 30, 0, TimeSpan.FromHours(-4)), first);
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 1, 30, 0, TimeSpan.FromHours(-5)), schedule.Next(first!.Value, Eastern));
    }

    [Fact]
    public void NineStaysNineAcrossADaylightSavingChange()
    {
        var schedule = Read("""{"time":"09:00","days":["weekdays"]}""");
        var friday = new DateTimeOffset(2026, 10, 30, 10, 0, 0, TimeSpan.FromHours(-4));
        var monday = schedule.Next(friday, Eastern)!.Value;
        Assert.Equal(new DateTimeOffset(2026, 11, 2, 9, 0, 0, TimeSpan.FromHours(-5)), monday);
        Assert.Equal(14, monday.UtcDateTime.Hour);
    }
}
