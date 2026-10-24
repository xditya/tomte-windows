using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Jobs;
using Tomte.Core.Text;

namespace TextCaptures;

/// <summary>The Jobs tab, from the panel's own layout: jobs coming up, one running, one failed and opened, dark and light.</summary>
internal static partial class Program
{
    private static readonly DateTimeOffset JobsNow = new(DateTime.Today.AddHours(20).AddMinutes(42));
    private static readonly ProviderConfig JobProvider = new("ollama", ProviderKind.OpenAICompatible, "http://127.0.0.1:11434/v1", "llama3.2:3b");

    private static JobStatus JobRow(long id, string name, JobSchedule schedule, DateTimeOffset? next, bool running = false, JobRunRecord? last = null) =>
        new(new ScheduledJob(id, name, "", schedule, [], JobProvider, next), last, running);

    private static JobSchedule Days(int hour, int minute, params DayOfWeek[] days) =>
        new() { Time = new TimeOnly(hour, minute), Days = days.Length == 0 ? [.. Enum.GetValues<DayOfWeek>()] : days };

    private static JobRunRecord JobRun(long id, DateTimeOffset finished, bool ok, string text) => new(id, "", finished.AddMinutes(-1), finished, ok, text, "");

    private static readonly DayOfWeek[] Weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    private static JobStatus[] UpcomingJobs =>
    [
        JobRow(1, "Weekly report", Days(21, 0, DayOfWeek.Friday, DayOfWeek.Wednesday), JobsNow.AddMinutes(18), last: JobRun(1, JobsNow.AddDays(-7), ok: true, "Sent the report.")),
        JobRow(2, "Back up the notes", Days(23, 30), JobsNow.AddHours(2).AddMinutes(48), last: JobRun(2, JobsNow.AddDays(-1).AddHours(2).AddMinutes(49), ok: true, "Done.")),
        JobRow(3, "Morning summary of what is due today, from notes.txt", Days(9, 0, Weekdays), JobsNow.Date.AddDays(1).AddHours(9), last: JobRun(3, JobsNow.Date.AddHours(9), ok: true, "Two things are due.")),
        JobRow(4, "Check the train times", new JobSchedule { Time = new TimeOnly(7, 15), Date = DateOnly.FromDateTime(JobsNow.Date.AddDays(9)) }, JobsNow.Date.AddDays(9).AddHours(7).AddMinutes(15)),
    ];

    private static JobStatus[] RunningJobs =>
    [
        JobRow(1, "Weekly report", Days(20, 40, DayOfWeek.Friday, DayOfWeek.Wednesday), JobsNow.AddDays(2).AddMinutes(-2), running: true, last: JobRun(1, JobsNow.AddDays(-5), ok: true, "Sent the report.")),
        .. UpcomingJobs.Skip(1),
    ];

    private const string FailedText = "Your PC was locked, so the job could not use Outlook. Unlock it before 09:00, or move the job to a time you are at your desk.";

    private static JobStatus[] FailedJobs =>
    [
        UpcomingJobs[0],
        JobRow(5, "Send the invoice reminder", Days(9, 0, Weekdays), JobsNow.Date.AddDays(1).AddHours(9), last: JobRun(5, JobsNow.Date.AddHours(9).AddMinutes(1), ok: false, FailedText)),
        .. UpcomingJobs.Skip(1),
    ];

    private static List<Scene> JobsScenes() =>
    [
        .. Schemes("jobs-upcoming", PanelSize.Jobs, UpcomingJobs, null),
        .. Schemes("jobs-running", PanelSize.Jobs, RunningJobs, null, step: "Opened Outlook."),
        .. Schemes("jobs-failed", PanelSize.Jobs, FailedJobs, null),
        .. Schemes("jobs-failed-opened", PanelSize.Jobs, FailedJobs, 5),
        .. Schemes("jobs-remove-asking", PanelSize.Jobs, FailedJobs, 5, asking: true),
        // 60% of an 800 x 600 screen: the narrowest a panel gets.
        .. Schemes("jobs-narrow-running", (480, PanelSize.Jobs.H), RunningJobs, null, step: "Opened Outlook."),
        .. Schemes("jobs-narrow-failed-opened", (480, PanelSize.Jobs.H), FailedJobs, 5),
    ];

