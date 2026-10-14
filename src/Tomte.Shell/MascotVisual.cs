using System.Numerics;
using Tomte.Core;
using Windows.UI;
using Windows.UI.Composition;

namespace Tomte.Shell;

/// <summary>
/// The tomte as composition visuals, nested like the art's groups: place > root > body > [face, cap > [cone, tip, brim]].
/// Paths are built once in viewBox units; DPI and size only change the outer scale, so the shapes stay vector.
/// </summary>
internal sealed class MascotVisual
{
    /// <summary>Rendered size of the 64-unit viewBox and its inset from the island's top-left, in DIPs.</summary>
    public const float SizeDips = 26;
    public const float InsetX = 8;
    // Low enough that the reveal start pose (PeekFromY) is fully above the island's top edge on the first frame.
    public const float InsetY = 3.5f;

    private readonly ContainerVisual place;
    private readonly ContainerVisual root;
    private readonly ContainerVisual body;
    private readonly ContainerVisual cap;
    private readonly ShapeVisual tip;
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
        place = Group(compositor, Vector2.Zero, root);
        place.IsVisible = false;

        // Nothing of the tomte shows above the island's top edge. The notch has the screen edge there; the floating
        // pill has the gap, and the drop-in comes out of the line the pill grows from, not down across the gap.
        place.Size = new Vector2(MascotArt.ViewBox);
        float far = 4 * MascotArt.ViewBox;
        place.Clip = compositor.CreateInsetClip(-far, -InsetY * MascotArt.ViewBox / SizeDips, -far, -far);
    }

    public Visual Visual => place;

    /// <summary>Copy the pose onto the visuals. Property sets only; nothing is allocated per frame.</summary>
    public void Apply(Mascot mascot, Vector2 offsetPx, float scale)
    {
        place.IsVisible = mascot.Visible;
        if (!mascot.Visible)
        {
            return;
        }

        float k = SizeDips / MascotArt.ViewBox * scale;
        place.Offset = new Vector3(offsetPx, 0);
        place.Scale = new Vector3(k, k, 1);
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
