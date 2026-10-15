using System.Runtime.CompilerServices;
using System.Text.Json;
using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Device;
using Xunit;

namespace Tomte.Tests;

/// <summary>Plays back one scripted turn per request; the last script repeats for later requests.</summary>
internal sealed class ScriptedProvider : IChatProvider
{
    public List<ChatEvent[]> Turns { get; } = [];

    public List<ChatRequest> Requests { get; } = [];

    public TimeSpan DelayPerEvent { get; set; }

    public Exception? Throw { get; set; }

    public ProviderCapabilities Capabilities => ProviderCapabilities.Streaming | ProviderCapabilities.ToolUse;

    public Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<ModelInfo>>([]);

    public async IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, [EnumeratorCancellation] CancellationToken ct = default)
    {
        Requests.Add(request);
        if (Throw is not null)
            throw Throw;
        foreach (var e in Turns[Math.Min(Requests.Count, Turns.Count) - 1])
        {
            if (DelayPerEvent > TimeSpan.Zero)
                await Task.Delay(DelayPerEvent, ct);
            yield return e;
        }
    }

    public static ChatEvent[] Text(int inputTokens = 10, int outputTokens = 5, params string[] deltas) =>
        [.. deltas.Select(d => (ChatEvent)new TextDelta(d)), new TurnDone("end_turn", inputTokens, outputTokens)];

    public static ChatEvent[] Call(string name, string json, string id = "c1") =>
        [new ToolCallEvent(new ToolCallPart(id, name, JsonDocument.Parse(json).RootElement)), new TurnDone("tool_use", 10, 5)];
}

