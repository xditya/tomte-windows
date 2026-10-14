using System.Text.Json;
using System.Text.Json.Nodes;
using Tomte.AI;
using Tomte.AI.Mcp;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Device;
using Tomte.Core.Rambler;
using Xunit;

namespace Tomte.Tests;

/// <summary>
/// A rambler transcript through the agent loop. The routing tests play back what llama3.2:3b on local Ollama
/// answered to the voice prompt and the voice tools (recorded from this branch's own requests).
/// </summary>
public sealed class VoiceTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tomte-voice-{Guid.NewGuid():N}");
    private readonly InMemoryShelf _shelf = new();
    private readonly InMemoryScratchpad _pad = new();
    private readonly List<string> _ran = [];
    private readonly List<ToolRequest> _asked = [];
    private readonly List<DeviceEvent> _events = [];
    private PermissionAnswer _answer = PermissionAnswer.AllowOnce;
    private int _undone;

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string LogPath => Path.Combine(_dir, "send-log.jsonl");

    private IslandTimer Timer { get; } = new(Path.Combine(Path.GetTempPath(), $"tomte-voice-timer-{Guid.NewGuid():N}.json"));

    private ActionResult FakeDevice(string tool, JsonElement input)
    {
        _ran.Add($"{tool} {input.GetRawText()}");
        return ActionResult.Done($"{tool} done.", () =>
        {
            _undone++;
            return ActionResult.Done($"{tool} put back.");
        });
    }

    private BuiltInTools Tools(Func<string, bool>? launchShortcut = null) =>
        new(_shelf, _pad, _ => _ran.Add("clipboard"), u => _ran.Add("open " + u), FakeDevice, Timer, launchShortcut);

    private PaletteSession Session(IChatProvider provider, bool voice = true, PermissionGrants? grants = null, BuiltInTools? tools = null, McpTools? mcp = null)
    {
        var config = new ProviderConfig("ollama", ProviderKind.OpenAICompatible, "http://127.0.0.1:11434/v1", OllamaFactAttribute.Model);
        var session = new PaletteSession(provider, config, new PaletteOptions(), new SendLog(LogPath), tools ?? Tools(), grants ?? new PermissionGrants(), [])
        {
            Voice = voice,
            Mcp = mcp,
            Prompt = "do it",
            ConfirmTool = (request, _) =>
            {
                _asked.Add(request);
                return Task.FromResult(_answer);
            },
        };
        session.DeviceActionExecuted += _events.Add;
        return session;
    }

    /// <summary>The recorded turns of one case, in order, and the handler that saw the requests.</summary>
    private (PaletteSession Session, RecordedHandler Handler) Recorded(string name, int turns)
    {
        var handler = new RecordedHandler($"openai-ollama-voice-{name}-1.sse", then: [.. Enumerable.Range(2, turns - 1).Select(i => $"openai-ollama-voice-{name}-{i}.sse")]);
        var provider = new OpenAICompatibleProvider(new HttpClient(handler), "http://127.0.0.1:11434/v1", null);
        return (Session(provider), handler);
    }

    private static Task Run(PaletteSession session) => session.SendAsync();

    /// <summary>The raw words on the in-memory scratchpad, as the shell's note keeps them.</summary>
    private Task Keep(string words)
    {
        _pad.Text = RamblerNotes.Append(_pad.Text, words, DateTime.Now);
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ACommandIsConfirmedThenRunsAndShowsItsEvent()
    {
        var (session, handler) = Recorded("command", 2);

        Assert.False(await VoiceCommand.RouteAsync("turn on dark mode", Keep, session, Run));

        var asked = Assert.Single(_asked);
        Assert.Equal("Turn dark mode on", asked.Summary);
        Assert.Equal(ActionTier.ExternalEffect, asked.Tier);
        Assert.False(asked.OfferAlwaysAllow);
        Assert.Equal(["set_dark_mode {\"on\":true}"], _ran);
        var e = Assert.Single(_events);
        Assert.Equal("set_dark_mode done.", e.Line);
        Assert.Equal("set_dark_mode put back.", e.Undo!().Message);
        Assert.Equal(PaletteStatus.Done, session.Status);
        Assert.Equal("The system is now in dark mode.", session.AnswerText);
        Assert.Equal("", _pad.Text);

        // What left the PC: the transcript under the voice prompt, and the voice tools.
        var first = JsonNode.Parse(handler.Bodies[0])!;
        Assert.Equal(PaletteSession.VoicePrompt, first["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("turn on dark mode", first["messages"]![1]!["content"]!.GetValue<string>());
        Assert.Equal(2, first["messages"]!.AsArray().Count);
        var sent = first["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>()).ToList();
        Assert.Equal(Tools().VoiceDefinitions.Select(d => d.Name), sent);
        var log = Assert.Single(new SendLog(LogPath).ReadAll());
        Assert.Equal(["transcript"], log.Attached);
        Assert.Null(log.Content);
    }

    [Fact]
    public async Task ADictatedNoteIsSavedWithoutAsking()
    {
        var (session, _) = Recorded("note", 2);

        Assert.False(await VoiceCommand.RouteAsync("remember to buy milk and eggs, and call the dentist on Monday at ten", Keep, session, Run));

        Assert.Empty(_asked);
        Assert.StartsWith("Save a note to the scratchpad: \"## Shopping - [ ] Buy milk", Assert.Single(session.Answer.OfType<ToolStepSegment>()).Summary);
        Assert.Empty(_ran);
        // The model escaped its line breaks twice; the note has real ones under the rambler's time heading.
        Assert.Matches(@"^## \d{4}-\d{2}-\d{2} \d{2}:\d{2}\n\n## Shopping\n- \[ \] Buy milk\n- \[ \] Buy eggs\nCall dentist on Monday at 10\n\n$", _pad.Text);
        var e = Assert.Single(_events);
        Assert.Equal("Note saved to the scratchpad.", e.Line);
        e.Undo!();
        Assert.Equal("", _pad.Text);
    }

    [Fact]
    public void ANoteLosesControlCharactersButKeepsItsLinesAndTabs()
    {
        var input = JsonDocument.Parse("""{"note":"### Milk\\n- \u001b[31mCall\u001b[0m\n\tthe dentist"}""").RootElement;
        var (result, _) = Tools().Run(new ToolCallPart("c1", BuiltInTools.SaveNote, input));

        Assert.False(result.IsError);
        Assert.EndsWith("\n\n### Milk\n- [31mCall[0m\n\tthe dentist\n\n", _pad.Text);
    }

    [Fact]
    public void ANoteInACodeFenceIsSavedWithoutTheFence()
    {
        var input = JsonDocument.Parse("""{"note":"```markdown\n### Plan\n- [ ] Call Ana\n```\n"}""").RootElement;
        Assert.Equal("Save a note to the scratchpad: \"### Plan - [ ] Call Ana\"", BuiltInTools.Describe(BuiltInTools.SaveNote, input));
        var (result, _) = Tools().Run(new ToolCallPart("c1", BuiltInTools.SaveNote, input));

        Assert.False(result.IsError);
        Assert.EndsWith("\n\n### Plan\n- [ ] Call Ana\n\n", _pad.Text);
    }

    [Fact]
    public async Task AnEmptyNoteKeepsTheTranscriptAsTheRawNote()
    {
        var provider = new ScriptedProvider();
        provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.SaveNote, """{"note":"```\n```"}"""));
        provider.Turns.Add(ScriptedProvider.Text(1, 1, "Saved."));

        Assert.True(await VoiceCommand.RouteAsync("call Ana at five", Keep, Session(provider), Run));

        Assert.Empty(_asked);
        Assert.Matches(@"^## \d{4}-\d{2}-\d{2} \d{2}:\d{2}\n\ncall Ana at five\n\n$", _pad.Text);
    }

    [Fact]
    public void TheNoteUndoTakesOffOnlyTheNoteAndNothingTypedAfterIt()
    {
        static ToolCallPart Note(string text) => new("c1", BuiltInTools.SaveNote, JsonDocument.Parse($$"""{"note":"{{text}}"}""").RootElement);

        _pad.Text = "Ideas:\n- a quieter alarm\n";
        var (_, undo) = Tools().Run(Note("buy milk"));
        // An edit above the note stays when the note goes.
        _pad.Text = _pad.Text.Replace("quieter", "softer", StringComparison.Ordinal);
        Assert.Equal("Note taken off the scratchpad.", undo!().Message);
        Assert.Equal("Ideas:\n- a softer alarm\n", _pad.Text);

        var (_, second) = Tools().Run(Note("call Ana"));
        _pad.Text += "typed after the note\n";
        var refused = second!();
        Assert.False(refused.Ok);
        Assert.Equal("The scratchpad changed after the note, so the note stays.", refused.Message);
        Assert.Contains("call Ana", _pad.Text);
        Assert.EndsWith("typed after the note\n", _pad.Text);
    }

    [Fact]
    public async Task AnUnclearRequestRunsNothingAndKeepsTheRawNote()
    {
        var (session, _) = Recorded("unclear", 1);

        Assert.True(await VoiceCommand.RouteAsync("hmm", Keep, session, Run));

        Assert.Empty(_asked);
        Assert.Empty(_ran);
        Assert.Empty(_events);
        Assert.Equal(PaletteStatus.Done, session.Status);
        Assert.Equal("Since the user didn't give a question, no action can be performed.", session.AnswerText);
        // The model answered in text; the words are kept, as they are when there is no provider.
        Assert.Matches(@"^## \d{4}-\d{2}-\d{2} \d{2}:\d{2}\n\nhmm\n\n$", _pad.Text);
    }

    [Fact]
    public async Task ARefusedActionNeverRuns()
    {
        var (session, handler) = Recorded("refused", 2);
        _answer = PermissionAnswer.Deny;

        Assert.False(await VoiceCommand.RouteAsync("lock the computer", Keep, session, Run));

        var asked = Assert.Single(_asked);
        Assert.Equal(("Lock the PC", ActionTier.Destructive, false), (asked.Summary, asked.Tier, asked.OfferAlwaysAllow));
        Assert.Empty(_ran);
        Assert.Empty(_events);
        var step = Assert.Single(session.Answer.OfType<ToolStepSegment>());
        Assert.True(step.IsError);
        Assert.Equal("The user did not allow this.", step.Result);
        Assert.Contains("The user did not allow this.", handler.Bodies[1]);
        Assert.Equal("Sorry, I don't have permission to lock the computer.", session.AnswerText);
        Assert.Equal("", _pad.Text);
    }

    [Fact]
    public async Task TheTimerActionStartsATimerAndItsUndoCancelsIt()
    {
        var (session, _) = Recorded("timer", 2);

        await VoiceCommand.RouteAsync("set a timer for ten minutes", Keep, session, Run);

        Assert.Equal("Set a timer for 10 minutes", Assert.Single(_asked).Summary);
        Assert.Equal(TimeSpan.FromMinutes(10), Timer.Run!.Duration);
        var e = Assert.Single(_events);
        Assert.StartsWith("Timer set; it ends at ", e.Line);
        e.Undo!();
        Assert.Null(Timer.Run);
    }

    [Fact]
    public async Task TheShelfActionCreatesAFileAndItsUndoRemovesIt()
    {
        var (session, _) = Recorded("shelf", 2);

        await VoiceCommand.RouteAsync("put a file called groceries on my shelf with milk and eggs", Keep, session, Run);

        Assert.Equal("Create shelf file \"groceries\" (10 characters)", Assert.Single(_asked).Summary);
        Assert.Equal("milk, eggs", _shelf.ReadText("groceries"));
        Assert.Equal("Created \"groceries\" on the shelf.", Assert.Single(_events).Line);
        _events[0].Undo!();
        Assert.Empty(_shelf.ItemNames);
    }

    [Fact]
    public async Task EveryVoiceCallButTheNoteAsksEvenWhenAlwaysAllowedAndAnAlwaysAnswerGrantsNothing()
    {
        var grants = new PermissionGrants();
        grants.AlwaysAllow(BuiltInTools.SetDarkMode);
        static ScriptedProvider FiveCalls()
        {
            var provider = new ScriptedProvider();
            provider.Turns.Add([
                .. ScriptedProvider.Call(BuiltInTools.SetDarkMode, """{"on":true}""", "c1")[..^1],
                .. ScriptedProvider.Call(BuiltInTools.SetVolume, """{"percent":20}""", "c2")[..^1],
                .. ScriptedProvider.Call(BuiltInTools.SetTimer, """{"minutes":5}""", "c3")[..^1],
                .. ScriptedProvider.Call(BuiltInTools.ReadShelfItem, """{"name":"x"}""", "c4")[..^1],
                .. ScriptedProvider.Call(BuiltInTools.SaveNote, """{"note":"buy milk"}""", "c5")]);
            provider.Turns.Add(ScriptedProvider.Text(1, 1, "Done."));
            return provider;
        }

        // Typed, the granted tool runs on its own, and so do the app-local and the read; the note is not offered.
        await Session(FiveCalls(), voice: false, grants: grants).SendAsync();
        Assert.Equal([BuiltInTools.SetVolume], _asked.Select(a => a.Tool));
        Assert.True(_asked[0].OfferAlwaysAllow);
        Assert.Equal("", _pad.Text);

        // Spoken, all but the note ask, none offers "always allow", and answering it anyway grants nothing.
        _asked.Clear();
        _answer = PermissionAnswer.AlwaysAllow;
        await Session(FiveCalls(), grants: grants).SendAsync();
        Assert.Equal([BuiltInTools.SetDarkMode, BuiltInTools.SetVolume, BuiltInTools.SetTimer, BuiltInTools.ReadShelfItem], _asked.Select(a => a.Tool));
        Assert.All(_asked, a => Assert.False(a.OfferAlwaysAllow));
        Assert.Equal([BuiltInTools.SetDarkMode], grants.AlwaysAllowed);
        Assert.Contains("buy milk", _pad.Text);
    }

    [Fact]
    public async Task AVoiceTurnIsCappedAt1024Tokens()
    {
        var provider = new ScriptedProvider();
        provider.Turns.Add(ScriptedProvider.Text(1, 1, "ok"));
        await Session(provider).SendAsync();
        await Session(provider, voice: false).SendAsync();

        Assert.Equal([1024, new PaletteOptions().MaxTokens], provider.Requests.Select(r => r.MaxTokens));
    }

    [Fact]
    public async Task McpToolsAreNotOfferedToVoiceAndACallToOneNeverRuns()
    {
        bool connected = false;
        await using var mcp = new McpTools([new McpServerSettings { Name = "echo", Command = "echo-server" }], connect: (_, _) =>
        {
            connected = true;
            throw new InvalidOperationException("no server in this test");
        });
        var provider = new ScriptedProvider();
        provider.Turns.Add(ScriptedProvider.Call("echo__say", """{"text":"hi"}"""));
        provider.Turns.Add(ScriptedProvider.Text(1, 1, "ok"));

        var session = Session(provider, mcp: mcp);
        session.Prompt = "say hi with echo";
        await session.SendAsync();

        Assert.False(connected);
        var offered = provider.Requests[0].Tools!.Select(t => t.Name).ToList();
        Assert.DoesNotContain(offered, n => n.Contains(McpTools.Separator));
        Assert.Equal(Tools().VoiceDefinitions.Select(d => d.Name), offered);
        Assert.Empty(_asked);
        Assert.Equal("There is no tool named echo__say.", Assert.Single(session.Answer.OfType<ToolStepSegment>()).Result);
        Assert.Equal(PaletteStatus.Done, session.Status);
    }

    [Fact]
    public void VoiceGetsThePowerToolsTheTimerTheShelfAndTheNoteButNotTheClipboard()
    {
        var voice = Tools().VoiceDefinitions.Select(d => d.Name).ToHashSet();
        Assert.Superset(new HashSet<string> { BuiltInTools.LockPc, BuiltInTools.ShutDownPc, BuiltInTools.SetDarkMode, BuiltInTools.OpenUrl, BuiltInTools.SetTimer,
            BuiltInTools.ReadShelfItem, BuiltInTools.CreateShelfFile, BuiltInTools.SaveNote }, voice);
        Assert.DoesNotContain(BuiltInTools.CopyToClipboard, voice);
        Assert.DoesNotContain(BuiltInTools.WriteScratchpad, voice);
        // Typed chat keeps its own list: no note step, no shortcuts.
        Assert.DoesNotContain(BuiltInTools.Definitions, d => d.Name is BuiltInTools.SaveNote or BuiltInTools.OpenShortcut);
    }

    [Fact]
    public async Task ShortcutsAreOfferedOnlyOnceALauncherIsWiredAndRunOffTheCallingThread()
    {
        Assert.DoesNotContain(Tools().VoiceDefinitions, d => d.Name == BuiltInTools.OpenShortcut);

        int caller = Environment.CurrentManagedThreadId, launchedOn = -1;
        var tools = Tools(name =>
        {
            launchedOn = Environment.CurrentManagedThreadId;
            return name == "Spotify";
        });
        Assert.Contains(tools.VoiceDefinitions, d => d.Name == BuiltInTools.OpenShortcut);
        var provider = new ScriptedProvider();
        provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.OpenShortcut, """{"name":"Spotify"}"""));
        provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.OpenShortcut, """{"name":"Nope"}""", "c2"));
        provider.Turns.Add(ScriptedProvider.Text(1, 1, "ok"));

        var session = Session(provider, tools: tools);
        await session.SendAsync();

        Assert.Equal(["Open the shortcut \"Spotify\"", "Open the shortcut \"Nope\""], _asked.Select(a => a.Summary));
        Assert.Equal(ActionTier.ExternalEffect, _asked[0].Tier);
        Assert.NotEqual(caller, launchedOn);
        Assert.Equal("Opened Spotify.", Assert.Single(_events).Line);
        var refused = session.Answer.OfType<ToolStepSegment>().Last();
        Assert.True(refused.IsError);
        Assert.Equal("No shortcut named \"Nope\" could be opened.", refused.Result);
    }

    [Fact]
    public async Task WithTheAiLayerOffTheTranscriptIsTheRawNoteAsBefore()
    {
        Assert.True(await VoiceCommand.RouteAsync("buy milk", Keep, null, _ => throw new InvalidOperationException("nothing is sent")));

        Assert.Matches(@"^## \d{4}-\d{2}-\d{2} \d{2}:\d{2}\n\nbuy milk\n\n$", _pad.Text);
    }

    [Fact]
    public async Task AFailedSendKeepsTheRawWordsAndACancelKeepsNothing()
    {
        var down = new ScriptedProvider { Throw = new ProviderException("Ollama is not running") };
        Assert.True(await VoiceCommand.RouteAsync("call Ana at five", Keep, Session(down), Run));
        Assert.Contains("call Ana at five", _pad.Text);

        _pad.Text = "";
        var slow = new ScriptedProvider { DelayPerEvent = TimeSpan.FromMilliseconds(50) };
        slow.Turns.Add(ScriptedProvider.Text(1, 1, "a", "b", "c"));
        var session = Session(slow);
        session.Changed += () =>
        {
            if (session.AnswerText.Length == 1)
                session.Cancel();
        };
        Assert.False(await VoiceCommand.RouteAsync("call Ana at five", Keep, session, Run));
        Assert.Equal(PaletteStatus.Cancelled, session.Status);
        Assert.Equal("", _pad.Text);
    }

    [Fact]
    public async Task SpokenRepliesAreOffByDefaultAndReadOnlyAFinishedAnswer()
    {
        Assert.False(new TomteSettings().SpokenReplies);
        Directory.CreateDirectory(_dir);
        string file = Path.Combine(_dir, "settings.json");
        File.WriteAllText(file, """{"desktopPeek":false}""");
        Assert.False(SettingsStore.Load(file).SpokenReplies);
        Assert.Contains("\"spokenReplies\": true", SettingsStore.Serialize(new TomteSettings { SpokenReplies = true }));

        var (session, _) = Recorded("unclear", 1);
        Assert.Null(VoiceCommand.ReplyToSpeak(session, spokenReplies: true));
        await VoiceCommand.RouteAsync("hmm", Keep, session, Run);
        Assert.Null(VoiceCommand.ReplyToSpeak(session, spokenReplies: false));
        Assert.Equal("Since the user didn't give a question, no action can be performed.", VoiceCommand.ReplyToSpeak(session, spokenReplies: true));
    }
}
