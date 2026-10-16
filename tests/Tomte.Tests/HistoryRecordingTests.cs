using Tomte.AI;
using Tomte.AI.Jobs;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Jobs;
using Tomte.Core.Storage;
using Xunit;

namespace Tomte.Tests;

/// <summary>The palette, voice commands and jobs keep their turns in the AI history, and a kept conversation goes on.</summary>
public class HistoryRecordingTests : IDisposable
{
    private static readonly DateTimeOffset Morning = AiHistoryTests.Morning;
    private static readonly ProviderConfig Config = new("p", ProviderKind.OpenAICompatible, "http://localhost", "m");

    // 1000 dollars a million either way: a scripted turn of 10 in and 5 out costs $0.015.
    private static readonly PaletteOptions Priced = new() { Prices = new Dictionary<string, ModelPrice> { ["m"] = new(1000m, 1000m) } };

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tomte-recording-{Guid.NewGuid():N}");
    private readonly ScriptedProvider _provider = new();
    private readonly InMemoryShelf _shelf = new();
    private readonly InMemoryScratchpad _scratchpad = new();

    public HistoryRecordingTests() => _shelf.CreateFile("notes.txt", "Milk, eggs, bread.");

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private TomteDb Db => new(Path.Combine(_dir, "tomte.db"));

    private AiHistory History() => new(Db, () => true);

    private string LogPath => Path.Combine(_dir, "send-log.jsonl");

    private static KeptResult Kept(DateTimeOffset at) => AiHistoryTests.Kept(at);

    private PaletteSession Session(AiHistory history, IReadOnlyList<ContextChip>? chips = null, AiExchange? continuing = null) =>
        new(_provider, Config, Priced, new SendLog(LogPath), new BuiltInTools(_shelf, _scratchpad, _ => { }, _ => { }), new PermissionGrants(), chips ?? [])
        {
            History = history,
            Continuing = continuing,
            Now = () => Morning,
        };