public class PaletteTests : IDisposable
{
    private const string Notes = "Milk, eggs, bread.\nCall the dentist on Monday.";
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"tomte-palette-{Guid.NewGuid():N}", "send-log.jsonl");
    private readonly ScriptedProvider _provider = new();
    private readonly InMemoryShelf _shelf = new();
    private readonly InMemoryScratchpad _scratchpad = new();
    private readonly List<string> _copied = [];
    private readonly List<string> _opened = [];
    private readonly PermissionGrants _grants = new();

    public PaletteTests() => _shelf.CreateFile("notes.txt", Notes);

    public void Dispose()
    {
        if (Directory.Exists(Path.GetDirectoryName(_logPath)))
            Directory.Delete(Path.GetDirectoryName(_logPath)!, recursive: true);
    }

    private BuiltInTools Tools => new(_shelf, _scratchpad, _copied.Add, _opened.Add);

    private PaletteSession Session(PaletteOptions? options = null, IReadOnlyList<ContextChip>? chips = null, string prompt = "hi")
    {
        var config = new ProviderConfig("p", ProviderKind.OpenAICompatible, "http://localhost", "m");
        return new PaletteSession(_provider, config, options ?? new PaletteOptions(), new SendLog(_logPath), Tools, _grants, chips ?? [])
        {
            Prompt = prompt,
        };
    }

    [Fact]
    public async Task StreamsTextAndReportsTheProviderWhileSending()
    {
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "Hel", "lo"));
        var session = Session();
        var indicators = new List<string>();
        session.Changed += () => indicators.Add(session.ProviderIndicator);

        Assert.Equal(PaletteStatus.Idle, session.Status);
        Assert.Equal("Nothing sent", session.ProviderIndicator);
        await session.SendAsync();

        Assert.Equal(PaletteStatus.Done, session.Status);
        Assert.Equal("Hello", session.AnswerText);
        Assert.Contains("Sending to p · m", indicators);
        Assert.Equal("Sent to p · m", session.ProviderIndicator);
        var request = Assert.Single(_provider.Requests);
        Assert.Equal("m", request.Model);
        Assert.NotNull(request.System);
        Assert.Equal(BuiltInTools.Definitions.Count - 1, request.Tools!.Count);  // read_shelf_item waits for the shelf chip.
        Assert.Equal("hi", ((TextPart)Assert.Single(request.Messages).Parts[0]).Text);
    }

    [Fact]
    public async Task EmptyPromptSendsNothing()
    {
        var session = Session(prompt: "  ");
        await session.SendAsync();
        Assert.Empty(_provider.Requests);
        Assert.Equal(PaletteStatus.Idle, session.Status);
    }

    [Fact]
    public async Task CancelEndsInCancelledAndStillLogsTheSend()
    {
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "a", "b", "c", "d"));
        _provider.DelayPerEvent = TimeSpan.FromMilliseconds(30);
        var session = Session();
        session.Changed += () =>
        {
            if (session.AnswerText.Length == 1)
                session.Cancel();
        };

        await session.SendAsync();

        Assert.Equal(PaletteStatus.Cancelled, session.Status);
        Assert.Null(session.Error);
        Assert.Single(new SendLog(_logPath).ReadAll());
    }

    [Fact]
    public async Task ProviderErrorEndsInFailed()
    {
        _provider.Throw = new ProviderException("Rate limited", 429);
        var session = Session();
        await session.SendAsync();
        Assert.Equal(PaletteStatus.Failed, session.Status);
        Assert.Equal("Rate limited", session.Error);
    }

    [Fact]
    public async Task FailingChipReadEndsInFailedWithoutSending()
    {
        var chip = new ContextChip(ContextKind.Screenshot, "Screenshot", "screenshot:app",
            _ => throw new System.Runtime.InteropServices.COMException("capture lost"), on: true);
        var session = Session(chips: [chip]);
        await session.SendAsync();
        Assert.Equal(PaletteStatus.Failed, session.Status);
        Assert.Contains("capture lost", session.Error);
        Assert.Empty(_provider.Requests);
        Assert.Empty(new SendLog(_logPath).ReadAll());
    }

    [Fact]
    public async Task TimeCapEndsInFailed()
    {
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "a", "b", "c"));
        _provider.DelayPerEvent = TimeSpan.FromMilliseconds(200);
        var session = Session(new PaletteOptions { Timeout = TimeSpan.FromMilliseconds(40) });
        await session.SendAsync();
        Assert.Equal(PaletteStatus.Failed, session.Status);
        Assert.StartsWith("Timed out", session.Error);
    }

    [Fact]
    public async Task ReadToolRunsWithoutConfirmationAndFeedsTheNextTurn()
    {
        _provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.ReadShelfItem, """{"name":"notes.txt"}"""));
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "Dentist on Monday."));
        var shelfChip = ContextChips.Shelf(_shelf);
        shelfChip.On = true;
        var session = Session(chips: [shelfChip]);
        int prompts = 0;
        session.ConfirmTool = (_, _) =>
        {
            prompts++;
            return Task.FromResult(PermissionAnswer.Deny);
        };

        await session.SendAsync();

        Assert.Equal(0, prompts);
        Assert.Equal(PaletteStatus.Done, session.Status);
        Assert.Equal(1, session.ToolSteps);
        var step = Assert.Single(session.Answer.OfType<ToolStepSegment>());
        Assert.False(step.IsError);
        Assert.Contains(Notes, step.Result);
        var second = _provider.Requests[1];
        Assert.Equal(3, second.Messages.Count);
        Assert.IsType<TextPart>(second.Messages[0].Parts[0]);
        Assert.IsType<ToolCallPart>(second.Messages[1].Parts[0]);
        var result = Assert.IsType<ToolResultPart>(second.Messages[2].Parts[0]);
        Assert.Equal("c1", result.CallId);
        Assert.Equal(step.Result, result.Content);
        Assert.Equal("Dentist on Monday.", session.AnswerText);
    }

    [Fact]
    public async Task StepCapStopsTheLoop()
    {
        _provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.ReadShelfItem, """{"name":"notes.txt"}"""));
        var session = Session(new PaletteOptions { MaxToolSteps = 3 });
        await session.SendAsync();
        Assert.Equal(3, session.ToolSteps);
        Assert.Equal(4, _provider.Requests.Count);
        Assert.Equal(PaletteStatus.Done, session.Status);
        Assert.Contains("Stopped after 3 tool steps.", session.AnswerText);
    }

    [Fact]
    public async Task ExternalEffectIsDeniedUnlessTheUserAllowsIt()
    {
        _provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.OpenUrl, """{"url":"https://example.com"}"""));
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "ok"));
        var session = Session();

        await session.SendAsync();

        Assert.Empty(_opened);
        var step = Assert.Single(session.Answer.OfType<ToolStepSegment>());
        Assert.True(step.IsError);
        Assert.Equal("Open https://example.com", step.Summary);
        Assert.True(((ToolResultPart)_provider.Requests[1].Messages[2].Parts[0]).IsError);
    }

    [Fact]
    public async Task TheProviderLineSaysItWaitsForTheUserWhileACardAsks()
    {
        _provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.OpenUrl, """{"url":"https://example.com"}"""));
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "ok"));
        var session = Session();
        string? asking = null;
        session.ConfirmTool = (_, _) =>
        {
            asking = session.ProviderIndicator;
            return Task.FromResult(PermissionAnswer.Deny);
        };
        var shown = new List<string>();
        session.Changed += () =>
        {
            if (shown.Count == 0 || shown[^1] != session.ProviderIndicator)
                shown.Add(session.ProviderIndicator);
        };

        await session.SendAsync();

        Assert.Equal("Waiting for you", asking);
        // Once answered the line is the provider's again: the denial goes back to the model.
        Assert.Equal(["Nothing sent", "Sending to p · m", "Waiting for you", "Sending to p · m", "Sent to p · m"], shown);
    }

    [Fact]
    public async Task AlwaysAllowAsksOnceThenRunsWithoutPrompting()
    {
        _provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.OpenUrl, """{"url":"https://example.com/1"}"""));
        _provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.OpenUrl, """{"url":"https://example.com/2"}""", "c2"));
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "ok"));
        var session = Session();
        var asked = new List<ToolRequest>();
        session.ConfirmTool = (request, _) =>
        {
            asked.Add(request);
            return Task.FromResult(PermissionAnswer.AlwaysAllow);
        };

        await session.SendAsync();

        var request = Assert.Single(asked);
        Assert.Equal(ActionTier.ExternalEffect, request.Tier);
        Assert.True(request.OfferAlwaysAllow);
        Assert.Equal(["https://example.com/1", "https://example.com/2"], _opened);
        Assert.Contains(BuiltInTools.OpenUrl, _grants.AlwaysAllowed);
    }

    [Fact]
    public async Task InvalidArgumentsNeverExecute()
    {
        _provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.OpenUrl, """{"url":"file:///C:/secret.txt"}"""));
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "ok"));
        var session = Session();
        session.ConfirmTool = (_, _) => Task.FromResult(PermissionAnswer.AllowOnce);

        await session.SendAsync();

        Assert.Empty(_opened);
        Assert.True(Assert.Single(session.Answer.OfType<ToolStepSegment>()).IsError);
    }

    [Fact]
    public async Task DailyCapBlocksTheSendBeforeAnythingLeaves()
    {
        var log = new SendLog(_logPath);
        var now = DateTimeOffset.Now;
        // Today's earlier send is logged at the same instant: an hour back is yesterday between midnight and 01:00.
        log.Append(new SendLogEntry(now, "p", "m", 1, 1, 0.75m, [], null));
        log.Append(new SendLogEntry(now.AddDays(-1), "p", "m", 1, 1, 5m, [], null));
        log.Append(new SendLogEntry(now, "other", "m", 1, 1, 5m, [], null));
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "x"));

        var fine = Session(new PaletteOptions { DailyCapUsd = 1m });
        Assert.Equal(0.75m, fine.SpentTodayUsd);
        await fine.SendAsync();
        Assert.Equal(PaletteStatus.Done, fine.Status);

        log.Append(new SendLogEntry(now, "p", "m", 1, 1, 0.25m, [], null));
        var capped = Session(new PaletteOptions { DailyCapUsd = 1m });
        await capped.SendAsync();
        Assert.Equal(PaletteStatus.Failed, capped.Status);
        Assert.Contains("Daily cap of $1.00", capped.Error);
        Assert.Single(_provider.Requests);
    }

    [Fact]
    public async Task SpendIsEstimatedFromUsageAndThePriceTable()
    {
        _provider.Turns.Add(ScriptedProvider.Text(100_000, 10_000, "x"));
        var options = new PaletteOptions { Prices = new Dictionary<string, ModelPrice> { ["m"] = new(3m, 15m) } };
        var session = Session(options);
        await session.SendAsync();
        Assert.Equal(0.45m, session.SpentTodayUsd);
        Assert.Equal(0.45m, Assert.Single(new SendLog(_logPath).ReadAll()).CostUsd);
        Assert.Equal(0m, SendLog.Estimate(options, "unpriced", 1000, 1000));
    }

    [Fact]
    public async Task SendLogRecordsNamesAndTokensAndContentOnlyWhenAsked()
    {
        var chip = new ContextChip(ContextKind.ShelfItem, "Shelf · notes.txt", "shelf:notes.txt",
            _ => Task.FromResult<IReadOnlyList<ContentPart>>([new TextPart("ctx")]), on: true);
        _provider.Turns.Add(ScriptedProvider.Text(12, 7, "answer"));

        await Session(chips: [chip], prompt: "question").SendAsync();
        var quiet = Assert.Single(new SendLog(_logPath).ReadAll());
        Assert.Equal(("p", "m", 12, 7), (quiet.Provider, quiet.Model, quiet.InputTokens, quiet.OutputTokens));
        Assert.Equal(["shelf:notes.txt"], quiet.Attached);
        Assert.Null(quiet.Content);

        await Session(new PaletteOptions { LogContent = true }, [chip], "question").SendAsync();
        var full = new SendLog(_logPath).ReadAll()[1];
        Assert.Contains("question", full.Content);
        Assert.Contains("answer", full.Content);

        new SendLog(_logPath).Clear();
        Assert.Empty(new SendLog(_logPath).ReadAll());
    }

    [Fact]
    public async Task ChipsAttachInOrderAndOffChipsAreNeverRead()
    {
        int offReads = 0;
        var on = new ContextChip(ContextKind.Selection, "Selection", "selection",
            _ => Task.FromResult<IReadOnlyList<ContentPart>>([new TextPart("<selection>x</selection>")]), on: true);
        var off = new ContextChip(ContextKind.Scratchpad, "Scratchpad", "scratchpad", _ =>
        {
            offReads++;
            return Task.FromResult<IReadOnlyList<ContentPart>>([new TextPart("never")]);
        });
        var image = new ContextChip(ContextKind.Screenshot, "Screenshot", "screenshot:app",
            _ => Task.FromResult<IReadOnlyList<ContentPart>>([new ImagePart("image/png", [1, 2, 3])]), on: true);
        var unavailable = new ContextChip(ContextKind.ShelfItem, "Shelf · empty", "shelf", _ => throw new InvalidOperationException(), available: false, on: true);

        var (parts, attached) = await ContextAssembly.BuildAsync([on, off, image, unavailable], "why?", CancellationToken.None);

        Assert.False(unavailable.On);
        Assert.Equal(0, offReads);
        Assert.Equal(["selection", "screenshot:app"], attached);
        Assert.Equal("<selection>x</selection>", ((TextPart)parts[0]).Text);
        Assert.IsType<ImagePart>(parts[1]);
        Assert.Equal("why?", ((TextPart)parts[2]).Text);
    }

    [Fact]
    public async Task ChipFactoriesReflectWhatIsThere()
    {
        Assert.False(ContextChips.Selection(null).Available);
        Assert.False(ContextChips.Selection("   ").Available);
        var selection = ContextChips.Selection("The quick brown fox jumps over the lazy dog", app: "notepad");
        Assert.True(selection.Available);
        Assert.False(selection.On, "every chip starts off; the user opts in per request");
        // A short name, and a tooltip that says exactly what goes.
        Assert.Equal("Selection", selection.Label);
        Assert.Equal("Selected text from notepad, 43 characters", selection.Tip);
        Assert.Equal("Selected text, 1 character", ContextChips.Selection("x").Tip);
        Assert.Equal("<selection>\nThe quick brown fox jumps over the lazy dog\n</selection>",
            ((TextPart)(await selection.ReadAsync(CancellationToken.None))[0]).Text);

        Assert.False(ContextChips.Shelf(new InMemoryShelf()).Available);
        var shelf = ContextChips.Shelf(_shelf);
        Assert.Equal(("Shelf", "Shelf: notes.txt"), (shelf.Label, shelf.Tip));
        // The chip names the file; the session reads it at send.
        Assert.Equal("notes.txt", Assert.IsType<Tomte.AI.Files.ShelfFilePart>(Assert.Single(await shelf.ReadAsync(CancellationToken.None))).Name);

        _scratchpad.Text = "todo";
        var note = ContextChips.Scratchpad(_scratchpad);
        Assert.Contains("todo", ((TextPart)(await note.ReadAsync(CancellationToken.None))[0]).Text);
        Assert.Equal(("Note", "The Scratchpad note, 1 line"), (note.Label, note.Tip));
        // The tooltip counts the note when it shows, which may have loaded since the palette opened.
        _scratchpad.Text = "Milk\r\nEggs\nBread\n";
        Assert.Equal("The Scratchpad note, 3 lines", note.Tip);
        _scratchpad.Text = "";
        Assert.Equal("The Scratchpad note, which is empty", note.Tip);

        var window = ContextChips.Window("msedge", "Top 20 Adventure Games");
        Assert.Equal(("Window", "Window title from msedge: Top 20 Adventure Games"), (window.Label, window.Tip));
        // A window with neither an app nor a title has nothing to add, and the screenshot needs the app.
        Assert.False(ContextChips.Window("", "").Available);
        Assert.Equal("The name of the app in front: code", ContextChips.Window("code", "").Tip);
        var screenshot = ContextChips.Screenshot("code", _ => Task.FromResult(Array.Empty<byte>()));
        Assert.Equal(("Screenshot", "Screenshot of code, sent as an image"), (screenshot.Label, screenshot.Tip));
        Assert.False(ContextChips.Screenshot("", _ => Task.FromResult(Array.Empty<byte>())).Available);
    }

    [Fact]
    public void ChipsNameTheAppAsPeopleKnowItAndTheLineSaysWhatTheSendCarries()
    {
        var shelf = new InMemoryShelf();
        shelf.CreateFile("report.pdf", "x");
        shelf.CreateFile("notes.txt", "y");
        shelf.CreateFile("todo.txt", "z");
        var chips = ContextChips.For(null, "hi", "Code", "main.cs - tomte", _ => Task.FromResult(Array.Empty<byte>()), shelf, _scratchpad, "Visual Studio Code");

        // What people call the app in what the chips say; the process name still goes to the model.
        Assert.Equal("Selected text from Visual Studio Code, 2 characters", chips[0].Tip);
        Assert.Equal("Window title from Visual Studio Code: main.cs - tomte", chips[1].Tip);
        Assert.Equal("window:Code", chips[1].AttachedName);
        Assert.Equal("Screenshot of Visual Studio Code, sent as an image", chips[2].Tip);

        // Nothing on: no line. Then the question and each chip that is on, in chip order.
        Assert.Equal("", ContextChips.Summary(chips));
        chips[4].On = true;
        Assert.Equal("Sends your question and the Scratchpad note", ContextChips.Summary(chips));
        chips[2].On = true;
        Assert.Equal("Sends your question, a screenshot of Visual Studio Code and the Scratchpad note", ContextChips.Summary(chips));
        chips[0].On = chips[1].On = chips[3].On = true;
        Assert.Equal("Sends your question, the text selected in Visual Studio Code, the title of the Visual Studio Code window, "
            + "a screenshot of Visual Studio Code, todo.txt from the shelf with the names of 2 others and the Scratchpad note", ContextChips.Summary(chips));
        Assert.Equal("Sends your question and the region you circled", ContextChips.Summary([ContextChips.Lasso([1], 10, 10)]));
    }

    [Theory]
    [InlineData("Visual Studio Code", "Visual Studio Code", "Code", "Visual Studio Code")]
    [InlineData(" ", "Microsoft Edge", "msedge", "Microsoft Edge")]
    [InlineData(null, null, "game", "game")]
    [InlineData(" Notepad ", null, "notepad", "Notepad")]
    public void TheAppsNameIsItsFileDescriptionThenItsProductThenTheProcess(string? description, string? product, string process, string expected) =>
        Assert.Equal(expected, WindowContext.Name(description, product, process));

    [Fact]
    public void TheChipsTheyDrawCarryTheirNameIconAndState()
    {
        var shelf = new InMemoryShelf();
        shelf.CreateFile("report.pdf", "x");
        var chips = ContextChips.For(ContextChips.Lasso([1], 200, 100), "hi", "notepad", "notes.txt - Notepad", _ => Task.FromResult(Array.Empty<byte>()), shelf, _scratchpad);
        chips[2].On = true;
        Assert.Equal(
            [new("Region", ChipIcon.Region, true), new("Selection", ChipIcon.Selection, false), new("Window", ChipIcon.Window, true), new("Screenshot", ChipIcon.Screenshot, false), new("Shelf", ChipIcon.Shelf, false), new ChipSpec("Note", ChipIcon.Note, false)],
            chips.Select(c => c.Spec));

        // Nothing selected and an empty shelf: those chips have nothing to add, and the palette leaves them out.
        var bare = ContextChips.For(null, null, "notepad", "notes.txt - Notepad", _ => Task.FromResult(Array.Empty<byte>()), new InMemoryShelf(), _scratchpad);
        Assert.Equal(["Window", "Screenshot", "Note"], bare.Where(c => c.Available).Select(c => c.Label));
    }

    [Fact]
    public async Task SelectionFallbackRunsOnlyWhenTheUserTurnsTheChipOn()
    {
        int reads = 0;
        var chip = ContextChips.Selection(null, () =>
        {
            reads++;
            return "copied text";
        }, "code");
        Assert.True(chip.Available);
        Assert.False(chip.On);
        Assert.Equal("Copies the selected text from code when you turn this on, then puts the clipboard back", chip.Tip);
        Assert.Equal(0, reads);

        chip.On = true;
        Assert.Equal(1, reads);
        Assert.True(chip.On);
        Assert.Equal("Selected text from code, 11 characters", chip.Tip);
        Assert.Contains("copied text", ((TextPart)(await chip.ReadAsync(CancellationToken.None))[0]).Text);
        chip.On = false;
        chip.On = true;
        Assert.Equal(1, reads);

        var nothing = ContextChips.Selection(null, () => null);
        nothing.On = true;
        Assert.False(nothing.On);
        Assert.False(nothing.Available);
        Assert.Equal("No text is selected", nothing.Tip);
    }
}

