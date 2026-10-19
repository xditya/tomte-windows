using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices.WindowsRuntime;
using Tomte.Core.Interaction;
using Tomte.Shell;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.System;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.Graphics.Dxgi;
using Windows.Win32.System.WinRT;

namespace TextCaptures;

/// <summary>
/// The island's own composition code drawn by the compositor rather than from its numbers, captured from the visual
/// with no window: the tooltip's card shown as a plain tip and then, as the island reuses it, as a "Copied"
/// acknowledgement, at 100%, 125% and 150%. The tick's figure needs Win2D, which this tool does not carry, so a filled
/// square of the tick's size stands in for it; what is checked is that the mark is drawn at all once the card has been a
/// plain tip. Exits 2 when it is not.
/// Usage: TextCaptures --composition &lt;output folder&gt;
/// </summary>
internal static partial class Program
{
    private static int CompositionCaptures(string dir)
    {
        Directory.CreateDirectory(dir);
        // The compositor lives on a thread of its own with a dispatcher queue, which pumps its own messages; this thread
        // lays out the text, as the island's does, and waits for the frames.
        var controller = DispatcherQueueController.CreateOnDedicatedThread();
        int fails = 0;
        try
        {
            foreach (double scale in Scales)
            {
                fails += AcknowledgedAfterTip(controller.DispatcherQueue, dir, scale) ? 0 : 1;
            }
        }
        finally
        {
            _ = controller.ShutdownQueueAsync();
        }

        return fails == 0 ? 0 : 2;
    }

