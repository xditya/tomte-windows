using Tomte.Core;
using Tomte.Core.Triggers;
using Windows.UI.Composition;

namespace Tomte.Shell;

/// <summary>
/// The words on the Compact pill: the first-run hint, an event's line (after the ring on the timer's done pill)
/// and the time a running timer has left beside its ring. What it says comes from
/// <see cref="TriggerController.CompactLine"/>. It shows from ContentReady on, follows changes the triggers make
/// to the pill, and fades out with the next state change, like the now-playing row. Only the countdown ticks:
/// one one-shot timer to the next whole second, while it shows.
/// </summary>
internal sealed class CompactLine
{
    private readonly IslandWindow island;
    private readonly TriggerController triggers;
    private readonly TextRenderer text;
    private readonly ScalarKeyFrameAnimation fadeIn, fadeOut;
    private readonly Windows.System.DispatcherQueueTimer release, tick;
    private View? view, leaving;
    private bool ready;

    public CompactLine(IslandWindow island, TriggerController triggers, TextRenderer text)
    {
        this.island = island;
        this.triggers = triggers;
        this.text = text;
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
    }

    private void Update()
    {
        tick.Stop();
        var (line, left) = ready && triggers.Machine.State == IslandState.Compact ? triggers.CompactLine(DateTimeOffset.Now) : (null, null);
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
        view.Text.Show(line, left is null ? CompactLayout.LineStyle : CompactLayout.ClockStyle, CompactLayout.Line(island.Mascot.Enabled, ring));

        if (left is { } l && l > TimeSpan.Zero)
        {
            // The countdown reads whole seconds rounded up, so it changes as the time left passes one.
            double fraction = l.TotalSeconds - Math.Floor(l.TotalSeconds);
            tick.Interval = TimeSpan.FromSeconds((fraction > 0 ? fraction : 1) + 0.01);
            tick.Start();
        }
    }

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
