using System.Numerics;
using Tomte.Core;
using Tomte.Core.Clipboard;
using Windows.UI.Composition;

namespace Tomte.Shell;

/// <summary>
/// The thumbnails of one build of the Clipboard panel's rows. Each image row's slot shows a neutral fill at
/// once; <see cref="Load"/> then decodes the stored thumbnails to the slot's size in pixels at the island's
/// scale and puts them on one set of surfaces, all on a pool thread. Disposed with the build.
/// </summary>
internal sealed class ClipboardThumbs(Compositor compositor, double scale, Action<string>? log) : IDisposable
{
    private const float Corner = 6;
    private static readonly Windows.UI.Color Placeholder = Windows.UI.Color.FromArgb(28, 255, 255, 255);

    private readonly List<(SpriteVisual Slot, byte[] Thumb)> slots = [];
    private PixelSurfaces? surfaces;
    private bool disposed;

    /// <summary>The slot for one row's thumbnail, inside <paramref name="row"/>.</summary>
    public void Add(ContainerVisual row, byte[] thumb)
    {
        var at = ClipboardPanelLayout.ThumbIn;
        var round = compositor.CreateRoundedRectangleGeometry();
        round.Size = new Vector2(at.Width, at.Height);
        round.CornerRadius = new Vector2(Corner);
        var slot = compositor.CreateSpriteVisual();
        slot.Offset = new Vector3(at.X, at.Y, 0);
        slot.Size = round.Size;
        slot.Clip = compositor.CreateGeometricClip(round);
        slot.Brush = compositor.CreateColorBrush(Placeholder);
        row.Children.InsertAtTop(slot);
        slots.Add((slot, thumb));
    }

    /// <summary>Call once the rows are built. Nothing happens without an image row.</summary>
    public async void Load()
    {
        if (slots.Count == 0)
        {
            return;
        }

        var wanted = slots.ToList();
        int width = (int)Math.Round(ClipboardPanelLayout.ThumbWidth * scale), height = (int)Math.Round(ClipboardPanelLayout.ThumbHeight * scale);
        try
        {
            var (made, brushes) = await Task.Run(() =>
            {
                var pixels = wanted.Select(s => ClipboardImages.Thumbnail(s.Thumb, width, height)).ToList();
                var made = new PixelSurfaces(compositor);
                try
                {
                    return (made, pixels.Select(p => made.Brush(width, height, p)).ToList());
                }
                catch
                {
                    made.Dispose();
                    throw;
                }
            });
            if (disposed)
            {
                made.Dispose();
                return;
            }

            surfaces = made;
            for (int i = 0; i < wanted.Count; i++)
            {
                wanted[i].Slot.Brush = brushes[i];
            }
        }
        catch (Exception e)
        {
            // A thumbnail that does not decode, or no device: the neutral fills stay.
            log?.Invoke($"clipboard panel: no thumbnails ({e.GetType().Name})");
        }
    }

    public void Dispose()
    {
        disposed = true;
        foreach (var (slot, _) in slots)
        {
            slot.Dispose();
        }

        slots.Clear();
        surfaces?.Dispose();
        surfaces = null;
    }
}
