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

/// <summary>What the capture overlay draws: circle to ask's lasso, Copy text from screen's rectangle, or the colour picker's magnifier.</summary>
internal enum CaptureMode
{
    Lasso,
    Rectangle,
    Colour,
}

/// <summary>
/// The capture overlay on one monitor, for circle to ask and, drawing a rectangle, for Copy text from screen: created on the trigger, destroyed on the way out, so nothing
/// exists while idle. Unlike the island it takes focus (Esc has to reach it) but stays a tool window,
/// topmost, out of the taskbar and Alt+Tab. It is excluded from capture, so the dim never shows in the
/// circled image. Drawn on the island's compositor: a dim with the lasso cut out, the stroke, the
/// bounding box, and at the top until the first press a line that says what to do (<see cref="LassoHint"/>).
/// <see cref="Result"/> is the box in the monitor's pixels, or null for Esc, a right click, focus loss or a
/// display change. For the colour picker it grabs the monitor once as it opens and shows a magnifier by the pointer
/// that reads that grab, never the live screen; a click, or Enter, picks the pixel under the pointer as
/// <see cref="Picked"/>, and the arrow keys move the pointer a pixel at a time.
/// </summary>
internal sealed unsafe class CaptureOverlay : IDisposable
{
    private const string ClassName = "TomteCapture";
    private static Color Dim => ShelfColors.Of(Theme.Scrim);
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
    private readonly CaptureMode mode;
    private HWND hwnd;

    // The colour picker's grab of the monitor, its magnifier and where the pointer is, in the monitor's pixels.
    private byte[]? frame;
    private int frameWidth;
    private ContainerVisual? loupe;
    private CompositionColorBrush[] cells = [];
    private TextVisual? hex;
    private (int X, int Y) pointer;

    public CaptureOverlay(Compositor compositor, System.Drawing.Point cursor, TextRenderer? text, CaptureMode mode = CaptureMode.Lasso)
    {
        this.compositor = compositor;
        this.mode = mode;
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
        Lasso = new Lasso(width, height, scale, mode == CaptureMode.Rectangle);
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
            // The accent, dotted with round caps, over the dim.
            stroke.StrokeBrush = compositor.CreateColorBrush(ShelfColors.Of(Theme.Accent));
            stroke.StrokeThickness = (float)(Lasso.StrokeDips * scale);
            stroke.StrokeLineJoin = CompositionStrokeLineJoin.Round;
            stroke.StrokeStartCap = stroke.StrokeEndCap = stroke.StrokeDashCap = CompositionStrokeCap.Round;
            stroke.StrokeDashArray.Add(Lasso.Dashes.On);
            stroke.StrokeDashArray.Add(Lasso.Dashes.Off);
            boxGeometry = compositor.CreateRectangleGeometry();
            box = compositor.CreateSpriteShape(boxGeometry);
            box.StrokeBrush = compositor.CreateColorBrush(ShelfColors.Of(Theme.WithOpacity(Theme.Ink, 0.5f)));
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
            if (mode == CaptureMode.Colour)
            {
                pointer = (Math.Clamp(cursor.X - rc.left, 0, width - 1), Math.Clamp(cursor.Y - rc.top, 0, height - 1));
                root.Children.InsertAtTop(loupe = Magnifier(text, scale));
            }

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

        if (mode == CaptureMode.Colour)
        {
            Grab();
        }
    }

    public HMONITOR Monitor { get; }

    /// <summary>The monitor in virtual-screen pixels.</summary>
    public PixelRect Bounds { get; }

    public Lasso Lasso { get; }

    /// <summary>False when Windows refused the foreground; then Esc cannot arrive and a right click cancels.</summary>
    public bool Activated { get; }

    public Task<PixelRect?> Result => result.Task;

    /// <summary>The colour picker's pick: the pixel's colour in the grab. Null until a pick, and in the other modes.</summary>
    public PickedColour? Picked { get; private set; }

