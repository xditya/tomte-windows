using Tomte.Core;
using Tomte.Core.Text;
using Tomte.Core.Triggers;
using Windows.UI.Composition;
using Windows.Win32;

namespace Tomte.Shell;

/// <summary>
/// The words on the Compact pill: the first-run hint, an event's line (after the ring on the timer's done pill)
/// and the time a running timer has left beside its ring. What it says comes from
/// <see cref="TriggerController.CompactLine"/>. It shows from ContentReady on, follows changes the triggers make
/// to the pill, and fades out with the next state change, like the now-playing row. On a push pill with no timer
/// and nothing playing it says the time of day and one more thing (<see cref="CompactRules.Glance"/>), read only
/// while it shows: the job due soonest, the events that wait, the battery, the shelf, the date. Only the countdown
/// and the time tick: one one-shot timer to the next whole second or the next minute, while it shows.
/// </summary>
internal sealed class CompactLine
{
    // Wide enough that a measured line is never cut by the measuring itself.
    private const float MeasureWidth = 4096;

    private static readonly Func<string, float> Measure = line => TextSlot.Measure(line, CompactLayout.LineStyle, MeasureWidth);

    private readonly IslandWindow island;
    private readonly TriggerController triggers;
    private readonly TextRenderer text;
    private readonly NowPlayingRow row;
    private readonly Action<string>? log;
    private readonly ScalarKeyFrameAnimation fadeIn, fadeOut;
    private readonly Windows.System.DispatcherQueueTimer release, tick;
    private View? view, leaving;
    private bool ready, shelfAsked, readingJob;

    // The job due soonest, as read for the one-shot's time in jobFor; null when nothing is due within the hour.
    private (string Name, DateTimeOffset At)? job;
    private DateTimeOffset? jobFor;

    public CompactLine(IslandWindow island, TriggerController triggers, TextRenderer text, NowPlayingRow row, Action<string>? log = null)
    {
        this.island = island;
        this.triggers = triggers;
        this.text = text;
        this.row = row;
        this.log = log;
        row.ShowingChanged += Update;
        fadeIn = PanelHost.Fade(island.Compositor, 1, PanelHost.FadeInMs);
        fadeOut = PanelHost.Fade(island.Compositor, 0, PanelHost.FadeOutMs);
        release = PanelHost.AfterFadeOut(island, Release);
        tick = island.Queue.CreateTimer();
        tick.IsRepeating = false;
        tick.Tick += (_, _) => Update();
        triggers.Machine.Changed += (_, _) =>
        {
            ready = false;
            Update();
        };
        island.ContentReady += () =>
        {
            ready = triggers.Machine.State == IslandState.Compact;
            Update();
        };
        triggers.CompactChanged += Update;
        // A removed job can leave the soonest time as it was; read the name again after any change.
        Jobs.NextChanged += () =>
        {
            jobFor = null;
            Update();
        };
    }

    private void Update()
    {
        tick.Stop();
        var now = DateTimeOffset.Now;
        var (line, left) = ready && triggers.Machine.State == IslandState.Compact ? triggers.CompactLine(now, row.Showing) : (null, null);
        if (line is null)
        {
            if (view is not null)
            {
                // Fades out over 0.1 s, then goes: on a timer, since a compositor that is not presenting
                // (display asleep) completes no animation.
                Release();
                (leaving, view) = (view, null);
                leaving.Root.StartAnimation("Opacity", fadeOut);
                release.Start();
            }

            return;
        }

        if (view is null)
        {
            Release();
            view = new View(island.Compositor, text);
            view.Root.Opacity = 0;
            island.Dips.Children.InsertAtTop(view.Root);
            view.Root.StartAnimation("Opacity", fadeIn);
        }

        bool ring = triggers.CompactShows(playing: false) == CompactContent.TimerRing;
        bool clock = left is null && triggers.Compact == CompactReason.Summary;
        bool mascot = island.Mascot.Enabled;
        var slot = CompactLayout.Line(mascot, ring);
        if (clock)
        {
            line = CompactRules.Glance(now, Facts(now), slot.Width, Measure);
        }

        view.Text.Show(line, CompactLayout.Ending(left is null ? CompactLayout.LineStyle : CompactLayout.ClockStyle, led: mascot || ring), slot);

        if (left is { } l && l > TimeSpan.Zero)
        {
            // The countdown reads whole seconds rounded up, so it changes as the time left passes one.
            double fraction = l.TotalSeconds - Math.Floor(l.TotalSeconds);
            tick.Interval = TimeSpan.FromSeconds((fraction > 0 ? fraction : 1) + 0.01);
            tick.Start();
        }
        else if (clock)
        {
            // The time of day changes on the minute.
            tick.Interval = TimeSpan.FromSeconds(60 - now.Second - now.Millisecond / 1000.0 + 0.05);
            tick.Start();
        }
    }

    /// <summary>
    /// What the push pill may mention, as it is now. The shelf loads the first time the line shows and the job's name
    /// is read when one is due within the hour; neither holds the line up, which updates when they come in.
    /// </summary>
    private GlanceFacts Facts(DateTimeOffset now)
    {
        if (!shelfAsked && Jobs.Shelf is { } shelf)
        {
            shelfAsked = true;
            shelf.Changed += Update;
            _ = shelf.Load();
        }

        if (Jobs.NextDue is not { } due || due - now > CompactRules.JobSoon)
        {
            (job, jobFor) = (null, null);
        }
        else if (due != jobFor && !readingJob)
        {
            _ = ReadJob(due);
        }

        return new GlanceFacts { Job = job, Waiting = triggers.Waiting, Quiet = triggers.Quiet, Paused = triggers.Paused, Battery = Battery(), ShelfCount = Jobs.Shelf?.Items.Count ?? 0 };
    }

    private async Task ReadJob(DateTimeOffset due)
    {
        readingJob = true;
        try
        {
            job = await Jobs.NextJobAsync();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            job = null;
            log?.Invoke($"compact: the job due next was not read: {e.Message}");
        }
        finally
        {
            readingJob = false;
        }

        jobFor = due;
        Update();
    }

    /// <summary>The charge in percent while the PC runs on its battery; null on mains power, with no battery, or when unknown.</summary>
    private static int? Battery() =>
        PInvoke.GetSystemPowerStatus(out var power) && power.ACLineStatus == 0 && power.BatteryLifePercent <= 100 ? power.BatteryLifePercent : null;

    private void Release()
    {
        release.Stop();
        if (leaving is { } gone)
        {
            leaving = null;
            island.Dips.Children.Remove(gone.Root);
            gone.Dispose();
        }
    }

    private sealed class View : IDisposable
    {
        public View(Compositor compositor, TextRenderer text)
        {
            Root = compositor.CreateContainerVisual();
            Text = text.Create(Root);
        }

        public ContainerVisual Root { get; }

        public TextVisual Text { get; }

        public void Dispose()
        {
            Text.Dispose();
            Root.Dispose();
        }
    }
}
