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
        Assert.Equal((760, HomeLayout.Height), PanelSize.Of(PanelId.Home));
        Assert.Equal((760, 172), PanelSize.Of(PanelId.Media));
        Assert.Equal((760, 310), PanelSize.Of(PanelId.Shortcuts));
        Assert.Equal((760, 330), PanelSize.Of(PanelId.Scratchpad));
        Assert.Equal((760, 270), PanelSize.Of(PanelId.Shelf));
        Assert.Equal((760, 370), PanelSize.Of(PanelId.Clipboard));
        Assert.Equal((760, 410), PanelSize.Of(PanelId.History));
    }

    [Fact]
    public void TheTabRowIsTheSameOnEveryTab()
    {
        // One width for every panel: with the same tab current, every tab and button is where it was on any other panel.
        var all = Enum.GetValues<PanelId>();
        Assert.All(all, id => Assert.Equal(PanelSize.Width, PanelSize.Of(id).W));
        foreach (bool mascot in new[] { true, false })
        {
            foreach (var current in all)
            {
                var here = TabStrip.Layout(all, current, (float)PanelSize.Width, mascot, "Win+Alt+Space", 8);
                Assert.All(all, other => Assert.Equal(here.Tabs, TabStrip.Layout(all, current, (float)PanelSize.Of(other).W, mascot, "Win+Alt+Space", 8).Tabs));
                // The rail starts at the same place whichever tab is current.
                Assert.Equal(here.Rail.X, TabStrip.Layout(all, PanelId.Home, (float)PanelSize.Width, mascot).Rail.X);
            }
        }
    }

    [Fact]
    public void PanelsAreCappedAtSixtyByFiftyPercentOfTheMonitor()
    {
        // 2560 x 1440 at 150% is 1706.7 x 960 DIPs: nothing is capped.
        Assert.Equal((760, 370), PanelSize.Fit(PanelSize.Clipboard, (1706.7, 960)));
        // 1366 x 768 at 125% is 1092.8 x 614.4 DIPs: 760 wide is cut to 655.68, 370 tall to 307.2.
        var fitted = PanelSize.Fit(PanelSize.Clipboard, (1092.8, 614.4));
        Assert.Equal(655.68, fitted.W, 6);
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

        // The body's origin is its top-left corner under the 12 DIP padding and the 40 DIP tab row; the panel is centred on x = 0.
        Assert.Equal(new Vector2(0, 0), PanelSpace.ToBody(new Vector2(-190, 52), 380));
        Assert.Equal(new Vector2(380, 88), PanelSpace.ToBody(new Vector2(190, 140), 380));
    }

    [Theory]
    // The Media panel, 380 x 140: its middle, its edges and its corners are on it.
    [InlineData(0, 70, true)]
    [InlineData(-190, 0, true)]
    [InlineData(190, 140, true)]
    // The shadow band beside the panel, and the band below it.
    [InlineData(200, 70, false)]
    [InlineData(-196, 120, false)]
    [InlineData(0, 150, false)]
    // The row above the pill, over the gap to the screen edge.
    [InlineData(0, -1, false)]
    public void OnlyThePillItselfTakesPressesAndTheWheel(float x, float y, bool expected)
    {
        Assert.Equal(expected, PanelSpace.OnShape(new Vector2(x, y), IslandGeometry.Panel(380, 140)));
    }

    [Theory]
    // A notch away from the user is the previous tab, toward the user the next.
    [InlineData(0, 120, false, -1, 0)]
    [InlineData(0, -120, false, 1, 0)]
    // A touchpad's small deltas add up to a notch.
    [InlineData(60, 40, false, 0, 100)]
    [InlineData(100, 40, false, -1, 0)]
    // Until the new panel starts to show, the rest of the scroll is dropped, carry and all.
    [InlineData(100, 120, true, 0, 0)]
    [InlineData(0, -240, true, 0, 0)]
    public void TheStripStepsOneTabANotchOnceThePanelShows(int carry, int delta, bool settling, int step, int carried)
    {
        Assert.Equal(step, PanelSpace.TabStep(ref carry, delta, settling));
        Assert.Equal(carried, carry);
    }

    [Fact]
    public void TheCurrentTabShowsItsNameAndTheOthersTheirIconAlone()
    {
        var all = Enum.GetValues<PanelId>();
        var row = TabStrip.Layout(all, PanelId.Media, 760, mascot: true, "Win+Alt+Space", 8);

        Assert.Equal(all, row.Tabs.Select(t => t.Id));
        // The tomte (16 in, 28 wide), 12 more, then the rail, 3 DIP round the tabs, 2 between them, 34 high in the 40 row.
        Assert.Equal(new System.Drawing.RectangleF(56, 12, row.Rail.Width, 40), row.Rail);
        Assert.Equal(59, row.Tabs[0].Bounds.X);
        float word = Tomte.Core.Text.TextSlot.Measure("Media", TabStrip.Label, 760);
        for (int i = 0; i < row.Tabs.Count; i++)
        {
            var tab = row.Tabs[i];
            Assert.Equal((15f, 34f), (tab.Bounds.Y, tab.Bounds.Height));
            Assert.Equal(32, tab.Icon.Y + (tab.Icon.Height / 2), 0.01);
            if (i > 0)
            {
                Assert.Equal(row.Tabs[i - 1].Bounds.Right + TabStrip.Gap, tab.Bounds.X, 0.01);
            }

            if (tab.Id == PanelId.Media)
            {
                // 11 before the icon, 18 icon, 6, the name, 14 after.
                Assert.Equal("Media", tab.Label!.Value.Text);
                Assert.Equal(11 + 18 + 6 + word + 14, tab.Bounds.Width, 0.01);
                Assert.Equal(tab.Bounds.X + 11, tab.Icon.X, 0.01);
            }
            else
            {
                Assert.Null(tab.Label);
                Assert.Equal(38, tab.Bounds.Width);
                Assert.Equal(tab.Bounds.X + 10, tab.Icon.X, 0.01);
            }
        }

        Assert.Equal(row.Tabs[^1].Bounds.Right + 3, row.Rail.Right, 0.01);
        // With the tomte off the rail starts at the left padding.
        Assert.Equal(16, TabStrip.Layout(all, PanelId.Media, 760, mascot: false).Rail.X);
    }

    [Theory]
    [InlineData(0, null)]
    [InlineData(8, "8")]
    [InlineData(99, "99")]
    [InlineData(140, "99+")]
    public void TheShelfTabCountsWhatIsOnTheShelf(int items, string? count)
    {
        var row = TabStrip.Layout(Enum.GetValues<PanelId>(), PanelId.Home, 760, mascot: true, shelfCount: items);
        var shelf = row.Tabs.Single(t => t.Id == PanelId.Shelf);
        Assert.Equal(count, shelf.Count);
        Assert.All(row.Tabs.Where(t => t.Id != PanelId.Shelf), t => Assert.Null(t.Badge));
        Assert.Equal(count is null, shelf.Badge is null);
        if (shelf.Badge is { } badge)
        {
            // 15 high, at least as wide, 3 below the tab's top, ending 6 past the icon.
            Assert.Equal(15, badge.Height);
            Assert.True(badge.Width >= 15);
            Assert.Equal(shelf.Bounds.Y + 3, badge.Y);
            Assert.Equal(shelf.Icon.Right + 6, badge.Right, 0.01);
        }
    }

    [Fact]
    public void TheButtonsEndTheRowAndTheSearchButtonCarriesItsShortcutWhenThereIsRoom()
    {
        var all = Enum.GetValues<PanelId>();
        var row = TabStrip.Layout(all, PanelId.Home, 760, mascot: true, "Win+Alt+Space");

        // Settings: 36 round at the right padding, 12 after the search button, both centred on the row.
        Assert.Equal(new System.Drawing.RectangleF(760 - 16 - 36, 14, 36, 36), row.Settings);
        Assert.Equal(row.Settings.X - 12, row.Search.Right, 0.01);
        Assert.Equal((14f, 36f), (row.Search.Y, row.Search.Height));
        Assert.Equal("Win Alt Space", row.KeycapText);
        Assert.Equal("Search", row.SearchLabel!.Value.Text);
        // 12, the magnifier, 8, the word, 8, the cap, 8.
        Assert.Equal(row.Search.X + 12, row.SearchIcon.X, 0.01);
        Assert.Equal(row.SearchIcon.Right + 8, row.SearchLabel!.Value.Slot.X, 0.01);
        Assert.Equal(row.SearchLabel!.Value.Slot.Right + 8, row.Keycap!.Value.X, 0.01);
        Assert.Equal(row.Search.Right - 8, row.Keycap!.Value.Right, 0.01);
        Assert.Equal(21, row.Keycap!.Value.Height);

        // With no shortcut set there is no cap.
        var none = TabStrip.Layout(all, PanelId.Home, 760, mascot: true);
        Assert.Null(none.Keycap);
        Assert.NotNull(none.SearchLabel);

        // Narrower than the rail and the wide button need, the button is round; every tab still shows, all short of it.
        for (float width = 470; width <= 760; width += 2)
        {
            foreach (var current in all)
            {
                var r = TabStrip.Layout(all, current, width, mascot: true, "Win+Alt+Space", 8);
                Assert.Equal(all.Length, r.Tabs.Count);
                Assert.True(r.Rail.Right + 12 <= r.Search.X + 0.01, $"{current} at {width}: rail ends at {r.Rail.Right}, search at {r.Search.X}");
                if (r.SearchLabel is null)
                {
                    Assert.Equal(36, r.Search.Width);
                }
            }
        }

        Assert.Null(TabStrip.Layout(all, PanelId.Scratchpad, 500, mascot: true, "Win+Alt+Space").SearchLabel);
    }

    [Fact]
    public void TheCurrentTabsNameGivesWayOnANarrowPanel()
    {
        var all = Enum.GetValues<PanelId>();
        // Room for the rail with the name cut: the name keeps what is left, never less than the least.
        var cut = TabStrip.Layout(all, PanelId.Scratchpad, 470, mascot: true);
        var tab = cut.Tabs.Single(t => t.Id == PanelId.Scratchpad);
        Assert.True(tab.Label is null || tab.Label.Value.Slot.Width >= TabStrip.MinLabel);
        Assert.True(cut.Rail.Right + 12 <= cut.Search.X + 0.01);
        // No room for a name at all: the current tab is an icon like the rest.
        var bare = TabStrip.Layout(all, PanelId.Scratchpad, 440, mascot: true);
        Assert.Null(bare.Tabs.Single(t => t.Id == PanelId.Scratchpad).Label);
        Assert.All(bare.Tabs, t => Assert.Equal(38, t.Bounds.Width));
    }

    [Theory]
    [InlineData("Win+Alt+Space", "Search (Win+Alt+Space)")]
    [InlineData("alt + win + f", "Search (Win+Alt+F)")]
    [InlineData("", "Search")]
    [InlineData("Win+Alt", "Search")]
    public void TheSearchButtonsTipNamesTheShortcutSetInSettings(string chord, string tip) => Assert.Equal(tip, TabStrip.SearchTip(chord));

    [Theory]
    [InlineData("Win+Alt+Space", "Win Alt Space")]
    [InlineData("ctrl+shift+f", "Ctrl Shift F")]
    [InlineData("", null)]
    public void AKeycapShowsTheWholeChordOnOneCap(string chord, string? words) => Assert.Equal(words, Keycap.Words(chord));

    [Fact]
    public void TheTomteSitsInTheTabRowAtTheLeftPaddingAndALittleLarger()
    {
        var shape = IslandGeometry.Panel(PanelSize.Shelf.W, PanelSize.Shelf.H);
        var seat = TabStrip.Seat;
        var box = MascotArt.Box(shape, seat.Shift, seat.Grow);
        Assert.Equal(-380 + 16, box.X, 0.01);
        Assert.Equal(28, box.Width, 0.01);
        // The art's middle row on the row's middle, 12 + 20 down.
        Assert.Equal(32, box.Y + (MascotArt.MiddleY * box.Height / MascotArt.ViewBox), 0.01);
        // Content after it starts where the rail does.
        Assert.Equal(56, seat.After(TabStrip.AfterMascot), 0.01);
    }

    [Fact]
    public void AnAttachedRowStartsAtTheBodyNotAtTheRamps()
    {
        var all = Enum.GetValues<PanelId>();
        var shape = IslandGeometry.Panel(PanelSize.Shelf.W, PanelSize.Shelf.H).Attached();
        Assert.True(shape.RampW > 0);
        float width = (float)shape.Width;

        // The tomte sits at the body's left edge, so the rail, laid out from there too, starts after it.
        var box = MascotArt.Box(shape, TabStrip.Seat.Shift, TabStrip.Seat.Grow);
        var row = TabStrip.Layout(all, PanelId.Shelf, width, mascot: true);
        Assert.True(PanelSpace.ToStrip(new(box.Right, TabStrip.Middle), shape) <= row.Rail.X + 0.01);
        // The settings button ends at the body's right padding, not short of it by the ramps.
        Assert.Equal(width - TabStrip.Pad, row.Settings.Right, 0.01);
    }
}
