using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Jobs;
using Tomte.Core.Text;
using Windows.UI.Composition;

namespace Tomte.Shell;

/// <summary>
/// The Jobs tab, laid out by <see cref="JobsPanelLayout"/>, and when it is on the strip: while a job runs or one is due
/// within the time set in Settings (<see cref="TomteSettings.JobsAhead"/>), as the Call tab is there only during a call.
/// The jobs are read again when the one-shot is armed for another run or the clock moves (<see cref="Jobs.NextChanged"/>,
/// which also follows a run in a --run-job start, as it tells the running Tomte), when a run here starts, acts or ends
/// (<see cref="Jobs.Ran"/>), and when the setting changes; the database is read only with a run near. One one-shot puts the
/// tab on the strip when the next run comes within the time; nothing polls. A tab that shows keeps showing until the island
/// hides. A click on a row opens it on its last result and Remove, which asks once more and removes as Settings does.
/// While the tab shows, a one-shot on the minute moves the times.
/// </summary>
internal sealed class JobsPanel : IPanel, IInteractive
{
    private const int WheelNotch = 120;

    // The longest wait the appear one-shot is set for; a fire before the run's time only looks again.
    private static readonly TimeSpan LongestWait = TimeSpan.FromDays(1);

    private readonly IslandWindow island;
    private readonly PanelHost panels;
    private readonly SettingsStore store;
    private readonly TextRenderer text;
    private readonly Action<string>? log;
    private readonly Windows.System.DispatcherQueueTimer appear, tick;
    private IReadOnlyList<JobStatus> jobs = [];
    private IReadOnlyList<Target> targets = [];
    private TimeSpan ahead;
    private bool shows, asking;
    private int reads;

    // While the tab shows: its body, what is drawn, the opened job, the first row in view and what went wrong.
    private ContainerVisual? body;
    private Vector2 size;
    private Parts? parts;
    private JobsPanelLayout? layout;
    private long? opened;
    private int first, wheel;
    private string? failure;

    public JobsPanel(IslandWindow island, PanelHost panels, SettingsStore store, TextRenderer text, Action<string>? log)
    {
        (this.island, this.panels, this.store, this.text, this.log) = (island, panels, store, text, log);
        ahead = store.Current.JobsAhead;
        appear = island.Queue.CreateTimer();
        appear.IsRepeating = false;
        appear.Tick += (_, _) => Watch();
        tick = island.Queue.CreateTimer();
        tick.IsRepeating = false;
        tick.Tick += (_, _) => Draw();
        panels.ShowTab(PanelId.Jobs, false);
        Jobs.NextChanged += Watch;
        Jobs.Ran += Watch;
        // Changed arrives on a pool thread.
        store.Changed += s => island.Queue.TryEnqueue(() =>
        {
            if (s.JobsAhead != ahead)
            {
                ahead = s.JobsAhead;
                Watch();
            }
        });
        // A run that ended while the tab was open leaves the tab until the island goes.
        island.WentHidden += () => panels.ShowTab(PanelId.Jobs, shows);
    }

    public PanelId Id => PanelId.Jobs;

    public IReadOnlyList<Target> Targets => targets;

    public event Action? TargetsChanged;

    public void Shown(ContainerVisual body, Vector2 size)
    {
        (this.body, this.size) = (body, size);
        Draw();
        Watch();
    }

    public void Hidden()
    {
        tick.Stop();
        parts?.Dispose();
        (parts, body, layout, opened, first, wheel, failure, asking) = (null, null, null, null, 0, 0, null, false);
        targets = [];
        TargetsChanged?.Invoke();
    }

    /// <summary>Presses come through the targets.</summary>
    public void Press(Vector2 point)
    {
    }

    /// <summary>The wheel moves the list a row a notch.</summary>
    public void Wheel(Vector2 point, int delta)
    {
        wheel += delta;
        int notches = wheel / WheelNotch;
        if (notches == 0 || layout is null)
        {
            return;
        }

        wheel -= notches * WheelNotch;
        if (layout.Scrolled(-notches) is var next && next != first)
        {
            first = next;
            Draw();
        }
    }

    /// <summary>Focus that reaches a row out of view (only the arrow keys can) moves the list to show it.</summary>
    public void Hovered(string? id)
    {
        if (layout is null || id is null || targets.FirstOrDefault(t => t.Id == id).Bounds.Height > 0)
        {
            return;
        }

        int index = layout.Jobs.ToList().FindIndex(j => JobsPanelLayout.RowId(j.Job.Id) == id);
        if (index >= 0 && layout.Reveal(index) is var next && next != first)
        {
            first = next;
            Draw();
        }
    }

    /// <summary>Esc closes the opened row before it retracts the island.</summary>
    public bool Command(NavKey key)
    {
        if (key is not (NavKey.Escape or NavKey.Back) || opened is null)
        {
            return false;
        }

        (opened, asking) = (null, false);
        Draw();
        return true;
    }

    /// <summary>A double click's second click would close the row the first opened: only the first counts.</summary>
    public void Activate(string id, ActivateBy by)
    {
        if (by != ActivateBy.DoubleClick)
        {
            Activate(id);
        }
    }

