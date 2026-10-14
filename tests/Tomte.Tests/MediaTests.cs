using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Media;
using Tomte.Core.Text;
using Xunit;

namespace Tomte.Tests;

public class MediaTests
{
    private static readonly MediaPanelLayout Layout = new((float)PanelSize.Media.W);

    private const MediaCommand All = MediaCommand.Play | MediaCommand.Pause | MediaCommand.Toggle | MediaCommand.Next | MediaCommand.Previous;
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    // A 195 s track reported at 48 s as of T0.
    private static NowPlaying Playing(double rate = 1) =>
        NowPlaying.For("Spotify.exe")
            .WithPlayback(true, rate, All, T0)
            .WithTimeline(S(0), S(195), S(48), T0, T0)
            .WithTrack("Nightfall", "Lo-fi radio");

    [Fact]
    public void PositionRunsOnBetweenReportsWhilePlaying()
    {
        var playing = Playing();

        Assert.Equal(S(48), playing.PositionAt(T0));
        Assert.Equal(S(58), playing.PositionAt(T0 + S(10)));
        Assert.Equal(S(195), playing.Duration);
        Assert.Equal(58.0 / 195, playing.Timeline!.Value.ProgressAt(T0 + S(10)), 6);
        Assert.Equal(S(137), playing.Timeline.Value.RemainingAt(T0 + S(10)));
    }

    [Fact]
    public void PauseHoldsThePositionAndResumeCarriesOnFromIt()
    {
        var paused = Playing().WithPlayback(false, 1, All, T0 + S(10));

        Assert.Equal(S(58), paused.PositionAt(T0 + S(10)));
        Assert.Equal(S(58), paused.PositionAt(T0 + S(300)));
        Assert.Null(paused.Timeline!.Value.RemainingAt(T0 + S(300)));

        var resumed = paused.WithPlayback(true, 1, All, T0 + S(30));
        Assert.Equal(S(58), resumed.PositionAt(T0 + S(30)));
        Assert.Equal(S(63), resumed.PositionAt(T0 + S(35)));
    }

    [Fact]
    public void SeekReplacesWhatWasExtrapolated()
    {
        var sought = Playing().WithTimeline(S(0), S(195), S(120), T0 + S(40), T0 + S(40));

        Assert.Equal(S(120), sought.PositionAt(T0 + S(40)));
        Assert.Equal(S(125), sought.PositionAt(T0 + S(45)));
    }

    [Fact]
    public void RateChangeKeepsThePositionAndChangesTheSpeed()
    {
        var doubled = Playing().WithPlayback(true, 2, All, T0 + S(10));

        Assert.Equal(S(58), doubled.PositionAt(T0 + S(10)));
        Assert.Equal(S(68), doubled.PositionAt(T0 + S(15)));
        // 127 s of track left at double speed is 63.5 s of wall time.
        Assert.Equal(S(63.5), doubled.Timeline!.Value.RemainingAt(T0 + S(15)));

        var halved = Playing(rate: 0.5);
        Assert.Equal(S(53), halved.PositionAt(T0 + S(10)));
    }

    [Fact]
    public void ReportStampedBeforeItArrivesIsExtrapolatedFromItsStamp()
    {
        // The app stamps the position 3 s before the event reaches us.
        var late = NowPlaying.For("Chrome").WithPlayback(true, 1, All, T0).WithTimeline(S(0), S(195), S(48), T0 - S(3), T0);

        Assert.Equal(S(51), late.PositionAt(T0));
    }

    [Fact]
    public void ReportStampedAheadOfTheClockNeverRunsBackwards()
    {
        var ahead = NowPlaying.For("Chrome").WithPlayback(true, 1, All, T0).WithTimeline(S(0), S(195), S(48), T0 + S(2), T0);

        Assert.Equal(S(48), ahead.PositionAt(T0));
        Assert.Equal(S(48), ahead.PositionAt(T0 + S(1)));
        Assert.Equal(S(49), ahead.PositionAt(T0 + S(3)));
    }

