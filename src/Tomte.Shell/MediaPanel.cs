using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Media;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.SystemServices;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>
/// The Media tab: album art, title, artist, the two clocks, a progress bar and previous, play or pause,
/// next, all composition visuals laid out by <see cref="MediaPanelLayout"/>. It exists only while the tab
/// shows. The bar moves as one compositor animation from the position now to the end over the time left,
/// restarted whenever the app reports something; the elapsed clock is drawn again once a second while
/// something plays, and nothing else in this process ticks for it. While the app takes a position the bar
/// is a seek bar: the pointer on it shows the knob and the time under the pointer, a click seeks there, a drag
/// moves the knob and seeks where it lets go, and with the keys Left and Right move 5 s, Home and End go to the
/// ends. Clicks and keys come through the island's interaction host.
/// </summary>
internal sealed class MediaPanel(IslandWindow island, MediaTracker tracker, TextRenderer text, Func<DateTimeOffset> now, Action<string>? log) : IPanel, IInteractive
{
    private View? view;
    private float bodyWidth;

    // The body's slots, for the width the panel shows at.
    private MediaPanelLayout layout = new((float)PanelSize.Media.W);
    private IReadOnlyList<Target> targets = [];
    private Vector2? pressAt;

    // The last seek shown: until the app reports another timeline, or for 2 s, the bar and the keys count from it.
    private PendingSeek? sought;

    // A key's seek waiting for the gap after the last one sent to pass.
    private TimeSpan? queued;
    private Windows.System.DispatcherQueueTimer? settle, keyGap;
    private bool drooped;

    public PanelId Id => PanelId.Media;

    public IReadOnlyList<Target> Targets => targets;

    public event Action? TargetsChanged;

    public void Shown(ContainerVisual body, Vector2 size)
    {
        layout = new MediaPanelLayout(size.X);
        view = new View(island, text, body, size, layout, now, log);
        bodyWidth = size.X;
        // With no report 2 s after a seek, the bar goes back to the last position the app gave.
        settle ??= OneShot(PendingSeek.Wait, Update);
        keyGap ??= OneShot(PendingSeek.KeyGap, SendQueued);
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
        (drooped, pressAt, sought, queued) = (false, null, null, null);
        settle?.Stop();
        keyGap?.Stop();
        targets = [];
        TargetsChanged?.Invoke();
    }

    /// <summary>Presses come through the targets.</summary>
    public void Press(Vector2 point)
    {
    }

    /// <summary>Nothing here scrolls.</summary>
    public void Wheel(Vector2 point, int delta)
    {
    }

    /// <summary>A transport button sends its command; a click on the seek bar (not Enter on it) seeks to where it was pressed.</summary>
    public void Activate(string id)
    {
        if (id != MediaPanelLayout.SeekId)
        {
            Send(MediaPanelLayout.Command(id, tracker.Current));
        }
        else if (pressAt is { } at && tracker.Current is { CanSeek: true, Timeline: { } timeline })
        {
            Seek(layout.SeekAt(at.X, timeline));
        }

        pressAt = null;
    }

    /// <summary>
    /// A press on the seek bar: kept for the click it may be, then a drag once the pointer leaves the drag distance,
    /// which seeks where the button comes up.
    /// </summary>
    public bool TryDrag(string id, Vector2 at)
    {
        pressAt = at;
        if (id != MediaPanelLayout.SeekId || view is null || tracker.Current is not { CanSeek: true, Timeline: { } timeline }
            || !PInvoke.GetCursorPos(out var screen))
        {
            return false;
        }

        if (!PInvoke.DragDetect(island.Hwnd, screen))
        {
            // Esc ends the wait with the button still down, which is no click.
            if (PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_LBUTTON) >= 0)
            {
                return false;
            }

            pressAt = null;
            return true;
        }

        pressAt = null;
        if (Scrub(timeline) is { } to)
        {
            Seek(to);
        }

