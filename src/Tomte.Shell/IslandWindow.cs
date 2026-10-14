using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.Graphics.Canvas.Geometry;
using Tomte.Core;
using Windows.System;
using Windows.UI;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using Windows.UI.ViewManagement;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;
using Windows.Win32.System.WinRT;
using Windows.Win32.System.WinRT.Composition;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Shell;

/// <summary>
/// The island surface: a WS_EX_NOREDIRECTIONBITMAP popup at the top of the primary monitor with a
/// Windows.UI.Composition tree. Five springs drive the outline; a worker thread waits on the
/// compositor clock and the UI thread rebuilds the path once per frame. The window is sized once
/// per morph to the union of start and target bounds, then shrunk to the shape and given a region
/// from the same outline when the springs settle. Hidden means SW_HIDE and a parked worker. The same tick
/// steps the mascot; a resting mascot parks the worker too and wakes it with a one-shot timer for its next beat.
/// </summary>
internal sealed unsafe class IslandWindow : IDisposable
{
    // One pixel of room around the outline for the stroke's anti-aliased edge: the drawn shape and the content
    // box start that far up and left of the outline's origin, which the root puts at the window's insets.
    private const int Margin = 1;
    private static readonly TimeSpan CrossfadeDuration = TimeSpan.FromMilliseconds(120);
    private static IslandWindow? instance;

    private readonly DispatcherQueueController queueController;
    private readonly Compositor compositor;
    private readonly DesktopWindowTarget target;
    private readonly ContainerVisual root;
    private readonly ContainerVisual live;
    private readonly Drawn pill;
    private readonly Drawn ghost;
    private readonly ScalarKeyFrameAnimation fadeIn;
    private readonly ScalarKeyFrameAnimation fadeOut;
    private readonly UISettings uiSettings = new();
    private readonly Spring width = new();
    private readonly Spring depth = new();
    private readonly Spring corner = new();
    private readonly Spring rampW = new();
    private readonly Spring rampH = new();
    private readonly Spring[] springs;
    private readonly List<OutlineSegment> segments = new(24);
    private readonly List<Vector2> flattened = new(128);
    private readonly List<System.Drawing.Point> regionPoints = new(128);
    private readonly ManualResetEventSlim ticking = new(false);
    private readonly DispatcherQueueHandler tickHandler;
    private readonly DispatcherQueueHandler finishHandler;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly MascotVisual mascotVisual;
    private readonly DispatcherQueueTimer wakeTimer;
    private double scale;
    private int screenCenterX;
    private int windowX;
    private int windowWidth;
    private int windowHeight;
    private bool hasRegion;
    private bool visible;
    private bool attached;
    private bool panelLook;
    private int crossfadeGeneration;
    private IslandGeometry targetGeometry = IslandGeometry.Hidden;
    private TaskCompletionSource? settled;
    private double lastTickMs = -1;
    private double morphStartMs;
    private bool islandMoving;
    private double leadMs;
    private IslandGeometry? anchor;
    private float mascotX;

    public IslandWindow()
    {
        instance = this;
        springs = [width, depth, corner, rampW, rampH];
        tickHandler = Tick;
        finishHandler = FinishWithoutClock;

        var hInstance = new HINSTANCE(Marshal.GetHINSTANCE(typeof(IslandWindow).Module));
        fixed (char* className = "TomteIsland")
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &HandleMessage,
                hInstance = hInstance,
                lpszClassName = className,
            };
            if (PInvoke.RegisterClassEx(in windowClass) == 0)
            {
                throw new Win32Exception();
            }