    private static IEnumerable<Scene> Schemes(string name, (double W, double H) size, JobStatus[] jobs, long? opened, string? step = null, bool asking = false) =>
        new[] { ThemeScheme.Dark, ThemeScheme.Light }.Select(scheme => new Scene($"{name}-{scheme.ToString().ToLowerInvariant()}", Panel(size), c =>
        {
            Theme.UseScheme(scheme);
            try
            {
                JobsTab(c, size, jobs, opened, step, asking, scheme);
            }
            finally
            {
                Theme.UseScheme(ThemeScheme.Dark);
            }
        }));

    // The Jobs tab as JobsPanel draws it: the heading, each row's card (ringed in the accent while it runs), icon, lines and
    // chevron, the opened row's result and Remove, and the line under the list.
    private static void JobsTab(Canvas c, (double W, double H) size, JobStatus[] jobs, long? opened, string? step, bool asking, ThemeScheme scheme)
    {
        var layout = new JobsPanelLayout(Body(size), JobsPanelLayout.Order(jobs), opened);
        // As a click opens it: the list moves to show the opened row.
        layout = new JobsPanelLayout(Body(size), layout.Jobs, opened, layout.Opened < 0 ? 0 : layout.Reveal(layout.Opened));
        var body = BodyOrigin(size);
        c.Pill();
        if (scheme != ThemeScheme.Dark)
        {
            // The canvas's pill is the dark one; the scheme's own surface goes over it, near enough to its outline.
            c.RoundRect(new RectangleF((float)(-size.W / 2), 0, (float)size.W, (float)size.H), (float)IslandGeometry.PanelCorner, Theme.Island);
        }

        c.Mascot();
        Tabs(c, PanelId.Jobs, size);
        foreach (var line in layout.HeadText())
        {
            c.Text(line, body);
        }

        for (int i = 0; i < layout.Jobs.Count; i++)
        {
            var row = layout.Rows[i];
            if (row.Height == 0)
            {
                continue;
            }

            var job = layout.Jobs[i];
            c.RoundRect(Offset(row, body), Section.TileCorner, i == layout.Opened ? Theme.Raised : Theme.Card);
            if (job.Running)
            {
                float inset = JobsPanelLayout.RunningLine / 2;
                c.Stroke(Offset(RectangleF.Inflate(row, -inset, -inset), body), Section.TileCorner, Theme.Accent, JobsPanelLayout.RunningLine);
            }

            c.RoundRect(Offset(JobsPanelLayout.IconIn(row), body), JobsPanelLayout.IconCorner, job.Running ? Theme.Accent : i == layout.Opened ? Theme.RaisedHi : Theme.Raised);
            var glyph = JobsPanelLayout.GlyphIn(row);
            c.Glyph(Glyphs.Timer, body + new Vector2(glyph.X, glyph.Y), glyph.Width, job.Running ? Theme.OnAccent : Theme.Ink2);
            foreach (var line in JobsPanelLayout.RowText(job, row, JobsNow, TimeZoneInfo.Local, job.Running ? step : null))
            {
                c.Text(line, body);
            }

            var chevron = JobsPanelLayout.ChevronIn(row);
            c.Glyph(Glyphs.ChevronDown, body + new Vector2(chevron.X, chevron.Y), chevron.Width, Theme.Ink3);
        }

        if (layout.Result is { } result)
        {
            c.Text(result, layout.ResultAt, body);
            var button = layout.RemoveButton(asking);
            c.RoundRect(Offset(button, body), Section.ButtonCorner, Theme.RaisedHi);
            c.Text(new TextSlot(asking ? JobsPanelLayout.ConfirmRemove : JobsPanelLayout.Remove, asking ? JobsPanelLayout.AskingText : JobsPanelLayout.ButtonText, button), body);
        }

        c.Text(layout.FootText(TimeSpan.FromMinutes(30)), body);
    }
}
