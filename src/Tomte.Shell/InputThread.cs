using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tomte.Core.Triggers;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Input;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Shell;

/// <summary>
/// The dedicated input thread: a message-only window that receives raw mouse input (edge push, pointer
/// tracking for retraction), the WH_MOUSE_LL hook (drag candidates), the registered hotkeys and the raw
/// keyboard input armed while the Rambler chord is held. Everything here is a few field reads and a
/// PostMessage to the island window; nothing logs or allocates. The UI thread writes the zone and the
/// island bounds into the shared fields below.
/// </summary>
internal static unsafe class InputThread
{
    // Messages posted to the island window. wParam and lParam carry what the name says.
    public const uint MsgEdgeReveal = PInvoke.WM_APP + 1;       // wParam 1 = dwell, 0 = push
    public const uint MsgPointer = PInvoke.WM_APP + 2;          // wParam 1 = inside the island bounds, 0 = outside; lParam = packed x, y
    public const uint MsgDragCandidate = PInvoke.WM_APP + 3;    // lParam = packed x, y of the button-down point
    public const uint MsgDragInZone = PInvoke.WM_APP + 4;       // lParam = packed x, y
    public const uint MsgDragEnd = PInvoke.WM_APP + 5;
    public const uint MsgHotkey = PInvoke.WM_APP + 6;           // wParam = hotkey id
    public const uint MsgHotkeyReleased = PInvoke.WM_APP + 7;   // the Rambler chord came up

    public const int EscapeHotkeyId = 100;
    private const uint MsgSetEscape = PInvoke.WM_APP + 10;      // posted to the input window: wParam 1 registers Esc, 0 releases it
    private const nuint DwellTimer = 1;
    private const ushort VkEscape = 0x1B;
    private static readonly HWND MessageOnlyParent = new(-3); // HWND_MESSAGE, which CsWin32 does not emit

    private static HWND island, window;
    private static HHOOK hook;
    private static EdgePushDetector detector = null!;
    private static TriggerSettings settings = null!;
    private static readonly ManualResetEventSlim ready = new(false);
    private static readonly List<HotkeyRegistration> registrations = [];
    private static readonly Stopwatch clock = Stopwatch.StartNew();

    // Shared with the UI thread. Written there, read here; a torn read costs one wrong pointer sample.
    private static int zoneLeft, zoneRight, zoneTop, bandBottom;
    private static int boundsLeft, boundsTop, boundsRight, boundsBottom, boundsMargin, boundsVersion;
    private static int trackPointer, dragArmed;

    // Input thread only.
    private static int seenBoundsVersion = -1;
    private static bool pointerInside;
    private static int dragX, dragY, downX, downY;
    private static bool buttonDown, candidate, rawKeyboardArmed;

    // Callback timing, read by the UI thread at exit.
    public static long HookCalls, HookTicksMax, HookTicksSum, InputCalls, InputTicksMax, InputTicksSum;

    public static IReadOnlyList<HotkeyRegistration> Registrations => registrations;

    public static bool HookInstalled => !hook.IsNull;

