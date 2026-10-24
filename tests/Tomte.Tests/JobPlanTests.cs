using System.Text.Json;
using Tomte.AI;
using Tomte.AI.Automation;
using Tomte.AI.Browser;
using Tomte.AI.Jobs;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Jobs;
using Tomte.Core.Storage;
using Tomte.Core.Text;
using Windows.Win32.UI.Accessibility;
using Xunit;

namespace Tomte.Tests;

/// <summary>
/// A job's plan: the steps, sites and apps schedule_job takes, the card that shows them, how they are stored and sent
/// at each run, and how a run keeps to them with nobody there.
/// </summary>
public sealed class JobPlanTests : IDisposable
{
    private static readonly ProviderConfig Haiku = new("api.anthropic.com", ProviderKind.Anthropic, null, "claude-haiku-5-5");

    private static readonly string[] SubCountTools =
    [
        BrowserTools.OpenPage, BrowserTools.ClickOn, BrowserTools.ReadPage, BuiltInTools.OpenApp,
        AutomationTools.ListControls, AutomationTools.TypeInControl, AutomationTools.ClickControl, AutomationTools.PressKeys,
    ];

    private static readonly string[] SubCountSteps =
    [
        "Open studio.youtube.com.",
        "Click Analytics.",
        "Read the subscriber count.",
        "Open Telegram.",
        "Search for Rahul and open the chat.",
        "Type \"Subscribers today: <count>\".",
        "Press Enter to send.",
    ];

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tomte-plan-{Guid.NewGuid():N}");
    private readonly JobStore _store;
    private readonly JobPlanner _planner;

