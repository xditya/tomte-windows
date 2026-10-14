using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Media;
using Xunit;

namespace Tomte.Tests;

public class MediaTests
{
    private const MediaCommand All = MediaCommand.Play | MediaCommand.Pause | MediaCommand.Toggle | MediaCommand.Next | MediaCommand.Previous;
    private static readonly DateTimeOffset T0 = new(2026, 10, 14, 9, 0, 0, TimeSpan.Zero);

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    // A 195 s track reported at 48 s as of T0, as the design shows it.
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

    [Theory]
    [MemberData(nameof(Scales))]
    public void PressOnATransportButtonSendsItsCommandAtEveryScale(double scale)
    {
        var playing = Playing();
        (RectangleF Button, MediaCommand Command)[] buttons =
        [
            (MediaPanelLayout.Previous, MediaCommand.Previous),
            (MediaPanelLayout.PlayPause, MediaCommand.Pause),
            (MediaPanelLayout.Next, MediaCommand.Next),
        ];

        foreach (var (button, command) in buttons)
        {
            var (cx, cy) = Pixel(button.X + button.Width / 2, button.Y + button.Height / 2, scale);
            Assert.Equal(command, MediaPanelLayout.Command(BodyPoint(cx, cy, scale), playing));

            // Two pixels inside each corner still hit; two pixels outside each edge miss.
            var (left, top) = Pixel(button.Left, button.Top, scale);
            var (right, bottom) = Pixel(button.Right, button.Bottom, scale);
            Assert.Equal(command, MediaPanelLayout.Command(BodyPoint(left + 2, top + 2, scale), playing));
            Assert.Equal(command, MediaPanelLayout.Command(BodyPoint(right - 2, bottom - 2, scale), playing));
            Assert.Equal(MediaCommand.None, MediaPanelLayout.Command(BodyPoint(left - 2, cy, scale), playing));
            Assert.Equal(MediaCommand.None, MediaPanelLayout.Command(BodyPoint(right + 2, cy, scale), playing));
            Assert.Equal(MediaCommand.None, MediaPanelLayout.Command(BodyPoint(cx, top - 2, scale), playing));
            Assert.Equal(MediaCommand.None, MediaPanelLayout.Command(BodyPoint(cx, bottom + 2, scale), playing));
        }
    }

    [Theory]
    [MemberData(nameof(Scales))]
    public void PressOutsideTheButtonsOrOnARefusedCommandSendsNothing(double scale)
    {
        var (artX, artY) = Pixel(MediaPanelLayout.Art.X + 30, MediaPanelLayout.Art.Y + 30, scale);
        Assert.Equal(MediaCommand.None, MediaPanelLayout.Command(BodyPoint(artX, artY, scale), Playing()));

        var (barX, barY) = Pixel(MediaPanelLayout.Bar.X + 40, MediaPanelLayout.Bar.Y + 2, scale);
        Assert.Equal(MediaCommand.None, MediaPanelLayout.Command(BodyPoint(barX, barY, scale), Playing()));

        // The app takes only pause: next and previous are dead, play or pause still works.
        var pauseOnly = Playing().WithPlayback(true, 1, MediaCommand.Pause, T0 + S(1));
        var (nextX, nextY) = Pixel(MediaPanelLayout.Next.X + 14, MediaPanelLayout.Next.Y + 14, scale);
        var (playX, playY) = Pixel(MediaPanelLayout.PlayPause.X + 14, MediaPanelLayout.PlayPause.Y + 14, scale);
        Assert.Equal(MediaCommand.None, MediaPanelLayout.Command(BodyPoint(nextX, nextY, scale), pauseOnly));
        Assert.Equal(MediaCommand.Pause, MediaPanelLayout.Command(BodyPoint(playX, playY, scale), pauseOnly));

        // Nothing plays: every button is dead.
        Assert.Equal(MediaCommand.None, MediaPanelLayout.Command(BodyPoint(playX, playY, scale), NowPlaying.None));
    }

    [Fact]
    public void MediaBodyLayoutFitsUnderTheTabStripAndClearOfTheCorners()
    {
        float width = (float)PanelSize.Media.W, height = (float)PanelSize.Media.H - PanelSpace.TabStripHeight;
        RectangleF[] all =
        [
            MediaPanelLayout.Art, MediaPanelLayout.Title, MediaPanelLayout.Artist, MediaPanelLayout.Elapsed, MediaPanelLayout.Bar,
            MediaPanelLayout.Total, MediaPanelLayout.Previous, MediaPanelLayout.PlayPause, MediaPanelLayout.Next,
        ];

        Assert.All(all, r => Assert.True(r.Left >= 0 && r.Top >= 0 && r.Right <= width && r.Bottom <= height, r.ToString()));
        // The three buttons do not overlap, so a press has one answer.
        Assert.False(MediaPanelLayout.Previous.IntersectsWith(MediaPanelLayout.PlayPause));
        Assert.False(MediaPanelLayout.PlayPause.IntersectsWith(MediaPanelLayout.Next));
    }
}