    public void Activate(string id)
    {
        if (layout is null)
        {
            return;
        }

        if (id == JobsPanelLayout.RemoveId && opened is { } job)
        {
            if (asking)
            {
                _ = RemoveAsync(job);
            }
            else
            {
                asking = true;
                Draw();
            }

            return;
        }

        if (layout.Jobs.FirstOrDefault(j => JobsPanelLayout.RowId(j.Job.Id) == id) is { } picked)
        {
            (opened, asking, failure) = (picked.Job.Id == opened ? null : picked.Job.Id, false, null);
            Draw();
            if (opened is not null && layout.Opened >= 0 && layout.Reveal(layout.Opened) is var next && next != first)
            {
                first = next;
                Draw();
            }
        }
    }

    private async Task RemoveAsync(long job)
    {
        string? problem = await Jobs.RemoveAsync(job);
        (opened, asking, failure) = (null, false, problem);
        // Removing re-arms the one-shot, which reads the jobs again.
        Draw();
    }

    /// <summary>
    /// Reads the jobs when a run may be near and puts the tab on the strip or takes it off; with no run near, sets the
    /// one-shot for when one comes within the time. The next-due time counts a claimed run at its limit, so a run in
    /// progress is near too.
    /// </summary>
    private async void Watch()
    {
        appear.Stop();
        int read = ++reads;
        var now = DateTimeOffset.Now;
        bool near = Jobs.NextDue is { } due && due - now <= (ahead > JobStore.RunLimit ? ahead : JobStore.RunLimit);
        IReadOnlyList<JobStatus> next = [];
        if (near || body is not null)
        {
            try
            {
                next = await Jobs.StatusesAsync();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                log?.Invoke($"jobs tab: the jobs could not be read ({e.GetType().Name}: {e.Message})");
                next = jobs;
            }
        }

        if (read != reads)
        {
            return;
        }

        jobs = next;
        bool was = shows;
        shows = JobsPanelLayout.Shows(jobs, now, ahead);
        if (shows != was)
        {
            log?.Invoke($"jobs tab: {(shows ? "shown" : "hidden")}");
        }

        // An open tab stays until the island goes.
        if (shows || body is null)
        {
            panels.ShowTab(PanelId.Jobs, shows);
        }

        if (!shows && JobsPanelLayout.ShowsAt(Jobs.NextDue, now, ahead) is { } at)
        {
            var wait = at - now;
            appear.Interval = wait < LongestWait ? wait : LongestWait;
            appear.Start();
        }

        Draw();
    }

    private void Draw()
    {
        tick.Stop();
        if (body is null)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        var zone = TimeZoneInfo.Local;
        parts?.Dispose();
        var p = parts = new Parts(island.Compositor, text);
        var shapes = p.Shapes(body, new RectangleF(0, 0, size.X, size.Y));
        var l = layout = new JobsPanelLayout(size, JobsPanelLayout.Order(jobs), opened, first);
        first = l.First;
        foreach (var line in l.HeadText())
        {
            p.Text(shapes, line);
        }

        if (l.Jobs.Count == 0)
        {
            p.Text(shapes, new TextSlot("No scheduled jobs.", TextStyle.Secondary with { Align = TextAlign.Center }, l.List));
        }

        var card = p.Brush(ShelfColors.Card);
        for (int i = 0; i < l.Jobs.Count; i++)
        {
            var row = l.Rows[i];
            if (row.Height == 0)
            {
                continue;
            }

            var job = l.Jobs[i];
            // A running job: its card ringed and its icon on the accent. The icon's square is a step up from its card.
            p.Box(shapes, row, Section.TileCorner, i == l.Opened ? p.Brush(ShelfColors.Raised) : card, job.Running ? p.Brush(ShelfColors.Accent) : null, JobsPanelLayout.RunningLine);
            p.Box(shapes, JobsPanelLayout.IconIn(row), JobsPanelLayout.IconCorner, p.Brush(job.Running ? ShelfColors.Accent : i == l.Opened ? ShelfColors.RaisedHi : ShelfColors.Raised));
            p.Glyph(shapes, Glyphs.Timer, job.Running ? ShelfColors.Of(Theme.OnAccent) : ShelfColors.Ink2, JobsPanelLayout.GlyphIn(row));
            foreach (var line in JobsPanelLayout.RowText(job, row, now, zone, job.Running ? Jobs.Step : null))
            {
                p.Text(shapes, line);
            }

            p.Glyph(shapes, Glyphs.ChevronDown, ShelfColors.Ink3, JobsPanelLayout.ChevronIn(row));
        }

        if (l.Result is { } result)
        {
            p.Text(shapes, result, l.ResultAt);
            var button = l.RemoveButton(asking);
            if (!button.IsEmpty)
            {
                p.Box(shapes, button, Section.ButtonCorner, p.Brush(ShelfColors.RaisedHi));
                p.Text(shapes, new TextSlot(asking ? JobsPanelLayout.ConfirmRemove : JobsPanelLayout.Remove, asking ? JobsPanelLayout.AskingText : JobsPanelLayout.ButtonText, button));
            }
        }

        p.Text(shapes, failure is null ? l.FootText(ahead) : l.FootText(ahead) with { Text = failure, Style = TextStyle.Small with { Color = Theme.Warn } });
        targets = l.Targets(asking);
        TargetsChanged?.Invoke();

        // The relative times change on the minute.
        tick.Interval = TimeSpan.FromSeconds(60 - now.Second - (now.Millisecond / 1000.0) + 0.05);
        tick.Start();
    }
}
