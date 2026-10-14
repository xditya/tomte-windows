using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tomte.Core.Triggers;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.StationsAndDesktops;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Accessibility;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Shell;

/// <summary>
/// What is happening on the primary monitor, from events only: the shell's own fullscreen notification through
/// a space-free appbar, foreground and location changes through WinEvent hooks, lock and unlock through WTS,
/// the secure desktop through the desktop-switch event. SHQueryUserNotificationState is read on a pool thread
/// after the appbar notification, never inside a callback, because it blocks for up to a second during a
/// D3D mode switch. Everything runs on the UI thread, which owns the island window.
/// </summary>
internal sealed unsafe class DesktopWatch : IDisposable
{
    public const uint MsgAppBar = PInvoke.WM_APP + 20;
    private const nuint NotificationStateTimer = 20;

    // QUNS_RUNNING_D3D_FULL_SCREEN is set between 0.1 and 1.4 s after the shell's own fullscreen notification, so
    // one more read follows a fullscreen entry after this delay. A one-shot, not a poll.
    private const uint NotificationStateRecheckMs = 1500;

    private static DesktopWatch? instance;
    private readonly HWND island;
    private readonly Func<bool> needsForegroundExe;
    private readonly Windows.System.DispatcherQueue queue;
    private readonly HWINEVENTHOOK[] hooks;
    private APPBARDATA appBar;
    private HWINEVENTHOOK locationHook;
    private uint locationPid;
    private bool shellFullscreen, rectFullscreen, qunsBusy, qunsExclusive, qunsQuiet, locked, secureDesktop;
    private string? foregroundExe;

    public DesktopWatch(HWND island, Windows.System.DispatcherQueue queue, Func<bool> needsForegroundExe)
    {
        instance = this;
        this.island = island;
        this.queue = queue;
        this.needsForegroundExe = needsForegroundExe;

        // ABM_NEW with no ABM_SETPOS reserves no space; it only subscribes to ABN_FULLSCREENAPP.
        appBar = new APPBARDATA { cbSize = (uint)sizeof(APPBARDATA), hWnd = island, uCallbackMessage = MsgAppBar };
        PInvoke.SHAppBarMessage(PInvoke.ABM_NEW, ref appBar);
        PInvoke.WTSRegisterSessionNotification(island, PInvoke.NOTIFY_FOR_THIS_SESSION);

        const uint flags = PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS;
        hooks =
        [
            PInvoke.SetWinEventHook(PInvoke.EVENT_SYSTEM_FOREGROUND, PInvoke.EVENT_SYSTEM_FOREGROUND, HMODULE.Null, &OnWinEvent, 0, 0, flags),
            PInvoke.SetWinEventHook(PInvoke.EVENT_SYSTEM_MINIMIZESTART, PInvoke.EVENT_SYSTEM_MINIMIZEEND, HMODULE.Null, &OnWinEvent, 0, 0, flags),
            PInvoke.SetWinEventHook(PInvoke.EVENT_SYSTEM_DESKTOPSWITCH, PInvoke.EVENT_SYSTEM_DESKTOPSWITCH, HMODULE.Null, &OnWinEvent, 0, 0, flags),
        ];

        Refresh();
        ReadNotificationState();
    }

    /// <summary>The suppression inputs changed.</summary>
    public event Action? Changed;

    /// <summary>A window came to the front, was minimised or restored, or the foreground window moved.</summary>
    public event Action? WindowsChanged;

    public SuppressionState State { get; private set; } = SuppressionState.Clear;

    /// <summary>Re-reads the foreground window, for example after the app rules changed.</summary>
    public void Refresh() => OnForeground(PInvoke.GetForegroundWindow());

