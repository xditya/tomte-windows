using System.Numerics;
using Tomte.Core;
using Tomte.Core.Interaction;
using Windows.UI;
using Windows.UI.Composition;

namespace Tomte.Shell;

/// <summary>
/// The tomte as composition visuals, nested like the art's groups: place > root > body > [face, cap > [cone, tip, brim]].
/// Paths are built once in viewBox units; DPI and size only change the outer scale, so the shapes stay vector.
/// </summary>
internal sealed class MascotVisual
{
    /// <summary>Rendered size of the 64-unit viewBox and its inset from the island's left edge, in DIPs.</summary>
    public const float SizeDips = MascotArt.SizeDips, InsetX = MascotArt.InsetX;

    private readonly ContainerVisual place;
    private readonly ContainerVisual root;
    private readonly ContainerVisual body;
    private readonly ContainerVisual cap;
    private readonly ShapeVisual tip, pin;
    private readonly SpriteVisual shadow;
    private readonly DropShadow drop;
    private readonly InsetClip edge;
    private readonly CompositionColorBrush capBrush;
    private readonly CompositionColorBrush shadeBrush;
    private uint capColor = uint.MaxValue;

    public MascotVisual(Compositor compositor)
    {
        capBrush = compositor.CreateColorBrush();
        shadeBrush = compositor.CreateColorBrush();
        var face = Shapes(
            compositor,
            Path(compositor, MascotArt.Beard, MascotArt.BeardColor),
            Path(compositor, MascotArt.BeardShade, MascotArt.BeardShadeColor),
            Circle(compositor, MascotArt.Nose, compositor.CreateColorBrush(Rgb(MascotArt.NoseColor))));
        var cone = Shapes(compositor, Path(compositor, MascotArt.CapCone, capBrush));
        tip = Shapes(compositor, Path(compositor, MascotArt.TipCurl, capBrush), Circle(compositor, MascotArt.TipBulb, capBrush));
        tip.CenterPoint = new Vector3(MascotArt.TipPivot, 0);
        var brim = Shapes(compositor, Path(compositor, MascotArt.Brim, shadeBrush));

        cap = Group(compositor, MascotArt.CapPivot, cone, tip, brim);
        body = Group(compositor, MascotArt.BodyPivot, face, cap);
        root = Group(compositor, MascotArt.RootPivot, body);

        // Its own soft shadow, for the perched tomte that rests with no pill behind it, so it reads on any wallpaper. Under
        // the body in the root, so it follows the sway and the hop; in viewBox units, like everything here.
        var copy = compositor.CreateVisualSurface();
        copy.SourceVisual = body;
        copy.SourceSize = new Vector2(MascotArt.ViewBox);
        drop = compositor.CreateDropShadow();
        drop.Mask = compositor.CreateSurfaceBrush(copy);
        drop.Color = Color.FromArgb(255, 0, 0, 0);
        drop.Offset = new Vector3(0, ShadowDropDips * MascotArt.ViewBox / SizeDips, 0);
        drop.BlurRadius = ShadowBlurDips * MascotArt.ViewBox / SizeDips;
        shadow = compositor.CreateSpriteVisual();
        shadow.Size = new Vector2(MascotArt.ViewBox);
        shadow.Shadow = drop;
        shadow.IsVisible = false;
        root.Children.InsertAtBottom(shadow);

        // The pinned mark sits on the place, not the body, so it stays still while the tomte moves.
        var ink = compositor.CreateColorBrush(Rgb(0x000000));
        pin = Shapes(compositor, [Circle(compositor, MascotArt.PinRing, ink), Circle(compositor, MascotArt.PinDisc, compositor.CreateColorBrush(Rgb(StateLook.Accent))),
            .. MascotArt.Pin.Select(part => Path(compositor, part, ink))]);
        pin.Size = new Vector2(MascotArt.PinRing.Center.X + MascotArt.PinRing.Radius);
        pin.IsVisible = false;
        place = Group(compositor, Vector2.Zero, root, pin);
        place.IsVisible = false;

        // Nothing of the tomte shows above the island's top edge: the notch has the screen edge there, and the floating
        // pill has the gap.
        place.Size = new Vector2(MascotArt.ViewBox);
        float far = 4 * MascotArt.ViewBox;
        edge = compositor.CreateInsetClip(-far, 0, -far, -far);
        place.Clip = edge;
    }

