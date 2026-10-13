using System.Runtime.InteropServices;
using Tomte.Core;
using Windows.Graphics;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;

namespace Tomte.AI.Palette;

/// <summary>
/// One frame of a window, or a region of a monitor, through Windows.Graphics.Capture, as a PNG no
/// wider than <see cref="MaxWidth"/>. The D3D device exists only for the call; nothing stays allocated.
/// </summary>
public static class WindowCapture
{
    public const int MaxWidth = 1280;
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromSeconds(3);

    public static async Task<byte[]> CapturePngAsync(nint hwnd, CancellationToken ct)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new InvalidOperationException("Window capture is not available on this PC.");
        var item = GraphicsCaptureItem.TryCreateFromWindowId(new Windows.UI.WindowId((ulong)hwnd))
            ?? throw new InvalidOperationException("The window cannot be captured.");

        using var bitmap = await FirstFrameAsync(item, ct);
        return await EncodePngAsync(bitmap, new PixelRect(0, 0, bitmap.PixelWidth, bitmap.PixelHeight));
    }

    /// <summary><paramref name="crop"/> is in the monitor's physical pixels, which is what the frame is in.</summary>
    public static async Task<byte[]> CaptureMonitorRegionPngAsync(nint hmonitor, PixelRect crop, CancellationToken ct)
    {
        if (!GraphicsCaptureSession.IsSupported())
            throw new InvalidOperationException("Screen capture is not available on this PC.");
        var item = GraphicsCaptureItem.TryCreateFromDisplayId(new DisplayId((ulong)hmonitor))
            ?? throw new InvalidOperationException("The screen cannot be captured.");

        using var bitmap = await FirstFrameAsync(item, ct);
        return await EncodePngAsync(bitmap, crop);
    }

    /// <summary>
    /// The encoder scales first and crops after, in scaled pixels, so a crop wider than
    /// <paramref name="maxWidth"/> scales the whole frame by the crop's factor and scales the crop with it.
    /// The crop is clamped to the frame first.
    /// </summary>
    internal static (int ScaledWidth, int ScaledHeight, PixelRect Bounds) Fit(int width, int height, PixelRect crop, int maxWidth)
    {
        int x = Math.Clamp(crop.X, 0, width - 1), y = Math.Clamp(crop.Y, 0, height - 1);
        int w = Math.Clamp(crop.Width, 1, width - x), h = Math.Clamp(crop.Height, 1, height - y);
        if (w <= maxWidth)
            return (width, height, new PixelRect(x, y, w, h));
        double f = (double)maxWidth / w;
        int sw = Math.Max(1, (int)Math.Round(width * f)), sh = Math.Max(1, (int)Math.Round(height * f));
        int bx = Math.Min((int)(x * f), sw - 1), by = Math.Min((int)(y * f), sh - 1);
        return (sw, sh, new PixelRect(bx, by, Math.Min(maxWidth, sw - bx), Math.Clamp((int)Math.Round(h * f), 1, sh - by)));
    }

    /// <summary>One frame, then the session and pool are gone before the encode starts, so no later frame is copied.</summary>
    private static async Task<SoftwareBitmap> FirstFrameAsync(GraphicsCaptureItem item, CancellationToken ct)
    {
        var device = CreateDevice();
        try
        {
            using var pool = Direct3D11CaptureFramePool.CreateFreeThreaded(device, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
            using var session = pool.CreateCaptureSession(item);
            session.IsCursorCaptureEnabled = false;
            try
            {
                // Without the graphicsCaptureWithoutBorder capability (MSIX only) the request fails or is
                // ignored and the yellow border shows for the one frame; the capture still works.
                await GraphicsCaptureAccess.RequestAccessAsync(GraphicsCaptureAccessKind.Borderless);
                session.IsBorderRequired = false;
            }
            catch (Exception e) when (e is UnauthorizedAccessException or COMException)
            {
            }

            var first = new TaskCompletionSource<SoftwareBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
            pool.FrameArrived += async (sender, _) =>
            {
                // Frames keep arriving at the monitor's rate until the session is disposed; only the first one is wanted.
                if (first.Task.IsCompleted)
                    return;
                try
                {
                    using var frame = sender.TryGetNextFrame();
                    if (frame is not null)
                        first.TrySetResult(await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface));
                }
                catch (Exception e)
                {
                    first.TrySetException(e);
                }
            };
            session.StartCapture();
            // A minimized window never produces a frame; fail fast instead of spending the request's 120 s.
            return await first.Task.WaitAsync(FirstFrameTimeout, ct);
        }
        finally
        {
            device.Dispose();
        }
    }

    private static unsafe IDirect3DDevice CreateDevice()
    {
        PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_HARDWARE, HMODULE.Null,
            D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT, null, PInvoke.D3D11_SDK_VERSION,
            out ID3D11Device? d3d, out _, out _).ThrowOnFailure();
        PInvoke.CreateDirect3D11DeviceFromDXGIDevice((IDXGIDevice)d3d!, out var inspectable).ThrowOnFailure();
        nint abi = Marshal.GetIUnknownForObject(inspectable);
        try
        {
            return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi(abi);
        }
        finally
        {
            Marshal.Release(abi);
        }
    }

    private static async Task<byte[]> EncodePngAsync(SoftwareBitmap bitmap, PixelRect crop)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);
        var (sw, sh, b) = Fit(bitmap.PixelWidth, bitmap.PixelHeight, crop, MaxWidth);
        if (sw != bitmap.PixelWidth)
        {
            encoder.BitmapTransform.ScaledWidth = (uint)sw;
            encoder.BitmapTransform.ScaledHeight = (uint)sh;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        encoder.BitmapTransform.Bounds = new BitmapBounds { X = (uint)b.X, Y = (uint)b.Y, Width = (uint)b.Width, Height = (uint)b.Height };

        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
