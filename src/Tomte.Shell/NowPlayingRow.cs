using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Media;
using Tomte.Core.Rambler;
using Tomte.Core.Triggers;
using Windows.UI.Composition;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>
/// The Compact pill's now-playing row: the art, the title and four bars that move while something
/// plays. It shows when the pill is up for an edge push, desktop peek or the track-change event, and
/// only while something plays. The media tracker is watched from the moment the pill opens until it
/// closes; the visuals exist only while the row shows.
/// the row assumes it has the pill's one line to itself. When the timer ring or the shelf
/// count land, something has to rank them (a running timer first, then media, then the shelf count).
/// </summary>
internal sealed class NowPlayingRow
{
    // Island space, x from the centre line. The pill is 220 wide: the mascot (8 + 26) and the row's 9 DIP
    // gap come first on the left, and 12 DIPs stay free on the right.
    private const float BarsLeft = 80, BarsTop = 9, BarsHeight = 20;
    private static readonly RectangleF Art = new(-67, 8, 22, 22);
    private static readonly RectangleF Title = new(-36, 9, 107, 20);

    // The four bars: how far each one reaches and how long one of its breaths takes.
    private static readonly float[] Reach = [.35f, .62f, .85f, 1f];
    private static readonly double[] Period = [.9, 1.07, 1.24, .9];

    private readonly IslandWindow island;
    private readonly TriggerController triggers;
    private readonly MediaTracker tracker;
    private readonly Action<string>? log;
    private readonly ScalarKeyFrameAnimation fadeIn, fadeOut;
    private readonly Windows.System.DispatcherQueueTimer release;
    private View? view, leaving;
    private bool watching, ready;

    public NowPlayingRow(IslandWindow island, TriggerController triggers, MediaTracker tracker, Action<string>? log)
    {
        this.island = island;
        this.triggers = triggers;
        this.tracker = tracker;
        this.log = log;
        fadeIn = PanelHost.Fade(island.Compositor, 1, PanelHost.FadeInMs);
        fadeOut = PanelHost.Fade(island.Compositor, 0, PanelHost.FadeOutMs);
        release = PanelHost.AfterFadeOut(island, Release);
        triggers.Machine.Changed += OnState;
        island.ContentReady += OnContentReady;
    }

    /// <summary>The track-change event raised last. The pill that shows for it is this row's to fill.</summary>
    public IslandEvent? Announced { get; set; }

    private void OnState(IslandState from, IslandState to)
    {
        if (to == IslandState.Compact)
        {
            ready = false;
            if (!watching)
            {
                watching = true;
                tracker.Changed += Update;
                tracker.Watch();
            }
        }
        else if (watching)
        {
            watching = false;
            tracker.Changed -= Update;
            tracker.Unwatch();
            Update();
        }
    }

    private void OnContentReady()
    {
        if (watching)
        {
            ready = true;
            Update();
        }
    }

    private void Update()
    {
        var playing = tracker.Current;
        var reason = triggers.Compact;
        bool mine = reason is CompactReason.Summary or CompactReason.Peek
            || (reason == CompactReason.Event && Announced is not null && ReferenceEquals(triggers.ShownEvent, Announced));
        if (!(watching && ready && mine && playing.IsPlaying))
        {
            if (view is not null)
            {
                // Fades out over 0.1 s, then goes with its surface: on a timer, since a compositor that is
                // not presenting (display asleep) completes no animation.
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
            // The peek pill is ambient and can stay up for hours, so nothing moves in it: no bars.
            view = new View(island.Compositor, bars: reason != CompactReason.Peek);
            view.Root.Opacity = 0;
            island.Dips.Children.InsertAtTop(view.Root);
            view.Root.StartAnimation("Opacity", fadeIn);
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
            island.Dips.Children.Remove(gone.Root);
            gone.Dispose();
        }
    }

    private sealed class View : IDisposable
    {
        private const float TitleBarHeight = 10;
        private readonly ShapeVisual shapes;
        private readonly CompositionRoundedRectangleGeometry titleBar;

        public View(Compositor compositor, bool bars)
        {
            Root = compositor.CreateContainerVisual();
            Art = new ArtVisual(compositor, NowPlayingRow.Art, 6);
            Root.Children.InsertAtTop(Art.Visual);

            // One shape visual from the title slot's left edge to the pill's right padding.
            shapes = compositor.CreateShapeVisual();
            shapes.Offset = new Vector3(Title.X, 0, 0);
            shapes.Size = new Vector2(BarsLeft + Reach.Length * (Waveform.BarWidth + Waveform.Gap) - Title.X, (float)IslandGeometry.Compact.Depth);
            Root.Children.InsertAtTop(shapes);

            titleBar = compositor.CreateRoundedRectangleGeometry();
            titleBar.Offset = new Vector2(0, Title.Y + (Title.Height - TitleBarHeight) / 2);
            titleBar.CornerRadius = new Vector2(TitleBarHeight / 2);
            var title = compositor.CreateSpriteShape(titleBar);
            title.FillBrush = compositor.CreateColorBrush(Color.FromArgb(28, 255, 255, 255));
            shapes.Shapes.Add(title);

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
                float x = BarsLeft - Title.X + i * (Waveform.BarWidth + Waveform.Gap), reach = Reach[i];
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
        /// TEXT SEAM. Nothing draws text on the island yet; which text surface to use is an open owner
        /// decision. The renderer draws one run here, clipped to the Title slot: playing.Title, 13 DIP
        /// semibold, white, one line, ellipsis. Until then a neutral bar stands where the title goes.
        /// </summary>
        public void DrawTextRuns(NowPlaying playing) => titleBar.Size = new Vector2(playing.Title.Length > 0 ? 84 : 0, TitleBarHeight);

        public void Dispose()
        {
            Art.Dispose();
            shapes.Dispose();
            Root.Dispose();
        }
    }
}