public class AgentToolsTests
{
    private readonly InMemoryShelf _shelf = new();
    private readonly InMemoryScratchpad _pad = new();
    private readonly List<string> _copied = [];
    private readonly List<string> _opened = [];

    private BuiltInTools Tools => new(_shelf, _pad, _copied.Add, _opened.Add);

    private static ToolCallPart Call(string name, string json) => new("id", name, JsonDocument.Parse(json).RootElement);

    [Theory]
    [InlineData("no_such_tool", """{"name":"x"}""", "no tool named")]
    [InlineData(BuiltInTools.ReadShelfItem, "\"a string\"", "JSON object")]
    [InlineData(BuiltInTools.ReadShelfItem, """{}""", "\"name\"")]
    [InlineData(BuiltInTools.ReadShelfItem, """{"name":""}""", "\"name\"")]
    [InlineData(BuiltInTools.ReadShelfItem, """{"name":5}""", "\"name\"")]
    [InlineData(BuiltInTools.WriteScratchpad, """{"text":"x","mode":"prepend"}""", "mode")]
    [InlineData(BuiltInTools.WriteScratchpad, """{"text":"x","mode":1}""", "mode")]
    [InlineData(BuiltInTools.CreateShelfFile, """{"name":"..\\x.txt","content":"c"}""", "path separators")]
    [InlineData(BuiltInTools.CreateShelfFile, """{"name":"a/b.txt","content":"c"}""", "path separators")]
    [InlineData(BuiltInTools.CreateShelfFile, """{"name":"CON","content":"c"}""", "path separators")]
    [InlineData(BuiltInTools.CreateShelfFile, """{"name":"nul.txt","content":"c"}""", "path separators")]
    [InlineData(BuiltInTools.CreateShelfFile, """{"name":"com1.log","content":"c"}""", "path separators")]
    [InlineData(BuiltInTools.OpenUrl, """{"url":"ftp://example.com"}""", "http")]
    [InlineData(BuiltInTools.OpenUrl, """{"url":"notaurl"}""", "http")]
    public void RejectsBadArguments(string tool, string json, string expected)
    {
        var error = BuiltInTools.Validate(tool, JsonDocument.Parse(json).RootElement);
        Assert.NotNull(error);
        Assert.Contains(expected, error, StringComparison.OrdinalIgnoreCase);
        var (result, undo) = Tools.Run(Call(tool, json));
        Assert.True(result.IsError);
        Assert.Null(undo);
        Assert.Empty(_opened);
        Assert.Empty(_copied);
    }

