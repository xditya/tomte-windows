using System.Numerics;
using Tomte.Core;
using Windows.UI;
using Windows.UI.Composition;

namespace Tomte.Shell;

/// <summary>
/// The Compact pill's timer ring: an 18 DIP track with a green arc that fills clockwise from 12 o'clock. The
/// compositor animates the arc from the progress at show time to full at the end time, so nothing in this
/// process ticks for it. The animation runs only while the ring is shown; hidden, it costs nothing.
/// </summary>
internal sealed class TimerRing
{
    private const float Size = 18, Stroke = 3.5f;

    // The gap between the mascot and the ring, and the ring's top so it sits centred in the 38 DIP pill.
    private const float Gap = 9;
    private static readonly float Top = (float)(IslandGeometry.Compact.Depth - Size) / 2;

    // The fill moves in this many steps over the time left, not on every frame. A linear fill made the compositor
    // redraw at the refresh rate for as long as the ring showed: on dwm.exe at 165 Hz, roughly a tenth of a core
    // and 4% of the GPU more than this stepped fill. A step is a degree of arc or less, under a pixel at this size.
    private const int FillSteps = 360;

    private readonly ShapeVisual visual;
    private readonly CompositionEllipseGeometry arc;
    private readonly ScalarKeyFrameAnimation fill;

    public TimerRing(Compositor compositor)
    {
        var track = Circle(compositor, Color.FromArgb(36, 255, 255, 255), out _);
        // An ellipse's path starts at 12 o'clock and runs clockwise (checked on screen), so the arc fills like a
        // conic gradient.
        var arcShape = Circle(compositor, Color.FromArgb(255, 0x4C, 0xC3, 0x8A), out arc);
        visual = compositor.CreateShapeVisual();
        visual.Size = new Vector2(Size);
        visual.Shapes.Add(track);
        visual.Shapes.Add(arcShape);
        visual.IsVisible = false;
        // The parent is as wide as the current outline, so half its width follows the pill's centre through every
        // frame of a morph and the ring stays where the open pill puts it, like the mascot's anchor.
        visual.RelativeOffsetAdjustment = new Vector3(0.5f, 0, 0);

        // One fill for the ring's life; each show only gives it the time left.
        fill = compositor.CreateScalarKeyFrameAnimation();
        fill.InsertKeyFrame(1, 1, compositor.CreateStepEasingFunction(FillSteps));
    }

    /// <summary>Goes into the island's content visual, which is the outline's bounds plus a 1 px margin each side.</summary>
    public Visual Visual => visual;

    /// <summary>
    /// Show the ring for a run: full when it has ended, otherwise filling until its end time. It sits right of
    /// the mascot, or at the mascot's own inset when the mascot is off, so it never stands beside a gap.
    /// </summary>
    public void Show(TimerRun run, DateTimeOffset now, double scale, bool mascotOn)
    {
        float s = (float)scale;
        // From the pill's centre; the parent's half width is one margin pixel right of it.
        float fromCenter = (float)(-IslandGeometry.Compact.Width / 2) + Left(mascotOn);
        visual.Scale = new Vector3(s, s, 1);
        visual.Offset = new Vector3(fromCenter * s - 1, Top * s, 0);

        // Stop first, so a fill left from the last show cannot override the value set here.
        arc.StopAnimation("TrimEnd");
        arc.TrimEnd = (float)run.Progress(now);
        var remaining = run.Remaining(now);
        if (remaining > TimeSpan.Zero)
        {
            // A composition animation takes 1 ms to 24 days and throws outside that; a wrong arc is better than a
            // throw in the middle of a state change.
            fill.Duration = TimeSpan.FromTicks(Math.Clamp(remaining.Ticks, TimeSpan.TicksPerMillisecond, TimeSpan.FromDays(24).Ticks));
            arc.StartAnimation("TrimEnd", fill);
        }

        visual.IsVisible = true;
    }

    /// <summary>Where the ring starts, from the pill's left edge, in DIPs; what follows it starts at <see cref="After"/>.</summary>
    public static float Left(bool mascotOn) => mascotOn ? MascotVisual.InsetX + MascotVisual.SizeDips + Gap : MascotVisual.InsetX;

    public static float After(bool mascotOn) => Left(mascotOn) + Size + Gap;

    public void Hide()
    {
        if (!visual.IsVisible)
        {
            return;
        }

        visual.IsVisible = false;
        arc.StopAnimation("TrimEnd");
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
