using System.Reflection;
using Tomte.Core.Device;
using Xunit;

namespace Tomte.Tests;

// Pure parts only. Every real action call here passes dryRun: true, which returns before any
// registry, WMI, WinRT, Core Audio or power call.
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
    public void AppTargetValidation(string target, bool valid) =>
        Assert.Equal(valid, Apps.ValidateAppTarget(target) is null);

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
}
