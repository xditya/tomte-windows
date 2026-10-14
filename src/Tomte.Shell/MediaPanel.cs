using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Media;
using Windows.UI.Composition;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>
/// The Media tab: album art, title, artist, the two clocks, a progress bar and previous, play or pause,
/// next, all composition visuals laid out by <see cref="MediaPanelLayout"/>. It exists only while the tab
/// shows. The bar moves as one compositor animation from the position now to the end over the time left,
/// restarted whenever the app reports something; the elapsed clock is drawn again once a second while
/// something plays, and nothing else in this process ticks for it.
/// </summary>
internal sealed class MediaPanel(IslandWindow island, MediaTracker tracker, TextRenderer text, Func<DateTimeOffset> now, Action<string>? log) : IPanel
{
    private View? view;
    private bool drooped;

    public PanelId Id => PanelId.Media;

    public void Shown(ContainerVisual body, Vector2 size)
    {
        view = new View(island, text, body, size, now, log);
        tracker.Changed += Update;
        tracker.Watch();
        Update();
    }

    public void Hidden()
    {
        tracker.Changed -= Update;
        tracker.Unwatch();
        view?.Dispose();
        view = null;
        drooped = false;
    }

    public async void Press(Vector2 point)
    {
        var command = MediaPanelLayout.Command(point, tracker.Current);
        if (command == MediaCommand.None)
        {
            return;
        }

        bool accepted = await tracker.SendAsync(command);
        if (view is null)
        {
            return;
        }

        // A command that did nothing is the mascot's blocked action: the cap droops until one works.
        if (!accepted)
        {
            island.Mascot.Set(MascotState.Error);
            drooped = true;
        }
        else if (drooped)
        {
            island.Mascot.Set(MascotState.Compact);
            drooped = false;
        }
    }

    /// <summary>Nothing here scrolls.</summary>
    public void Wheel(Vector2 point, int delta)
    {
    }

    private void Update()
    {
        var playing = tracker.Current;
        var at = now();
        view?.Update(playing, tracker.Failure, at);
        log?.Invoke(playing.HasSession
            ? $"media: {playing.App} {(playing.IsPlaying ? "playing" : "paused")} \"{playing.Title}\" at {(playing.PositionAt(at) is { } p ? NowPlaying.Clock(p) : "no timeline")}"
            : "media: nothing playing");
    }

    private sealed class View : IDisposable
    {
        // The icons are drawn in a 24 unit box, 14 DIPs wide, centred in their button.
        private const float GlyphBox = 24, GlyphSize = 14;

        private static readonly Color White = Color.FromArgb(255, 255, 255, 255);
        private static readonly Color Dim = Color.FromArgb(97, 255, 255, 255);
        private static readonly Color Black = Color.FromArgb(255, 0, 0, 0);
        private static readonly Color Clear = Color.FromArgb(0, 0, 0, 0);

        private readonly Compositor compositor;
        private readonly Func<DateTimeOffset> now;
        private readonly Action<string>? log;
        private readonly ArtVisual art;
        private readonly ShapeVisual shapes;
        private readonly TextVisual title, artist, elapsed, total;
        private readonly Windows.System.DispatcherQueueTimer clock;
        private NowPlaying shown = NowPlaying.None;

        // One brush per part, recoloured as the state changes. A brush taken off its last shape and put back
        // later does not draw again (seen on Windows 11 26200), so brushes are never swapped or removed.
        private readonly CompositionColorBrush previous, next, circle, play, pause, track, fill;
        private readonly CompositionRoundedRectangleGeometry fillShape;
        private readonly Vector2KeyFrameAnimation advance;

        public View(IslandWindow island, TextRenderer text, ContainerVisual body, Vector2 size, Func<DateTimeOffset> now, Action<string>? log)
        {
            compositor = island.Compositor;
            this.now = now;
            this.log = log;
            art = new ArtVisual(compositor, MediaPanelLayout.Art, MediaPanelLayout.ArtCorner);
            body.Children.InsertAtTop(art.Visual);

            shapes = compositor.CreateShapeVisual();
            shapes.Size = size;
            body.Children.InsertAtTop(shapes);

            (title, artist, elapsed, total) = (text.Create(body), text.Create(body), text.Create(body), text.Create(body));
            clock = island.Queue.CreateTimer();
            clock.IsRepeating = false;
            clock.Tick += (_, _) => ShowClocks();

            var bar = MediaPanelLayout.Bar;
            track = Add(shapes.Shapes, Rounded(bar, bar.Height / 2), compositor.CreateColorBrush());
            fillShape = Rounded(bar with { Width = 0 }, bar.Height / 2);
            fill = Add(shapes.Shapes, fillShape, compositor.CreateColorBrush());
            advance = compositor.CreateVector2KeyFrameAnimation();
            advance.InsertKeyFrame(1, new Vector2(bar.Width, bar.Height), compositor.CreateLinearEasingFunction());

            var glyph = Glyph(MediaPanelLayout.Previous);
            previous = compositor.CreateColorBrush();
            Add(glyph.Shapes, Bar(6, 4, 2, 16), previous);
            Add(glyph.Shapes, Triangle(new(20, 4), new(9, 12), new(20, 20)), previous);

            var button = MediaPanelLayout.PlayPause;
            var disc = compositor.CreateEllipseGeometry();
            disc.Center = new Vector2(button.X + button.Width / 2, button.Y + button.Height / 2);
            disc.Radius = new Vector2(button.Width / 2);
            circle = Add(shapes.Shapes, disc, compositor.CreateColorBrush());
            glyph = Glyph(button);
            play = Add(glyph.Shapes, Triangle(new(7, 4), new(21, 12), new(7, 20)), compositor.CreateColorBrush());
            pause = compositor.CreateColorBrush();
            Add(glyph.Shapes, Bar(6, 4, 4, 16), pause);
            Add(glyph.Shapes, Bar(14, 4, 4, 16), pause);

            glyph = Glyph(MediaPanelLayout.Next);
            next = compositor.CreateColorBrush();
            Add(glyph.Shapes, Bar(16, 4, 2, 16), next);
            Add(glyph.Shapes, Triangle(new(4, 4), new(15, 12), new(4, 20)), next);
        }

