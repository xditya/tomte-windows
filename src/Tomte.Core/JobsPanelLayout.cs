using System.Drawing;
using System.Globalization;
using System.Numerics;
using Tomte.Core.Interaction;
using Tomte.Core.Jobs;
using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>
/// Where the Jobs tab's parts sit, in DIPs from the body's top-left corner (under the tab row), and what it says: the
/// heading and a count, every scheduled job with those running first and then the soonest run first, and a line under
/// them that says when the tab shows. A row has the job's icon, on the accent while it runs, its name and under it the
/// schedule in words or, while it runs, what it did last; at its right end the next run and the last result. The opened
/// row grows to show the last result's text and Remove, which asks once more. The list moves a row at a time.
/// </summary>
public sealed class JobsPanelLayout
{
    public const float Pad = Section.BodyPad, Top = Section.BodyTop, Bottom = Section.BodyPad, HeadHeight = 22, FootHeight = 14, Gap = 10;
    public const float RowHeight = 56, RowGap = 6, RowPad = 12, Icon = 32, IconCorner = 10, IconGlyph = 16, Chevron = 14, WhenWidth = 176;
    public const float ButtonHeight = StateLook.ActionHeight, ButtonPadX = 14, RunningLine = 1.5f;
    public const int ResultLines = 4, ShownRows = 4;

    /// <summary>The body's height: the heading, four rows and the line under them.</summary>
    public const float Height = Top + HeadHeight + Gap + (ShownRows * RowHeight) + ((ShownRows - 1) * RowGap) + Gap + FootHeight + Bottom;

    public const string RemoveId = "remove", Remove = "Remove", ConfirmRemove = ShelfLayout.ConfirmRemove;

    public static TextStyle NameStyle => TextStyle.Title;
    public static TextStyle ScheduleStyle => TextStyle.Secondary;
    public static TextStyle RunningStyle => TextStyle.Secondary with { Color = Theme.Ink };
    public static TextStyle WhenStyle => TextStyle.Primary with { Align = TextAlign.Trailing, Tabular = true };
    public static TextStyle LastStyle => TextStyle.Small with { Align = TextAlign.Trailing, Tabular = true };
    public static TextStyle FailedStyle => LastStyle with { Color = Theme.Warn };
    public static TextStyle ResultStyle => new(12, Color: Theme.Ink2, LineHeight: 18);
    public static TextStyle ButtonText => new(12, 500, Align: TextAlign.Center);
    public static TextStyle AskingText => ButtonText with { Color = Theme.Warn };

    private static readonly CultureInfo Words = CultureInfo.InvariantCulture;
    private readonly float[] heights;
    private readonly RectangleF[] rows;

    /// <param name="jobs">In the order the list shows them (<see cref="Order"/>).</param>
    /// <param name="opened">The opened job's id, or null.</param>
    /// <param name="first">The first row in view.</param>
    public JobsPanelLayout(Vector2 body, IReadOnlyList<JobStatus> jobs, long? opened = null, int first = 0)
    {
        Body = body;
        Jobs = jobs;
        Opened = jobs.ToList().FindIndex(j => j.Job.Id == opened);
        heights = [.. jobs.Select((_, i) => i == Opened ? OpenedHeight : RowHeight)];
        First = Math.Clamp(first, 0, LastFirst);
        rows = new RectangleF[jobs.Count];
        float y = List.Y;
        for (int i = 0; i < jobs.Count; i++)
        {
            bool shown = i >= First && y + heights[i] <= List.Bottom + 0.5f;
            // Out of view a row keeps a place with no height at the list's top or bottom edge, so only the arrow keys reach it.
            rows[i] = shown ? new RectangleF(List.X, y, List.Width, heights[i]) : new RectangleF(List.X, i < First ? List.Y : List.Bottom, List.Width, 0);
            y += shown ? heights[i] + RowGap : 0;
        }
    }

    public Vector2 Body { get; }

    public IReadOnlyList<JobStatus> Jobs { get; }

    /// <summary>The opened row's index, or -1.</summary>
    public int Opened { get; }

    public int First { get; }

    public RectangleF Head => new(Pad, Top, Body.X - (2 * Pad), HeadHeight);

    public RectangleF List => RectangleF.FromLTRB(Pad, Top + HeadHeight + Gap, Body.X - Pad, Body.Y - Bottom - FootHeight - Gap);

    public RectangleF Foot => new(Pad, Body.Y - Bottom - FootHeight, Body.X - (2 * Pad), FootHeight);

    /// <summary>Each row's card in body space; a row out of view has no height.</summary>
    public IReadOnlyList<RectangleF> Rows => rows;

    /// <summary>The opened row's result text, under its lines and up to <see cref="ResultLines"/> lines; null with none opened or that row out of view.</summary>
    public TextRun? Result => Opened < 0 || rows[Opened].Height == 0 ? null : ResultRun;

    private TextRun ResultRun => new(ResultText(Jobs[Opened].Last), Jobs[Opened].Last is { Ok: false } ? ResultStyle with { Color = Theme.Warn } : ResultStyle, List.Width - TextX - RowPad, ResultLines);

