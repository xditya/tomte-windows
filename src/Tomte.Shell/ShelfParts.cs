using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Text;
using Windows.System;
using Windows.UI.Composition;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>The <see cref="Theme"/>'s colours as composition colours, for the shelf and the drop zones.</summary>
internal static class ShelfColors
{
    public static readonly Color Clear = default;
    public static Color Card => Of(Theme.Card);
    public static Color CardSelected => Of(Theme.Raised);
    public static Color Raised => Of(Theme.Raised);
    public static Color RaisedHi => Of(Theme.RaisedHi);
    public static Color Dash => Of(Theme.Dash);
    public static Color Line => Of(Theme.Line);
    public static Color Ink => Of(Theme.Ink);
    public static Color Ink2 => Of(Theme.Ink2);
    public static Color Ink3 => Of(Theme.Ink3);
    public static Color Warn => Of(Theme.Warn);
    public static Color Disabled => Of(Theme.WithOpacity(Theme.Ink, Tomte.Core.Interaction.StateLook.DisabledOpacity));
    public static Color Mark => Of(Theme.LineStrong);
    public static Color WarnFill => Of(Theme.WarnSoft);

    public static Color Accent => Of(Theme.Accent);


    // Brushes that live as long as the island, each with the theme colour it shows.
    private static readonly List<(CompositionColorBrush Brush, Func<uint> Argb)> Kept = [];

    /// <summary>
    /// A brush for a view that lives as long as the island: it takes <paramref name="argb"/>'s new value whenever the
    /// theme changes. Views built on each show use plain brushes instead; this one is never released.
    /// </summary>
    public static CompositionColorBrush Live(Compositor compositor, Func<uint> argb)
    {
        if (Kept.Count == 0)
        {
            Theme.Changed += () => Kept.ForEach(k => k.Brush.Color = Of(k.Argb()));
        }

        var brush = compositor.CreateColorBrush(Of(argb()));
        Kept.Add((brush, argb));
        return brush;
    }

    /// <summary>An 0xAARRGGBB colour as a composition colour.</summary>
    public static Color Of(uint argb) => Color.FromArgb((byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb);

    /// <summary>A colour's red, green and blue (any alpha ignored), opaque.</summary>
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
    public TextVisual Text(ContainerVisual host, TextSlot line)
    {
        var visual = Keep(text.Create(host));
        Texts.Add(visual);
        visual.Show(line);
        return visual;
    }

    /// <summary>A run of one or more lines from <paramref name="at"/> in <paramref name="host"/>, which goes with the part.</summary>
    public void Text(ContainerVisual host, TextRun run, Vector2 at)
    {
        var visual = Keep(text.Create(host));
        Texts.Add(visual);
        visual.Show(run, at);
    }

    /// <summary>The part's text visuals, for a list that scrolls them to land them on whole pixels again.</summary>
    public List<TextVisual> Texts { get; } = [];

    /// <summary>One of the <see cref="Glyphs"/> in <paramref name="ink"/>, drawn to fill the square <paramref name="at"/>.</summary>
    public void Glyph(ShapeVisual into, GlyphFigure[] figures, Color ink, RectangleF at)
    {
        var holder = Keep(compositor.CreateContainerShape());
        holder.Offset = new Vector2(at.X, at.Y);
        into.Shapes.Add(holder);
        PaletteView.DrawGlyph(holder, figures, Brush(ink), at.Width);
    }

    /// <summary>A sprite in <paramref name="parent"/> at <paramref name="at"/>, cut to a rounded rectangle, filled with <paramref name="brush"/>.</summary>
    public SpriteVisual Sprite(ContainerVisual parent, RectangleF at, float radius, CompositionBrush brush)
    {
        var sprite = Attach(parent, compositor.CreateSpriteVisual());
        sprite.Offset = new Vector3(at.X, at.Y, 0);
        sprite.Size = new Vector2(at.Width, at.Height);
        var round = Keep(compositor.CreateRoundedRectangleGeometry());
        round.Size = sprite.Size;
        round.CornerRadius = new Vector2(radius);
        sprite.Clip = Keep(compositor.CreateGeometricClip(round));
        sprite.Brush = brush;
        return sprite;
    }

    /// <summary>A keycap: the raised cap with its border and the keys in it, in <paramref name="into"/>'s own DIPs.</summary>
    public void Keycap(ShapeVisual into, RectangleF cap, string keys)
    {
        Box(into, cap, Tomte.Core.Keycap.Corner, Brush(ShelfColors.Raised), Brush(ShelfColors.Mark), 1);
        Text(into, new TextSlot(keys, Tomte.Core.Keycap.Style, cap));
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
        Texts.Clear();
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
