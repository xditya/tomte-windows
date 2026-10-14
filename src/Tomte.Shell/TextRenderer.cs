using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Tomte.Core.Text;
using Windows.Graphics;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.System.WinRT.Composition;

namespace Tomte.Shell;

/// <summary>
/// Text on the island. Views ask it for <see cref="TextVisual"/>s and place them in DIPs inside a container that
/// hangs off <see cref="IslandWindow.Dips"/> (a panel body, a Compact view's root). Each visual is a sprite whose
/// surface is drawn once per run and scale, never per frame. The Direct2D device behind them lives while any
/// visual does: it is made off the UI thread when the first one appears and released when the last one goes,
/// which is when the island hides. A scale change redraws every visual at the new scale.
/// </summary>
internal sealed class TextRenderer
{
    private readonly List<TextVisual> live = [];
    private readonly Action<string>? log;
    private Task<TextDevice>? making;

    public TextRenderer(IslandWindow island, Action<string>? log)
    {
        Island = island;
        this.log = log;
        // The first layout in a process takes about 25 ms (the DirectWrite factory, the font, the interop code).
        // Paid here, at start-up with nothing on screen, instead of in the middle of the first reveal; the
        // factory and the formats then stay for the life of the process, about 1 MB.
        long started = Stopwatch.GetTimestamp();
        TextLayout.Of(new TextRun("Tomte 00:00", new TextStyle(13, 600), 100)).Dispose();
        log?.Invoke($"text: first layout in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms");
        island.LayoutChanged += () =>
        {
            foreach (var visual in live.ToArray())
            {
                visual.Refresh();
            }
        };
    }

    internal IslandWindow Island { get; }

    /// <summary>Null until the device has been made, and again once the last visual is gone.</summary>
    internal TextDevice? Device { get; private set; }

    /// <summary>A text visual inside <paramref name="host"/>, empty until shown. Dispose it with the view.</summary>
    public TextVisual Create(ContainerVisual host)
    {
        var visual = new TextVisual(this, host);
        live.Add(visual);
        if (Device is null)
        {
            Make();
        }

        return visual;
    }

    internal void Failed(Exception e) => log?.Invoke($"text: a run was not drawn: {e.Message}");

    internal void Released(TextVisual visual)
    {
        live.Remove(visual);
        if (live.Count == 0 && Device is not null)
        {
            Device.Dispose();
            Device = null;
            log?.Invoke("text: device released");
        }
    }

    private async void Make()
    {
        if (making is not null)
        {
            return;
        }

        long started = Stopwatch.GetTimestamp();
        TextDevice made;
        try
        {
            making = Task.Run(() => new TextDevice(Island.Compositor));
            made = await making;
        }
        catch (Exception e)
        {
            // No device: the text stays undrawn and the rest of the island works.
            log?.Invoke($"text: no device: {e.Message}");
            return;
        }
        finally
        {
            making = null;
        }

        if (live.Count == 0)
        {
            made.Dispose();
            return;
        }

        Device = made;
        log?.Invoke($"text: device in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1} ms, off the UI thread");
        foreach (var visual in live.ToArray())
        {
            visual.Draw();
        }
    }
}

/// <summary>
/// The Direct2D device on the software Direct3D device, and the composition graphics device over it that the
/// text surfaces come from. Safe to make on any thread.
/// a lost device is not handled, because the software device is not lost with the GPU.
/// </summary>
internal sealed unsafe class TextDevice : IDisposable
{
    private ID3D11Device* d3d;
    private ID2D1Device* d2d;

