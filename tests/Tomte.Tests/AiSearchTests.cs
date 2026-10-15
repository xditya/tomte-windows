using System.Text.Json;
using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Search;
using Tomte.Core.Storage;
using Xunit;

namespace Tomte.Tests;

/// <summary>AI search: find_files' arguments and answers, show_results' filter, the prompt, and whole searches played back from recorded answers.</summary>
public sealed class AiSearchTests : IDisposable
{
    private static readonly DateTimeOffset Friday = new(2026, 10, 16, 0, 24, 0, TimeSpan.FromHours(5.5));
    private static readonly FoundFile Globex = new(@"D:\Corpus\Work\Invoices\invoice-2026-09-globex.pdf", false, 1200, new DateTime(2026, 9, 20, 10, 0, 0));
    private static readonly FoundFile Initech = new(@"D:\Corpus\Work\Invoices\invoice-2026-10-initech.pdf", false, 983, new DateTime(2026, 10, 10, 10, 0, 0));
    private static readonly FoundFile Invoices = new(@"D:\Corpus\Work\Invoices", true, null, new DateTime(2026, 10, 16, 0, 20, 0));

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tomte-ai-search-{Guid.NewGuid():N}");
    private readonly List<FileFilter> _asked = [];

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private static JsonElement Json(string json) => JsonDocument.Parse(json).RootElement;

    private static ToolCallPart Call(string name, string json) => new("c", name, Json(json));

    // A finder whose searches answer from a script, one list per call; the filters it was asked are kept.
    private FileFinder Finder(params IReadOnlyList<FoundFile>?[] answers) => new((filter, max, _) =>
    {
        _asked.Add(filter);
        Assert.Equal(FileFinder.PerCall, max);
        return answers[Math.Min(_asked.Count, answers.Length) - 1];
    });

    private PaletteSession Session(IChatProvider provider, ProviderConfig config, FileFinder finder, PaletteOptions? options = null, AiHistory? history = null) =>
        new(provider, config, options ?? new PaletteOptions { MaxToolSteps = FileFinder.MaxCalls + 3 }, new SendLog(Path.Combine(_dir, "send-log.jsonl")),
            new BuiltInTools(new InMemoryShelf(), new InMemoryScratchpad(), _ => { }, _ => { }), new PermissionGrants(), [])
        {
            Prompt = "the invoices I got last month",
            FileSearch = finder,
            History = history,
            Now = () => Friday,
        };

    [Fact]
    public void ArgumentsBecomeAFilter()
    {
        Assert.Null(FileFinder.Read(Json("""{"words":["Invoice","2026"],"folder":"c:","kind":"file","extensions":[".PDF","docx"],"modified_from":"2026-09-01","modified_to":"2026-09-30"}"""), out var filter));
        Assert.Equal(["Invoice", "2026"], filter!.Words);
        // A drive letter alone is its root; AI search's words may match the folders above a file too.
        Assert.Equal(@"C:\", filter.Folder, ignoreCase: true);
        Assert.True(filter.InPath);
        Assert.False(filter.Folders);
        Assert.Equal(["pdf", "docx"], filter.Extensions!);
        Assert.Equal((new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)), (filter.From!.Value, filter.To!.Value));
    }

    [Theory]
    // A small local model sent the array as JSON inside a string (recorded from llama3.2:3b).
    [InlineData("""{"words":"[\"invoices\"]"}""", new[] { "invoices" })]
    [InlineData("""{"words":"tax return"}""", new[] { "tax", "return" })]
    [InlineData("""{"words":["a\"b","one","two","three","four","five","six","seven"],"kind":""}""", new[] { "ab", "one", "two", "three", "four", "five" })]
    public void WordsAreCleanedAsTypedWordsAre(string json, string[] words)
    {
        Assert.Null(FileFinder.Read(Json(json), out var filter));
        Assert.Equal(words, filter!.Words);
    }

    [Fact]
    public void ANamedFolderIsAFullPathAndExtensionsMeanFiles()
    {
        Assert.Null(FileFinder.Read(Json("""{"folder":"downloads","kind":"folder","extensions":"jpg, png","folder2":1}"""), out var filter));
        Assert.True(Path.IsPathFullyQualified(filter!.Folder!));
        Assert.EndsWith("Downloads", filter.Folder);
        Assert.False(filter.Folders);
        Assert.Equal(["jpg", "png"], filter.Extensions!);
    }

