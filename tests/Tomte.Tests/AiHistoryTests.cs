using Tomte.Core;
using Tomte.Core.Storage;
using Xunit;

namespace Tomte.Tests;

public class AiHistoryTests : IDisposable
{
    internal static readonly DateTimeOffset Morning = new(2026, 10, 14, 9, 30, 0, TimeSpan.Zero);

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tomte-history-{Guid.NewGuid():N}");
    private bool _on = true;

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private TomteDb Db => new(Path.Combine(_dir, "tomte.db"));

    private AiHistory History() => new(Db, () => _on);

    internal static AiExchange Talk(string key, DateTimeOffset at, string question = "What is this?") =>
        new(key, AiSource.Palette, at, "Ollama", "m", 0.01m, [new AiTurn(question, ["selection"], [], "An answer.")]);

    internal static KeptResult Kept(DateTimeOffset at, string answer = "# Q3\n- up") =>
        new(DropZone.Summarize, null, "report.pdf", @"C:\Shelf\report.pdf", "ollama", "llama3.2:3b", 0.0031m, at, answer, []);

    // ---- The store ----

    [Fact]
    public async Task ConversationsAndDropZoneResultsReadTogetherNewestFirst()
    {
        var history = History();
        await new KeptResults(Db).Keep(Kept(Morning.AddHours(1)), "stamp");
        await history.Keep(Talk("a", Morning));
        await history.Keep(Talk("b", Morning.AddHours(2), "Second question"));

        var all = await history.All(Morning.AddHours(3));

        Assert.Equal(["b", "kept:1", "a"], all.Select(e => e.Key));
        Assert.Equal("Second question", all[0].Question);
        // A drop zone result is one turn, named by its zone and file, read from its own table and not stored again.
        var drop = all[1];
        Assert.Equal(AiSource.DropZone, drop.Source);
        Assert.Equal("Summarize: report.pdf", drop.Question);
        Assert.Equal("# Q3\n- up", drop.LastAnswer);
        Assert.Equal(("llama3.2:3b", 0.0031m), (drop.Model, drop.CostUsd));
        Assert.Equal(["selection"], all[2].Turns[0].Chips);
    }

    [Fact]
    public async Task TheSameConversationKeptAgainReplacesItsRow()
    {
        var history = History();
        var first = Talk("a", Morning);
        await history.Keep(first);
        await history.Keep(first with { At = Morning.AddMinutes(5), CostUsd = 0.03m, Turns = [.. first.Turns, new AiTurn("And then?", [], [], "Then this.")] });

        var kept = Assert.Single(await history.All(Morning.AddHours(1)));
        Assert.Equal(2, kept.Turns.Count);
        Assert.Equal((Morning.AddMinutes(5), 0.03m, "Then this."), (kept.At, kept.CostUsd, kept.LastAnswer));
    }

    [Fact]
    public async Task AtMostFiveHundredAreKeptAcrossBothTables()
    {
        var history = History();
        // The oldest is a drop zone result, so the cap reaches into its table too.
        await new KeptResults(Db).Keep(Kept(Morning.AddMinutes(-1)), "stamp");
        for (int i = 0; i < AiHistory.MaxItems; i++)
        {
            await history.Keep(Talk($"k{i}", Morning.AddMinutes(i)));
        }

        var all = await history.All(Morning.AddDays(1));

        Assert.Equal(AiHistory.MaxItems, all.Count);
        Assert.DoesNotContain(all, e => e.Source == AiSource.DropZone);
        Assert.Equal("k499", all[0].Key);
        Assert.Equal("k0", all[^1].Key);
    }

    [Fact]
    public async Task NothingOlderThanNinetyDaysIsKept()
    {
        var history = History();
        await new KeptResults(Db).Keep(Kept(Morning.AddDays(-100)), "stamp");
        await history.Keep(Talk("old", Morning.AddDays(-91)));
        await history.Keep(Talk("recent", Morning.AddDays(-89)));
        await history.Keep(Talk("now", Morning));

        Assert.Equal(["now", "recent"], (await history.All(Morning)).Select(e => e.Key));
    }

    [Fact]
    public async Task ClearDeletesConversationsAndDropZoneResults()
    {
        var history = History();
        await new KeptResults(Db).Keep(Kept(Morning), "stamp");
        await history.Keep(Talk("a", Morning));

        await history.Clear();

        Assert.Empty(await history.All(Morning));
        Assert.Null(await new KeptResults(Db).Last(DropZone.Summarize, null, @"C:\Shelf\report.pdf"));
    }

    [Fact]
    public async Task OffKeepsNothingAndAReadDeletesWhatIsThere()
    {
        var history = History();
        await new KeptResults(Db).Keep(Kept(Morning), "stamp");
        await history.Keep(Talk("a", Morning));

        _on = false;
        await history.Keep(Talk("b", Morning));
        await new KeptResults(Db, on: () => _on).Keep(Kept(Morning.AddHours(1)), "stamp");
        Assert.Empty(await history.All(Morning));
        Assert.Null(await new KeptResults(Db, on: () => _on).Last(DropZone.Summarize, null, @"C:\Shelf\report.pdf"));

        // Turned on again, what was there before is gone.
        _on = true;
        Assert.Empty(await history.All(Morning));
    }

    [Fact]
    public async Task ADatabaseWithOnlyKeptResultsGainsTheHistoryTable()
    {
        // A tomte.db as the drop zones left it: the results feature at its first step, with one result in it.
        using (var connection = Db.Open("results", [KeptResults.Schema[0]]))
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = "INSERT INTO results_kept(zone, skill, source_name, source_path, source_stamp, provider, model, cost_usd, at, answer, files) " +
                "VALUES ('Tables', NULL, 'sales.xlsx', 'C:\\sales.xlsx', 's', 'ollama', 'llama3.2:3b', '0', $at, 'a,b', '')";
            insert.Parameters.AddWithValue("$at", Morning.ToUnixTimeMilliseconds());
            insert.ExecuteNonQuery();
        }

        var history = History();
        await history.Keep(Talk("a", Morning.AddHours(1)));
        var all = await history.All(Morning.AddHours(2));

        Assert.Equal(["a", "kept:1"], all.Select(e => e.Key));
        Assert.Equal("Tables to CSV: sales.xlsx", all[1].Question);
        using var check = Db.Open("results", KeptResults.Schema);
        using var version = check.CreateCommand();
        version.CommandText = "SELECT version FROM schema_versions WHERE feature = 'results'";
        Assert.Equal(2L, version.ExecuteScalar());
    }
}
