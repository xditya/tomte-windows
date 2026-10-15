using System.Diagnostics;
using System.Runtime.InteropServices;
using Tomte.AI.Automation;
using Tomte.Core.Rambler;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Tomte.AI.Palette;

/// <summary>The built-in effects that reach outside the app, for the shell and the harness to hand to <see cref="BuiltInTools"/>, and the rambler's Insert.</summary>
public static unsafe class DesktopEffects
{
    public static void CopyToClipboard(string text) => ClipboardSnapshot.SetText(text);

    /// <summary>The URL is validated by the tool; the shell opens it in the default browser.</summary>
    public static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();

    /// <summary>
    /// Types <paramref name="text"/> at the cursor of <paramref name="window"/>, the window the words were spoken into, as
    /// Unicode key events, so nothing passes through the clipboard or its history. The island never takes the keys, so the
    /// field that had them when the hold started still has them. Null when typed, else why not (see
    /// <see cref="VoiceChoices.Refusal"/>), for the island to say.
    /// </summary>
    public static string? TypeInto(nint window, string text)
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

        if (VoiceChoices.Refusal(window, open, (nint)PInvoke.GetForegroundWindow().Value, elevated) is { } refused)
            return refused;

        var keys = new List<INPUT>(2 * text.Length + 2);
        // Alt+I picked Insert and Alt is still held: let it go first, or the words arrive as Alt chords. The input thread
        // tapped a key assigned to nothing as the hotkey came, so the release opens no menu.
        foreach (var alt in (ReadOnlySpan<VIRTUAL_KEY>)[VIRTUAL_KEY.VK_LMENU, VIRTUAL_KEY.VK_RMENU])
        {
            if ((PInvoke.GetAsyncKeyState((int)alt) & 0x8000) != 0)
                keys.Add(Key(alt, true));
        }

        foreach (char c in text)
            keys.AddRange([Key(0, false, c), Key(0, true, c)]);
        return PInvoke.SendInput(CollectionsMarshal.AsSpan(keys), sizeof(INPUT)) == keys.Count ? null : "Windows did not take the keys.";
    }

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
