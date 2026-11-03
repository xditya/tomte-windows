using System.Numerics;
using Tomte.Core;
using Tomte.Core.Jobs;
using Tomte.Core.Storage;
using Tomte.Core.Text;
using Xunit;

namespace Tomte.Tests;

/// <summary>
/// The Jobs tab: when it is on the strip, what its rows say with a fixed clock and zone, how the rows fit the narrowest
/// panel, the setting behind it, and the store's read of what runs and how each job last went.
/// </summary>
public sealed class JobsTabTests : IDisposable
{
    private static readonly TimeZoneInfo India = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    // Wednesday 21 October 2026, 20:42 in India.
    private static readonly DateTimeOffset Now = new(2026, 10, 21, 20, 42, 0, Ist);
    private static readonly TimeSpan Ahead = TimeSpan.FromMinutes(30);
    private static readonly ProviderConfig Ollama = new("ollama", ProviderKind.OpenAICompatible, "http://127.0.0.1:11434/v1", "llama3.2:3b");

    private readonly string dir = Path.Combine(Path.GetTempPath(), $"tomte-jobs-tab-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static JobSchedule Daily(int hour, int minute = 0) => new()
    {
        Time = new TimeOnly(hour, minute),
        Days = [.. Enum.GetValues<DayOfWeek>()],
    };

    private static JobStatus Job(long id, DateTimeOffset? next, bool running = false, JobRunRecord? last = null, string name = "Weekly report") =>
        new(new ScheduledJob(id, name, "Write it", Daily(21), [], Ollama, next), last, running);

    private static JobRunRecord Run(long id, DateTimeOffset finished, bool ok, string text = "Sent.") => new(id, "Weekly report", finished.AddMinutes(-1), finished, ok, text, "");

    [Fact]
    public void TheTabShowsWhileAJobRunsOrIsDueWithinTheTime()
    {
        Assert.False(JobsPanelLayout.Shows([], Now, Ahead));
        Assert.True(JobsPanelLayout.Shows([Job(1, Now.AddMinutes(30))], Now, Ahead));
        Assert.False(JobsPanelLayout.Shows([Job(1, Now.AddMinutes(31))], Now, Ahead));
        Assert.True(JobsPanelLayout.Shows([Job(1, Now.AddMinutes(31)), Job(2, Now.AddHours(5), running: true)], Now, Ahead));
        // A run due and not yet taken still counts; a one-off that has run has no next run and does not.
        Assert.True(JobsPanelLayout.Shows([Job(1, Now.AddMinutes(-1))], Now, Ahead));
        Assert.False(JobsPanelLayout.Shows([Job(1, null, last: Run(1, Now.AddHours(-1), ok: true))], Now, Ahead));
        // A longer time from Settings.
        Assert.True(JobsPanelLayout.Shows([Job(1, Now.AddMinutes(31))], Now, TimeSpan.FromHours(1)));
    }

    [Fact]
    public void TheOneShotIsForTheNextRunLessTheTime()
    {
        Assert.Equal(Now.AddMinutes(10), JobsPanelLayout.ShowsAt(Now.AddMinutes(40), Now, Ahead));
        Assert.Null(JobsPanelLayout.ShowsAt(Now.AddMinutes(30), Now, Ahead));
        Assert.Null(JobsPanelLayout.ShowsAt(null, Now, Ahead));
    }

    [Fact]
    public void RowsSayTheNextRunAndTheLastResultInWords()
    {
        Assert.Equal("21:00 · in 18 min", JobsPanelLayout.When(Now.AddMinutes(18), Now, India));
        Assert.Equal("20:42 · in 1 min", JobsPanelLayout.When(Now.AddSeconds(10), Now, India));
        Assert.Equal("21:41 · in 59 min", JobsPanelLayout.When(Now.AddMinutes(59), Now, India));
        Assert.Equal("23:42 · in 3 h", JobsPanelLayout.When(Now.AddHours(3), Now, India));
        Assert.Equal("Thu 09:00 · in 12 h", JobsPanelLayout.When(new DateTimeOffset(2026, 10, 22, 9, 0, 0, Ist), Now, India));
        Assert.Equal("Sat 09:00 · in 2 days", JobsPanelLayout.When(new DateTimeOffset(2026, 10, 24, 9, 0, 0, Ist), Now, India));
        Assert.Equal("2 Nov 09:00 · in 11 days", JobsPanelLayout.When(new DateTimeOffset(2026, 11, 2, 9, 0, 0, Ist), Now, India));
        Assert.Equal("20:41 · now", JobsPanelLayout.When(Now.AddMinutes(-1), Now, India));
        Assert.Equal("No run left", JobsPanelLayout.When(null, Now, India));

        // The same instant read in the zone it is shown in.
        Assert.Equal("11:30 · in 18 min", JobsPanelLayout.When(Now.AddMinutes(18), Now, TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time")));

        Assert.Equal("Not run yet", JobsPanelLayout.Last(Job(1, null), India, Now));
        Assert.Equal("Done 09:00", JobsPanelLayout.Last(Job(1, null, last: Run(1, new DateTimeOffset(2026, 10, 21, 9, 0, 0, Ist), ok: true)), India, Now));
        Assert.Equal("Failed Tue 09:00", JobsPanelLayout.Last(Job(1, null, last: Run(1, new DateTimeOffset(2026, 10, 20, 9, 0, 0, Ist), ok: false)), India, Now));
        var missed = new JobRunRecord(1, "Weekly report", new DateTimeOffset(2026, 10, 19, 9, 0, 0, Ist), Now, false, JobStore.Missed, "");
        Assert.Equal("Missed Mon 09:00", JobsPanelLayout.Last(Job(1, null, last: missed), India, Now));

        Assert.Equal("Every day at 21:00", JobsPanelLayout.Second(Job(1, Now), "Opened Spotify."));
        Assert.Equal("Running", JobsPanelLayout.Second(Job(1, Now, running: true), null));
        Assert.Equal("Running · Opened Spotify.", JobsPanelLayout.Second(Job(1, Now, running: true), "Opened\nSpotify."));
        Assert.Equal("This job has not run yet.", JobsPanelLayout.ResultText(null));
        Assert.Equal("Sent.", JobsPanelLayout.ResultText(Run(1, Now, ok: true, " Sent.\n")));
    }

    [Fact]
    public void RunningJobsComeFirstThenTheSoonestRun()
    {
        var order = JobsPanelLayout.Order([Job(1, null), Job(2, Now.AddHours(3)), Job(3, Now.AddHours(9), running: true), Job(4, Now.AddMinutes(5))]);
        Assert.Equal([3L, 4, 2, 1], order.Select(j => j.Job.Id));
    }

    [Fact]
    public void FourRowsFitAndTheListMovesARowAtATime()
    {
        var body = new Vector2((float)PanelSize.Jobs.W, JobsPanelLayout.Height);
        Assert.Equal((760, PanelSpace.Head + JobsPanelLayout.Height), PanelSize.Of(PanelId.Jobs));
        var jobs = Enumerable.Range(1, 6).Select(i => Job(i, Now.AddHours(i))).ToArray();
        var layout = new JobsPanelLayout(body, jobs);
        Assert.Equal(4, layout.Rows.Count(r => r.Height > 0));
        Assert.True(layout.Rows.Where(r => r.Height > 0).All(r => r.Bottom <= layout.List.Bottom));
        Assert.True(layout.List.Bottom < layout.Foot.Y);
        Assert.Equal(2, layout.Scrolled(5));
        Assert.Equal(0, layout.Scrolled(-1));
        Assert.Equal(2, layout.Reveal(5));
        // Out of view, a row keeps a place with no height, so focus reaches it with the arrow keys.
        Assert.Equal(0, layout.Targets(asking: false)[5].Bounds.Height);

        // Opened, a row grows by its result and Remove, under its own lines; it ends in the list.
        var opened = new JobsPanelLayout(body, jobs, opened: 1);
        Assert.True(opened.Rows[0].Height > JobsPanelLayout.RowHeight);
        var remove = opened.RemoveButton(asking: false);
        Assert.True(remove.Y > opened.ResultAt.Y && remove.Bottom < opened.Rows[0].Bottom && opened.Rows[0].Bottom <= opened.List.Bottom);
        Assert.Contains(opened.Targets(asking: true), t => t.Id == JobsPanelLayout.RemoveId && t.Bounds.Width > remove.Width);
    }

    [Fact]
    public void AtTheNarrowestPanelEveryLineKeepsItsRoom()
    {
        // 60% of an 800 x 600 screen: the narrowest panel.
        var body = new Vector2(480, JobsPanelLayout.Height);
        var job = Job(1, new DateTimeOffset(2026, 11, 22, 9, 0, 0, Ist), last: Run(1, new DateTimeOffset(2026, 10, 14, 9, 0, 0, Ist), ok: false), name: "Send the weekly status report to the whole team");
        var layout = new JobsPanelLayout(body, [job]);
        var row = layout.Rows[0];
        var lines = JobsPanelLayout.RowText(job, row, Now, India);
        Assert.All(lines, l => Assert.True(l.Slot.X >= row.X && l.Slot.Right <= JobsPanelLayout.ChevronIn(row).X && l.Slot.Width > 100));
        // The next run and the last result fit their column whole; the name is the part that gives way.
        foreach (var line in lines.Skip(2))
        {
            Assert.True(TextSlot.Measure(line.Text, line.Style, 1000) <= line.Slot.Width, line.Text);
        }

        Assert.False(lines[0].Slot.IntersectsWith(lines[2].Slot));
        var head = layout.HeadText();
        Assert.True(TextSlot.Measure(head[1].Text, head[1].Style, 1000) <= head[1].Slot.Width);
        var foot = layout.FootText(TimeSpan.FromMinutes(240));
        Assert.True(TextSlot.Measure(foot.Text, foot.Style, 1000) <= foot.Slot.Width, foot.Text);
    }

    [Fact]
    public void TheTimeIsHalfAnHourUnlessChangedHeldToItsRangeAndKept()
    {
        Assert.Equal(30, new TomteSettings().JobsAheadMinutes);
        Assert.Equal(TimeSpan.FromMinutes(5), new TomteSettings { JobsAheadMinutes = 0 }.JobsAhead);
        Assert.Equal(TimeSpan.FromMinutes(240), new TomteSettings { JobsAheadMinutes = 10000 }.JobsAhead);
        string file = Path.Combine(dir, "settings.json");
        Directory.CreateDirectory(dir);
        using (var store = new SettingsStore(file))
        {
            Assert.Equal(TimeSpan.FromMinutes(30), store.Current.JobsAhead);
            store.Save(store.Current with { JobsAheadMinutes = 90 });
        }

        Assert.Equal(TimeSpan.FromMinutes(90), SettingsStore.Load(file).JobsAhead);
    }

    [Fact]
    public void TheStoreSaysWhatRunsAndHowEachJobLastWent()
    {
        var store = new JobStore(new TomteDb(Path.Combine(dir, "tomte.db")));
        var at = new DateTimeOffset(2026, 10, 21, 21, 0, 0, Ist);
        var one = store.Add(new JobDraft("Weekly report", "Write it", Daily(21), []), Ollama, Now, India);
        var two = store.Add(new JobDraft("Backup", "Back up", Daily(23), []), Ollama, Now, India);
        Assert.All(store.Statuses(Now), s => Assert.False(s.Running || s.Last is not null));

        var claim = Assert.Single(store.ClaimDue(at, India));
        var running = store.Statuses(at);
        Assert.True(running.Single(s => s.Job.Id == one.Id).Running);
        Assert.False(running.Single(s => s.Job.Id == two.Id).Running);
        // A claim past its limit is a run that did not finish, not one that runs.
        Assert.False(store.Statuses(at + JobStore.RunLimit).Single(s => s.Job.Id == one.Id).Running);

        store.Record(new JobRunRecord(one.Id, one.Name, claim.Due, at.AddMinutes(1), false, "No answer.", ""));
        store.Record(new JobRunRecord(one.Id, one.Name, claim.Due, at.AddMinutes(2), true, "Sent.", ""));
        var after = store.Statuses(at.AddMinutes(3)).Single(s => s.Job.Id == one.Id);
        Assert.False(after.Running);
        Assert.Equal("Sent.", after.Last?.Text);
        Assert.Null(store.Statuses(at.AddMinutes(3)).Single(s => s.Job.Id == two.Id).Last);
    }

    [Fact]
    public void AOneOffThatRanIsDoneAndLeavesTheTab()
    {
        var store = new JobStore(new TomteDb(Path.Combine(dir, "tomte.db")));
        var at = new DateTimeOffset(2026, 10, 21, 21, 0, 0, Ist);
        var once = store.Add(new JobDraft("Sub count", "Send it", new JobSchedule { Time = new TimeOnly(21, 0), Date = new DateOnly(2026, 10, 21) }, []), Ollama, Now, India);

        var claim = Assert.Single(store.ClaimDue(at, India));
        Assert.True(Assert.Single(store.Statuses(at)).Running);

        store.Record(new JobRunRecord(once.Id, once.Name, claim.Due, at.AddMinutes(1), true, "Sent.", ""));
        Assert.Empty(store.Statuses(at.AddMinutes(2)));
    }

    [Fact]
    public void TheJobsTabIsNotOnTheStripUntilItIsShown()
    {
        var tabs = new PanelTabs();
        foreach (var id in Enum.GetValues<PanelId>())
        {
            tabs.Register(id);
        }

        tabs.Show(PanelId.Jobs, false);
        Assert.DoesNotContain(PanelId.Jobs, tabs.Order);
        tabs.Show(PanelId.Jobs, true);
        Assert.True(tabs.Select(PanelId.Jobs));
        Assert.Equal("Jobs", TabStrip.Name(PanelId.Jobs));
    }
}