    [Theory]
    [InlineData(BuiltInTools.ReadShelfItem, """{"name":"x"}""")]
    [InlineData(BuiltInTools.WriteScratchpad, """{"text":"x"}""")]
    [InlineData(BuiltInTools.WriteScratchpad, """{"text":"x","mode":"replace"}""")]
    [InlineData(BuiltInTools.CreateShelfFile, """{"name":"x.txt","content":"c"}""")]
    [InlineData(BuiltInTools.OpenUrl, """{"url":"https://example.com/a?b=1"}""")]
    [InlineData(BuiltInTools.CopyToClipboard, """{"text":"x"}""")]
    public void AcceptsGoodArguments(string tool, string json) =>
        Assert.Null(BuiltInTools.Validate(tool, JsonDocument.Parse(json).RootElement));

    [Fact]
    public void DefinitionsCarryTheSchemaTheModelNeeds()
    {
        Assert.Equal(6 + DeviceActionTiers.All.Count, BuiltInTools.Definitions.Count);
        var open = BuiltInTools.Definitions.Single(d => d.Name == BuiltInTools.OpenUrl);
        Assert.Equal("object", open.InputSchema.GetProperty("type").GetString());
        Assert.Equal("string", open.InputSchema.GetProperty("properties").GetProperty("url").GetProperty("type").GetString());
        Assert.Equal("url", open.InputSchema.GetProperty("required")[0].GetString());
        Assert.All(BuiltInTools.Definitions, d => Assert.True(BuiltInTools.Tiers.ContainsKey(d.Name)));
    }