    /// <summary>Where the opened row's result text starts, under its two lines.</summary>
    public Vector2 ResultAt => Opened < 0 ? default : new(rows[Opened].X + TextX, rows[Opened].Y + RowHeight - 4);

    /// <summary>Remove in the opened row, under the result; empty with none opened or that row out of view.</summary>
    public RectangleF RemoveButton(bool asking)
    {
        if (Opened < 0 || rows[Opened].Height == 0)
        {
            return RectangleF.Empty;
        }

        var row = rows[Opened];
        float w = MathF.Ceiling(TextSlot.Measure(asking ? ConfirmRemove : Remove, ButtonText, row.Width)) + (2 * ButtonPadX);
        return new(row.X + TextX, row.Bottom - RowPad - ButtonHeight, w, ButtonHeight);
    }

    private float OpenedHeight
    {
        get
        {
            using var laid = TextLayout.Of(ResultRun);
            return RowHeight - 4 + laid.Height + Gap + ButtonHeight + RowPad;
        }
    }

    /// <summary>The first row in view as far down as the list goes: the last rows then just fit.</summary>
    private int LastFirst
    {
        get
        {
            float room = List.Height, used = 0;
            int i = heights.Length;
            while (i > 0 && used + heights[i - 1] <= room + 0.5f)
            {
                used += heights[--i] + RowGap;
            }

            return Math.Min(i, Math.Max(0, heights.Length - 1));
        }
    }

    /// <summary>The first row in view after <paramref name="steps"/> rows down (negative up), held to the list's ends.</summary>
    public int Scrolled(int steps) => Math.Clamp(First + steps, 0, LastFirst);

    /// <summary>The first row in view that shows row <paramref name="index"/>: as it is when that row shows already.</summary>
    public int Reveal(int index)
    {
        if (index < First || rows[index].Height > 0)
        {
            return Math.Min(index, First);
        }

        // The rows from the new first one to this one, with their gaps, just fit.
        int first = index;
        for (float used = heights[index]; first > First && used + RowGap + heights[first - 1] <= List.Height + 0.5f; first--)
        {
            used += RowGap + heights[first - 1];
        }

        return first;
    }

    // The text's left edge in a row: after the icon.
    private const float TextX = RowPad + Icon + RowPad;

    private static float TextWidth(float rowWidth) => rowWidth - TextX - RowPad - WhenWidth - RowPad - Chevron - RowPad;

    public static RectangleF IconIn(RectangleF row) => new(row.X + RowPad, row.Y + ((RowHeight - Icon) / 2), Icon, Icon);

    public static RectangleF GlyphIn(RectangleF row)
    {
        var icon = IconIn(row);
        return new(icon.X + ((Icon - IconGlyph) / 2), icon.Y + ((Icon - IconGlyph) / 2), IconGlyph, IconGlyph);
    }

    public static RectangleF ChevronIn(RectangleF row) => new(row.Right - RowPad - Chevron, row.Y + ((RowHeight - Chevron) / 2), Chevron, Chevron);

    /// <summary>
    /// A row's lines in body space: the name and under it the schedule (or what the run is doing), and at the right end the
    /// next run and the last result. <paramref name="step"/> is the line the running job raised last, if any.
    /// </summary>
    public static IReadOnlyList<TextSlot> RowText(JobStatus status, RectangleF row, DateTimeOffset now, TimeZoneInfo zone, string? step = null)
    {
        float x = row.X + TextX, width = TextWidth(row.Width), when = ChevronIn(row).X - RowPad - WhenWidth;
        return
        [
            new(status.Job.Name, NameStyle, new(x, row.Y + 10, width, 18)),
            new(Second(status, step), status.Running ? RunningStyle : ScheduleStyle, new(x, row.Y + 29, width, 16)),
            new(When(status.Job.NextDue, now, zone), WhenStyle, new(when, row.Y + 10, WhenWidth, 18)),
            new(Last(status, zone, now), status.Last is { Ok: false } && !status.Running ? FailedStyle : LastStyle, new(when, row.Y + 29, WhenWidth, 16)),
        ];
    }

    /// <summary>The heading and the count beside it: "3 jobs", "3 jobs · 1 running".</summary>
    public IReadOnlyList<TextSlot> HeadText()
    {
        const string title = "Scheduled jobs";
        float after = TextSlot.Measure(title, TextStyle.PageTitle, Head.Width) + Gap;
        int running = Jobs.Count(j => j.Running);
        string count = (Jobs.Count == 1 ? "1 job" : string.Create(Words, $"{Jobs.Count} jobs")) + (running == 0 ? "" : string.Create(Words, $" · {running} running"));
        return
        [
            new(title, TextStyle.PageTitle, Head),
            new(count, Section.Note with { Size = 13 }, new(Head.X + after, Top, Head.Width - after, HeadHeight)),
        ];
    }

