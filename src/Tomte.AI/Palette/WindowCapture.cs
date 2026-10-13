using System.Runtime.InteropServices;
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
/// One frame of a window through Windows.Graphics.Capture, as a PNG no wider than
/// <see cref="MaxWidth"/>. The D3D device exists only for the call; nothing stays allocated.
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
            using var bitmap = await first.Task.WaitAsync(FirstFrameTimeout, ct);
            return await EncodePngAsync(bitmap);
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

    private static async Task<byte[]> EncodePngAsync(SoftwareBitmap bitmap)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream);
        encoder.SetSoftwareBitmap(bitmap);
        if (bitmap.PixelWidth > MaxWidth)
        {
            encoder.BitmapTransform.ScaledWidth = MaxWidth;
            encoder.BitmapTransform.ScaledHeight = (uint)Math.Max(1, (long)bitmap.PixelHeight * MaxWidth / bitmap.PixelWidth);
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
        }

        await encoder.FlushAsync();
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