    public TextDevice(Compositor compositor)
    {
        d2d = TextDraw.CreateDevice(out d3d);
        try
        {
            Graphics = PixelSurfaces.CreateGraphicsDevice(compositor, d2d);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public CompositionGraphicsDevice Graphics { get; } = null!;

    public void Dispose()
    {
        if (d2d is null)
        {
            return;
        }

        Graphics?.Dispose();
        d2d->Release();
        d3d->Release();
        d2d = null;
        d3d = null;
    }
}

/// <summary>
/// One run of text as a sprite. <see cref="Show(TextRun, Vector2)"/> places it at once and draws it only when the
/// run or the scale changed. It lands on a whole device pixel and its surface is a whole number of pixels, so it
/// shows 1:1 at 100%, 125% and 150%. It fades with whatever it sits in.
/// </summary>
internal sealed unsafe class TextVisual : IDisposable
{
    private readonly TextRenderer renderer;
    private readonly TextBlock block = new();
    private readonly CompositionSurfaceBrush brush;
    private CompositionDrawingSurface? surface;
    private Vector2 at;
    private bool drawn, disposed;

    public TextVisual(TextRenderer renderer, ContainerVisual host)
    {
        this.renderer = renderer;
        var compositor = renderer.Island.Compositor;
        brush = compositor.CreateSurfaceBrush();
        brush.Stretch = CompositionStretch.Fill;
        Visual = compositor.CreateSpriteVisual();
        Visual.Brush = brush;
        Visual.IsVisible = false;
        host.Children.InsertAtTop(Visual);
    }

    public SpriteVisual Visual { get; }

    /// <summary>The run as laid out, in DIPs: the widest line and the height of the lines shown. Zero when empty.</summary>
    public Vector2 Size => new(block.Size.Width, block.Size.Height);

    /// <summary>Show a run with its box's top-left corner at this point of the host, in DIPs.</summary>
    public void Show(TextRun run, Vector2 topLeft)
    {
        if (block.Set(run, renderer.Island.Scale))
        {
            drawn = false;
            Draw();
        }

        at = topLeft;
        Place();
    }

    /// <summary>Show one line in a slot of the host: as wide as the slot, centred in its height.</summary>
    public void Show(string text, TextStyle style, RectangleF slot)
    {
        var run = new TextRun(text, style, slot.Width);
        if (block.Set(run, renderer.Island.Scale))
        {
            drawn = false;
            Draw();
        }

        at = new Vector2(slot.X, slot.Y + (slot.Height - Size.Y) / 2);
        Place();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Visual.Dispose();
        brush.Dispose();
        surface?.Dispose();
        surface = null;
        block.Dispose();
        renderer.Released(this);
    }

    /// <summary>The scale changed: draw again at the new one and land on its pixel grid.</summary>
    internal void Refresh()
    {
        if (block.Run is { } run && block.Set(run, renderer.Island.Scale))
        {
            drawn = false;
            Draw();
        }

        Place();
    }

    /// <summary>Draw the run if it has not been drawn at this scale. Without a device yet it waits for one.</summary>
    internal void Draw()
    {
        if (drawn || disposed)
        {
            return;
        }

        if (block.Layout is not { } layout)
        {
            Visual.IsVisible = false;
            drawn = true;
            return;
        }

        if (renderer.Device is not { } device)
        {
            return;
        }

        var (width, height) = block.Pixels;
        var size = new SizeInt32 { Width = width, Height = height };
        try
        {
            if (surface is null)
            {
                surface = device.Graphics.CreateDrawingSurface2(size, DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
                brush.Surface = surface;
            }
            else if (surface.SizeInt32.Width != width || surface.SizeInt32.Height != height)
            {
                surface.Resize(size);
            }

            Paint(surface, layout, block.Scale);
        }
        catch (Exception e)
        {
            // A surface that cannot be drawn leaves this run blank; it must not take the island down with it.
            renderer.Failed(e);
            return;
        }
        drawn = true;
        Visual.IsVisible = true;
        Place();
    }

    private static void Paint(CompositionDrawingSurface surface, TextLayout layout, double scale)
    {
        nint unknown = WinRT.MarshalInspectable<object>.FromManaged(surface);
        try
        {
            var iid = ICompositionDrawingSurfaceInterop.IID_Guid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out nint raw));
            var interop = (ICompositionDrawingSurfaceInterop*)raw;
            try
            {
                ID2D1DeviceContext* dc;
                Point offset;
                var dcIid = ID2D1DeviceContext.IID_Guid;
                interop->BeginDraw(null, &dcIid, (void**)&dc, &offset);
                try
                {
                    // The surface is a rectangle of a larger texture; BeginDraw says where, in pixels.
                    float s = (float)scale, pad = TextBlock.Pad / s;
                    dc->SetDpi(96 * s, 96 * s);
                    var move = new D2D_MATRIX_3X2_F { m11 = 1, m22 = 1, dx = offset.X / s, dy = offset.Y / s };
                    dc->SetTransform(&move);
                    var clear = default(D2D1_COLOR_F);
                    dc->Clear(&clear);
                    TextDraw.Run(dc, layout, new Vector2(pad));
                }
                finally
                {
                    dc->Release();
                    interop->EndDraw();
                }
            }
            finally
            {
                Marshal.Release(raw);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>
    /// Snap the box's corner to the pixel grid: island space starts on a whole pixel, so the corner lands on
    /// one when its distance from there, times the scale, is whole. The host's own offset counts toward it.
    /// </summary>
    private void Place()
    {
        double scale = block.Scale > 0 ? block.Scale : renderer.Island.Scale;
        var host = HostOffset();
        float pad = (float)(TextBlock.Pad / scale);
        Visual.Offset = new Vector3(PixelGrid.Snap(host.X, at.X, scale) - pad, PixelGrid.Snap(host.Y, at.Y, scale) - pad, 0);
        var (width, height) = block.Pixels;
        Visual.Size = new Vector2((float)(width / scale), (float)(height / scale));
    }

    /// <summary>
    /// How far the host sits from island space's origin, in DIPs: the offsets of every container up to
    /// <see cref="IslandWindow.Dips"/>, which is the first one that carries the scale.
    /// </summary>
    private Vector2 HostOffset()
    {
        var sum = Vector2.Zero;
        for (var parent = Visual.Parent; parent is not null && !ReferenceEquals(parent, renderer.Island.Dips) && parent.Scale == Vector3.One; parent = parent.Parent)
        {
            sum += new Vector2(parent.Offset.X, parent.Offset.Y);
        }

        return sum;
    }
}