    [Theory]
    [InlineData("[]", "Arguments must be a JSON object.")]
    [InlineData("""{"folder":"Work\\Taxes"}""", "folder must be a path on a local drive")]
    [InlineData("""{"folder":"C:\\a\"b"}""", "folder must be a path on a local drive")]
    // A share, a device and a \\?\ path never: only drive letters on fixed drives, or the named folders.
    [InlineData("""{"folder":"\\\\host\\share\\docs"}""", "folder must be a path on a local drive")]
    [InlineData("""{"folder":"\\\\.\\PhysicalDrive0"}""", "folder must be a path on a local drive")]
    [InlineData("""{"folder":"\\\\?\\C:\\Users"}""", "folder must be a path on a local drive")]
    [InlineData("""{"folder":"//host/share"}""", "folder must be a path on a local drive")]
    [InlineData("""{"words":["x"],"kind":"photo"}""", "kind must be")]
    [InlineData("""{"extensions":["p.df"]}""", "extensions must be")]
    [InlineData("""{"words":["x"],"modified_from":"last week"}""", "modified_from must be a day")]
    // The day after the last day is asked, so 9999-12-31 must never get that far.
    [InlineData("""{"words":["x"],"modified_to":"9999-12-31"}""", "modified_to must be a day")]
    [InlineData("""{"words":["x"],"modified_from":"1970-01-01"}""", "modified_from must be a day")]
    [InlineData("""{"modified_from":"2026-10-02","modified_to":"2026-10-01"}""", "modified_from is after")]
    [InlineData("""{"kind":"file","words":[]}""", "Give at least one")]
    public void BadArgumentsSayWhatToFix(string json, string message)
    {
        Assert.StartsWith(message, FileFinder.Read(Json(json), out var filter));
        Assert.Null(filter);
    }

    [Fact]
    public void FilesAreListedByFolderWithNumbersThatStay()
    {
        var finder = Finder([Globex], [Initech, Globex, Invoices]);

        var (summary, first, error) = finder.Run(Call(FileFinder.FindFiles, """{"words":["invoice"],"kind":"file","modified_from":"2026-09-01"}"""), default);
        var (_, second, _) = finder.Run(Call(FileFinder.FindFiles, """{"words":["invoice"]}"""), default);

        Assert.False(error);
        Assert.Equal("Searched files: invoice, files, from 2026-09-01", summary);
        Assert.Equal("1 found, newest first:\nD:\\Corpus\\Work\\Invoices\n1. invoice-2026-09-globex.pdf, 1 KB, 2026-09-20\n"
            + "Call show_results with the numbers that fit, or search again.", first);
        // The file found before keeps its number; a folder has no size.
        Assert.Equal("3 found, newest first:\nD:\\Corpus\\Work\\Invoices\n2. invoice-2026-10-initech.pdf, 983 B, 2026-10-10\n1. invoice-2026-09-globex.pdf, 1 KB, 2026-09-20\n"
            + "D:\\Corpus\\Work\n3. Invoices, folder, 2026-10-16\nCall show_results with the numbers that fit, or search again.", second);
        Assert.Equal([Globex, Initech, Invoices], finder.Found);
        Assert.Equal(2, finder.Calls);
    }

    [Fact]
    public void AFullPageSaysThereIsMoreAndNothingFoundSaysSo()
    {
        var page = Enumerable.Range(1, FileFinder.PerCall).Select(i => new FoundFile($@"C:\F\{i}.txt", false, 5L << 30, null)).ToList();
        var finder = Finder(page, []);

        var (_, full, _) = finder.Run(Call(FileFinder.FindFiles, """{"words":["x"]}"""), default);
        var (_, none, _) = finder.Run(Call(FileFinder.FindFiles, """{"words":["y"]}"""), default);

        Assert.StartsWith("50 found, newest first (only the first 50; narrow the search for others):", full);
        Assert.Contains("50. 50.txt, 5 GB, date unknown", full);
        Assert.Equal("No files found. Try other or fewer words, or fewer filters.", none);
    }

    [Fact]
    public void SearchesStopAfterTheCapAndAMissingSearchIsAnError()
    {
        var finder = Finder([Globex]);
        for (int i = 0; i < FileFinder.MaxCalls; i++)
            finder.Run(Call(FileFinder.FindFiles, """{"words":["x"]}"""), default);

        var (_, result, error) = finder.Run(Call(FileFinder.FindFiles, """{"words":["x"]}"""), default);

        Assert.True(error);
        Assert.StartsWith("No searches left", result);
        Assert.Equal(FileFinder.MaxCalls, _asked.Count);
        var (_, off, offError) = Finder([null]).Run(Call(FileFinder.FindFiles, """{"words":["x"]}"""), default);
        Assert.True(offError);
        Assert.StartsWith("File search is not available", off);
    }

