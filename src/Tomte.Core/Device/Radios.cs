using Windows.Devices.Radios;

namespace Tomte.Core.Device;

// Windows.Devices.Radios needs the "radios" device capability, which only a packaged (MSIX)
// app has. Unpackaged, or when the user has blocked radio access, the call is refused and
// the matching settings page opens instead.
public static class Radios
{
    public static ActionResult SetWifi(bool on, bool dryRun = false) =>
        Set(RadioKind.WiFi, "Wi-Fi", "ms-settings:network-wifi", on, dryRun);

    public static ActionResult SetBluetooth(bool on, bool dryRun = false) =>
        Set(RadioKind.Bluetooth, "Bluetooth", "ms-settings:bluetooth", on, dryRun);

    // blocks on the WinRT calls (tens of ms); callers already run off the UI thread.
    private static ActionResult Set(RadioKind kind, string name, string settingsPage, bool on, bool dryRun)
    {
        if (dryRun) return ActionResult.DryRun($"turn {name} {Display.OnOff(on)}.");

        Radio? radio = null;
        try
        {
            if (Radio.RequestAccessAsync().AsTask().GetAwaiter().GetResult() == RadioAccessStatus.Allowed)
                radio = Radio.GetRadiosAsync().AsTask().GetAwaiter().GetResult().FirstOrDefault(r => r.Kind == kind);
        }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Runtime.InteropServices.COMException)
        {
        }
        if (radio is null) return Fallback(name, settingsPage, on);

        var previous = radio.State;
        var target = on ? RadioState.On : RadioState.Off;
        if (radio.SetStateAsync(target).AsTask().GetAwaiter().GetResult() != RadioAccessStatus.Allowed)
            return Fallback(name, settingsPage, on);
        return ActionResult.Done($"{name} {Display.OnOff(on)}.", () =>
            radio.SetStateAsync(previous).AsTask().GetAwaiter().GetResult() == RadioAccessStatus.Allowed
                ? ActionResult.Done($"{name} back to {previous.ToString().ToLowerInvariant()}.")
                : ActionResult.Fail($"Could not set {name} back."));
    }

    private static ActionResult Fallback(string name, string settingsPage, bool on)
    {
        var opened = Apps.OpenSettingsPage(settingsPage);
        return opened.Ok
            ? ActionResult.Done($"Windows did not let Tomte switch {name}; opened {name} settings to turn it {Display.OnOff(on)}.")
            : opened;
    }
}