        return true;
    }

    public void Hovered(string? id) => view?.Hover(id == MediaPanelLayout.SeekId);

    public void PointerMoved(string id, Vector2 at)
    {
        if (id == MediaPanelLayout.SeekId && tracker.Current.Timeline is { } timeline)
        {
            view?.Label(at.X, layout.SeekAt(at.X, timeline));
        }
    }

    /// <summary>
    /// Space plays or pauses; on the transport buttons Left and Right skip tracks. On the seek bar, Left and Right move
    /// 5 s and Home and End go to the ends; repeated keys add up before the app reports back.
    /// </summary>
    public bool KeyDown(string id, NavKey key, bool repeat)
    {
        // Space plays or pauses from any of them, and Left and Right skip on the transport buttons; a held key sends once.
        if (MediaPanelLayout.KeyFor(id, key) is { } button)
        {
            if (!repeat)
            {
                Send(MediaPanelLayout.Command(button, tracker.Current));
            }

            return true;
        }

        if (id != MediaPanelLayout.SeekId || tracker.Current is not { CanSeek: true, Timeline: { } timeline })
        {
            return false;
        }

        var from = sought is { } last && last.Holds(timeline, now()) ? last.To : timeline.PositionAt(now());
        TimeSpan? to = key switch
        {
            NavKey.Left => MediaPanelLayout.Stepped(from, -1, timeline),
            NavKey.Right => MediaPanelLayout.Stepped(from, 1, timeline),
            NavKey.Home => timeline.Start,
            NavKey.End => timeline.End,
            _ => null,
        };
        if (to is not { } position)
        {
            return false;
        }

        view?.Label(layout.BarX(position, timeline), position);
        if (Show(position))
        {
            // A held key sends at most one seek per gap; the last position wins.
            queued = position;
            if (keyGap?.IsRunning != true)
            {
                SendQueued();
            }
        }

        return true;
    }

    /// <summary>
    /// The drag on the seek bar, from the drag distance until the button comes up: the fill, the knob and the time follow
    /// the pointer, and the position under it at the release is the answer. Esc, the mouse going to another window or
    /// the panel hiding end it with nothing to seek to. Every other message is dispatched as the island's loop would.
    /// </summary>
    private unsafe TimeSpan? Scrub(MediaTimeline timeline)
    {
        var hwnd = island.Hwnd;
        PInvoke.SetCapture(hwnd);
        try
        {
            MSG message;
            while (view is not null && PInvoke.GetCapture() == hwnd)
            {
                if (PInvoke.GetMessage(&message, HWND.Null, 0, 0).Value <= 0)
                {
                    // The app is quitting: the island's own loop has to see it too.
                    PInvoke.PostQuitMessage((int)message.wParam.Value);
                    return null;
                }

                // The island is never the foreground window, so a button let go off it can go unseen: a move with
                // the button up ends the drag with nothing to seek to.
                if (message.message == PInvoke.WM_MOUSEMOVE && (message.wParam.Value & (nuint)MODIFIERKEYS_FLAGS.MK_LBUTTON) == 0)
                {
                    return null;
                }

                if (message.message is PInvoke.WM_MOUSEMOVE or PInvoke.WM_LBUTTONUP && message.hwnd == hwnd)
                {
                    int x = (short)(message.lParam.Value & 0xFFFF), y = (short)((message.lParam.Value >> 16) & 0xFFFF);
                    var to = layout.SeekAt(PanelSpace.ToBody(island.ToIsland(x, y), bodyWidth).X, timeline);
                    if (message.message == PInvoke.WM_LBUTTONUP)
                    {
                        return to;
                    }

                    view?.Scrub(layout.BarX(to, timeline), to);
                    continue;
                }

                if (message.message == PInvoke.WM_KEYDOWN && message.wParam.Value == (nuint)VIRTUAL_KEY.VK_ESCAPE)
                {
                    return null;
                }

                PInvoke.TranslateMessage(&message);
                PInvoke.DispatchMessage(&message);
            }

            return null;
        }
        finally
        {
            if (PInvoke.GetCapture() == hwnd)
            {
                PInvoke.ReleaseCapture();
            }

            view?.Scrubbed();
        }
    }

    /// <summary>A click or a drag seeks at once.</summary>
    private void Seek(TimeSpan position)
    {
        if (Show(position))
        {
            SendSeek(position);
        }
    }

    /// <summary>The bar shows a seek before the app reports it; false when the app takes no position now.</summary>
    private bool Show(TimeSpan position)
    {
        if (tracker.Current is not { CanSeek: true, Timeline: { } timeline })
        {
            return false;
        }

        sought = new PendingSeek(position, timeline, now());
        view?.Hold(layout.BarX(position, timeline));
        settle?.Stop();
        settle?.Start();
        return true;
    }

    private void SendQueued()
    {
        if (queued is { } position)
        {
            queued = null;
            SendSeek(position);
            keyGap?.Start();
        }
    }

    private Windows.System.DispatcherQueueTimer OneShot(TimeSpan interval, Action tick)
    {
        var timer = island.Queue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = interval;
        timer.Tick += (_, _) => tick();
        return timer;
    }

    private async void SendSeek(TimeSpan position)
    {
        bool accepted = await tracker.SeekAsync(position);
        if (!accepted)
        {
            // Refused: the fill goes back to where the track is.
            sought = null;
            Update();
        }

        Done(accepted);
    }

    private async void Send(MediaCommand command)
    {
        if (command != MediaCommand.None)
        {
            Done(await tracker.SendAsync(command));
        }
    }

    // A command that did nothing is the mascot's blocked action: the cap droops until one works.
    private void Done(bool accepted)
    {
        if (view is null)
        {
            return;
        }

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
        view?.Update(playing, tracker.Failure, at);
        // A seek the app has not reported yet keeps the fill where it was sent, for a while.
        if (sought is { } last && last.Holds(playing.Timeline, at))
        {
            view?.Hold(layout.BarX(last.To, last.From));
        }
        else
        {
            sought = null;
        }

        var next = layout.Targets(playing);
        if (!next.SequenceEqual(targets))
        {
            targets = next;
            TargetsChanged?.Invoke();
        }

        log?.Invoke(playing.HasSession
            ? $"media: {playing.App} {(playing.IsPlaying ? "playing" : "paused")} \"{playing.Title}\" at {(playing.PositionAt(at) is { } p ? NowPlaying.Clock(p) : "no timeline")}"
            : "media: nothing playing");
    }

    private sealed class View : IDisposable
    {
        // The icons are drawn in a 24 unit box, 14 DIPs wide, centred in their button.
        private const float GlyphBox = 24, GlyphSize = 14;

        private static readonly Color White = Color.FromArgb(255, 255, 255, 255);
        // A button the app does not take is drawn at the disabled opacity.
        private static readonly Color Dim = Color.FromArgb((byte)(255 * StateLook.DisabledOpacity), 255, 255, 255);
        private static readonly Color Black = Color.FromArgb(255, 0, 0, 0);
        private static readonly Color Clear = Color.FromArgb(0, 0, 0, 0);
        private static readonly Color Card = Color.FromArgb(255, 0x2A, 0x2A, 0x2F);

        private readonly Compositor compositor;
        private readonly MediaPanelLayout layout;
        private readonly Func<DateTimeOffset> now;
        private readonly Action<string>? log;
        private readonly ArtVisual art;
        private readonly ShapeVisual shapes, seekShapes;
        private readonly TextVisual title, artist, elapsed, total, seekTime;
        private readonly Windows.System.DispatcherQueueTimer clock;
        private NowPlaying shown = NowPlaying.None;
        private bool hovered, scrubbing;

        // One brush per part, recoloured as the state changes. A brush taken off its last shape and put back
        // later does not draw again (seen on Windows 11 26200), so brushes are never swapped or removed.
        private readonly CompositionColorBrush previous, next, circle, play, pause, track, fill, knob, card;
        private readonly CompositionRoundedRectangleGeometry fillShape, cardShape;
        private readonly Vector2KeyFrameAnimation advance;

        public View(IslandWindow island, TextRenderer text, ContainerVisual body, Vector2 size, MediaPanelLayout layout, Func<DateTimeOffset> now, Action<string>? log)
        {
            compositor = island.Compositor;
            this.layout = layout;
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

            var bar = layout.Bar;
            track = Add(shapes.Shapes, Rounded(bar, bar.Height / 2), compositor.CreateColorBrush());
            fillShape = Rounded(bar with { Width = 0 }, bar.Height / 2);
            fill = Add(shapes.Shapes, fillShape, compositor.CreateColorBrush());
            advance = compositor.CreateVector2KeyFrameAnimation();
            advance.InsertKeyFrame(1, new Vector2(bar.Width, bar.Height), compositor.CreateLinearEasingFunction());

            // The knob rides the fill's end on the compositor, so it moves with the bar and nothing here ticks for it.
            var dot = compositor.CreateEllipseGeometry();
            dot.Radius = new Vector2(MediaPanelLayout.Knob);
            var ride = compositor.CreateExpressionAnimation("Vector2(x + fill.Size.X, y)");
            ride.SetReferenceParameter("fill", fillShape);
            ride.SetScalarParameter("x", bar.X);
            ride.SetScalarParameter("y", bar.Y + bar.Height / 2);
            dot.StartAnimation("Center", ride);
            knob = Add(shapes.Shapes, dot, compositor.CreateColorBrush());

            var glyph = Glyph(layout.Previous);
            previous = compositor.CreateColorBrush();
            Add(glyph.Shapes, Bar(6, 4, 2, 16), previous);
            Add(glyph.Shapes, Triangle(new(20, 4), new(9, 12), new(20, 20)), previous);

            var button = layout.PlayPause;
            var disc = compositor.CreateEllipseGeometry();
            disc.Center = new Vector2(button.X + button.Width / 2, button.Y + button.Height / 2);
            disc.Radius = new Vector2(MediaPanelLayout.Disc / 2);
            circle = Add(shapes.Shapes, disc, compositor.CreateColorBrush());
            glyph = Glyph(button);
            play = Add(glyph.Shapes, Triangle(new(7, 4), new(21, 12), new(7, 20)), compositor.CreateColorBrush());
            pause = compositor.CreateColorBrush();
            Add(glyph.Shapes, Bar(6, 4, 4, 16), pause);
            Add(glyph.Shapes, Bar(14, 4, 4, 16), pause);

            glyph = Glyph(layout.Next);
            next = compositor.CreateColorBrush();
            Add(glyph.Shapes, Bar(16, 4, 2, 16), next);
            Add(glyph.Shapes, Triangle(new(4, 4), new(15, 12), new(4, 20)), next);

            // The seek time's card and its clock, over everything else: it sits over the artist's line.
            seekShapes = compositor.CreateShapeVisual();
            seekShapes.Size = size;
            body.Children.InsertAtTop(seekShapes);
            cardShape = Rounded(default, 4);
            card = Add(seekShapes.Shapes, cardShape, compositor.CreateColorBrush());
            seekTime = text.Create(body);
        }

        /// <summary>The pointer or keyboard focus came onto the seek bar, or left it: the knob shows while it is there.</summary>
        public void Hover(bool on)
        {
            hovered = on;
            ShowKnob();
            if (!on && !scrubbing)
            {
                Label(null);
            }
        }

        /// <summary>The time a seek would go to, on its card over the bar at <paramref name="x"/>; null hides it.</summary>
        public void Label(float x, TimeSpan time) => Label((x, time));

        /// <summary>A drag holds the knob: the fill ends at <paramref name="x"/>, with the time over it.</summary>
        public void Scrub(float x, TimeSpan time)
        {
            scrubbing = true;
            Hold(x);
            ShowKnob();
            Label(x, time);
        }

        /// <summary>The drag ended; the knob stays only if the pointer is still on the bar.</summary>
        public void Scrubbed()
        {
            scrubbing = false;
            ShowKnob();
            Label(null);
        }

        /// <summary>The fill stops at <paramref name="x"/>, where a seek went or a drag holds it, until the app reports again.</summary>
        public void Hold(float x)
        {
            var bar = layout.Bar;
            fillShape.StopAnimation("Size");
            fillShape.Size = new Vector2(Math.Clamp(x - bar.X, 0, bar.Width), bar.Height);
        }

        public void Update(NowPlaying playing, string? failure, DateTimeOffset at)
        {
            art.Show(playing.Thumbnail, log);

            // A command the app does not accept is drawn disabled, and its target is too.
            previous.Color = playing.Accepts(MediaCommand.Previous) ? White : Dim;
            next.Color = playing.Accepts(MediaCommand.Next) ? White : Dim;
            circle.Color = playing.PlayPause == MediaCommand.None ? Dim : White;
            play.Color = playing.IsPlaying ? Clear : Black;
            pause.Color = playing.IsPlaying ? Black : Clear;

            // An app with no timeline gets no bar at all, rather than an empty one that never moves. A drag keeps the fill where it holds it.
            var bar = layout.Bar;
            track.Color = playing.Timeline is null ? Clear : Color.FromArgb(41, 255, 255, 255);
            fill.Color = playing.Timeline is null ? Clear : White;
            shown = playing;
            ShowKnob();
            if (!scrubbing)
            {
                fillShape.StopAnimation("Size");
            }

            if (playing.Timeline is { } timeline && !scrubbing)
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
            seekShapes.Dispose();
            title.Dispose();
            artist.Dispose();
            elapsed.Dispose();
            total.Dispose();
            seekTime.Dispose();
        }

        private void ShowKnob() => knob.Color = shown.CanSeek && (hovered || scrubbing) ? White : Clear;

        private void Label((float X, TimeSpan Time)? at)
        {
            if (at is not { } label)
            {
                card.Color = Clear;
                seekTime.Show("", MediaPanelLayout.SeekStyle, default);
                return;
            }

            var slot = layout.SeekLabel(label.X);
            cardShape.Offset = new Vector2(slot.X, slot.Y);
            cardShape.Size = new Vector2(slot.Width, slot.Height);
            card.Color = Card;
            seekTime.Show(NowPlaying.Clock(label.Time), MediaPanelLayout.SeekStyle, slot);
        }

        /// <summary>
        /// Each run one line in its slot, cut with an ellipsis. The title reads "Nothing playing" with no
        /// session; the artist line falls back to the app's name, and a command that just failed says so in
        /// its place, in the warn colour.
        /// </summary>
        private void DrawTextRuns(NowPlaying playing, string? failure)
        {
            title.Show(playing.HasSession ? playing.Title : NowPlaying.NothingPlaying, MediaPanelLayout.TitleStyle, layout.Title);
            artist.Show(failure ?? (playing.Artist.Length > 0 ? playing.Artist : playing.App), failure is null ? MediaPanelLayout.ArtistStyle : MediaPanelLayout.FailedStyle, layout.Artist);
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
            total.Show(timeline is { } d ? NowPlaying.Clock(d.Duration) : "", MediaPanelLayout.TotalStyle, layout.Total);
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