    [Fact]
    public void OnlyFilesFindFilesReturnedCanBeShown()
    {
        var finder = Finder([Globex, Initech]);
        finder.Run(Call(FileFinder.FindFiles, """{"words":["invoice"]}"""), default);

        // 99 was never returned, the made-up path neither; "1" as text and a returned path count; a repeat counts once.
        var (summary, result, error) = finder.Run(Call(FileFinder.ShowResults,
            """{"numbers":[99,"1",1,"D:\\Corpus\\Work\\Invoices\\invoice-2026-10-initech.pdf","D:\\Corpus\\made-up.pdf",0,-2],"reason":"  The   September\n invoice. "}"""), default);

        Assert.False(error);
        Assert.Equal(("Showed 2 files", "Shown."), (summary, result));
        Assert.Equal([Globex, Initech], finder.Picked!);
        Assert.Equal("The September invoice.", finder.Reason);
    }

    [Fact]
    public void ShowResultsWithoutNumbersIsSentBack()
    {
        var finder = Finder([Globex]);
        var (_, result, error) = finder.Run(Call(FileFinder.ShowResults, """{"reason":"x"}"""), default);
        Assert.True(error);
        Assert.StartsWith("show_results needs", result);
        Assert.Null(finder.Picked);
        Assert.True(finder.Run(Call("open_url", "{}"), default).IsError);
    }

    [Fact]
    public void ThePromptIsShortAndCarriesTheDay()
    {
        string prompt = FileFinder.Prompt(Friday);
        Assert.EndsWith("Today is Friday 16 October 2026.", prompt);
        Assert.Contains("never what is in a file", prompt);
        Assert.Contains("a plain reason under 60 characters", prompt);
        Assert.True(prompt.Length < 600, $"{prompt.Length} characters");
        Assert.Equal([FileFinder.FindFiles, FileFinder.ShowResults], FileFinder.Definitions.Select(d => d.Name));
    }

    /// <summary>
    /// Recorded from claude-haiku-5-5 on 2026-10-16 (a real provider, a scratch data folder, a named Everything
    /// instance over a scratch folder): one search, two at once, then show_results with 1 and 3.
    /// </summary>
    [Fact]
    public async Task ACloudSearchPicksAmongWhatItFoundAndStops()
    {
        var handler = new RecordedHandler("anthropic-ai-search-1.sse", then: ["anthropic-ai-search-2.sse", "anthropic-ai-search-3.sse"]);
        var config = new ProviderConfig("api.anthropic.com", ProviderKind.Anthropic, null, "claude-haiku-5-5");
        var history = new AiHistory(new TomteDb(Path.Combine(_dir, "tomte.db")), () => true);
        var options = new PaletteOptions { MaxToolSteps = FileFinder.MaxCalls + 3, Prices = PriceTable.Models };
        var session = Session(new AnthropicProvider(new HttpClient(handler), "key"), config, Finder([Globex], [], [Initech, Globex, Invoices]), options, history);

        await session.SendAsync();

        Assert.Equal(PaletteStatus.Done, session.Status);
        Assert.Equal(3, handler.Bodies.Count);
        Assert.Equal([Globex, Invoices], session.FileSearch!.Picked!);
        Assert.StartsWith("The September invoice is invoice-2026-09-globex.pdf", session.FileSearch.Reason);
        // Asked as recorded: last month as its days, files only first.
        Assert.Equal((new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30), (bool?)false), (_asked[0].From!.Value, _asked[0].To!.Value, _asked[0].Folders));
        // Only the two tools and the finder's prompt went; no chip, no other tool.
        using var first = JsonDocument.Parse(handler.Bodies[0]);
        Assert.Equal([FileFinder.FindFiles, FileFinder.ShowResults], first.RootElement.GetProperty("tools").EnumerateArray().Select(t => t.GetProperty("name").GetString()));
        Assert.Equal(FileFinder.Prompt(Friday), first.RootElement.GetProperty("system").GetString());
        Assert.Equal("the invoices I got last month", first.RootElement.GetProperty("messages")[0].GetProperty("content")[0].GetProperty("text").GetString());

