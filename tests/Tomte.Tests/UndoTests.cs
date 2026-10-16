using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Triggers;
using Xunit;

namespace Tomte.Tests;

public sealed class UndoTests : IDisposable
{
    private static readonly DateTimeOffset T0 = new(2026, 10, 16, 12, 0, 0, TimeSpan.Zero);
    private readonly string dir = Path.Combine(Path.GetTempPath(), "tomte-undo-" + Guid.NewGuid().ToString("N"));
    private readonly ScriptedProvider provider = new();
    private readonly UndoClose undo = new();
    private int restored, freed;

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(-5, null)]
    [InlineData(5, 5)]
    [InlineData(10, 10)]
    [InlineData(3, 5)]
    [InlineData(7, 5)]
    [InlineData(8, 10)]
    [InlineData(60, 10)]
    public void TheSettingIsOffFiveOrTenSeconds(int seconds, int? held) =>
        Assert.Equal(held is { } s ? TimeSpan.FromSeconds(s) : null, UndoClose.Time(seconds));

    [Fact]
    public void UndoIsTenSecondsUnlessChangedAndTheChoiceIsKept()
    {
        Assert.Equal(10, new TomteSettings().UndoSeconds);
        string file = Path.Combine(dir, "settings.json");
        using (var store = new SettingsStore(file))
        {
            Assert.Equal(10, store.Current.UndoSeconds);
            store.Save(store.Current with { UndoSeconds = 5 });
        }

        Assert.Equal(5, SettingsStore.Load(file).UndoSeconds);
    }

    [Fact]
    public void AClosedViewIsKeptForItsTimeAndLetGoWhenItIsUp()
    {
        Assert.True(Keep("AI answer closed"));
        Assert.Equal("AI answer closed", undo.Line);
        Assert.True(undo.Live(T0.AddSeconds(9.9)));
        Assert.Equal(TimeSpan.FromSeconds(4), undo.Left(T0.AddSeconds(6)));
        Assert.False(undo.Live(T0.AddSeconds(10)));
        Assert.Equal(0, freed);

        // The timer's end: let go once, and nothing comes back after.
        undo.End();
        undo.End();
        Assert.Equal((0, 1), (restored, freed));
        Assert.Null(undo.Line);
        Assert.False(undo.Undo(T0.AddSeconds(11)));
        Assert.Equal((0, 1), (restored, freed));
    }

    [Fact]
    public void UndoInTimeBringsItBackAndNeverLetsItGo()
    {
        Keep("AI search closed");

        Assert.True(undo.Undo(T0.AddSeconds(3)));
        Assert.Equal((1, 0), (restored, freed));
        Assert.False(undo.Live(T0.AddSeconds(3)));
        // A second press, or the timer firing after, finds nothing.
        Assert.False(undo.Undo(T0.AddSeconds(4)));
        undo.End();
        Assert.Equal((1, 0), (restored, freed));
    }

    [Fact]
    public void UndoAfterTheTimeIsUpLetsItGo()
    {
        Keep("AI answer closed");

        Assert.False(undo.Undo(T0.AddSeconds(10)));
        Assert.Equal((0, 1), (restored, freed));
    }

    [Fact]
    public void AViewThatCannotComeBackIsLetGo()
    {
        undo.Keep("AI answer closed", () => false, () => freed++, T0, TimeSpan.FromSeconds(5));

        Assert.False(undo.Undo(T0.AddSeconds(1)));
        Assert.Equal(1, freed);
        Assert.Null(undo.Line);
    }

    [Fact]
    public void WithUndoOffAClosedViewIsLetGoAtOnce()
    {
        Assert.False(undo.Keep("AI answer closed", () => ++restored > 0, () => freed++, T0, UndoClose.Time(0)));

        Assert.Equal((0, 1), (restored, freed));
        Assert.Null(undo.Line);
        Assert.False(undo.Live(T0));
    }

    [Fact]
    public void ANewerCloseLetsTheOlderOneGo()
    {
        int olderFreed = 0;
        undo.Keep("AI answer closed", () => true, () => olderFreed++, T0, TimeSpan.FromSeconds(10));

        Keep("Transcript closed", at: T0.AddSeconds(2));

        Assert.Equal(1, olderFreed);
        Assert.Equal("Transcript closed", undo.Line);
        Assert.True(undo.Live(T0.AddSeconds(11)));
    }

    [Fact]
    public async Task ARequestStillRunningGoesOnThroughTheUndoTimeAndIsCancelledWhenItRunsOut()
    {
        provider.Turns.Add(ScriptedProvider.Text(10, 5, "a", "b", "c", "d", "e", "f"));
        provider.DelayPerEvent = TimeSpan.FromMilliseconds(40);
        var session = Session();
        var sending = session.SendAsync();
        await Until(() => session.AnswerText.Length >= 1);

        // Closed: kept, the send goes on.
        undo.Keep("AI answer closed", () => true, session.Cancel, T0, TimeSpan.FromSeconds(10));
        await Until(() => session.AnswerText.Length >= 3);
        Assert.Equal(PaletteStatus.Sending, session.Status);

        // The time runs out without Undo: cancelled then.
        undo.End();
        await sending;
        Assert.Equal(PaletteStatus.Cancelled, session.Status);
        Assert.True(session.AnswerText.Length < 6);
    }

    [Fact]
    public async Task UndoKeepsTheRequestRunningToItsAnswer()
    {
        provider.Turns.Add(ScriptedProvider.Text(10, 5, "a", "b", "c", "d"));
        provider.DelayPerEvent = TimeSpan.FromMilliseconds(30);
        var session = Session();
        var sending = session.SendAsync();
        await Until(() => session.AnswerText.Length >= 1);
        undo.Keep("AI answer closed", () => true, session.Cancel, T0, TimeSpan.FromSeconds(10));

        Assert.True(undo.Undo(T0.AddSeconds(1)));
        await sending;

        Assert.Equal(PaletteStatus.Done, session.Status);
        Assert.Equal("abcd", session.AnswerText);
    }

    [Fact]
    public async Task OnlyWhatCameFromTheProviderOrIsOnItsWayIsKept()
    {
        // Nothing sent yet: typed words alone are not kept.
        var session = Session();
        Assert.False(session.Holds);

        provider.Turns.Add(ScriptedProvider.Text(10, 5, "a", "b"));
        provider.DelayPerEvent = TimeSpan.FromMilliseconds(30);
        var sending = session.SendAsync();
        Assert.True(session.Holds);
        await sending;
        Assert.True(session.Holds);

        // A conversation picked up from the history, with nothing new: the history keeps it already.
        var turn = new AiTurn("earlier", [], [], "an answer");
        var continued = Session(continuing: new AiExchange("k", AiSource.Palette, T0, "p", "m", 0, [turn]));
        Assert.Single(continued.Conversation);
        Assert.False(continued.Holds);

        // A send that failed before anything came back.
        var capped = Session(new PaletteOptions { DailyCapUsd = 0 });
        await capped.SendAsync();
        Assert.Equal(PaletteStatus.Failed, capped.Status);
        Assert.False(capped.Holds);
    }

    [Fact]
    public void TheUndoPillHoldsTheIslandAndARestOnItOpensNothing()
    {
        Assert.False(RetractionRules.OpensPanels(IslandState.Compact, IslandActivity.Undo));
        Assert.False(RetractionRules.ShouldRetract(IslandState.Compact, IslandActivity.Undo));
        Assert.True(RetractionRules.OpensPanels(IslandState.Compact, IslandActivity.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ThePillFitsItsWordsAndButtonAndIsNeverNarrowerThanCompact(bool mascot)
    {
        var (narrow, _, _) = CompactLayout.Undo(mascot, 20, 30);
        Assert.Equal((float)IslandGeometry.Compact.Width, narrow);

        var (width, line, button) = CompactLayout.Undo(mascot, 110, 90);
        Assert.True(width > IslandGeometry.Compact.Width);
        // The button ends inside the pill's right end, the line starts where the ring would and ends before the button.
        Assert.Equal((width / 2) - CompactLayout.UndoInset, button.Right, 3);
        Assert.Equal(CompactLayout.UndoInset, button.Top);
        Assert.Equal((float)IslandGeometry.Compact.Depth - CompactLayout.UndoInset, button.Bottom, 3);
        Assert.Equal((-width / 2) + CompactLayout.RingLeft(mascot), line.Left, 3);
        Assert.True(line.Right <= button.Left - CompactLayout.Gap + 0.01f);
        Assert.True(line.Width >= 110 - 0.01f);
    }

    private bool Keep(string line, DateTimeOffset? at = null) =>
        undo.Keep(line, () => ++restored > 0, () => freed++, at ?? T0, TimeSpan.FromSeconds(10));

    private PaletteSession Session(PaletteOptions? options = null, AiExchange? continuing = null)
    {
        var config = new ProviderConfig("p", ProviderKind.OpenAICompatible, "http://localhost", "m");
        var tools = new BuiltInTools(new InMemoryShelf(), new InMemoryScratchpad(), _ => { }, _ => { });
        return new PaletteSession(provider, config, options ?? new PaletteOptions(), new SendLog(Path.Combine(dir, "send-log.jsonl")), tools, new PermissionGrants(), [])
        {
            Prompt = "hi",
            Continuing = continuing,
        };
    }

    private static async Task Until(Func<bool> condition)
    {
        for (int i = 0; i < 400 && !condition(); i++)
        {
            await Task.Delay(5);
        }

        Assert.True(condition());
    }
}
