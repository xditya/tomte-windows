using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.DataExchange;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Core;

/// <summary>
/// Asks the settings window that is already open to show a page or section: a WM_COPYDATA with Tomte's tag and the
/// page's title or section's header as UTF-16 text. The window reads only a message with the tag and a short text, and
/// shows only a title or header it has.
/// </summary>
public static class SettingsSection
{
    /// <summary>The longest text a message carries; every title and header is far shorter.</summary>
    public const int MaxLength = 64;

    // "TMSE": marks the message as Tomte's, so another program's WM_COPYDATA to the window is left to the window.
    internal const nuint Tag = 0x544D5345;

    /// <summary>
    /// Sends <paramref name="section"/> to the window, waiting at most half a second for a window that hangs. True when
    /// the window took it. A worker thread only: the window's thread answers it.
    /// </summary>
    public static unsafe bool Send(nint window, string section)
    {
        if (window == 0 || section.Length is 0 or > MaxLength)
        {
            return false;
        }

        fixed (char* text = section)
        {
            var data = new COPYDATASTRUCT { dwData = Tag, cbData = (uint)(section.Length * sizeof(char)), lpData = text };
            nuint taken;
            return PInvoke.SendMessageTimeout(new HWND(window), PInvoke.WM_COPYDATA, 0, (nint)(&data), SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG, 500, &taken) != 0
                && taken == 1;
        }
    }

    /// <summary>The text a WM_COPYDATA's <paramref name="lParam"/> carries when it is this kind of message; false otherwise.</summary>
    public static unsafe bool TryRead(nint lParam, out string section)
    {
        section = "";
        var data = (COPYDATASTRUCT*)lParam;
        if (data is null || data->dwData != Tag || data->lpData is null || data->cbData is 0 or > MaxLength * sizeof(char) || data->cbData % sizeof(char) != 0)
        {
            return false;
        }

        section = new string((char*)data->lpData, 0, (int)(data->cbData / sizeof(char)));
        return true;
    }
}