    [Fact]
    public async Task APaletteSessionKeepsEveryTurnAsOneConversation()
    {
        var history = History();
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "It is ", "a lasso."));
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "**Blue.**"));
        // The circled region as circle-to-ask names it; text here, since the scripted provider reads no images.
        var lasso = new ContextChip(ContextKind.Region, "Region", "lasso",
            _ => Task.FromResult<IReadOnlyList<ContentPart>>([new TextPart("pixels")]), on: true);
        var off = ContextChips.Scratchpad(_scratchpad);
        var session = Session(history, [lasso, off]);

        session.Prompt = "What is in the circle?";
        await session.SendAsync();
        // Each chip is opt-in per request: after a finished send they are all off again.
        Assert.False(lasso.On);
        session.Prompt = "What colour is it?";
        await session.SendAsync();
        Assert.Contains(_provider.Requests[0].Messages[^1].Parts, p => p is TextPart { Text: "pixels" });
        Assert.DoesNotContain(_provider.Requests[1].Messages.SelectMany(m => m.Parts), p => p is TextPart { Text: "pixels" });

        var kept = Assert.Single(await history.All(Morning));
        Assert.Equal(AiSource.CircleToAsk, kept.Source);
        Assert.Equal(2, kept.Turns.Count);
        // The names of the chips that were on, never what they attached.
        var first = kept.Turns[0];
        Assert.Equal(("What is in the circle?", "It is a lasso.", null), (first.Prompt, first.Answer, first.Error));
        Assert.Equal(["lasso"], first.Chips);
        Assert.Empty(first.Steps);
        // What Copy answer copies: the model's text as written, Markdown and all.
        Assert.Equal(("**Blue.**", 0.03m, "p", "m"), (kept.LastAnswer, kept.CostUsd, kept.Provider, kept.Model));

        // The follow-up went with the first turn's text, and the send log says so.
        var second = _provider.Requests[1];
        Assert.Equal(3, second.Messages.Count);
        Assert.Equal("What is in the circle?", ((TextPart)second.Messages[0].Parts[0]).Text);
        Assert.Equal(ChatRole.Assistant, second.Messages[1].Role);
        Assert.Equal("It is a lasso.", ((TextPart)second.Messages[1].Parts[0]).Text);
        Assert.Contains("text of 1 earlier turn", new SendLog(LogPath).ReadAll()[1].Attached);
    }

    [Fact]
    public async Task AFollowUpCountsTheEarlierTextInItsSize()
    {
        var earlier = new AiExchange("old", AiSource.Palette, Morning, "Ollama", "m", 0, [new AiTurn("Tell me a long story", [], [], string.Join(' ', Enumerable.Repeat("word", 2000)))]);
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "Done."));
        var history = History();
        var fresh = Session(history);
        var going = Session(history, continuing: earlier);
        fresh.Prompt = going.Prompt = "And then?";

        await fresh.SendAsync();
        await going.SendAsync();

        // The earlier text goes in front of the question, so the estimate (and the fit check behind it) counts it.
        Assert.InRange(going.Estimate!.Tokens - fresh.Estimate!.Tokens, 1000, 3000);
        // The writes are done before the folder goes.
        await history.All(Morning);
    }

    [Fact]
    public async Task APaletteStillOpenAfterAClearKeepsOnlyItsNewTurns()
    {
        var history = History();
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "First answer."));
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "Second answer."));
        var session = Session(history);
        session.Prompt = "First";
        await session.SendAsync();

        await history.Clear();
        session.Prompt = "Second";
        await session.SendAsync();

        // The model still has the whole conversation; the history keeps only what came after the clear.
        Assert.Equal(3, _provider.Requests[1].Messages.Count);
        var kept = Assert.Single(await history.All(Morning));
        Assert.Equal(["Second"], kept.Turns.Select(t => t.Prompt));
        Assert.Equal(0.015m, kept.CostUsd);
    }

    [Fact]
    public async Task ToolCallsAreKeptAsLinesWithoutWhatAReadBroughtBack()
    {
        var history = History();
        _provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.ReadShelfItem, """{"name":"notes.txt"}""", "c1"));
        _provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.WriteScratchpad, """{"text":"Buy milk"}""", "c2"));
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "Noted."));
        var shelf = ContextChips.Shelf(_shelf);
        shelf.On = true;
        var session = Session(history, [shelf]);
        session.Prompt = "Put the shopping on my note";

        await session.SendAsync();

        var turn = Assert.Single(Assert.Single(await history.All(Morning)).Turns);
        Assert.Equal(["shelf:notes.txt"], turn.Chips);
        Assert.Equal(2, turn.Steps.Count);
        Assert.DoesNotContain("Milk, eggs", turn.Steps[0], StringComparison.Ordinal);
        Assert.StartsWith(BuiltInTools.Describe(BuiltInTools.WriteScratchpad, System.Text.Json.JsonDocument.Parse("""{"text":"Buy milk"}""").RootElement) + ": ", turn.Steps[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContinuingSendsTheEarlierTextAndKeepsTheSameRow()
    {
        var history = History();
        var earlier = new AiExchange("old", AiSource.Voice, Morning.AddHours(-1), "Ollama", "m", 0.02m,
            [new AiTurn("Remind me what the dentist said", [], ["Saved a note"], ""), new AiTurn("And the time?", ["lasso"], [], "Monday at 10."),
             new AiTurn("Remind me an hour before", [], ["Set a timer: Timer set; it ends at 09:00."], "Done.")]);
        await history.Add(earlier);
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "Yes, Monday."));
        var session = Session(history, continuing: earlier);

        Assert.Equal(3, session.Conversation.Count);
        session.Prompt = "Is that this week?";
        await session.SendAsync();

        // Each earlier turn as text: the question, then its tool lines and its answer.
        var request = Assert.Single(_provider.Requests);
        Assert.Equal(["Remind me what the dentist said", "Saved a note", "And the time?", "Monday at 10.", "Remind me an hour before",
            "Set a timer: Timer set; it ends at 09:00.\nDone.", "Is that this week?"], request.Messages.Select(m => ((TextPart)m.Parts[^1]).Text));
        var kept = Assert.Single(await history.All(Morning));
        Assert.Equal(("old", AiSource.Voice, 4, 0.035m), (kept.Key, kept.Source, kept.Turns.Count, kept.CostUsd));
    }

    [Fact]
    public async Task ContinuingADropZoneResultStartsAConversationOfItsOwn()
    {
        var history = History();
        await new KeptResults(Db).Keep(Kept(Morning.AddHours(-1)), "stamp");
        var drop = Assert.Single(await history.All(Morning));
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "Mostly the north."));
        var session = Session(history, continuing: drop);
        session.Prompt = "Where did revenue grow?";

        await session.SendAsync();

        var all = await history.All(Morning);
        Assert.Equal(2, all.Count);
        var talk = all.Single(e => e.Source == AiSource.Palette);
        Assert.Equal(["Summarize: report.pdf", "Where did revenue grow?"], talk.Turns.Select(t => t.Prompt));
        Assert.Equal(0.015m, talk.CostUsd);
    }

    [Fact]
    public async Task AFailedSendIsKeptOnlyWhenItCostMoney()
    {
        var history = History();
        _provider.Throw = new HttpRequestException("connection refused");
        var session = Session(history);
        session.Prompt = "hi";

        await session.SendAsync();

        Assert.Equal(PaletteStatus.Failed, session.Status);
        Assert.Empty(await history.All(Morning));
        Assert.Empty(session.Conversation);
    }

    [Fact]
    public async Task AJobsResultIsKeptAndSoIsAFailedRunThatCostMoney()
    {
        var history = History();
        var job = new ScheduledJob(1, "Morning notes", "Summarise my note", new JobSchedule { Time = new TimeOnly(9, 0) }, [BuiltInTools.WriteScratchpad], Config, null);
        var runner = new JobRunner(new JobStore(Db), new NoJobTasks(), _ => _provider, new BuiltInTools(_shelf, _scratchpad, _ => { }, _ => { }), new SendLog(LogPath), _ => Priced)
        {
            History = history,
            Now = () => Morning,
        };
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "Nothing new."));
        await runner.RunAsync(job, Morning);

        var done = Assert.Single(await history.All(Morning));
        Assert.Equal(AiSource.Job, done.Source);
        Assert.Equal(("Morning notes: Summarise my note", "Nothing new.", null), (done.Turns[0].Prompt, done.Turns[0].Answer, done.Turns[0].Error));

        // A call to a tool the job was not approved for ends the run; its first turn was paid for, so it is kept, with why.
        _provider.Turns.Clear();
        _provider.Requests.Clear();
        _provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.OpenUrl, """{"url":"https://example.com"}"""));
        await runner.RunAsync(job, Morning);

        var failed = (await history.All(Morning)).Single(e => e.Key != done.Key);
        Assert.Equal("The job called open_url, which it was not approved to use, so the run stopped.", failed.Turns[0].Error);
        Assert.Equal(0.015m, failed.CostUsd);
    }

    [Fact]
    public async Task ADropZoneSendIsNotKeptAsAConversation()
    {
        var history = History();
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "A summary."));
        var session = new PaletteSession(_provider, Config, Priced, new SendLog(LogPath), new BuiltInTools(_shelf, _scratchpad, _ => { }, _ => { }), new PermissionGrants(), [])
        {
            FromDropZone = true,
            History = history,
            Prompt = "Summarize the attached file.",
        };

        await session.SendAsync();

        Assert.Empty(await history.All(Morning));
    }
}
