using System.Text.Json;
using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Device;
using Xunit;

namespace Tomte.Tests;

/// <summary>The device actions as tools: tiers, validation, the dry-run mapping, and the agent loop around them with a fake device.</summary>
public sealed class DeviceToolsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tomte-device-tools-{Guid.NewGuid():N}");
    private readonly ScriptedProvider _provider = new();
    private readonly PermissionGrants _grants = new();
    private readonly List<(string Tool, JsonElement Input, int Thread)> _ran = [];
    private readonly ManualResetEventSlim _deviceRan = new();
    private int _undone;
    private int _copiedOn;

    public void Dispose()
    {
        _deviceRan.Dispose();
        // A store's watcher may still be reading settings.json; a leaked temp folder is better than a failed test.
        for (int attempt = 0; attempt < 5 && Directory.Exists(_dir); attempt++)
        {
            try
            {
                Directory.Delete(_dir, recursive: true);
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static ToolCallPart Call(string name, string json, string id = "c1") => new(id, name, Json(json));

    // Every device call succeeds with an undo, like dark mode or the volume would.
    private ActionResult FakeDevice(string tool, JsonElement input)
    {
        _ran.Add((tool, input, Environment.CurrentManagedThreadId));
        _deviceRan.Set();
        return ActionResult.Done($"{tool} done.", () =>
        {
            _undone++;
            return ActionResult.Done($"{tool} put back.");
        });
    }

    private BuiltInTools Tools => new(new InMemoryShelf(), new InMemoryScratchpad(), _ => _copiedOn = Environment.CurrentManagedThreadId, _ => { }, FakeDevice);

    private PaletteSession Session(PermissionGrants? grants = null)
    {
        var config = new ProviderConfig("p", ProviderKind.OpenAICompatible, "http://localhost", "m");
        return new PaletteSession(_provider, config, new PaletteOptions(), new SendLog(Path.Combine(_dir, "send-log.jsonl")), Tools, grants ?? _grants, [])
        {
            Prompt = "do it",
        };
    }

    [Fact]
    public void OneToolPerDeviceActionWithItsTier()
    {
        var deviceTools = BuiltInTools.Definitions.Select(d => d.Name).Where(BuiltInTools.IsDeviceTool).ToList();
        Assert.Equal(DeviceActionTiers.All.Count, deviceTools.Count);
        Assert.Equal(5 + DeviceActionTiers.All.Count, BuiltInTools.Definitions.Count);
        // The same tiers as the device table, one for one.
        Assert.Equal(DeviceActionTiers.All.Values.Order(), deviceTools.Select(t => BuiltInTools.Tiers[t]).Order());
        Assert.Equal(ActionTier.ExternalEffect, BuiltInTools.Tiers[BuiltInTools.SetDarkMode]);
        Assert.Equal(ActionTier.Read, BuiltInTools.Tiers[BuiltInTools.ListOutputDevices]);
        Assert.All(new[] { BuiltInTools.LockPc, BuiltInTools.SleepPc, BuiltInTools.RestartPc, BuiltInTools.ShutDownPc },
            t => Assert.Equal(ActionTier.Destructive, BuiltInTools.Tiers[t]));
        Assert.False(BuiltInTools.IsDeviceTool(BuiltInTools.OpenUrl));
    }

    // A tool from outside (an MCP server) reaches the model as "server__tool". No built-in name may have a
    // double underscore, or an outside tool could take its place.
    [Fact]
    public void BuiltInNamesCannotBeTakenByAnOutsideTool() =>
        Assert.All(BuiltInTools.Definitions, d => Assert.DoesNotContain("__", d.Name));

    [Fact]
    public void DestructiveToolsAreOutOfScheduledJobs()
    {
        foreach (var tool in BuiltInTools.Definitions.Select(d => d.Name))
            Assert.Equal(BuiltInTools.Tiers[tool] != ActionTier.Destructive, BuiltInTools.AllowedInScheduledJobs(tool));
        Assert.False(BuiltInTools.AllowedInScheduledJobs(BuiltInTools.ShutDownPc));
        Assert.True(BuiltInTools.AllowedInScheduledJobs(BuiltInTools.SetVolume));
        Assert.False(BuiltInTools.AllowedInScheduledJobs("no_such_tool"));
    }

    [Fact]
    public void SchemasAreTypedAndNoArgumentToolsHaveNoProperties()
    {
        var volume = BuiltInTools.Definitions.Single(d => d.Name == BuiltInTools.SetVolume).InputSchema;
        Assert.Equal("integer", volume.GetProperty("properties").GetProperty("percent").GetProperty("type").GetString());
        Assert.Equal("percent", volume.GetProperty("required")[0].GetString());
        var dark = BuiltInTools.Definitions.Single(d => d.Name == BuiltInTools.SetDarkMode).InputSchema;
        Assert.Equal("boolean", dark.GetProperty("properties").GetProperty("on").GetProperty("type").GetString());
        var lockPc = BuiltInTools.Definitions.Single(d => d.Name == BuiltInTools.LockPc).InputSchema;
        Assert.Equal("object", lockPc.GetProperty("type").GetString());
        Assert.Empty(lockPc.GetProperty("properties").EnumerateObject());
        Assert.Empty(lockPc.GetProperty("required").EnumerateArray());
    }

    [Theory]
    [InlineData(BuiltInTools.SetVolume, """{}""", "\"percent\"")]
    [InlineData(BuiltInTools.SetVolume, """{"percent":"loud"}""", "whole-number")]
    [InlineData(BuiltInTools.SetVolume, """{"percent":12.5}""", "whole-number")]
    [InlineData(BuiltInTools.SetVolume, """{"percent":150}""", "between 0 and 100")]
    [InlineData(BuiltInTools.SetBrightness, """{"percent":-1}""", "between 0 and 100")]
    [InlineData(BuiltInTools.SetDarkMode, """{"on":"maybe"}""", "true or false")]
    [InlineData(BuiltInTools.SetDarkMode, """{"on":1}""", "true or false")]
    [InlineData(BuiltInTools.SetMute, """{}""", "\"muted\"")]
    [InlineData(BuiltInTools.SetOutputDevice, """{"name":""}""", "\"name\"")]
    [InlineData(BuiltInTools.OpenApp, """{"name":"https://example.com"}""", "URL")]
    [InlineData(BuiltInTools.OpenApp, """{"name":"ms-settings:display"}""", "OpenSettingsPage")]
    [InlineData(BuiltInTools.OpenApp, """{"name":"cmd\" /c del"}""", "quotes")]
    [InlineData(BuiltInTools.OpenSettingsPage, """{"uri":"display"}""", "ms-settings:display")]
    [InlineData(BuiltInTools.OpenSettingsPage, """{"uri":"ms-settings:display&x"}""", "ms-settings:display")]
    public void RejectsBadDeviceArgumentsBeforeAnythingRuns(string tool, string json, string expected)
    {
        var error = BuiltInTools.Validate(tool, Json(json));
        Assert.NotNull(error);
        Assert.Contains(expected, error, StringComparison.OrdinalIgnoreCase);
        var (result, undo) = Tools.Run(Call(tool, json));
        Assert.True(result.IsError);
        Assert.Null(undo);
        Assert.Empty(_ran);
    }

    [Theory]
    [InlineData(BuiltInTools.SetVolume, """{"percent":30}""")]
    [InlineData(BuiltInTools.SetVolume, """{"percent":"30"}""")]
    [InlineData(BuiltInTools.SetBrightness, """{"percent":0}""")]
    [InlineData(BuiltInTools.SetDarkMode, """{"on":false}""")]
    [InlineData(BuiltInTools.SetDarkMode, """{"on":"true"}""")]
    [InlineData(BuiltInTools.SetWifi, """{"on":true}""")]
    [InlineData(BuiltInTools.SetOutputDevice, """{"name":"headphones"}""")]
    [InlineData(BuiltInTools.OpenApp, """{"name":"notepad"}""")]
    [InlineData(BuiltInTools.OpenSettingsPage, """{"uri":"ms-settings:bluetooth"}""")]
    [InlineData(BuiltInTools.ListOutputDevices, """{}""")]
    [InlineData(BuiltInTools.LockPc, """{}""")]
    [InlineData(BuiltInTools.ShutDownPc, """{"force":true}""")]
    public void AcceptsGoodDeviceArguments(string tool, string json) =>
        Assert.Null(BuiltInTools.Validate(tool, Json(json)));

    // The dry-run path goes through the real Tomte.Core.Device functions and returns before any system call.
    [Theory]
    [InlineData(BuiltInTools.SetDarkMode, """{"on":false}""", "turn dark mode off")]
    [InlineData(BuiltInTools.SetDarkMode, """{"on":"true"}""", "turn dark mode on")]
    [InlineData(BuiltInTools.SetNightLight, """{"on":true}""", "Night light")]
    [InlineData(BuiltInTools.SetBrightness, """{"percent":40}""", "brightness to 40%")]
    [InlineData(BuiltInTools.SetWifi, """{"on":false}""", "Wi-Fi off")]
    [InlineData(BuiltInTools.SetBluetooth, """{"on":true}""", "Bluetooth on")]
    [InlineData(BuiltInTools.SetVolume, """{"percent":"30"}""", "volume to 30%")]
    [InlineData(BuiltInTools.SetMute, """{"muted":true}""", "mute sound")]
    [InlineData(BuiltInTools.SetMicMute, """{"muted":false}""", "unmute microphone")]
    [InlineData(BuiltInTools.SetOutputDevice, """{"name":"headphones"}""", "headphones")]
    [InlineData(BuiltInTools.OpenApp, """{"name":"notepad"}""", "open or focus notepad")]
    [InlineData(BuiltInTools.OpenSettingsPage, """{"uri":"ms-settings:display"}""", "ms-settings:display")]
    [InlineData(BuiltInTools.LockPc, """{}""", "lock the PC")]
    [InlineData(BuiltInTools.SleepPc, """{}""", "sleep")]
    [InlineData(BuiltInTools.RestartPc, """{}""", "restart the PC")]
    [InlineData(BuiltInTools.ShutDownPc, """{}""", "shut down the PC")]
    public void DryRunMapsEachToolToItsAction(string tool, string json, string expected)
    {
        var result = BuiltInTools.RunDevice(tool, Json(json), dryRun: true);
        Assert.True(result.Ok, result.Message);
        Assert.StartsWith("Dry run: would ", result.Message);
        Assert.Contains(expected, result.Message);
        Assert.False(result.UndoAvailable);
    }

    [Fact]
    public void DescribeGivesOneLinePerAction()
    {
        Assert.Equal("Turn dark mode off", BuiltInTools.Describe(BuiltInTools.SetDarkMode, Json("""{"on":false}""")));
        Assert.Equal("Set volume to 30%", BuiltInTools.Describe(BuiltInTools.SetVolume, Json("""{"percent":30}""")));
        Assert.Equal("Mute the microphone", BuiltInTools.Describe(BuiltInTools.SetMicMute, Json("""{"muted":true}""")));
        Assert.Equal("Open or focus notepad", BuiltInTools.Describe(BuiltInTools.OpenApp, Json("""{"name":"notepad"}""")));
        Assert.Equal("Shut down the PC", BuiltInTools.Describe(BuiltInTools.ShutDownPc, Json("{}")));
    }

    [Fact]
    public async Task ExternalEffectAsksOffersAlwaysAllowRaisesTheEventAndUndoes()
    {
        _provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.SetDarkMode, """{"on":false}"""));
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "Dark mode is off."));
        var session = Session();
        var asked = new List<ToolRequest>();
        var events = new List<DeviceEvent>();
        session.ConfirmTool = (request, _) =>
        {
            asked.Add(request);
            return Task.FromResult(PermissionAnswer.AllowOnce);
        };
        session.DeviceActionExecuted += events.Add;
        int caller = Environment.CurrentManagedThreadId;

        // The send returns where the device call hops to the pool. Waiting here keeps this thread busy, so the
        // pool cannot hand the call back to it and the thread check below means something.
        var send = session.SendAsync();
        Assert.True(_deviceRan.Wait(TimeSpan.FromSeconds(5)), "the device call never ran");
        await send;

        Assert.Equal(PaletteStatus.Done, session.Status);
        var request = Assert.Single(asked);
        Assert.Equal((BuiltInTools.SetDarkMode, "Turn dark mode off", ActionTier.ExternalEffect, true), (request.Tool, request.Summary, request.Tier, request.OfferAlwaysAllow));
        var run = Assert.Single(_ran);
        Assert.Equal(BuiltInTools.SetDarkMode, run.Tool);
        Assert.False(run.Input.GetProperty("on").GetBoolean());
        Assert.NotEqual(caller, run.Thread);
        Assert.Empty(_grants.AlwaysAllowed);

        var step = Assert.Single(session.Answer.OfType<ToolStepSegment>());
        Assert.False(step.IsError);
        Assert.Equal("set_dark_mode done.", step.Result);
        var e = Assert.Single(events);
        Assert.Equal("set_dark_mode done.", e.Line);
        Assert.NotNull(e.Undo);
        Assert.Same(step.Undo, e.Undo);

        Assert.Equal("set_dark_mode put back.", e.Undo!().Message);
        Assert.Equal(1, _undone);
        Assert.False(e.Undo().Ok, "undo is one-shot");
        Assert.Equal(1, _undone);
    }

    [Fact]
    public async Task DestructiveAlwaysAsksAndNeverTakesAlwaysAllow()
    {
        _provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.LockPc, "{}"));
        _provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.LockPc, "{}", "c2"));
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "Locked."));
        var session = Session();
        var asked = new List<ToolRequest>();
        session.ConfirmTool = (request, _) =>
        {
            asked.Add(request);
            return Task.FromResult(PermissionAnswer.AlwaysAllow);
        };

        await session.SendAsync();

        Assert.Equal(2, asked.Count);
        Assert.All(asked, r => Assert.Equal((ActionTier.Destructive, false), (r.Tier, r.OfferAlwaysAllow)));
        Assert.Equal(2, _ran.Count);
        Assert.Empty(_grants.AlwaysAllowed);
        Assert.True(_grants.NeedsConfirmation(BuiltInTools.LockPc, ActionTier.Destructive));
    }

    [Fact]
    public async Task DeniedAndInvalidCallsNeverReachTheDevice()
    {
        _provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.SetVolume, """{"percent":150}"""));
        _provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.SetVolume, """{"percent":50}""", "c2"));
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "ok"));
        var session = Session();
        int prompts = 0;
        session.ConfirmTool = (_, _) =>
        {
            prompts++;
            return Task.FromResult(PermissionAnswer.Deny);
        };
        var events = new List<DeviceEvent>();
        session.DeviceActionExecuted += events.Add;

        await session.SendAsync();

        Assert.Equal(1, prompts);
        Assert.Empty(_ran);
        Assert.Empty(events);
        var steps = session.Answer.OfType<ToolStepSegment>().ToList();
        Assert.Equal(2, steps.Count);
        Assert.All(steps, s => Assert.True(s.IsError));
        Assert.Contains("between 0 and 100", steps[0].Result);
        Assert.Equal("The user did not allow this.", steps[1].Result);
    }

    [Fact]
    public async Task ReadDeviceToolRunsWithoutAskingAndRaisesNoEvent()
    {
        _provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.ListOutputDevices, "{}"));
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "Speakers."));
        var session = Session();
        int prompts = 0;
        session.ConfirmTool = (_, _) =>
        {
            prompts++;
            return Task.FromResult(PermissionAnswer.Deny);
        };
        var events = new List<DeviceEvent>();
        session.DeviceActionExecuted += events.Add;

        await session.SendAsync();

        Assert.Equal(0, prompts);
        Assert.Single(_ran);
        Assert.Empty(events);
        Assert.Equal("list_output_devices done.", ((ToolResultPart)_provider.Requests[1].Messages[2].Parts[0]).Content);
    }

    // The clipboard's owner window belongs to the thread that first used it, so only device tools may leave the calling thread.
    [Fact]
    public async Task ANonDeviceToolRunsOnTheCallingThread()
    {
        _provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.CopyToClipboard, """{"text":"hi"}"""));
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "Copied."));
        var session = Session();
        session.ConfirmTool = (_, _) => Task.FromResult(PermissionAnswer.AllowOnce);

        // Nothing in a scripted send waits, so a tool that stays on this thread has run by the time SendAsync returns.
        var send = session.SendAsync();

        Assert.Equal(Environment.CurrentManagedThreadId, _copiedOn);
        Assert.True(send.IsCompleted);
        await send;
        Assert.Equal(PaletteStatus.Done, session.Status);
    }

    [Fact]
    public async Task AlwaysAllowRoundTripsThroughSettings()
    {
        var file = Path.Combine(_dir, "settings.json");
        using var store = new SettingsStore(file);
        var grants = new PermissionGrants(store);
        _provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.SetVolume, """{"percent":20}"""));
        _provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.SetVolume, """{"percent":40}""", "c2"));
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "ok"));
        var session = Session(grants);
        int prompts = 0;
        session.ConfirmTool = (_, _) =>
        {
            prompts++;
            return Task.FromResult(PermissionAnswer.AlwaysAllow);
        };
        // Save raises Changed on the thread that saved, which must not be the one that sent.
        int savedOn = 0;
        using var saved = new ManualResetEventSlim();
        store.Changed += _ =>
        {
            savedOn = Environment.CurrentManagedThreadId;
            saved.Set();
        };
        int caller = Environment.CurrentManagedThreadId;

        // The send returns where the grant hops to the pool; this thread stays busy until the save is done.
        var send = session.SendAsync();
        Assert.True(saved.Wait(TimeSpan.FromSeconds(5)), "the grant was never saved");
        await send;

        Assert.NotEqual(caller, savedOn);
        Assert.Equal(1, prompts);
        Assert.Equal(2, _ran.Count);
        Assert.Equal([BuiltInTools.SetVolume], store.Current.AlwaysAllowedTools);
        Assert.Contains("\"set_volume\"", File.ReadAllText(file));

        // The settings window's process reads the same file and shows the grant; removing it there asks again here.
        var reopened = SettingsStore.Load(file);
        Assert.Equal([BuiltInTools.SetVolume], reopened.AlwaysAllowedTools);
        Assert.False(new PermissionGrants(store).NeedsConfirmation(BuiltInTools.SetVolume, ActionTier.ExternalEffect));

        using var got = new ManualResetEventSlim();
        store.Changed += _ => got.Set();
        using (var window = new SettingsStore(file))
        {
            window.Save(window.Current with { AlwaysAllowedTools = [] });
        }

        Assert.True(got.Wait(TimeSpan.FromSeconds(5)), "no change notification");
        Assert.True(grants.NeedsConfirmation(BuiltInTools.SetVolume, ActionTier.ExternalEffect));
        Assert.Empty(grants.AlwaysAllowed);

        grants.AlwaysAllow(BuiltInTools.OpenUrl);
        grants.AlwaysAllow(BuiltInTools.SetDarkMode);
        grants.Revoke(BuiltInTools.OpenUrl);
        Assert.Equal([BuiltInTools.SetDarkMode], SettingsStore.Load(file).AlwaysAllowedTools);
    }
}
