using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Media;
using Tomte.Core.Rambler;
using Tomte.Core.Text;
using Tomte.Core.Triggers;
using Windows.UI.Composition;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>
/// The Compact pill's now-playing row: the art, the title and four bars that move while something
/// plays. It shows where <see cref="TriggerController.CompactShows"/> puts it: the push pill when no
/// timer runs, the peek pill with the tomte off, the track-change event's own pill and the pill kept
/// while music plays. The media
/// tracker is watched while such a pill is up; the visuals exist only while the row shows.
/// </summary>
internal sealed class NowPlayingRow
{
    // Island space, x from the centre line; where things sit is CompactLayout's.
    private const float BarsLeft = CompactLayout.BarsLeft, BarsTop = CompactLayout.BarsTop, BarsHeight = CompactLayout.BarsHeight;

    // The four bars: how far each one reaches and how long one of its breaths takes.
    private static readonly float[] Reach = CompactLayout.BarReach;
    private static readonly double[] Period = [.9, 1.07, 1.24, .9];

    private readonly IslandWindow island;
    private readonly TriggerController triggers;
    private readonly MediaTracker tracker;
    private readonly TextRenderer text;
    private readonly Action<string>? log;
    private readonly ScalarKeyFrameAnimation fadeIn, fadeOut;
    private readonly Windows.System.DispatcherQueueTimer release;
    private View? view, leaving;
    private bool watching, ready;

    /// <summary>Whether the row is on the pill; the line keeps the pill's time of day off it meanwhile.</summary>
    public bool Showing => view is not null;

    /// <summary>Raised when <see cref="Showing"/> changes.</summary>
    public event Action? ShowingChanged;

    public NowPlayingRow(IslandWindow island, TriggerController triggers, MediaTracker tracker, TextRenderer text, Action<string>? log)
    {
        this.island = island;
        this.triggers = triggers;
        this.tracker = tracker;
        this.text = text;
        this.log = log;
        fadeIn = PanelHost.Fade(island.Compositor, 1, PanelHost.FadeInMs);
        fadeOut = PanelHost.Fade(island.Compositor, 0, PanelHost.FadeOutMs);
        release = PanelHost.AfterFadeOut(island, Release);
        triggers.Machine.Changed += OnState;
        island.ContentReady += OnContentReady;
    }

    // Why the pill is up is settled once it is up, so the row decides at ContentReady whether to watch:
    // a pill that would show the row if something played.
    private bool Mine => triggers.CompactShows(playing: true) == CompactContent.NowPlaying;

    private void OnState(IslandState from, IslandState to)
    {
        ready = false;
        if (watching)
        {
            watching = false;
            tracker.Changed -= Update;
            tracker.Unwatch(timeline: false, art: true);
        }

        Update();
    }

    /// <summary>
    /// Watch only for the row's own pills: the first-run hint, the rambler, a push pill with a running
    /// timer or another feature's event never ask for the media session manager. Art yes, positions no:
    /// the row draws no bar.
    /// </summary>
    private void OnContentReady()
    {
        if (triggers.Machine.State != IslandState.Compact || !Mine)
        {
            return;
        }

        ready = true;
        if (!watching)
        {
            watching = true;
            tracker.Changed += Update;
            tracker.Watch(timeline: false, art: true);
        }

        Update();
    }

    private void Update()
    {
        var playing = tracker.Current;
        var reason = triggers.Compact;
        if (!(watching && ready && triggers.CompactShows(playing.IsPlaying) == CompactContent.NowPlaying))
        {
            if (view is not null)
            {
                // Fades out over 0.1 s, then goes with its surface: on a timer, since a compositor that is
                // not presenting (display asleep) completes no animation.
                Release();
                (leaving, view) = (view, null);
                leaving.Root.StartAnimation("Opacity", fadeOut);
                release.Start();
                ShowingChanged?.Invoke();
            }

            return;
        }

        if (view is null)
        {
            Release();
            // The peek pill and the pill kept while music plays are ambient and can stay up for hours, so nothing
            // moves in them: no bars.
            bool mascot = island.Mascot.Enabled, bars = reason is not (CompactReason.Peek or CompactReason.Playing);
            // Without bars the art and its title end the pill as one group, so they go with its trailing end.
            view = new View(island.Compositor, text, mascot && !bars ? island.Trailing : island.Dips, mascot, bars);
            view.Root.Opacity = 0;
            view.Root.StartAnimation("Opacity", fadeIn);
            ShowingChanged?.Invoke();
        }

        view.Art.Show(playing.Thumbnail, log);
        view.DrawTextRuns(playing);
    }

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
        private readonly ShapeVisual shapes;
        private readonly TextVisual title;
        private readonly bool mascot, bars;
        private RectangleF titleSlot;

