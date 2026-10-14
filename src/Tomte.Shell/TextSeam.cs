using System.Numerics;
using Windows.UI;
using Windows.UI.Composition;

namespace Tomte.Shell;

/// <summary>How a piece of island text would be set: the size and weight decide its rough width and its bar's tone.</summary>
internal enum TextLook
{
    Title,
    Primary,
    Secondary,
    Small,
}

/// <summary>
/// The one place island text goes through. There is no text renderer on the island yet: how text is
/// drawn is an open decision. Until then every slot shows a neutral rounded bar about as wide as the
/// text would be, so layouts can be judged. A renderer replaces <see cref="Slot"/> and nothing else;
/// callers pass the text they would show, already shortened, and never the whole of a stored item.
/// </summary>
internal static class TextSeam
{
    /// <summary>
    /// A slot for <paramref name="text"/> on one line whose top-left is <paramref name="at"/>, no wider than
    /// <paramref name="maxWidth"/>, in DIPs. Right-aligned slots end at at.X instead. Returns the width taken.
    /// </summary>
    public static float Slot(Compositor compositor, ShapeVisual into, string text, Vector2 at, float lineHeight, float maxWidth, TextLook look, bool alignRight = false)
    {
        float width = Math.Min(maxWidth, text.Length * CharWidth(look));
        if (width <= 0)
        {
            return 0;
        }

        float height = MathF.Round(lineHeight * 0.5f);
        var bar = compositor.CreateRoundedRectangleGeometry();
        bar.Size = new Vector2(width, height);
        bar.CornerRadius = new Vector2(height / 2);
        var shape = compositor.CreateSpriteShape(bar);
        shape.FillBrush = compositor.CreateColorBrush(Tone(look));
        shape.Offset = new Vector2(alignRight ? at.X - width : at.X, at.Y + (lineHeight - height) / 2);
        into.Shapes.Add(shape);
        return width;
    }

    /// <summary>Average advance of Segoe UI Variable at the look's size, in DIPs. Rough on purpose.</summary>
    public static float CharWidth(TextLook look) => look switch
    {
        TextLook.Title => 7f,
        TextLook.Primary => 6.4f,
        TextLook.Secondary => 6.2f,
        _ => 5.6f,
    };

    private static Color Tone(TextLook look) => look switch
    {
        TextLook.Title => Color.FromArgb(84, 255, 255, 255),
        TextLook.Primary => Color.FromArgb(66, 255, 255, 255),
        TextLook.Secondary => Color.FromArgb(40, 255, 255, 255),
        _ => Color.FromArgb(28, 255, 255, 255),
    };
}
