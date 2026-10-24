using System.Text.Json;
using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Device;
using Tomte.Core.Jobs;
using Tomte.Core.Storage;
using Xunit;

namespace Tomte.Tests;

/// <summary>Reminders: reading them from typed words, storing and ringing them, and the model's set_reminder.</summary>
public sealed class ReminderTests : IDisposable
{
    private static readonly TimeZoneInfo India = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    // Thursday 22 October 2026, 14:00.
    private static readonly DateTimeOffset Now = new(2026, 10, 22, 14, 0, 0, Ist);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tomte-reminders-{Guid.NewGuid():N}");
    private readonly ReminderStore _store;

    public ReminderTests() => _store = new ReminderStore(new TomteDb(Path.Combine(_dir, "tomte.db")));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static DateTimeOffset At(int day, int hour, int minute = 0) => new(2026, 10, day, hour, minute, 0, Ist);

    [Theory]
    [InlineData("remind me in 20 minutes to check the oven", "check the oven", 22, 14, 20)]
    [InlineData("Remind me to check the oven in 20 min", "check the oven", 22, 14, 20)]
    [InlineData("remind me in an hour to stretch", "stretch", 22, 15, 0)]
    [InlineData("remind me in half an hour to stretch", "stretch", 22, 14, 30)]
    [InlineData("remind me in 2h to leave", "leave", 22, 16, 0)]
    [InlineData("remind me at 17:00 to call mum", "call mum", 22, 17, 0)]
    [InlineData("remind me at 5pm to call mum", "call mum", 22, 17, 0)]
    [InlineData("remind me at 5:30 pm to call mum", "call mum", 22, 17, 30)]
    [InlineData("Remind me: buy milk at 6 p.m.", "buy milk", 22, 18, 0)]
    // An hour with no am or pm is the first of its morning and its evening still ahead.
    [InlineData("remind me at 5 to call mum", "call mum", 22, 17, 0)]
    [InlineData("remind me at 9:15 to call mum", "call mum", 22, 21, 15)]
    // Written with two digits it is the 24-hour time, tomorrow once today's has passed.
    [InlineData("remind me at 09:15 to call mum", "call mum", 23, 9, 15)]
    [InlineData("remind me at 13:00 to eat", "eat", 23, 13, 0)]
    [InlineData("remind me tomorrow at 9 to pay rent", "pay rent", 23, 9, 0)]
    [InlineData("remind me to pay rent tomorrow at 9am", "pay rent", 23, 9, 0)]
    [InlineData("remind me at noon tomorrow to eat", "eat", 23, 12, 0)]
    [InlineData("remind me tonight at 8 to call dad", "call dad", 22, 20, 0)]
    [InlineData("remind me on friday at 10 to submit the form", "submit the form", 23, 10, 0)]
    [InlineData("remind me monday at 8:15 to call the bank", "call the bank", 26, 8, 15)]
    // Today's time has passed: next week's.
    [InlineData("remind me on thu at 10 to water the plants", "water the plants", 29, 10, 0)]
    public void ReadsAOneOff(string typed, string text, int day, int hour, int minute)
    {
        var request = ReminderParser.Parse(typed, Now, India);
        Assert.NotNull(request);
        Assert.Equal(text, request.Text);
        Assert.Equal(At(day, hour, minute), request.Due);
        Assert.Equal(DateOnly.FromDateTime(At(day, hour).DateTime), request.Schedule.Date);
        Assert.Equal(new TimeOnly(hour, minute), request.Schedule.Time);
    }

    [Theory]
    [InlineData("every weekday at 09:30 remind me standup", "standup", "Every weekday at 09:30", 23, 9, 30)]
    [InlineData("remind me every day at 10 to stretch", "stretch", "Every day at 10:00", 23, 10, 0)]
    [InlineData("remind me daily at 7pm to take the pills", "take the pills", "Every day at 19:00", 22, 19, 0)]
    [InlineData("remind me every monday and wednesday at 18:00 to run", "run", "Every Monday and Wednesday at 18:00", 26, 18, 0)]
    [InlineData("Every Thursday at 15:00, remind me to send the report", "send the report", "Every Thursday at 15:00", 22, 15, 0)]
    public void ReadsARepeatingOne(string typed, string text, string described, int day, int hour, int minute)
    {
        var request = ReminderParser.Parse(typed, Now, India);
        Assert.NotNull(request);
        Assert.Equal(text, request.Text);
        Assert.Equal(described, request.Schedule.Describe());
        Assert.Null(request.Schedule.Date);
        Assert.Equal(At(day, hour, minute), request.Due);
    }

    [Theory]
    [InlineData("remind me to check the oven")]               // no time
    [InlineData("remind me in 20 minutes")]                   // nothing to say
    [InlineData("remind me tomorrow to pay rent")]            // a day with no time
    [InlineData("remind me in 20 minutes tomorrow to leave")] // two times
    [InlineData("remind me today at 09:00 to leave")]          // passed
    [InlineData("remind me at 25:00 to leave")]
    [InlineData("remind me in 0 minutes to leave")]
    [InlineData("set a timer for 5 minutes")]
    [InlineData("what is the time in 20 minutes")]
    public void LeavesWhatItCannotReadForTheModel(string typed) => Assert.Null(ReminderParser.Parse(typed, Now, India));

