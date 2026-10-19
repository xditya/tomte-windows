using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Text;
using Windows.UI.Composition;

namespace Tomte.Shell;

/// <summary>
/// The key hints of one view: caps drawn in one shape visual and their keys and words as text, made as they are first
/// needed and emptied, not disposed, when fewer show. Island thread only.
/// </summary>
internal sealed class Keycaps : IDisposable
{
    private readonly Compositor compositor;
    private readonly TextRenderer text;
    private readonly ContainerVisual host;
    private readonly ShapeVisual shapes;
    private readonly List<(CompositionRoundedRectangleGeometry Cap, TextVisual Keys, TextVisual Word)> caps = [];
    private int shown;

    public Keycaps(Compositor compositor, TextRenderer text, ContainerVisual host, Vector2 size)
    {
        this.compositor = compositor;
        this.text = text;
        this.host = host;
        shapes = compositor.CreateShapeVisual();
        shapes.Size = size;
        host.Children.InsertAtTop(shapes);
    }

    /// <summary>Starts a new set: the caps shown after this replace the ones before, and the rest are emptied by <see cref="End"/>.</summary>
    public void Begin() => shown = 0;

    /// <summary>One cap with its keys, and the word after it when there is one.</summary>
    public void Add(RectangleF cap, string keys, TextSlot? word = null)
    {
        if (shown == caps.Count)
        {
            var geometry = compositor.CreateRoundedRectangleGeometry();
            geometry.CornerRadius = new Vector2(Keycap.Corner);
            var sprite = compositor.CreateSpriteShape(geometry);
            sprite.FillBrush = compositor.CreateColorBrush(ShelfColors.Of(Keycap.Fill));
            sprite.StrokeBrush = compositor.CreateColorBrush(ShelfColors.Of(Keycap.Border));
            sprite.StrokeThickness = 1;
            shapes.Shapes.Add(sprite);
            caps.Add((geometry, text.Create(host), text.Create(host)));
        }

        var (round, label, after) = caps[shown++];
        // The border is drawn on the cap's edge, half a DIP in.
        round.Offset = new Vector2(cap.X + 0.5f, cap.Y + 0.5f);
        round.Size = new Vector2(cap.Width - 1, cap.Height - 1);
        label.Show(keys, Keycap.Style, cap);
        after.Show(word ?? new TextSlot("", Keycap.Style, RectangleF.Empty));
    }

    /// <summary>Empties every cap past the ones added since <see cref="Begin"/>.</summary>
    public void End()
    {
        for (int i = shown; i < caps.Count; i++)
        {
            caps[i].Cap.Size = Vector2.Zero;
            caps[i].Keys.Show("", Keycap.Style, RectangleF.Empty);
            caps[i].Word.Show("", Keycap.Style, RectangleF.Empty);
        }
    }

    /// <summary>The words and keys again on whole pixels, after the host moved.</summary>
    public void Refresh()
    {
        foreach (var (_, keys, word) in caps)
        {
            keys.Refresh();
            word.Refresh();
        }
    }

    public void Dispose()
    {
        foreach (var (_, keys, word) in caps)
        {
            keys.Dispose();
            word.Dispose();
        }

        shapes.Dispose();
    }
}