    [Fact]
    public void ScratchpadWriteAppendsReplacesAndUndoes()
    {
        var (first, undoFirst) = Tools.Run(Call(BuiltInTools.WriteScratchpad, """{"text":"one"}"""));
        Assert.False(first.IsError);
        Assert.Equal("one", _pad.Text);
        Tools.Run(Call(BuiltInTools.WriteScratchpad, """{"text":"two"}"""));
        Assert.Equal("one\ntwo", _pad.Text);
        var (_, undoReplace) = Tools.Run(Call(BuiltInTools.WriteScratchpad, """{"text":"three","mode":"replace"}"""));
        Assert.Equal("three", _pad.Text);
        undoReplace!();
        Assert.Equal("one\ntwo", _pad.Text);
        undoFirst!();
        Assert.Equal("", _pad.Text);
    }

    [Fact]
    public void ShelfFileCreateUndoRemovesOrRestores()
    {
        var (created, undoCreate) = Tools.Run(Call(BuiltInTools.CreateShelfFile, """{"name":"a.txt","content":"first"}"""));
        Assert.False(created.IsError);
        Assert.Equal("first", _shelf.ReadText("a.txt"));
        var (_, undoReplace) = Tools.Run(Call(BuiltInTools.CreateShelfFile, """{"name":"a.txt","content":"second"}"""));
        Assert.Equal("second", _shelf.ReadText("a.txt"));
        undoReplace!();
        Assert.Equal("first", _shelf.ReadText("a.txt"));
        undoCreate!();
        Assert.Null(_shelf.ReadText("a.txt"));
        Assert.Empty(_shelf.ItemNames);
    }