    [Fact]
    public void UnstampedReportCountsFromWhenItArrived()
    {
        var unstamped = NowPlaying.For("old.exe").WithPlayback(true, 1, All, T0).WithTimeline(S(0), S(195), S(48), DateTimeOffset.FromFileTime(0), T0);

        Assert.Equal(S(50), unstamped.PositionAt(T0 + S(2)));
    }

    [Fact]
    public void PositionStaysInsideTheTrack()
    {
        var playing = Playing();

        Assert.Equal(S(195), playing.PositionAt(T0 + S(10_000)));
        Assert.Equal(1, playing.Timeline!.Value.ProgressAt(T0 + S(10_000)));
        Assert.Equal(TimeSpan.Zero, playing.Timeline.Value.RemainingAt(T0 + S(10_000)));

        var rewinding = Playing(rate: -4);
        Assert.Equal(S(0), rewinding.PositionAt(T0 + S(60)));
        Assert.Null(rewinding.Timeline!.Value.RemainingAt(T0));
    }

    [Fact]
    public void AppThatReportsNoTimelineHasNone()
    {
        var live = NowPlaying.For("radio.exe").WithPlayback(true, 1, MediaCommand.Pause, T0).WithTimeline(S(0), S(0), S(0), T0, T0);

        Assert.Null(live.Timeline);
        Assert.Null(live.Duration);
        Assert.Null(live.PositionAt(T0 + S(5)));
        // A timeline that goes away (a track, then a stream) is dropped too.
        Assert.Null(Playing().WithTimeline(S(0), S(0), S(0), T0, T0).Timeline);
    }

    [Fact]
    public void NewTrackDropsTheOldArtAndTheSameTrackKeepsIt()
    {
        var art = new MediaThumbnail(2, 2, new byte[16], 7);
        var withArt = Playing() with { Thumbnail = art };

        Assert.Same(withArt, withArt.WithTrack("Nightfall", "Lo-fi radio"));
        Assert.Null(withArt.WithTrack("Daybreak", "Lo-fi radio").Thumbnail);
        Assert.Equal("Daybreak", withArt.WithTrack("Daybreak", "Lo-fi radio").Title);
    }

    [Fact]
    public void ReportsThatChangeNothingReturnTheSameState()
    {
        var playing = Playing();

        Assert.Same(playing, playing.WithPlayback(true, 1, All, T0 + S(20)));
        Assert.Same(playing, playing.WithTimeline(S(0), S(195), S(48), T0, T0 + S(20)));
        Assert.NotSame(playing, playing.WithPlayback(true, 1, MediaCommand.Pause, T0 + S(20)));
    }

    [Fact]
    public void NoSessionReadsAsNothingPlaying()
    {
        Assert.False(NowPlaying.None.HasSession);
        Assert.False(NowPlaying.None.IsPlaying);
        Assert.Equal("", NowPlaying.None.Title);
        Assert.Equal(MediaCommand.None, NowPlaying.None.PlayPause);
        Assert.Equal("Nothing playing", NowPlaying.NothingPlaying);

        var fresh = NowPlaying.For("Spotify.exe");
        Assert.True(fresh.HasSession);
        Assert.Equal("Spotify", fresh.App);
    }

    [Fact]
    public void TrackChangeIsAnotherTitleThatPlays()
    {
        var first = Playing();
        var second = first.WithTrack("Daybreak", "Lo-fi radio");

        Assert.True(second.IsNewTrackAfter(first));
        Assert.True(second.IsNewTrackAfter(null));
        // The same track resumed, a paused track and a track with no title yet are not changes.
        Assert.False(first.WithPlayback(false, 1, All, T0).WithPlayback(true, 1, All, T0).IsNewTrackAfter(first));
        Assert.False(second.WithPlayback(false, 1, All, T0).IsNewTrackAfter(first));
        Assert.False(NowPlaying.For("Chrome").WithPlayback(true, 1, All, T0).IsNewTrackAfter(first));
        // The same title from another app is another track.
        Assert.True(NowPlaying.For("Chrome").WithPlayback(true, 1, All, T0).WithTrack("Nightfall", "Lo-fi radio").IsNewTrackAfter(first));
    }

