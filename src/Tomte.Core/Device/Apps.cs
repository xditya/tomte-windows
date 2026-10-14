using System.ComponentModel;
using System.Diagnostics;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Core.Device;

public static class Apps
{
    // An app name on PATH or in App Paths ("notepad"), a full path, a .lnk shortcut, or
    // shell:AppsFolder\<AUMID>. A running app with a window is brought to the front; otherwise
    // it is started through the shell with no arguments, so the target cannot smuggle a
    // command line in.
    public static ActionResult OpenApp(string target, bool dryRun = false)
    {
        if (ValidateAppTarget(target) is { } error) return ActionResult.Fail(error);
        if (dryRun) return ActionResult.DryRun($"open or focus {target}.");
        if (ProcessNameFor(target) is { } exe && MainWindowOf(exe) is { } window) return Focus(target, window);
        return Launch(target);
    }

    // "notepad" and "C:\Tools\app.exe" name a process; a .lnk shortcut or an AUMID does not, so those only launch.
    public static string? ProcessNameFor(string target)
    {
        if (target.StartsWith("shell:", StringComparison.OrdinalIgnoreCase) || Path.GetExtension(target).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            return null;
        var name = Path.GetFileNameWithoutExtension(target);
        return name.Length > 0 ? name : null;
    }

    private static HWND? MainWindowOf(string exe)
    {
        foreach (var process in Process.GetProcessesByName(exe))
            using (process)
                if (process.MainWindowHandle != 0)
                    return new HWND(process.MainWindowHandle);
        return null;
    }

    // Runs right after the user confirmed in the palette, which is when Windows lets this process
    // hand the foreground over. When the foreground rules refuse, Windows flashes the app's taskbar
    // button instead, and the result says so.
    private static ActionResult Focus(string target, HWND window)
    {
        if (PInvoke.IsIconic(window)) PInvoke.ShowWindow(window, SHOW_WINDOW_CMD.SW_RESTORE);
        return PInvoke.SetForegroundWindow(window)
            ? ActionResult.Done($"Brought {target} to the front.")
            : ActionResult.Done($"{target} is running, but Windows did not let Tomte bring it to the front; it flashed in the taskbar.");
    }

    public static ActionResult OpenSettingsPage(string uri, bool dryRun = false)
    {
        if (!IsSettingsUri(uri)) return ActionResult.Fail("A settings page looks like ms-settings:display.");
        if (dryRun) return ActionResult.DryRun($"open {uri}.");
        return Launch(uri);
    }

    public static string? ValidateAppTarget(string target)
    {
        if (string.IsNullOrWhiteSpace(target)) return "Name an app to open.";
        if (target.Contains("://", StringComparison.Ordinal)) return "That is a URL, not an app.";
        if (target.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)) return "Use OpenSettingsPage for settings pages.";
        if (target.IndexOfAny(['"', '\r', '\n']) >= 0) return "App names cannot contain quotes or line breaks.";
        return null;
    }

    // ms-settings: followed by a page id made of letters, digits, '-' and '.'.
    public static bool IsSettingsUri(string uri) =>
        uri.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)
        && uri.Length > "ms-settings:".Length
        && uri["ms-settings:".Length..].All(c => char.IsAsciiLetterOrDigit(c) || c is '-' or '.');

    // Starts the target through the shell; OpenApp has already looked for a running window to
    // focus. AllowSetForegroundWindow only works while this process may set the foreground, which
    // the never-activated island usually may not, so the result says when the app is likely to
    // open behind the current window.
    private static ActionResult Launch(string target)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            // No process means the shell handed the start to an existing broker (settings pages,
            // packaged apps); whether it comes to the front is up to that broker.
            if (process is null || PInvoke.AllowSetForegroundWindow((uint)process.Id))
                return ActionResult.Done($"Opened {target}.");
            return ActionResult.Done($"Opened {target} in the background; it may only flash in the taskbar.");
        }
        catch (Win32Exception e)
        {
            return ActionResult.Fail($"Could not open {target}: {e.Message}");
        }
    }
}
