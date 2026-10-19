using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tomte.Core;
using Tomte.Core.Rambler;
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
/// tracking for retraction), the WH_MOUSE_LL hook (drag candidates, and presses outside the island while it is
/// tracked; the hook is there whenever the drag trigger is on, the only way to the drop zones), the registered hotkeys and the raw
/// keyboard input armed while the Rambler chord is held. Everything here is a few field reads and a
/// PostMessage to the island window; nothing logs or allocates. Settings are read on this thread only:
/// the UI thread posts <see cref="ApplySettings"/> and the thread re-reads the store itself.
/// </summary>
internal static unsafe class InputThread
{
    // Messages posted to the island window. wParam and lParam carry what the name says.
    public const uint MsgEdgeReveal = PInvoke.WM_APP + 1;       // wParam 1 = dwell, 0 = push
    public const uint MsgPointer = PInvoke.WM_APP + 2;          // wParam bit 0 = inside the island bounds plus the margin, bit 1 = on the island itself, bit 2 = a sample the island asked for changed them; lParam = packed x, y
    public const uint MsgDragCandidate = PInvoke.WM_APP + 3;    // lParam = packed x, y of the button-down point
    public const uint MsgDragInZone = PInvoke.WM_APP + 4;       // lParam = packed x, y
    public const uint MsgDragEnd = PInvoke.WM_APP + 5;
    public const uint MsgHotkey = PInvoke.WM_APP + 6;           // wParam = hotkey id
    public const uint MsgHotkeyReleased = PInvoke.WM_APP + 7;   // the Rambler chord came up; wParam 1 = held long enough to keep
    public const uint MsgHotkeysRegistered = PInvoke.WM_APP + 8; // Registrations holds a new snapshot
    public const uint MsgConfirmUnavailable = PInvoke.WM_APP + 9; // Win+Alt+Enter could not be registered; wParam = the error
    public const uint MsgPressOutside = PInvoke.WM_APP + 14;    // a mouse button went down outside the tracked island bounds
    public const uint MsgUndoUnavailable = PInvoke.WM_APP + 18; // Win+Alt+Z could not be registered; wParam = the error

    public const int EscapeHotkeyId = 100;

    // Win+Alt+Enter, only while the palette asks to run a voice command. A chord rather than Enter alone, so typing
    // in another app can never say yes.
    public const int ConfirmHotkeyId = 101;

    // While the rambler's card offers its choices, between the release and a pick: Alt+I, Alt+A and Alt+N from the first
    // id on, in VoiceChoice order, and Enter alone on its own id, which a card that refused Insert does not take.
    public const int ChoiceHotkeyId = 102, ChoiceEnterHotkeyId = 105;

    // Win+Alt+Z, only while the pill after a closed AI view offers Undo.
    public const int UndoHotkeyId = 106;

    // E alone while the rambler's card offers its choices and can edit its words; never once Insert was refused, when
    // the user may be typing in another app.
    public const int EditHotkeyId = 107;

    // Posted to the input window by the UI thread.
    private const uint MsgSetEscape = PInvoke.WM_APP + 10;      // wParam 1 registers Esc, 0 releases it
    private const uint MsgApplySettings = PInvoke.WM_APP + 11;  // re-read the store: thresholds, hooks, hotkeys
    private const uint MsgRawMouse = PInvoke.WM_APP + 12;       // wParam 1 wants raw mouse input for pointer tracking, 0 no longer
    private const uint MsgSetConfirm = PInvoke.WM_APP + 13;     // wParam 1 registers Win+Alt+Enter, 2 the card's keys with Enter, 3 without it, 4 with Enter and E, 0 releases them
    private const uint MsgSample = PInvoke.WM_APP + 15;         // sample the pointer against the island bounds now
    private const uint MsgPause = PInvoke.WM_APP + 16;          // wParam 1 releases the action hotkeys, 0 registers them again
    private const uint MsgSetUndo = PInvoke.WM_APP + 17;        // wParam 1 registers Win+Alt+Z, 0 releases it
    private const nuint DwellTimer = 1;
    private const ushort VkEscape = 0x1B, VkReturn = 0x0D, VkMenu = 0x12;
    private static readonly ushort[] ChoiceLetters = ['I', 'A', 'N'];
    private static readonly HWND MessageOnlyParent = new(-3); // HWND_MESSAGE, which CsWin32 does not emit

