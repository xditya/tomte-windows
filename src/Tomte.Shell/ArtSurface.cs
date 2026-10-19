using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using Tomte.Core.Media;
using Windows.UI.Composition;

namespace Tomte.Shell;

/// <summary>
/// A rounded square that shows album art, or a neutral fill while there is none. Built when a view shows
/// and disposed when it goes, with the surface and the device behind it.
/// </summary>
internal sealed class ArtVisual : IDisposable
{
    private readonly Compositor compositor;
    private readonly CompositionColorBrush empty;
    private PixelSurfaces? surface;
    private MediaThumbnail? showing;
    private bool disposed;

    public ArtVisual(Compositor compositor, RectangleF rect, float corner)
    {
        this.compositor = compositor;
        empty = compositor.CreateColorBrush(ShelfColors.Of(Tomte.Core.Theme.Raised));
        var round = compositor.CreateRoundedRectangleGeometry();
        round.Size = new Vector2(rect.Width, rect.Height);
        round.CornerRadius = new Vector2(corner);
        Visual = compositor.CreateSpriteVisual();
        Visual.Offset = new Vector3(rect.X, rect.Y, 0);
        Visual.Size = round.Size;
        Visual.Clip = compositor.CreateGeometricClip(round);
        Visual.Brush = empty;
    }

    public SpriteVisual Visual { get; }

    /// <summary>
    /// Show this art. The same art again costs nothing. Other art keeps what is up until its surface is
    /// ready, a few milliseconds later, so the square never blinks empty between two tracks.
    /// </summary>
    public async void Show(MediaThumbnail? art, Action<string>? log)
    {
        if (ReferenceEquals(art, showing))
        {
            return;
        }

        showing = art;
        PixelSurfaces? next = null;
        CompositionSurfaceBrush? brush = null;
        if (art is not null)
        {
            try
            {
                // The device and the surface take 30 to 90 ms the first time in a process and about 10 ms
                // after that, so they are made on a pool thread (composition objects are agile). Only
                // putting the finished brush on the visual happens here.
                long started = Stopwatch.GetTimestamp();
                (next, brush) = await Task.Run(() =>
                {
                    var made = new PixelSurfaces(compositor);
                    try
                    {
                        var b = made.Brush(art.Width, art.Height, art.Pixels);
                        // Art that is not square is cropped to the square, centred.
                        b.Stretch = CompositionStretch.UniformToFill;
                        return (made, b);
                    }
                    catch
                    {
                        made.Dispose();
                        throw;
                    }
                });
                log?.Invoke($"media: art on a surface in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms, off the UI thread");
            }
            catch (Exception e)
            {
                // No device (a driver restarting, a session without one): the neutral fill shows instead.
                log?.Invoke($"media: no surface for the art: {e.Message}");
            }
        }

        if (disposed || !ReferenceEquals(art, showing))
        {
            next?.Dispose();
            return;
        }

        Visual.Brush = brush is null ? empty : brush;
        surface?.Dispose();
        surface = next;
    }

    public void Dispose()
    {
        disposed = true;
        Visual.Dispose();
        surface?.Dispose();
        surface = null;
    }
}
