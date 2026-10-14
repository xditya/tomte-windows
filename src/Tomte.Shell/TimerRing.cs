using System.Numerics;
using Tomte.Core;
using Windows.UI;
using Windows.UI.Composition;

namespace Tomte.Shell;

/// <summary>
/// The Compact pill's timer ring: an 18 DIP track with a green arc that fills clockwise from 12 o'clock. The
/// compositor animates the arc from the progress at show time to full at the end time, so nothing in this
/// process ticks for it. Both animations run only while the ring is shown; hidden, it costs nothing.
/// </summary>
internal sealed class TimerRing
{
    private const float Size = 18, Stroke = 3.5f;

    // The gap between the mascot and the ring, and the ring's top so it sits centred in the 38 DIP pill.
    private const float Gap = 9;
    private static readonly float Top = (float)(IslandGeometry.Compact.Depth - Size) / 2;

    private readonly ShapeVisual visual;
    private readonly CompositionEllipseGeometry arc;
    private readonly ExpressionAnimation place;
    private readonly ScalarKeyFrameAnimation fill;

    public TimerRing(Compositor compositor, Visual content)
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

        // One linear fill for the ring's life; each show only gives it the time left.
        fill = compositor.CreateScalarKeyFrameAnimation();
        fill.InsertKeyFrame(1, 1, compositor.CreateLinearEasingFunction());

        // Content is the outline's bounds plus a 1 px margin each side, and its size changes every frame of a
        // morph; anchoring to its centre keeps the ring where the open pill puts it, like the mascot's anchor.
        // x and y are DIPs, from the pill's centre and from its top.
        place = compositor.CreateExpressionAnimation("Vector3((c.Size.X - 2) / 2 + this.Target.Scale.X * x, this.Target.Scale.X * y, 0)");
        place.SetReferenceParameter("c", content);
        place.SetScalarParameter("y", Top);
    }

    public Visual Visual => visual;

    /// <summary>
    /// Show the ring for a run: full when it has ended, otherwise filling until its end time. It sits right of
    /// the mascot, or at the mascot's own inset when the mascot is off, so it never stands beside a gap.
    /// </summary>
    public void Show(TimerRun run, DateTimeOffset now, double scale, bool mascotOn)
    {
        float left = mascotOn ? MascotVisual.InsetX + MascotVisual.SizeDips + Gap : MascotVisual.InsetX;
        place.SetScalarParameter("x", (float)(-IslandGeometry.Compact.Width / 2) + left);
        visual.Scale = new Vector3((float)scale, (float)scale, 1);
        visual.StartAnimation("Offset", place);

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

    public void Hide()
    {
        if (!visual.IsVisible)
        {
            return;
        }

        visual.IsVisible = false;
        arc.StopAnimation("TrimEnd");
        visual.StopAnimation("Offset");
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
