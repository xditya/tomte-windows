using System.Diagnostics;
using Tomte.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Threading;

namespace Tomte.AI.Palette;

/// <summary>The window in front of the user when the palette opened, and the chips built from it.</summary>
public static unsafe class WindowContext
{
    public static nint Foreground() => (nint)PInvoke.GetForegroundWindow().Value;

    /// <summary>The app and title of a window; both empty for no window (0), which process 0, the idle process, is not.</summary>
    public static (string App, string Title) Describe(nint hwnd)
    {
        if (hwnd == 0)
        {
            return ("", "");
        }

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
    /// The name people know a window's app by, for what the chips say: its exe's file description ("Visual Studio
    /// Code"), else its product name, else <paramref name="app"/>, the process name. The exe's path is read with
    /// query-only access, which elevated apps allow too.
    /// </summary>
    public static string Product(nint hwnd, string app)
    {
        if (hwnd == 0)
        {
            return app;
        }

        uint pid;
        PInvoke.GetWindowThreadProcessId(new HWND(hwnd), &pid);
        using var process = PInvoke.OpenProcess_SafeHandle(PROCESS_ACCESS_RIGHTS.PROCESS_QUERY_LIMITED_INFORMATION, false, pid);
        Span<char> path = stackalloc char[1024];
        uint length = (uint)path.Length;
        if (process.IsInvalid || !PInvoke.QueryFullProcessImageName(process, PROCESS_NAME_FORMAT.PROCESS_NAME_WIN32, path, ref length))
        {
            return app;
        }

        try
        {
            var info = FileVersionInfo.GetVersionInfo(path[..(int)length].ToString());
            return Name(info.FileDescription, info.ProductName, app);
        }
        catch (FileNotFoundException)
        {
            return app;
        }
    }

    /// <summary>The file description if it says anything, else the product name, else the process name.</summary>
    public static string Name(string? description, string? product, string process) =>
        !string.IsNullOrWhiteSpace(description) ? description.Trim() : !string.IsNullOrWhiteSpace(product) ? product.Trim() : process;

    /// <summary>
    /// The palette's chip row for one open: UIA selection and window title read now (focus moves
    /// once the user clicks in), the Ctrl+C fallback only when the user turns the chip on,
    /// screenshot and the app's own data read at send.
    /// </summary>
    public static List<ContextChip> Chips(nint foreground, IShelf shelf, IScratchpad scratchpad)
    {
        var (app, title) = Describe(foreground);
        return
        [
            SelectionReader.Chip(foreground),
            ContextChips.Window(app, title),
            ContextChips.Screenshot(app, ct => WindowCapture.CapturePngAsync(foreground, ct)),
            ContextChips.Shelf(shelf),
            ContextChips.Scratchpad(scratchpad),
        ];
    }
}
