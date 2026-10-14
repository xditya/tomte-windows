using System.ComponentModel;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas.Geometry;
using Tomte.Core;
using Tomte.Core.Text;
using Windows.UI;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Shell;

/// <summary>
/// The circle-to-ask overlay on one monitor: created on the trigger, destroyed on the way out, so nothing
/// exists while idle. Unlike the island it takes focus (Esc has to reach it) but stays a tool window,
/// topmost, out of the taskbar and Alt+Tab. It is excluded from capture, so the dim never shows in the
/// circled image. Drawn on the island's compositor: a dim with the lasso cut out, the stroke, the
/// bounding box, and at the top until the first press a line that says what to do (<see cref="LassoHint"/>).
/// <see cref="Result"/> is the box in the monitor's pixels, or null for Esc, a right click, focus loss or a
/// display change.
/// </summary>
internal sealed unsafe class CaptureOverlay : IDisposable
{
    private const string ClassName = "TomteCapture";
    private static readonly Color Dim = Color.FromArgb(89, 0, 0, 0);
    private static CaptureOverlay? current;
    private static bool registered;

    private readonly Compositor compositor;
    private readonly Windows.UI.Composition.Desktop.DesktopWindowTarget target;
    private readonly ContainerVisual root, hint;
    private readonly TextVisual? line;
    private readonly ShapeVisual visual;
    private readonly CompositionPathGeometry dimGeometry;
    private readonly CompositionPathGeometry strokeGeometry;
    private readonly CompositionRectangleGeometry boxGeometry;
    private readonly CompositionSpriteShape box;
    private readonly TaskCompletionSource<PixelRect?> result = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly int width;
    private readonly int height;
    private HWND hwnd;

    public CaptureOverlay(Compositor compositor, System.Drawing.Point cursor, TextRenderer? text)
    {
        this.compositor = compositor;
        Monitor = PInvoke.MonitorFromPoint(cursor, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        if (!PInvoke.GetMonitorInfo(Monitor, ref info))
        {
            throw new Win32Exception();
        }

        // Per-monitor aware: rcMonitor and the client coordinates are this monitor's physical pixels.
        var rc = info.rcMonitor;
        (width, height) = (rc.right - rc.left, rc.bottom - rc.top);
        double scale = PInvoke.GetDpiForMonitor(Monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpi, out _).Succeeded ? dpi / 96.0 : 1;
        Lasso = new Lasso(width, height, scale);
        Bounds = new PixelRect(rc.left, rc.top, width, height);

        var hInstance = new HINSTANCE(Marshal.GetHINSTANCE(typeof(CaptureOverlay).Module));
        fixed (char* className = ClassName)
        {
            if (!registered)
            {
                var windowClass = new WNDCLASSEXW
                {
                    cbSize = (uint)sizeof(WNDCLASSEXW),
                    lpfnWndProc = &HandleMessage,
                    hInstance = hInstance,
                    lpszClassName = className,
                    hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_CROSS),
                };
                if (PInvoke.RegisterClassEx(in windowClass) == 0)
                {
                    throw new Win32Exception();
                }

                registered = true;
            }

            current = this;
            hwnd = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP,
                className,
                null,
                WINDOW_STYLE.WS_POPUP,
                rc.left,
                rc.top,
                width,
                height,
                HWND.Null,
                HMENU.Null,
                hInstance,
                null);
            if (hwnd == HWND.Null)
            {
                current = null;
                throw new Win32Exception();
            }
        }

        // The window exists from here on: anything that throws must not leave it, or current, behind.
        try
        {
            BOOL disableTransitions = true;
            PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_TRANSITIONS_FORCEDISABLED, &disableTransitions, (uint)sizeof(BOOL));
            if (!PInvoke.SetWindowDisplayAffinity(hwnd, WINDOW_DISPLAY_AFFINITY.WDA_EXCLUDEFROMCAPTURE))
            {
                // Without the exclusion the dim lands in the crop; no overlay beats a wrong image.
                throw new Win32Exception();
            }

