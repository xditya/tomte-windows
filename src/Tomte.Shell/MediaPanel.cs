using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Media;
using Windows.UI.Composition;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>
/// The Media tab: album art, a progress bar and previous, play or pause, next, all composition visuals
/// laid out by <see cref="MediaPanelLayout"/>. It exists only while the tab shows. The bar moves as one
/// compositor animation from the position now to the end over the time left, restarted whenever the
/// app reports something; nothing in this process ticks for it.
/// </summary>
internal sealed class MediaPanel(IslandWindow island, MediaTracker tracker, Func<DateTimeOffset> now, Action<string>? log) : IPanel
{
    private View? view;
    private bool drooped;

    public PanelId Id => PanelId.Media;

    public void Shown(ContainerVisual body, Vector2 size)
    {
        view = new View(island.Compositor, body, size, log);
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

    private void Update()
    {
        var playing = tracker.Current;
        var at = now();
        view?.Update(playing, at);
        log?.Invoke(playing.HasSession
            ? $"media: {playing.App} {(playing.IsPlaying ? "playing" : "paused")} \"{playing.Title}\" at {(playing.PositionAt(at) is { } p ? NowPlaying.Clock(p) : "no timeline")}"
            : "media: nothing playing");
    }

    private sealed class View : IDisposable
    {
        // The icons are drawn in a 24 unit box, 14 DIPs wide, centred in their button.
        private const float GlyphBox = 24, GlyphSize = 14;
        private const float TitleBarHeight = 10;

        private static readonly Color White = Color.FromArgb(255, 255, 255, 255);
        private static readonly Color Dim = Color.FromArgb(97, 255, 255, 255);
        private static readonly Color Black = Color.FromArgb(255, 0, 0, 0);
        private static readonly Color Clear = Color.FromArgb(0, 0, 0, 0);

        private readonly Compositor compositor;
        private readonly Action<string>? log;
        private readonly ArtVisual art;
        private readonly ShapeVisual shapes;

        // One brush per part, recoloured as the state changes. A brush taken off its last shape and put back
        // later does not draw again (seen on Windows 11 26200), so brushes are never swapped or removed.
        private readonly CompositionColorBrush previous, next, circle, play, pause, track, fill;
        private readonly CompositionRoundedRectangleGeometry fillShape, titleBar;
        private readonly Vector2KeyFrameAnimation advance;

        public View(Compositor compositor, ContainerVisual body, Vector2 size, Action<string>? log)
        {
            this.compositor = compositor;
            this.log = log;
            art = new ArtVisual(compositor, MediaPanelLayout.Art, MediaPanelLayout.ArtCorner);
            body.Children.InsertAtTop(art.Visual);

            shapes = compositor.CreateShapeVisual();
            shapes.Size = size;
            body.Children.InsertAtTop(shapes);

            var slot = MediaPanelLayout.Title;
            titleBar = Rounded(new RectangleF(slot.X, slot.Y + (slot.Height - TitleBarHeight) / 2, 0, TitleBarHeight), TitleBarHeight / 2);
            Add(shapes.Shapes, titleBar, compositor.CreateColorBrush(Color.FromArgb(28, 255, 255, 255)));

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

        public void Update(NowPlaying playing, DateTimeOffset at)
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

            DrawTextRuns(playing);
        }

        public void Dispose()
        {
            fillShape.StopAnimation("Size");
            art.Dispose();
            shapes.Dispose();
        }

        /// <summary>
        /// TEXT SEAM. Nothing draws text on the island yet; which text surface to use is an open owner
        /// decision. The renderer draws these runs here, each clipped to its slot in MediaPanelLayout:
        ///   Title:   playing.Title, or NowPlaying.NothingPlaying when there is no session. 14 DIP semibold,
        ///            white, one line, ellipsis.
        ///   Artist:  playing.Artist, or playing.App when the artist is empty; tracker.Failure instead
        ///            while a command has just failed. 12 DIP, 60% white.
        ///   Elapsed: NowPlaying.Clock(playing.PositionAt(now)). 11 DIP tabular, 38% white. It changes
        ///            every second while playing, so the renderer needs one redraw a second while this shows.
        ///   Total:   NowPlaying.Clock(playing.Duration). Both clock slots stay empty with no timeline.
        /// Until then a neutral bar stands where the title goes, shorter when nothing plays.
        /// </summary>
        private void DrawTextRuns(NowPlaying playing) => titleBar.Size = new Vector2(playing.HasSession ? 140 : 96, TitleBarHeight);

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