    private static HWND island, window;
    private static HHOOK hook;
    private static SettingsStore store = null!;
    private static RevealSettings reveal = null!;
    private static HotkeySettings? hotkeys;
    private static bool paused;
    private static EdgePushDetector detector = null!;
    private static HoldDetector hold = null!;
    private static ushort ramblerKey;
    private static readonly ManualResetEventSlim ready = new(false);
    private static Registration[] registrations = [];
    private static readonly Stopwatch clock = Stopwatch.StartNew();

    // Shared with the UI thread. Written there, read here; a torn read costs one wrong pointer sample.
    private static int zoneLeft, zoneRight, zoneTop, bandBottom;
    private static int boundsLeft, boundsTop, boundsRight, boundsBottom, boundsMargin, boundsVersion;
    private static int trackPointer, dragArmed;

    // Input thread only.
    private static int seenBoundsVersion = -1;
    private static bool pointerInside, pointerOn, rawMouseOn;
    private static int dragX, dragY, downX, downY;
    private static bool buttonDown, candidate;

    // Callback timing, read by the UI thread at exit.
    public static long HookCalls, HookTicksMax, HookTicksSum, InputCalls, InputTicksMax, InputTicksSum;

    /// <summary>What RegisterHotKey said for one action. Error 0 is registered; 1409 means another app holds the chord.</summary>
    public readonly record struct Registration(HotkeyAction Action, string Chord, int Error);

    /// <summary>A snapshot replaced whole after each (re)registration; MsgHotkeysRegistered says when.</summary>
    public static IReadOnlyList<Registration> Registrations => registrations;

    public static bool HookInstalled => !hook.IsNull;

