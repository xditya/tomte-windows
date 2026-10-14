using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;
using Xunit;

namespace Tomte.Tests;

public class CaptureTests
{
    private static Lasso Draw(Lasso lasso, params (int X, int Y)[] points)
    {
        lasso.Down(points[0].X, points[0].Y);
        foreach (var (x, y) in points.Skip(1))
            lasso.Move(x, y);
        return lasso;
    }

    [Fact]
    public void LassoBoxIsInclusiveInPixelsAndDividedByTheScaleInDips()
    {
        var lasso = Draw(new Lasso(2560, 1440, 1.5), (100, 200), (400, 150), (430, 380), (90, 360));

        Assert.Equal(new PixelRect(90, 150, 341, 231), lasso.Box);
        Assert.Equal((341 / 1.5, 231 / 1.5), lasso.BoxDips);
        Assert.Equal(new PixelRect(90, 150, 341, 231), lasso.Up());
        Assert.False(lasso.Drawing);
    }

    [Fact]
    public void SameMovementIsTheSameBoxInPixelsButNotInDipsOnMixedScales()
    {
        // A 100% primary and a 150% secondary: client coordinates are each monitor's own pixels, so only the DIPs differ.
        var primary = Draw(new Lasso(2560, 1440, 1.0), (10, 10), (70, 10), (70, 40), (10, 40));
        var secondary = Draw(new Lasso(2880, 1620, 1.5), (10, 10), (70, 10), (70, 40), (10, 40));

        Assert.Equal(primary.Box, secondary.Box);
        Assert.Equal((61.0, 31.0), primary.BoxDips);
        Assert.Equal((61 / 1.5, 31 / 1.5), secondary.BoxDips);
        Assert.NotNull(primary.Up());
        Assert.NotNull(secondary.Up());
    }

    [Fact]
    public void PointsDraggedPastTheMonitorEdgeClampToIt()
    {
        // The mouse is captured, so a drag onto the next monitor reports coordinates outside this one.
        var lasso = Draw(new Lasso(1920, 1080, 1.25), (1800, 1000), (2500, 1300), (-40, -7));

        Assert.Equal(new PixelRect(0, 0, 1920, 1080), lasso.Box);
        Assert.All(lasso.Points, p => Assert.InRange(p.X, 0, 1919));
    }

    [Fact]
    public void TooSmallInDipsIsClearedForAnotherTry()
    {
        // 25 px is 20.0 DIPs at 125% but 16.7 at 150%.
        var ok = Draw(new Lasso(1920, 1080, 1.25), (0, 0), (24, 24));
        var slip = Draw(new Lasso(1920, 1080, 1.5), (0, 0), (24, 24));

        Assert.NotNull(ok.Up());
        Assert.Null(slip.Up());
        Assert.Empty(slip.Points);
        Assert.False(slip.Move(50, 50));
    }

    [Fact]
    public void UpWithoutDownIsNothing()
    {
        Assert.Null(new Lasso(100, 100, 1).Up());
    }

    [Fact]
    public void CropWithinMaxWidthIsNotScaled()
    {
        Assert.Equal((2560, 1440, new PixelRect(90, 150, 341, 231)), WindowCapture.Fit(2560, 1440, new PixelRect(90, 150, 341, 231), 1280));
    }

    [Fact]
    public void WideCropScalesTheFrameAndTheCropTogether()
    {
        var (sw, sh, b) = WindowCapture.Fit(3840, 2160, new PixelRect(100, 50, 2560, 1000), 1280);

        Assert.Equal((1920, 1080), (sw, sh));
        Assert.Equal(new PixelRect(50, 25, 1280, 500), b);
    }

    [Fact]
    public void CropIsClampedToTheFrame()
    {
        Assert.Equal(new PixelRect(1900, 1000, 20, 80), WindowCapture.Fit(1920, 1080, new PixelRect(1900, 1000, 500, 500), 1280).Bounds);
        // The window capture path: the whole frame, scaled to the max width.
        Assert.Equal((1280, 720, new PixelRect(0, 0, 1280, 720)), WindowCapture.Fit(2560, 1440, new PixelRect(0, 0, 2560, 1440), 1280));
    }

    [Fact]
    public async Task LassoChipStartsOnAndAttachesThePng()
    {
        byte[] png = [1, 2, 3];
        var chip = ContextChips.Lasso(png, 227.3, 154);

        Assert.True(chip.On);
        Assert.Equal(("Region", "The region you circled, 227 × 154, sent as an image"), (chip.Label, chip.Tip));
        var (parts, attached) = await ContextAssembly.BuildAsync([chip], "what is this?", CancellationToken.None);
        Assert.Same(png, Assert.IsType<ImagePart>(parts[0]).Data);
        Assert.Equal(["lasso"], attached);
    }

    [Fact]
    public async Task TheHotkeyPaletteOpensOnTheLassoAndSendsItWithTheQuestionTheUserTypes()
    {
        byte[] png = [1, 2, 3];
        var shelf = new InMemoryShelf();
        shelf.CreateFile("notes.txt", "x");
        var chips = ContextChips.For(ContextChips.Lasso(png, 312, 184), null, "msedge", "Store - Edge", _ => Task.FromResult(Array.Empty<byte>()), shelf, new InMemoryScratchpad());

        // The region first and on; the palette's other chips after it, off, and no selection is read for a lasso.
        Assert.Equal(("Region", ContextKind.Region), (chips[0].Label, chips[0].Kind));
        Assert.Equal([true, false, false, false, false, false], chips.Select(c => c.On));
        Assert.False(chips[1].Available);

        // The question is whatever the user typed, sent after the region.
        var (parts, attached) = await ContextAssembly.BuildAsync(chips, "Which game is this?", CancellationToken.None);
        Assert.Same(png, Assert.IsType<ImagePart>(parts[0]).Data);
        Assert.Equal("Which game is this?", Assert.IsType<TextPart>(parts[^1]).Text);
        Assert.Equal(["lasso"], attached);
    }

    [Fact]
    public void CaptureIsEnteredOnlyFromHiddenAndLeftToPaletteOrHidden()
    {
        // Esc: Hidden -> Capture -> Hidden. Lasso done: Hidden -> Capture -> Palette.
        var cancel = new IslandStateMachine();
        cancel.TransitionTo(IslandState.Capture);
        cancel.TransitionTo(IslandState.Hidden);
        var done = new IslandStateMachine();
        done.TransitionTo(IslandState.Capture);
        done.TransitionTo(IslandState.Palette);

        Assert.Equal(IslandState.Hidden, cancel.State);
        Assert.Equal(IslandState.Palette, done.State);
        foreach (var other in new[] { IslandState.Compact, IslandState.Panels, IslandState.ShelfDrop, IslandState.Palette, IslandState.Capture })
            Assert.False(IslandStateMachine.CanTransition(other, IslandState.Capture));
        foreach (var other in new[] { IslandState.Compact, IslandState.Panels, IslandState.ShelfDrop })
            Assert.False(IslandStateMachine.CanTransition(IslandState.Capture, other));
    }
}
