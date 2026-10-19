using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Text;
using Windows.UI.Composition;

namespace Tomte.Shell;

/// <summary>
/// The pieces every panel draws the same way, on a <see cref="Parts"/> so they go with it, besides its own glyph and
/// keycap: a capsule, a chip, a section card and a round icon button. All in DIPs, laid out by the numbers in Tomte.Core
/// (<see cref="Chip"/>, <see cref="Section"/>).
/// </summary>
internal static class PanelChrome
{
    public static CompositionColorBrush Brush(this Parts p, uint argb) => p.Brush(ShelfColors.Of(argb));

    /// <summary>A capsule: a box rounded by half its height.</summary>
    public static CompositionSpriteShape Capsule(this Parts p, ShapeVisual into, RectangleF at, CompositionBrush? fill, CompositionBrush? stroke = null) =>
        p.Box(into, at, at.Height / 2, fill, stroke);

    /// <summary>A chip: an outlined capsule with its icon and word, or filled while it holds something.</summary>
    public static void Chip(this Parts p, ShapeVisual into, RectangleF at, GlyphFigure[] icon, string label, bool filled)
    {
        p.Capsule(into, at, filled ? p.Brush(Theme.Raised) : null, filled ? null : p.Brush(Theme.LineStrong));
        float mid = at.Y + (at.Height / 2);
        var glyph = new RectangleF(at.X + Core.Chip.PadX, mid - (Core.Chip.Icon / 2), Core.Chip.Icon, Core.Chip.Icon);
        p.Glyph(into, icon, ShelfColors.Of(filled ? Theme.Ink : Theme.Ink2), glyph);
        float x = glyph.Right + Core.Chip.IconGap;
        p.Text(into, new TextSlot(label, filled ? Core.Chip.Style with { Color = Theme.Ink } : Core.Chip.Style, new(x, at.Y, at.Right - Core.Chip.PadX - x, at.Height)));
    }

    /// <summary>A section's card.</summary>
    public static void SectionCard(this Parts p, ShapeVisual into, RectangleF at) => p.Box(into, at, Section.Corner, p.Brush(Theme.Card));

    /// <summary>A round button with a glyph centred in it: a fill (or none) and the glyph's ink.</summary>
    public static void RoundButton(this Parts p, ShapeVisual into, RectangleF at, GlyphFigure[] icon, float iconSize, uint? fill, uint ink)
    {
        if (fill is { } f)
        {
            p.Box(into, at, at.Height / 2, p.Brush(f));
        }

        p.Glyph(into, icon, ShelfColors.Of(ink), new RectangleF(at.X + ((at.Width - iconSize) / 2), at.Y + ((at.Height - iconSize) / 2), iconSize, iconSize));
    }
}
