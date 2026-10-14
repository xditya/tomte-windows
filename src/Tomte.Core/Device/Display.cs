using System.Management;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Core.Device;

public static class Display
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";
    private const nint HwndBroadcast = 0xffff;

    // How long the broadcast waits for each window. A sent message that a window's thread has not picked up when
    // the wait runs out is cancelled, not left queued, so that window never re-reads the theme and keeps its old
    // colours. A thread still repainting other windows for the change (Explorer has many) can take longer than a
    // short wait. 5 s is what Microsoft's PowerToys Light Switch waits for the same broadcast; SMTO_ABORTIFHUNG
    // still skips a hung window at once.
    private const uint BroadcastTimeoutMs = 5000;

    // One theme change at a time, writes and broadcast together: see WriteTheme.
    private static readonly Lock ThemeGate = new();

    // Dark mode is two per-user values (0 = dark) plus a broadcast so running apps and the shell repaint.
    // Blocks until every running window has been told, a few seconds: call it off the UI thread. So does its undo.
    public static ActionResult SetDarkMode(bool on, bool dryRun = false) => SetDarkMode(on, dryRun, ThemeCalls.Windows);

    internal static ActionResult SetDarkMode(bool on, bool dryRun, ThemeCalls calls)
    {
        if (dryRun) return ActionResult.DryRun($"turn dark mode {OnOff(on)}.");

        var light = on ? 0 : 1;
        var (apps, system) = WriteTheme(calls, light, light);
        return ActionResult.Done($"Dark mode {OnOff(on)}.", () =>
        {
            WriteTheme(calls, apps, system);
            return ActionResult.Done("Restored the previous theme.");
        });
    }

    // Night light has no public API; its state lives in an undocumented CloudStore blob.
    // Writing that blob is fragile across builds, so this opens the settings page instead.
    // settings page only; write the blob if a one-step toggle is ever wanted.
    public static ActionResult SetNightLight(bool on, bool dryRun = false)
    {
        if (dryRun) return ActionResult.DryRun($"open Night light settings so you can turn it {OnOff(on)}.");
        var opened = Apps.OpenSettingsPage("ms-settings:nightlight");
        return opened.Ok
            ? ActionResult.Done($"Opened Night light settings; turn it {OnOff(on)} there.")
            : opened;
    }

    // Built-in panels only (laptops, tablets). External monitors need DDC/CI, which this does not do.
    public static ActionResult SetBrightness(int percent, bool dryRun = false)
    {
        if (percent is < 0 or > 100) return ActionResult.Fail("Brightness must be between 0 and 100.");
        if (dryRun) return ActionResult.DryRun($"set brightness to {percent}%.");

        try
        {
            var previous = ReadBrightness();
            WriteBrightness(percent);
            return ActionResult.Done($"Brightness {percent}%.", () =>
            {
                WriteBrightness(previous);
                return ActionResult.Done($"Brightness back to {previous}%.");
            });
        }
        catch (ManagementException)
        {
            return ActionResult.Fail("This display has no brightness control Windows can set.");
        }
    }

    // The action and its undo both come here. Both values first, then one broadcast, on the caller's thread, so the
    // change is reported done only once every window has been told, a process that exits right after cannot cut the
    // broadcast short, and an undo's broadcast never runs alongside the action's: windows still busy with the first
    // would let the second time out, keep the colours of the first, and Windows would end up half light, half dark.
    // Returns the values it replaced, read under the same lock, so an undo never restores another call's half-write.
    private static (int Apps, int System) WriteTheme(ThemeCalls calls, int apps, int system)
    {
        lock (ThemeGate)
        {
            var before = (calls.Read("AppsUseLightTheme") ?? 1, calls.Read("SystemUsesLightTheme") ?? 1);
            calls.Write("AppsUseLightTheme", apps);
            calls.Write("SystemUsesLightTheme", system);
            calls.Send(HwndBroadcast, PInvoke.WM_SETTINGCHANGE, 0, "ImmersiveColorSet", SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG, BroadcastTimeoutMs);
            return before;
        }
    }

    private static unsafe void SendTimeout(nint window, uint message, nuint wParam, string text, SEND_MESSAGE_TIMEOUT_FLAGS flags, uint timeoutMs)
    {
        fixed (char* p = text)
        {
            PInvoke.SendMessageTimeout(new HWND(window), message, wParam, (nint)p, flags, timeoutMs, null);
        }
    }

    /// <summary>
    /// What a theme change touches: the two per-user values and the broadcast. Tests pass a recorder, so no test
    /// changes the real theme.
    /// </summary>
    internal sealed class ThemeCalls
    {
        public static readonly ThemeCalls Windows = new()
        {
            Read = name => Registry.GetValue(@"HKEY_CURRENT_USER\" + PersonalizeKey, name, null) as int?,
            Write = (name, value) =>
            {
                using var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
                key.SetValue(name, value, RegistryValueKind.DWord);
            },
            Send = SendTimeout,
        };

        public required Func<string, int?> Read { get; init; }

        public required Action<string, int> Write { get; init; }

        /// <summary>SendMessageTimeout with a string lParam: window, message, wParam, the string, flags, wait per window.</summary>
        public required Action<nint, uint, nuint, string, SEND_MESSAGE_TIMEOUT_FLAGS, uint> Send { get; init; }
    }

    // Reads the first built-in panel only, while WriteBrightness sets every panel. On a device with
    // two built-in panels, undo restores both to the first panel's old level.
    private static int ReadBrightness()
    {
        using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
        using var found = searcher.Get();
        foreach (var monitor in found)
            using (monitor) return (byte)monitor["CurrentBrightness"];
        throw new ManagementException("No WmiMonitorBrightness instance.");
    }

    private static void WriteBrightness(int percent)
    {
        using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
        using var found = searcher.Get();
        var any = false;
        foreach (ManagementObject monitor in found)
            using (monitor)
            {
                monitor.InvokeMethod("WmiSetBrightness", [(uint)1, (byte)percent]);
                any = true;
            }
        if (!any) throw new ManagementException("No WmiMonitorBrightnessMethods instance.");
    }

    internal static string OnOff(bool on) => on ? "on" : "off";
}