    [Fact]
    public void ReadMissingItemListsWhatIsThere()
    {
        _shelf.CreateFile("b.txt", "bee");
        var (result, _) = Tools.Run(Call(BuiltInTools.ReadShelfItem, """{"name":"a.txt"}"""));
        Assert.True(result.IsError);
        Assert.Contains("b.txt", result.Content);
        Assert.Equal("bee", Tools.Run(Call(BuiltInTools.ReadShelfItem, """{"name":"B.TXT"}""")).Result.Content);
    }

    [Fact]
    public void ExternalToolsCallTheInjectedEffects()
    {
        Tools.Run(Call(BuiltInTools.OpenUrl, """{"url":"https://example.com"}"""));
        Tools.Run(Call(BuiltInTools.CopyToClipboard, """{"text":"copied"}"""));
        Assert.Equal(["https://example.com"], _opened);
        Assert.Equal(["copied"], _copied);
        Assert.Equal("Copy to the clipboard: \"copied\"", BuiltInTools.Describe(BuiltInTools.CopyToClipboard, JsonDocument.Parse("""{"text":"copied"}""").RootElement));
    }
}

public class PermissionTests
{
    [Fact]
    public void TiersDecideWhoAsks()
    {
        var grants = new PermissionGrants();
        Assert.False(grants.NeedsConfirmation("t", ActionTier.Read));
        Assert.False(grants.NeedsConfirmation("t", ActionTier.AppLocalWrite));
        Assert.True(grants.NeedsConfirmation("t", ActionTier.ExternalEffect));
        Assert.True(grants.NeedsConfirmation("t", ActionTier.Destructive));

        grants.AlwaysAllow("t");
        Assert.False(grants.NeedsConfirmation("t", ActionTier.ExternalEffect));
        Assert.True(grants.NeedsConfirmation("other", ActionTier.ExternalEffect));
        Assert.True(grants.NeedsConfirmation("t", ActionTier.Destructive), "destructive actions always confirm");

        grants.Revoke("t");
        Assert.True(grants.NeedsConfirmation("t", ActionTier.ExternalEffect));
        Assert.True(PermissionGrants.CanAlwaysAllow(ActionTier.ExternalEffect));
        Assert.False(PermissionGrants.CanAlwaysAllow(ActionTier.Destructive));
    }
}

public class PaletteLayoutTests
{
    [Fact]
    public void HeightGrowsInStepsBetweenTheSpecBounds()
    {
        Assert.Equal(150, PanelSize.PaletteHeightFor([]));
        // A tool step or an empty line is one line; 200 characters wrap to three.
        Assert.Equal(174, PanelSize.PaletteHeightFor([""]));
        Assert.Equal(222, PanelSize.PaletteHeightFor([new string('x', 200)]));
        Assert.Equal(366, PanelSize.PaletteHeightFor(Enumerable.Repeat("line", 10)));
        Assert.Equal(480, PanelSize.PaletteHeightFor(Enumerable.Repeat("line", 100)));
    }

