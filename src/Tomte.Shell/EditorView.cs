using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Tomte.Core;
using Tomte.Core.Text;
using Windows.Graphics;
using Windows.Graphics.DirectX;
using Windows.System;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.System.WinRT.Composition;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Shell;

/// <summary>
/// A text box on the island: the text services drawn into a composition surface, with the caret as a sprite of its
/// own above it that the compositor blinks, since the services never draw a caret. Repaints are gathered and drawn at
/// most once per turn of the message loop, only the part that changed. The box sits in DIPs in a host container under
/// island space and lands on whole pixels. Dispose, when its panel hides, releases the services, the surface and the
/// caret; nothing of it runs after that.
/// </summary>
internal sealed unsafe class EditorView : IDisposable
{
    private readonly DispatcherQueue queue;
    private readonly ContainerVisual dips;
    private readonly CompositionSurfaceBrush brush;
    private readonly SpriteVisual caret;
    private readonly ScalarKeyFrameAnimation? blink;
    private readonly PaintGate gate = new();
    private readonly int caretWidth;
    private CompositionDrawingSurface? surface;
    private CompositionGraphicsDevice? graphics;
    private RectangleF box;
    private double scale;
    private int width, height;
    private bool queued, idleQueued, caretMoved, disposed;

    /// <param name="dips">Island space: the container whose children are laid out in DIPs; offsets are summed up to it to land on whole pixels.</param>
    /// <param name="window">The window that has keyboard focus while the box is typed in.</param>
    public EditorView(Compositor compositor, DispatcherQueue queue, ContainerVisual host, ContainerVisual dips, HWND window, bool multiline, float fontDips)
    {
        this.queue = queue;
        this.dips = dips;
        Services = new TextServices(window, multiline, fontDips);
        brush = compositor.CreateSurfaceBrush();
        brush.Stretch = CompositionStretch.Fill;
        Visual = compositor.CreateSpriteVisual();
        Visual.Brush = brush;
        caret = compositor.CreateSpriteVisual();
        caret.Brush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(255, 255, 255, 255));
        caret.IsVisible = false;
        Visual.Children.InsertAtTop(caret);
        host.Children.InsertAtTop(Visual);

