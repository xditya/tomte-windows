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
    [InlineData(IslandState.Panels, IslandActivity.Pressing, false)]
    public void RetractsOnlyWhenNothingIsInProgress(IslandState state, IslandActivity activity, bool expected)
    {
        Assert.Equal(expected, RetractionRules.ShouldRetract(state, activity));
    }

    // Ctrl+P: pinned, Panels and the AI box stay when the pointer leaves and when the keys go elsewhere, also once the
    // typing or the press that held them has ended; unpinned, both retract again.
    [Fact]
    public void APinnedIslandStaysWhenThePointerLeavesOrTheKeysGo()
    {
        foreach (var state in new[] { IslandState.Panels, IslandState.Palette })
        {
            Assert.False(RetractionRules.ShouldRetract(state, IslandActivity.Pinned));
            Assert.False(RetractionRules.ShouldRetract(state, IslandActivity.Pinned & ~IslandActivity.Typing));
            Assert.False(RetractionRules.ShouldRetract(state, RetractionRules.Released(IslandActivity.Pinned | IslandActivity.Pressing, buttonDown: false)));
            Assert.True(RetractionRules.ShouldRetract(state, IslandActivity.Pinned ^ IslandActivity.Pinned));
        }

        Assert.False(RetractionRules.RetractsOnFocusLoss(IslandActivity.Pinned));
        Assert.False(RetractionRules.RetractsOnFocusLoss(IslandActivity.Pinned | IslandActivity.Typing));
        Assert.True(RetractionRules.RetractsOnFocusLoss(IslandActivity.None));
        // Opening the AI box from a pinned panel keeps the pin.
        Assert.True(RetractionRules.TakenOver(IslandActivity.Pinned).HasFlag(IslandActivity.Pinned));
    }

    [Fact]
    public void APressLetGoOutsideTheIslandHoldsNothingWhenTheDelayIsUp()
    {
        var held = IslandActivity.Pressing | IslandActivity.Typing;

        Assert.Equal(held, RetractionRules.Released(held, buttonDown: true));
        Assert.Equal(IslandActivity.Typing, RetractionRules.Released(held, buttonDown: false));
        Assert.True(RetractionRules.ShouldRetract(IslandState.Panels, RetractionRules.Released(IslandActivity.Pressing, buttonDown: false)));
        Assert.False(RetractionRules.ShouldRetract(IslandState.Panels, RetractionRules.Released(IslandActivity.Pressing, buttonDown: true)));
        // A drag coming in is not a press: the button being up does not drop it here.
        Assert.Equal(IslandActivity.DragInProgress, RetractionRules.Released(IslandActivity.DragInProgress, buttonDown: false));
    }

    [Fact]
    public void ADragOutOfTheIslandLetsItRetractOnceThePointerHasLeft()
    {
        // The press on the tile that started the drag holds nothing once the drag runs, with the button still down.
        Assert.True(RetractionRules.ShouldRetract(IslandState.Panels, RetractionRules.DraggingOut(IslandActivity.Pressing)));
        Assert.Equal(IslandActivity.Pressing, RetractionRules.Released(IslandActivity.Pressing, buttonDown: true));
        // A paid drop zone result, or typing, still holds the island through a drag out of its tiles.
        Assert.False(RetractionRules.ShouldRetract(IslandState.Palette, RetractionRules.DraggingOut(IslandActivity.Pressing | IslandActivity.Result)));
        Assert.False(RetractionRules.ShouldRetract(IslandState.Panels, RetractionRules.DraggingOut(IslandActivity.Pressing | IslandActivity.Typing)));
    }

    [Theory]
    [InlineData(IslandState.Compact, IslandActivity.None, true)]
    [InlineData(IslandState.Compact, IslandActivity.Peek, true)]
    [InlineData(IslandState.Compact, IslandActivity.Rambling, false)]
    [InlineData(IslandState.Compact, IslandActivity.DragInProgress, false)]
    [InlineData(IslandState.Compact, IslandActivity.Pressing, false)]
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
    // A pinned island stays up after the user has gone back to their app: Esc there is theirs.
    [InlineData(IslandState.Panels, IslandActivity.Pinned, true, true)]
    [InlineData(IslandState.Panels, IslandActivity.Pinned, false, false)]
    [InlineData(IslandState.Palette, IslandActivity.Pinned, false, false)]
    // With the keys, Esc reaches the island as a key, never as a hotkey.
    [InlineData(IslandState.Panels, IslandActivity.Typing, true, false)]
    [InlineData(IslandState.Palette, IslandActivity.Typing | IslandActivity.Pinned, true, false)]
    public void EscIsTakenWhileAResultIsUpOnlyWithThePointerOnIt(IslandState state, IslandActivity activity, bool pointerInside, bool expected)
    {
        Assert.Equal(expected, RetractionRules.TakesEscape(state, activity, pointerInside));
    }

    // One level at a time: the question first, then the open conversation, then back to the tab, then away.
    [Theory]
    [InlineData(true, false, true, EscapeStep.Cancel)]
    [InlineData(true, false, false, EscapeStep.Cancel)]
    [InlineData(false, true, false, EscapeStep.Close)]
    [InlineData(false, false, true, EscapeStep.Back)]
    [InlineData(false, false, false, EscapeStep.Retract)]
    public void EscStepsBackOneLevelThenRetracts(bool asking, bool conversationOpen, bool fromPanels, EscapeStep expected) =>
        Assert.Equal(expected, RetractionRules.Escape(asking, conversationOpen, fromPanels));

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

// The hover dwell that opens Panels from the pill, driven as the controller drives its one-shot: Start arms it, Cancel
// disarms it, and it opens the panels when it runs out before the next report.
public class HoverDwellTests
{
    [Theory]
    [InlineData(false, true, HoverStep.Start)]
    [InlineData(true, false, HoverStep.Cancel)]
    [InlineData(true, true, HoverStep.None)]
    [InlineData(false, false, HoverStep.None)]
    public void ComingOnStartsItAndLeavingCancelsIt(bool wasOn, bool on, HoverStep expected)
    {
        Assert.Equal(expected, RetractionRules.Hover(wasOn, on));
    }

    [Fact]
    public void RestingOnThePillOpensThePanelsOnceTheDwellHasRun()
    {
        Assert.Equal(1000, OpensAt(1000, (0, true)));
        // Moves about the pill, and the same place reported again for new bounds, do not start it over.
        Assert.Equal(1000, OpensAt(1000, (0, true), (200, true), (600, true), (950, true)));
        // A short dwell set in settings opens sooner, from the same rest.
        Assert.Equal(300, OpensAt(300, (0, true), (100, true)));
    }

    [Fact]
    public void LeavingThePillCancelsItAndComingBackStartsOver()
    {
        Assert.Null(OpensAt(1000, (0, true), (900, false)));
        Assert.Equal(2200, OpensAt(1000, (0, true), (900, false), (1200, true), (1500, true)));
        // The margin around the pill holds the island open but is not on the pill, so resting there never opens it.
        Assert.Null(OpensAt(1000, (0, false), (500, false), (3000, false)));
    }

    private static double? OpensAt(int dwellMs, params (double At, bool OnPill)[] reports)
    {
        bool on = false;
        double? due = null;
        foreach (var (at, onPill) in reports)
        {
            if (due <= at)
            {
                return due;
            }

            switch (RetractionRules.Hover(on, onPill))
            {
                case HoverStep.Start:
                    due = at + dwellMs;
                    break;
                case HoverStep.Cancel:
                    due = null;
                    break;
            }

            on = onPill;
        }

        return due;
    }
}