    public void Dispose()
    {
        result.TrySetResult(null);
        // The colour picker's grab is the size of the monitor; it goes with the overlay.
        frame = null;
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
            hex?.Dispose();
            loupe?.Dispose();
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
        string says = mode switch
        {
            CaptureMode.Rectangle => LassoHint.TextCapture,
            CaptureMode.Colour => LassoHint.ColourPick,
            _ => LassoHint.Text,
        };
        var card = LassoHint.Card(monitor, TextSlot.Measure(says, LassoHint.Style, monitor));
        var shape = compositor.CreateRoundedRectangleGeometry();
        shape.Offset = new Vector2(card.X, card.Y);
        shape.Size = new Vector2(card.Width, card.Height);
        shape.CornerRadius = new Vector2(card.Height / 2);
        var capsule = compositor.CreateSpriteShape(shape);
        // The island's own fill and hairline.
        capsule.FillBrush = compositor.CreateColorBrush(ShelfColors.Of(Theme.Island));
        capsule.StrokeBrush = compositor.CreateColorBrush(ShelfColors.Of(Theme.LineOver));
        capsule.StrokeThickness = 1 / scale;
        var back = compositor.CreateShapeVisual();
        back.Size = new Vector2(monitor, card.Bottom + 1);
        back.Shapes.Add(capsule);
        hint.Children.InsertAtTop(back);
        words = text.Create(hint);
        words.Show(says, LassoHint.Style, LassoHint.Line(card));
        return hint;
    }

    /// <summary>
    /// The magnifier, hidden until the grab arrives: the zoomed pixels as blocks under a rounded clip with the centre
    /// one ringed in the accent, and under them the centre pixel's hex on the hint's capsule. In the monitor's pixels,
    /// the label in DIPs under a container that carries the monitor's scale.
    /// </summary>
    private ContainerVisual Magnifier(TextRenderer? text, double scale)
    {
        int cell = Loupe.Cell(scale), side = Loupe.Cells * cell;
        var (zoom, label) = Loupe.Place(0, 0, int.MaxValue / 4, int.MaxValue / 4, scale);
        var magnifier = compositor.CreateContainerVisual();
        magnifier.Opacity = 0;
        var blocks = compositor.CreateShapeVisual();
        blocks.Size = new Vector2(side, side);
        var corner = new Vector2((float)(Loupe.CornerDips * scale));
        var outline = compositor.CreateRoundedRectangleGeometry();
        outline.Size = new Vector2(side, side);
        outline.CornerRadius = corner;
        blocks.Clip = compositor.CreateGeometricClip(outline);
        cells = new CompositionColorBrush[Loupe.Cells * Loupe.Cells];
        for (int i = 0; i < cells.Length; i++)
        {
            var block = compositor.CreateRectangleGeometry();
            block.Offset = new Vector2(i % Loupe.Cells * cell, i / Loupe.Cells * cell);
            block.Size = new Vector2(cell, cell);
            var shape = compositor.CreateSpriteShape(block);
            shape.FillBrush = cells[i] = compositor.CreateColorBrush(Dim);
            blocks.Shapes.Add(shape);
        }

        float ring = (float)(2 * scale);
        var centre = compositor.CreateRectangleGeometry();
        centre.Offset = new Vector2((Loupe.Cells / 2 * cell) - (ring / 2), (Loupe.Cells / 2 * cell) - (ring / 2));
        centre.Size = new Vector2(cell + ring, cell + ring);
        var mark = compositor.CreateSpriteShape(centre);
        mark.StrokeBrush = compositor.CreateColorBrush(ShelfColors.Of(Theme.Accent));
        mark.StrokeThickness = ring;
        blocks.Shapes.Add(mark);
        var edge = compositor.CreateRoundedRectangleGeometry();
        edge.Offset = new Vector2(ring / 2);
        edge.Size = new Vector2(side - ring, side - ring);
        edge.CornerRadius = corner;
        var border = compositor.CreateSpriteShape(edge);
        border.StrokeBrush = compositor.CreateColorBrush(ShelfColors.Of(Theme.Island));
        border.StrokeThickness = ring;
        blocks.Shapes.Add(border);
        magnifier.Children.InsertAtTop(blocks);

        if (text is not null)
        {
            var caption = compositor.CreateContainerVisual();
            caption.Scale = new Vector3((float)scale, (float)scale, 1);
            caption.Offset = new Vector3(0, label.Y - zoom.Y, 0);
            float w = (float)(label.Width / scale), h = (float)(label.Height / scale);
            var shape = compositor.CreateRoundedRectangleGeometry();
            shape.Size = new Vector2(w, h);
            shape.CornerRadius = new Vector2(h / 2);
            var capsule = compositor.CreateSpriteShape(shape);
            // The hint's capsule: the island's own fill and hairline.
            capsule.FillBrush = compositor.CreateColorBrush(ShelfColors.Of(Theme.Island));
            capsule.StrokeBrush = compositor.CreateColorBrush(ShelfColors.Of(Theme.LineOver));
            capsule.StrokeThickness = (float)(1 / scale);
            var back = compositor.CreateShapeVisual();
            back.Size = new Vector2(w, h);
            back.Shapes.Add(capsule);
            caption.Children.InsertAtTop(back);
            hex = text.Create(caption);
            magnifier.Children.InsertAtTop(caption);
        }

        return magnifier;
    }

