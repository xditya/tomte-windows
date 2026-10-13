using Tomte.Core;
using Xunit;

namespace Tomte.Tests;

public class SpringTests
{
    private const double FrameDt = 1.0 / 165;

    // Runs the spring from 0 to target: time until it is within one DIP (what the eye sees as arrived)
    // and time until it reports settled (when the window shrinks and the region is applied).
    private static (double Visible, double AtRest) SettleTime(SpringPreset preset, double target)
    {
        var spring = new Spring();
        spring.Use(preset);
        spring.Target = target;
        double t = 0, visible = -1;
        while (!spring.Settled)
        {
            spring.Step(FrameDt);
            t += FrameDt;
            if (visible < 0 && Math.Abs(spring.Value - target) < 1)
            {
                visible = t;
            }

            Assert.True(t < 2, "spring never settled");
        }

        return (visible, t);
    }

    [Fact]
    public void SnappyRevealArrivesInAboutThreeTenths()
    {
        var (visible, atRest) = SettleTime(SpringPreset.Snappy, IslandGeometry.Compact.Depth);
        Assert.InRange(visible, 0.2, 0.35);
        Assert.InRange(atRest, visible, 0.6);
    }

    [Fact]
    public void GentlePanelSwitchArrivesInAboutFourTenths()
    {
        double size = PanelSize.Clipboard.H - PanelSize.Shelf.H;
        var gentle = SettleTime(SpringPreset.Gentle, size);
        var snappy = SettleTime(SpringPreset.Snappy, size);
        Assert.InRange(gentle.Visible, 0.3, 0.55);
        Assert.True(gentle.Visible > snappy.Visible);
    }

    [Fact]
    public void RetargetMidFlightKeepsVelocityAndSettlesOnNewTarget()
    {
        var spring = new Spring();
        spring.Use(SpringPreset.Snappy);
        spring.Target = IslandGeometry.ShelfDrop.Depth;
        for (int i = 0; i < 10; i++)
        {
            spring.Step(FrameDt);
        }

        double valueBefore = spring.Value, velocityBefore = spring.Velocity;
        Assert.True(velocityBefore > 0);

        spring.Target = 0;
        spring.Step(FrameDt);

        // No pop: the value moved on by about one frame of the old velocity, not back toward zero.
        Assert.InRange(spring.Value - valueBefore, 0, velocityBefore * FrameDt * 1.05);
        double t = FrameDt;
        while (!spring.Settled)
        {
            spring.Step(FrameDt);
            t += FrameDt;
            Assert.True(t < 2);
        }

        Assert.InRange(spring.Value, -0.05, 0.05);
    }

    [Fact]
    public void JumpStopsMotion()
    {
        var spring = new Spring();
        spring.Target = 100;
        spring.Step(0.1);
        spring.Jump(5);
        Assert.Equal(5, spring.Value);
        Assert.Equal(5, spring.Target);
        Assert.True(spring.Settled);
    }

    [Theory]
    [InlineData(IslandState.Hidden, IslandState.Compact, SpringPreset.Snappy)]
    [InlineData(IslandState.Hidden, IslandState.Panels, SpringPreset.Snappy)]
    [InlineData(IslandState.Hidden, IslandState.Palette, SpringPreset.Snappy)]
    [InlineData(IslandState.Hidden, IslandState.ShelfDrop, SpringPreset.Snappy)]
    [InlineData(IslandState.Compact, IslandState.Hidden, SpringPreset.Snappy)]
    [InlineData(IslandState.Panels, IslandState.Hidden, SpringPreset.Snappy)]
    [InlineData(IslandState.Compact, IslandState.Panels, SpringPreset.Gentle)]
    [InlineData(IslandState.Panels, IslandState.Panels, SpringPreset.Gentle)]
    [InlineData(IslandState.ShelfDrop, IslandState.Panels, SpringPreset.Gentle)]
    [InlineData(IslandState.Palette, IslandState.Palette, SpringPreset.Gentle)]
    public void PresetFollowsTheSpec(IslandState from, IslandState to, SpringPreset expected) =>
        Assert.Equal(expected, Spring.PresetFor(from, to));
}
