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
/// The island surface: a WS_EX_NOREDIRECTIONBITMAP popup at the top of the primary monitor, where along the edge a
/// drag on the pill left it, with a Windows.UI.Composition tree. Five springs drive the outline; a worker thread
/// waits on the compositor clock and the UI thread rebuilds the path once per frame. The window is sized once
/// per morph to the union of start and target bounds, then shrunk to the shape and given a region
/// from the same outline when the springs settle. Hidden means SW_HIDE and a parked worker. The same tick
/// steps the mascot; a resting mascot parks the worker too and wakes it with a one-shot timer for its next beat.
/// </summary>
internal sealed unsafe class IslandWindow : IDisposable
{
    // One pixel of room around the outline for the stroke's anti-aliased edge: the drawn shape and the content
    // box start that far up and left of the outline's origin, which the root puts at the window's insets.
    private const int Margin = 1;

    // Content fades in once the shape is this far along its morph, and gets this long to fade out before a retract.
    private const double ContentReadyAt = 0.6;
    private const double ContentFadeOutSeconds = 0.1;
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

    // The centre's place along the top edge, in DIPs from the middle (IslandPlace). Not one of the shape's springs: it
    // moves the window rather than the outline, and a drag sets it straight from the pointer.
    private readonly Spring place = new();
    private readonly List<OutlineSegment> segments = new(24);
    private readonly List<Vector2> flattened = new(128);
    private readonly List<System.Drawing.Point> regionPoints = new(128);
    private readonly ManualResetEventSlim ticking = new(false);
    private readonly DispatcherQueueHandler tickHandler;
    private readonly DispatcherQueueHandler finishHandler;
    private readonly DispatcherQueueHandler contentReadyHandler;
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly MascotVisual mascotVisual;
    private readonly DispatcherQueueTimer wakeTimer;
    private double scale;
    private int screenWidth;
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
    private double leadMs, heldLead;
    private IslandGeometry? anchor;
    private float mascotX;
    private double fromWidth, fromDepth;
    private bool contentPending;

    // The swell is asked for, and the springs aim at its targets (grown or back) rather than a state's.
    private bool swell, swellAimed;

    // A press on the pill's own surface, held until it is a click or a drag; and the last such click where one may be
    // half of a double click, and where it landed.
    private bool grabbing, grabMoved, grabPill;
    private System.Drawing.Point grabFrom, lastClickAt;
    private Vector2 grabAt;
    private double grabOffset;
    private long lastClick;