        var sent = Assert.Single(new SendLog(Path.Combine(_dir, "send-log.jsonl")).ReadAll());
        Assert.Equal((4020, 782, 0.000793m), (sent.InputTokens, sent.OutputTokens, sent.CostUsd));
        Assert.Equal(["names, sizes and dates of 3 files"], sent.Attached);
        var kept = Assert.Single(await history.All(Friday));
        Assert.Equal(AiSource.Search, kept.Source);
        Assert.Equal(["Searched files: invoice, files, from 2026-09-01, to 2026-09-30", "Searched files: invoices, from 2026-09-01, to 2026-09-30",
            "Searched files: invoice, from 2026-09-01, to 2026-10-16", "Showed 2 files"], kept.Turns[0].Steps);
        Assert.EndsWith($"{Globex.Path}\n{Invoices.Path}", kept.Turns[0].Answer);
    }

    /// <summary>Recorded from qwen2.5-coder:7b on Ollama on 2026-10-16: one search, then show_results with 1. Costs nothing.</summary>
    [Fact]
    public async Task ALocalSearchCostsNothing()
    {
        var handler = new RecordedHandler("openai-ollama-ai-search-1.sse", then: ["openai-ollama-ai-search-2.sse"]);
        var config = new ProviderConfig("127.0.0.1:11434", ProviderKind.OpenAICompatible, "http://127.0.0.1:11434/v1", "qwen2.5-coder:7b");
        var session = Session(new OpenAICompatibleProvider(new HttpClient(handler), "http://127.0.0.1:11434/v1", null), config, Finder([Globex]));

        await session.SendAsync();

        Assert.Equal([Globex], session.FileSearch!.Picked!);
        Assert.Equal("Found an invoice from last month.", session.FileSearch.Reason);
        var sent = Assert.Single(new SendLog(Path.Combine(_dir, "send-log.jsonl")).ReadAll());
        Assert.Equal((1140, 86, 0m), (sent.InputTokens, sent.OutputTokens, sent.CostUsd));
    }

    /// <summary>Recorded from llama3.2:3b on Ollama: the words as JSON in a string, then prose instead of show_results.</summary>
    [Fact]
    public async Task AnAnswerInProseShowsNoFiles()
    {
        var handler = new RecordedHandler("openai-ollama-ai-search-prose-1.sse", then: ["openai-ollama-ai-search-prose-2.sse"]);
        var config = new ProviderConfig("127.0.0.1:11434", ProviderKind.OpenAICompatible, "http://127.0.0.1:11434/v1", "llama3.2:3b");
        var session = Session(new OpenAICompatibleProvider(new HttpClient(handler), "http://127.0.0.1:11434/v1", null), config, Finder(Array.Empty<FoundFile>()));

        await session.SendAsync();

        Assert.True(session.Status == PaletteStatus.Done, session.Error);
        Assert.Equal(["invoices"], Assert.Single(_asked).Words);
        Assert.Null(session.FileSearch!.Picked);
        Assert.StartsWith("The system can only search", session.AnswerText);
    }

    [Fact]
    public async Task NothingIsSentPastTheCap()
    {
        var provider = new ScriptedProvider();
        var config = new ProviderConfig("p", ProviderKind.OpenAICompatible, "http://localhost", "m");
        new SendLog(Path.Combine(_dir, "send-log.jsonl")).Append(new SendLogEntry(DateTimeOffset.Now, "p", "m", 1, 1, 2m, [], null));
        var session = Session(provider, config, Finder([Globex]), new PaletteOptions { DailyCapUsd = 1m });

        await session.SendAsync();

        Assert.Equal(PaletteStatus.Failed, session.Status);
        Assert.StartsWith("Daily cap of $1.00 reached", session.Error);
        Assert.Empty(provider.Requests);
        Assert.Empty(_asked);
    }

    [Fact]
    public async Task AToolTheSearchWasNotGivenNeverRuns()
    {
        var provider = new ScriptedProvider();
        provider.Turns.Add(ScriptedProvider.Call(BuiltInTools.OpenUrl, """{"url":"https://example.com"}"""));
        provider.Turns.Add(ScriptedProvider.Call(FileFinder.ShowResults, """{"numbers":[1],"reason":"none"}"""));
        var config = new ProviderConfig("p", ProviderKind.OpenAICompatible, "http://localhost", "m");
        var session = Session(provider, config, Finder([Globex]));

        await session.SendAsync();

        var step = Assert.IsType<ToolStepSegment>(session.Answer[0]);
        Assert.Equal(("There is no tool named open_url.", true), (step.Result, step.IsError));
        // Nothing was found, so number 1 is nobody.
        Assert.Empty(session.FileSearch!.Picked!);
        Assert.Equal(2, provider.Requests.Count);
    }
}
