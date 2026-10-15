using System.Drawing;
using System.Numerics;
using Tomte.Core.Interaction;
using Windows.UI.Composition;

namespace Tomte.Shell;

/// <summary>
/// The tooltip's card in island space: a rounded box in the tip's grey with a one-pixel hairline, and the
/// acknowledgement's tick in the accent before its line of text, which the caller lays over <see cref="Visual"/>.
/// One card serves every tip and every acknowledgement of the island.
/// </summary>
internal sealed class TipCard
{
    private readonly CompositionRoundedRectangleGeometry box;
    private readonly CompositionSpriteShape shape, tick;
    private readonly CompositionColorBrush accent, clear;

    /// <param name="compositor">The island's compositor.</param>
    /// <param name="accent">The tick's fill.</param>
    /// <param name="mark">The tick's figure, in DIPs from its box's top-left corner.</param>
    public TipCard(Compositor compositor, CompositionColorBrush accent, CompositionGeometry mark)
    {
        this.accent = accent;
        clear = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(0, 0, 0, 0));
        Visual = compositor.CreateShapeVisual();
        Visual.IsVisible = false;
        box = compositor.CreateRoundedRectangleGeometry();
        box.CornerRadius = new Vector2(StateLook.TipCorner);
        shape = compositor.CreateSpriteShape(box);
        shape.FillBrush = compositor.CreateColorBrush(Argb(StateLook.TipFill));
        shape.StrokeBrush = compositor.CreateColorBrush(Argb(StateLook.TipLine));
        Visual.Shapes.Add(shape);
        tick = compositor.CreateSpriteShape(mark);
        tick.FillBrush = clear;
        Visual.Shapes.Add(tick);
    }

    public ShapeVisual Visual { get; }

    /// <summary>
    /// The card at <paramref name="card"/>, with the tick in <paramref name="mark"/>, or none when it is empty. A shape the
    /// compositor has drawn with no fill is not drawn again once it gets one, so a tip shown first would leave every later
    /// acknowledgement without its tick: the tick swaps to a clear fill, never to none.
    /// </summary>
    public void Show(RectangleF card, RectangleF mark, double scale)
    {
        Visual.Offset = new Vector3(card.X, card.Y, 0);
        Visual.Size = new Vector2(card.Width, card.Height);
        box.Size = Visual.Size;
        // A one-pixel hairline at any scale.
        shape.StrokeThickness = (float)(1 / scale);
        tick.Offset = new Vector2(mark.X - card.X, mark.Y - card.Y);
        tick.FillBrush = mark.Width > 0 ? accent : clear;
        Visual.IsVisible = true;
    }

    private static Windows.UI.Color Argb(uint c) => Windows.UI.Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
}
