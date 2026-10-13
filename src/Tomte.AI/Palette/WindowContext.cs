using System.Diagnostics;
using Tomte.Core;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Tomte.AI.Palette;

/// <summary>The window in front of the user when the palette opened, and the chips built from it.</summary>
public static unsafe class WindowContext
{
    public static nint Foreground() => (nint)PInvoke.GetForegroundWindow().Value;

    public static (string App, string Title) Describe(nint hwnd)
    {
        var window = new HWND(hwnd);
        Span<char> buffer = stackalloc char[512];
        int length = PInvoke.GetWindowText(window, buffer);
        uint pid;
        PInvoke.GetWindowThreadProcessId(window, &pid);
        string app;
        try
        {
            using var process = Process.GetProcessById((int)pid);
            app = process.ProcessName;
        }
        catch (ArgumentException)
        {
            app = "";
        }

        return (app, new string(buffer[..length]));
    }

    /// <summary>
    /// The palette's chip row for one open: selection read now (focus moves once the user clicks
    /// in), window title read now, screenshot and the app's own data read at send.
    /// </summary>
    public static List<ContextChip> Chips(nint foreground, IShelf shelf, IScratchpad scratchpad)
    {
        var (app, title) = Describe(foreground);
        return
        [
            ContextChips.Selection(SelectionReader.Read(foreground)),
            ContextChips.Window(app, title),
            ContextChips.Screenshot(app, ct => WindowCapture.CapturePngAsync(foreground, ct)),
            ContextChips.Shelf(shelf),
            ContextChips.Scratchpad(scratchpad),
        ];
    }
}
