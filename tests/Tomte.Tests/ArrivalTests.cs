using Tomte.Core;
using Tomte.Core.Triggers;
using Xunit;

namespace Tomte.Tests;

public class ArrivalTests
{
    private static readonly IReadOnlyList<AppRule> NoRules = [];

    [Fact]
    public void PlaysOnlyWhereAnEventCouldShow()
    {
        Assert.True(Arrival.Plays(animations: true, tomte: true, SuppressionState.Clear, NoRules));
        Assert.False(Arrival.Plays(animations: false, tomte: true, SuppressionState.Clear, NoRules));
        Assert.False(Arrival.Plays(animations: true, tomte: false, SuppressionState.Clear, NoRules));

        // The lock screen, a fullscreen or presenting app, exclusive fullscreen, quiet hours or Focus.
        foreach (var desktop in new[]
        {
            new SuppressionState { Secure = true },
            new SuppressionState { Fullscreen = true },
            new SuppressionState { Fullscreen = true, ExclusiveFullscreen = true },
            new SuppressionState { Quiet = true },
        })
        {
            Assert.False(Arrival.Plays(animations: true, tomte: true, desktop, NoRules), desktop.ToString());
        }

        // An app the user keeps the island away from.
        var deny = new SuppressionState { ForegroundExe = "slides.exe" };
        Assert.False(Arrival.Plays(animations: true, tomte: true, deny, [new AppRule("slides.exe", AppRuleMode.Deny)]));
    }

    [Fact]
    public void ItsPillShowsTheTomteAlone()
    {
        // Nothing to read, load or watch at launch: no line, no timer ring, no now-playing row.
        Assert.Null(CompactRules.Line(CompactReason.Arrival, [], TimeSpan.FromMinutes(5), DateTimeOffset.Now));
        Assert.Equal(CompactContent.None, CompactRules.Content(CompactReason.Arrival, IslandEventSource.Other, timerRuns: true, playing: true));
    }

    // A normal start lands in the peek pill, which holds the tomte alone: centred across it, its art's middle row on the
    // pill's middle, a pixel either way.
    [Theory]
    [InlineData(1.0, 1920)]
    [InlineData(1.25, 1920)]
    [InlineData(1.5, 2560)]
    public void LandsCentredInThePeekPill(double scale, int screenWidth)
    {
        int center = screenWidth / 2;
        var pill = IslandGeometry.Peek;
        var at = Arrival.Landing(pill, scale, center, attached: false);

        Assert.InRange(at.X + (MascotArt.SizeDips * scale / 2), center - 1, center + 1);
        double pillLeft = center - (pill.Width * scale / 2);
        Assert.InRange(at.X - pillLeft, (MascotArt.InsetX * scale) - 1, (MascotArt.InsetX * scale) + 1);
        double middle = at.Y + (MascotArt.MiddleY / MascotArt.ViewBox * MascotArt.SizeDips * scale);
        double pillMiddle = IslandOutline.GapPixels(scale) + (pill.Depth * scale / 2);
        Assert.InRange(middle, pillMiddle - 1, pillMiddle + 1);
    }

    // A first start lands in the Compact pill that says the hint, at its left edge.
    [Theory]
    [InlineData(1.0, 1920)]
    [InlineData(1.25, 1920)]
    [InlineData(1.5, 2560)]
    public void LandsInsideThePillAtItsLeftEdge(double scale, int screenWidth)
    {
        int center = screenWidth / 2;
        var pill = IslandGeometry.Compact;
        var at = Arrival.Landing(pill, scale, center, attached: false);

        // The tomte's inset from the pill's left edge, and its art's middle row on the pill's middle, a pixel either way.
        double pillLeft = center - pill.Width * scale / 2;
        Assert.InRange(at.X - pillLeft, (MascotArt.InsetX * scale) - 1, (MascotArt.InsetX * scale) + 1);
        double middle = at.Y + (MascotArt.MiddleY / MascotArt.ViewBox * MascotArt.SizeDips * scale);
        double pillMiddle = IslandOutline.GapPixels(scale) + (pill.Depth * scale / 2);
        Assert.InRange(middle, pillMiddle - 1, pillMiddle + 1);
    }
}
