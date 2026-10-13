// Manual check for Tomte.Core.Device.
//   dry      every action in dry-run mode; changes nothing
//   outputs  list the audio output devices; changes nothing
//   safe     dark mode, volume, mute and brightness: change each, then undo it at once
using Tomte.Core.Device;

switch (args.FirstOrDefault())
{
    case "dry":
        Show("Display.SetDarkMode(on)", Display.SetDarkMode(true, dryRun: true));
        Show("Display.SetNightLight(on)", Display.SetNightLight(true, dryRun: true));
        Show("Display.SetBrightness(40)", Display.SetBrightness(40, dryRun: true));
        Show("Display.SetBrightness(140)", Display.SetBrightness(140, dryRun: true));
        Show("Radios.SetWifi(off)", Radios.SetWifi(false, dryRun: true));
        Show("Radios.SetBluetooth(off)", Radios.SetBluetooth(false, dryRun: true));
        Show("Audio.SetVolume(30)", Audio.SetVolume(30, dryRun: true));
        Show("Audio.SetMute(true)", Audio.SetMute(true, dryRun: true));
        Show("Audio.SetMicMute(true)", Audio.SetMicMute(true, dryRun: true));
        Show("Audio.SetOutputDevice(\"speakers\")", Audio.SetOutputDevice("speakers", dryRun: true));
        Show("Audio.SetOutputDevice(\"nvidia\")", Audio.SetOutputDevice("nvidia", dryRun: true));
        Show("Apps.OpenApp(notepad)", Apps.OpenApp("notepad", dryRun: true));
        Show("Apps.OpenSettingsPage(ms-settings:display)", Apps.OpenSettingsPage("ms-settings:display", dryRun: true));
        Show("Power.Lock", Power.Lock(dryRun: true));
        Show("Power.Sleep", Power.Sleep(dryRun: true));
        Show("Power.Restart", Power.Restart(dryRun: true));
        Show("Power.ShutDown", Power.ShutDown(dryRun: true));
        break;

    case "outputs":
        Show("Audio.ListOutputDevices", Audio.ListOutputDevices());
        break;

    case "safe":
        var dark = Microsoft.Win32.Registry.GetValue(
            @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize", "AppsUseLightTheme", 1) as int? == 0;
        RoundTrip("Display.SetDarkMode", () => Display.SetDarkMode(!dark));
        RoundTrip("Audio.SetVolume(7)", () => Audio.SetVolume(7));
        RoundTrip("Audio.SetMute(true)", () => Audio.SetMute(true));
        RoundTrip("Display.SetBrightness(50)", () => Display.SetBrightness(50));
        break;

    default:
        Console.WriteLine("usage: DeviceHarness dry | outputs | safe");
        return 1;
}
return 0;

static void Show(string action, ActionResult r) =>
    Console.WriteLine($"{action,-45} ok={r.Ok,-5} undo={r.UndoAvailable,-5} {r.Message.ReplaceLineEndings(" | ")}");

static void RoundTrip(string action, Func<ActionResult> run)
{
    var clock = System.Diagnostics.Stopwatch.StartNew();
    var r = run();
    Show($"{action} [{clock.ElapsedMilliseconds} ms]", r);
    Thread.Sleep(1000);
    clock.Restart();
    var undo = r.Undo();
    Show($"  undo [{clock.ElapsedMilliseconds} ms]", undo);
}
