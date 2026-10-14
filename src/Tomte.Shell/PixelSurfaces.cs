using System.Drawing;
using System.Runtime.InteropServices;
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
/// Decoded pixels (album art, shell icons) on composition surfaces. Windows.UI.Composition only takes pixels
/// through a Direct3D or Direct2D device, so one lives here until Dispose: the software device (WARP), because the
/// job is a few copies per showing and the software device keeps the GPU driver out of this process. One surface
/// and brush per image. Safe to build on a pool thread, which the first device in a process needs (30 to 90 ms);
/// Dispose releases every surface, every brush and the device.
/// a lost device is not handled, because the software device is not lost with the GPU.
/// </summary>
internal sealed unsafe class PixelSurfaces : IDisposable
{
    private readonly Compositor compositor;
    private readonly CompositionGraphicsDevice graphics;
    private readonly List<IDisposable> made = [];
    private ID3D11Device* device;
    private ID3D11DeviceContext* context;

    public PixelSurfaces(Compositor compositor)
    {
        this.compositor = compositor;
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
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    /// <summary>
    /// A brush over premultiplied BGRA pixels, top row first; they show 1:1 when the visual is the image's size in
    /// pixels. Callable from any thread.
    /// </summary>
    public CompositionSurfaceBrush Brush(int width, int height, byte[] pixels)
    {
        var surface = graphics.CreateDrawingSurface(new Windows.Foundation.Size(width, height), DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
        made.Add(surface);
        Upload(surface, width, height, pixels);
        var brush = compositor.CreateSurfaceBrush(surface);
        made.Add(brush);
        return brush;
    }

    public void Dispose()
    {
        if (device is null)
        {
            return;
        }

        foreach (var item in made)
        {
            item.Dispose();
        }

        made.Clear();
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
                var created = CompositionGraphicsDevice.FromAbi((nint)raw);
                Marshal.Release((nint)raw);
                return created;
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
    private void Upload(CompositionDrawingSurface surface, int width, int height, byte[] pixels)
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
                        right = (uint)(offset.X + width),
                        bottom = (uint)(offset.Y + height),
                        back = 1,
                    };
                    fixed (byte* bits = pixels)
                    {
                        context->UpdateSubresource(texture, 0, &box, bits, (uint)width * 4, 0);
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