        public View(Compositor compositor, TextRenderer text, ContainerVisual frame, bool mascot, bool bars)
        {
            Root = compositor.CreateContainerVisual();
            (this.frame, this.mascot, this.bars) = (frame, mascot, bars);
            frame.Children.InsertAtTop(Root);
            Art = new ArtVisual(compositor, CompactLayout.Art(mascot), CompactLayout.ArtCorner);
            Root.Children.InsertAtTop(Art.Visual);
            title = text.Create(Root);
            titleSlot = CompactLayout.Title(mascot);

            // One shape visual from the title slot's left edge to the end of the bars.
            shapes = compositor.CreateShapeVisual();
            shapes.Offset = new Vector3(titleSlot.X, 0, 0);
            shapes.Size = new Vector2(BarsLeft + Reach.Length * (Waveform.BarWidth + Waveform.Gap) - titleSlot.X, (float)IslandGeometry.Compact.Depth);
            Root.Children.InsertAtTop(shapes);

            if (!bars)
            {
                return;
            }

            // Each bar breathes on its own period, 50 ms after the one before it, from the compositor:
            // 4 DIPs, up to 4 + 18 x reach, down to 6 + 8 x reach, up to 4 + 16 x reach, back to 4.
            var white = compositor.CreateColorBrush(Color.FromArgb(255, 255, 255, 255));
            var ease = compositor.CreateCubicBezierEasingFunction(new Vector2(0.42f, 0), new Vector2(0.58f, 1));
            for (int i = 0; i < Reach.Length; i++)
            {
                float x = BarsLeft - titleSlot.X + i * (Waveform.BarWidth + Waveform.Gap), reach = Reach[i];
                var bar = compositor.CreateRoundedRectangleGeometry();
                bar.CornerRadius = new Vector2(Waveform.BarWidth / 2);
                var shape = compositor.CreateSpriteShape(bar);
                shape.FillBrush = white;
                shapes.Shapes.Add(shape);

                var size = compositor.CreateVector2KeyFrameAnimation();
                var offset = compositor.CreateVector2KeyFrameAnimation();
                ReadOnlySpan<float> heights =
                    [Waveform.MinHeight, Waveform.MinHeight + Waveform.Swing * reach, 6 + 8 * reach, Waveform.MinHeight + 16 * reach, Waveform.MinHeight];
                for (int k = 0; k < heights.Length; k++)
                {
                    size.InsertKeyFrame(k / 4f, new Vector2(Waveform.BarWidth, heights[k]), ease);
                    offset.InsertKeyFrame(k / 4f, new Vector2(x, BarsTop + (BarsHeight - heights[k]) / 2), ease);
                }

                foreach (var animation in (ReadOnlySpan<KeyFrameAnimation>)[size, offset])
                {
                    animation.Duration = TimeSpan.FromSeconds(Period[i]);
                    animation.DelayTime = TimeSpan.FromMilliseconds(50 * i);
                    animation.IterationBehavior = AnimationIterationBehavior.Forever;
                }

                bar.Size = new Vector2(Waveform.BarWidth, heights[0]);
                shape.Offset = new Vector2(x, BarsTop + (BarsHeight - heights[0]) / 2);
                bar.StartAnimation("Size", size);
                shape.StartAnimation("Offset", offset);
            }
        }

        public ContainerVisual Root { get; }

        public ArtVisual Art { get; }

        /// <summary>
        /// The title in its slot: 13 DIP semibold, white, one line, ellipsis. With no bars the art and the title are one
        /// group, placed from the title's width, in the trailing frame with the tomte on.
        /// </summary>
        public void DrawTextRuns(NowPlaying playing)
        {
            if (!bars)
            {
                var art = CompactLayout.Art(mascot);
                var (lead, x, width) = CompactLayout.Group(mascot, art.Width, TextSlot.Measure(playing.Title, CompactLayout.TitleStyle, 4096));
                float end = mascot ? CompactLayout.End : 0;
                Art.Visual.Offset = new Vector3(lead - end, art.Y, 0);
                titleSlot = new RectangleF(x - end, titleSlot.Y, width, titleSlot.Height);
            }

            title.Show(playing.Title, CompactLayout.TitleStyle, titleSlot);
        }

        public void Dispose()
        {
            frame.Children.Remove(Root);
            title.Dispose();
            Art.Dispose();
            shapes.Dispose();
            Root.Dispose();
        }
    }
}
