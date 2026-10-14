using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tomte.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Shell;

/// <summary>
/// The one notification-area icon and its menu. Windows 11 puts a new icon in the overflow, so it
/// takes no visible space. A hidden top-level window (never shown) receives the icon's callbacks
/// and the TaskbarCreated broadcast, which a message-only window would miss after Explorer restarts.
/// </summary>
internal sealed unsafe class TrayIcon : IDisposable
{
    private const uint CallbackMessage = PInvoke.WM_APP + 1;
    private const uint IconId = 1;
    private const uint KeySelect = PInvoke.NIN_SELECT | 1; // NIN_KEYSELECT: NIN_SELECT with NINF_KEY
    private const nuint PauseTimer = 1;

    private const int OpenItem = 1;
    private const int SettingsItem = 2;
    private const int PauseHourItem = 3;
    private const int PauseRestartItem = 4;
    private const int QuitItem = 5;
    private const int TimerStopItem = 6;

    // The timer menu's lengths in minutes; item ids start at TimerFirstItem in this order.
    private const int TimerFirstItem = 10;
    private static readonly int[] TimerMinutes = [5, 10, 15, 25, 60];

    private static TrayIcon? instance;
    private static uint taskbarCreated;

    private readonly HWND hwnd;
    private readonly PauseState pause;
    private readonly IslandTimer timer;
    private HICON icon;
    private bool menuOpen;

