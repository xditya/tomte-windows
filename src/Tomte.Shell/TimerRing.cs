using System.Numerics;
using Tomte.Core;
using Windows.UI;
using Windows.UI.Composition;

namespace Tomte.Shell;

/// <summary>
/// The Compact pill's timer ring: a 22 DIP track with an accent arc, round-capped, that drains clockwise from 12 o'clock. The
/// compositor animates the arc from what is left at show time to nothing at the end time, so nothing in this
/// process ticks for it. The animation runs only while the ring is shown; hidden, it costs nothing.
/// </summary>
internal sealed class TimerRing
{
    private const float Size = CompactLayout.RingSize, Stroke = CompactLayout.RingStroke;

    // The ring's top, so it sits centred in the pill.
    private static readonly float Top = (float)(IslandGeometry.Compact.Depth - Size) / 2;

    // The fill moves in this many steps over the time left, not on every frame. A linear fill made the compositor
    // redraw at the refresh rate for as long as the ring showed: on dwm.exe at 165 Hz, roughly a tenth of a core
    // and 4% of the GPU more than this stepped fill. A step is a degree of arc or less, under a pixel at this size.
    private const int FillSteps = 360;

    private readonly ShapeVisual visual;
    private readonly CompositionEllipseGeometry arc;
    private readonly ScalarKeyFrameAnimation fill;
    private ContainerVisual? frame;

    public TimerRing(Compositor compositor)
    {
        var track = Circle(compositor, ShelfColors.Of(Theme.LineStrong), out _);
        // An ellipse's path starts at 12 o'clock and runs clockwise (checked on screen), so the arc is what is left,
        // from 12 round, and it drains back towards 12 as the time runs out.
        var arcShape = Circle(compositor, ShelfColors.Of(Theme.Accent), out arc);
        arcShape.StrokeStartCap = arcShape.StrokeEndCap = CompositionStrokeCap.Round;
        visual = compositor.CreateShapeVisual();
        visual.Size = new Vector2(Size);
        visual.Shapes.Add(track);
        visual.Shapes.Add(arcShape);
        visual.IsVisible = false;

        // One fill for the ring's life; each show only gives it the time left.
        fill = compositor.CreateScalarKeyFrameAnimation();
        fill.InsertKeyFrame(1, 0, compositor.CreateStepEasingFunction(FillSteps));
    }

    /// <summary>
    /// Show the ring for a run, in DIPs in <paramref name="into"/> (the island's trailing frame, or its centre one) with
    /// its left edge at <paramref name="x"/>: full when the run has ended, otherwise draining until its end time. It
    /// goes a gap before the words it belongs to.
    /// </summary>
    public void Show(TimerRun run, DateTimeOffset now, ContainerVisual into, float x)
    {
        if (frame != into)
        {
            frame?.Children.Remove(visual);
            into.Children.InsertAtTop(visual);
            frame = into;
        }

        Place(x);

        // Stop first, so a fill left from the last show cannot override the value set here.
        arc.StopAnimation("TrimEnd");
        var remaining = run.Remaining(now);
        // An ended run shows the whole ring, the mark of a timer that is done.
        arc.TrimEnd = remaining > TimeSpan.Zero ? (float)(1 - run.Progress(now)) : 1;
        if (remaining > TimeSpan.Zero)
        {
            // A composition animation takes 1 ms to 24 days and throws outside that; a wrong arc is better than a
            // throw in the middle of a state change.
            fill.Duration = TimeSpan.FromTicks(Math.Clamp(remaining.Ticks, TimeSpan.TicksPerMillisecond, TimeSpan.FromDays(24).Ticks));
            arc.StartAnimation("TrimEnd", fill);
        }

        visual.IsVisible = true;
    }

    /// <summary>Move the ring along its row, as the words after it change width (1:00:00 to 59:59).</summary>
    public void Place(float x) => visual.Offset = new Vector3(x, Top, 0);

    public void Hide()
    {
        if (!visual.IsVisible)
        {
            return;
        }

        visual.IsVisible = false;
        arc.StopAnimation("TrimEnd");
        // Out of its frame, so a close with nothing showing does not wait for a fade; Show puts it back.
        frame?.Children.Remove(visual);
        frame = null;
    }

    private static CompositionSpriteShape Circle(Compositor compositor, Color color, out CompositionEllipseGeometry geometry)
    {
        geometry = compositor.CreateEllipseGeometry();
        geometry.Center = new Vector2(Size / 2);
        geometry.Radius = new Vector2((Size - Stroke) / 2);
        var shape = compositor.CreateSpriteShape(geometry);
        shape.StrokeBrush = compositor.CreateColorBrush(color);
        shape.StrokeThickness = Stroke;
        return shape;
    }
}