    /// <summary>Starts the thread and returns once the hotkeys are registered, so the conflict report is ready.</summary>
    public static void Start(HWND islandWindow, TriggerSettings triggerSettings)
    {
        island = islandWindow;
        settings = triggerSettings;
        detector = new EdgePushDetector(settings.PushCounts, settings.PushWindowMs);
        dragX = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXDRAG);
        dragY = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYDRAG);
        // The first WM_INPUT otherwise pays the JIT of the whole path (1.8 ms measured).
        RuntimeHelpers.PrepareMethod(typeof(InputThread).GetMethod(nameof(OnMouseInput), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.MethodHandle);
        RuntimeHelpers.PrepareMethod(typeof(EdgePushDetector).GetMethod(nameof(EdgePushDetector.Feed))!.MethodHandle);
        new Thread(Loop) { IsBackground = true, Name = "tomte-input" }.Start();
        ready.Wait();
    }

    public static void Stop() => PInvoke.PostMessage(window, PInvoke.WM_CLOSE, default, default);

    public static void SetZone(RevealZone zone)
    {
        Volatile.Write(ref zoneLeft, zone.Left);
        Volatile.Write(ref zoneRight, zone.Right);
        Volatile.Write(ref zoneTop, zone.Top);
        Volatile.Write(ref bandBottom, zone.DragBandBottom);
    }

    /// <summary>Island bounds in physical pixels to track the pointer against; the next sample reports inside or outside.</summary>
    public static void TrackPointer(System.Drawing.Rectangle bounds, int margin)
    {
        boundsLeft = bounds.Left;
        boundsTop = bounds.Top;
        boundsRight = bounds.Right;
        boundsBottom = bounds.Bottom;
        boundsMargin = margin;
        Volatile.Write(ref boundsVersion, boundsVersion + 1);
        Volatile.Write(ref trackPointer, 1);
    }

    public static void StopTrackingPointer() => Volatile.Write(ref trackPointer, 0);

    /// <summary>The button-down window said HTCLIENT: the next move into the drag band inside the zone reports once.</summary>
    public static void ArmDrag() => Volatile.Write(ref dragArmed, 1);

    public static void SetEscape(bool on) => PInvoke.PostMessage(window, MsgSetEscape, (nuint)(on ? 1 : 0), 0);

    public static (int X, int Y) Unpack(LPARAM packed) => ((short)(packed.Value & 0xFFFF), (short)((packed.Value >> 16) & 0xFFFF));

    private static LPARAM Pack(int x, int y) => (nint)(((y & 0xFFFF) << 16) | (x & 0xFFFF));

    private static void Loop()
    {
        var instance = new HINSTANCE(Marshal.GetHINSTANCE(typeof(InputThread).Module));
        fixed (char* className = "TomteInput")
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &HandleMessage,
                hInstance = instance,
                lpszClassName = className,
            };
            if (PInvoke.RegisterClassEx(in windowClass) == 0)
            {
                throw new Win32Exception();
            }

            window = PInvoke.CreateWindowEx(0, className, null, 0, 0, 0, 0, 0, MessageOnlyParent, HMENU.Null, instance, null);
            if (window == HWND.Null)
            {
                throw new Win32Exception();
            }
        }

        RegisterRaw(PInvoke.HID_USAGE_GENERIC_MOUSE, true);
        if (settings.DragApproaching)
        {
            hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, &MouseHook, HINSTANCE.Null, 0);
        }

        foreach (var (action, chord) in settings.Hotkeys)
        {
            bool ok = PInvoke.RegisterHotKey(window, (int)action + 1, (HOT_KEY_MODIFIERS)chord.Modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT, chord.VirtualKey);
            registrations.Add(new HotkeyRegistration(action, chord, ok ? 0 : Marshal.GetLastPInvokeError()));
        }

        ready.Set();
        MSG message;
        while (PInvoke.GetMessage(&message, HWND.Null, 0, 0).Value > 0)
        {
            PInvoke.DispatchMessage(&message);
        }
    }

    private static void RegisterRaw(ushort usage, bool on)
    {
        var device = new RAWINPUTDEVICE
        {
            usUsagePage = PInvoke.HID_USAGE_PAGE_GENERIC,
            usUsage = usage,
            dwFlags = on ? RAWINPUTDEVICE_FLAGS.RIDEV_INPUTSINK : RAWINPUTDEVICE_FLAGS.RIDEV_REMOVE,
            hwndTarget = on ? window : HWND.Null,
        };
        PInvoke.RegisterRawInputDevices(&device, 1, (uint)sizeof(RAWINPUTDEVICE));
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT HandleMessage(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case PInvoke.WM_INPUT:
                long t0 = Stopwatch.GetTimestamp();
                OnInput(new HRAWINPUT(lParam.Value));
                long ticks = Stopwatch.GetTimestamp() - t0;
                InputCalls++;
                InputTicksSum += ticks;
                if (ticks > InputTicksMax)
                {
                    InputTicksMax = ticks;
                }

                break; // DefWindowProc frees the input buffer
            case PInvoke.WM_TIMER when wParam.Value == DwellTimer:
                PInvoke.KillTimer(hwnd, DwellTimer);
                if (detector.DwellElapsed() == EdgePushDetector.Event.Reveal)
                {
                    PInvoke.PostMessage(island, MsgEdgeReveal, 1, 0);
                }

                return new LRESULT(0);
            case PInvoke.WM_HOTKEY:
                PInvoke.PostMessage(island, MsgHotkey, wParam, 0);
                if (wParam.Value == (nuint)HotkeyAction.Rambler + 1 && !rawKeyboardArmed)
                {
                    // Hold-to-talk: RegisterHotKey gives the press but not the release; raw keyboard input armed only
                    // while the chord is down sees the first key-up, with no keyboard hook.
                    rawKeyboardArmed = true;
                    RegisterRaw(PInvoke.HID_USAGE_GENERIC_KEYBOARD, true);
                }

                return new LRESULT(0);
            case MsgSetEscape:
                if (wParam.Value == 1)
                {
                    PInvoke.RegisterHotKey(hwnd, EscapeHotkeyId, HOT_KEY_MODIFIERS.MOD_NOREPEAT, VkEscape);
                }
                else
                {
                    PInvoke.UnregisterHotKey(hwnd, EscapeHotkeyId);
                }

                return new LRESULT(0);
            case PInvoke.WM_CLOSE:
                foreach (var r in registrations)
                {
                    PInvoke.UnregisterHotKey(hwnd, (int)r.Action + 1);
                }

                PInvoke.UnregisterHotKey(hwnd, EscapeHotkeyId);
                if (!hook.IsNull)
                {
                    PInvoke.UnhookWindowsHookEx(hook);
                }

                PInvoke.DestroyWindow(hwnd);
                return new LRESULT(0);
            case PInvoke.WM_DESTROY:
                PInvoke.PostQuitMessage(0);
                return new LRESULT(0);
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    private static void OnInput(HRAWINPUT handle)
    {
        RAWINPUT raw;
        uint size = (uint)sizeof(RAWINPUT);
        if (PInvoke.GetRawInputData(handle, RAW_INPUT_DATA_COMMAND_FLAGS.RID_INPUT, &raw, &size, (uint)sizeof(RAWINPUTHEADER)) == unchecked((uint)-1))
        {
            return;
        }

        if (raw.header.dwType == (uint)RID_DEVICE_INFO_TYPE.RIM_TYPEMOUSE)
        {
            OnMouseInput(in raw.data.mouse);
        }
        else if (raw.header.dwType == (uint)RID_DEVICE_INFO_TYPE.RIM_TYPEKEYBOARD && rawKeyboardArmed && (raw.data.keyboard.Flags & PInvoke.RI_KEY_BREAK) != 0)
        {
            // V, Win or Alt up: the chord is over, raw keyboard input goes away again.
            ushort vk = raw.data.keyboard.VKey;
            if (vk is 0x56 or 0x5B or 0x5C or 0x12 or 0xA4 or 0xA5)
            {
                rawKeyboardArmed = false;
                RegisterRaw(PInvoke.HID_USAGE_GENERIC_KEYBOARD, false);
                PInvoke.PostMessage(island, MsgHotkeyReleased, 0, 0);
            }
        }
    }

    private static void OnMouseInput(in RAWMOUSE mouse)
    {
        System.Drawing.Point cursor;
        PInvoke.GetCursorPos(&cursor);
        int top = Volatile.Read(ref zoneTop);
        bool atTop = cursor.Y == top;
        bool inZone = atTop && cursor.X >= Volatile.Read(ref zoneLeft) && cursor.X <= Volatile.Read(ref zoneRight)
            // Asked only on the top row: a monitor above this x means there is no edge to push against.
            && PInvoke.MonitorFromPoint(new System.Drawing.Point(cursor.X, top - 1), MONITOR_FROM_FLAGS.MONITOR_DEFAULTTONULL) == HMONITOR.Null;
        // Absolute devices (pen, RDP) carry no push; they still get the dwell.
        int dy = (mouse.usFlags & MOUSE_STATE.MOUSE_MOVE_ABSOLUTE) != 0 ? 0 : mouse.lLastY;
        if (settings.EdgePush)
        {
            switch (detector.Feed(clock.Elapsed.TotalMilliseconds, dy, atTop, inZone))
            {
                case EdgePushDetector.Event.EnterZone:
                    PInvoke.SetTimer(window, DwellTimer, (uint)settings.DwellMs, null);
                    break;
                case EdgePushDetector.Event.LeaveZone:
                    PInvoke.KillTimer(window, DwellTimer);
                    break;
                case EdgePushDetector.Event.Reveal:
                    PInvoke.KillTimer(window, DwellTimer);
                    PInvoke.PostMessage(island, MsgEdgeReveal, 0, 0);
                    break;
            }
        }

        if (Volatile.Read(ref trackPointer) == 0)
        {
            return;
        }

        int version = Volatile.Read(ref boundsVersion);
        bool inside = RetractionRules.Inside(cursor.X, cursor.Y, boundsLeft, boundsTop, boundsRight, boundsBottom, boundsMargin);
        if (version != seenBoundsVersion || inside != pointerInside)
        {
            seenBoundsVersion = version;
            pointerInside = inside;
            PInvoke.PostMessage(island, MsgPointer, (nuint)(inside ? 1 : 0), Pack(cursor.X, cursor.Y));
        }
    }

    /// <summary>Reads the struct, compares, posts. The hit test and everything else happens on the UI thread.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT MouseHook(int code, WPARAM wParam, LPARAM lParam)
    {
        long t0 = Stopwatch.GetTimestamp();
        if (code >= 0)
        {
            var info = (MSLLHOOKSTRUCT*)lParam.Value;
            int x = info->pt.X, y = info->pt.Y;
            switch ((uint)wParam.Value)
            {
                case PInvoke.WM_LBUTTONDOWN:
                    buttonDown = true;
                    candidate = false;
                    (downX, downY) = (x, y);
                    break;
                case PInvoke.WM_MOUSEMOVE:
                    if (buttonDown && !candidate && (Math.Abs(x - downX) >= dragX || Math.Abs(y - downY) >= dragY))
                    {
                        candidate = true;
                        PInvoke.PostMessage(island, MsgDragCandidate, 0, Pack(downX, downY));
                    }
                    else if (candidate && Volatile.Read(ref dragArmed) == 1 && y >= Volatile.Read(ref zoneTop) && y <= Volatile.Read(ref bandBottom)
                        && x >= Volatile.Read(ref zoneLeft) && x <= Volatile.Read(ref zoneRight))
                    {
                        // Once per drag: a retract must not re-show the surface for the rest of it.
                        Volatile.Write(ref dragArmed, 0);
                        PInvoke.PostMessage(island, MsgDragInZone, 0, Pack(x, y));
                    }

                    break;
                case PInvoke.WM_LBUTTONUP:
                    if (buttonDown)
                    {
                        buttonDown = false;
                        bool was = candidate;
                        candidate = false;
                        Volatile.Write(ref dragArmed, 0);
                        if (was)
                        {
                            PInvoke.PostMessage(island, MsgDragEnd, 0, 0);
                        }
                    }

                    break;
            }
        }

        long ticks = Stopwatch.GetTimestamp() - t0;
        HookCalls++;
        HookTicksSum += ticks;
        if (ticks > HookTicksMax)
        {
            HookTicksMax = ticks;
        }

        return PInvoke.CallNextHookEx(HHOOK.Null, code, wParam, lParam);
    }
}