    // The perched tomte's shadow: a DIP down, blurred over 4, at most this dark.
    private const float ShadowDropDips = 1, ShadowBlurDips = 4, ShadowOpacity = 0.6f;

    public Visual Visual => place;

    /// <summary>The island is pinned (Ctrl+P): the mark shows at the tomte's lower right.</summary>
    public bool Pinned
    {
        set => pin.IsVisible = value;
    }

    /// <summary>
    /// Copy the pose onto the visuals, the box's top-left at <paramref name="offsetPx"/> from the island's, drawn at
    /// <paramref name="ride"/> of its size and opacity (<see cref="MascotArt.Ride"/>) and <paramref name="grow"/> times larger again, with
    /// <paramref name="shade"/> (0 to 1) of its own shadow. Property sets only; nothing is
    /// allocated per frame.
    /// </summary>
    public void Apply(Mascot mascot, Vector2 offsetPx, float scale, float ride = 1, float grow = 1, float shade = 0)
    {
        shadow.IsVisible = shade > 0;
        drop.Opacity = ShadowOpacity * shade;
        bool shown = mascot.Visible && ride > 0;
        place.IsVisible = shown;
        if (!shown)
        {
            return;
        }

        float k = SizeDips / MascotArt.ViewBox * scale * ride * grow;
        place.Offset = new Vector3(offsetPx, 0);
        place.Scale = new Vector3(k, k, 1);
        // The box sits lower in a deeper row; the clip stays on the island's top edge, in viewBox units.
        edge.TopInset = -offsetPx.Y / k;
        // On the tomte, not the place: the launch arrival hides the place while its own tomte flies in.
        root.Opacity = ride;
        root.RotationAngleInDegrees = (float)mascot.RootRotate;
        root.Offset = new Vector3((float)mascot.RootX, (float)mascot.RootY, 0);
        body.Scale = new Vector3((float)mascot.ScaleX, (float)mascot.ScaleY, 1);
        cap.RotationAngleInDegrees = (float)mascot.CapRotate;
        tip.RotationAngleInDegrees = (float)mascot.TipRotate;
        if (mascot.CapColor != capColor)
        {
            capColor = mascot.CapColor;
            capBrush.Color = Rgb(capColor);
            shadeBrush.Color = Rgb(MascotArt.Shade(capColor));
        }
    }

    private static Color Rgb(uint rgb) => Color.FromArgb(255, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static CompositionSpriteShape Path(Compositor compositor, OutlineSegment[] segments, uint rgb) =>
        Path(compositor, segments, compositor.CreateColorBrush(Rgb(rgb)));

    private static CompositionSpriteShape Path(Compositor compositor, OutlineSegment[] segments, CompositionBrush brush)
    {
        var shape = compositor.CreateSpriteShape(compositor.CreatePathGeometry(IslandWindow.BuildPath(segments, 1)));
        shape.FillBrush = brush;
        return shape;
    }

    private static CompositionSpriteShape Circle(Compositor compositor, (Vector2 Center, float Radius) circle, CompositionBrush brush)
    {
        var geometry = compositor.CreateEllipseGeometry();
        geometry.Center = circle.Center;
        geometry.Radius = new Vector2(circle.Radius);
        var shape = compositor.CreateSpriteShape(geometry);
        shape.FillBrush = brush;
        return shape;
    }

    private static ShapeVisual Shapes(Compositor compositor, params CompositionShape[] shapes)
    {
        var visual = compositor.CreateShapeVisual();
        visual.Size = new Vector2(MascotArt.ViewBox);
        foreach (var shape in shapes)
        {
            visual.Shapes.Add(shape);
        }

        return visual;
    }

    private static ContainerVisual Group(Compositor compositor, Vector2 pivot, params Visual[] children)
    {
        var group = compositor.CreateContainerVisual();
        group.CenterPoint = new Vector3(pivot, 0);
        foreach (var child in children)
        {
            group.Children.InsertAtTop(child);
        }

        return group;
    }
}