    // A "Copied" on a strip-sized stretch of panel, after the same card showed a tip there.
    private static bool AcknowledgedAfterTip(DispatcherQueue queue, string dir, double scale)
    {
        var island = new RectangleF(-140, 0, 280, 64);
        var target = new RectangleF(-60, 8, 120, 24);
        var (tipCard, tipMark, _) = StateLook.Tip("Copy the path", tick: false, target, island);
        var (ackCard, ackMark, _) = StateLook.Tip("Copied", tick: true, target, island);
        int width = (int)Math.Ceiling(island.Width * scale), height = (int)Math.Ceiling(island.Height * scale);

        var (card, capture) = On(queue, () =>
        {
            var compositor = new Compositor();
            var root = compositor.CreateContainerVisual();
            root.Size = new Vector2(width, height);
            var fill = compositor.CreateSpriteVisual();
            fill.Size = root.Size;
            fill.Brush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 0, 0, 0));
            root.Children.InsertAtTop(fill);
            var dips = compositor.CreateContainerVisual();
            dips.Scale = new Vector3((float)scale, (float)scale, 1);
            dips.Offset = new Vector3((float)(-island.X * scale), 0, 0);
            root.Children.InsertAtTop(dips);
            uint c = StateLook.Accent;
            var accent = compositor.CreateColorBrush(Windows.UI.Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c));
            var stand = compositor.CreateRoundedRectangleGeometry();
            stand.Size = new Vector2(StateLook.TickSize);
            stand.CornerRadius = new Vector2(2);
            var card = new TipCard(compositor, accent, stand);
            dips.Children.InsertAtTop(card.Visual);
            return (card, new VisualFrames(root, new Windows.Graphics.SizeInt32 { Width = width, Height = height }));
        });

        using (capture)
        {
            // The empty tree's frame first, so the next one is the tip's and the card has been drawn as a tip.
            capture.First.GetAwaiter().GetResult().Dispose();
            var shown = capture.Next();
            On(queue, () => card.Show(tipCard, tipMark, scale));
            shown.GetAwaiter().GetResult().Dispose();

            var acknowledged = capture.Next();
            On(queue, () => card.Show(ackCard, ackMark, scale));
            using var frame = acknowledged.GetAwaiter().GetResult();
            var pixels = new byte[width * height * 4];
            frame.CopyToBuffer(pixels.AsBuffer());
            Canvas.Png(Path.Combine(dir, $"{scale * 100:0}-tip-acknowledged-composition.png"), pixels, width, height);

            // The accent is the one colour on the card that is near it: count its pixels inside the mark's box.
            int blue = 0;
            uint accent = StateLook.Accent;
            var box = Rectangle.Round(new RectangleF((float)((ackMark.X - island.X) * scale), (float)(ackMark.Y * scale), (float)(ackMark.Width * scale), (float)(ackMark.Height * scale)));
            for (int y = Math.Max(0, box.Top); y < Math.Min(height, box.Bottom); y++)
            {
                for (int x = Math.Max(0, box.Left); x < Math.Min(width, box.Right); x++)
                {
                    int i = (y * width + x) * 4;
                    int off = Math.Abs(pixels[i] - (int)(accent & 0xFF)) + Math.Abs(pixels[i + 1] - (int)(accent >> 8 & 0xFF)) + Math.Abs(pixels[i + 2] - (int)(accent >> 16 & 0xFF));
                    blue += off < 96 ? 1 : 0;
                }
            }

            bool drawn = blue > box.Width * box.Height / 2;
            Console.WriteLine($"{scale * 100:0}%: an acknowledgement after a tip draws its mark: {drawn} ({blue} of {box.Width * box.Height} pixels in the accent)");
            return drawn;
        }
    }

    // Runs on the compositor's thread and waits for the result.
    private static T On<T>(DispatcherQueue queue, Func<T> work)
    {
        var result = new TaskCompletionSource<T>();
        queue.TryEnqueue(() =>
        {
            try
            {
                result.SetResult(work());
            }
            catch (Exception e)
            {
                result.SetException(e);
            }
        });
        return result.Task.GetAwaiter().GetResult();
    }

    private static void On(DispatcherQueue queue, Action work) => On(queue, () =>
    {
        work();
        return 0;
    });

    /// <summary>Frames of a visual, drawn by the compositor off screen, on a software Direct3D device.</summary>
    private sealed class VisualFrames : IDisposable
    {
        private readonly Direct3D11CaptureFramePool pool;
        private readonly GraphicsCaptureSession session;
        private TaskCompletionSource<SoftwareBitmap>? waiting;

        public VisualFrames(Visual visual, Windows.Graphics.SizeInt32 size)
        {
            pool = Direct3D11CaptureFramePool.CreateFreeThreaded(Device(), DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, size);
            pool.FrameArrived += (sender, _) =>
            {
                using var frame = sender.TryGetNextFrame();
                if (frame is not null && Interlocked.Exchange(ref waiting, null) is { } next)
                {
                    next.TrySetResult(SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface, BitmapAlphaMode.Premultiplied).AsTask().GetAwaiter().GetResult());
                }
            };
            session = pool.CreateCaptureSession(GraphicsCaptureItem.CreateFromVisual(visual));
            First = Next();
            session.StartCapture();
        }

        /// <summary>The first frame, of the tree as it was when the capture started.</summary>
        public Task<SoftwareBitmap> First { get; }

        /// <summary>The frame after the change the caller makes next; asked for before the change, so it cannot be missed.</summary>
        public Task<SoftwareBitmap> Next()
        {
            var next = new TaskCompletionSource<SoftwareBitmap>(TaskCreationOptions.RunContinuationsAsynchronously);
            waiting = next;
            return next.Task.WaitAsync(TimeSpan.FromSeconds(5));
        }

        public void Dispose()
        {
            session.Dispose();
            pool.Dispose();
        }

        private static unsafe IDirect3DDevice Device()
        {
            ID3D11Device* d3d;
            PInvoke.D3D11CreateDevice(null, D3D_DRIVER_TYPE.D3D_DRIVER_TYPE_WARP, HMODULE.Null, D3D11_CREATE_DEVICE_FLAG.D3D11_CREATE_DEVICE_BGRA_SUPPORT,
                null, 0, PInvoke.D3D11_SDK_VERSION, &d3d, null, null).ThrowOnFailure();
            IDXGIDevice* dxgi = null;
            IInspectable* inspectable = null;
            try
            {
                d3d->QueryInterface(out dxgi).ThrowOnFailure();
                PInvoke.CreateDirect3D11DeviceFromDXGIDevice(dxgi, &inspectable).ThrowOnFailure();
                return WinRT.MarshalInterface<IDirect3DDevice>.FromAbi((nint)inspectable);
            }
            finally
            {
                if (inspectable is not null)
                {
                    inspectable->Release();
                }

                if (dxgi is not null)
                {
                    dxgi->Release();
                }

                d3d->Release();
            }
        }
    }
}
