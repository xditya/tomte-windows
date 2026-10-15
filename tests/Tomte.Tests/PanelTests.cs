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
        Assert.Equal((560, 140), PanelSize.Of(PanelId.Media));
        Assert.Equal((560, 300), PanelSize.Of(PanelId.Shortcuts));
        Assert.Equal((560, 320), PanelSize.Of(PanelId.Scratchpad));
        Assert.Equal((560, 260), PanelSize.Of(PanelId.Shelf));
        Assert.Equal((560, 360), PanelSize.Of(PanelId.Clipboard));
        Assert.Equal((560, 400), PanelSize.Of(PanelId.History));
    }

    [Fact]
    public void TheTabStripIsTheSameOnEveryTab()
    {
        // One width for every panel: a label is where it was on the tab before, so a click goes to the tab it was aimed at.
        PanelId[] all = [PanelId.Shelf, PanelId.Shortcuts, PanelId.Media, PanelId.Scratchpad, PanelId.Clipboard, PanelId.History];
        Assert.All(all, id => Assert.Equal(PanelSize.Width, PanelSize.Of(id).W));
        foreach (bool mascot in new[] { true, false })
        {
            var onShelf = TabStrip.Labels(all, PanelId.Shelf, (float)PanelSize.Width, mascot).Select(l => (l.Text, l.Slot)).ToList();
            Assert.Equal(all.Length, onShelf.Count);
            Assert.All(all, current => Assert.Equal(onShelf, TabStrip.Labels(all, current, (float)PanelSize.Of(current).W, mascot).Select(l => (l.Text, l.Slot))));
        }
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
    public void TabLabelsRunInTabOrderAfterTheTomteAndStayInsideThePanel()
    {
        PanelId[] all = [PanelId.Shelf, PanelId.Shortcuts, PanelId.Media, PanelId.Scratchpad, PanelId.Clipboard, PanelId.History];
        var wide = TabStrip.Labels(all, PanelId.Media, 560, mascot: true);

        Assert.Equal(["Shelf", "Shortcuts", "Media", "Scratchpad", "Clipboard", "History"], wide.Select(label => label.Text));
        // After the tomte (5.5 in, 26 wide, 6 more), each pill its measured text and 10 DIP either side, 2 apart.
        Assert.Equal(37.5f, wide[0].Slot.X);
        for (int i = 0; i < wide.Count; i++)
        {
            float text = Tomte.Core.Text.TextSlot.Measure(wide[i].Text, wide[i].Style, 560);
            Assert.Equal(text + 2 * TabStrip.PadX, wide[i].Slot.Width, 0.01);
            Assert.Equal(i == 2 ? TabStrip.Current : TabStrip.Label, wide[i].Style);
            Assert.Equal(PanelSpace.TabStripHeight / 2, wide[i].Slot.Y + wide[i].Slot.Height / 2, 0.01);
            if (i > 0)
            {
                Assert.Equal(wide[i - 1].Slot.Right + TabStrip.Gap, wide[i].Slot.X, 0.01);
            }
        }

        // A panel cut to a small monitor, here to 380 wide: what does not fit is cut or left out, never past the right padding.
        var narrow = TabStrip.Labels(all, PanelId.Media, 380, mascot: true);
        Assert.InRange(narrow.Count, 3, 6);
        Assert.All(narrow, label => Assert.True(label.Slot.Right <= 380 - TabStrip.RightPad + 0.01, $"{label.Text} ends at {label.Slot.Right}"));
        // With the tomte off the labels start at the left padding; only registered panels get one.
        Assert.Equal(TabStrip.LeftPad, TabStrip.Labels([PanelId.Media], PanelId.Media, 380, mascot: false)[0].Slot.X);
        Assert.Single(TabStrip.Labels([PanelId.Media], PanelId.Media, 380, mascot: true));
    }

    [Fact]
    public void APressOnALabelPicksItsTabAndNothingElseDoes()
    {
        PanelId[] all = [PanelId.Shelf, PanelId.Shortcuts, PanelId.Media, PanelId.Scratchpad, PanelId.Clipboard, PanelId.History];
        var labels = TabStrip.Labels(all, PanelId.Shelf, 560, mascot: true);

        for (int i = 0; i < labels.Count; i++)
        {
            Assert.Equal(all[i], TabStrip.At(labels[i].Slot.X + 1, all, PanelId.Shelf, 560, mascot: true));
            Assert.Equal(all[i], TabStrip.At(labels[i].Slot.Right - 1, all, PanelId.Shelf, 560, mascot: true));
        }

        // The tomte, the gap between two labels and the room after the last one are no tab.
        Assert.Null(TabStrip.At(MascotArt.InsetX + 1, all, PanelId.Shelf, 560, mascot: true));
        Assert.Null(TabStrip.At(labels[0].Slot.Right + TabStrip.Gap / 2, all, PanelId.Shelf, 560, mascot: true));
        Assert.Null(TabStrip.At(labels[^1].Slot.Right + 1, all, PanelId.Shelf, 560, mascot: true));

        // Anywhere along a narrow strip, only a tab that got a label can be pressed.
        var shownTabs = all.Take(TabStrip.Labels(all, PanelId.Media, 380, mascot: true).Count).ToHashSet();
        for (float x = 0; x < 380; x++)
        {
            Assert.True(TabStrip.At(x, all, PanelId.Media, 380, mascot: true) is not { } id || shownTabs.Contains(id), $"x {x}");
        }
    }

    private static readonly float[] PanelWidths = [(float)PanelSize.Media.W, (float)PanelSize.Shortcuts.W, (float)PanelSize.Scratchpad.W, (float)PanelSize.Shelf.W, (float)PanelSize.Clipboard.W];

    [Fact]
    public void TheAskPillEndsAtTheRightPaddingOfEveryPanelAndNoLabelRunsIntoIt()
    {
        PanelId[] all = [PanelId.Shelf, PanelId.Shortcuts, PanelId.Media, PanelId.Scratchpad, PanelId.Clipboard, PanelId.History];
        foreach (float width in PanelWidths)
        {
            var (card, glyph, word) = TabStrip.AskPill(width);
            Assert.Equal(width - TabStrip.RightPad, card.Right, 0.01);
            Assert.Equal(TabStrip.Height, card.Height);
            Assert.Equal(PanelSpace.TabStripHeight / 2, card.Y + card.Height / 2, 0.01);
            // The sparkle, then the word, inside the card with the tab labels' padding either side.
            Assert.Equal("Ask", word.Text);
            Assert.Equal(card.Left + TabStrip.PadX, glyph.Left, 0.01);
            Assert.Equal(glyph.Right + TabStrip.GlyphGap, word.Slot.Left, 0.01);
            Assert.Equal(card.Right - TabStrip.PadX, word.Slot.Right, 0.01);
            Assert.Equal(PanelSpace.TabStripHeight / 2, glyph.Y + glyph.Height / 2, 0.01);

            foreach (bool mascot in new[] { true, false })
            {
                foreach (var current in all)
                {
                    var labels = TabStrip.Labels(all, current, width, mascot);
                    Assert.True(labels.Count >= 3, $"{labels.Count} labels at {width}");
                    Assert.All(labels, label => Assert.True(label.Slot.Right <= card.Left - TabStrip.Gap + 0.01, $"{label.Text} ends at {label.Slot.Right}, Ask starts at {card.Left}"));
                }
            }
        }
    }

    [Fact]
    public void AnAttachedStripStartsAtTheBodyNotAtTheRamps()
    {
        PanelId[] all = [PanelId.Shelf, PanelId.Shortcuts, PanelId.Media, PanelId.Scratchpad, PanelId.Clipboard, PanelId.History];
        var shape = IslandGeometry.Panel(PanelSize.Shelf.W, PanelSize.Shelf.H).Attached();
        Assert.True(shape.RampW > 0);
        float width = (float)shape.Width;

        // The tomte sits at the body's left edge, so the first label, laid out from there too, starts after it.
        var box = MascotArt.Box(shape);
        var first = TabStrip.Labels(all, PanelId.Shelf, width, mascot: true)[0];
        Assert.True(PanelSpace.ToStrip(new(box.Right, PanelSpace.TabStripHeight / 2), shape) <= first.Slot.X);
        Assert.Null(TabStrip.At(PanelSpace.ToStrip(new(box.X + box.Width / 2, PanelSpace.TabStripHeight / 2), shape), all, PanelId.Shelf, width, mascot: true));

        // The Ask pill ends at the body's right padding, not short of it by the ramps.
        float right = width / 2 - TabStrip.RightPad;
        Assert.True(TabStrip.OnAsk(PanelSpace.ToStrip(new(right - 1, PanelSpace.TabStripHeight / 2), shape), width));
        Assert.False(TabStrip.OnAsk(PanelSpace.ToStrip(new(right + 1, PanelSpace.TabStripHeight / 2), shape), width));
    }

    [Fact]
    public void TheAskPillFollowsTheLiveRightEdgeThroughAMorph()
    {
        // Through a morph's live widths up to the panel's, a press just inside the live shape's right padding is on Ask.
        foreach (double live in new[] { 380, 430, 470, 510, PanelSize.Width })
        {
            var shape = IslandGeometry.Panel(live, 200);
            float right = (float)live / 2 - TabStrip.RightPad;
            Assert.True(TabStrip.OnAsk(PanelSpace.ToStrip(new(right - 1, 20), shape), (float)shape.Width), $"at {live}");
            Assert.False(TabStrip.OnAsk(PanelSpace.ToStrip(new(right + 1, 20), shape), (float)shape.Width), $"at {live}");
        }
    }

    [Fact]
    public void APressOnTheAskPillIsAskAndNoTab()
    {
        PanelId[] all = [PanelId.Shelf, PanelId.Shortcuts, PanelId.Media, PanelId.Scratchpad, PanelId.Clipboard, PanelId.History];
        foreach (float width in PanelWidths)
        {
            var card = TabStrip.AskPill(width).Card;
            Assert.True(TabStrip.OnAsk(card.Left, width));
            Assert.True(TabStrip.OnAsk(card.Right - 0.5f, width));
            Assert.False(TabStrip.OnAsk(card.Left - 0.5f, width));
            Assert.False(TabStrip.OnAsk(card.Right, width));
            for (float x = 0; x < width; x++)
            {
                if (TabStrip.OnAsk(x, width))
                {
                    Assert.Null(TabStrip.At(x, all, PanelId.Media, width, mascot: true));
                }
            }
        }
    }
}