            target = IslandWindow.CreateDesktopWindowTarget(compositor, hwnd);
            visual = compositor.CreateShapeVisual();
            visual.Size = new Vector2(width, height);
            dimGeometry = compositor.CreatePathGeometry();
            var dim = compositor.CreateSpriteShape(dimGeometry);
            dim.FillBrush = compositor.CreateColorBrush(Dim);
            strokeGeometry = compositor.CreatePathGeometry();
            var stroke = compositor.CreateSpriteShape(strokeGeometry);
            stroke.StrokeBrush = compositor.CreateColorBrush(Color.FromArgb(255, 255, 255, 255));
            stroke.StrokeThickness = (float)(2 * scale);
            stroke.StrokeLineJoin = CompositionStrokeLineJoin.Round;
            boxGeometry = compositor.CreateRectangleGeometry();
            box = compositor.CreateSpriteShape(boxGeometry);
            box.StrokeBrush = compositor.CreateColorBrush(Color.FromArgb(128, 255, 255, 255));
            box.StrokeThickness = (float)scale;
            box.StrokeDashArray.Add(4);
            box.StrokeDashArray.Add(4);
            visual.Shapes.Add(dim);
            visual.Shapes.Add(box);
            visual.Shapes.Add(stroke);
            root = compositor.CreateContainerVisual();
            root.Children.InsertAtBottom(visual);
            hint = Hint(text, (float)scale, out line);
            root.Children.InsertAtTop(hint);
            target.Root = root;
            Redraw();

            PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_SHOW);
            Activated = PInvoke.SetForegroundWindow(hwnd);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    public HMONITOR Monitor { get; }

    /// <summary>The monitor in virtual-screen pixels.</summary>
    public PixelRect Bounds { get; }

    public Lasso Lasso { get; }

    /// <summary>False when Windows refused the foreground; then Esc cannot arrive and a right click cancels.</summary>
    public bool Activated { get; }

    public Task<PixelRect?> Result => result.Task;

    public void Dispose()
    {
        result.TrySetResult(null);
        if (current == this)
        {
            current = null;
        }

        // Cleared first: destroying the active window sends WM_ACTIVATE, which must not land here again.
        var window = hwnd;
        hwnd = HWND.Null;
        if (window != HWND.Null)
        {
            PInvoke.DestroyWindow(window);
            // Null when the constructor failed before building them.
            target?.Dispose();
            visual?.Dispose();
            line?.Dispose();
            hint?.Dispose();
            root?.Dispose();
        }
    }

    /// <summary>
    /// The line at the top and its dark capsule, in DIPs under a container that carries this monitor's scale. Without
    /// the island's text renderer there is no hint.
    /// the line is drawn at the island's scale, so on a monitor with another scale it is stretched a little;
    /// give a text visual a scale of its own if mixed-scale setups show it.
    /// </summary>
    private ContainerVisual Hint(TextRenderer? text, float scale, out TextVisual? words)
    {
        var hint = compositor.CreateContainerVisual();
        hint.Scale = new Vector3(scale, scale, 1);
        words = null;
        if (text is null)
        {
            return hint;
        }

        float monitor = width / scale;
        var card = LassoHint.Card(monitor, TextSlot.Measure(LassoHint.Text, LassoHint.Style, monitor));
        var shape = compositor.CreateRoundedRectangleGeometry();
        shape.Offset = new Vector2(card.X, card.Y);
        shape.Size = new Vector2(card.Width, card.Height);
        shape.CornerRadius = new Vector2(card.Height / 2);
        var capsule = compositor.CreateSpriteShape(shape);
        // The island's own dark and its 10% hairline.
        capsule.FillBrush = compositor.CreateColorBrush(Color.FromArgb(255, 0x10, 0x10, 0x12));
        capsule.StrokeBrush = compositor.CreateColorBrush(Color.FromArgb(26, 255, 255, 255));
        capsule.StrokeThickness = 1 / scale;
        var back = compositor.CreateShapeVisual();
        back.Size = new Vector2(monitor, card.Bottom + 1);
        back.Shapes.Add(capsule);
        hint.Children.InsertAtTop(back);
        words = text.Create(hint);
        words.Show(LassoHint.Text, LassoHint.Style, LassoHint.Line(card));
        return hint;
    }

    private void Finish(PixelRect? box)
    {
        result.TrySetResult(box);
        Dispose();
    }

    // the whole stroke is rebuilt on every move; a lasso is a few hundred points, so that holds.
    private void Redraw()
    {
        var points = Lasso.Points;
        using (var dimPath = new CanvasPathBuilder(null))
        {
            dimPath.SetFilledRegionDetermination(CanvasFilledRegionDetermination.Alternate);
            dimPath.BeginFigure(0, 0);
            dimPath.AddLine(width, 0);
            dimPath.AddLine(width, height);
            dimPath.AddLine(0, height);
            dimPath.EndFigure(CanvasFigureLoop.Closed);
            if (points.Count > 2)
            {
                AddStroke(dimPath, points);
            }

            using var g = CanvasGeometry.CreatePath(dimPath);
            dimGeometry.Path = new CompositionPath(g);
        }

        if (points.Count > 1)
        {
            using var strokePath = new CanvasPathBuilder(null);
            AddStroke(strokePath, points);
            using var g = CanvasGeometry.CreatePath(strokePath);
            strokeGeometry.Path = new CompositionPath(g);
            var b = Lasso.Box;
            boxGeometry.Offset = new Vector2(b.X, b.Y);
            boxGeometry.Size = new Vector2(b.Width, b.Height);
            box.StrokeThickness = (float)Lasso.Scale;
        }
        else
        {
            strokeGeometry.Path = null;
            box.StrokeThickness = 0;
        }
    }

    private static void AddStroke(CanvasPathBuilder builder, IReadOnlyList<(int X, int Y)> points)
    {
        builder.BeginFigure(points[0].X + 0.5f, points[0].Y + 0.5f);
        for (int i = 1; i < points.Count; i++)
        {
            builder.AddLine(points[i].X + 0.5f, points[i].Y + 0.5f);
        }

        builder.EndFigure(CanvasFigureLoop.Closed);
    }

    private static (int X, int Y) Point(LPARAM lParam) => ((short)(lParam.Value & 0xFFFF), (short)((lParam.Value >> 16) & 0xFFFF));

    private void OnMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case PInvoke.WM_LBUTTONDOWN:
                // The hint has said its piece once a drag starts.
                hint.StartAnimation("Opacity", PanelHost.Fade(compositor, 0, PanelHost.FadeOutMs));
                PInvoke.SetCapture(hwnd);
                Lasso.Down(Point(lParam).X, Point(lParam).Y);
                Redraw();
                break;
            case PInvoke.WM_MOUSEMOVE:
                if (Lasso.Move(Point(lParam).X, Point(lParam).Y))
                {
                    Redraw();
                }

                break;
            case PInvoke.WM_LBUTTONUP:
                PInvoke.ReleaseCapture();
                if (Lasso.Up() is PixelRect done)
                {
                    Finish(done);
                }
                else
                {
                    // Too small to be a lasso: clear it and let the user draw again.
                    Redraw();
                }

                break;
            case PInvoke.WM_KEYDOWN when wParam.Value == (nuint)VIRTUAL_KEY.VK_ESCAPE:
            case PInvoke.WM_RBUTTONUP:
            case PInvoke.WM_CLOSE:
            case PInvoke.WM_DISPLAYCHANGE:
                // A close from outside must complete the result like any cancel. A resolution or scale change
                // under the overlay leaves its pixel mapping stale.
                Finish(null);
                break;
            case PInvoke.WM_ACTIVATE when (wParam.Value & 0xFFFF) == PInvoke.WA_INACTIVE:
                // Alt+Tab, the Start menu, a UAC prompt or the lock screen: the overlay never lingers behind them.
                Finish(null);
                break;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT HandleMessage(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (current is { } overlay && overlay.hwnd == window)
            {
                overlay.OnMessage(message, wParam, lParam);
            }
        }
        catch (Exception e)
        {
            // An exception must not unwind into user32; cancel instead.
            System.Diagnostics.Debug.WriteLine($"capture overlay: {e}");
            current?.Finish(null);
        }

        return PInvoke.DefWindowProc(window, message, wParam, lParam);
    }
}