    // The one grab the magnifier and the pick read from. The overlay is excluded from capture, so the grab shows what
    // is under it. A refused capture closes the overlay, which tells the caller nothing was picked.
    private void Grab() =>
        // A continuation, not an await: the class is unsafe, and async methods cannot be. Back on this thread.
        Tomte.AI.Palette.WindowCapture.CaptureMonitorPixelsAsync(Monitor, CancellationToken.None).ContinueWith(
            grab =>
            {
                if (grab.Exception is { } failed)
                {
                    Grabbed = failed.InnerException ?? failed;
                    Finish(null);
                }
                else if (hwnd == HWND.Null || grab.Result.Width != width || grab.Result.Height != height)
                {
                    // Closed meanwhile, or the monitor changed size under it.
                    Finish(null);
                }
                else
                {
                    (frame, frameWidth) = (grab.Result.Pixels, grab.Result.Width);
                    ShowLoupe();
                    loupe!.Opacity = 1;
                }
            },
            TaskScheduler.FromCurrentSynchronizationContext());

    /// <summary>Why the colour picker's grab failed, for the caller to log and explain; null when it did not.</summary>
    public Exception? Grabbed { get; private set; }

    // The magnifier at the pointer: where it sits, the colours of its blocks, and the centre pixel's hex.
    private void ShowLoupe()
    {
        if (frame is null || loupe is null)
        {
            return;
        }

        var (zoom, label) = Loupe.Place(pointer.X, pointer.Y, width, height, Lasso.Scale);
        loupe.Offset = new Vector3(zoom.X, zoom.Y, 0);
        for (int i = 0; i < cells.Length; i++)
        {
            var (x, y) = Loupe.Source(pointer.X, pointer.Y, i % Loupe.Cells, i / Loupe.Cells, width, height);
            cells[i].Color = ShelfColors.Rgb(PickedColour.At(frame, frameWidth, x, y).Rgb);
        }

        var slot = new System.Drawing.RectangleF(0, 0, (float)(label.Width / Lasso.Scale), (float)(label.Height / Lasso.Scale));
        hex?.Show(PickedColour.At(frame, frameWidth, pointer.X, pointer.Y).Hex, LassoHint.Style with { Align = TextAlign.Center, Tabular = true }, slot);
    }

    private void Pick()
    {
        if (frame is not null)
        {
            Picked = PickedColour.At(frame, frameWidth, pointer.X, pointer.Y);
            Finish(new PixelRect(pointer.X, pointer.Y, 1, 1));
        }
    }

    // An arrow key moves the pointer itself one pixel, so the magnifier and the pointer never part.
    private void Nudge(int dx, int dy)
    {
        pointer = (Math.Clamp(pointer.X + dx, 0, width - 1), Math.Clamp(pointer.Y + dy, 0, height - 1));
        PInvoke.SetCursorPos(Bounds.X + pointer.X, Bounds.Y + pointer.Y);
        ShowLoupe();
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
            // A rectangle is its own box.
            box.StrokeThickness = Lasso.Rectangle ? 0 : (float)Lasso.Scale;
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
        if (mode == CaptureMode.Colour && OnPickerMessage(message, wParam, lParam))
        {
            return;
        }

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

    // The picker's own mouse and keys; true when the message was its. Cancels go the shared way.
    private bool OnPickerMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case PInvoke.WM_MOUSEMOVE:
                pointer = Point(lParam);
                ShowLoupe();
                return true;
            case PInvoke.WM_LBUTTONDOWN:
                pointer = Point(lParam);
                Pick();
                return true;
            case PInvoke.WM_LBUTTONUP:
                return true;
            case PInvoke.WM_KEYDOWN:
                switch ((VIRTUAL_KEY)wParam.Value)
                {
                    case VIRTUAL_KEY.VK_RETURN or VIRTUAL_KEY.VK_SPACE:
                        Pick();
                        return true;
                    case VIRTUAL_KEY.VK_LEFT:
                        Nudge(-1, 0);
                        return true;
                    case VIRTUAL_KEY.VK_RIGHT:
                        Nudge(1, 0);
                        return true;
                    case VIRTUAL_KEY.VK_UP:
                        Nudge(0, -1);
                        return true;
                    case VIRTUAL_KEY.VK_DOWN:
                        Nudge(0, 1);
                        return true;
                }

                return false;
            default:
                return false;
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
