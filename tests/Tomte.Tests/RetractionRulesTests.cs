using Tomte.Core;
using Tomte.Core.Triggers;
using Xunit;

namespace Tomte.Tests;

// The hold on the island ends when the user opens something else, so the rambler's end cannot retract it.
public class TakenOverTests
{
    [Fact]
    public void AStateTheUserOpensEndsPeekAndTheRamblersHold()
    {
        var all = IslandActivity.Peek | IslandActivity.Rambling | IslandActivity.Pinned | IslandActivity.DragInProgress;
        Assert.Equal(IslandActivity.Pinned | IslandActivity.DragInProgress, RetractionRules.TakenOver(all));
        Assert.Equal(IslandActivity.None, RetractionRules.TakenOver(IslandActivity.Rambling));
        // A drop zone's result gives way to whatever the user opens next.
        Assert.Equal(IslandActivity.None, RetractionRules.TakenOver(IslandActivity.Result));
    }
}

public class RetractionRulesTests
{
    [Theory]
    [InlineData(IslandState.Compact, IslandActivity.None, true)]
    [InlineData(IslandState.Panels, IslandActivity.None, true)]
    [InlineData(IslandState.ShelfDrop, IslandActivity.None, true)]
    [InlineData(IslandState.Palette, IslandActivity.None, true)]
    [InlineData(IslandState.Hidden, IslandActivity.None, false)]
    [InlineData(IslandState.Capture, IslandActivity.None, false)]
    [InlineData(IslandState.ShelfDrop, IslandActivity.DragInProgress, false)]
    [InlineData(IslandState.Palette, IslandActivity.Typing, false)]
    [InlineData(IslandState.Palette, IslandActivity.AiTask, false)]
    [InlineData(IslandState.Panels, IslandActivity.Pinned, false)]
    [InlineData(IslandState.Compact, IslandActivity.Peek, false)]
    [InlineData(IslandState.Compact, IslandActivity.Rambling, false)]
    [InlineData(IslandState.Palette, IslandActivity.Result, false)]
    public void RetractsOnlyWhenNothingIsInProgress(IslandState state, IslandActivity activity, bool expected)
    {
        Assert.Equal(expected, RetractionRules.ShouldRetract(state, activity));
    }

    [Theory]
    [InlineData(IslandState.Compact, IslandActivity.None, true)]
    [InlineData(IslandState.Compact, IslandActivity.Peek, true)]
    [InlineData(IslandState.Compact, IslandActivity.Rambling, false)]
    [InlineData(IslandState.Compact, IslandActivity.DragInProgress, false)]
    [InlineData(IslandState.Panels, IslandActivity.None, false)]
    [InlineData(IslandState.Hidden, IslandActivity.None, false)]
    [InlineData(IslandState.Palette, IslandActivity.None, false)]
    public void HoverOrScrollOnCompactOpensPanels(IslandState state, IslandActivity activity, bool expected)
    {
        Assert.Equal(expected, RetractionRules.OpensPanels(state, activity));
    }

    [Theory]
    [InlineData(IslandState.Panels, IslandActivity.None, false, true)]
    [InlineData(IslandState.Palette, IslandActivity.AiTask, false, true)]
    [InlineData(IslandState.Palette, IslandActivity.Result, true, true)]
    [InlineData(IslandState.Palette, IslandActivity.Result, false, false)]
    [InlineData(IslandState.Compact, IslandActivity.None, true, false)]
    [InlineData(IslandState.Hidden, IslandActivity.None, false, false)]
    public void EscIsTakenWhileAResultIsUpOnlyWithThePointerOnIt(IslandState state, IslandActivity activity, bool pointerInside, bool expected)
    {
        Assert.Equal(expected, RetractionRules.TakesEscape(state, activity, pointerInside));
    }

    [Theory]
    [InlineData(IslandState.Palette, IslandActivity.Result, true)]
    [InlineData(IslandState.Palette, IslandActivity.Result | IslandActivity.DragInProgress, true)]
    [InlineData(IslandState.Palette, IslandActivity.AiTask, false)]
    [InlineData(IslandState.Palette, IslandActivity.None, false)]
    [InlineData(IslandState.Panels, IslandActivity.None, false)]
    [InlineData(IslandState.Compact, IslandActivity.Peek, false)]
    public void APressOutsideDismissesOnlyAFinishedZoneResult(IslandState state, IslandActivity activity, bool expected)
    {
        Assert.Equal(expected, RetractionRules.DismissedByClickOutside(state, activity));
    }

    // An event pill or the hint is held past its time while the pointer is on it, or while a drag moves it and the
    // pointer tracking still has the bounds from before the drag.
    [Theory]
    [InlineData(true, IslandActivity.None, true)]
    [InlineData(false, IslandActivity.DragInProgress, true)]
    [InlineData(false, IslandActivity.Peek, false)]
    [InlineData(false, IslandActivity.None, false)]
    public void APillIsHeldPastItsTimeByThePointerOrADrag(bool pointerInside, IslandActivity activity, bool held) =>
        Assert.Equal(held, RetractionRules.HoldsPastItsTime(pointerInside, activity));

    [Fact]
    public void InsideIncludesTheMargin()
    {
        // A 240 x 40 px island centred at x = 1280, with the 24 px margin at 100%.
        const int l = 1160, t = 0, r = 1400, b = 40, m = 24;
        Assert.True(RetractionRules.Inside(1280, 20, l, t, r, b, m));
        Assert.True(RetractionRules.Inside(l - m, b + m, l, t, r, b, m));
        Assert.False(RetractionRules.Inside(l - m - 1, 20, l, t, r, b, m));
        Assert.False(RetractionRules.Inside(1280, b + m + 1, l, t, r, b, m));
    }

    [Fact]
    public void RevealZoneIsTheMiddleThirtyPercentScaledByDpi()
    {
        var settings = new RevealSettings();
        var zone = RevealZone.For(0, 0, 2560, 1280, settings.ZoneFraction, settings.DragApproachDip, 1.5);

        Assert.Equal(896, zone.Left);
        Assert.Equal(1664, zone.Right);
        Assert.Equal(72, zone.DragBandBottom);
        Assert.True(zone.ContainsX(1280));
        Assert.False(zone.ContainsX(895));
        Assert.True(zone.InDragBand(1280, 72));
        Assert.False(zone.InDragBand(1280, 73));
        Assert.False(zone.InDragBand(800, 10));
    }

    [Fact]
    public void TheRevealZoneFollowsTheIslandAndStaysOnTheMonitor()
    {
        // 30% of 2560 is 768 wide, centred under an island moved to x = 600, and moved in at either side.
        Assert.Equal((216, 984), Span(RevealZone.For(0, 0, 2560, 600, 0.30, 48, 1.5)));
        Assert.Equal((0, 768), Span(RevealZone.For(0, 0, 2560, 100, 0.30, 48, 1.5)));
        Assert.Equal((1792, 2560), Span(RevealZone.For(0, 0, 2560, 2500, 0.30, 48, 1.5)));
        // A hand-edited fraction over 1 is the whole edge.
        Assert.Equal((0, 2560), Span(RevealZone.For(0, 0, 2560, 600, 1.5, 48, 1.5)));

        static (int, int) Span(RevealZone zone) => (zone.Left, zone.Right);
    }
}