    public JobPlanTests()
    {
        _store = new JobStore(new TomteDb(Path.Combine(_dir, "tomte.db")));
        _planner = new JobPlanner(_store, new NoTasks());
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static JsonElement Call(object? steps = null, object? sites = null, object? apps = null, string[]? tools = null) =>
        JsonSerializer.SerializeToElement(new Dictionary<string, object?>
        {
            ["name"] = "Sub count to Rahul",
            ["prompt"] = "Read my YouTube subscriber count and send it to Rahul on Telegram.",
            ["time"] = "21:00",
            ["days"] = new[] { "mon", "tue", "wed", "thu", "fri", "sat", "sun" },
            ["tools"] = tools ?? SubCountTools,
            ["steps"] = steps,
            ["sites"] = sites,
            ["apps"] = apps,
        }.Where(p => p.Value is not null).ToDictionary());

    [Fact]
    public void ScheduleJobTakesStepsSitesAndApps()
    {
        var (draft, error) = _planner.Read(Call(SubCountSteps, new[] { "https://studio.youtube.com/", "*.YouTube.com", "youtube.com" }, new[] { "Telegram", "telegram.exe" }));

        Assert.Null(error);
        Assert.Equal(SubCountSteps, draft!.Steps);
        // Kept as host names, each once; the app once, as the model named it first.
        Assert.Equal(["studio.youtube.com", "youtube.com"], draft.Sites);
        Assert.Equal(["Telegram"], draft.Apps);
    }

    [Fact]
    public void BrowserAndAppToolsNeedTheirSitesAppsAndSteps()
    {
        Assert.Equal("sites must list the domains the browser may visit, such as youtube.com, since tools has browser tools.",
            _planner.Read(Call(SubCountSteps, apps: new[] { "Telegram" })).Error);
        Assert.Equal("apps must list the apps the job works in, such as Telegram, since tools has app tools.",
            _planner.Read(Call(SubCountSteps, sites: new[] { "youtube.com" })).Error);
        Assert.Equal("steps must give the plan, one short step each, since the job works in the browser or in apps.",
            _planner.Read(Call(sites: new[] { "youtube.com" }, apps: new[] { "Telegram" })).Error);
        Assert.Equal("\"192.168.1.1\" is not a site. Give domain names such as youtube.com.",
            _planner.Read(Call(SubCountSteps, new[] { "192.168.1.1" }, new[] { "Telegram" })).Error);
        Assert.Equal("\"C:\\Tools\\x.exe\" is not an app name. Give program names such as Telegram or notepad.",
            _planner.Read(Call(SubCountSteps, new[] { "youtube.com" }, new[] { @"C:\Tools\x.exe" })).Error);
        Assert.Contains("at most 10 short steps", _planner.Read(Call(Enumerable.Repeat("Click.", 11).ToArray(), new[] { "youtube.com" }, new[] { "Telegram" })).Error);

        // A job with neither keeps working as before: no plan needed.
        var (plain, none) = _planner.Read(Call(tools: [BuiltInTools.ReadShelfItem]));
        Assert.Null(none);
        Assert.Empty(plain!.Steps);
        Assert.True(JobPlanner.IsJobTool(BrowserTools.ClickOn) && JobPlanner.IsJobTool(AutomationTools.PressKeys));
        Assert.False(JobPlanner.IsJobTool(BuiltInTools.ShutDownPc));
    }

    [Fact]
    public async Task AnAppThePcLacksIsSentBackBeforeTheCard()
    {
        var planner = new JobPlanner(_store, new NoTasks()) { Installed = app => app != "Telegram" };
        bool asked = false;
        var (_, result, isError) = await planner.ScheduleAsync(new ToolCallPart("1", JobPlanner.Tool, Call(SubCountSteps, new[] { "youtube.com" }, new[] { "Telegram" })),
            Haiku, (_, _) => { asked = true; return Task.FromResult(PermissionAnswer.AllowOnce); }, CancellationToken.None);

        Assert.True(isError);
        Assert.False(asked);
        Assert.Equal("Telegram is not installed on this PC. Ask the user which installed app to use instead, then call schedule_job again.", result);
        Assert.Empty(_store.All());
    }

    [Fact]
    public void TheCardShowsThePlanSitesAppsToolsAndProviderWhole()
    {
        var draft = _planner.Read(Call(SubCountSteps, new[] { "youtube.com" }, new[] { "Telegram" })).Draft!;
        string card = JobPlanner.Confirmation(draft, Haiku);

        Assert.Equal("""
            Every day at 21:00: Read my YouTube subscriber count and send it to Rahul on Telegram.
            1. Open studio.youtube.com.
            2. Click Analytics.
            3. Read the subscriber count.
            4. Open Telegram.
            5. Search for Rahul and open the chat.
            6. Type "Subscribers today: <count>".
            7. Press Enter to send.
            Sites: youtube.com. Apps: Telegram.
            Uses browser_open, browser_click, browser_read, open_app, list_controls, type_in_control, click_control and press_keys. Sends to Anthropic · claude-haiku-5-5 each time.
            """.ReplaceLineEndings("\n"), card);
        Assert.True(PaletteLayout.FitsJobCard(card));

        // The card, as tall as the plan needs, stays under the prompt in the tallest palette.
        var layout = new PaletteLayout(720, mascot: true, confirm: new Confirmation(card, "", AlwaysAllow: false, Destructive: false, Job: true));
        var ask = layout.Ask(layout.Height(0))!.Value;
        using var laid = TextLayout.Of(ask.Summary);
        Assert.False(laid.Trimmed);
        Assert.True(ask.Card.Y >= layout.AnswerTop - PaletteLayout.Gap, $"card at {ask.Card.Y}, answer at {layout.AnswerTop}");

        // A plan too long to read whole is refused, not cut.
        var longer = draft with { Steps = [.. Enumerable.Repeat("Click the button that says Next and wait for the page to load fully.", 10)] };
        Assert.False(PaletteLayout.FitsJobCard(JobPlanner.Confirmation(longer, Haiku)));
    }

    [Fact]
    public void ThePlanIsStoredAndSentWithEachRun()
    {
        var draft = _planner.Read(Call(SubCountSteps, new[] { "youtube.com" }, new[] { "Telegram" })).Draft!;
        _store.Add(draft, Haiku, DateTimeOffset.Now, TimeZoneInfo.Local);
        _store.Add(new JobDraft("Old", "Read notes.txt.", draft.Schedule, [BuiltInTools.ReadShelfItem]), Haiku, DateTimeOffset.Now, TimeZoneInfo.Local);

        var (job, old) = (_store.All()[0], _store.All()[1]);
        Assert.Equal(SubCountSteps, job.Steps);
        Assert.Equal(["youtube.com"], job.Sites);
        Assert.Equal(["Telegram"], job.Apps);
        Assert.EndsWith("Sites: youtube.com. Apps: Telegram.", job.Describe(TimeZoneInfo.Local));
        Assert.Equal("""
            Read my YouTube subscriber count and send it to Rahul on Telegram.

            The plan the user approved. Follow it step by step:
            1. Open studio.youtube.com.
            2. Click Analytics.
            3. Read the subscriber count.
            4. Open Telegram.
            5. Search for Rahul and open the chat.
            6. Type "Subscribers today: <count>".
            7. Press Enter to send.
            The browser may only load youtube.com and their subdomains.
            Only these apps may be used: Telegram.
            """.ReplaceLineEndings("\n"), JobPlanner.RunPrompt(job));

        // A job made before plans reads with none and sends its prompt alone.
        Assert.Empty(old.Steps);
        Assert.Empty(old.Sites);
        Assert.Equal("Read notes.txt.", JobPlanner.RunPrompt(old));
    }

    [Theory]
    [InlineData("Telegram", true)]
    [InlineData("C:\\Users\\a\\AppData\\Roaming\\Telegram Desktop\\Telegram.exe", true)]
    [InlineData("TELEGRAM.EXE", true)]
    [InlineData("WhatsApp.Root.exe", false)]
    [InlineData("TelegramX.exe", false)]
    [InlineData("explorer.exe", false)]
    public void AnAppIsApprovedByItsFileName(string exe, bool approved) => Assert.Equal(approved, AutomationTools.Approved(["telegram"], exe));

    [Fact]
    public void AnAppNameAlsoCoversItsDottedProgramName() => Assert.True(AutomationTools.Approved(["WhatsApp"], "WhatsApp.Root.exe"));

    [Theory]
    [InlineData("Enter", "0D")]
    [InlineData("ctrl+k", "11 4B")]
    [InlineData("Ctrl+A Delete", "11 41|2E")]
    [InlineData("Ctrl+Shift+F", "11 10 46")]
    [InlineData("Alt+F4", "12 73")]
    [InlineData("Down, Down", "28|28")]
    public void KeysAreReadAsChords(string keys, string expected)
    {
        var (chords, error) = KeyPress.Parse(keys);
        Assert.Null(error);
        Assert.Equal(expected, string.Join('|', chords.Select(c => string.Join(' ', c.Select(k => k.ToString("X2"))))));
    }

    [Theory]
    [InlineData("Win+R", "Tomte does not press the Windows key.")]
    [InlineData("Hyper+K", "\"Hyper\" is not Ctrl, Shift or Alt.")]
    [InlineData("Ctrl+PrintScreen", "\"PrintScreen\" is not a key press_keys knows.")]
    [InlineData("  ", "keys is empty.")]
    public void KeysThatCannotBePressedAreRefused(string keys, string error) => Assert.Equal(error, KeyPress.Parse(keys).Error);

    // ---- A run keeps to its apps ----

    private const nint TelegramWindow = 0x200, ExplorerWindow = 0x300;

    private static FakeUia Desktop()
    {
        var uia = new FakeUia();
        uia.TopWindows.Add(new(ExplorerWindow, 10, "Downloads"));
        uia.TopWindows.Add(new(TelegramWindow, 20, "Telegram"));
        uia.Processes[10] = new("explorer.exe", false);
        uia.Processes[20] = new("Telegram.exe", false);
        uia.Controls[TelegramWindow] = [new([7, 1], "search", "Search", UIA_CONTROLTYPE_ID.UIA_EditControlTypeId, 20, UiaPatterns.Value)];
        uia.Controls[ExplorerWindow] = [new([7, 2], "del", "Delete", UIA_CONTROLTYPE_ID.UIA_ButtonControlTypeId, 10, UiaPatterns.Invoke)];
        return uia;
    }

    private static AutomationTools JobTools(FakeUia uia, List<nint> fronted, Func<bool>? locked = null) =>
        new(new UiAutomationSettings { Enabled = true }, [], 0, locked ?? (() => false), () => uia, apps: ["Telegram"])
        {
            Front = w =>
            {
                fronted.Add(w);
                return true;
            },
            Send = _ => true,
        };

    private static Task<PermissionAnswer> Yes(ToolRequest request, CancellationToken ct) => Task.FromResult(PermissionAnswer.AllowOnce);

    private static ToolCallPart Tool(string name, string json) => new("c1", name, JsonDocument.Parse(json).RootElement);

    [Fact]
    public async Task AJobWorksOnlyInItsApprovedAppsAndAnyOtherEndsTheRun()
    {
        var uia = Desktop();
        var fronted = new List<nint>();
        using var tools = JobTools(uia, fronted);

        var telegram = await tools.RunAsync(Tool(AutomationTools.ListControls, """{"window":"telegram"}"""), Yes, CancellationToken.None);
        Assert.False(telegram.IsError, telegram.Result);
        Assert.Contains("\"Search\"", telegram.Result);
        Assert.Equal([TelegramWindow], fronted);

        var explorer = await tools.RunAsync(Tool(AutomationTools.ListControls, """{"window":"Downloads"}"""), Yes, CancellationToken.None);
        Assert.True(explorer.Stop);
        Assert.Equal("explorer is not one of the apps this job may use (Telegram), so the run stopped.", explorer.Result);

        var keys = await tools.RunAsync(Tool(AutomationTools.PressKeys, """{"app":"explorer","keys":"Delete"}"""), Yes, CancellationToken.None);
        Assert.True(keys.Stop);

        var sent = await tools.RunAsync(Tool(AutomationTools.PressKeys, """{"app":"Telegram","keys":"Enter"}"""), Yes, CancellationToken.None);
        Assert.Equal("Pressed Enter in Telegram.", sent.Result);

        // Not a window of any app: an error the model can act on (open the app first), not the end of the run.
        var missing = await tools.RunAsync(Tool(AutomationTools.ListControls, """{"window":"Rahul"}"""), Yes, CancellationToken.None);
        Assert.False(missing.Stop);
        Assert.Contains("Open the app first", missing.Result);
    }

    [Fact]
    public async Task ALockedPcEndsAJobsAppStep()
    {
        using var tools = JobTools(Desktop(), [], locked: () => true);
        var step = await tools.RunAsync(Tool(AutomationTools.ListControls, """{"window":"Telegram"}"""), Yes, CancellationToken.None);
        Assert.True(step.Stop);
        Assert.Equal("Your PC was locked, so the job could not use Telegram.", step.Result);
    }

    [Fact]
    public async Task ARunOnALockedPcFailsBeforeAnythingIsSent()
    {
        var job = _store.Add(_planner.Read(Call(SubCountSteps, new[] { "youtube.com" }, new[] { "Telegram" })).Draft!, Haiku, DateTimeOffset.Now, TimeZoneInfo.Local);
        var provider = new ScriptedProvider();
        bool made = false;
        var runner = new JobRunner(_store, new NoTasks(), _ => provider, new BuiltInTools(new InMemoryShelf(), new InMemoryScratchpad(), _ => { }, _ => { }),
            new SendLog(Path.Combine(_dir, "send-log.jsonl")), _ => new PaletteOptions())
        {
            Locked = () => true,
            Automation = _ =>
            {
                made = true;
                return null;
            },
        };

        var run = await runner.RunAsync(job, DateTimeOffset.Now);

        Assert.False(run.Ok);
        Assert.Equal("Your PC was locked, so the job could not use Telegram.", run.Text);
        Assert.Empty(provider.Requests);
        Assert.False(made);
    }

    [Fact]
    public async Task ARunThatReachesForAnotherAppStopsThere()
    {
        var job = _store.Add(_planner.Read(Call(SubCountSteps, new[] { "youtube.com" }, new[] { "Telegram" })).Draft!, Haiku, DateTimeOffset.Now, TimeZoneInfo.Local);
        var provider = new ScriptedProvider();
        provider.Turns.Add(ScriptedProvider.Call(AutomationTools.ListControls, """{"window":"Downloads"}"""));
        provider.Turns.Add(ScriptedProvider.Text(deltas: "done"));
        using var tools = JobTools(Desktop(), []);
        var session = new PaletteSession(provider, Haiku, new PaletteOptions(), new SendLog(Path.Combine(_dir, "send-log.jsonl")),
            new BuiltInTools(new InMemoryShelf(), new InMemoryScratchpad(), _ => { }, _ => { }), new PermissionGrants(), [])
        {
            Job = job,
            Prompt = JobPlanner.RunPrompt(job),
            Automation = tools,
            ConfirmTool = (request, _) => Task.FromResult(job.Tools.Contains(request.Tool) ? PermissionAnswer.AllowOnce : PermissionAnswer.Deny),
        };

        await session.SendAsync();

        Assert.Equal(PaletteStatus.Failed, session.Status);
        Assert.Equal("explorer is not one of the apps this job may use (Telegram), so the run stopped.", session.Error);
        Assert.Single(provider.Requests);
        // The job's own tools are offered, the browser's and the apps' included; nothing it was not approved for.
        Assert.Equal(SubCountTools.Order(), provider.Requests[0].Tools!.Select(t => t.Name).Order());
    }

    [Fact]
    public async Task AJobThatOpensAnotherAppStops()
    {
        var job = _store.Add(_planner.Read(Call(SubCountSteps, new[] { "youtube.com" }, new[] { "Telegram" })).Draft!, Haiku, DateTimeOffset.Now, TimeZoneInfo.Local);
        var provider = new ScriptedProvider();
        provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.OpenApp, """{"name":"cmd"}"""));
        var opened = new List<string>();
        var session = new PaletteSession(provider, Haiku, new PaletteOptions(), new SendLog(Path.Combine(_dir, "send-log.jsonl")),
            new BuiltInTools(new InMemoryShelf(), new InMemoryScratchpad(), _ => { }, _ => { }, (name, _) =>
            {
                opened.Add(name);
                return Tomte.Core.Device.ActionResult.Done("opened");
            }), new PermissionGrants(), [])
        {
            Job = job,
            Prompt = job.Prompt,
            ConfirmTool = (_, _) => Task.FromResult(PermissionAnswer.AllowOnce),
        };

        await session.SendAsync();

        Assert.Equal(PaletteStatus.Failed, session.Status);
        Assert.Equal("The job tried to open an app that is not one of Telegram, so the run stopped.", session.Error);
        Assert.Empty(opened);
    }

    private sealed class NoTasks : IJobTasks
    {
        public void Register(ScheduledJob job)
        {
        }

        public void Remove(long jobId)
        {
        }
    }
}
