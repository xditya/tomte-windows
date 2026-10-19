using System.Numerics;
using Tomte.Core.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.DirectWrite;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.System.Com;

namespace Tomte.Shell;

/// <summary>
/// The Direct2D half of text, the same for the island and for offscreen captures: the device text is drawn
/// with, and the one call that draws a laid-out run.
/// </summary>
internal static unsafe class TextDraw
{
    /// <summary>
    /// A Direct2D device on the software Direct3D device (WARP), as the art has: a few short runs per show need
    /// no GPU, and the software device keeps the GPU driver out of this process. The first one in a process takes
    /// tens of milliseconds, so callers make it off the UI thread; the factory is multithreaded because the
    /// device is made on one thread and drawn with on another. The caller releases both devices.
    /// </summary>
    public static ID2D1Device* CreateDevice(out ID3D11Device* d3d)
    {
        ID3D11Device* made;
        PInvoke.D3D11CreateDevice(
            null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP, HMODULE.Null, D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
            null, 0, PInvoke.D3D11_SDK_VERSION, &made, null, null).ThrowOnFailure();
        IDXGIDevice* dxgi = null;
        ID2D1Factory1* factory = null;
        try
        {
            made->QueryInterface(out dxgi).ThrowOnFailure();
            var iid = ID2D1Factory1.IID_Guid;
            PInvoke.D2D1CreateFactory(D2D1_FACTORY_TYPE.D2D1_FACTORY_TYPE_MULTI_THREADED, &iid, null, (void**)&factory).ThrowOnFailure();
            ID2D1Device* device;
            factory->CreateDevice(dxgi, &device);
            d3d = made;
            return device;
        }
        catch
        {
            made->Release();
            throw;
        }
        finally
        {
            if (dxgi is not null)
            {
                dxgi->Release();
            }

            // The device keeps its own reference to the factory.
            if (factory is not null)
            {
                factory->Release();
            }
        }
    }

    /// <summary>
    /// Draw a laid-out run with its box's top-left corner at a point, in DIPs, on a context whose DPI is the
    /// display scale. Grayscale anti-aliasing: the surfaces are transparent, and ClearType's per-channel coverage
    /// has nothing known to blend with until the compositor puts the surface over the pill.
    /// </summary>
    public static void Run(ID2D1DeviceContext* dc, TextLayout layout, Vector2 at)
    {
        var brush = Brush(dc, layout.Run.Style.Color);
        var text = (IDWriteTextLayout*)layout.Native;
        ID2D1SolidColorBrush* tint = null;
        if (layout.Run.Tint.Count > 0 && layout.KeepsParts)
        {
            // Direct2D draws a range with the brush set as its drawing effect instead of the default one.
            tint = Brush(dc, layout.Run.TintColor);
            foreach (var (start, length) in layout.Run.Tint)
            {
                text->SetDrawingEffect((IUnknown*)tint, new DWRITE_TEXT_RANGE { startPosition = (uint)start, length = (uint)length });
            }
        }

        dc->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE.D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
        // Colour fonts so an emoji in a song title keeps its colours.
        dc->DrawTextLayout(new D2D_POINT_2F { x = at.X, y = at.Y }, text, (ID2D1Brush*)brush, D2D1_DRAW_TEXT_OPTIONS.D2D1_DRAW_TEXT_OPTIONS_ENABLE_COLOR_FONT);
        if (tint is not null)
        {
            // The layout outlives this device context: it must not keep a brush of it.
            text->SetDrawingEffect(null, new DWRITE_TEXT_RANGE { startPosition = 0, length = (uint)layout.Run.Text.Length });
            tint->Release();
        }

        brush->Release();
    }

    private static ID2D1SolidColorBrush* Brush(ID2D1DeviceContext* dc, uint c)
    {
        var color = new D2D1_COLOR_F { r = ((c >> 16) & 0xFF) / 255f, g = ((c >> 8) & 0xFF) / 255f, b = (c & 0xFF) / 255f, a = (c >> 24) / 255f };
        ID2D1SolidColorBrush* brush;
        dc->CreateSolidColorBrush(&color, null, &brush);
        return brush;
    }
}