    [Theory]
    [InlineData(true, MediaCommand.Play | MediaCommand.Pause | MediaCommand.Toggle, MediaCommand.Pause)]
    [InlineData(false, MediaCommand.Play | MediaCommand.Pause | MediaCommand.Toggle, MediaCommand.Play)]
    [InlineData(true, MediaCommand.Toggle, MediaCommand.Toggle)]
    [InlineData(false, MediaCommand.Pause | MediaCommand.Toggle, MediaCommand.Toggle)]
    [InlineData(true, MediaCommand.Play, MediaCommand.None)]
    [InlineData(false, MediaCommand.None, MediaCommand.None)]
    public void PlayPauseSendsWhatTheAppAccepts(bool playing, MediaCommand accepted, MediaCommand expected)
    {
        Assert.Equal(expected, NowPlaying.For("a").WithPlayback(playing, 1, accepted, T0).PlayPause);
    }

    [Theory]
    [InlineData(0, "00:00")]
    [InlineData(48, "00:48")]
    [InlineData(195, "03:15")]
    [InlineData(3599, "59:59")]
    [InlineData(3795, "1:03:15")]
    [InlineData(-5, "00:00")]
    public void ClockText(double seconds, string expected)
    {
        Assert.Equal(expected, NowPlaying.Clock(S(seconds)));
    }

    [Fact]
    public void WatchersStartAndStopListeningPositionsAndArtEachOnce()
    {
        var watchers = new MediaWatchers();
        const MediaWatchers.Flip None = MediaWatchers.Flip.None, Listening = MediaWatchers.Flip.Listening,
            Timeline = MediaWatchers.Flip.Timeline, Art = MediaWatchers.Flip.Art;

        // The track-change event: titles only, while the island is hidden.
        Assert.Equal(Listening, watchers.Add(wantsTimeline: false, wantsArt: false));
        Assert.True(watchers.Listening);
        Assert.False(watchers.Timeline || watchers.Art);

        // The Compact row: art, no positions. Then the Media panel: both.
        Assert.Equal(Art, watchers.Add(false, true));
        Assert.Equal(Timeline, watchers.Add(true, true));
        Assert.True(watchers.Timeline && watchers.Art);

        // The row goes first: the panel still wants art. The panel goes: positions and art stop, listening goes on.
        Assert.Equal(None, watchers.Remove(false, true));
        Assert.Equal(Timeline | Art, watchers.Remove(true, true));
        Assert.True(watchers.Listening);

        // The event is turned off: nothing listens.
        Assert.Equal(Listening, watchers.Remove(false, false));
        Assert.False(watchers.Listening);

        // From nothing, the panel alone starts all three.
        Assert.Equal(Listening | Timeline | Art, watchers.Add(true, true));
        Assert.Equal(Listening | Timeline | Art, watchers.Remove(true, true));
    }

    [Fact]
    public void TakingBackAWatchThatWasNeverMadeIsAnError()
    {
        var watchers = new MediaWatchers();
        Assert.Throws<InvalidOperationException>(() => watchers.Remove(false, false));

        watchers.Add(false, true);
        Assert.Throws<InvalidOperationException>(() => watchers.Remove(true, true));
        Assert.True(watchers.Listening && watchers.Art);
    }

    [Theory]
    // The system's current session wins while it plays.
    [InlineData(new[] { true, true }, 1, 1)]
    [InlineData(new[] { true, false }, 0, 0)]
    // It is paused and another app plays: the one that plays.
    [InlineData(new[] { false, true }, 0, 1)]
    [InlineData(new[] { false, false, true }, 0, 2)]
    // Nothing plays: the current session stays.
    [InlineData(new[] { false, false }, 1, 1)]
    // No current session reported: the first that plays, else the first.
    [InlineData(new[] { false, true }, -1, 1)]
    [InlineData(new[] { false, false }, -1, 0)]
    [InlineData(new[] { true }, 5, 0)]
    [InlineData(new bool[0], -1, -1)]
    [InlineData(new bool[0], 0, -1)]
    public void SessionPickedWhenSeveralAppsReportMedia(bool[] playing, int current, int expected)
    {
        Assert.Equal(expected, MediaSessions.Pick(playing, current));
    }