    public TrayIcon(PauseState pause, IslandTimer timer)
    {
        instance = this;
        this.pause = pause;
        this.timer = timer;
        taskbarCreated = PInvoke.RegisterWindowMessage("TaskbarCreated");
        var hInstance = new HINSTANCE(Marshal.GetHINSTANCE(typeof(TrayIcon).Module));
        fixed (char* className = "TomteTray")
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

            hwnd = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW, className, null, WINDOW_STYLE.WS_POPUP, 0, 0, 0, 0, HWND.Null, HMENU.Null, hInstance, null);
            if (hwnd == HWND.Null)
            {
                throw new Win32Exception();
            }
        }

        Add();
    }

    public event Action? OpenIsland;

    public event Action? OpenSettings;

    /// <summary>Raised after either pause item starts a pause; the island should retract.</summary>
    public event Action? Paused;

    public event Action? Quit;

    public void Dispose()
    {
        var data = Data(0);
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_DELETE, &data);
        DestroyIcon();
        PInvoke.DestroyWindow(hwnd);
    }

    private NOTIFYICONDATAW Data(NOTIFY_ICON_DATA_FLAGS flags) => new()
    {
        cbSize = (uint)sizeof(NOTIFYICONDATAW),
        hWnd = hwnd,
        uID = IconId,
        uFlags = flags,
        uCallbackMessage = CallbackMessage,
        hIcon = icon,
        szTip = pause.Describe(DateTimeOffset.Now),
    };

    private void Add()
    {
        LoadIcon();
        var data = Data(NOTIFY_ICON_DATA_FLAGS.NIF_MESSAGE | NOTIFY_ICON_DATA_FLAGS.NIF_ICON | NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP);
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_ADD, &data);

        // Version 4: the callback carries the event in the low word and the anchor point in wParam, and keyboard selection works.
        data.Anonymous.uVersion = PInvoke.NOTIFYICON_VERSION_4;
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_SETVERSION, &data);
    }

    private void Update(NOTIFY_ICON_DATA_FLAGS flags)
    {
        var data = Data(flags);
        PInvoke.Shell_NotifyIcon(NOTIFY_ICON_MESSAGE.NIM_MODIFY, &data);
    }

    // The small-icon size at the window's DPI, read from the multi-size .ico so it is never scaled up.
    private void LoadIcon()
    {
        DestroyIcon();
        uint dpi = PInvoke.GetDpiForWindow(hwnd);
        int size = PInvoke.GetSystemMetricsForDpi(SYSTEM_METRICS_INDEX.SM_CXSMICON, dpi);
        string path = Path.Combine(AppContext.BaseDirectory, "Assets", "tomte.ico");
        fixed (char* file = path)
        {
            var handle = PInvoke.LoadImage(HINSTANCE.Null, file, GDI_IMAGE_TYPE.IMAGE_ICON, size, size, IMAGE_FLAGS.LR_LOADFROMFILE);
            icon = new HICON(handle.Value);
        }
    }

    private void DestroyIcon()
    {
        if (icon != HICON.Null)
        {
            PInvoke.DestroyIcon(icon);
            icon = HICON.Null;
        }
    }

    private void ShowMenu(int x, int y)
    {
        // Enter sends NIN_KEYSELECT twice; the second arrives inside TrackPopupMenu's own loop.
        if (menuOpen)
        {
            return;
        }

        menuOpen = true;
        try
        {
            ShowMenuCore(x, y);
        }
        finally
        {
            menuOpen = false;
        }
    }

    private void ShowMenuCore(int x, int y)
    {
        var now = DateTimeOffset.Now;
        bool timed = pause.IsPaused(now) && !pause.UntilRestart;
        var menu = PInvoke.CreatePopupMenu();
        Append(menu, MENU_ITEM_FLAGS.MF_STRING, OpenItem, "Open island");
        Append(menu, MENU_ITEM_FLAGS.MF_STRING, SettingsItem, "Settings");
        Append(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, null);
        // A timer with no AI: the same countdown the palette's set_timer starts, with its ring and its done event.
        var timers = PInvoke.CreatePopupMenu();
        foreach (var (minutes, i) in TimerMinutes.Select((m, i) => (m, i)))
        {
            Append(timers, MENU_ITEM_FLAGS.MF_STRING, TimerFirstItem + i, minutes < 60 ? $"{minutes} minutes" : "1 hour");
        }

        if (timer.DueIn(now) is { } left)
        {
            Append(timers, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, null);
            Append(timers, MENU_ITEM_FLAGS.MF_STRING, TimerStopItem, $"Stop timer ({Math.Ceiling(left.TotalMinutes)} min left)");
        }

        fixed (char* label = "Timer")
        {
            // The submenu is destroyed with the menu.
            PInvoke.AppendMenu(menu, MENU_ITEM_FLAGS.MF_POPUP, (nuint)timers.Value, label);
        }

        Append(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, null);
        Append(menu, MENU_ITEM_FLAGS.MF_STRING | (timed ? MENU_ITEM_FLAGS.MF_CHECKED : 0), PauseHourItem, timed ? $"Paused until {pause.Until!.Value.ToLocalTime():HH:mm}" : "Pause for 1 hour");
        Append(menu, MENU_ITEM_FLAGS.MF_STRING | (pause.UntilRestart ? MENU_ITEM_FLAGS.MF_CHECKED : 0), PauseRestartItem, "Pause until restart");
        Append(menu, MENU_ITEM_FLAGS.MF_SEPARATOR, 0, null);
        Append(menu, MENU_ITEM_FLAGS.MF_STRING, QuitItem, "Quit");

        // Without the foreground the menu would not close when the user clicks elsewhere; WM_NULL is the documented follow-up.
        PInvoke.SetForegroundWindow(hwnd);
        int item = PInvoke.TrackPopupMenu(menu, TRACK_POPUP_MENU_FLAGS.TPM_RETURNCMD | TRACK_POPUP_MENU_FLAGS.TPM_RIGHTBUTTON | TRACK_POPUP_MENU_FLAGS.TPM_BOTTOMALIGN, x, y, 0, hwnd, null).Value;
        PInvoke.PostMessage(hwnd, PInvoke.WM_NULL, 0, 0);
        PInvoke.DestroyMenu(menu);
        Choose(item);
    }

    private static void Append(HMENU menu, MENU_ITEM_FLAGS flags, int id, string? text)
    {
        fixed (char* p = text)
        {
            PInvoke.AppendMenu(menu, flags, (nuint)id, p);
        }
    }

    private void Choose(int item)
    {
        var now = DateTimeOffset.Now;
        switch (item)
        {
            case OpenItem:
                OpenIsland?.Invoke();
                break;
            case SettingsItem:
                OpenSettings?.Invoke();
                break;
            case PauseHourItem when pause.IsPaused(now) && !pause.UntilRestart:
            case PauseRestartItem when pause.UntilRestart:
                // Choosing the checked item again resumes.
                pause.Resume();
                PInvoke.KillTimer(hwnd, PauseTimer);
                Update(NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP);
                break;
            case PauseHourItem:
                pause.PauseFor(TimeSpan.FromHours(1), now);
                // One timer for the expiry: it ends the pause, so events that waited show, and refreshes the tooltip.
                PInvoke.SetTimer(hwnd, PauseTimer, (uint)TimeSpan.FromHours(1).TotalMilliseconds, null);
                Update(NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP);
                Paused?.Invoke();
                break;
            case PauseRestartItem:
                pause.PauseUntilRestart();
                PInvoke.KillTimer(hwnd, PauseTimer);
                Update(NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP);
                Paused?.Invoke();
                break;
            case QuitItem:
                Quit?.Invoke();
                break;
            case TimerStopItem:
                timer.Run = null;
                break;
            case >= TimerFirstItem when item - TimerFirstItem < TimerMinutes.Length:
                timer.Run = TimerRun.Starting(now, TimeSpan.FromMinutes(TimerMinutes[item - TimerFirstItem]));
                break;
        }
    }

    private LRESULT OnMessage(uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == CallbackMessage)
        {
            uint notification = (uint)(lParam.Value & 0xFFFF);
            if (notification is PInvoke.WM_CONTEXTMENU or PInvoke.NIN_SELECT or KeySelect)
            {
                ShowMenu((short)(wParam.Value & 0xFFFF), (short)((wParam.Value >> 16) & 0xFFFF));
            }

            return new LRESULT(0);
        }

        if (message == taskbarCreated)
        {
            // Explorer restarted and forgot the icon.
            Add();
            return new LRESULT(0);
        }

        switch (message)
        {
            case PInvoke.WM_TIMER when wParam.Value == PauseTimer:
                PInvoke.KillTimer(hwnd, PauseTimer);
                // Every other pause choice kills or restarts this one-shot, so the hour is what ran out. A one-shot can
                // fire a few milliseconds early; ending the pause here rather than asking IsPaused keeps that from
                // leaving the waiting events stuck.
                pause.Resume();
                Update(NOTIFY_ICON_DATA_FLAGS.NIF_TIP | NOTIFY_ICON_DATA_FLAGS.NIF_SHOWTIP);
                return new LRESULT(0);
            case PInvoke.WM_DPICHANGED:
            case PInvoke.WM_DISPLAYCHANGE:
                LoadIcon();
                Update(NOTIFY_ICON_DATA_FLAGS.NIF_ICON);
                return new LRESULT(0);
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT HandleMessage(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        // A managed exception leaving this callback would end the process.
        try
        {
            return instance is { } tray && tray.hwnd == window ? tray.OnMessage(message, wParam, lParam) : PInvoke.DefWindowProc(window, message, wParam, lParam);
        }
        catch (Exception)
        {
            return PInvoke.DefWindowProc(window, message, wParam, lParam);
        }
    }
}