    [Fact]
    public void ChipsAreTogglesWithAPictureANameAndAMarkAfterInclude()
    {
        ChipSpec[] chips = [new("Region", ChipIcon.Region, true), new("Selection", ChipIcon.Selection, false), new("Window", ChipIcon.Window, false), new("Screenshot", ChipIcon.Screenshot, true), new("Shelf", ChipIcon.Shelf, false), new("Note", ChipIcon.Note, false)];
        var layout = new PaletteLayout(640, mascot: true, chips: chips);

        // "Include" first, in the first row; the chips after it, each as tall as its click area must be.
        var include = layout.IncludeLabel;
        Assert.Equal(("Include", PaletteLayout.Pad, layout.ChipsTop), (include.Text, include.Slot.X, include.Slot.Y));
        Assert.True(layout.Chips[0].Box.X >= include.Slot.Right + PaletteLayout.IncludeGap - 0.01f);
        Assert.All(layout.Chips, chip =>
        {
            Assert.Equal(Tomte.Core.Interaction.StateLook.ChipHeight, chip.Box.Height);
            // The picture, the name and the mark sit inside the pill, left to right, without touching.
            Assert.True(chip.Box.Contains(chip.Icon) && chip.Box.Contains(chip.Mark));
            Assert.True(chip.Icon.Right < chip.Label.Slot.X && chip.Label.Slot.Right < chip.Mark.X);
            Assert.Equal(chip.Box.Y + chip.Box.Height / 2, chip.Icon.Y + chip.Icon.Height / 2, 2);
            Assert.Equal(chip.Box.Y + chip.Box.Height / 2, chip.Mark.Y + chip.Mark.Height / 2, 2);
        });

        // On or off as asked, in the on style; no two pills overlap; a row that wraps starts under the first chip.
        Assert.Equal([true, false, false, true, false, false], layout.Chips.Select(c => c.On));
        Assert.Equal(PaletteLayout.ChipOn, layout.Chips[3].Label.Style);
        Assert.Equal(PaletteLayout.Chip, layout.Chips[1].Label.Style);
        for (int i = 1; i < layout.Chips.Count; i++)
        {
            Assert.False(layout.Chips[i].Box.IntersectsWith(layout.Chips[i - 1].Box));
        }

        var wrapped = layout.Chips.First(c => c.Box.Y > layout.ChipsTop);
        Assert.Equal(layout.Chips[0].Box.X, wrapped.Box.X);
        Assert.Equal(layout.ChipsTop + PaletteLayout.ChipHeight + PaletteLayout.ChipGap, wrapped.Box.Y);

        // With no chips there is no "Include" and the answer starts under the prompt.
        var none = new PaletteLayout(640, mascot: true);
        Assert.Equal("", none.IncludeLabel.Text);
        Assert.Empty(none.Chips);

        // The line of what the send carries sits under the chips, where they start, and the answer moves down for it.
        var line = new PaletteLayout(640, mascot: true, chips: chips, sends: "Sends your question and the region you circled");
        var sends = line.SendsLine;
        Assert.Equal(layout.Chips[^1].Box.Bottom + PaletteLayout.ChipGap, sends.Slot.Y);
        Assert.Equal(layout.Chips[0].Box.X, sends.Slot.X);
        Assert.Equal(sends.Slot.Bottom + PaletteLayout.Gap, line.AnswerTop);
        Assert.Equal(layout.AnswerTop + PaletteLayout.ChipGap + PaletteLayout.SendsHeight, line.AnswerTop, 2);
        Assert.Equal("", layout.SendsLine.Text);
    }

    [Fact]
    public void SendSitsAtThePromptsRightEndAndWorksOnlyWithSomethingToSend()
    {
        var plain = new PaletteLayout(640, mascot: true);
        var layout = new PaletteLayout(640, mascot: true, text: 52, send: true);
        Assert.Equal(System.Drawing.RectangleF.Empty, plain.Send);

        // Inside the card at its right end, level with the last line as the box grows; the text box stops short of it.
        var card = layout.Prompt;
        var send = layout.Send;
        Assert.Equal(card.Right - PaletteLayout.SendInset, send.Right);
        Assert.Equal(card.Bottom - PaletteLayout.SendInset, send.Bottom);
        Assert.True(send.Width >= Tomte.Core.Interaction.StateLook.MinTarget && send.Height >= Tomte.Core.Interaction.StateLook.MinTarget);
        Assert.Equal(send.X - PaletteLayout.SendGap, layout.Editor.Right, 2);
        Assert.Equal(plain.Editor.X, layout.Editor.X);

        Assert.False(PaletteLayout.CanSend("", sending: false));
        Assert.False(PaletteLayout.CanSend(" \r\n", sending: false));
        Assert.True(PaletteLayout.CanSend("what is this?", sending: false));
        Assert.False(PaletteLayout.CanSend("what is this?", sending: true));
    }

    [Fact]
    public void TheBackArrowSitsBetweenTheTomteAndThePromptOnlyWhenOpenedFromThePanels()
    {
        var plain = new PaletteLayout(640, mascot: true);
        var back = new PaletteLayout(640, mascot: true, back: true);
        Assert.Equal(System.Drawing.RectangleF.Empty, plain.Back);

        // After the tomte, level with the prompt's first line; the prompt moves right by the arrow and its gap.
        var arrow = back.Back;
        Assert.True(arrow.Left >= MascotArt.InsetX + MascotArt.SizeDips);
        Assert.Equal(plain.Prompt.Y + (PaletteLayout.PromptHeight / 2), arrow.Y + (arrow.Height / 2));
        Assert.Equal(arrow.Right + PaletteLayout.BackGap, back.Prompt.X);
        Assert.Equal(plain.Prompt.Right, back.Prompt.Right);
        Assert.True(arrow.Width >= Tomte.Core.Interaction.StateLook.MinTarget);
        // With the tomte off it starts at the padding.
        Assert.Equal(PaletteLayout.Pad, new PaletteLayout(640, mascot: false, back: true).Back.X);
    }

