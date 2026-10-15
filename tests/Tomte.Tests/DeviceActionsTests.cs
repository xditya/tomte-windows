using System.Reflection;
using Tomte.Core.Device;
using Xunit;

namespace Tomte.Tests;

// Pure parts only. Every real action call here passes dryRun: true, which returns before any
// registry, WMI, WinRT, Core Audio or power call. Dark mode is also run for real against a
// recorder that stands in for the registry and the broadcast.
public class DeviceActionsTests
{
    private static readonly Type[] Groups = [typeof(Display), typeof(Radios), typeof(Audio), typeof(Apps), typeof(Power)];

    [Fact]
    public void EveryActionHasATierAndPowerIsDestructive()
    {
        var actions = Groups
            .SelectMany(g => g.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Where(m => m.ReturnType == typeof(ActionResult))
                .Select(m => $"{g.Name}.{m.Name}"))
            .ToHashSet();

        Assert.Equal(actions, DeviceActionTiers.All.Keys.ToHashSet());
        foreach (var (action, tier) in DeviceActionTiers.All)
        {
            var expected = action.StartsWith("Power.", StringComparison.Ordinal) ? ActionTier.Destructive
                : action == "Audio.ListOutputDevices" ? ActionTier.Read
                : ActionTier.ExternalEffect;
            Assert.True(expected == tier, $"{action} is {tier}, expected {expected}");
        }
    }

    public static TheoryData<string, Func<ActionResult>> DryRuns => new()
    {
        { "dark mode", () => Display.SetDarkMode(true, dryRun: true) },
        { "night light", () => Display.SetNightLight(true, dryRun: true) },
        { "brightness", () => Display.SetBrightness(40, dryRun: true) },
        { "wifi off", () => Radios.SetWifi(false, dryRun: true) },
        { "bluetooth", () => Radios.SetBluetooth(true, dryRun: true) },
        { "volume", () => Audio.SetVolume(30, dryRun: true) },
        { "mute", () => Audio.SetMute(true, dryRun: true) },
        { "mic mute", () => Audio.SetMicMute(true, dryRun: true) },
        { "output device", () => Audio.SetOutputDevice("headphones", dryRun: true) },
        { "open app", () => Apps.OpenApp("notepad", dryRun: true) },
        { "settings page", () => Apps.OpenSettingsPage("ms-settings:display", dryRun: true) },
        { "lock", () => Power.Lock(dryRun: true) },
        { "sleep", () => Power.Sleep(dryRun: true) },
        { "restart", () => Power.Restart(dryRun: true) },
        { "shut down", () => Power.ShutDown(dryRun: true) },
    };

    [Theory]
    [MemberData(nameof(DryRuns))]
    public void DryRunReportsWithoutUndo(string name, Func<ActionResult> action)
    {
        var result = action();
        Assert.True(result.Ok, name);
        Assert.StartsWith("Dry run: would ", result.Message);
        Assert.False(result.UndoAvailable);
    }

    [Fact]
    public void UndoRunsOnceThenReportsNothingLeft()
    {
        var calls = 0;
        var result = ActionResult.Done("Volume 40%.", () => { calls++; return ActionResult.Done("Volume back."); });

        Assert.True(result.UndoAvailable);
        Assert.Equal("Volume back.", result.Undo().Message);
        Assert.False(result.UndoAvailable);
        Assert.False(result.Undo().Ok);
        Assert.Equal(1, calls);
        Assert.False(ActionResult.Fail("no").UndoAvailable);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(101)]
    public void OutOfRangeLevelsFailEvenInDryRun(int percent)
    {
        Assert.False(Display.SetBrightness(percent, dryRun: true).Ok);
        Assert.False(Audio.SetVolume(percent, dryRun: true).Ok);
    }

    [Theory]
    [InlineData("ms-settings:display", true)]
    [InlineData("MS-SETTINGS:network-wifi", true)]
    [InlineData("ms-settings:", false)]
    [InlineData("ms-settings:display&x", false)]
    [InlineData("https://example.com", false)]
    [InlineData("display", false)]
    public void SettingsUriValidation(string uri, bool valid) =>
        Assert.Equal(valid, Apps.IsSettingsUri(uri));

    [Theory]
    [InlineData("notepad", true)]
    [InlineData(@"C:\Users\me\Desktop\Tool.lnk", true)]
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", true)]
    [InlineData("", false)]
    [InlineData("https://example.com", false)]
    [InlineData("ms-settings:display", false)]
    [InlineData("cmd\" /c del", false)]
    [InlineData(@"C:\Tools\obs64.exe", true)]
    [InlineData("mailto:a@b.c", false)]
    [InlineData("calculator:", false)]
    [InlineData("someapp:open?x=1&y=2", false)]
    [InlineData("shell:startup", false)]
    [InlineData(@"\\host\share\tool.exe", false)]
    public void AppTargetValidation(string target, bool valid) =>
        Assert.Equal(valid, Apps.ValidateAppTarget(target) is null);

