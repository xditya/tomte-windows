using System.Globalization;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Core.Device;

/// <summary>Whether a camera streams, and to whom, as the call view says it.</summary>
public enum CameraUse
{
    Off,

    /// <summary>The call app streams from a camera.</summary>
    Call,

    /// <summary>Another app does, and the call app does not.</summary>
    Other,
}

/// <summary>
/// The app on a call: the process recording from the microphone, its name as people know it, and the keys that turn its
/// camera on or off in its own window, where it has them. <paramref name="Process"/> is the process name without .exe.
/// </summary>
public sealed record CallApp(uint Pid, string Process, string Name, string? CameraKeys)
{
    // Desktop call apps by process name, with their camera shortcut. Discord and Slack have none by default; a browser
    // (Meet, Teams on the web) is left out, since the keys would reach whichever tab is in front.
    private static readonly Dictionary<string, (string Name, string? Keys)> Known = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ms-teams"] = ("Teams", "Ctrl+Shift+O"),
        ["Teams"] = ("Teams", "Ctrl+Shift+O"),
        ["Zoom"] = ("Zoom", "Alt+V"),
        ["Discord"] = ("Discord", null),
        ["slack"] = ("Slack", null),
    };

    /// <summary>
    /// The app for a process: a known call app by its process name, else the name its file gives
    /// (<paramref name="description"/>), else the process name with a capital.
    /// </summary>
    public static CallApp Identify(uint pid, string process, string? description) =>
        Known.TryGetValue(process, out var known) ? new(pid, process, known.Name, known.Keys)
            : new(pid, process, description is { Length: > 0 } d ? d.Trim() : Capital(process), null);

    /// <summary>The app recording as process <paramref name="pid"/>, read from the process; null once it has gone.</summary>
    public static CallApp? Of(uint pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            string? description = null;
            try
            {
                description = process.MainModule?.FileVersionInfo.FileDescription;
            }
            catch (Exception e) when (e is System.ComponentModel.Win32Exception or InvalidOperationException)
            {
                // A process this one may not read; its name will do.
            }

            return Identify(pid, process.ProcessName, description);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Which of these audio sessions is the call app's: its own process first, then another process of the same name (a
    /// browser or Electron app records in one process and plays in another), an active session before an idle one in
    /// each. -1 when none is, and the caller falls back to the device's own volume.
    /// </summary>
    public int PickSession(IReadOnlyList<(uint Pid, string Process, bool Active)> sessions)
    {
        int best = -1, bestRank = int.MaxValue;
        for (int i = 0; i < sessions.Count; i++)
        {
            var (pid, process, active) = sessions[i];
            int rank = pid == Pid ? 0 : string.Equals(process, Process, StringComparison.OrdinalIgnoreCase) && pid != 0 ? 2 : -1;
            if (rank < 0)
            {
                continue;
            }

            rank += active ? 0 : 1;
            if (rank < bestRank)
            {
                (best, bestRank) = (i, rank);
            }
        }

        return best;
    }

    /// <summary>
    /// Whose camera is on, from the processes streaming from a camera now: the call app's (its process or another of
    /// its name), another app's (the first, by its process id), or nobody's. Tomte itself never counts.
    /// </summary>
    public (CameraUse Use, uint Other) Camera(IEnumerable<(uint Pid, string Process)> streaming, uint ownPid)
    {
        uint other = 0;
        foreach (var (pid, process) in streaming)
        {
            if (pid == ownPid)
            {
                continue;
            }

            if (pid == Pid || string.Equals(process, Process, StringComparison.OrdinalIgnoreCase))
            {
                return (CameraUse.Call, 0);
            }

            other = other == 0 ? pid : other;
        }

        return other == 0 ? (CameraUse.Off, 0) : (CameraUse.Other, other);
    }

    /// <summary>
    /// The call app's window to bring forward: the frontmost visible top-level window, not a tool window and not owned
    /// by another, of its process or of any process of its name. Simplification: an app with a meeting window and a
    /// main window gets whichever was used last, which is usually the meeting.
    /// </summary>
    private unsafe HWND? Window()
    {
        var pids = new HashSet<uint> { Pid };
        foreach (var p in System.Diagnostics.Process.GetProcessesByName(Process))
        {
            using (p)
            {
                pids.Add((uint)p.Id);
            }
        }

        HWND found = default;
        // EnumWindows goes front to back, so the first match is the one used last.
        PInvoke.EnumWindows((hwnd, _) =>
        {
            uint pid;
            PInvoke.GetWindowThreadProcessId(hwnd, &pid);
            if (!pids.Contains(pid) || !PInvoke.IsWindowVisible(hwnd) || PInvoke.GetWindow(hwnd, GET_WINDOW_CMD.GW_OWNER) != HWND.Null
                || ((WINDOW_EX_STYLE)PInvoke.GetWindowLong(hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE)).HasFlag(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW)
                || PInvoke.GetWindowTextLength(hwnd) == 0)
            {
                return true;
            }

            found = hwnd;
            return false;
        }, 0);
        return found == HWND.Null ? null : found;
    }

    /// <summary>
    /// Brings the call app's window to the front, restoring it if it is minimized. Only after the user's own click on the
    /// island, which is when Windows lets this process hand the foreground over. False when there is no window or
    /// Windows refused (it then flashes the app in the taskbar).
    /// </summary>
    public bool BringForward()
    {
        if (Window() is not { } window)
        {
            return false;
        }

        if (PInvoke.IsIconic(window))
        {
            PInvoke.ShowWindow(window, SHOW_WINDOW_CMD.SW_RESTORE);
        }

        return PInvoke.SetForegroundWindow(window);
    }

    private static string Capital(string process) =>
        process.Length == 0 ? process : char.ToUpper(process[0], CultureInfo.CurrentCulture) + process[1..];
}
