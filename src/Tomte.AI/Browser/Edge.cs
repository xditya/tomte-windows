using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32;

namespace Tomte.AI.Browser;

/// <summary>
/// Tomte's own Microsoft Edge: the installed msedge.exe on a profile of Tomte's (%LOCALAPPDATA%\Tomte\Browser), never
/// the user's own profile or windows. It always starts with a DevTools port, so a later palette or job can drive the
/// copy that is already open on that profile; logins made in it stay in the profile.
/// </summary>
public static class Edge
{
    public static string Profile { get; } = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tomte", "Browser");

    /// <summary>The file Edge writes in the profile once its DevTools port is open.</summary>
    internal static string PortFile => Path.Combine(Profile, "DevToolsActivePort");

    /// <summary>msedge.exe from App Paths, else the usual install folders; null when Edge is not installed.</summary>
    public static string? Find()
    {
        const string key = @"SOFTWARE\Microsoft\Windows\CurrentVersion\App Paths\msedge.exe";
        foreach (var hive in new[] { Registry.CurrentUser, Registry.LocalMachine })
        {
            using var paths = hive.OpenSubKey(key);
            if (paths?.GetValue(null) is string path && File.Exists(path.Trim('"')))
                return path.Trim('"');
        }

        return new[] { Environment.SpecialFolder.ProgramFilesX86, Environment.SpecialFolder.ProgramFiles }
            .Select(f => Path.Combine(Environment.GetFolderPath(f), "Microsoft", "Edge", "Application", "msedge.exe"))
            .FirstOrDefault(File.Exists);
    }

    /// <summary>
    /// The browser endpoint a DevToolsActivePort file names: its first line is the port, its second the path,
    /// "/devtools/browser/&lt;id&gt;". Null for anything else.
    /// </summary>
    public static Uri? ReadPort(string text)
    {
        var lines = text.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return lines.Length >= 2
            && int.TryParse(lines[0], NumberStyles.None, CultureInfo.InvariantCulture, out int port) && port is > 0 and <= 65535
            && lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal) && lines[1].Skip(1).All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-')
            ? new Uri($"ws://127.0.0.1:{port}{lines[1]}")
            : null;
    }

    /// <summary>How Edge is started: Tomte's profile, a DevTools port Edge picks, no first-run pages; headless for a job.</summary>
    internal static ProcessStartInfo StartInfo(string exe, bool headless, string? url = "about:blank")
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = false };
        start.ArgumentList.Add($"--user-data-dir={Profile}");
        start.ArgumentList.Add("--remote-debugging-port=0");
        start.ArgumentList.Add("--no-first-run");
        start.ArgumentList.Add("--no-default-browser-check");
        if (headless)
            start.ArgumentList.Add("--headless=new");
        if (url is not null)
            start.ArgumentList.Add(url);
        return start;
    }

    /// <summary>Opens Tomte's browser in a window, for signing in to sites. Null, or why it did not open.</summary>
    public static string? OpenForSignIn()
    {
        if (Find() is not { } exe)
            return "Microsoft Edge was not found.";
        try
        {
            Directory.CreateDirectory(Profile);
            using var _ = Process.Start(StartInfo(exe, headless: false, url: null));
            return null;
        }
        catch (Exception e) when (e is System.ComponentModel.Win32Exception or IOException or UnauthorizedAccessException)
        {
            return $"Edge did not start: {e.Message}";
        }
    }
}