    [Fact]
    public void GlyphsStayInTheirBoxes()
    {
        foreach (var (figures, size) in Enum.GetValues<ChipIcon>().Select(i => (PaletteGlyphs.Icon(i), PaletteLayout.IconSize))
            .Append((PaletteGlyphs.Plus, PaletteLayout.MarkSize)).Append((PaletteGlyphs.Tick, PaletteLayout.MarkSize)).Append((PaletteGlyphs.Send, PaletteGlyphs.SendSize))
            .Append((PaletteGlyphs.Back, PaletteGlyphs.SendSize)))
        {
            Assert.All(figures.SelectMany(f => f.Path.Select(s => (s.End, f.Stroked))), p =>
            {
                // A stroke reaches half its width past its outline.
                float edge = p.Stroked ? PaletteGlyphs.Stroke / 2 : 0;
                Assert.InRange(p.End.X, edge, size - edge);
                Assert.InRange(p.End.Y, edge, size - edge);
            });
        }
    }
}

public class SelectionRuleTests
{
    [Theory]
    [InlineData("hello", null, null, null, false)]
    [InlineData("", null, null, null, false)]
    [InlineData("", "Edit", "Chrome_WidgetWin_1", null, false)]
    [InlineData("", "The editor is not accessible at this time. To enable screen reader...", "native-edit-context", null, true)]
    [InlineData("", "Untitled", "native-edit-context", null, true)]
    [InlineData("hello", "The editor is not accessible at this time.", null, null, false)]
    [InlineData(null, "Pane", null, "no TextPattern", true)]
    [InlineData(null, null, null, "UIA 0x80070005", true)]
    public void FallsBackOnlyOnFailureOrTheVsCodeException(string? text, string? name, string? className, string? error, bool expected) =>
        Assert.Equal(expected, SelectionRule.ShouldFallBack(new UiaSelection(text, name, className, error)));

    [Theory]
    [InlineData("""{"isFromEmptySelection":true,"mode":"javascript"}""", true)]
    [InlineData("""vscode-editor-data\u0000{\"version\":1,\"isFromEmptySelection\":true}""", true)]
    [InlineData("""{"isFromEmptySelection" : true}""", true)]
    [InlineData("""{"isFromEmptySelection":false}""", false)]
    [InlineData("plain text", false)]
    [InlineData(null, false)]
    public void VsCodeWholeLineCopyIsDiscarded(string? mime, bool expected) =>
        Assert.Equal(expected, SelectionRule.IsEmptySelectionCopy(mime));

    [Fact]
    public void NormalizeMakesBothPathsAgree() =>
        Assert.Equal("a\n\nb", SelectionRule.Normalize("a\r\n\r\nb \r\n"));

    [Theory]
    [InlineData("Tomte is a Windows utility\r\n", null, null, null, "Tomte is a Windows utility")]
    [InlineData("", null, null, null, null)]
    [InlineData("   ", null, null, null, null)]
    // Where only the Ctrl+C fallback could tell, the hotkey palette offers nothing rather than a wrong "none".
    [InlineData("", "The editor is not accessible at this time.", "native-edit-context", null, null)]
    [InlineData(null, "Pane", null, "no TextPattern", null)]
    public void TheHotkeyPaletteTakesOnlyWhatUiAutomationSaysIsSelected(string? text, string? name, string? className, string? error, string? expected) =>
        Assert.Equal(expected, SelectionRule.Text(new UiaSelection(text, name, className, error)));

    [Fact]
    public void TheSelectionChipOffersWhatIsSelectedAndStaysOffUntilTurnedOn()
    {
        var shelf = new InMemoryShelf();
        var chips = ContextChips.For(null, "Meet at 10, not 9", "notepad", "notes.txt - Notepad", _ => Task.FromResult(Array.Empty<byte>()), shelf, new InMemoryScratchpad());
        Assert.Equal([ContextKind.Selection, ContextKind.Window, ContextKind.Screenshot, ContextKind.ShelfItem, ContextKind.Scratchpad], chips.Select(c => c.Kind));
        var selection = chips[0];
        Assert.Equal("Selected text from notepad, 17 characters", selection.Tip);
        Assert.True(selection.Available);
        Assert.All(chips, chip => Assert.False(chip.On));
        selection.On = true;
        Assert.True(selection.On);

        // Nothing selected: the chip has nothing to add, so it is not shown, and cannot be turned on.
        var none = ContextChips.For(null, null, "notepad", "notes.txt - Notepad", _ => Task.FromResult(Array.Empty<byte>()), shelf, new InMemoryScratchpad())[0];
        Assert.False(none.Available);
        none.On = true;
        Assert.False(none.On);
    }

    [Fact]
    public void WithNoWindowInFrontTheWindowAndScreenshotChipsCannotBeTurnedOn()
    {
        // The tray's "Ask AI" with no window the user worked in: no window is described, not the idle process.
        var (app, title) = WindowContext.Describe(0);
        Assert.Equal(("", ""), (app, title));
        var chips = ContextChips.For(null, null, app, title, _ => Task.FromResult(Array.Empty<byte>()), new InMemoryShelf(), new InMemoryScratchpad());
        var window = chips.Where(c => c.Kind is ContextKind.Window or ContextKind.Screenshot).ToList();
        Assert.Equal(2, window.Count);
        foreach (var chip in window)
        {
            Assert.False(chip.Available);
            chip.On = true;
            Assert.False(chip.On);
        }
    }
}