    [Theory]
    [InlineData("Spotify.exe", "Spotify")]
    [InlineData("SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify", "Spotify")]
    [InlineData(@"C:\Program Files\VideoLAN\VLC\vlc.exe", "vlc")]
    [InlineData("Chrome", "Chrome")]
    [InlineData("308046B0AF4A39CB", "308046B0AF4A39CB")]
    public void AppNameFromTheSessionId(string id, string expected)
    {
        Assert.Equal(expected, MediaSessions.AppName(id));
    }

    // A press arrives in window pixels. The island's DIP space starts one pixel in (the window's margin)
    // plus half the outline's width, and carries the monitor's scale; the Media body starts under the tab strip.
    private static Vector2 BodyPoint(int x, int y, double scale)
    {
        var size = PanelSize.Media;
        float originX = 1 + (float)(IslandGeometry.Panel(size.W, size.H).OuterWidth / 2 * scale);
        return PanelSpace.ToBody(PanelSpace.FromPixels(x, y, originX, 1, scale), size.W);
    }

    private static (int X, int Y) Pixel(float bodyX, float bodyY, double scale)
    {
        var size = PanelSize.Media;
        double originX = 1 + IslandGeometry.Panel(size.W, size.H).OuterWidth / 2 * scale;
        return ((int)Math.Round(originX + (bodyX - size.W / 2) * scale), (int)Math.Round(1 + (bodyY + PanelSpace.TabStripHeight) * scale));
    }

    public static TheoryData<double> Scales => [1.0, 1.25, 1.5];

    // The playing track, taking a new position too.
    private static NowPlaying Seekable() => Playing().WithPlayback(true, 1, All | MediaCommand.Seek, T0);

    private static string? HitId(IReadOnlyList<Target> targets, Vector2 at) =>
        Target.Hit([(targets, Same)], at) is { } hit ? targets[hit.Index].Id : null;

    private static readonly Func<Vector2, Vector2> Same = p => p;

