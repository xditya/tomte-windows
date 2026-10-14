using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Windows.System;
using Windows.UI.Composition;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>The island's colours: cards, inks, the info blue and the warning amber.</summary>
internal static class ShelfColors
{
    public static readonly Color Clear = Color.FromArgb(0, 0, 0, 0);
    public static readonly Color Card = Color.FromArgb(15, 255, 255, 255);
    public static readonly Color CardSelected = Color.FromArgb(28, 255, 255, 255);
    public static readonly Color Dash = Color.FromArgb(89, 255, 255, 255);
    public static readonly Color Info = Color.FromArgb(255, 0x6F, 0xA8, 0xFF);
    public static readonly Color HotFill = Color.FromArgb(41, 0x6F, 0xA8, 0xFF);
    public static readonly Color Ink = Color.FromArgb(230, 255, 255, 255);
    public static readonly Color Ink2 = Color.FromArgb(153, 255, 255, 255);
    public static readonly Color Ink3 = Color.FromArgb(97, 255, 255, 255);
    public static readonly Color Warn = Color.FromArgb(255, 0xF0, 0xB3, 0x5A);
    public static readonly Color ButtonFill = Color.FromArgb(115, 0, 0, 0);

    public static Color Rgb(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
}

/// <summary>
/// The composition objects of one built part, taken off their parents and disposed together when the part goes.
/// Every shape is in DIPs: the part hangs under a container that carries the island's scale.
/// </summary>
internal sealed class Parts(Compositor compositor) : IDisposable
{
    private readonly List<IDisposable> made = [];
    private readonly List<(ContainerVisual Parent, Visual Child)> attached = [];

    public Compositor Compositor => compositor;

    public T Keep<T>(T made)
        where T : IDisposable
    {
        this.made.Add(made);
        return made;
    }

    public CompositionColorBrush Brush(Color color) => Keep(compositor.CreateColorBrush(color));

    public T Attach<T>(ContainerVisual parent, T child)
        where T : Visual
    {
        Keep(child);
        attached.Add((parent, child));
        parent.Children.InsertAtTop(child);
        return child;
    }

    public ShapeVisual Shapes(ContainerVisual parent, RectangleF at)
    {
        var shapes = Attach(parent, compositor.CreateShapeVisual());
        shapes.Offset = new Vector3(at.X, at.Y, 0);
        shapes.Size = new Vector2(at.Width, at.Height);
        return shapes;
    }

    public CompositionSpriteShape Box(ShapeVisual into, RectangleF r, float radius, CompositionBrush? fill, CompositionBrush? stroke = null, float thickness = 1)
    {
        var geometry = Keep(compositor.CreateRoundedRectangleGeometry());
        // The stroke is centred on the edge; keep all of it inside the rectangle.
        float inset = stroke is null ? 0 : thickness / 2;
        geometry.Offset = new Vector2(r.X + inset, r.Y + inset);
        geometry.Size = new Vector2(r.Width - 2 * inset, r.Height - 2 * inset);
        geometry.CornerRadius = new Vector2(radius);
        var shape = Keep(compositor.CreateSpriteShape(geometry));
        shape.FillBrush = fill;
        shape.StrokeBrush = stroke;
        shape.StrokeThickness = stroke is null ? 0 : thickness;
        into.Shapes.Add(shape);
        return shape;
    }

    public void Line(ShapeVisual into, Vector2 from, Vector2 to, CompositionBrush stroke, float thickness = 1.3f)
    {
        var geometry = Keep(compositor.CreateLineGeometry());
        geometry.Start = from;
        geometry.End = to;
        var shape = Keep(compositor.CreateSpriteShape(geometry));
        shape.StrokeBrush = stroke;
        shape.StrokeThickness = thickness;
        shape.StrokeStartCap = shape.StrokeEndCap = CompositionStrokeCap.Round;
        into.Shapes.Add(shape);
    }

    /// <summary>
    /// Where a label goes. On-island text has no renderer yet and which text surface to use is an open owner decision,
    /// so this draws a neutral bar over the slot instead. The string is passed so the renderer has one place to start.
    /// </summary>
    public void TextSlot(ShapeVisual into, string text, RectangleF slot, Color tone)
    {
        _ = text;
        Box(into, new RectangleF(slot.X, slot.Y + slot.Height * 0.2f, slot.Width, slot.Height * 0.6f), slot.Height * 0.3f, Brush(Color.FromArgb((byte)(tone.A / 3), tone.R, tone.G, tone.B)));
    }