        public void Update(NowPlaying playing, string? failure, DateTimeOffset at)
        {
            art.Show(playing.Thumbnail, log);

            // A command the app does not accept is dimmed, and a press on it sends nothing.
            previous.Color = playing.Accepts(MediaCommand.Previous) ? White : Dim;
            next.Color = playing.Accepts(MediaCommand.Next) ? White : Dim;
            circle.Color = playing.PlayPause == MediaCommand.None ? Dim : White;
            play.Color = playing.IsPlaying ? Clear : Black;
            pause.Color = playing.IsPlaying ? Black : Clear;

            // An app with no timeline gets no bar at all, rather than an empty one that never moves.
            var bar = MediaPanelLayout.Bar;
            fillShape.StopAnimation("Size");
            track.Color = playing.Timeline is null ? Clear : Color.FromArgb(41, 255, 255, 255);
            fill.Color = playing.Timeline is null ? Clear : White;
            if (playing.Timeline is { } timeline)
            {
                fillShape.Size = new Vector2(bar.Width * (float)timeline.ProgressAt(at), bar.Height);
                if (timeline.RemainingAt(at) is { } left && left > TimeSpan.FromMilliseconds(1))
                {
                    advance.Duration = left;
                    fillShape.StartAnimation("Size", advance);
                }
            }

            DrawTextRuns(playing, failure);
        }

        public void Dispose()
        {
            clock.Stop();
            fillShape.StopAnimation("Size");
            art.Dispose();
            shapes.Dispose();
            title.Dispose();
            artist.Dispose();
            elapsed.Dispose();
            total.Dispose();
        }

        /// <summary>
        /// Each run one line in its slot, cut with an ellipsis. The title reads "Nothing playing" with no
        /// session; the artist line falls back to the app's name, and a command that just failed says so in
        /// its place, in the warn colour.
        /// </summary>
        private void DrawTextRuns(NowPlaying playing, string? failure)
        {
            title.Show(playing.HasSession ? playing.Title : NowPlaying.NothingPlaying, MediaPanelLayout.TitleStyle, MediaPanelLayout.Title);
            artist.Show(failure ?? (playing.Artist.Length > 0 ? playing.Artist : playing.App), failure is null ? MediaPanelLayout.ArtistStyle : MediaPanelLayout.FailedStyle, MediaPanelLayout.Artist);
            shown = playing;
            ShowClocks();
        }

        /// <summary>
        /// Elapsed and total, both empty with no timeline. While the position moves, a one-shot timer comes
        /// back as it passes the next whole second, which is when the elapsed clock reads differently.
        /// </summary>
        private void ShowClocks()
        {
            clock.Stop();
            var at = now();
            var timeline = shown.Timeline;
            elapsed.Show(timeline is { } t ? NowPlaying.Clock(t.PositionAt(at)) : "", MediaPanelLayout.ElapsedStyle, MediaPanelLayout.Elapsed);
            total.Show(timeline is { } d ? NowPlaying.Clock(d.Duration) : "", MediaPanelLayout.TotalStyle, MediaPanelLayout.Total);
            if (timeline is { Playing: true, Rate: > 0 } moving && moving.PositionAt(at) < moving.End)
            {
                double seconds = moving.PositionAt(at).TotalSeconds;
                clock.Interval = TimeSpan.FromSeconds((Math.Floor(seconds) + 1 - seconds) / moving.Rate + 0.01);
                clock.Start();
            }
        }

        private CompositionColorBrush Add(CompositionShapeCollection into, CompositionGeometry geometry, CompositionColorBrush brush)
        {
            var shape = compositor.CreateSpriteShape(geometry);
            shape.FillBrush = brush;
            into.Add(shape);
            return brush;
        }

        private CompositionContainerShape Glyph(RectangleF button)
        {
            var glyph = compositor.CreateContainerShape();
            glyph.Scale = new Vector2(GlyphSize / GlyphBox);
            glyph.Offset = new Vector2(button.X + (button.Width - GlyphSize) / 2, button.Y + (button.Height - GlyphSize) / 2);
            shapes.Shapes.Add(glyph);
            return glyph;
        }

        private CompositionRoundedRectangleGeometry Rounded(RectangleF rect, float radius)
        {
            var geometry = compositor.CreateRoundedRectangleGeometry();
            geometry.Offset = new Vector2(rect.X, rect.Y);
            geometry.Size = new Vector2(rect.Width, rect.Height);
            geometry.CornerRadius = new Vector2(radius);
            return geometry;
        }

        private CompositionRectangleGeometry Bar(float x, float y, float width, float height)
        {
            var geometry = compositor.CreateRectangleGeometry();
            geometry.Offset = new Vector2(x, y);
            geometry.Size = new Vector2(width, height);
            return geometry;
        }

        private CompositionPathGeometry Triangle(Vector2 a, Vector2 b, Vector2 c) =>
            compositor.CreatePathGeometry(IslandWindow.BuildPath([OutlineSegment.Move(a), OutlineSegment.Line(b), OutlineSegment.Line(c)], 1));
    }
}
