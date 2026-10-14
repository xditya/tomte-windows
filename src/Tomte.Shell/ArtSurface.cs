using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Tomte.Core.Media;
using Windows.Graphics.DirectX;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.System.WinRT.Composition;

namespace Tomte.Shell;

/// <summary>
/// A rounded square that shows album art, or a neutral fill while there is none. Built when a view shows
/// and disposed when it goes, with the surface and the device behind it.
/// </summary>
internal sealed class ArtVisual : IDisposable
{
    private readonly Compositor compositor;
    private readonly CompositionColorBrush empty;
    private ArtSurface? surface;
    private MediaThumbnail? showing;
    private bool disposed;

    public ArtVisual(Compositor compositor, RectangleF rect, float corner)
    {
        this.compositor = compositor;
        empty = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(28, 255, 255, 255));
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
        ArtSurface? next = null;
        if (art is not null)
        {
            try
            {
                // The device and the surface take 30 to 90 ms the first time in a process and about 10 ms
                // after that, so they are made on a pool thread (composition objects are agile). Only
                // putting the finished brush on the visual happens here.
                long started = Stopwatch.GetTimestamp();
                next = await Task.Run(() => new ArtSurface(compositor, art));
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

        Visual.Brush = next is null ? empty : next.Brush;
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

/// <summary>
/// Decoded art on a composition surface. Windows.UI.Composition only takes pixels through a Direct3D or
/// Direct2D device, so one lives here until Dispose. It is the software device (WARP): the one job is
/// copying a thumbnail once per track, and the software device keeps the GPU driver out of this process.
/// a lost device is not handled, because the software device is not lost with the GPU.
/// </summary>
internal sealed unsafe class ArtSurface : IDisposable
{
    private readonly CompositionGraphicsDevice graphics;
    private readonly CompositionDrawingSurface surface;
    private ID3D11Device* device;
    private ID3D11DeviceContext* context;

    /// <summary>Safe to call from any thread, and slow enough the first time that it should not be the UI thread.</summary>
    public ArtSurface(Compositor compositor, MediaThumbnail art)
    {
        ID3D11Device* d;
        ID3D11DeviceContext* c;
        PInvoke.D3D11CreateDevice(
            null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP, HMODULE.Null, D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            null, 0, PInvoke.D3D11_SDK_VERSION, &d, null, &c).ThrowOnFailure();
        device = d;
        context = c;
        try
        {
            graphics = CreateGraphicsDevice(compositor, device);
            surface = graphics.CreateDrawingSurface(new Windows.Foundation.Size(art.Width, art.Height), DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
            Upload(art);
            Brush = compositor.CreateSurfaceBrush(surface);
            // Art that is not square is cropped to the square, centred.
            Brush.Stretch = CompositionStretch.UniformToFill;
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public CompositionSurfaceBrush Brush { get; } = null!;

    public void Dispose()
    {
        if (device is null)
        {
            return;
        }

        Brush?.Dispose();
        surface?.Dispose();
        graphics?.Dispose();
        context->Release();
        device->Release();
        device = null;
    }

    private static CompositionGraphicsDevice CreateGraphicsDevice(Compositor compositor, ID3D11Device* device)
    {
        nint unknown = WinRT.MarshalInspectable<object>.FromManaged(compositor);
        try
        {
            var iid = ICompositorInterop.IID_Guid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out nint interop));
            try
            {
                // Raw vtable call (slot 5), as for the window target: the generated marshaler returns a bare __ComObject.
                void** vtbl = *(void***)interop;
                void* raw;
                ((delegate* unmanaged[Stdcall]<void*, void*, void**, HRESULT>)vtbl[5])((void*)interop, device, &raw).ThrowOnFailure();
                var graphics = CompositionGraphicsDevice.FromAbi((nint)raw);
                Marshal.Release((nint)raw);
                return graphics;
            }
            finally
            {
                Marshal.Release(interop);
            }
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>Copy the pixels in. The surface is a rectangle of a larger texture; BeginDraw says where.</summary>
    private void Upload(MediaThumbnail art)
    {
        nint unknown = WinRT.MarshalInspectable<object>.FromManaged(surface);
        try
        {
            var iid = ICompositionDrawingSurfaceInterop.IID_Guid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out nint raw));
            var interop = (ICompositionDrawingSurfaceInterop*)raw;
            try
            {
                IDXGISurface* target;
                Point offset;
                var surfaceIid = IDXGISurface.IID_Guid;
                interop->BeginDraw(null, &surfaceIid, (void**)&target, &offset);
                try
                {
                    target->QueryInterface(out ID3D11Resource* texture).ThrowOnFailure();
                    var box = new D3D11_BOX
                    {
                        left = (uint)offset.X,
                        top = (uint)offset.Y,
                        right = (uint)(offset.X + art.Width),
                        bottom = (uint)(offset.Y + art.Height),
                        back = 1,
                    };
                    fixed (byte* pixels = art.Pixels)
                    {
                        context->UpdateSubresource(texture, 0, &box, pixels, (uint)art.Width * 4, 0);
                    }

                    texture->Release();
                }
                finally
                {
                    target->Release();
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
}
