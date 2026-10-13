using System.ComponentModel;
using System.Diagnostics;
using Windows.Win32;

namespace Tomte.Core.Device;

public static class Apps
{
    // An app name on PATH or in App Paths ("notepad"), a full path, a .lnk shortcut, or
    // shell:AppsFolder\<AUMID>. Started through the shell with no arguments, so the
    // target cannot smuggle a command line in.
    public static ActionResult OpenApp(string target, bool dryRun = false)
    {
        if (ValidateAppTarget(target) is { } error) return ActionResult.Fail(error);
        if (dryRun) return ActionResult.DryRun($"open {target}.");
        return Launch(target, $"Opened {target}.");
    }

    public static ActionResult OpenSettingsPage(string uri, bool dryRun = false)
    {
        if (!IsSettingsUri(uri)) return ActionResult.Fail("A settings page looks like ms-settings:display.");
        if (dryRun) return ActionResult.DryRun($"open {uri}.");
        return Launch(uri, $"Opened {uri}.");
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

    private static ActionResult Launch(string target, string message)
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
            // The launched app is a background start; let it take the foreground once.
            if (process is not null) PInvoke.AllowSetForegroundWindow((uint)process.Id);
            return ActionResult.Done(message);
        }
        catch (Win32Exception e)
        {
            return ActionResult.Fail($"Could not open {target}: {e.Message}");
        }
    }
}
