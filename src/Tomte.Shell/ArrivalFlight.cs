using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tomte.Core;
using Windows.System;
using Windows.UI.Composition;
using Windows.UI.Composition.Desktop;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Shell;

/// <summary>
/// The tomte's flight into the island at launch (<see cref="Arrival"/>), in a window that exists only while it flies.
/// The island's own window is the pill's size and takes clicks; this one covers the column from the top edge down to
/// where the tomte pops in, is layered and transparent so every click goes through it, is never activated, stays out of
/// the taskbar and Alt+Tab, and is owned by the island so it draws over the pill growing under it. It never covers
/// the whole monitor, so the shell does not take it for a fullscreen app. Three springs (x, y, size) step on the
/// compositor clock in a thread of its own. Once the tomte rests where the island draws its tomte in the pill, that
/// one shows, and a frame later this window, its visuals, its class and its thread are gone.
/// </summary>
internal sealed unsafe class ArrivalFlight
{
    private const string ClassName = "TomteArrival";

    // Room around the tomte at its largest, in DIPs, for its anti-aliased edge.
    private const double Room = 4;

    private readonly IslandWindow island;
    private readonly Func<bool> openPill;
    private readonly Action ended;
    private readonly Action<string>? log;
    private readonly Spring x = new(), y = new(), size = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly DispatcherQueueHandler step;
    private readonly DesktopWindowTarget? target;
    private readonly ContainerVisual? root;
    private readonly float tomte;
    private readonly double toX, toY;
    private HWND hwnd;
    private volatile bool ending;
    private double last, longest, longestAt;
    private int frames;
    private bool setOff, pillOpen, handedOver;

