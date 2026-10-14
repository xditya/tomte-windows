using Tomte.Core.Triggers;
using Xunit;

namespace Tomte.Tests;

public class CompactRulesTests
{
    private const IslandEventSource Other = IslandEventSource.Other, Timer = IslandEventSource.Timer, Track = IslandEventSource.TrackChange;
    private const CompactContent None = CompactContent.None, Ring = CompactContent.TimerRing, Row = CompactContent.NowPlaying;

    [Theory]
    // The push pill: a running timer first, then what plays, then nothing more.
    [InlineData(CompactReason.Summary, Other, true, true, Ring)]
    [InlineData(CompactReason.Summary, Other, true, false, Ring)]
    [InlineData(CompactReason.Summary, Other, false, true, Row)]
    [InlineData(CompactReason.Summary, Other, false, false, None)]
    // Peek: what plays, never the ring.
    [InlineData(CompactReason.Peek, Other, true, true, Row)]
    [InlineData(CompactReason.Peek, Other, true, false, None)]
    [InlineData(CompactReason.Peek, Other, false, true, Row)]
    // An event pill shows only its own event's content.
    [InlineData(CompactReason.Event, Timer, true, true, Ring)]
    [InlineData(CompactReason.Event, Timer, false, false, Ring)]
    [InlineData(CompactReason.Event, Track, true, true, Row)]
    [InlineData(CompactReason.Event, Track, false, false, None)]
    [InlineData(CompactReason.Event, Other, true, true, None)]
    // The first-run hint, the rambler and no pill at all show neither.
    [InlineData(CompactReason.Hint, Other, true, true, None)]
    [InlineData(CompactReason.Rambler, Other, true, true, None)]
    [InlineData(CompactReason.None, Timer, true, true, None)]
    public void OneLineShowsOneThing(CompactReason reason, IslandEventSource shown, bool timerRuns, bool playing, CompactContent expected)
    {
        Assert.Equal(expected, CompactRules.Content(reason, shown, timerRuns, playing));
    }

    [Fact]
    public void AnEventPillBelongsToTheFeatureThatRaisedItsOneEvent()
    {
        var now = DateTimeOffset.Now;
        var done = new IslandEvent("Timer done", now) { Source = Timer };
        var track = new IslandEvent("Daybreak", now) { Source = Track };
        var job = new IslandEvent("Report ready", now);

        Assert.Equal(Timer, CompactRules.Owner([done]));
        Assert.Equal(Track, CompactRules.Owner([track]));
        Assert.Equal(Other, CompactRules.Owner([job]));
        Assert.Equal(Other, CompactRules.Owner([]));
        // A digest shows only its own content: no ring and no row, whichever events it holds.
        Assert.Equal(Other, CompactRules.Owner([job, track, done]));
        Assert.Equal(Other, CompactRules.Owner([job, track]));
        Assert.Equal(Other, CompactRules.Owner([done, track]));
        // The text alone does not make an event the timer's.
        Assert.Equal(Other, CompactRules.Owner([new IslandEvent("Timer done", now)]));
    }
}