    public IslandWindow()
    {
        instance = this;
        springs = [width, depth, corner, rampW, rampH];
        tickHandler = Tick;
        finishHandler = FinishWithoutClock;
        contentReadyHandler = RaiseContentReady;

        var hInstance = new HINSTANCE(Marshal.GetHINSTANCE(typeof(IslandWindow).Module));
        fixed (char* className = "TomteIsland")
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &HandleMessage,
                hInstance = hInstance,
                // Without a class cursor Windows sets none over the island, and the pointer keeps the shape of
                // whatever it was over last: an I-beam, a resize arrow.
                hCursor = PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_ARROW),
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
        screenWidth = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN);

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
        // Island space hangs off Content alone: the compositor keeps its origin on Content's centre line
        // (Content is the outline plus the margin on each side) through every frame of a morph. Rounded down to a
        // whole pixel, which for a window centred on its place is the place's centre column in every frame, so
        // text drawn in DIPs can be put on the pixel grid once and stay there.
        Dips = compositor.CreateContainerVisual();
        Dips.Scale = new Vector3((float)scale, (float)scale, 1);
        var centre = compositor.CreateExpressionAnimation(FormattableString.Invariant($"Vector3(Floor((content.Size.X - {2 * Margin}) / 2), 0, 0)"));
        centre.SetReferenceParameter("content", Content);
        Dips.StartAnimation("Offset", centre);
        Content.Children.InsertAtBottom(Dips);
        Live = compositor.CreatePropertySet();
        Live.InsertScalar("RampW", 0);

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
        Interaction = new InteractionHost(this);
    }

    public HWND Hwnd { get; }

    public DispatcherQueue Queue { get; }

    /// <summary>Clipped by the outline and morphing with it, in physical pixels.</summary>
    public ContainerVisual Content { get; }

    /// <summary>
    /// Inside <see cref="Content"/>, in island space: DIPs, x = 0 on the island's centre line, y = 0 at the
    /// island's top edge. It carries the monitor's scale, so what is built here is laid out in DIPs and
    /// follows a DPI change by itself. Panels and Compact content attach here.
    /// </summary>
    public ContainerVisual Dips { get; }

    /// <summary>
    /// The live shape for expressions, set in every frame of a morph: "RampW" is how far in DIPs each fillet of an
    /// attached shape flares out past the body at the top edge, 0 for the floating pill. Content laid out from the
    /// body's edges, as the tab strip is, takes it off <see cref="Dips"/>' offset.
    /// </summary>
    public CompositionPropertySet Live { get; }

    /// <summary>
    /// Raised once per morph to a visible shape when it has come 60% of the way, which is when new content
    /// fades in. Always on a later turn of the dispatcher than the MorphTo call, reduced motion included.
    /// </summary>
    public event Action? ContentReady;

    /// <summary>A morph to a visible shape came to rest: work held back while <see cref="Moving"/> can run now.</summary>
    public event Action? Settled;

    /// <summary>A morph or a retarget starts, before the springs move: what is placed from the shape as it is now should go.</summary>
    public event Action? Morphing;

    /// <summary>
    /// A left button press on the island, in island space. One that <see cref="Grabs"/> holds arrives when the button
    /// comes up.
    /// </summary>
    public event Action<Vector2>? Pressed;

    /// <summary>
    /// Whether a press at this island point may move the island: the pill's own surface rather than a control on it.
    /// Such a press takes the mouse (the window still does not activate) until the button comes up: past the system's
    /// drag distance the island follows the pointer along the top edge, and short of it the press is a click. Two
    /// quick clicks on a panel's tab strip outside its labels, where one does nothing, send a moved island back to the
    /// middle.
    /// </summary>
    public Func<Vector2, bool>? Grabs { get; set; }

    /// <summary>A press that <see cref="Grabs"/> took is down: it is not a hover, and not yet a click or a drag.</summary>
    public bool Holding => grabbing;

    /// <summary>A held press became a drag; from now until <see cref="Moved"/> the island follows the pointer.</summary>
    public event Action? MoveStarted;

    /// <summary>The drag let go, or a double click on the pill sent the island back to the middle: its new place, to keep.</summary>
    public event Action<double>? Moved;

    /// <summary>The pixel column the Compact pill's centre rests on, which the reveal zone is centred under.</summary>
    public int CenterX => IslandPlace.Center(place.Target, attached ? IslandGeometry.Compact.Attached() : IslandGeometry.Compact, scale, screenWidth, attached);

    /// <summary>The wheel turned over the island, at a point in island space: the raw delta, 120 a notch, positive away from the user.</summary>
    public event Action<Vector2, int>? Wheel;

    /// <summary>The tomte at the island's left edge. Settings set Enabled and CapColor; features set its reactions.</summary>
    public Mascot Mascot { get; }

    /// <summary>Whether an island point is on the tomte's box at the shape's left edge, while the tomte is on.</summary>
    public bool OnMascot(Vector2 at) => Mascot.Enabled && MascotArt.Box(anchor ?? Current).Contains(at.X, at.Y);

    /// <summary>The island is pinned (Ctrl+P): a small mark at the tomte's lower right says so.</summary>
    public bool Pinned
    {
        set => mascotVisual.Pinned = value;
    }

    /// <summary>The mascot's centre in screen pixels, for the Shelf-drop lean.</summary>
    public double MascotScreenX => windowX + root.Offset.X + mascotX + MascotVisual.SizeDips / 2 * scale;

    /// <summary>For content visuals; the tree is in physical pixels, so content scales by <see cref="Scale"/>.</summary>
    public Compositor Compositor => compositor;

    public double Scale => scale;

    public IslandGeometry Current => new(width.Value, depth.Value, corner.Value, rampW.Value, rampH.Value);

    /// <summary>The shape the current morph is heading for, as drawn (the notch when attached), without the swell.</summary>
    public IslandGeometry Heading => targetGeometry;

    /// <summary>
    /// A press at this island point on a target that moves the island: true once the pointer has gone the system's drag
    /// distance (Win32 runs that loop), and the island then follows it along the top edge until the button comes up, as
    /// for a press <see cref="Grabs"/> holds; false when the button came up first, which is a click. It returns true with
    /// the button still down: the follow goes on under the island's capture, and works only because the window
    /// procedure hands the held press's moves, release and capture change to <see cref="Grab"/> before the interaction
    /// host sees them.
    /// </summary>
    public bool Drag(Vector2 at)
    {
        var from = IslandToScreen(at);
        if (!PInvoke.DragDetect(Hwnd, from))
        {
            return false;
        }

        double offset = IslandPlace.Clamp(place.Value, targetGeometry, scale, screenWidth, attached);
        (grabbing, grabMoved, grabPill, grabFrom, grabAt, grabOffset) = (true, false, false, from, at, offset);
        PInvoke.SetCapture(Hwnd);
        // The pointer is past the drag distance already: the island catches up with it now.
        Grab(PInvoke.WM_MOUSEMOVE);
        return true;
    }

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

    /// <summary>Windows animation effects are off, or the debug aid says so: no morphs, crossfades only.</summary>
    public bool ReduceMotion => ForceReduceMotion || !uiSettings.AnimationsEnabled;

    /// <summary>A driven run cloaked the island (<see cref="Cloak"/>); nothing of Tomte may reach the screen.</summary>
    public bool Cloaked { get; private set; }

    /// <summary>
    /// The launch arrival flies a tomte of its own into the pill: the island's stays undrawn while it does, and shows
    /// again where that one lands.
    /// </summary>
    public bool MascotAway
    {
        set => mascotVisual.Visual.Opacity = value ? 0 : 1;
    }

    /// <summary>
    /// The next morph from rest shows the window at once but holds the shape still for this many seconds: the launch
    /// arrival opens the pill while its tomte holds still, and the pill grows once the tomte is nearly up.
    /// </summary>
    public void HoldNextMorph(double seconds) => heldLead = seconds;

    /// <summary>Where the tomte rests in the Compact pill at the island's place: its box's top-left in screen pixels.</summary>
    public System.Drawing.PointF MascotLanding()
    {
        Rescale(MonitorScale());
        var shape = attached ? IslandGeometry.Compact.Attached() : IslandGeometry.Compact;
        return Arrival.Landing(shape, scale, IslandPlace.Center(place.Target, shape, scale, screenWidth, attached), attached);
    }

    /// <summary>
    /// What the pointer is tracked against for the geometry the current morph is heading for, in physical pixels:
    /// the outline's box and, under the floating pill, the strip up to the screen edge. Not the shadow's room.
    /// </summary>
    public System.Drawing.Rectangle TargetBounds { get; private set; }

    /// <summary>The scale, the screen's width or the island's place changed; anything measured in pixels needs recomputing.</summary>
    public event Action? LayoutChanged;

    /// <summary>
    /// Timers, WM_APP posts from the input thread, shell notifications, power broadcasts and clock changes land
    /// on this window. The handler returns true when it took the message, false to leave the answer to
    /// DefWindowProc; exceptions are logged, never unwound into the caller. WM_DESTROY comes too, so a handler
    /// can take off what it put on the window; its answer is ignored.
    /// </summary>
    public Func<uint, nuint, nint, bool>? Message { get; set; }

    /// <summary>
    /// Keyboard, input method, focus and drag messages while a text box may be typed in: the answer, or null for
    /// DefWindowProc. Only a typing state ever activates the window, so nothing else receives these.
    /// </summary>
    internal Func<uint, nuint, nint, LRESULT?>? Typing { get; set; }

    /// <summary>Hover, press, the cursor, tooltips and keyboard focus for the views registered with it.</summary>
    internal InteractionHost Interaction { get; }

    /// <summary>Where a failure inside the window procedure is written; an exception there would otherwise kill the process.</summary>
    public Action<string>? Log { get; set; }

    /// <summary>Ticks and wall time of the last completed morph, for frame-rate checks.</summary>
    public int LastMorphTicks { get; private set; }

    public double LastMorphMs { get; private set; }

    /// <summary>The shape's springs are moving; costly work that would drop frames can wait until they stop.</summary>
    public bool Moving => islandMoving;

    /// <summary>
    /// The morph under way ends now, on its target, as it does when there is no compositor clock: for when this thread is
    /// about to be held for a long while (another app taking a drop), which would leave the shape frozen part of the way,
    /// its content already faded out.
    /// </summary>
    public void FinishMorph()
    {
        if (islandMoving)
        {
            ticking.Reset();
            FinishWithoutClock();
        }
    }

    /// <summary>
    /// Retarget the springs toward <paramref name="to"/>; safe mid-flight. The task completes when the
    /// shape comes to rest, whichever target it rests on; a retarget shares the task of the running morph.
    /// </summary>
    public Task MorphTo(IslandGeometry to, SpringPreset preset)
    {
        Morphing?.Invoke();
        Rescale(MonitorScale());
        if (attached)
        {
            to = to.Attached();
        }

        var from = Current;
        if (to.Depth != depth.Target)
        {
            // Another state: a click made on the one before must not pair with one on this.
            ForgetClick();
        }

        targetGeometry = to;
        swell = swellAimed = false;
        double lead = Math.Max(Mascot.TakeIslandLead(), heldLead);
        heldLead = 0;
        if (to.Depth <= 0.5 && Dips.Children.Count > 0)
        {
            // Whatever shows inside fades out first; the shape follows.
            lead = Math.Max(lead, ContentFadeOutSeconds);
        }

        (fromWidth, fromDepth) = (from.Width, from.Depth);
        contentPending = to.Depth > 0.5;
        // a retarget mid-reveal drops the anchor and the mascot jumps to the live edge in one frame; give the
        // mascot its own x spring if that shows.
        anchor = MascotArt.Anchor(from, to);
        TargetBounds = PointerTarget();
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
            Queue.TryEnqueue(contentReadyHandler);
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

    /// <summary>
    /// The resting shape grows a few DIPs while the pointer rests on it, and springs back: the width, depth and corner
    /// springs retargeted on the gentle preset with no change of state, so nothing fades in again, a click waiting for
    /// a second one keeps waiting and the content stays where it is. Asked for during a morph, it starts once the shape
    /// settles; the next morph ends it. Windows animation effects off, it does not grow.
    /// </summary>
    public void Swell(bool on)
    {
        if (on == swell || !visible || (on && (ForceReduceMotion || !uiSettings.AnimationsEnabled)))
        {
            return;
        }

        swell = on;
        if (!islandMoving || swellAimed)
        {
            AimSwell();
        }
    }

    private void AimSwell()
    {
        var rest = targetGeometry;
        var to = swell ? rest.Swollen() : rest;
        swellAimed = true;
        foreach (var spring in springs)
        {
            spring.Use(SpringPreset.Gentle);
        }

        (width.Target, depth.Target, corner.Target) = (to.Width, to.Depth, to.Corner);
        if (swell)
        {
            // The grown pill's region holds the shape on its way up and back down, so the gap above it and the room
            // beside the shadow keep passing clicks through, as at rest: a pointer that just left may click the app below.
            // It covers the resting pill too, so it can go on before the window grows.
            SetRegion(to);
            SizeWindow(Bounds(to));
        }

        Apply(Current);
        if (!islandMoving)
        {
            islandMoving = true;
            morphStartMs = clock.Elapsed.TotalMilliseconds;
            LastMorphTicks = 0;
            leadMs = 0;
        }

        Wake();
    }

    /// <summary>
    /// Send the island to a place along the top edge: on the gentle spring while it shows, at once while it is hidden
    /// or Windows animation effects are off. A drag in progress keeps it where the pointer has it.
    /// </summary>
    public void MoveTo(double offset)
    {
        if (grabMoved || offset == place.Target)
        {
            return;
        }

        place.Use(SpringPreset.Gentle);
        place.Target = offset;
        if (!visible || ForceReduceMotion || !uiSettings.AnimationsEnabled)
        {
            place.Jump(offset);
            Follow();
            LayoutChanged?.Invoke();
            return;
        }

        Wake();
    }

    /// <summary>
    /// DWM still composes the window but draws none of it, and it takes no clicks: for driven runs and measurements
    /// that must leave the screen alone. Throws when DWM refuses, so such a run never shows by accident.
    /// </summary>
    public void Cloak()
    {
        BOOL on = true;
        PInvoke.DwmSetWindowAttribute(Hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAK, &on, (uint)sizeof(BOOL)).ThrowOnFailure();
        Cloaked = true;
    }

    public void Dispose()
    {
        ticking.Reset();
        wakeTimer.Stop();
        PInvoke.DestroyWindow(Hwnd);
        instance = null;
    }

    /// <summary>
    /// The window rectangle a state's geometry rests in at the island's place, in physical pixels: the pill, the gap
    /// above it and the room its shadow takes. Desktop peek asks whether anything is under it.
    /// </summary>
    public System.Drawing.Rectangle RestBounds(IslandGeometry g)
    {
        Rescale(MonitorScale());
        var shape = attached ? g.Attached() : g;
        return IslandOutline.WindowBounds(shape, scale, IslandPlace.Center(place.Target, shape, scale, screenWidth, attached), attached);
    }

    /// <summary>Window rectangle for a geometry in physical pixels, at its place along the primary monitor's top edge.</summary>
    private System.Drawing.Rectangle Bounds(in IslandGeometry g) =>
        IslandOutline.WindowBounds(g, scale, IslandPlace.Center(place.Value, g, scale, screenWidth, attached), attached);

    // The pointer is tracked against where the shape is heading, its place included: on a slide back to the middle,
    // a pointer left where the island was is outside at once, and the island retracts as it would for any pointer
    // that left it.
    private System.Drawing.Rectangle PointerTarget() =>
        IslandOutline.PointerBounds(IslandOutline.WindowBounds(targetGeometry, scale, IslandPlace.Center(place.Target, targetGeometry, scale, screenWidth, attached), attached), scale, attached);

    /// <summary>The place moved: the window and the bounds the pointer is tracked against follow it.</summary>
    private void Follow()
    {
        TargetBounds = PointerTarget();
        if (!visible)
        {
            return;
        }

        if (islandMoving)
        {
            SizeWindow(System.Drawing.Rectangle.Union(Bounds(Current), Bounds(targetGeometry)));
            Apply(Current);
        }
        else
        {
            // At rest only the window moves; the drawn shape and its region are in window pixels and come along.
            SizeWindow(Bounds(Current));
        }
    }

    /// <summary>One step of the place's spring; true while it still moves.</summary>
    private bool Place(double dt)
    {
        if (place.Value == place.Target && place.Velocity == 0)
        {
            return false;
        }

        place.Step(dt);
        bool done = place.Settled;
        if (done)
        {
            place.Jump(place.Target);
        }

        Follow();
        if (done)
        {
            LayoutChanged?.Invoke();
        }

        return !done;
    }

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
        // Size and place only: a drag and the slide back call this per move and per frame, and the show re-asserts topmost.
        PInvoke.SetWindowPos(Hwnd, HWND.HWND_TOPMOST, r.X, 0, r.Width, r.Height, SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE | SET_WINDOW_POS_FLAGS.SWP_NOZORDER);
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

            if (contentPending && (done || Progress() >= ContentReadyAt))
            {
                RaiseContentReady();
            }
        }

        bool placing = Place(dt);
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

        // A settle can start the next motion itself (a swell asked for while the pill opened): keep ticking for it.
        if (visible && (mascotActive || placing || islandMoving))
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
        if (place.Value != place.Target)
        {
            place.Jump(place.Target);
            Follow();
            LayoutChanged?.Invoke();
        }

        // A swell has no task to complete, but its window and region still need the shape it rests on.
        if (settled is not null || swellAimed)
        {
            Settle();
            RaiseContentReady();
        }
    }

    /// <summary>How far the shape is along the current morph, by whichever of width and depth is further behind.</summary>
    private double Progress()
    {
        return Math.Min(Along(fromWidth, width), Along(fromDepth, depth));

        static double Along(double from, Spring spring)
        {
            double span = Math.Abs(spring.Target - from);
            return span < 1 ? 1 : 1 - Math.Abs(spring.Value - spring.Target) / span;
        }
    }

    private void RaiseContentReady()
    {
        if (contentPending)
        {
            contentPending = false;
            ContentReady?.Invoke();
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
            // Hidden costs nothing: no beat may wake the tick, and the next show starts from a clean tomte at its place.
            visible = false;
            wakeTimer.Stop();
            ForgetClick();
            Mascot.Park();
            place.Jump(place.Target);
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
        if (visible)
        {
            Settled?.Invoke();
            if (swell && !swellAimed)
            {
                // The pointer came to rest on the pill while it was still opening.
                AimSwell();
            }
        }
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
        Live.InsertScalar("RampW", (float)g.RampW);

        // The pill's hairline thins out with its last pixel of depth and goes where the outline has no area left,
        // so a reveal does not start as a line under the gap.
        pill.Shape.StrokeThickness = attached ? (panelLook ? 1 : 0) : g.Depth <= 0.5 ? 0 : (float)Math.Min(1, g.Depth * scale);

        // Over its last 2 DIP of depth the pill fades with the depth the springs give each frame, so a retract does
        // not end as a thin line under the gap and a reveal does not start as one.
        pill.Visual.Opacity = attached ? 1 : (float)Math.Clamp(g.Depth / 2, 0, 1);

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
        var box = MascotArt.Box(anchor ?? g);
        float s = (float)scale;
        mascotX = (float)(g.OuterWidth / 2 + box.X) * s;
        mascotVisual.Apply(Mascot, new Vector2(mascotX, box.Y * s), s);
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
        var strip = IslandOutline.DropStrip(g, scale, attached);
        if (!strip.IsEmpty)
        {
            var gap = PInvoke.CreateRectRgn(strip.Left, strip.Top, strip.Right, strip.Bottom);
            var into = new HRGN(region.DangerousGetHandle());
            PInvoke.CombineRgn(into, into, gap, RGN_COMBINE_MODE.RGN_OR);
            PInvoke.DeleteObject(gap);
        }

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
    /// Re-lay the current outline out at a new scale or screen width. WM_DPICHANGED can arrive at once,
    /// during the next SetWindowPos, or not until then, so this is idempotent and never completes a morph.
    /// A resolution change at the same DPI only moves the place, so that is checked too.
    /// </summary>
    private void Rescale(double newScale)
    {
        int width = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN);
        if (newScale == scale && width == screenWidth)
        {
            return;
        }

        (scale, screenWidth) = (newScale, width);
        ShadowLook();
        TargetBounds = PointerTarget();
        Dips.Scale = new Vector3((float)scale, (float)scale, 1);
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

    /// <summary>
    /// A point in the window's client pixels to island space, whose origin is the top of Content's centre line, rounded
    /// down to a whole pixel as the Dips container's is, so a press lands where its target is drawn.
    /// </summary>
    internal Vector2 ToIsland(int x, int y) =>
        PanelSpace.FromPixels(x, y, root.Offset.X + MathF.Floor((Content.Size.X - 2 * Margin) / 2), root.Offset.Y, scale);

    /// <summary>A point in island space to screen pixels, for input method windows beside the caret.</summary>
    internal System.Drawing.Point IslandToScreen(Vector2 island)
    {
        var point = new System.Drawing.Point(
            (int)MathF.Round(root.Offset.X + MathF.Floor((Content.Size.X - 2 * Margin) / 2) + island.X * (float)scale),
            (int)MathF.Round(root.Offset.Y + island.Y * (float)scale));
        PInvoke.ClientToScreen(Hwnd, &point);
        return point;
    }

    /// <summary>The same from screen pixels, which is where the wheel message puts its point.</summary>
    internal Vector2 ScreenToIsland(int x, int y)
    {
        var point = new System.Drawing.Point(x, y);
        PInvoke.ScreenToClient(Hwnd, &point);
        return ToIsland(point.X, point.Y);
    }

    /// <summary>
    /// A press or a wheel, handed on only when it lands on the shape as drawn now. The window also covers the
    /// pill's shadow and a row above it, so nothing that listens ever sees a point there.
    /// </summary>
    private void Pointer(uint message, nuint wParam, nint lParam)
    {
        int x = (short)(lParam & 0xFFFF), y = (short)((lParam >> 16) & 0xFFFF);
        bool wheel = message == PInvoke.WM_MOUSEWHEEL;
        // Unlike the button messages, the wheel's point is in screen pixels.
        var at = wheel ? ScreenToIsland(x, y) : ToIsland(x, y);
        if (!PanelSpace.OnShape(at, Current))
        {
            return;
        }

        if (wheel)
        {
            Wheel?.Invoke(at, (short)((wParam >> 16) & 0xFFFF));
            return;
        }

        var screen = new System.Drawing.Point(x, y);
        PInvoke.ClientToScreen(Hwnd, &screen);
        bool grabs = Grabs?.Invoke(at) == true;
        // Only a shape deeper than the strip has one.
        bool pill = IslandPlace.CountsForDoubleClick(grabs, OnMascot(at), panel: targetGeometry.Depth > PanelSpace.TabStripHeight);
        if (IslandPlace.IsDoubleClick(pill, screen.X - lastClickAt.X, screen.Y - lastClickAt.Y, Environment.TickCount64 - lastClick,
            PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXDOUBLECLK), PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYDOUBLECLK), PInvoke.GetDoubleClickTime()))
        {
            // The second click of a double click on the strip: back to the middle, and both presses are used up.
            lastClick = 0;
            MoveTo(0);
            Moved?.Invoke(0);
            return;
        }

        if (grabs)
        {
            // From where the shape it rests in may be, so an inward drag moves it at once after an earlier one pushed a
            // narrower shape further out.
            double from = IslandPlace.Clamp(place.Value, targetGeometry, scale, screenWidth, attached);
            (grabbing, grabMoved, grabPill, grabFrom, grabAt, grabOffset) = (true, false, pill, screen, at, from);
            PInvoke.SetCapture(Hwnd);
            return;
        }

        Pressed?.Invoke(at);
    }

    /// <summary>
    /// The held press: once the pointer has gone the drag distance the island follows it along the top edge, straight
    /// from the pointer with no spring, its top kept on the edge. Let go short of that, it is a click. Losing the mouse
    /// to another window ends it as a release that clicks nothing.
    /// </summary>
    private void Grab(uint message)
    {
        PInvoke.GetCursorPos(out var cursor);
        if (message == PInvoke.WM_MOUSEMOVE)
        {
            int dx = cursor.X - grabFrom.X, dy = cursor.Y - grabFrom.Y;
            if (!grabMoved)
            {
                if (!IslandPlace.IsDrag(dx, dy, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXDRAG), PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYDRAG)))
                {
                    return;
                }

                grabMoved = true;
                MoveStarted?.Invoke();
            }

            place.Jump(IslandPlace.Clamp(grabOffset + dx / scale, targetGeometry, scale, screenWidth, attached));
            Follow();
            return;
        }

        // Before the release: it sends WM_CAPTURECHANGED here at once.
        grabbing = false;
        if (message == PInvoke.WM_LBUTTONUP)
        {
            PInvoke.ReleaseCapture();
        }

        if (grabMoved)
        {
            grabMoved = false;
            LayoutChanged?.Invoke();
            Moved?.Invoke(place.Target);
        }
        else if (message == PInvoke.WM_LBUTTONUP)
        {
            // A click counts at once. Off the middle, where it may be half of a double click, it is one that does
            // nothing on its own, so nothing has to wait to see whether a second comes.
            (lastClick, lastClickAt) = (grabPill && place.Target != 0 ? Environment.TickCount64 : 0, grabFrom);
            Pressed?.Invoke(grabAt);
        }
    }

    // A click made on the state before pairs with no click on the next.
    private void ForgetClick() => lastClick = 0;

    /// <summary>Pointer input for whoever listens; like every handler here, it must not throw into the caller.</summary>
    private static LRESULT Guarded(Action<IslandWindow> action)
    {
        try
        {
            if (instance is not null)
            {
                action(instance);
            }
        }
        catch (Exception e)
        {
            instance?.Log?.Invoke($"pointer input failed: {e}");
        }

        return new LRESULT(0);
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
            case PInvoke.WM_LBUTTONDOWN:
                // The window never activates, but the mouse still reaches it. A registered target takes the press first.
                return Interact(message, wParam, lParam) ?? Guarded(i => i.Pointer(message, wParam.Value, lParam.Value));
            case PInvoke.WM_MOUSEWHEEL:
                return Guarded(i => i.Pointer(message, wParam.Value, lParam.Value));
            case PInvoke.WM_MOUSEMOVE or PInvoke.WM_LBUTTONUP or PInvoke.WM_CAPTURECHANGED when instance is { grabbing: true }:
                // Before the interaction host's case below, and it must stay before it: a target's drag (Drag) hands
                // the held press back to the host with the button still down, and the follow lives on these messages.
                return Guarded(i => i.Grab(message));
            case PInvoke.WM_MOUSELEAVE:
                return Interact(message, wParam, lParam) ?? PInvoke.DefWindowProc(window, message, wParam, lParam);
            case PInvoke.WM_SETCURSOR:
                // A text box's I-beam first, then the hand over a registered target, else the class arrow.
                return Typed(message, wParam, lParam) ?? Interact(message, wParam, lParam) ?? PInvoke.DefWindowProc(window, message, wParam, lParam);
            case PInvoke.WM_MOUSEMOVE or PInvoke.WM_LBUTTONUP or PInvoke.WM_CAPTURECHANGED:
                // The registered targets first, then a text box being drag-selected.
                return Interact(message, wParam, lParam) ?? Typed(message, wParam, lParam) ?? PInvoke.DefWindowProc(window, message, wParam, lParam);
            case PInvoke.WM_DPICHANGED:
                return GuardedRescale(((uint)(wParam.Value >> 16) & 0xFFFF) / 96.0);
            case PInvoke.WM_DISPLAYCHANGE:
                // A scaling change reaches a resting window here before its own WM_DPICHANGED does.
                return GuardedRescale(instance?.MonitorScale());
            // Who has the keys decides: a text box, or with none the registered targets, which TypingFocus hands them to.
            case PInvoke.WM_KEYDOWN or PInvoke.WM_KEYUP or PInvoke.WM_CHAR or PInvoke.WM_SYSKEYDOWN or PInvoke.WM_SYSKEYUP or PInvoke.WM_SYSCHAR:
            case >= PInvoke.WM_IME_STARTCOMPOSITION and <= PInvoke.WM_IME_COMPOSITION:
            case >= PInvoke.WM_IME_SETCONTEXT and <= PInvoke.WM_IME_KEYUP:
            case PInvoke.WM_ACTIVATE or PInvoke.WM_SETFOCUS or PInvoke.WM_KILLFOCUS:
                return Typed(message, wParam, lParam) ?? PInvoke.DefWindowProc(window, message, wParam, lParam);
            case PInvoke.WM_CLOSE:
                PInvoke.DestroyWindow(window);
                return new LRESULT(0);
            case PInvoke.WM_DESTROY:
                Route(message, wParam.Value, lParam.Value);
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

    // The text boxes' answer, or null for the next handler.
    private static LRESULT? Typed(uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            return instance?.Typing?.Invoke(message, wParam.Value, lParam.Value);
        }
        catch (Exception e)
        {
            instance?.Log?.Invoke($"typing message 0x{message:X} failed: {e}");
            return null;
        }
    }

    // The registered targets' answer, or null for the old handlers. One that failed is used up: a press must not reach
    // the old handlers as well.
    private static LRESULT? Interact(uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            // Null while the window is being made, before the host is.
            return instance?.Interaction is { } host ? host.OnMessage(message, wParam.Value, lParam.Value) : null;
        }
        catch (Exception e)
        {
            instance?.Log?.Invoke($"interaction message 0x{message:X} failed: {e}");
            return new LRESULT(0);
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

        /// <summary>The compositor's blur radius is three sigma (fitted on screen: 10.5 px for 7 DIP at 150%).</summary>
        public void Shadow(bool on, double scale)
        {
            shadowOn = on;
            shadowVisual.IsVisible = on;
            shadow.Offset = new Vector3(0, (float)(IslandOutline.ShadowOffsetY * scale), 0);
            shadow.BlurRadius = (float)(3 * IslandOutline.ShadowSigma * scale);
        }
    }
}
