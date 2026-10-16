using Tomte.Core;
using Tomte.Core.Triggers;
using Xunit;

namespace Tomte.Tests;

// "Keep showing while music plays": the pill stays while something plays and goes five seconds after it stops.
public class KeptPillTests
{
    private static readonly SuppressionState Clear = SuppressionState.Clear;
    private static readonly TimeSpan Second = TimeSpan.FromSeconds(1);

    private static bool Keeps(bool on = true, bool kept = false, TimeSpan? stoppedFor = null, bool paused = false, SuppressionState? desktop = null, params AppRule[] rules) =>
        RetractionRules.KeepsPill(on, kept, stoppedFor, paused, desktop ?? Clear, rules);

    [Fact]
    public void ItIsOffUntilTurnedOnAndOffChangesNothing()
    {
        Assert.False(new TomteSettings().KeepWhilePlaying);
        Assert.False(Keeps(on: false));
        Assert.False(Keeps(on: false, kept: true));
    }

    [Fact]
    public void ThePillComesWhileSomethingPlaysAndStaysWhileItDoes()
    {
        Assert.True(Keeps());
        Assert.True(Keeps(kept: true));
        // The pointer leaving does not retract it.
        Assert.False(RetractionRules.ShouldRetract(IslandState.Compact, IslandActivity.Playing));
    }

    [Fact]
    public void ItGoesFiveSecondsAfterPlaybackStopsAndAStoppedSessionNeverBringsItBack()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), RetractionRules.KeptAfterPlayback);
        Assert.True(Keeps(kept: true, stoppedFor: TimeSpan.Zero));
        Assert.True(Keeps(kept: true, stoppedFor: TimeSpan.FromMilliseconds(4999)));
        Assert.False(Keeps(kept: true, stoppedFor: 5 * Second));
        // Panels closing a second after the music stopped does not bring the pill back for its last four seconds.
        Assert.False(Keeps(kept: false, stoppedFor: Second));
    }

    [Fact]
    public void ItHidesWhilePausedOverFullscreenOnTheLockScreenAndUnderADenyRule()
    {
        Assert.False(Keeps(kept: true, paused: true));
        Assert.False(Keeps(kept: true, desktop: Clear with { Fullscreen = true }));
        Assert.False(Keeps(kept: true, desktop: Clear with { Fullscreen = true, ExclusiveFullscreen = true }));
        Assert.False(Keeps(kept: true, desktop: Clear with { Secure = true }));
        var player = Clear with { ForegroundExe = @"C:\Games\player.exe" };
        Assert.False(Keeps(kept: true, desktop: player, rules: new AppRule("player.exe", AppRuleMode.Deny)));
        Assert.False(Keeps(kept: true, desktop: player, rules: new AppRule("player", AppRuleMode.DenyWithHotkeys)));
        // An allow rule lets an edge push through over its fullscreen app, not the kept pill.
        Assert.False(Keeps(kept: true, desktop: player with { Fullscreen = true }, rules: new AppRule("player", AppRuleMode.Allow)));
        // Focus and Do Not Disturb hold events, not this pill, and a rule for another app changes nothing.
        Assert.True(Keeps(kept: true, desktop: player with { Quiet = true }, rules: new AppRule("game", AppRuleMode.Deny)));
    }

    [Fact]
    public void ItIsAmbientLikePeekSoEventsAndWhatTheUserOpensTakeItOver()
    {
        Assert.False(TriggerQueue.IslandBusy(IslandState.Compact, IslandActivity.Playing));
        Assert.Equal(IslandActivity.None, RetractionRules.TakenOver(IslandActivity.Playing));
        Assert.True(RetractionRules.OpensPanels(IslandState.Compact, IslandActivity.Playing));
    }

    [Fact]
    public void NoTwoHoldsShareAFlagSoTheKeptPillNeverReadsAsTheUndoPill()
    {
        int[] flags = [.. Enum.GetValues<IslandActivity>().Where(f => f != IslandActivity.None).Select(f => (int)f)];
        Assert.All(flags, f => Assert.Equal(1, System.Numerics.BitOperations.PopCount((uint)f)));
        Assert.Equal(flags.Length, flags.Distinct().Count());
        Assert.Equal(IslandActivity.None, IslandActivity.Playing & IslandActivity.Undo);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void ItShowsWhatPlaysWithNoWordsAlsoOnceStoppedOrWithATimer(bool timerRuns, bool playing)
    {
        Assert.Equal(CompactContent.NowPlaying, CompactRules.Content(CompactReason.Playing, IslandEventSource.Other, timerRuns, playing));
        Assert.Equal(CompactContent.NowPlaying, CompactRules.Content(CompactReason.Playing, IslandEventSource.Other, timerRuns, playing, tomte: false));
        Assert.Null(CompactRules.Line(CompactReason.Playing, [], timerRuns ? Second : null, DateTimeOffset.Now));
    }

    [Fact]
    public void TheTomteRestsWithoutItsSwaySoNothingMovesWhileNothingChanges()
    {
        double now = 0, dt = 1.0 / 165;
        var tomte = new Mascot(() => now, () => false);
        double Run(double seconds)
        {
            double max = 0;
            for (double t = 0; t < seconds; t += dt)
            {
                now += dt;
                tomte.Step(dt);
                max = Math.Max(max, Math.Abs(tomte.RootRotate));
            }

            return max;
        }

        tomte.Follow(IslandState.Hidden, IslandState.Compact);
        Run(1);
        Assert.NotNull(tomte.WakeIn);
        // The sway already scheduled when the pill is kept does not start, and none follows it.
        tomte.Still = true;
        Assert.Equal(0, Run(9));
        Assert.Null(tomte.WakeIn);
        Assert.False(tomte.Step(dt));
        // Whatever takes the pill over sways again.
        tomte.Still = false;
        Assert.InRange(tomte.WakeIn ?? -1, 3, 7);
    }

    [Fact]
    public void AClickOnTheNowPlayingRowOpensMediaAndAnyOtherClickTheTabLastOpen()
    {
        Assert.Equal(PanelId.Media, CompactRules.ClickOpens(nowPlayingShows: true));
        Assert.Null(CompactRules.ClickOpens(nowPlayingShows: false));
    }
}
