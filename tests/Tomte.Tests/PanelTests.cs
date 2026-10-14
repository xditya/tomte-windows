using System.Numerics;
using Tomte.Core;
using Xunit;

namespace Tomte.Tests;

public class PanelTests
{
    [Fact]
    public void NothingRegisteredMeansNoCurrentPanel()
    {
        var tabs = new PanelTabs();

        Assert.Null(tabs.Current);
        Assert.Empty(tabs.Order);
        Assert.False(tabs.Select(PanelId.Media));
        Assert.False(tabs.Step(1));
    }

    [Fact]
    public void TabsSitInStripOrderWhateverOrderTheyRegisterIn()
    {
        var tabs = new PanelTabs();
        tabs.Register(PanelId.Media);
        Assert.Equal(PanelId.Media, tabs.Current);

        tabs.Register(PanelId.Clipboard);
        tabs.Register(PanelId.Shelf);

        Assert.Equal([PanelId.Shelf, PanelId.Media, PanelId.Clipboard], tabs.Order);
        // Nothing was picked yet, so the first tab is the one Panels opens on.
        Assert.Equal(PanelId.Shelf, tabs.Current);
    }

    [Fact]
    public void RegisteringAPanelTwiceIsAnError()
    {
        var tabs = new PanelTabs();
        tabs.Register(PanelId.Media);

        Assert.Throws<InvalidOperationException>(() => tabs.Register(PanelId.Media));
    }

    [Fact]
    public void SelectSticksAndSaysWhetherAnythingChanged()
    {
        var tabs = new PanelTabs();
        tabs.Register(PanelId.Shelf);
        tabs.Register(PanelId.Media);

        Assert.False(tabs.Select(PanelId.Shelf));
        Assert.True(tabs.Select(PanelId.Media));
        Assert.False(tabs.Select(PanelId.Media));
        Assert.False(tabs.Select(PanelId.Scratchpad));
        Assert.Equal(PanelId.Media, tabs.Current);

        // A panel that registers later does not take the tab away: the last-used one stays.
        tabs.Register(PanelId.Shortcuts);
        Assert.Equal(PanelId.Media, tabs.Current);
    }

    [Fact]
    public void StepWalksTheTabsAndWrapsAtBothEnds()
    {
        var tabs = new PanelTabs();
        tabs.Register(PanelId.Shelf);
        tabs.Register(PanelId.Media);
        tabs.Register(PanelId.Clipboard);

        Assert.True(tabs.Step(1));
        Assert.Equal(PanelId.Media, tabs.Current);
        Assert.True(tabs.Step(1));
        Assert.Equal(PanelId.Clipboard, tabs.Current);
        Assert.True(tabs.Step(1));
        Assert.Equal(PanelId.Shelf, tabs.Current);
        Assert.True(tabs.Step(-1));
        Assert.Equal(PanelId.Clipboard, tabs.Current);
        Assert.False(tabs.Step(0));
    }

    [Fact]
    public void OnePanelHasNoTabToSwitchTo()
    {
        var tabs = new PanelTabs();
        tabs.Register(PanelId.Media);

        Assert.False(tabs.Step(1));
        Assert.False(tabs.Step(-1));
        Assert.Equal(PanelId.Media, tabs.Current);
    }

    [Fact]
    public void EveryPanelHasItsSize()
    {
        Assert.Equal((380, 140), PanelSize.Of(PanelId.Media));
        Assert.Equal((520, 300), PanelSize.Of(PanelId.Shortcuts));
        Assert.Equal((520, 320), PanelSize.Of(PanelId.Scratchpad));
        Assert.Equal((560, 260), PanelSize.Of(PanelId.Shelf));
        Assert.Equal((560, 360), PanelSize.Of(PanelId.Clipboard));
        Assert.All(Enum.GetValues<PanelId>(), id => Assert.True(PanelSize.Of(id).W > 0));
    }

    [Fact]
    public void PanelsAreCappedAtSixtyByFiftyPercentOfTheMonitor()
    {
        // 2560 x 1440 at 150% is 1706.7 x 960 DIPs: nothing is capped.
        Assert.Equal((560, 360), PanelSize.Fit(PanelSize.Clipboard, (1706.7, 960)));
        // 1366 x 768 at 125% is 1092.8 x 614.4 DIPs: 560 wide fits, 360 tall is cut to 307.2.
        var fitted = PanelSize.Fit(PanelSize.Clipboard, (1092.8, 614.4));
        Assert.Equal(560, fitted.W);
        Assert.Equal(307.2, fitted.H, 6);
        // 800 x 600 at 100%: 480 wide at most.
        Assert.Equal(480, PanelSize.Fit(PanelSize.Shelf, (800, 600)).W, 6);
    }

    [Fact]
    public void TabSwitchIsPanelsReenteringItselfAndEveryTransitionIsHeard()
    {
        var machine = new IslandStateMachine();
        var heard = new List<(IslandState From, IslandState To)>();
        machine.Changed += (from, to) => heard.Add((from, to));

        machine.TransitionTo(IslandState.Compact);
        machine.TransitionTo(IslandState.Panels);
        machine.TransitionTo(IslandState.Panels);
        machine.TransitionTo(IslandState.Hidden);
        Assert.Throws<InvalidOperationException>(() => machine.TransitionTo(IslandState.Hidden));

        Assert.Equal(
            [
                (IslandState.Hidden, IslandState.Compact),
                (IslandState.Compact, IslandState.Panels),
                (IslandState.Panels, IslandState.Panels),
                (IslandState.Panels, IslandState.Hidden),
            ],
            heard);
        // A panel to panel morph is the gentle one.
        Assert.Equal(SpringPreset.Gentle, Spring.PresetFor(IslandState.Panels, IslandState.Panels));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void WindowPixelsMapToIslandAndBodySpace(double scale)
    {
        // Island space starts at the window's 1 px margin plus half the outline (428 DIPs for the Media panel).
        float originX = 1 + (float)(214 * scale), originY = 1;

        Assert.Equal(Vector2.Zero, PanelSpace.FromPixels((int)originX, 1, (int)originX, originY, scale));
        var right = PanelSpace.FromPixels((int)Math.Round(originX + 100 * scale), (int)Math.Round(originY + 50 * scale), originX, originY, scale);
        Assert.InRange(right.X, 99.5f, 100.5f);
        Assert.InRange(right.Y, 49.5f, 50.5f);

        // The body's origin is its top-left corner under the 42 DIP tab strip; the panel is centred on x = 0.
        Assert.Equal(new Vector2(0, 0), PanelSpace.ToBody(new Vector2(-190, 42), 380));
        Assert.Equal(new Vector2(380, 98), PanelSpace.ToBody(new Vector2(190, 140), 380));
    }
}