    /// <summary>Starts the thread and returns once the hotkeys are registered, so the conflict report is ready.</summary>
    public static void Start(HWND islandWindow, SettingsStore settings)
    {
        island = islandWindow;
        store = settings;
        dragX = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXDRAG);
        dragY = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYDRAG);
        // The first WM_INPUT would otherwise pay the JIT of the whole path, about 2 ms.
        RuntimeHelpers.PrepareMethod(typeof(InputThread).GetMethod(nameof(OnMouseInput), System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!.MethodHandle);
        RuntimeHelpers.PrepareMethod(typeof(EdgePushDetector).GetMethod(nameof(EdgePushDetector.Feed))!.MethodHandle);
        new Thread(Loop) { IsBackground = true, Name = "tomte-input" }.Start();
        ready.Wait();
    }

    public static void Stop() => PInvoke.PostMessage(window, PInvoke.WM_CLOSE, default, default);

    /// <summary>The store changed; the input thread re-reads it where it runs.</summary>
    public static void ApplySettings() => PInvoke.PostMessage(window, MsgApplySettings, 0, 0);

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
        PInvoke.PostMessage(window, MsgRawMouse, 1, 0);
    }

    public static void StopTrackingPointer()
    {
        Volatile.Write(ref trackPointer, 0);
        PInvoke.PostMessage(window, MsgRawMouse, 0, 0);
    }

    /// <summary>
    /// Looks at the pointer against the island bounds now, and reports as a raw-input sample would: for when the island's
    /// own window saw the pointer arrive and this thread's last sample may not have.
    /// </summary>
    public static void SamplePointer() => PInvoke.PostMessage(window, MsgSample, 0, 0);

    /// <summary>The button-down window said HTCLIENT: the next move into the drag band inside the zone reports once.</summary>
    public static void ArmDrag() => Volatile.Write(ref dragArmed, 1);

    /// <summary>
    /// While a settings shortcut box records keys, the action hotkeys are released so a chord Tomte uses reaches the
    /// box; settings saved meanwhile are registered when they come back. Asking twice for the same state does nothing.
    /// </summary>
    public static void PauseHotkeys(bool on) => PInvoke.PostMessage(window, MsgPause, (nuint)(on ? 1 : 0), 0);

    public static void SetEscape(bool on) => PInvoke.PostMessage(window, MsgSetEscape, (nuint)(on ? 1 : 0), 0);

    public static void SetConfirm(bool on) => PInvoke.PostMessage(window, MsgSetConfirm, (nuint)(on ? 1 : 0), 0);

    /// <summary>Win+Alt+Z while the undo pill shows. When another app holds it, the island hears <see cref="MsgUndoUnavailable"/>.</summary>
    public static void SetUndo(bool on) => PInvoke.PostMessage(window, MsgSetUndo, (nuint)(on ? 1 : 0), 0);

    /// <summary>The card's keys while it waits: the Alt keys, and Enter unless <paramref name="enter"/> is false.</summary>
    public static void SetChoices(bool on, bool enter = true, bool edit = false) => PInvoke.PostMessage(window, MsgSetConfirm, (nuint)(!on ? 0 : !enter ? 3 : edit ? 4 : 2), 0);

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

        Apply(store.Current);
        ready.Set();
        MSG message;
        while (PInvoke.GetMessage(&message, HWND.Null, 0, 0).Value > 0)
        {
            PInvoke.DispatchMessage(&message);
        }
    }

    /// <summary>
    /// Thresholds, the hook, raw mouse input and the hotkeys, from the current settings. Each part is applied only
    /// when its own settings changed (records compare by value), so a save of something else interrupts no push or
    /// dwell in progress and re-registers no chord.
    /// </summary>
    private static void Apply(TomteSettings settings)
    {
        if (settings.Reveal != reveal)
        {
            ApplyReveal(settings.Reveal);
        }

        if (settings.Hotkeys != hotkeys)
        {
            hotkeys = settings.Hotkeys;
            if (!paused)
            {
                Register(settings.Hotkeys);
            }
        }
    }

    private static void ApplyReveal(RevealSettings next)
    {
        reveal = next;
        detector = new EdgePushDetector(reveal.PushCounts, reveal.TouchpadPushCounts, reveal.PushWindowMs);
        PInvoke.KillTimer(window, DwellTimer);

        // Raw mouse input is needed for the push, and otherwise only while the island is shown and the pointer is tracked.
        RawMouse(reveal.EdgePush || Volatile.Read(ref trackPointer) == 1);

        if (reveal.DragReveal && hook.IsNull)
        {
            hook = PInvoke.SetWindowsHookEx(WINDOWS_HOOK_ID.WH_MOUSE_LL, &MouseHook, HINSTANCE.Null, 0);
        }
        else if (!reveal.DragReveal && !hook.IsNull)
        {
            PInvoke.UnhookWindowsHookEx(hook);
            hook = HHOOK.Null;
        }
    }

    private static void Unregister()
    {
        foreach (var r in registrations)
        {
            PInvoke.UnregisterHotKey(window, (int)r.Action + 1);
        }
    }

    private static void Register(HotkeySettings chords)
    {
        Unregister();
        var next = new List<Registration>(5);
        foreach (var action in Enum.GetValues<HotkeyAction>())
        {
            string chord = chords[action];
            if (!Hotkey.TryParse(chord, out var hotkey, out _))
            {
                continue; // empty means no shortcut; an unparsable one was refused by the settings window
            }

            bool ok = PInvoke.RegisterHotKey(window, (int)action + 1, (HOT_KEY_MODIFIERS)hotkey.Modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT, hotkey.Key);
            next.Add(new Registration(action, hotkey.ToString(), ok ? 0 : Marshal.GetLastPInvokeError()));
            if (action == HotkeyAction.Rambler && (ushort)hotkey.Key != ramblerKey)
            {
                // The hold detector ends on the key-up of the chord's own key, so it follows the binding.
                ramblerKey = (ushort)hotkey.Key;
                NewHold();
            }
        }

        registrations = [.. next];
        PInvoke.PostMessage(island, MsgHotkeysRegistered, 0, 0);
    }

    /// <summary>
    /// Hold-to-talk: RegisterHotKey gives the press but not the release, so the detector arms raw keyboard input only
    /// while the chord is down and sees the first key-up. Auto-repeat presses while held are ignored by it.
    /// </summary>
    private static void NewHold()
    {
        hold = new HoldDetector(on => RegisterRaw(PInvoke.HID_USAGE_GENERIC_KEYBOARD, on), ramblerKey);
        hold.Started += () => PInvoke.PostMessage(island, MsgHotkey, (nuint)HotkeyAction.Rambler + 1, 0);
        hold.Ended += keep => PInvoke.PostMessage(island, MsgHotkeyReleased, (nuint)(keep ? 1 : 0), 0);
    }

    private static void RawMouse(bool on)
    {
        if (on != rawMouseOn)
        {
            rawMouseOn = on;
            RegisterRaw(PInvoke.HID_USAGE_GENERIC_MOUSE, on);
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

    // Alt+I, Alt+A or Alt+N picked on the card, with Alt still down: a tap of a key assigned to nothing, so the app in
    // front takes Alt as used in a chord. Letting Alt go then opens no menu, and letters typed after are not menu keys.
    private static void MaskAlt()
    {
        const VIRTUAL_KEY Unassigned = (VIRTUAL_KEY)0xE8;
        ReadOnlySpan<INPUT> tap =
        [
            new() { type = INPUT_TYPE.INPUT_KEYBOARD, Anonymous = { ki = new KEYBDINPUT { wVk = Unassigned } } },
            new() { type = INPUT_TYPE.INPUT_KEYBOARD, Anonymous = { ki = new KEYBDINPUT { wVk = Unassigned, dwFlags = KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP } } },
        ];
        PInvoke.SendInput(tap, sizeof(INPUT));
    }

    // Win+Alt+Enter, or the card's three Alt keys and its Enter: an id that is not registered is ignored.
    private static void UnregisterConfirm(HWND hwnd)
    {
        for (int id = ConfirmHotkeyId; id <= ChoiceEnterHotkeyId; id++)
        {
            PInvoke.UnregisterHotKey(hwnd, id);
        }

        PInvoke.UnregisterHotKey(hwnd, EditHotkeyId);
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT HandleMessage(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        // An exception leaving an unmanaged callback ends the process.
        try
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
                case PInvoke.WM_HOTKEY when wParam.Value == (nuint)HotkeyAction.Rambler + 1:
                    hold.Press(clock.ElapsedMilliseconds);
                    // A tap shorter than WM_HOTKEY's delivery has already released the key when this runs.
                    if ((PInvoke.GetAsyncKeyState(ramblerKey) & 0x8000) == 0)
                    {
                        hold.KeyUp(ramblerKey, clock.ElapsedMilliseconds);
                    }

                    return new LRESULT(0);
                case PInvoke.WM_HOTKEY:
                    if ((int)wParam.Value is >= ChoiceHotkeyId and < ChoiceEnterHotkeyId && (PInvoke.GetAsyncKeyState(VkMenu) & 0x8000) != 0)
                    {
                        MaskAlt();
                    }

                    PInvoke.PostMessage(island, MsgHotkey, wParam, 0);
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
                case MsgSetConfirm:
                    if (wParam.Value is 2 or 3 or 4)
                    {
                        // A key another app holds is left to it: the card's buttons still take clicks.
                        UnregisterConfirm(hwnd);
                        for (int i = 0; i < ChoiceLetters.Length; i++)
                        {
                            PInvoke.RegisterHotKey(hwnd, ChoiceHotkeyId + i, HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_NOREPEAT, ChoiceLetters[i]);
                        }

                        if (wParam.Value is 2 or 4)
                        {
                            PInvoke.RegisterHotKey(hwnd, ChoiceEnterHotkeyId, HOT_KEY_MODIFIERS.MOD_NOREPEAT, VkReturn);
                        }

                        if (wParam.Value == 4)
                        {
                            PInvoke.RegisterHotKey(hwnd, EditHotkeyId, HOT_KEY_MODIFIERS.MOD_NOREPEAT, 'E');
                        }
                    }
                    else if (wParam.Value == 1 && !PInvoke.RegisterHotKey(hwnd, ConfirmHotkeyId, HOT_KEY_MODIFIERS.MOD_WIN | HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_NOREPEAT, VkReturn))
                    {
                        // Nothing else can say yes, so the island must hear it and end the question.
                        PInvoke.PostMessage(island, MsgConfirmUnavailable, (nuint)Marshal.GetLastPInvokeError(), 0);
                    }
                    else if (wParam.Value == 0)
                    {
                        UnregisterConfirm(hwnd);
                    }

                    return new LRESULT(0);
                case MsgSetUndo when wParam.Value == 1:
                    if (!PInvoke.RegisterHotKey(hwnd, UndoHotkeyId, HOT_KEY_MODIFIERS.MOD_WIN | HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_NOREPEAT, (uint)UndoClose.Chord.Key))
                    {
                        PInvoke.PostMessage(island, MsgUndoUnavailable, (nuint)Marshal.GetLastPInvokeError(), 0);
                    }

                    return new LRESULT(0);
                case MsgSetUndo:
                    PInvoke.UnregisterHotKey(hwnd, UndoHotkeyId);
                    return new LRESULT(0);
                case MsgApplySettings:
                    Apply(store.Current);
                    return new LRESULT(0);
                case MsgPause when (wParam.Value == 1) != paused:
                    paused = !paused;
                    if (paused)
                    {
                        Unregister();
                    }
                    else
                    {
                        Register(hotkeys!);
                    }

                    return new LRESULT(0);
                case MsgRawMouse:
                    RawMouse(reveal.EdgePush || wParam.Value == 1);
                    return new LRESULT(0);
                case MsgSample:
                    System.Drawing.Point now;
                    PInvoke.GetCursorPos(&now);
                    Track(now, asked: true);
                    return new LRESULT(0);
                case PInvoke.WM_CLOSE:
                    Unregister();
                    PInvoke.UnregisterHotKey(hwnd, EscapeHotkeyId);
                    UnregisterConfirm(hwnd);
                    PInvoke.UnregisterHotKey(hwnd, UndoHotkeyId);
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
        }
        catch (Exception)
        {
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
            // A precision touchpad's input arrives with no device handle; so does injected input.
            OnMouseInput(in raw.data.mouse, raw.header.hDevice.IsNull);
        }
        else if (raw.header.dwType == (uint)RID_DEVICE_INFO_TYPE.RIM_TYPEKEYBOARD && hold.Held && (raw.data.keyboard.Flags & PInvoke.RI_KEY_BREAK) != 0)
        {
            hold.KeyUp(raw.data.keyboard.VKey, clock.ElapsedMilliseconds);
        }
    }

    private static void OnMouseInput(in RAWMOUSE mouse, bool touchpad)
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
        if (reveal.EdgePush)
        {
            switch (detector.Feed(clock.Elapsed.TotalMilliseconds, dy, atTop, inZone, touchpad))
            {
                case EdgePushDetector.Event.EnterZone:
                    if (reveal.DwellMs > 0)
                    {
                        PInvoke.SetTimer(window, DwellTimer, (uint)reveal.DwellMs, null);
                    }

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

        Track(cursor);
    }

    /// <summary>
    /// Inside the bounds plus the margin, and on the island itself: posted when either or the bounds changed, and marked
    /// when a sample the island <paramref name="asked"/> for changed either.
    /// </summary>
    private static void Track(System.Drawing.Point cursor, bool asked = false)
    {
        if (Volatile.Read(ref trackPointer) == 0)
        {
            return;
        }

        int version = Volatile.Read(ref boundsVersion);
        bool inside = RetractionRules.Inside(cursor.X, cursor.Y, boundsLeft, boundsTop, boundsRight, boundsBottom, boundsMargin);
        bool on = inside && RetractionRules.Inside(cursor.X, cursor.Y, boundsLeft, boundsTop, boundsRight, boundsBottom, 0);
        if (version != seenBoundsVersion || inside != pointerInside || on != pointerOn)
        {
            bool changed = asked && (inside != pointerInside || on != pointerOn);
            seenBoundsVersion = version;
            (pointerInside, pointerOn) = (inside, on);
            PInvoke.PostMessage(island, MsgPointer, (nuint)((inside ? 1 : 0) | (on ? 2 : 0) | (changed ? 4 : 0)), Pack(cursor.X, cursor.Y));
        }
    }

    /// <summary>Reads the struct, compares, posts. The hit test and everything else happens on the UI thread.</summary>
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT MouseHook(int code, WPARAM wParam, LPARAM lParam)
    {
        long t0 = Stopwatch.GetTimestamp();
        try
        {
            if (code >= 0)
            {
                var info = (MSLLHOOKSTRUCT*)lParam.Value;
                int x = info->pt.X, y = info->pt.Y;
                if ((uint)wParam.Value is PInvoke.WM_LBUTTONDOWN or PInvoke.WM_RBUTTONDOWN && Volatile.Read(ref trackPointer) == 1
                    && !RetractionRules.Inside(x, y, boundsLeft, boundsTop, boundsRight, boundsBottom, 0))
                {
                    PInvoke.PostMessage(island, MsgPressOutside, 0, 0);
                }

                switch ((uint)wParam.Value)
                {
                    case PInvoke.WM_LBUTTONDOWN:
                        buttonDown = true;
                        candidate = false;
                        // A hit-test answer that lands after a fast click's button-up must not arm the next drag.
                        Volatile.Write(ref dragArmed, 0);
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
        }
        catch (Exception)
        {
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
