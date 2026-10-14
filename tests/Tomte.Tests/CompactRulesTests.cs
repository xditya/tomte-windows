using System.Globalization;
using Tomte.Core;
using Tomte.Core.Text;
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
    public void WithTheTomteOffPeekIsTheFullPillWithWhatPlaysAndNeverTheRing()
    {
        Assert.Equal(Row, CompactRules.Content(CompactReason.Peek, Other, timerRuns: true, playing: true, tomte: false));
        Assert.Equal(None, CompactRules.Content(CompactReason.Peek, Other, timerRuns: true, playing: false, tomte: false));
        Assert.Equal(Ring, CompactRules.Content(CompactReason.Summary, Other, timerRuns: true, playing: true, tomte: false));
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
        // The push pill counts a running timer down beside its ring; without one it says the time of day unless
        // the now-playing row is up (no clock passed).
        Assert.Equal("01:24", CompactRules.Line(CompactReason.Summary, [], left, now));
        Assert.Null(CompactRules.Line(CompactReason.Summary, [], null));
        Assert.Equal(CompactRules.TimeOfDay(now), CompactRules.Line(CompactReason.Summary, [], null, now));
        Assert.Null(CompactRules.Line(CompactReason.Peek, [], null, now));
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

public class GlanceTests
{
    private static readonly CultureInfo British = CultureInfo.GetCultureInfo("en-GB");
    private static readonly DateTimeOffset Now = new(new DateTime(2026, 10, 14, 18, 41, 0, DateTimeKind.Local));

    // Seven DIPs a character, so a width says how many characters fit; wide enough here for any line.
    private static float Chars(string line) => line.Length * 7f;

    private static string Glance(GlanceFacts facts, float width = 1000) => CompactRules.Glance(Now, facts, width, Chars, British);

    private static readonly GlanceFacts All = new()
    {
        Job = ("Weekly report", Now.AddMinutes(19)),
        Waiting = [new IslandEvent("Summary ready", Now.AddMinutes(-5))],
        Battery = 15,
        ShelfCount = 3,
    };

    [Fact]
    public void TheTimeOfDayComesWithTheFirstThingThatApplies()
    {
        Assert.Equal("18:41 · Weekly report at 19:00", Glance(All));
        Assert.Equal("18:41 · Summary ready", Glance(All with { Job = null }));
        Assert.Equal("18:41 · Battery 15%", Glance(All with { Job = null, Waiting = [] }));
        Assert.Equal("18:41 · 3 on the shelf", Glance(All with { Job = null, Waiting = [], Battery = null }));
        Assert.Equal("18:41 · Wed 14 Oct", Glance(new GlanceFacts()));
    }

    [Fact]
    public void AJobCountsOnlyWithinTheHourAhead()
    {
        Assert.Equal("18:41 · Weekly report at 19:41", Glance(All with { Job = ("Weekly report", Now.AddHours(1)) }));
        Assert.Equal("18:41 · Summary ready", Glance(All with { Job = ("Weekly report", Now.AddMinutes(61)) }));
        Assert.Equal("18:41 · Summary ready", Glance(All with { Job = ("Weekly report", Now) }));
        Assert.Equal("18:41 · Summary ready", Glance(All with { Job = ("Weekly report", Now.AddMinutes(-3)) }));
    }

    [Fact]
    public void SeveralWaitingEventsAreCountedAndTheBatteryOnlyWhenLow()
    {
        var two = new GlanceFacts { Waiting = [new IslandEvent("Timer done · 25:00", Now), new IslandEvent("Summary ready", Now)] };
        Assert.Equal("18:41 · 2 events waiting", Glance(two));
        Assert.Equal("18:41 · Battery 20%", Glance(new GlanceFacts { Battery = 20, ShelfCount = 2 }));
        Assert.Equal("18:41 · 2 on the shelf", Glance(new GlanceFacts { Battery = 21, ShelfCount = 2 }));
        Assert.Equal("18:41 · 1 on the shelf", Glance(new GlanceFacts { ShelfCount = 1 }));
    }

    [Theory]
    [InlineData("en-GB", "Wed 14 Oct")]
    [InlineData("en-US", "Wed Oct 14")]
    [InlineData("de-DE", "Mi 14. Okt.")]
    public void TheDateIsShortInTheCulturesOwnOrder(string culture, string reads)
    {
        Assert.Equal(reads, CompactRules.ShortDate(Now, CultureInfo.GetCultureInfo(culture)));
    }

    [Fact]
    public void ALongLineIsCutAndAJobKeepsItsTime()
    {
        // 24 characters fit.
        Assert.Equal("18:41 · Weekly… at 19:00", Glance(All with { Job = ("Weekly report summary", Now.AddMinutes(19)) }, 24 * 7));
        Assert.Equal("18:41 · Tomte found a d…", Glance(new GlanceFacts { Waiting = [new IslandEvent("Tomte found a damaged database and started a new one.", Now)] }, 24 * 7));
        // A space before the cut goes, so the ellipsis follows the word.
        Assert.Equal("18:41 · Weekly…", Glance(new GlanceFacts { Waiting = [new IslandEvent("Weekly report", Now)] }, 15 * 7));
        // What fits exactly is not cut.
        Assert.Equal("18:41 · Summary ready", Glance(new GlanceFacts { Waiting = [new IslandEvent("Summary ready", Now)] }, 21 * 7));
    }

    [Fact]
    public void WithNoRoomForTheMentionTheTimeStandsAlone()
    {
        Assert.Equal("18:41", Glance(new GlanceFacts { ShelfCount = 3 }, 9 * 7));
        // The job's own time needs room too: no name is better than no time.
        Assert.Equal("18:41", Glance(All, 18 * 7));
        Assert.Equal("18:41 · W… at 19:00", Glance(All, 19 * 7));
    }

    [Fact]
    public void TheLineFitsThePillAsDirectWriteLaysItOut()
    {
        var slot = CompactLayout.Line(mascot: true, ring: false);
        float Measure(string line) => TextSlot.Measure(line, CompactLayout.LineStyle, 4096);
        var facts = All with { Job = ("Send the weekly status report to the whole team", Now.AddMinutes(19)) };
        string line = CompactRules.Glance(Now, facts, slot.Width, Measure, British);
        Assert.EndsWith("… at 19:00", line);
        Assert.StartsWith("18:41 · Send", line);
        Assert.True(Measure(line) <= slot.Width);
        using var drawn = TextLayout.Of(new TextRun(line, CompactLayout.LineStyle, slot.Width));
        Assert.False(drawn.Trimmed);
        // The short ones fit whole.
        Assert.Equal("18:41 · 3 on the shelf", CompactRules.Glance(Now, new GlanceFacts { ShelfCount = 3 }, slot.Width, Measure, British));
    }
}