    /// <summary>Appbar and session messages arrive on the island window; the owner routes them here.</summary>
    public bool HandleMessage(uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case MsgAppBar when wParam == PInvoke.ABN_FULLSCREENAPP:
                shellFullscreen = lParam != 0;
                Publish();
                ReadNotificationState();
                return true;
            case PInvoke.WM_WTSSESSION_CHANGE when wParam is PInvoke.WTS_SESSION_LOCK or PInvoke.WTS_SESSION_UNLOCK:
                locked = wParam == PInvoke.WTS_SESSION_LOCK;
                Publish();
                return true;
            case PInvoke.WM_TIMER when wParam == NotificationStateTimer:
                PInvoke.KillTimer(island, NotificationStateTimer);
                ReadNotificationState();
                return true;
            default:
                return false;
        }
    }

    /// <summary>
    /// Reads SHQueryUserNotificationState on a pool thread and folds the result in on the UI thread, then runs
    /// <paramref name="then"/> there. Quiet hours and Focus have no event of their own, so an event asks for a read.
    /// </summary>
    public void ReadNotificationState(Action? then = null)
    {
        _ = Task.Run(() =>
        {
            PInvoke.SHQueryUserNotificationState(out var state);
            queue.TryEnqueue(() =>
            {
                qunsExclusive = state == QUERY_USER_NOTIFICATION_STATE.QUNS_RUNNING_D3D_FULL_SCREEN;
                qunsBusy = state is QUERY_USER_NOTIFICATION_STATE.QUNS_BUSY or QUERY_USER_NOTIFICATION_STATE.QUNS_PRESENTATION_MODE;
                qunsQuiet = state == QUERY_USER_NOTIFICATION_STATE.QUNS_QUIET_TIME;
                Publish();
                then?.Invoke();
            });
        });
    }

    /// <summary>
    /// Desktop peek's question: does any visible, non-minimised, uncloaked top-level window other than the
    /// desktop, the taskbar and tool windows intersect the rectangle the pill would occupy? Returns the class of
    /// the first window that does, or null when nothing is under the pill.
    /// </summary>
    public string? WindowUnder(System.Drawing.Rectangle pill)
    {
        var probe = (Zone: new RECT { left = pill.Left, top = pill.Top, right = pill.Right, bottom = pill.Bottom }, Island: island, Covering: HWND.Null);
        PInvoke.EnumWindows(&CheckWindow, (nint)(&probe));
        if (probe.Covering.IsNull)
        {
            return null;
        }

        Span<char> name = stackalloc char[64];
        return name[..PInvoke.GetClassName(probe.Covering, name)].ToString();
    }

    public void Dispose()
    {
        foreach (var hook in hooks.Append(locationHook))
        {
            PInvoke.UnhookWinEvent(hook);
        }

        PInvoke.SHAppBarMessage(PInvoke.ABM_REMOVE, ref appBar);
        PInvoke.WTSUnRegisterSessionNotification(island);
        instance = null;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static BOOL CheckWindow(HWND hwnd, LPARAM lParam)
    {
        ref var probe = ref *((RECT Zone, HWND Island, HWND Covering)*)lParam.Value;
        if (hwnd == probe.Island || !PInvoke.IsWindowVisible(hwnd) || PInvoke.IsIconic(hwnd))
        {
            return true;
        }

        // Tool windows are overlays and palettes, not something the user works in: the shell's own monitor-sized
        // CopilotKeyPressFocusFrame is one, and it would otherwise keep peek off for good.
        if (((WINDOW_EX_STYLE)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE)).HasFlag(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW))
        {
            return true;
        }

        uint cloaked = 0;
        PInvoke.DwmGetWindowAttribute(hwnd, DWMWINDOWATTRIBUTE.DWMWA_CLOAKED, &cloaked, sizeof(uint));
        if (cloaked != 0)
        {
            return true;
        }

        var buffer = stackalloc char[64];
        var name = new ReadOnlySpan<char>(buffer, PInvoke.GetClassName(hwnd, buffer, 64));
        if (IsDesktop(name) || name.SequenceEqual("Shell_TrayWnd") || name.SequenceEqual("Shell_SecondaryTrayWnd"))
        {
            return true;
        }

        PInvoke.GetWindowRect(hwnd, out var r);
        if (r.left < probe.Zone.right && r.right > probe.Zone.left && r.top < probe.Zone.bottom && r.bottom > probe.Zone.top)
        {
            probe.Covering = hwnd;
            return false;
        }

        return true;
    }

    private static bool IsDesktop(ReadOnlySpan<char> className) => className.SequenceEqual("Progman") || className.SequenceEqual("WorkerW");

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static void OnWinEvent(HWINEVENTHOOK hook, uint evt, HWND hwnd, int idObject, int idChild, uint thread, uint time)
    {
        // An exception leaving an unmanaged callback ends the process.
        try
        {
            instance?.HandleWinEvent(evt, hwnd, idObject, idChild);
        }
        catch (Exception)
        {
        }
    }

    private void HandleWinEvent(uint evt, HWND hwnd, int idObject, int idChild)
    {
        switch (evt)
        {
            case PInvoke.EVENT_SYSTEM_FOREGROUND:
                OnForeground(hwnd);
                WindowsChanged?.Invoke();
                break;
            case PInvoke.EVENT_OBJECT_LOCATIONCHANGE:
                if (idObject == (int)OBJECT_IDENTIFIER.OBJID_WINDOW && idChild == 0 && hwnd == PInvoke.GetForegroundWindow())
                {
                    SetRectFullscreen(hwnd);
                    WindowsChanged?.Invoke();
                }

                break;
            case PInvoke.EVENT_SYSTEM_DESKTOPSWITCH:
                secureDesktop = !OnDefaultDesktop();
                Publish();
                break;
            default:
                WindowsChanged?.Invoke();
                break;
        }
    }

    private void OnForeground(HWND foreground)
    {
        uint pid;
        PInvoke.GetWindowThreadProcessId(foreground, &pid);
        if (pid != locationPid)
        {
            // Location changes fire for every caret and cursor move in every process; only the foreground process is watched.
            PInvoke.UnhookWinEvent(locationHook);
            locationPid = pid;
            locationHook = PInvoke.SetWinEventHook(PInvoke.EVENT_OBJECT_LOCATIONCHANGE, PInvoke.EVENT_OBJECT_LOCATIONCHANGE, HMODULE.Null, &OnWinEvent, pid, 0, PInvoke.WINEVENT_OUTOFCONTEXT | PInvoke.WINEVENT_SKIPOWNPROCESS);
        }

        // Looked up each time: process ids are reused, so a cache would hand a closed game's rule to the next app.
        foregroundExe = needsForegroundExe() ? ExeName(pid) : null;
        secureDesktop = !OnDefaultDesktop();
        SetRectFullscreen(foreground);
    }

    /// <summary>
    /// The client rect equals the primary monitor: borderless games and fullscreen video. Equality, never
    /// containment: a maximized window overhangs the monitor. The desktop itself is monitor-sized and comes to
    /// the front after Win+D; it is not a fullscreen app.
    /// </summary>
    private void SetRectFullscreen(HWND foreground)
    {
        PInvoke.GetClientRect(foreground, out var client);
        var origin = default(System.Drawing.Point);
        PInvoke.ClientToScreen(foreground, &origin);
        var className = stackalloc char[64];
        rectFullscreen = origin.X == 0 && origin.Y == 0
            && client.right == PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN)
            && client.bottom == PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYSCREEN)
            && !IsDesktop(new ReadOnlySpan<char>(className, PInvoke.GetClassName(foreground, className, 64)));
        Publish();
    }

    private void Publish()
    {
        var next = new SuppressionState
        {
            Fullscreen = shellFullscreen || rectFullscreen || qunsBusy || qunsExclusive,
            ExclusiveFullscreen = qunsExclusive,
            Secure = locked || secureDesktop,
            Quiet = qunsQuiet,
            ForegroundExe = foregroundExe,
        };
        if (next == State)
        {
            return;
        }

        if (next.Fullscreen && !State.Fullscreen)
        {
            PInvoke.SetTimer(island, NotificationStateTimer, NotificationStateRecheckMs, null);
        }

        State = next;
        Changed?.Invoke();
    }

    private static bool OnDefaultDesktop()
    {
        using var desktop = PInvoke.OpenInputDesktop_SafeHandle(0, false, DESKTOP_ACCESS_FLAGS.DESKTOP_READOBJECTS);
        if (desktop.IsInvalid)
        {
            return false;
        }

        var name = stackalloc char[32];
        uint needed;
        return PInvoke.GetUserObjectInformation((HANDLE)desktop.DangerousGetHandle(), USER_OBJECT_INFORMATION_INDEX.UOI_NAME, name, 64, &needed)
            && new ReadOnlySpan<char>(name, (int)needed / 2 - 1).SequenceEqual("Default");
    }

    private static string? ExeName(uint pid)
    {
        using var process = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        if (process.IsInvalid)
        {
            return null;
        }

        Span<char> buffer = stackalloc char[1024];
        uint length = 1024;
        return PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, buffer, ref length)
            ? Path.GetFileName(buffer[..(int)length].ToString())
            : null;
    }
}