    [Fact]
    public void KnowsWordsMeantAsAReminder()
    {
        Assert.True(ReminderParser.IsReminder("Remind me to call mum sometime"));
        Assert.False(ReminderParser.IsReminder("reminders"));
    }

    [Fact]
    public void StoresAndReadsBack()
    {
        var weekdays = ReminderParser.Parse("every weekday at 09:30 remind me standup", Now, India)!;
        var added = _store.Add(weekdays.Text, weekdays.Schedule, weekdays.Due, Now);
        var back = Assert.Single(_store.All());
        Assert.Equal(added.Id, back.Id);
        Assert.Equal("standup", back.Text);
        Assert.Equal(weekdays.Schedule.ToJson(), back.Schedule.ToJson());
        Assert.Equal(At(23, 9, 30), back.Due);
        Assert.Equal(Now, back.Created);
        Assert.Null(back.Ringing);
        Assert.Equal("Every weekday at 09:30. Next Friday 23 October at 09:30.", back.Describe(India));
        Assert.True(_store.Remove(added.Id));
        Assert.Empty(_store.All());
    }

    [Fact]
    public void AOneOffRingsUntilDismissedAndIsThenGone()
    {
        var oven = ReminderParser.Parse("remind me in 20 minutes to check the oven", Now, India)!;
        _store.Add(oven.Text, oven.Schedule, oven.Due, Now);
        Assert.Equal(At(22, 14, 20), _store.NextDue());
        Assert.Empty(_store.Ring(At(22, 14, 19), India));

        var rung = Assert.Single(_store.Ring(At(22, 14, 20), India));
        Assert.Equal(At(22, 14, 20), rung.Ringing);
        Assert.Null(rung.Due);
        Assert.Equal("check the oven", rung.Line(At(22, 14, 20), India));
        Assert.Null(_store.NextDue());
        // A start before it was dismissed rings it again, now missed.
        var again = Assert.Single(_store.Ring(At(22, 16, 0), India));
        Assert.Equal("Missed at 14:20: check the oven", again.Line(At(22, 16, 0), India));

        _store.Dismiss(rung.Id);
        Assert.Empty(_store.Ring(At(22, 16, 0), India));
        Assert.Empty(_store.All());
    }

    [Fact]
    public void ARepeatingOneMovesOnAndRingsOnceForEveryTimeItMissed()
    {
        var standup = ReminderParser.Parse("every day at 09:30 remind me standup", Now, India)!;
        var added = _store.Add(standup.Text, standup.Schedule, standup.Due, Now);
        // Tomte was closed from Thursday to Sunday afternoon: Sunday's time rings, once, as missed.
        var rung = Assert.Single(_store.Ring(At(25, 15, 0), India));
        Assert.Equal(At(25, 9, 30), rung.Ringing);
        Assert.Equal(At(26, 9, 30), rung.Due);
        Assert.Equal("Missed at 09:30: standup", rung.Line(At(25, 15, 0), India));
        Assert.Equal("Missed Sun at 09:30: standup", rung.Line(At(26, 8, 0), India));

        _store.Dismiss(added.Id);
        var kept = Assert.Single(_store.All());
        Assert.Null(kept.Ringing);
        Assert.Equal(At(26, 9, 30), kept.Due);
    }

    [Fact]
    public void SnoozeRingsTheTextAgainTenMinutesOn()
    {
        var standup = ReminderParser.Parse("every weekday at 09:30 remind me standup", Now, India)!;
        var added = _store.Add(standup.Text, standup.Schedule, standup.Due, Now);
        _store.Ring(At(23, 9, 30), India);

        var snoozed = _store.Snooze(added.Id, At(23, 9, 31), India)!;
        Assert.Equal(At(23, 9, 41), snoozed.Due);
        Assert.Equal("standup", snoozed.Text);
        // The repeating one keeps its next time, not ringing any more.
        Assert.Equal([At(23, 9, 41), At(26, 9, 30)], _store.All().Select(r => r.Due!.Value));
        Assert.Empty(_store.Ring(At(23, 9, 40), India));
        Assert.Equal(snoozed.Id, Assert.Single(_store.Ring(At(23, 9, 41), India)).Id);
        Assert.Null(_store.Snooze(999, Now, India));
    }

    [Fact]
    public void TheFileBesideTheDatabaseSaysWhenToLook()
    {
        Assert.Null(JobStore.ReadMarker(_store.MarkerPath));
        var oven = ReminderParser.Parse("remind me in 20 minutes to check the oven", Now, India)!;
        var added = _store.Add(oven.Text, oven.Schedule, oven.Due, Now);
        Assert.Equal(At(22, 14, 20), JobStore.ReadMarker(_store.MarkerPath));
        // While it rings, a start looks at once.
        _store.Ring(At(22, 14, 20), India);
        Assert.Equal(DateTimeOffset.UnixEpoch, JobStore.ReadMarker(_store.MarkerPath));
        _store.Dismiss(added.Id);
        Assert.Null(JobStore.ReadMarker(_store.MarkerPath));
    }

