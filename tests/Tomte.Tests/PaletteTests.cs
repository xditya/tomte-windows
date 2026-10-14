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
        log.Append(new SendLogEntry(now.AddHours(-1), "p", "m", 1, 1, 0.75m, [], null));
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
        Assert.Equal("Selection · none", ContextChips.Selection("   ").Label);
        var selection = ContextChips.Selection("The quick brown fox jumps over the lazy dog");
        Assert.True(selection.Available);
        Assert.False(selection.On, "every chip starts off; the user opts in per request");
        Assert.Equal("Selection · \"The quick brown fox jumps ov...\"", selection.Label);
        Assert.Equal("<selection>\nThe quick brown fox jumps over the lazy dog\n</selection>",
            ((TextPart)(await selection.ReadAsync(CancellationToken.None))[0]).Text);

        Assert.False(ContextChips.Shelf(new InMemoryShelf()).Available);
        var shelf = ContextChips.Shelf(_shelf);
        Assert.Equal("Shelf · notes.txt", shelf.Label);
        // The chip names the file; the session reads it at send.
        Assert.Equal("notes.txt", Assert.IsType<Tomte.AI.Files.ShelfFilePart>(Assert.Single(await shelf.ReadAsync(CancellationToken.None))).Name);

        _scratchpad.Text = "todo";
        Assert.Contains("todo", ((TextPart)(await ContextChips.Scratchpad(_scratchpad).ReadAsync(CancellationToken.None))[0]).Text);
        Assert.Equal("Window · msedge · Top 20 Adventure Games", ContextChips.Window("msedge", "Top 20 Adventure Games").Label);
    }

    [Fact]
    public async Task SelectionFallbackRunsOnlyWhenTheUserTurnsTheChipOn()
    {
        int reads = 0;
        var chip = ContextChips.Selection(null, () =>
        {
            reads++;
            return "copied text";
        });
        Assert.True(chip.Available);
        Assert.False(chip.On);
        Assert.Equal("Selection · read on use", chip.Label);
        Assert.Equal(0, reads);

        chip.On = true;
        Assert.Equal(1, reads);
        Assert.True(chip.On);
        Assert.Equal("Selection · \"copied text\"", chip.Label);
        Assert.Contains("copied text", ((TextPart)(await chip.ReadAsync(CancellationToken.None))[0]).Text);
        chip.On = false;
        chip.On = true;
        Assert.Equal(1, reads);

        var nothing = ContextChips.Selection(null, () => null);
        nothing.On = true;
        Assert.False(nothing.On);
        Assert.False(nothing.Available);
        Assert.Equal("Selection · none", nothing.Label);
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
        Assert.Equal("Selection · \"Meet at 10, not 9\"", selection.Label);
        Assert.True(selection.Available);
        Assert.All(chips, chip => Assert.False(chip.On));
        selection.On = true;
        Assert.True(selection.On);

        // Nothing selected: the chip says so and cannot be turned on.
        var none = ContextChips.For(null, null, "notepad", "notes.txt - Notepad", _ => Task.FromResult(Array.Empty<byte>()), shelf, new InMemoryScratchpad())[0];
        Assert.Equal("Selection · none", none.Label);
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
