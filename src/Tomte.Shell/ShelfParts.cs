using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Text;
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
    public static readonly Color Line = Color.FromArgb(26, 255, 255, 255);
    public static readonly Color Info = Color.FromArgb(255, 0x6F, 0xA8, 0xFF);
    public static readonly Color HotFill = Color.FromArgb(41, 0x6F, 0xA8, 0xFF);
    public static readonly Color Ink = Color.FromArgb(230, 255, 255, 255);
    public static readonly Color Ink2 = Color.FromArgb(153, 255, 255, 255);
    public static readonly Color Ink3 = Color.FromArgb(97, 255, 255, 255);
    public static readonly Color Warn = Color.FromArgb(255, 0xF0, 0xB3, 0x5A);
    public static readonly Color Disabled = Color.FromArgb((byte)(230 * Tomte.Core.Interaction.StateLook.DisabledOpacity), 255, 255, 255);
    public static readonly Color Mark = Color.FromArgb(77, 255, 255, 255);
    public static readonly Color WarnFill = Color.FromArgb(41, 0xF0, 0xB3, 0x5A);

    public static Color Rgb(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    public static uint Argb(Color c) => (uint)(c.A << 24 | c.R << 16 | c.G << 8 | c.B);
}

/// <summary>
/// The composition objects of one built part, its text visuals among them, taken off their parents and disposed
/// together when the part goes. Every shape is in DIPs: the part hangs under a container that carries the island's
/// scale.
/// </summary>
internal sealed class Parts(Compositor compositor, TextRenderer text) : IDisposable
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

    /// <summary>A line of text in a slot of <paramref name="host"/>, which goes with the part.</summary>
    public void Text(ContainerVisual host, TextSlot line) => Keep(text.Create(host)).Show(line);

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
/// is over it. With a provider set up, the AI zones sit in a row beside a narrower target; the one under the cursor
/// is lit instead, and a line under the row says what a drop there does. Built when the state is entered and
/// released 0.1 s after it is left.
/// </summary>
internal sealed class ShelfDropSurface
{
    private readonly IslandWindow island;
    private readonly TextRenderer text;
    private readonly Func<DropZoneLayout?> zones;
    private readonly DispatcherQueueTimer release;
    private readonly Dictionary<DropZone, (CompositionColorBrush Fill, CompositionColorBrush Stroke, Windows.UI.Color Normal)> lit = [];
    private Parts? parts, leaving;
    private ContainerVisual? space;
    private TextVisual? line;
    private bool fadeInPending, accepting;

    public ShelfDropSurface(IslandWindow island, IslandStateMachine machine, DropTarget drops, TextRenderer text, Func<DropZoneLayout?>? zones = null)
    {
        this.island = island;
        this.text = text;
        this.zones = zones ?? (() => null);
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
        drops.Entered += () =>
        {
            accepting = drops.Accepting;
            Light(Layout is null && accepting ? DropZone.Shelf : null);
        };
        drops.Left += () => Light(null);
        drops.Over += (x, y) =>
        {
            if (Layout is { } layout && accepting && space is not null)
            {
                Light(layout.At(island.ScreenToIsland(x, y)));
            }
        };
        island.ContentReady += () =>
        {
            if (fadeInPending && space is not null)
            {
                fadeInPending = false;
                Fades.Run(space, parts!, show: true);
            }
        };
    }

    /// <summary>The zones drawn now; null while the surface is the shelf's alone.</summary>
    public DropZoneLayout? Layout { get; private set; }

    /// <summary>Where a drop now would go: the zone lit under the cursor, null while nothing the island takes is over it.</summary>
    public DropZone? Hot { get; private set; }

    private void Show()
    {
        Release();
        var p = parts = new Parts(island.Compositor, text);
        // Island space: DIPs from the island's centre line and top edge, scaled and kept centred by the island.
        space = p.Attach(island.Dips, island.Compositor.CreateContainerVisual());
        space.Opacity = 0;
        fadeInPending = true;
        Hot = null;
        var g = IslandGeometry.ShelfDrop;
        var layout = Layout = zones();
        var r = layout?.Target ?? ShelfLayout.DropTarget(g.Width, g.Depth);
        var shapes = p.Shapes(space, r);
        var target = Card(p, shapes, DropZone.Shelf, new RectangleF(0, 0, r.Width, r.Height), ShelfColors.Dash, 1.5f);
        target.StrokeDashArray.Add(3);
        target.StrokeDashArray.Add(2);
        var ink = p.Brush(ShelfColors.Ink2);
        if (layout is null)
        {
            // A folder and the label, centred together.
            var (x, label) = ShelfLayout.DropLabel(new SizeF(r.Width, r.Height));
            float y = r.Height / 2;
            p.Box(shapes, new RectangleF(x, y - 7, 7, 4), 1.5f, null, ink, 1.4f);
            p.Box(shapes, new RectangleF(x, y - 5, 16, 12), 2, null, ink, 1.4f);
            p.Text(shapes, label);
            return;
        }

        // The folder over the word, both centred, then each zone with its words, then the line under the row.
        var (word, glyph) = layout.TargetText();
        if (glyph is { } folder)
        {
            var at = folder - new Vector2(r.X, r.Y);
            p.Box(shapes, new RectangleF(at.X, at.Y, 7, 4), 1.5f, null, ink, 1.4f);
            p.Box(shapes, new RectangleF(at.X, at.Y + 2, 16, 12), 2, null, ink, 1.4f);
        }

        p.Keep(text.Create(shapes)).Show(word.Run, word.At - new Vector2(r.X, r.Y));
        foreach (var (zone, rect) in layout.Zones)
        {
            var card = p.Shapes(space, rect);
            var box = Card(p, card, zone, new RectangleF(0, 0, rect.Width, rect.Height), zone == DropZone.Skill ? ShelfColors.Dash : ShelfColors.Line, 1);
            if (zone == DropZone.Skill)
            {
                box.StrokeDashArray.Add(3);
                box.StrokeDashArray.Add(2);
            }

            var words = layout.ZoneText(zone, rect);
            p.Keep(text.Create(card)).Show(words.Run, words.At - new Vector2(rect.X, rect.Y));
        }

        line = p.Keep(text.Create(space));
    }

    /// <summary>A target or zone card whose colours <see cref="Light"/> changes in place.</summary>
    private CompositionSpriteShape Card(Parts p, ShapeVisual into, DropZone zone, RectangleF r, Windows.UI.Color rest, float thickness)
    {
        var fill = p.Brush(ShelfColors.Clear);
        var stroke = p.Brush(rest);
        lit[zone] = (fill, stroke, rest);
        return p.Box(into, r, 8, fill, stroke, thickness);
    }

    private void Light(DropZone? hot)
    {
        if (hot == Hot || space is null)
        {
            return;
        }

        Hot = hot;
        // Colours change in place: a brush moved off its shape and back does not draw again.
        foreach (var (zone, (fill, stroke, normal)) in lit)
        {
            fill.Color = zone == hot ? ShelfColors.HotFill : ShelfColors.Clear;
            stroke.Color = zone == hot ? ShelfColors.Info : normal;
        }

        if (Layout is { } layout && line is not null)
        {
            line.Show(hot is { } zone ? layout.LineText(zone) : new TextSlot("", DropZoneLayout.LineStyle, layout.Line));
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
        (parts, space, line, Layout, Hot) = (null, null, null, null, null);
        lit.Clear();

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