            // Created hidden: idle means nothing drawn, no space reserved, no focus taken.
            Hwnd = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_TOPMOST
                | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
                | WINDOW_EX_STYLE.WS_EX_NOACTIVATE
                | WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP,
                className,
                null,
                WINDOW_STYLE.WS_POPUP,
                0,
                0,
                1,
                1,
                HWND.Null,
                HMENU.Null,
                hInstance,
                null);
            if (Hwnd == HWND.Null)
            {
                throw new Win32Exception();
            }
        }

        BOOL disableTransitions = true;
        PInvoke.DwmSetWindowAttribute(Hwnd, DWMWINDOWATTRIBUTE.DWMWA_TRANSITIONS_FORCEDISABLED, &disableTransitions, (uint)sizeof(BOOL));
        scale = PInvoke.GetDpiForWindow(Hwnd) / 96.0;

        // Windows.UI.Composition needs the OS dispatcher queue on this thread.
        PInvoke.CreateDispatcherQueueController(
            new DispatcherQueueOptions
            {
                dwSize = (uint)sizeof(DispatcherQueueOptions),
                threadType = DISPATCHERQUEUE_THREAD_TYPE.DQTYPE_THREAD_CURRENT,
                apartmentType = DISPATCHERQUEUE_THREAD_APARTMENTTYPE.DQTAT_COM_STA,
            },
            out queueController).ThrowOnFailure();
        Queue = queueController.DispatcherQueue;

        compositor = new Compositor();
        target = CreateDesktopWindowTarget(compositor, Hwnd);
        root = compositor.CreateContainerVisual();
        target.Root = root;

        // Tree: root > [ghost (previous outline, only during a reduced-motion crossfade), live > [pill, Content]].
        // The pill is drawn outside the clip so its stroke keeps both halves; content is clipped by the same path.
        var gradient = compositor.CreateLinearGradientBrush();
        gradient.StartPoint = new Vector2(0, 0);
        gradient.EndPoint = new Vector2(0, 1);
        gradient.ColorStops.Add(compositor.CreateColorGradientStop(0, Color.FromArgb(255, 0x10, 0x10, 0x12)));
        gradient.ColorStops.Add(compositor.CreateColorGradientStop(1, Color.FromArgb(255, 0, 0, 0)));
        pill = new Drawn(compositor, gradient);

        Content = compositor.CreateContainerVisual();
        Content.Clip = compositor.CreateGeometricClip(pill.Geometry);

        live = compositor.CreateContainerVisual();
        live.Children.InsertAtBottom(pill.Visual);
        live.Children.InsertAtTop(Content);

        ghost = new Drawn(compositor, gradient);
        ghost.Visual.Opacity = 0;
        ShadowLook();

        // The mascot sits above the content and outside the clip, so it can be seen before the shape has any depth.
        Mascot = new Mascot(() => clock.Elapsed.TotalSeconds, () => ForceReduceMotion || !uiSettings.AnimationsEnabled) { Changed = Wake };
        mascotVisual = new MascotVisual(compositor);
        live.Children.InsertAtTop(mascotVisual.Visual);
        wakeTimer = Queue.CreateTimer();
        wakeTimer.IsRepeating = false;
        wakeTimer.Tick += (_, _) => Wake();

        root.Children.InsertAtBottom(ghost.Visual);
        root.Children.InsertAtTop(live);

        fadeIn = compositor.CreateScalarKeyFrameAnimation();
        fadeIn.Duration = CrossfadeDuration;
        fadeIn.InsertKeyFrame(1, 1);
        fadeOut = compositor.CreateScalarKeyFrameAnimation();
        fadeOut.Duration = CrossfadeDuration;
        fadeOut.InsertKeyFrame(1, 0);

        foreach (var spring in springs)
        {
            spring.Jump(0);
        }

        width.Jump(IslandGeometry.Hidden.Width);

        // Warm the path code while hidden; the first rebuild is otherwise an 80 to 110 ms JIT stall on the first reveal.
        Apply(IslandGeometry.Compact);
        Apply(IslandGeometry.Hidden);

        new Thread(CompositorClockLoop) { IsBackground = true, Name = "tomte-compositor-clock" }.Start();
    }

    public HWND Hwnd { get; }

    public DispatcherQueue Queue { get; }

    /// <summary>Clipped by the outline and morphing with it. Panel content attaches here.</summary>
    public ContainerVisual Content { get; }

    /// <summary>The tomte at the island's left edge. Settings set Enabled and CapColor; features set its reactions.</summary>
    public Mascot Mascot { get; }

    /// <summary>The mascot's centre in screen pixels, for the Shelf-drop lean.</summary>
    public double MascotScreenX => windowX + root.Offset.X + mascotX + MascotVisual.SizeDips / 2 * scale;

    /// <summary>For content visuals; the tree is in physical pixels, so content scales by <see cref="Scale"/>.</summary>
    public Compositor Compositor => compositor;

    public double Scale => scale;

    public IslandGeometry Current => new(width.Value, depth.Value, corner.Value, rampW.Value, rampH.Value);

    /// <summary>Primary monitor in DIPs at the window's DPI, for the 60% x 50% panel cap.</summary>
    public (double Width, double Height) MonitorSize =>
        (PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN) / scale, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSCREEN) / scale);

    /// <summary>
    /// Only the notch tells panels and the palette from the other states by the 1 px stroke; the floating pill
    /// carries it in every state. Read when the next morph draws.
    /// </summary>
    public bool PanelLook
    {
        set => panelLook = value;
    }

    /// <summary>
    /// The "Attach to the top edge" setting: the notch joined to the screen edge, with fillets and no shadow,
    /// instead of the floating pill. Takes effect with the next morph.
    /// </summary>
    // switched while the island shows, that morph starts with a jump between the two silhouettes;
    // retract first if the setting ever gets a switch in the settings window.
    public bool Attached
    {
        get => attached;
        set
        {
            attached = value;
            ShadowLook();
        }
    }

    /// <summary>Debug aid: crossfade even when Windows animation effects are on.</summary>
    public bool ForceReduceMotion { get; init; }

    /// <summary>
    /// What the pointer is tracked against for the geometry the current morph is heading for, in physical pixels:
    /// the outline's box and, under the floating pill, the strip up to the screen edge. Not the shadow's room.
    /// </summary>
    public System.Drawing.Rectangle TargetBounds { get; private set; }

    /// <summary>The scale or the screen centre changed; anything measured in pixels needs recomputing.</summary>
    public Action? LayoutChanged { get; set; }

    /// <summary>
    /// Timers, WM_APP posts from the input thread, shell notifications, power broadcasts and clock changes land
    /// on this window. The handler returns true when it took the message, false to leave the answer to
    /// DefWindowProc; exceptions are logged, never unwound into the caller.
    /// </summary>
    public Func<uint, nuint, nint, bool>? Message { get; set; }

    /// <summary>Where a failure inside the window procedure is written; an exception there would otherwise kill the process.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Ticks and wall time of the last completed morph, for frame-rate checks.</summary>
    public int LastMorphTicks { get; private set; }

    public double LastMorphMs { get; private set; }

    /// <summary>
    /// Retarget the springs toward <paramref name="to"/>; safe mid-flight. The task completes when the
    /// shape comes to rest, whichever target it rests on; a retarget shares the task of the running morph.
    /// </summary>
    public Task MorphTo(IslandGeometry to, SpringPreset preset)
    {
        Rescale(MonitorScale());
        if (attached)
        {
            to = to.Attached();
        }

        var from = Current;
        targetGeometry = to;
        double lead = Mascot.TakeIslandLead();
        // Revealing or retracting, the mascot stays at the open shape's left edge while the outline grows or shrinks.
        // a retarget mid-reveal drops the anchor and the mascot jumps to the live edge in one frame; give the
        // mascot its own x spring if that shows.
        anchor = from.Depth <= 0.5 ? to : to.Depth <= 0.5 ? from : null;
        TargetBounds = IslandOutline.PointerBounds(Bounds(to), scale, attached);
        crossfadeGeneration++; // a crossfade still in flight must not settle this morph
        foreach (var spring in springs)
        {
            spring.Use(preset);
        }

        (width.Target, depth.Target, corner.Target, rampW.Target, rampH.Target) = (to.Width, to.Depth, to.Corner, to.RampW, to.RampH);

        // The region goes before the resize; the other order leaves it clipping the enlarged window for a frame.
        ClearRegion();
        SizeWindow(System.Drawing.Rectangle.Union(Bounds(from), Bounds(to)));
        // Re-place the start outline inside the resized window now; waiting for the first tick draws it at
        // the union's left edge for one frame, and the crossfade would copy that stale offset to the ghost.
        Apply(from);
        if (!visible)
        {
            visible = true;
            PInvoke.ShowWindow(Hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
            PInvoke.SetWindowPos(Hwnd, HWND.HWND_TOPMOST, 0, 0, 0, 0, SET_WINDOW_POS_FLAGS.SWP_NOMOVE | SET_WINDOW_POS_FLAGS.SWP_NOSIZE | SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
        }

        settled ??= new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        if (ForceReduceMotion || !uiSettings.AnimationsEnabled)
        {
            Crossfade(to);
        }
        else
        {
            if (!islandMoving)
            {
                islandMoving = true;
                morphStartMs = clock.Elapsed.TotalMilliseconds;
                LastMorphTicks = 0;
                leadMs = lead * 1000;
            }

            Wake();
        }

        return settled.Task;
    }

    public void Dispose()
    {
        ticking.Reset();
        wakeTimer.Stop();
        PInvoke.DestroyWindow(Hwnd);
        instance = null;
    }

    /// <summary>
    /// The window rectangle a state's geometry rests in, in physical pixels: the pill, the gap above it and the
    /// room its shadow takes. Desktop peek asks whether anything is under it.
    /// </summary>
    public System.Drawing.Rectangle RestBounds(IslandGeometry g)
    {
        Rescale(MonitorScale());
        return Bounds(attached ? g.Attached() : g);
    }

    /// <summary>Window rectangle for a geometry in physical pixels, centred on the primary monitor's top edge.</summary>
    private System.Drawing.Rectangle Bounds(in IslandGeometry g) => IslandOutline.WindowBounds(g, scale, screenCenterX, attached);

    /// <summary>Where the outline's origin sits in a window that is exactly its geometry's bounds.</summary>
    private Vector3 Origin()
    {
        var (side, top, _) = IslandOutline.Insets(scale, attached);
        return new Vector3(side, top, 0);
    }

    /// <summary>The floating pill casts the shadow; the notch has none. Its offset and blur are DIPs times the scale.</summary>
    private void ShadowLook()
    {
        pill.Shadow(!attached, scale);
        ghost.Shadow(!attached, scale);
    }

    private void SizeWindow(System.Drawing.Rectangle r)
    {
        if (r.X == windowX && r.Width == windowWidth && r.Height == windowHeight)
        {
            return;
        }

        (windowX, windowWidth, windowHeight) = (r.X, r.Width, r.Height);
        PInvoke.SetWindowPos(Hwnd, HWND.HWND_TOPMOST, r.X, 0, r.Width, r.Height, SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    private void CompositorClockLoop()
    {
        while (true)
        {
            ticking.Wait();
            // Blocks until the next compositor frame; the park above is what keeps idle at 0% CPU.
            uint wait = PInvoke.DCompositionWaitForCompositorClock(0, null, 100);
            if ((WAIT_EVENT)wait == WAIT_EVENT.WAIT_OBJECT_0)
            {
                Queue.TryEnqueue(tickHandler);
            }
            else if ((WAIT_EVENT)wait != WAIT_EVENT.WAIT_TIMEOUT)
            {
                // No compositor clock (DWM restarting, remote session): finish the morph in one step rather than spin.
                ticking.Reset();
                Queue.TryEnqueue(finishHandler);
            }
        }
    }

    private void Tick()
    {
        if (!ticking.IsSet)
        {
            return;
        }

        double now = clock.Elapsed.TotalMilliseconds;
        double dt = lastTickMs < 0 ? 0 : Math.Min(0.05, (now - lastTickMs) / 1000);
        lastTickMs = now;
        bool finished = false;
        if (islandMoving)
        {
            // The tomte drops in from above the edge; the shape starts 70 ms later.
            double islandDt = now - morphStartMs < leadMs ? 0 : dt;
            bool done = true;
            foreach (var spring in springs)
            {
                spring.Step(islandDt);
                done &= spring.Settled;
            }

            LastMorphTicks++;
            if (done)
            {
                (islandMoving, finished) = (false, true);
                LastMorphMs = now - morphStartMs;
            }
        }

        bool mascotActive = Mascot.Step(dt);
        if (islandMoving)
        {
            Apply(Current);
            return;
        }

        if (finished)
        {
            Settle();
        }
        else
        {
            PlaceMascot(Current);
        }

        if (visible && mascotActive)
        {
            return;
        }

        ticking.Reset();
        if (visible && Mascot.WakeIn is double wait)
        {
            wakeTimer.Interval = TimeSpan.FromSeconds(wait);
            wakeTimer.Start();
        }
    }

    /// <summary>Resume the tick for a morph or a mascot change; nothing runs while the island is hidden.</summary>
    private void Wake()
    {
        if (!visible)
        {
            return;
        }

        wakeTimer.Stop();
        if (!ticking.IsSet)
        {
            lastTickMs = -1;
            ticking.Set();
        }
    }

    private void FinishWithoutClock()
    {
        islandMoving = false;
        if (settled is not null)
        {
            Settle();
        }
    }

    /// <summary>Snap the springs to their targets, draw the final outline, shrink the window and apply the region.</summary>
    private void Settle()
    {
        foreach (var spring in springs)
        {
            spring.Jump(spring.Target);
        }

        var g = Current;
        anchor = null;
        if (g.Depth <= 0.5)
        {
            // Hidden costs nothing: no beat may wake the tick, and the next show starts from a clean tomte.
            visible = false;
            wakeTimer.Stop();
            Mascot.Park();
        }

        Apply(g);
        SizeWindow(Bounds(g));
        root.Offset = Origin();
        if (!visible)
        {
            PInvoke.ShowWindow(Hwnd, SHOW_WINDOW_CMD.SW_HIDE);
        }
        else
        {
            SetRegion(g);
        }

        var done = settled;
        settled = null;
        done?.TrySetResult();
    }

    /// <summary>A closed composition path from segments, scaled by <paramref name="s"/>.</summary>
    internal static CompositionPath BuildPath(ReadOnlySpan<OutlineSegment> segments, float s)
    {
        using var builder = new CanvasPathBuilder(null);
        foreach (var segment in segments)
        {
            switch (segment.Kind)
            {
                case OutlineSegmentKind.Move:
                    builder.BeginFigure(segment.End * s);
                    break;
                case OutlineSegmentKind.Line:
                    builder.AddLine(segment.End * s);
                    break;
                case OutlineSegmentKind.Quadratic:
                    builder.AddQuadraticBezier(segment.Control1 * s, segment.End * s);
                    break;
                default:
                    builder.AddCubicBezier(segment.Control1 * s, segment.Control2 * s, segment.End * s);
                    break;
            }
        }

        builder.EndFigure(CanvasFigureLoop.Closed);
        return new CompositionPath(CanvasGeometry.CreatePath(builder));
    }

    /// <summary>Rebuild the path from the spring values. Only the path object is new; every buffer is reused.</summary>
    private void Apply(in IslandGeometry g)
    {
        IslandOutline.Build(g, attached, segments);
        pill.Geometry.Path = BuildPath(CollectionsMarshal.AsSpan(segments), (float)scale);

        // The pill's hairline thins out with its last pixel of depth and goes where the outline has no area left,
        // so a reveal does not start as a line under the gap.
        pill.Shape.StrokeThickness = attached ? (panelLook ? 1 : 0) : g.Depth <= 0.5 ? 0 : (float)Math.Min(1, g.Depth * scale);

        root.Offset = Origin() + new Vector3(Bounds(g).X - windowX, 0, 0);

        // The outline's box and a pixel around it. Content anchors to this width, so the shadow's room stays out of it.
        var size = new Vector2((float)Math.Ceiling(g.OuterWidth * scale), (float)Math.Ceiling(Math.Max(0, g.Depth) * scale)) + new Vector2(2 * Margin);
        pill.Size = size;
        Content.Size = size;
        PlaceMascot(g);
    }

    /// <summary>Put the tomte at the left edge of the shape, or of the anchor shape during a reveal or retract.</summary>
    private void PlaceMascot(in IslandGeometry g)
    {
        var a = anchor ?? g;
        float s = (float)scale;
        mascotX = (float)(g.OuterWidth / 2 - a.Width / 2 + MascotVisual.InsetX) * s;
        mascotVisual.Apply(Mascot, new Vector2(mascotX, MascotVisual.InsetY * s), s);
    }

    private void ClearRegion()
    {
        if (hasRegion)
        {
            hasRegion = false;
            PInvoke.SetWindowRgn(Hwnd, null, false);
        }
    }

    /// <summary>
    /// Hit-test region for the outline just drawn. The region clips pixels too, so it is the notch's own outline,
    /// or the pill with the room its shadow needs and nothing of the gap above it.
    /// </summary>
    private void SetRegion(in IslandGeometry g)
    {
        IslandOutline.Region(g, scale, attached, segments, flattened, regionPoints);
        var region = PInvoke.CreatePolygonRgn(CollectionsMarshal.AsSpan(regionPoints), CREATE_POLYGON_RGN_MODE.WINDING);
        if (PInvoke.SetWindowRgn(Hwnd, region, true) != 0)
        {
            // The system owns the region now; DeleteObject must not run on it.
            region.SetHandleAsInvalid();
            hasRegion = true;
        }
        else
        {
            region.Dispose();
        }
    }

    /// <summary>Reduced motion: no morph. The old outline fades out on the ghost while the new one fades in.</summary>
    private void Crossfade(in IslandGeometry to)
    {
        ghost.Geometry.Path = pill.Geometry.Path;
        ghost.Shape.StrokeThickness = pill.Shape.StrokeThickness;
        ghost.Size = pill.Size;
        float oldOffset = root.Offset.X;
        foreach (var spring in springs)
        {
            spring.Jump(spring.Target);
        }

        Apply(to);
        ghost.Visual.Offset = new Vector3(oldOffset - root.Offset.X - Margin, -Margin, 0);
        ghost.Visual.Opacity = 1;
        live.Opacity = 0;

        int generation = crossfadeGeneration;
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        ghost.Visual.StartAnimation("Opacity", fadeOut);
        live.StartAnimation("Opacity", fadeIn);
        batch.End();
        batch.Completed += (_, _) =>
        {
            // A newer morph may have started meanwhile; only the latest crossfade settles.
            if (generation == crossfadeGeneration)
            {
                Settle();
            }
        };
    }

    /// <summary>The monitor's effective DPI, which is live; a window's own DPI only updates once WM_DPICHANGED lands.</summary>
    private double MonitorScale()
    {
        var monitor = PInvoke.MonitorFromWindow(Hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY);
        return PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out uint dpi, out _).Succeeded ? dpi / 96.0 : scale;
    }

    /// <summary>
    /// Re-lay the current outline out at a new scale or screen centre. WM_DPICHANGED can arrive at once,
    /// during the next SetWindowPos, or not until then, so this is idempotent and never completes a morph.
    /// A resolution change at the same DPI only moves the centre, so that is checked too.
    /// </summary>
    private void Rescale(double newScale)
    {
        int centerX = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN) / 2;
        if (newScale == scale && centerX == screenCenterX)
        {
            return;
        }

        (scale, screenCenterX) = (newScale, centerX);
        ShadowLook();
        TargetBounds = IslandOutline.PointerBounds(Bounds(targetGeometry), scale, attached);
        LayoutChanged?.Invoke();
        if (!visible)
        {
            return;
        }

        ClearRegion();
        if (ticking.IsSet)
        {
            SizeWindow(System.Drawing.Rectangle.Union(Bounds(Current), Bounds(targetGeometry)));
            Apply(Current);
        }
        else
        {
            var g = Current;
            Apply(g);
            SizeWindow(Bounds(g));
            root.Offset = Origin();
            SetRegion(g);
        }
    }

    internal static DesktopWindowTarget CreateDesktopWindowTarget(Compositor compositor, HWND hwnd)
    {
        nint unknown = WinRT.MarshalInspectable<object>.FromManaged(compositor);
        try
        {
            var iid = ICompositorDesktopInterop.IID_Guid;
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in iid, out nint interop));
            try
            {
                // Raw vtable call (slot 3): CsWin32's generated marshaler hands back a bare __ComObject for the out parameter.
                // isTopmost=false keeps the tree beneath any child HWND a panel might host later.
                void** vtbl = *(void***)interop;
                void* raw;
                var hr = ((delegate* unmanaged[Stdcall]<void*, HWND, BOOL, void**, HRESULT>)vtbl[3])((void*)interop, hwnd, false, &raw);
                hr.ThrowOnFailure();
                var target = DesktopWindowTarget.FromAbi((nint)raw);
                Marshal.Release((nint)raw);
                return target;
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

    /// <summary>An exception must not unwind through the unmanaged caller: log it and report the message handled.</summary>
    private static LRESULT GuardedRescale(double? newScale)
    {
        try
        {
            if (instance is not null && newScale is double s)
            {
                instance.Rescale(s);
            }
        }
        catch (Exception e)
        {
            instance?.Log?.Invoke($"rescale failed: {e}");
        }

        return new LRESULT(0);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT HandleMessage(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case PInvoke.WM_MOUSEACTIVATE:
                return new LRESULT((nint)PInvoke.MA_NOACTIVATE);
            case PInvoke.WM_DPICHANGED:
                return GuardedRescale(((uint)(wParam.Value >> 16) & 0xFFFF) / 96.0);
            case PInvoke.WM_DISPLAYCHANGE:
                // A scaling change reaches a resting window here before its own WM_DPICHANGED does.
                return GuardedRescale(instance?.MonitorScale());
            case PInvoke.WM_CLOSE:
                PInvoke.DestroyWindow(window);
                return new LRESULT(0);
            case PInvoke.WM_DESTROY:
                PInvoke.PostQuitMessage(0);
                return new LRESULT(0);
            case PInvoke.WM_TIMER:
            case PInvoke.WM_WTSSESSION_CHANGE:
            case PInvoke.WM_POWERBROADCAST:
            case PInvoke.WM_TIMECHANGE:
            case >= PInvoke.WM_APP and < PInvoke.WM_APP + 0x100:
                return Route(message, wParam.Value, lParam.Value) ? new LRESULT(0) : PInvoke.DefWindowProc(window, message, wParam, lParam);
            default:
                return PInvoke.DefWindowProc(window, message, wParam, lParam);
        }
    }

    private static bool Route(uint message, nuint wParam, nint lParam)
    {
        try
        {
            return instance?.Message?.Invoke(message, wParam, lParam) ?? false;
        }
        catch (Exception e)
        {
            instance?.Log?.Invoke($"message 0x{message:X} failed: {e}");
            return true;
        }
    }

    /// <summary>
    /// One outline as drawn: the gradient fill with its hairline, and the floating pill's drop shadow, which the
    /// compositor blurs from an off-screen copy of that fill. The live shape and the crossfade ghost are one each.
    /// </summary>
    private sealed class Drawn
    {
        // Further than any monitor is wide: the sides of the clip that must not cut anything.
        private const float Far = 16384;
        private readonly ShapeVisual fill;
        private readonly CompositionVisualSurface copy;
        private readonly SpriteVisual shadowVisual;
        private readonly DropShadow shadow;
        private bool shadowOn;

        public Drawn(Compositor compositor, CompositionBrush gradient)
        {
            Geometry = compositor.CreatePathGeometry();
            Shape = compositor.CreateSpriteShape(Geometry);
            Shape.FillBrush = gradient;
            Shape.StrokeBrush = compositor.CreateColorBrush(Color.FromArgb(26, 255, 255, 255));
            Shape.StrokeThickness = 0;
            Shape.Offset = new Vector2(Margin);
            fill = compositor.CreateShapeVisual();
            fill.Shapes.Add(Shape);

            copy = compositor.CreateVisualSurface();
            copy.SourceVisual = fill;
            shadow = compositor.CreateDropShadow();
            shadow.Mask = compositor.CreateSurfaceBrush(copy);
            shadow.Color = Color.FromArgb(255, 0, 0, 0);
            shadow.Opacity = 0.35f;
            shadowVisual = compositor.CreateSpriteVisual();
            shadowVisual.Shadow = shadow;

            // Nothing is drawn above the pixel row over the outline's top edge: the shadow stays out of the gap,
            // which passes clicks through, during a morph as it does once the region is on.
            Visual = compositor.CreateContainerVisual();
            Visual.Offset = new Vector3(-Margin, -Margin, 0);
            Visual.Clip = compositor.CreateInsetClip(-Far, 0, -Far, -Far);
            Visual.Children.InsertAtBottom(shadowVisual);
            Visual.Children.InsertAtTop(fill);
        }

        public ContainerVisual Visual { get; }

        public CompositionPathGeometry Geometry { get; }

        public CompositionSpriteShape Shape { get; }

        /// <summary>The outline's box with <see cref="Margin"/> around it, in pixels.</summary>
        public Vector2 Size
        {
            get => fill.Size;
            set
            {
                fill.Size = value;
                if (shadowOn)
                {
                    shadowVisual.Size = value;
                    copy.SourceSize = value;
                }
            }
        }

        /// <summary>The compositor's blur radius is three standard deviations (fitted on screen: 10.5 px for 7 DIP at 150%).</summary>
        public void Shadow(bool on, double scale)
        {
            shadowOn = on;
            shadowVisual.IsVisible = on;
            shadow.Offset = new Vector3(0, (float)(IslandOutline.ShadowOffsetY * scale), 0);
            shadow.BlurRadius = (float)(3 * IslandOutline.ShadowSigma * scale);
        }
    }
}
