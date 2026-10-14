using System.Drawing;
using System.Numerics;
using Tomte.Core.Interaction;
using Xunit;

namespace Tomte.Tests;

public class InteractionTests
{
    private static readonly Vector2 Right = new(1, 0), Left = new(-1, 0), Down = new(0, 1), Up = new(0, -1);

    private static Target At(string id, float x, float y, float w = 20, float h = 20, bool enabled = true, int group = 0) =>
        new(id, new RectangleF(x, y, w, h), 4, TargetKind.Button, enabled, Group: group);

    [Fact]
    public void HitFindsTheTargetUnderThePointInTheViewsOwnSpace()
    {
        Target[] body = [At("a", 0, 0), At("b", 30, 0)];
        // The view's space starts 100 DIPs right of and 42 below island space's origin.
        var views = new List<(IReadOnlyList<Target>, Func<Vector2, Vector2>)> { (body, p => p - new Vector2(100, 42)) };

        Assert.Equal((0, 1), Target.Hit(views, new Vector2(135, 50)));
        Assert.Equal((0, 0), Target.Hit(views, new Vector2(100, 42)));
        // Right and bottom edges are outside, and so is the gap between targets.
        Assert.Null(Target.Hit(views, new Vector2(120, 50)));
        Assert.Null(Target.Hit(views, new Vector2(110, 62)));
    }

    [Fact]
    public void LaterTargetsAndLaterViewsSitOnTop()
    {
        // A row with its remove button inside it, then a disabled one: a press on it is still nobody else's.
        Target[] rows = [At("row", 0, 0, 200, 40), At("remove", 170, 8, 24, 24), At("off", 0, 0, 10, 10, enabled: false)];
        Target[] over = [At("tip", 180, 10)];
        var views = new List<(IReadOnlyList<Target>, Func<Vector2, Vector2>)> { (rows, p => p) };

        Assert.Equal((0, 1), Target.Hit(views, new Vector2(180, 20)));
        Assert.Equal((0, 0), Target.Hit(views, new Vector2(100, 20)));
        Assert.Equal((0, 2), Target.Hit(views, new Vector2(5, 5)));

        views.Add((over, p => p));
        Assert.Equal((1, 0), Target.Hit(views, new Vector2(185, 15)));
        Assert.Equal((0, 1), Target.Hit(views, new Vector2(172, 30)));
    }

    [Fact]
    public void TabGoesThroughEnabledTargetsInListOrderAndWraps()
    {
        Target[] list = [At("a", 0, 0), At("b", 30, 0, enabled: false), At("c", 60, 0), At("d", 90, 0)];

        Assert.Equal(0, Target.Next(list, -1, 1));
        Assert.Equal(3, Target.Next(list, -1, -1));
        Assert.Equal(2, Target.Next(list, 0, 1));
        Assert.Equal(0, Target.Next(list, 3, 1));
        Assert.Equal(0, Target.Next(list, 2, -1));
        Assert.Equal(3, Target.Next(list, 0, -1));
        // A lone enabled target keeps the focus.
        Assert.Equal(0, Target.Next([At("a", 0, 0), At("b", 30, 0, enabled: false)], 0, 1));
        Assert.Equal(-1, Target.Next([At("a", 0, 0, enabled: false)], -1, 1));
        Assert.Equal(-1, Target.Next([], -1, 1));
    }

    [Fact]
    public void AGroupIsOneTabStopEnteredAtItsFirstMember()
    {
        // A prompt, three chips in group 1 (the first one disabled), and a Send button.
        Target[] list =
        [
            At("prompt", 0, 0), At("chip-a", 0, 40, enabled: false, group: 1), At("chip-b", 40, 40, group: 1),
            At("chip-c", 80, 40, group: 1), At("send", 120, 0),
        ];

        // Tab from the prompt enters the group at its first enabled chip; Tab from any chip leaves the group.
        Assert.Equal(2, Target.Next(list, 0, 1));
        Assert.Equal(4, Target.Next(list, 2, 1));
        Assert.Equal(4, Target.Next(list, 3, 1));
        // Shift+Tab from Send enters the group at its first enabled chip too; from a chip it goes back to the prompt.
        Assert.Equal(2, Target.Next(list, 4, -1));
        Assert.Equal(0, Target.Next(list, 3, -1));
        // A list that is one group: Tab keeps the focus where it is.
        Assert.Equal(1, Target.Next([At("a", 0, 0, group: 2), At("b", 30, 0, group: 2)], 1, 1));
    }

    [Fact]
    public void ArrowsMoveWithinAGroupByWhereTargetsAre()
    {
        // Two rows of three tiles in group 1, a disabled tile at the end of the second row and a button in no group.
        Target[] grid =
        [
            At("a", 0, 0, 100, 80, group: 1), At("b", 110, 0, 100, 80, group: 1), At("c", 220, 0, 100, 80, group: 1),
            At("d", 0, 90, 100, 80, group: 1), At("e", 110, 90, 100, 80, group: 1), At("f", 220, 90, 100, 80, enabled: false, group: 1),
            At("add", 330, 0, 100, 80),
        ];

        Assert.Equal(1, Target.Arrow(grid, 0, Right));
        Assert.Equal(3, Target.Arrow(grid, 0, Down));
        Assert.Equal(1, Target.Arrow(grid, 4, Up));
        Assert.Equal(3, Target.Arrow(grid, 4, Left));
        // Down from the last column: its own cell below is disabled, so the nearest one that way, not across the row.
        Assert.Equal(4, Target.Arrow(grid, 2, Down));
        // No wrapping, nothing past the edge, and the button outside the group is never reached.
        Assert.Equal(-1, Target.Arrow(grid, 2, Right));
        Assert.Equal(-1, Target.Arrow(grid, 0, Up));
        Assert.Equal(-1, Target.Arrow(grid, 6, Left));
        Assert.Equal(-1, Target.Arrow(grid, -1, Right));
    }

    [Fact]
    public void TheFocusRingSitsOutsideTheBoundsAndFollowsTheCorner()
    {
        var (ring, corner) = StateLook.FocusRing(new RectangleF(10, 20, 100, 28), 8);

        // The stroke's middle line: 2 DIP out, then half the 2 DIP stroke, so its inner edge is 2 DIP off the bounds.
        Assert.Equal(new RectangleF(7, 17, 106, 34), ring);
        Assert.Equal(11, corner);
    }
}
