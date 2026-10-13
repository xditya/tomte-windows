using System.Management;
using Microsoft.Win32;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Core.Device;

public static class Display
{
    private const string PersonalizeKey = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

    // Dark mode is two per-user values (0 = dark) plus a broadcast so running apps and the shell repaint.
    public static ActionResult SetDarkMode(bool on, bool dryRun = false)
    {
        if (dryRun) return ActionResult.DryRun($"turn dark mode {OnOff(on)}.");

        using var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey);
        var apps = key.GetValue("AppsUseLightTheme") as int? ?? 1;
        var system = key.GetValue("SystemUsesLightTheme") as int? ?? 1;
        var light = on ? 0 : 1;
        WriteTheme(light, light);
        return ActionResult.Done($"Dark mode {OnOff(on)}.", () =>
        {
            WriteTheme(apps, system);
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

    private static void WriteTheme(int apps, int system)
    {
        using (var key = Registry.CurrentUser.CreateSubKey(PersonalizeKey))
        {
            key.SetValue("AppsUseLightTheme", apps, RegistryValueKind.DWord);
            key.SetValue("SystemUsesLightTheme", system, RegistryValueKind.DWord);
        }
        // Every top-level window repaints before the broadcast returns (about 3 s measured), so it
        // runs off the caller's thread. The registry is already written; the repaint follows.
        _ = Task.Run(() => Broadcast("ImmersiveColorSet"));
    }

    private static unsafe void Broadcast(string setting)
    {
        fixed (char* name = setting)
        {
            PInvoke.SendMessageTimeout(
                new HWND(0xffff), // HWND_BROADCAST
                PInvoke.WM_SETTINGCHANGE,
                default,
                (nint)name,
                SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG,
                200,
                null);
        }
    }

    private static int ReadBrightness()
    {
        using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT CurrentBrightness FROM WmiMonitorBrightness");
        foreach (var monitor in searcher.Get())
            using (monitor) return (byte)monitor["CurrentBrightness"];
        throw new ManagementException("No WmiMonitorBrightness instance.");
    }

    private static void WriteBrightness(int percent)
    {
        using var searcher = new ManagementObjectSearcher(@"root\wmi", "SELECT * FROM WmiMonitorBrightnessMethods");
        var any = false;
        foreach (ManagementObject monitor in searcher.Get())
            using (monitor)
            {
                monitor.InvokeMethod("WmiSetBrightness", [(uint)1, (byte)percent]);
                any = true;
            }
        if (!any) throw new ManagementException("No WmiMonitorBrightnessMethods instance.");
    }

    internal static string OnOff(bool on) => on ? "on" : "off";
}
