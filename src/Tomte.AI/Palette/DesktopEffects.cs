using System.Diagnostics;
using System.Runtime.InteropServices;
using Tomte.AI.Automation;
using Tomte.Core.Rambler;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.AI.Palette;

/// <summary>The built-in effects that reach outside the app, for the shell and the harness to hand to <see cref="BuiltInTools"/>, and the rambler's Insert.</summary>
public static unsafe class DesktopEffects
{
    public static void CopyToClipboard(string text) => ClipboardSnapshot.SetText(text);

    /// <summary>
    /// Ctrl+V to the window that has the keys, as if the user pressed it; not while a modifier is still held, which would
    /// make it another shortcut. False when it was not sent.
    /// </summary>
    public static bool Paste() => !ClipboardSnapshot.ModifierDown() && ClipboardSnapshot.SendChord(VIRTUAL_KEY.VK_CONTROL, VIRTUAL_KEY.VK_V);

    /// <summary>The URL is validated by the tool; the shell opens it in the default browser.</summary>
    public static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    /// <summary>
    /// Types <paramref name="text"/> at the cursor of <paramref name="window"/>, the window the words were spoken into, as
    /// a paste, with the clipboard put back after and nothing kept in its history, or as Unicode key events where Ctrl+V
    /// does not paste. The island never takes the keys, so the field that had them when the hold started,
    /// <paramref name="field"/> (see <see cref="FocusIn"/>), should still have them; if another field has, nothing is typed. Null when typed, else why not (see <see cref="VoiceChoices.Refusal"/>), for
    /// the island to say.
    /// </summary>
    public static string? TypeInto(nint window, nint field, string text)
    {
        var hwnd = new HWND(window);
        bool open = window != 0 && PInvoke.IsWindow(hwnd);
        bool? elevated = null;
        uint pid = 0;
        if (open && PInvoke.GetWindowThreadProcessId(hwnd, &pid) != 0)
        {
            using var process = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
            elevated = process.IsInvalid ? null : UiaClient.Elevated(process);
        }

        if (VoiceChoices.Refusal(window, open, (nint)PInvoke.GetForegroundWindow().Value, elevated, sameField: FocusIn(window) == field) is { } refused)
            return refused;

        // A paste lands whole in every app that takes Ctrl+V. Typed keys are the fallback: apps that read the key
        // state as they handle each key (Notepad, WinUI fields) drop and repeat keys when they come faster than that.
        if (ClipboardSnapshot.Paste(text.ReplaceLineEndings(" "), window, pid))
            return null;

        // One event per call, so an app that keeps up with typing sees them one at a time.
        var keys = Keys(text, Held(VIRTUAL_KEY.VK_LMENU), Held(VIRTUAL_KEY.VK_RMENU));
        foreach (var key in CollectionsMarshal.AsSpan(keys))
        {
            if (PInvoke.SendInput([key], sizeof(INPUT)) != 1)
                return "Windows did not take the keys.";
        }

        return null;
    }

    /// <summary>
    /// The key events that type <paramref name="text"/>: first the release of each Alt still held from Alt+I, or the words
    /// would arrive as Alt chords (the input thread tapped a key assigned to nothing as the hotkey came, so the release
    /// opens no menu); then each UTF-16 unit as a Unicode key, down and up. A control character (a line break, a tab)
    /// types a space, so no word ever acts as Enter or Tab.
    /// </summary>
    internal static List<INPUT> Keys(string text, bool leftAlt, bool rightAlt)
    {
        var keys = new List<INPUT>(2 * text.Length + 2);
        if (leftAlt)
            keys.Add(Key(VIRTUAL_KEY.VK_LMENU, true));
        if (rightAlt)
            keys.Add(Key(VIRTUAL_KEY.VK_RMENU, true));
        foreach (char c in text)
        {
            char typed = char.IsControl(c) ? ' ' : c;
            keys.AddRange([Key(0, false, typed), Key(0, true, typed)]);
        }

        return keys;
    }

    /// <summary>
    /// The control that has the keys in <paramref name="window"/>'s thread, or 0 when none has or Windows does not say. Apps
    /// that draw their own fields in one window (browsers, Electron) answer the same window for every field.
    /// </summary>
    public static nint FocusIn(nint window)
    {
        uint thread = window == 0 ? 0 : PInvoke.GetWindowThreadProcessId(new HWND(window), null);
        var info = new GUITHREADINFO { cbSize = (uint)sizeof(GUITHREADINFO) };
        return thread != 0 && PInvoke.GetGUIThreadInfo(thread, ref info) ? (nint)info.hwndFocus.Value : 0;
    }

    private static bool Held(VIRTUAL_KEY vk) => (PInvoke.GetAsyncKeyState((int)vk) & 0x8000) != 0;

    private static INPUT Key(VIRTUAL_KEY vk, bool up, char unicode = default) => new()
    {
        type = INPUT_TYPE.INPUT_KEYBOARD,
        Anonymous =
        {
            ki = new KEYBDINPUT
            {
                wVk = vk,
                wScan = unicode,
                dwFlags = (unicode == 0 ? 0 : KEYBD_EVENT_FLAGS.KEYEVENTF_UNICODE) | (up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0),
            },
        },
    };
}
