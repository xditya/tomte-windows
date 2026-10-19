using Tomte.Core;
using Tomte.Core.Text;
using Tomte.Core.Triggers;
using Windows.UI.Composition;
using Windows.Win32;

namespace Tomte.Shell;

/// <summary>
/// The words on the Compact pill: the first-run hint, an event's line (after the ring on the timer's done pill)
/// and, beside its ring, a running timer's word and the time it has left. What it says comes from
/// <see cref="TriggerController.CompactLine"/>. It shows from ContentReady on, follows changes the triggers make
/// to the pill, and fades out with the next state change, like the now-playing row. On a push pill with no timer
/// and nothing playing it says the time of day, a short rule and one more thing (<see cref="CompactRules.Glance"/>), read only
/// while it shows: the job due soonest, the events that wait, the battery, the shelf, the date. Only the countdown
/// and the time tick: one one-shot timer to the next whole second or the next minute, while it shows.
/// </summary>
internal sealed class CompactLine
{
    // Wide enough that a measured line is never cut by the measuring itself.
    private const float MeasureWidth = 4096;


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

        bool mascot = island.Mascot.Enabled, corner = triggers.PeekInCorner;
        if (view is null)
        {
            Release();
            // Words the tomte leads end the pill, so they go with its trailing end; alone they keep to its centre. The
            // corner pill has its own shape and lays its time out from its centre.
            bool fromEnd = mascot && !corner;
            view = new View(island.Compositor, text, fromEnd ? island.Trailing : island.Dips, fromEnd);
            view.Root.Opacity = 0;
            view.Root.StartAnimation("Opacity", fadeIn);
        }

        bool ring = triggers.CompactShows(playing: false) == CompactContent.TimerRing;
        bool clock = left is null && triggers.Compact == CompactReason.Summary;
        float depth = (float)IslandGeometry.Compact.Depth;
        if (ring && left is not null)
        {
            // The ring, the word and the time left are one group; the ring follows the time's width, which changes at
            // 1:00:00 to 59:59.
            float label = Measure(CompactLayout.TimerLabel, CompactLayout.TimerLabelStyle), count = Measure(line, CompactLayout.ClockStyle);
            var (lead, x, width) = CompactLayout.Group(mascot, CompactLayout.RingSize, label + CompactLayout.Gap + count);
            view.Text.Show(CompactLayout.TimerLabel, CompactLayout.TimerLabelStyle, view.Place(new(x, 0, label + 1, depth)));
            float countX = x + label + CompactLayout.Gap;
            view.Second.Show(line, CompactLayout.ClockStyle, view.Place(new(countX, 0, Math.Max(0, x + width - countX) + 1, depth)));
            view.Rule(null);
            triggers.PlaceRing(view.At(lead));
        }
        else if (ring)
        {
            var (lead, x, width) = CompactLayout.Group(mascot, CompactLayout.RingSize, Measure(line, CompactLayout.LineStyle));
            view.Text.Show(line, CompactLayout.LineStyle, view.Place(new(x, 0, width, depth)));
            view.Second.Show("", CompactLayout.LineStyle, default);
            view.Rule(null);
            triggers.PlaceRing(view.At(lead));
        }
        else if (clock)
        {
            // The time, a short rule, then what else there is to know, as one group that ends the pill.
            line = CompactRules.Glance(now, Facts(now), CompactLayout.Line(mascot).Width, CompactLayout.GlanceWidth);
            var (time, rest) = CompactLayout.SplitGlance(line);
            var (timeSlot, rule, restSlot) = CompactLayout.Glance(mascot, Measure(time, CompactLayout.TimeStyle), rest.Length == 0 ? 0 : Measure(rest, CompactLayout.GlanceStyle));
            view.Text.Show(time, CompactLayout.TimeStyle, view.Place(timeSlot));
            view.Second.Show(rest, CompactLayout.GlanceStyle, view.Place(restSlot));
            view.Rule(rest.Length == 0 ? null : view.At(rule));
        }
        else if (corner)
        {
            view.Text.Show(line, CompactLayout.CornerTimeStyle, view.Place(CompactLayout.CornerClock((float)island.Heading.Width)));
            view.Second.Show("", CompactLayout.LineStyle, default);
            view.Rule(null);
        }
        else
        {
            view.Text.Show(line, CompactLayout.Ending(CompactLayout.LineStyle, led: mascot), view.Place(CompactLayout.Line(mascot)));
            view.Second.Show("", CompactLayout.LineStyle, default);
            view.Rule(null);
        }

        if (left is { } l && l > TimeSpan.Zero)
        {
            // The countdown reads whole seconds rounded up, so it changes as the time left passes one.
            double fraction = l.TotalSeconds - Math.Floor(l.TotalSeconds);
            tick.Interval = TimeSpan.FromSeconds((fraction > 0 ? fraction : 1) + 0.01);
            tick.Start();
        }
        else if (clock || corner)
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

    private static float Measure(string text, TextStyle style) => TextSlot.Measure(text, style, MeasureWidth);

    /// <summary>The charge in percent while the PC runs on its battery; null on mains power, with no battery, or when unknown.</summary>
    private static int? Battery() =>
        PInvoke.GetSystemPowerStatus(out var power) && power.ACLineStatus == 0 && power.BatteryLifePercent <= 100 ? power.BatteryLifePercent : null;

    private void Release()
    {
        release.Stop();
        if (leaving is { } gone)
        {
            leaving = null;
            gone.Dispose();
        }
    }

    private sealed class View : IDisposable
    {
        private readonly ContainerVisual frame;
        private readonly ShapeVisual rule;
        private readonly bool fromEnd;

        public View(Compositor compositor, TextRenderer text, ContainerVisual frame, bool fromEnd)
        {
            Root = compositor.CreateContainerVisual();
            Text = text.Create(Root);
            Second = text.Create(Root);
            (this.frame, this.fromEnd) = (frame, fromEnd);

            // The glance's rule: 1 x 14, centred down the pill.
            rule = compositor.CreateShapeVisual();
            rule.Size = new System.Numerics.Vector2(1, CompactLayout.RuleHeight);
            var line = compositor.CreateRectangleGeometry();
            line.Size = rule.Size;
            var shape = compositor.CreateSpriteShape(line);
            shape.FillBrush = compositor.CreateColorBrush(ShelfColors.Of(Theme.LineStrong));
            rule.Shapes.Add(shape);
            rule.IsVisible = false;
            Root.Children.InsertAtTop(rule);
            frame.Children.InsertAtTop(Root);
        }

        public ContainerVisual Root { get; }

        public TextVisual Text { get; }

        /// <summary>The second run on the row: a running timer's time left, or what the glance says after the time.</summary>
        public TextVisual Second { get; }

        /// <summary>The glance's rule at this x of the view's frame, or none.</summary>
        public void Rule(float? x)
        {
            rule.IsVisible = x is not null;
            rule.Offset = new System.Numerics.Vector3(x ?? 0, (float)(IslandGeometry.Compact.Depth - CompactLayout.RuleHeight) / 2, 0);
        }

        /// <summary>An x of island space as this view's frame measures it: from the pill's trailing end, or from its centre.</summary>
        public float At(float x) => fromEnd ? x - CompactLayout.End : x;

        public System.Drawing.RectangleF Place(System.Drawing.RectangleF slot) => slot with { X = At(slot.X) };

        public void Dispose()
        {
            frame.Children.Remove(Root);
            Text.Dispose();
            Second.Dispose();
            rule.Dispose();
            Root.Dispose();
        }
    }
}