        uint width = 1, timeout = 5000;
        PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETCARETWIDTH, 0, &width, 0);
        PInvoke.SystemParametersInfo(SYSTEM_PARAMETERS_INFO_ACTION.SPI_GETCARETTIMEOUT, 0, &timeout, 0);
        caretWidth = (int)Math.Max(1, width);
        if (CaretBlink.Plan(PInvoke.GetCaretBlinkTime(), timeout) is { } plan)
        {
            // On for the first half of each cycle, off for the second, on at the end: the caret is solid once the
            // timeout has run out, with nothing left animating.
            blink = compositor.CreateScalarKeyFrameAnimation();
            var step = compositor.CreateStepEasingFunction();
            blink.InsertKeyFrame(0f, 1f, step);
            blink.InsertKeyFrame(0.5f, 0f, step);
            blink.InsertKeyFrame(1f, 1f, step);
            blink.Duration = plan.Cycle;
            blink.IterationBehavior = plan.Iterations == 0 ? AnimationIterationBehavior.Forever : AnimationIterationBehavior.Count;
            blink.IterationCount = Math.Max(1, plan.Iterations);
        }

        Services.Invalidated += Invalidate;
        Services.CaretChanged += () =>
        {
            caretMoved = true;
            Queue();
        };
        Services.MapToScreen = MapToScreen;
    }

    public SpriteVisual Visual { get; }

    public TextServices Services { get; }

    /// <summary>The box in its host, in DIPs.</summary>
    public RectangleF Box => box;

    public bool Focused { get; private set; }

    /// <summary>The height the text asks for, in DIPs; 0 before the first layout.</summary>
    public float ContentHeight => scale > 0 ? (float)(Services.ContentHeight / scale) : 0;

    /// <summary>Island space to screen pixels and back, for input method windows. Unset offscreen.</summary>
    public Func<Vector2, Point>? IslandToScreen { get; set; }

    public Func<Point, Vector2>? ScreenToIsland { get; set; }

    /// <summary>Paints done, for the offscreen check of the once-per-turn rule.</summary>
    public int Paints { get; private set; }

    /// <summary>A paint that failed; the box then stays as it was and the island keeps working.</summary>
    public event Action<Exception>? Failed;

    /// <summary>Raised once, as the box is disposed.</summary>
    public event Action? Disposing;

    /// <summary>The box took the keys or let them go (<see cref="Focus"/>, <see cref="Blur"/>).</summary>
    public event Action? FocusChanged;

    /// <summary>Sees every key, character and input method message first; true swallows it (the palette's Enter).</summary>
    public Func<uint, nuint, nint, bool>? BeforeKey { get; set; }

    /// <summary>Esc while this box has the keys: true when something on the island used it (the palette's open question), so the island stays.</summary>
    public Func<bool>? Escape { get; set; }

    /// <summary>Runs first in each paint; what it changes in the services is drawn by that same paint (the note's styling).</summary>
    public Action? BeforePaint { get; set; }

    /// <summary>
    /// A paint even with nothing invalidated, so <see cref="BeforePaint"/> runs: on the next turn, or when
    /// <paramref name="idle"/> at low priority, after input and the dispatcher's other work.
    /// </summary>
    public void Repaint(bool idle = false)
    {
        if (!idle)
        {
            Queue();
        }
        else if (!idleQueued && !disposed)
        {
            idleQueued = true;
            queue.TryEnqueue(DispatcherQueuePriority.Low, () =>
            {
                idleQueued = false;
                Paint(idle: true);
            });
        }
    }

    /// <summary>The graphics device the surface comes from, once there is one; the whole box is drawn then.</summary>
    public void Use(CompositionGraphicsDevice? device)
    {
        if (ReferenceEquals(device, graphics))
        {
            return;
        }

        graphics = device;
        surface?.Dispose();
        surface = null;
        brush.Surface = null;
        MakeSurface();
    }

    /// <summary>Put the box at a rectangle of its host, in DIPs, at the island's scale. A new size or scale lays the text out again.</summary>
    public void Place(RectangleF at, double newScale)
    {
        box = at;
        var host = HostOffset();
        Visual.Offset = new Vector3(PixelGrid.Snap(host.X, at.X, newScale), PixelGrid.Snap(host.Y, at.Y, newScale), 0);
        int w = Math.Max(1, PixelGrid.Cover(at.Width, newScale)), h = Math.Max(1, PixelGrid.Cover(at.Height, newScale));
        Visual.Size = new Vector2((float)(w / newScale), (float)(h / newScale));
        if (w == width && h == height && newScale == scale)
        {
            return;
        }

        (width, height, scale) = (w, h, newScale);
        Services.Resize(w, h, EditorScale.Zoom(newScale, TextServices.SystemDpi));
        Services.Activate();
        if (surface is not null)
        {
            surface.Resize(new SizeInt32 { Width = w, Height = h });
        }
        else
        {
            MakeSurface();
        }

        Invalidate(new PixelRect(0, 0, w, h));
        caretMoved = true;
    }

    /// <summary>The caret shows and blinks, keys typed go in. The window must already have keyboard focus.</summary>
    public void Focus()
    {
        Focused = true;
        Services.Focus();
        caretMoved = true;
        Queue();
        FocusChanged?.Invoke();
    }

    public void Blur()
    {
        Focused = false;
        Services.Blur();
        HideCaret();
        FocusChanged?.Invoke();
    }

    /// <summary>Enter hands the text to <paramref name="send"/> and adds nothing; Shift+Enter is a new line. The palette's prompt.</summary>
    public void SendOnEnter(Action<string> send)
    {
        var keys = new PromptKeys();
        BeforeKey = (message, wParam, lParam) =>
        {
            bool shift = PInvoke.GetKeyState((int)Windows.Win32.UI.Input.KeyboardAndMouse.VIRTUAL_KEY.VK_SHIFT) < 0;
            if (keys.Sends(message, wParam, shift, out bool swallow, lParam))
            {
                send(Services.Text);
            }

            return swallow;
        };
    }

    /// <summary>A key, character or input method message for the services; false when the window's default should have it.</summary>
    public bool Key(uint message, nuint wParam, nint lParam, out LRESULT result) => Services.TrySend(message, wParam, lParam, out result);

    /// <summary>A mouse message at a point in island space; the services place the caret or select.</summary>
    public void Mouse(uint message, Vector2 island, nuint wParam)
    {
        var local = (island - IslandOrigin()) * (float)scale;
        int x = (int)MathF.Floor(local.X), y = (int)MathF.Floor(local.Y);
        Services.Send(message, wParam, (nint)(((y & 0xFFFF) << 16) | (x & 0xFFFF)));
    }

    /// <summary>Whether a point in island space is on the box.</summary>
    public bool Contains(Vector2 island)
    {
        var at = island - IslandOrigin();
        return at.X >= 0 && at.Y >= 0 && at.X < Visual.Size.X && at.Y < Visual.Size.Y;
    }

    /// <summary>The wheel over the box scrolls its text, as an edit box does.</summary>
    public void Wheel(int delta) => Services.Send(PInvoke.WM_MOUSEWHEEL, (nuint)((delta & 0xFFFF) << 16), 0);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Disposing?.Invoke();
        HideCaret();
        Services.Dispose();
        Visual.Parent?.Children.Remove(Visual);
        caret.Dispose();
        Visual.Dispose();
        brush.Dispose();
        surface?.Dispose();
        surface = null;
        blink?.Dispose();
    }

    private void MakeSurface()
    {
        if (graphics is null || width == 0 || surface is not null)
        {
            return;
        }

        surface = graphics.CreateDrawingSurface2(new SizeInt32 { Width = width, Height = height }, DirectXPixelFormat.B8G8R8A8UIntNormalized, DirectXAlphaMode.Premultiplied);
        brush.Surface = surface;
        Invalidate(new PixelRect(0, 0, width, height));
    }

    private void Invalidate(PixelRect rect)
    {
        if (gate.Invalidate(rect))
        {
            Queue();
        }
    }

    // One paint per turn of the loop, whatever the services invalidated or moved in the turns before it.
    private void Queue()
    {
        if (!queued && !disposed)
        {
            queued = true;
            queue.TryEnqueue(() => Paint(idle: false));
        }
    }

    private void Paint(bool idle)
    {
        if (disposed)
        {
            queued &= idle;
            return;
        }

        // While it runs a paint counts as queued, so what it invalidates joins this paint instead of queueing another.
        bool wasQueued = queued;
        queued = true;
        try
        {
            BeforePaint?.Invoke();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Failed?.Invoke(e);
        }

        queued = idle && wasQueued;
        if (caretMoved)
        {
            caretMoved = false;
            PlaceCaret();
        }

        if (surface is null || gate.Take(width, height) is not { } update)
        {
            return;
        }

        try
        {
            Draw(surface, update);
            Paints++;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Failed?.Invoke(e);
        }
    }

    private void Draw(CompositionDrawingSurface target, PixelRect update)
    {
        nint unknown = WinRT.MarshalInspectable<object>.FromManaged(target);
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
                var rect = new RECT { left = update.X, top = update.Y, right = update.X + update.Width, bottom = update.Y + update.Height };
                interop->BeginDraw(&rect, &dcIid, (void**)&dc, &offset);
                try
                {
                    // BeginDraw's offset is where the update rectangle starts in the atlas; the box starts that much before it.
                    Services.Draw(dc, new Point(offset.X - update.X, offset.Y - update.Y), update);
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

    // The caret where the services put it, the user's text cursor thickness wide, solid while typing and blinking
    // from the last key until the system's caret timeout.
    private void PlaceCaret()
    {
        var (x, y, h, shown) = Services.Caret;
        if (!Focused || !shown || h <= 0 || scale <= 0)
        {
            HideCaret();
            return;
        }

        int w = Math.Max(1, (int)Math.Round(caretWidth * scale));
        caret.Offset = new Vector3((float)(x / scale), (float)(y / scale), 0);
        caret.Size = new Vector2((float)(w / scale), (float)(h / scale));
        caret.IsVisible = true;
        caret.StopAnimation("Opacity");
        caret.Opacity = 1;
        if (blink is not null)
        {
            caret.StartAnimation("Opacity", blink);
        }
    }

    private void HideCaret()
    {
        caret.StopAnimation("Opacity");
        caret.IsVisible = false;
    }

    /// <summary>The box's top-left in island space: its own offset and every container's up to island space.</summary>
    private Vector2 IslandOrigin() => HostOffset() + new Vector2(Visual.Offset.X, Visual.Offset.Y);

    private Vector2 HostOffset()
    {
        var sum = Vector2.Zero;
        for (var parent = Visual.Parent; parent is not null && !ReferenceEquals(parent, dips); parent = parent.Parent)
        {
            sum += new Vector2(parent.Offset.X, parent.Offset.Y);
        }

        return sum;
    }

    // Client pixels of the box to screen pixels and back, through island space.
    private Point MapToScreen(Point p, bool toScreen)
    {
        if (toScreen)
        {
            return IslandToScreen is { } to ? to(IslandOrigin() + new Vector2(p.X, p.Y) / (float)scale) : p;
        }

        if (ScreenToIsland is not { } from)
        {
            return p;
        }

        var local = (from(p) - IslandOrigin()) * (float)scale;
        return new Point((int)MathF.Floor(local.X), (int)MathF.Floor(local.Y));
    }
}