    [Theory]
    [MemberData(nameof(Scales))]
    public void PressOnATransportButtonOrTheSeekBarHitsItAtEveryScale(double scale)
    {
        var targets = Layout.Targets(Seekable());
        (RectangleF Button, string Id)[] buttons =
        [
            (Layout.Seek, "seek"),
            (Layout.Previous, "previous"),
            (Layout.PlayPause, "playpause"),
            (Layout.Next, "next"),
        ];

        foreach (var (button, id) in buttons)
        {
            var (cx, cy) = Pixel(button.X + button.Width / 2, button.Y + button.Height / 2, scale);
            Assert.Equal(id, HitId(targets, BodyPoint(cx, cy, scale)));

            // Two pixels inside each corner still hit; two pixels outside each edge do not.
            var (left, top) = Pixel(button.Left, button.Top, scale);
            var (right, bottom) = Pixel(button.Right, button.Bottom, scale);
            Assert.Equal(id, HitId(targets, BodyPoint(left + 2, top + 2, scale)));
            Assert.Equal(id, HitId(targets, BodyPoint(right - 2, bottom - 2, scale)));
            Assert.NotEqual(id, HitId(targets, BodyPoint(left - 2, cy, scale)));
            Assert.NotEqual(id, HitId(targets, BodyPoint(right + 2, cy, scale)));
            Assert.NotEqual(id, HitId(targets, BodyPoint(cx, top - 2, scale)));
            Assert.NotEqual(id, HitId(targets, BodyPoint(cx, bottom + 2, scale)));
        }

        Assert.Equal(MediaCommand.Previous, MediaPanelLayout.Command("previous", Playing()));
        Assert.Equal(MediaCommand.Pause, MediaPanelLayout.Command("playpause", Playing()));
        Assert.Equal(MediaCommand.Next, MediaPanelLayout.Command("next", Playing()));
        Assert.Equal(MediaCommand.None, MediaPanelLayout.Command("seek", Playing()));
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public void ARefusedCommandIsADisabledButtonAndTheArtIsNoTarget(double scale)
    {
        var (artX, artY) = Pixel(MediaPanelLayout.Art.X + 30, MediaPanelLayout.Art.Y + 30, scale);
        Assert.Null(HitId(Layout.Targets(Playing()), BodyPoint(artX, artY, scale)));

        var transport = Layout.Targets(Playing());
        Assert.Equal(["previous", "playpause", "next"], transport.Select(t => t.Id));
        Assert.All(transport, t => Assert.True(t.Enabled && t.Group == 1 && t.Bounds.Width >= 32 && t.Bounds.Height >= 32, t.Id));
        Assert.Equal(["Previous", "Pause", "Next"], transport.Select(t => t.Tip));

        // The app takes only pause: next and previous are disabled, play or pause still works.
        var pauseOnly = Playing().WithPlayback(true, 1, MediaCommand.Pause, T0 + S(1));
        Assert.Equal([false, true, false], Layout.Targets(pauseOnly).Select(t => t.Enabled));
        Assert.Equal(MediaCommand.None, MediaPanelLayout.Command("next", pauseOnly));
        Assert.Equal(MediaCommand.Pause, MediaPanelLayout.Command("playpause", pauseOnly));

        // Paused, the middle button says Play. Nothing plays: every button is disabled.
        Assert.Equal("Play", Layout.Targets(Playing().WithPlayback(false, 1, All, T0))[1].Tip);
        Assert.All(Layout.Targets(NowPlaying.None), t => Assert.False(t.Enabled, t.Id));
    }

    [Fact]
    public void TheBarIsASeekBarOnlyWhileTheAppTakesAPosition()
    {
        var seek = Layout.Targets(Seekable())[0];
        Assert.Equal(("seek", TargetKind.Slider, true, 0), (seek.Id, seek.Kind, seek.Draggable, seek.Group));
        Assert.True(seek.Bounds.Height >= StateLook.MinTarget);
        Assert.True(seek.Bounds.Contains(Layout.Bar));
        // The knob at either end is inside it.
        Assert.True(seek.Bounds.Left <= Layout.Bar.Left - MediaPanelLayout.Knob);
        Assert.True(seek.Bounds.Right >= Layout.Bar.Right + MediaPanelLayout.Knob);

        // No position taken, or no timeline to put one on: a plain progress bar, and no target.
        Assert.DoesNotContain(Layout.Targets(Playing()), t => t.Id == "seek");
        var noTimeline = Seekable().WithTimeline(S(0), S(0), S(0), T0, T0);
        Assert.Null(noTimeline.Timeline);
        Assert.DoesNotContain(Layout.Targets(noTimeline), t => t.Id == "seek");
    }

    [Fact]
    public void ASeekHoldsTheBarUntilTheAppReportsOrTwoSecondsPass()
    {
        var sent = Seekable().Timeline!.Value;
        var seek = new PendingSeek(S(120), sent, T0);
        Assert.True(seek.Holds(sent, T0 + S(1.9)));
        // No report within 2 s: the bar goes back to what the app last said.
        Assert.False(seek.Holds(sent, T0 + S(2)));
        // The app reported the new position, or stopped reporting a timeline: the bar shows that at once.
        var reported = Seekable().WithTimeline(S(0), S(195), S(120), T0 + S(0.3), T0 + S(0.3)).Timeline;
        Assert.False(seek.Holds(reported, T0 + S(0.3)));
        Assert.False(seek.Holds(null, T0 + S(0.3)));
    }

    [Fact]
    public void APointOnTheBarIsAPositionAndKeysStepFiveSeconds()
    {
        var bar = Layout.Bar;
        var timeline = new MediaTimeline(S(10), S(190), S(48), T0, 1, true);
        Assert.Equal(S(10), Layout.SeekAt(bar.Left, timeline));
        Assert.Equal(S(190), Layout.SeekAt(bar.Right, timeline));
        Assert.Equal(S(100), Layout.SeekAt(bar.Left + bar.Width / 2, timeline));
        // Past the ends, where the knob can still be grabbed, the position stays on the track.
        Assert.Equal(S(10), Layout.SeekAt(bar.Left - MediaPanelLayout.Knob, timeline));
        Assert.Equal(S(190), Layout.SeekAt(bar.Right + MediaPanelLayout.Knob, timeline));
        Assert.Equal(bar.Left + bar.Width / 4, Layout.BarX(S(55), timeline), 0.01);
        Assert.Equal(bar.Right, Layout.BarX(S(500), timeline));

        Assert.Equal(S(53), MediaPanelLayout.Stepped(S(48), 1, timeline));
        Assert.Equal(S(43), MediaPanelLayout.Stepped(S(48), -1, timeline));
        Assert.Equal(S(10), MediaPanelLayout.Stepped(S(12), -1, timeline));
        Assert.Equal(S(190), MediaPanelLayout.Stepped(S(188), 1, timeline));

        // The time's card follows the pointer along the bar and stops at its ends, above the bar.
        var card = Layout.SeekLabel(bar.Left + 40);
        Assert.Equal(bar.Left + 40, card.X + card.Width / 2);
        Assert.Equal(Layout.SeekLabel(bar.Right), Layout.SeekLabel(bar.Right + 30));
        Assert.True(card.Bottom < bar.Top);
        using var widest = TextLayout.Of(new TextRun(NowPlaying.Clock(S(35999)), MediaPanelLayout.SeekStyle, card.Width));
        Assert.False(widest.Trimmed);
    }

    [Theory]
    [InlineData(560)]
    // 60% of a 1280 x 800 screen at 150%, and of an 800 x 600 one at 100%.
    [InlineData(512)]
    [InlineData(480)]
    public void MediaBodyLayoutFitsUnderTheTabStripAndClearOfTheCorners(float width)
    {
        var layout = new MediaPanelLayout(width);
        float height = (float)PanelSize.Media.H - PanelSpace.TabStripHeight;
        RectangleF[] all =
        [
            MediaPanelLayout.Art, layout.Title, layout.Artist, MediaPanelLayout.Elapsed, layout.Bar,
            layout.Total, layout.Seek, layout.Previous, layout.PlayPause, layout.Next,
            layout.SeekLabel(layout.Bar.Left), layout.SeekLabel(layout.Bar.Right),
        ];

        Assert.All(all, r => Assert.True(r.Left >= 0 && r.Top >= 0 && r.Right <= width - 14 && r.Bottom <= height, r.ToString()));
        // The end clock keeps the right padding, the bar takes what the clocks leave, and the transport sits under its middle.
        Assert.Equal(width - 14, layout.Total.Right);
        Assert.Equal(layout.Total.Left - 2, layout.Bar.Right);
        Assert.Equal(MediaPanelLayout.Elapsed.Right + 2, layout.Bar.Left);
        Assert.Equal(layout.Bar.X + layout.Bar.Width / 2, layout.PlayPause.X + layout.PlayPause.Width / 2);
        Assert.Equal(layout.Bar.Right, layout.BarX(S(190), new MediaTimeline(S(10), S(190), S(48), T0, 1, true)));
        // No two targets overlap, so a press has one answer; the disc is drawn inside its button.
        RectangleF[] targets = [layout.Seek, layout.Previous, layout.PlayPause, layout.Next];
        for (int i = 0; i < targets.Length; i++)
        {
            for (int j = i + 1; j < targets.Length; j++)
            {
                Assert.False(targets[i].IntersectsWith(targets[j]), $"{targets[i]} and {targets[j]}");
            }
        }

        Assert.True(MediaPanelLayout.Disc <= layout.PlayPause.Width);
    }

    [Fact]
    public void TheBarIs360AtTheFullWidthAndShortensWithAPanelTheMonitorCaps()
    {
        Assert.Equal(new RectangleF(140, 48, 360, 4), Layout.Bar);
        Assert.Equal(new RectangleF(502, 43, 44, 14), Layout.Total);
        var narrow = new MediaPanelLayout(512);
        Assert.Equal(new RectangleF(140, 48, 312, 4), narrow.Bar);
        Assert.Equal(new RectangleF(454, 43, 44, 14), narrow.Total);
        Assert.Equal(404, narrow.Title.Width);
    }
}