    public void Dispose()
    {
        foreach (var (parent, child) in attached)
        {
            parent.Children.Remove(child);
        }

        attached.Clear();
        foreach (var part in made)
        {
            part.Dispose();
        }

        made.Clear();
    }
}

/// <summary>The content fades: in over 0.15 s once the shape is 60% along (ContentReady), out over 0.1 s before it goes.</summary>
internal static class Fades
{
    public static void Run(Visual visual, Parts parts, bool show)
    {
        var fade = parts.Keep(visual.Compositor.CreateScalarKeyFrameAnimation());
        fade.InsertKeyFrame(1, show ? 1 : 0);
        fade.Duration = TimeSpan.FromMilliseconds(show ? 150 : 100);
        visual.StartAnimation("Opacity", fade);
    }
}

/// <summary>
/// The Shelf-drop state's content: the dashed target to the right of the mascot, lit while a drag the island takes
/// is over it. Built when the state is entered and released 0.1 s after it is left.
/// </summary>
internal sealed class ShelfDropSurface
{
    private readonly IslandWindow island;
    private readonly DispatcherQueueTimer release;
    private Parts? parts, leaving;
    private ContainerVisual? space;
    private CompositionColorBrush? fill, stroke;
    private bool fadeInPending;

    public ShelfDropSurface(IslandWindow island, IslandStateMachine machine, DropTarget drops)
    {
        this.island = island;
        release = island.Queue.CreateTimer();
        release.IsRepeating = false;
        release.Interval = TimeSpan.FromMilliseconds(100);
        release.Tick += (_, _) => Release();
        machine.Changed += (from, to) =>
        {
            if (to == IslandState.ShelfDrop && from != IslandState.ShelfDrop)
            {
                Show();
            }
            else if (from == IslandState.ShelfDrop && to != IslandState.ShelfDrop)
            {
                Hide();
            }
        };
        // A drag the island refuses (its own drag out, a format it does not take) does not light the target.
        drops.Entered += () => Hot(drops.Accepting);
        drops.Left += () => Hot(false);
        island.ContentReady += () =>
        {
            if (fadeInPending && space is not null)
            {
                fadeInPending = false;
                Fades.Run(space, parts!, show: true);
            }
        };
    }

    private void Show()
    {
        Release();
        var p = parts = new Parts(island.Compositor);
        // Island space: DIPs from the island's centre line and top edge, scaled and kept centred by the island.
        space = p.Attach(island.Dips, island.Compositor.CreateContainerVisual());
        space.Opacity = 0;
        fadeInPending = true;
        var g = IslandGeometry.ShelfDrop;
        var r = ShelfLayout.DropTarget(g.Width, g.Depth);
        var shapes = p.Shapes(space, r);
        fill = p.Brush(ShelfColors.Clear);
        stroke = p.Brush(ShelfColors.Dash);
        var target = p.Box(shapes, new RectangleF(0, 0, r.Width, r.Height), 8, fill, stroke, 1.5f);
        target.StrokeDashArray.Add(3);
        target.StrokeDashArray.Add(2);

        // A folder and the label, centred together.
        const float glyph = 16, gap = 8, label = 84;
        float x = (r.Width - glyph - gap - label) / 2, y = r.Height / 2;
        var ink = p.Brush(ShelfColors.Ink2);
        p.Box(shapes, new RectangleF(x, y - 7, 7, 4), 1.5f, null, ink, 1.4f);
        p.Box(shapes, new RectangleF(x, y - 5, glyph, 12), 2, null, ink, 1.4f);
        p.TextSlot(shapes, ShelfLayout.DropHere, new RectangleF(x + glyph + gap, y - 6, label, 12), ShelfColors.Ink);
    }

    private void Hot(bool on)
    {
        if (fill is not null && stroke is not null)
        {
            // Colours change in place: a brush moved off its shape and back does not draw again.
            fill.Color = on ? ShelfColors.HotFill : ShelfColors.Clear;
            stroke.Color = on ? ShelfColors.Info : ShelfColors.Dash;
        }
    }

    private void Hide()
    {
        if (parts is null)
        {
            return;
        }

        Release();
        leaving = parts;
        Fades.Run(space!, leaving, show: false);
        (parts, space, fill, stroke) = (null, null, null, null);

        // A one-shot timer, not the fade's end: with the display asleep the compositor finishes no animation.
        release.Start();
    }

    private void Release()
    {
        release.Stop();
        leaving?.Dispose();
        leaving = null;
    }
}