    /// <summary>The line under the list: when the tab shows.</summary>
    public TextSlot FootText(TimeSpan ahead) =>
        new(string.Create(Words, $"Shows while a job runs or is due within {Minutes(ahead)}. Change it in Settings, Jobs."), TextStyle.Small, Foot);

    /// <summary>
    /// The targets in Tab order: every row by its first <see cref="RowHeight"/> DIPs, one arrow-key group, then the
    /// opened row's Remove.
    /// </summary>
    public IReadOnlyList<Target> Targets(bool asking)
    {
        var list = new List<Target>();
        for (int i = 0; i < Jobs.Count; i++)
        {
            list.Add(new(RowId(Jobs[i].Job.Id), rows[i] with { Height = Math.Min(rows[i].Height, RowHeight) }, Section.TileCorner, TargetKind.Row, Group: 1, Checked: i == Opened));
        }

        if (RemoveButton(asking) is { IsEmpty: false } remove)
        {
            list.Add(new(RemoveId, remove, Section.ButtonCorner, TargetKind.Button, Tip: asking ? null : "Remove this job and its Task Scheduler entry"));
        }

        return list;
    }

    public static string RowId(long job) => string.Create(Words, $"job:{job}");

    /// <summary>
    /// Whether the tab is on the strip: while a job runs, or while a job's next run is <paramref name="ahead"/> away or
    /// less (a run just due and not yet taken counts). Never just because jobs exist.
    /// </summary>
    public static bool Shows(IEnumerable<JobStatus> jobs, DateTimeOffset now, TimeSpan ahead) =>
        jobs.Any(j => j.Running || (j.Job.NextDue is { } at && at - now <= ahead));

    /// <summary>When the tab is to show next for a run due at <paramref name="next"/>; null when it shows already or nothing is due.</summary>
    public static DateTimeOffset? ShowsAt(DateTimeOffset? next, DateTimeOffset now, TimeSpan ahead) =>
        next is { } at && at - ahead > now ? at - ahead : null;

    /// <summary>Running jobs first, then by the next run, soonest first, and jobs with no run left last.</summary>
    public static IReadOnlyList<JobStatus> Order(IEnumerable<JobStatus> jobs) =>
        [.. jobs.OrderBy(j => !j.Running).ThenBy(j => j.Job.NextDue ?? DateTimeOffset.MaxValue).ThenBy(j => j.Job.Id)];

    /// <summary>The line under the name: the schedule in words, or while it runs "Running" and the step it took last.</summary>
    public static string Second(JobStatus status, string? step) =>
        !status.Running ? status.Job.Schedule.Describe() : step is { Length: > 0 } ? $"Running · {OneLine(step)}" : "Running";

    /// <summary>The next run: "21:00 · in 18 min", "Thu 09:00 · in 2 days", or "No run left" for a one-off that has run.</summary>
    public static string When(DateTimeOffset? next, DateTimeOffset now, TimeZoneInfo zone) =>
        next is { } at ? $"{At(at, now, zone)} · {In(at - now)}" : "No run left";

    /// <summary>The last result and its time: "Done 09:00", "Failed Tue 09:00", "Missed 21:00", or "Not run yet".</summary>
    public static string Last(JobStatus status, TimeZoneInfo zone, DateTimeOffset now) => status.Last switch
    {
        null => "Not run yet",
        { Ok: true } run => $"Done {At(run.Finished, now, zone)}",
        { Text: JobStore.Missed } run => $"Missed {At(run.Due, now, zone)}",
        var run => $"Failed {At(run.Finished, now, zone)}",
    };

    /// <summary>The opened row's text: what the last run answered, or why it failed.</summary>
    public static string ResultText(JobRunRecord? last) => last switch
    {
        null => "This job has not run yet.",
        { Text.Length: 0 } => last.Ok ? "Done, with no answer." : "Failed, with no reason given.",
        _ => last.Text.Trim(),
    };

    /// <summary>A time as the schedule says it: "21:00" today, "Thu 09:00" within the week, "15 Oct 09:00" further.</summary>
    public static string At(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        int days = Math.Abs((local.Date - TimeZoneInfo.ConvertTime(now, zone).Date).Days);
        string format = days == 0 ? "HH:mm" : days < 7 ? "ddd HH:mm" : "d MMM HH:mm";
        return local.ToString(format, Words);
    }

    /// <summary>How long until a run: "in 18 min" (up to the minute), "in 5 h", "in 3 days", or "now" once it is due.</summary>
    public static string In(TimeSpan left) =>
        left <= TimeSpan.Zero ? "now"
        : Math.Ceiling(left.TotalMinutes) < 60 ? $"in {Minutes(left)}"
        : left < TimeSpan.FromDays(1) ? string.Create(Words, $"in {(int)left.TotalHours} h")
        : (int)left.TotalDays == 1 ? "in 1 day" : string.Create(Words, $"in {(int)left.TotalDays} days");

    private static string Minutes(TimeSpan span) => string.Create(Words, $"{(int)Math.Ceiling(span.TotalMinutes)} min");

    private static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}
