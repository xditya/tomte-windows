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
    // Peek: the tomte alone, neither what plays nor the ring.
    [InlineData(CompactReason.Peek, Other, true, true, None)]
    [InlineData(CompactReason.Peek, Other, false, true, None)]
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

    [Fact]
    public void ThePillSaysWhatItIsUpFor()
    {
        var now = DateTimeOffset.Now;
        var done = new IslandEvent(CompactRules.TimerDone(TimeSpan.FromMinutes(25)), now) { Source = Timer };
        var track = new IslandEvent("Daybreak", now) { Source = Track };
        var dark = new IslandEvent("Dark mode on", now);
        var left = TimeSpan.FromSeconds(83.4);

        Assert.Equal("Push your cursor up here", CompactRules.Line(CompactReason.Hint, [], null));
        Assert.Equal("Timer done · 25:00", CompactRules.Line(CompactReason.Event, [done], null));
        Assert.Equal("Dark mode on", CompactRules.Line(CompactReason.Event, [dark], null));
        Assert.Equal("2 events: Timer done · 25:00, Dark mode on", CompactRules.Line(CompactReason.Event, [done, dark], null));
        // The track-change pill shows the now-playing row with the title; the line would say it twice.
        Assert.Null(CompactRules.Line(CompactReason.Event, [track], null));
        // A digest is the line's even when it holds the track change.
        Assert.Equal("2 events: Daybreak, Dark mode on", CompactRules.Line(CompactReason.Event, [track, dark], null));
        // The push pill counts a running timer down beside its ring, and says nothing without one.
        Assert.Equal("01:24", CompactRules.Line(CompactReason.Summary, [], left));
        Assert.Null(CompactRules.Line(CompactReason.Summary, [], null));
        // Peek, the rambler and no pill have no words of their own, timer or not.
        Assert.Null(CompactRules.Line(CompactReason.Peek, [], left));
        Assert.Null(CompactRules.Line(CompactReason.Rambler, [], left));
        Assert.Null(CompactRules.Line(CompactReason.None, [], left));
    }

    [Theory]
    // Rounded up to the whole second: 00:01 until the very end, 00:00 at it.
    [InlineData(0, "00:00")]
    [InlineData(0.2, "00:01")]
    [InlineData(1, "00:01")]
    [InlineData(59.01, "01:00")]
    [InlineData(1499.5, "25:00")]
    [InlineData(3600.5, "1:00:01")]
    public void TheCountdownReadsWholeSecondsRoundedUp(double seconds, string reads)
    {
        Assert.Equal(reads, CompactRules.Countdown(TimeSpan.FromSeconds(seconds)));
    }

    [Theory]
    [InlineData(1, "Timer done · 00:01")]
    [InlineData(90, "Timer done · 01:30")]
    [InlineData(3600, "Timer done · 1:00:00")]
    public void TheDonePillSaysHowLongTheTimerRan(double seconds, string reads)
    {
        Assert.Equal(reads, CompactRules.TimerDone(TimeSpan.FromSeconds(seconds)));
    }
}