    /// <param name="openPill">Asked once, as the tomte nears the edge: open the pill under it. False ends the flight there.</param>
    /// <param name="ended">
    /// The flight is over by itself: the tomte rests in the pill and the island's own has taken over, or there was no
    /// compositor clock to fly by. Not raised for <see cref="End"/> or a pill that did not open.
    /// </param>
    /// <param name="log">Gets the landing's time, frame count and longest frame, for frame-rate checks.</param>
    /// <param name="alone">The pill <paramref name="openPill"/> opens is the peek pill, the tomte alone, rather than Compact.</param>
    public ArrivalFlight(IslandWindow island, Func<bool> openPill, Action ended, Action<string>? log, bool alone)
    {
        this.island = island;
        this.openPill = openPill;
        this.ended = ended;
        this.log = log;
        step = Step;

        // In the island monitor's pixels: the middle of its work area, and where the island draws its tomte.
        var land = island.MascotLanding(alone);
        double scale = island.Scale;
        tomte = (float)(MascotArt.SizeDips * scale);
        var info = new MONITORINFO { cbSize = (uint)sizeof(MONITORINFO) };
        PInvoke.GetMonitorInfo(PInvoke.MonitorFromWindow(island.Hwnd, MONITOR_FROM_FLAGS.MONITOR_DEFAULTTOPRIMARY), ref info);
        var work = info.rcWork;
        var start = new Vector2((work.left + work.right) / 2f, (work.top + work.bottom) / 2f);
        double half = tomte * Arrival.PopScale / 2 + Room * scale;
        // Its popped size at both ends, since it lines up under its place before it has shrunk.
        int left = (int)Math.Floor(Math.Min(start.X, land.X + tomte / 2) - half);
        int right = (int)Math.Ceiling(Math.Max(start.X, land.X + tomte / 2) + half);
        int bottom = (int)Math.Ceiling(start.Y + half);

        // The springs work on the tomte's centre in window pixels; the window's top is the screen's.
        x.Jump(start.X - left);
        y.Jump(start.Y);
        (toX, toY) = (land.X - left + tomte / 2, land.Y + tomte / 2);
        size.Jump(0);
        size.Target = Arrival.PopScale;

        var hInstance = new HINSTANCE(Marshal.GetHINSTANCE(typeof(ArrivalFlight).Module));
        fixed (char* className = ClassName)
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

            hwnd = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_TOPMOST | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE
                | WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP | WINDOW_EX_STYLE.WS_EX_LAYERED | WINDOW_EX_STYLE.WS_EX_TRANSPARENT,
                className,
                null,
                WINDOW_STYLE.WS_POPUP,
                left,
                0,
                right - left,
                bottom,
                island.Hwnd,
                HMENU.Null,
                hInstance,
                null);
        }

        // From here on a failure must not leave the window or its class behind.
        try
        {
            if (hwnd == HWND.Null || !PInvoke.SetLayeredWindowAttributes(hwnd, default, 255, LAYERED_WINDOW_ATTRIBUTES_FLAGS.LWA_ALPHA))
            {
                throw new Win32Exception();
            }

            BOOL disableTransitions = true;
            PInvoke.DwmSetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_TRANSITIONS_FORCEDISABLED, &disableTransitions, (uint)sizeof(BOOL));

            // The island's tomte at rest, drawn by the island's own visuals; reduced motion keeps it still.
            var pose = new Mascot(() => 0, () => true) { CapColor = island.Mascot.CapColor };
            pose.Set(MascotState.Compact);
            var art = new MascotVisual(island.Compositor);
            art.Apply(pose, Vector2.Zero, (float)scale);
            root = island.Compositor.CreateContainerVisual();
            root.CenterPoint = new Vector3(tomte / 2, tomte / 2, 0);
            root.Children.InsertAtTop(art.Visual);
            target = IslandWindow.CreateDesktopWindowTarget(island.Compositor, hwnd);
            target.Root = root;
            Draw();
            PInvoke.ShowWindow(hwnd, SHOW_WINDOW_CMD.SW_SHOWNOACTIVATE);
        }
        catch
        {
            Destroy();
            throw;
        }

        new Thread(Clock) { IsBackground = true, Name = "tomte-arrival-clock" }.Start();
    }

    /// <summary>Ends the flight now, wherever the tomte is: the island's own tomte shows, and this window goes.</summary>
    public void End()
    {
        if (hwnd != HWND.Null)
        {
            if (pillOpen)
            {
                // A pill still held grows now, or whatever takes over moves at once.
                island.HoldNextMorph(0);
            }

            island.MascotAway = false;
            Destroy();
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT HandleMessage(HWND window, uint message, WPARAM wParam, LPARAM lParam) =>
        PInvoke.DefWindowProc(window, message, wParam, lParam);

    private void Clock()
    {
        while (!ending)
        {
            // Blocks until the next compositor frame, so the springs step at the monitor's refresh rate. A wait that
            // runs out steps too: with nothing else moving on screen the clock may not tick, and the window must still go.
            var wait = (WAIT_EVENT)PInvoke.DCompositionWaitForCompositorClock(0, null, 100);
            if (wait is WAIT_EVENT.WAIT_OBJECT_0 or WAIT_EVENT.WAIT_TIMEOUT)
            {
                island.Queue.TryEnqueue(step);
            }
            else
            {
                // No compositor clock (DWM restarting, a remote session): the flight ends rather than spins.
                ending = true;
                island.Queue.TryEnqueue(() =>
                {
                    if (hwnd != HWND.Null)
                    {
                        End();
                        ended();
                    }
                });
            }
        }
    }

    private void Step()
    {
        if (hwnd == HWND.Null)
        {
            return;
        }

        if (handedOver)
        {
            // The island's tomte has been drawn for a frame under this one; nothing shows the switch.
            Destroy();
            return;
        }

        double now = clock.Elapsed.TotalSeconds, dt = Math.Min(0.05, now - last);
        if (frames++ > 0 && now - last > longest)
        {
            (longest, longestAt) = (now - last, now);
        }

        last = now;
        if (!setOff && now >= Arrival.SetOffSeconds)
        {
            // Across and back to its pill size on the snappy spring, up on the gentle one: it lines up under its place,
            // is its own size again before it reaches the pill, and rises into it.
            setOff = true;
            x.Use(SpringPreset.Snappy);
            x.Target = toX;
            y.Use(SpringPreset.Gentle);
            y.Target = toY;
            size.Use(SpringPreset.Snappy);
            size.Target = 1;
        }

        x.Step(dt);
        y.Step(dt);
        size.Step(dt);
        if (!pillOpen && now >= Arrival.OpenPillSeconds)
        {
            // The pill opens while the tomte holds still, and its shape waits until the tomte is nearly up.
            pillOpen = true;
            island.HoldNextMorph(Arrival.PillGrowsSeconds - now);
            if (!openPill())
            {
                End();
                return;
            }

            island.MascotAway = true;
        }

        bool resting = setOff && x.Settled && y.Settled && size.Settled;
        if (resting)
        {
            x.Jump(toX);
            y.Jump(toY);
            size.Jump(1);
        }

        Draw();
        if (resting && pillOpen && !island.Moving)
        {
            handedOver = true;
            island.MascotAway = false;
            log?.Invoke(FormattableString.Invariant($"arrival: landed {now:F2}s after it showed, {frames} frames, longest {longest * 1000:F1}ms at {longestAt:F2}s"));
            ended();
        }
    }

    private void Draw()
    {
        float s = (float)size.Value;
        root!.Scale = new Vector3(s, s, 1);
        root.Offset = new Vector3((float)x.Value - tomte / 2, (float)y.Value - tomte / 2, 0);
    }

    private void Destroy()
    {
        ending = true;
        // The target lets go of the window before the window goes.
        target?.Dispose();
        root?.Dispose();
        // the tomte's parts under the root (about 45 objects) go when the GC collects their wrappers; a forced
        // GC after landing freed nothing measurable. Give MascotVisual a Dispose if a measurement ever shows them.
        if (hwnd != HWND.Null)
        {
            PInvoke.DestroyWindow(hwnd);
            hwnd = HWND.Null;
        }

        fixed (char* className = ClassName)
        {
            PInvoke.UnregisterClass(className, new HINSTANCE(Marshal.GetHINSTANCE(typeof(ArrivalFlight).Module)));
        }
    }
}