    // Only a plain name or an exe path can be matched to a running process for the focus half of "open or focus".
    [Theory]
    [InlineData("notepad", "notepad")]
    [InlineData("Notepad.exe", "Notepad")]
    [InlineData(@"C:\Tools\obs64.exe", "obs64")]
    [InlineData(@"C:\Users\me\Desktop\Tool.lnk", null)]
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App", null)]
    public void ProcessNameComesFromNamesAndExePathsOnly(string target, string? expected) =>
        Assert.Equal(expected, Apps.ProcessNameFor(target));

    // "Settings" is no program the shell finds by name; open_app falls back to the app Start lists under it. Nothing opens.
    [Fact]
    public void ANameStartShowsIsFoundInStartsList()
    {
        Assert.StartsWith(@"shell:AppsFolder\windows.immersivecontrolpanel_", Apps.StartApp("settings")!.Target, StringComparison.OrdinalIgnoreCase);
        Assert.Null(Apps.StartApp("Settings2 that no app is called"));
        Assert.Null(Apps.StartApp(@"C:\Windows\Settings"));
    }

    private static readonly OutputDevice[] Devices =
    [
        new("a", "Speakers (Realtek Audio)", true),
        new("b", "Headphones (WH-1000XM4)", false),
        new("c", "Headset (WH-1000XM4 Hands-Free)", false),
    ];

    [Theory]
    [InlineData("speakers (realtek audio)", "a")]
    [InlineData("headphones", "b")]
    [InlineData("hands-free", "c")]
    [InlineData("WH-1000XM4", null)]
    [InlineData("hdmi", null)]
    public void PickDeviceNeedsAUniqueMatch(string query, string? expectedId)
    {
        var (device, error) = Audio.PickDevice(Devices, query);
        Assert.Equal(expectedId, device?.Id);
        Assert.Equal(expectedId is null, error is not null);
    }

    [Fact]
    public void DarkModeAndItsUndoWriteBothValuesThenSendOneBroadcastBeforeReturning()
    {
        var calls = new List<string>();
        var threads = new HashSet<int>();
        // Apps dark, the taskbar and Start light: the undo must put back exactly this.
        var values = new Dictionary<string, int> { ["AppsUseLightTheme"] = 0, ["SystemUsesLightTheme"] = 1 };
        var recorder = Recorder(calls, threads, values);

        var result = Display.SetDarkMode(on: false, dryRun: false, recorder);

        // Recorded before SetDarkMode returned, on this thread: the change is not reported done while windows
        // are still being told, and nothing is left running for a quitting process to cut short.
        const string broadcast = "send ffff 1A 0 ImmersiveColorSet SMTO_ABORTIFHUNG 5000";
        Assert.Equal(["write AppsUseLightTheme=1", "write SystemUsesLightTheme=1", broadcast], calls);
        Assert.Equal("Dark mode off.", result.Message);

        calls.Clear();
        Assert.Equal("Restored the previous theme.", result.Undo().Message);

        Assert.Equal(["write AppsUseLightTheme=0", "write SystemUsesLightTheme=1", broadcast], calls);
        Assert.Equal([Environment.CurrentManagedThreadId], threads);
    }

    [Fact]
    public async Task ASecondThemeChangeWaitsUntilTheFirstBroadcastHasFinished()
    {
        var calls = new List<string>();
        using var broadcasting = new ManualResetEventSlim();
        using var finish = new ManualResetEventSlim();
        int sends = 0;
        var recorder = new Display.ThemeCalls
        {
            Read = _ => 1,
            Write = (name, _) =>
            {
                lock (calls)
                {
                    calls.Add("write");
                }
            },
            Send = (_, _, _, _, _, _) =>
            {
                lock (calls)
                {
                    calls.Add("send");
                }

                if (Interlocked.Increment(ref sends) == 1)
                {
                    broadcasting.Set();
                    finish.Wait(TimeSpan.FromSeconds(10));
                }
            },
        };

        var first = Task.Run(() => Display.SetDarkMode(on: true, dryRun: false, recorder));
        Assert.True(broadcasting.Wait(TimeSpan.FromSeconds(10)));
        var second = Task.Run(() => Display.SetDarkMode(on: false, dryRun: false, recorder));
        // Without the gate the second change's writes land here, while windows are still taking the first broadcast.
        await Task.Delay(200);
        lock (calls)
        {
            Assert.Equal(["write", "write", "send"], calls);
        }

        finish.Set();
        await Task.WhenAll(first, second);
        Assert.Equal(["write", "write", "send", "write", "write", "send"], calls);
    }

    private static Display.ThemeCalls Recorder(List<string> calls, HashSet<int> threads, Dictionary<string, int> values) => new()
    {
        Read = name => values.TryGetValue(name, out int value) ? value : null,
        Write = (name, value) =>
        {
            values[name] = value;
            calls.Add($"write {name}={value}");
            threads.Add(Environment.CurrentManagedThreadId);
        },
        Send = (window, message, wParam, text, flags, timeoutMs) =>
        {
            calls.Add($"send {window:x} {message:X} {wParam} {text} {flags} {timeoutMs}");
            threads.Add(Environment.CurrentManagedThreadId);
        },
    };
}