    [Theory]
    [InlineData("""{"text":"standup","time":"09:30","days":["mon","tue","wed","thu","fri"]}""")]
    [InlineData("""{"text":"standup","time":"09:30","days":"mon, tue"}""")]
    [InlineData("""{"text":"pay rent","time":"09:00","date":"2026-11-01","days":null}""")]
    [InlineData("""{"text":"pay rent","time":"09:00","date":"2026-11-01","days":[]}""")]
    public void SetReminderTakesGoodArguments(string json) =>
        Assert.Null(BuiltInTools.Validate(BuiltInTools.SetReminder, JsonDocument.Parse(json).RootElement));

    [Theory]
    [InlineData("""{"time":"09:30","days":["mon"]}""", "set_reminder needs a non-empty string argument \"text\".")]
    [InlineData("""{"text":"","time":"09:30","days":["mon"]}""", "set_reminder: \"text\" must be a non-empty string.")]
    [InlineData("""{"text":"x","days":["mon"]}""", "set_reminder needs a non-empty string argument \"time\".")]
    [InlineData("""{"text":"x","time":"half nine","days":["mon"]}""", "time must be a 24-hour time such as 09:00, not \"half nine\".")]
    [InlineData("""{"text":"x","time":"09:30"}""", "Say when the job runs: days for a repeating job, or date for a one-off. Ask the user when that is unclear.")]
    [InlineData("""{"text":"x","time":"09:30","days":["mon"],"date":"2026-11-01"}""", "Give days for a repeating job or date for a one-off, not both.")]
    [InlineData("""{"text":"x","time":"09:30","days":["someday"]}""", "days takes mon, tue, wed, thu, fri, sat or sun, not \"someday\".")]
    [InlineData("""{"text":"x","time":"09:30","days":5}""", "set_reminder: \"days\" must be a list.")]
    public void SetReminderRefusesBadArguments(string json, string error) =>
        Assert.Equal(error, BuiltInTools.Validate(BuiltInTools.SetReminder, JsonDocument.Parse(json).RootElement));

    [Fact]
    public void SetReminderRefusesTextTooLongToShow()
    {
        string json = JsonSerializer.Serialize(new { text = new string('a', ReminderStore.MaxText + 1), time = "09:00", date = "2026-11-01" });
        Assert.Equal("text must be at most 200 characters.", BuiltInTools.Validate(BuiltInTools.SetReminder, JsonDocument.Parse(json).RootElement));
    }

    [Fact]
    public void SetReminderStoresThroughTheShellAndSaysWhenItShows()
    {
        var set = new List<(string Text, string Schedule)>();
        var tools = new BuiltInTools(new InMemoryShelf(), new InMemoryScratchpad(), _ => { }, _ => { })
        {
            Reminders = (text, schedule) =>
            {
                set.Add((text, schedule.Describe()));
                return schedule.Date is null ? new DateTimeOffset(2026, 10, 23, 9, 30, 0, TimeSpan.Zero) : null;
            },
        };

        var (done, undo) = tools.Run(Call("""{"text":" standup ","time":"09:30","days":["weekdays"]}"""));
        Assert.False(done.IsError, done.Content);
        Assert.StartsWith("Reminder set; it first shows Friday 23 October at ", done.Content);
        Assert.Null(undo);
        Assert.Equal([("standup", "Every weekday at 09:30")], set);
        Assert.Equal("Remind you \"standup\": Every weekday at 09:30", BuiltInTools.Describe(BuiltInTools.SetReminder, Call("""{"text":"standup","time":"09:30","days":["weekdays"]}""").Input));

        var (passed, _) = tools.Run(Call("""{"text":"pay rent","time":"09:00","date":"2026-01-01"}"""));
        Assert.True(passed.IsError);
        Assert.Equal("That time has already passed. Ask the user for a time in the future.", passed.Content);

        var (missing, _) = new BuiltInTools(new InMemoryShelf(), new InMemoryScratchpad(), _ => { }, _ => { }).Run(Call("""{"text":"x","time":"09:00","days":["mon"]}"""));
        Assert.Equal("Reminders are not available here.", missing.Content);
    }

    [Fact]
    public void SetReminderOffersItsDaysAsAListOfStringsAndRunsOnThePool()
    {
        var schema = BuiltInTools.Definitions.Single(d => d.Name == BuiltInTools.SetReminder).InputSchema;
        var days = schema.GetProperty("properties").GetProperty("days");
        Assert.Equal("array", days.GetProperty("type").GetString());
        Assert.Equal("string", days.GetProperty("items").GetProperty("type").GetString());
        Assert.Equal(["text", "time"], schema.GetProperty("required").EnumerateArray().Select(e => e.GetString()));
        Assert.Equal(ActionTier.AppLocalWrite, BuiltInTools.Tiers[BuiltInTools.SetReminder]);
        Assert.True(BuiltInTools.RunsOffThread(BuiltInTools.SetReminder));
    }

    private static ToolCallPart Call(string json) => new("id", BuiltInTools.SetReminder, JsonDocument.Parse(json).RootElement);
}
