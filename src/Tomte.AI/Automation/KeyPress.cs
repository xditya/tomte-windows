using Tomte.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.StationsAndDesktops;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.AI.Automation;

/// <summary>
/// press_keys and a job's view of the desktop: key chords read from text such as "Ctrl+K Enter", a window brought to
/// the front before keys go to it, and whether the PC is locked, when nothing can be done for the user.
/// </summary>
internal static unsafe class KeyPress
{
    private const int MaxChords = 10;
    private const ushort Ctrl = 0x11, Shift = 0x10, Alt = 0x12;

    // A key no keyboard has: tapping it makes this process the one with the last input, which Windows asks of a
    // process that brings another window to the front.
    private const VIRTUAL_KEY Unassigned = (VIRTUAL_KEY)0xE8;

    private static readonly Dictionary<string, ushort> Named = new(StringComparer.OrdinalIgnoreCase)
    {
        ["enter"] = 0x0D,
        ["return"] = 0x0D,
        ["tab"] = 0x09,
        ["esc"] = 0x1B,
        ["escape"] = 0x1B,
        ["space"] = 0x20,
        ["backspace"] = 0x08,
        ["delete"] = 0x2E,
        ["del"] = 0x2E,
        ["insert"] = 0x2D,
        ["home"] = 0x24,
        ["end"] = 0x23,
        ["pageup"] = 0x21,
        ["pagedown"] = 0x22,
        ["left"] = 0x25,
        ["up"] = 0x26,
        ["right"] = 0x27,
        ["down"] = 0x28,
    };

    // The navigation keys sit on the extended part of the keyboard; without the flag some apps read them as the number pad.
    private static bool Extended(ushort vk) => vk is >= 0x21 and <= 0x28 or 0x2D or 0x2E;

    /// <summary>
    /// The chords in <paramref name="keys"/>, each its modifiers then its key, or why they cannot be pressed: chords
    /// are separated by spaces, keys in a chord by "+", modifiers are Ctrl, Shift and Alt. The Windows key is refused.
    /// </summary>
    public static (IReadOnlyList<ushort[]> Chords, string? Error) Parse(string keys)
    {
        var chords = new List<ushort[]>();
        foreach (string chord in keys.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries))
        {
            var parts = chord.Split('+', StringSplitOptions.TrimEntries);
            var vks = new List<ushort>();
            for (int i = 0; i < parts.Length; i++)
            {
                string part = parts[i].ToLowerInvariant();
                if (part is "win" or "windows" or "meta" or "cmd" or "super")
                    return ([], "Tomte does not press the Windows key.");
                ushort? vk = i < parts.Length - 1
                    ? part switch { "ctrl" or "control" => Ctrl, "shift" => Shift, "alt" => Alt, _ => null }
                    : Key(part);
                if (vk is null)
                    return ([], i < parts.Length - 1 ? $"\"{parts[i]}\" is not Ctrl, Shift or Alt." : $"\"{parts[i]}\" is not a key press_keys knows.");
                vks.Add(vk.Value);
            }

            chords.Add([.. vks]);
        }

        return chords.Count switch
        {
            0 => ([], "keys is empty."),
            > MaxChords => ([], $"press_keys takes at most {MaxChords} chords at a time."),
            _ => (chords, null),
        };
    }

    private static ushort? Key(string name) => name switch
    {
        _ when Named.TryGetValue(name, out ushort vk) => vk,
        [>= 'a' and <= 'z'] => (ushort)char.ToUpperInvariant(name[0]),
        [>= '0' and <= '9'] => name[0],
        ['f', .. var n] when int.TryParse(n, out int f) && f is >= 1 and <= 12 => (ushort)(0x6F + f),
        _ => null,
    };

    /// <summary>Brings a top-level window to the front, restoring it when minimized; true once it is there.</summary>
    public static bool Bring(nint handle)
    {
        var window = new HWND(handle);
        if (!PInvoke.IsWindow(window))
            return false;
        if (PInvoke.GetForegroundWindow() == window)
            return true;
        if (PInvoke.IsIconic(window))
            PInvoke.ShowWindow(window, SHOW_WINDOW_CMD.SW_RESTORE);
        PInvoke.SendInput([Input(Unassigned, false), Input(Unassigned, true)], sizeof(INPUT));
        PInvoke.SetForegroundWindow(window);
        for (int i = 0; i < 10; i++)
        {
            if (PInvoke.GetForegroundWindow() == window)
                return true;
            Thread.Sleep(50);
        }

        return false;
    }

    /// <summary>The window in front now, to bring back when a job is done.</summary>
    public static nint InFront() => PInvoke.GetForegroundWindow();

    /// <summary>Sends each chord as its keys down in order, then up in reverse, to whatever window is in front.</summary>
    public static bool Send(IReadOnlyList<ushort[]> chords)
    {
        var inputs = new List<INPUT>();
        foreach (var chord in chords)
        {
            inputs.AddRange(chord.Select(vk => Input((VIRTUAL_KEY)vk, false)));
            inputs.AddRange(chord.Reverse().Select(vk => Input((VIRTUAL_KEY)vk, true)));
        }

        return PInvoke.SendInput(inputs.ToArray(), sizeof(INPUT)) == inputs.Count;
    }

    /// <summary>
    /// True when the session is locked or a secure desktop is up: the desktop taking input is not the user's own, or
    /// the lock screen is in front.
    /// </summary>
    public static bool Locked()
    {
        using var desktop = PInvoke.OpenInputDesktop_SafeHandle(0, false, DESKTOP_ACCESS_FLAGS.DESKTOP_READOBJECTS);
        if (desktop.IsInvalid)
            return true;
        var name = stackalloc char[32];
        uint needed;
        if (!PInvoke.GetUserObjectInformation((HANDLE)desktop.DangerousGetHandle(), USER_OBJECT_INFORMATION_INDEX.UOI_NAME, name, 64, &needed)
            || !new ReadOnlySpan<char>(name, (int)needed / 2 - 1).SequenceEqual("Default"))
            return true;
        uint pid;
        PInvoke.GetWindowThreadProcessId(PInvoke.GetForegroundWindow(), &pid);
        using var process = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        Span<char> path = stackalloc char[1024];
        uint length = (uint)path.Length;
        return !process.IsInvalid && PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, path, ref length)
            && AppRules.Normalize(path[..(int)length].ToString()) is "lockapp" or "logonui";
    }

    private static INPUT Input(VIRTUAL_KEY vk, bool up) => new()
    {
        type = INPUT_TYPE.INPUT_KEYBOARD,
        Anonymous =
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                dwFlags = (up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0) | (Extended((ushort)vk) ? KEYBD_EVENT_FLAGS.KEYEVENTF_EXTENDEDKEY : 0),
            },
        },
    };
}
