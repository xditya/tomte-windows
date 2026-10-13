using Tomte.Core;
using Xunit;

namespace Tomte.Tests;

public class IslandStateMachineTests
{
    // The entry and exit rules of the state table, one row per state.
    private static readonly (IslandState From, IslandState To)[] SpecTransitions =
    [
        (IslandState.Hidden, IslandState.Compact),
        (IslandState.Hidden, IslandState.Panels),
        (IslandState.Hidden, IslandState.ShelfDrop),
        (IslandState.Hidden, IslandState.Palette),
        (IslandState.Hidden, IslandState.Capture),
        (IslandState.Compact, IslandState.Panels),
        (IslandState.Compact, IslandState.Hidden),
        (IslandState.Panels, IslandState.Panels),
        (IslandState.Panels, IslandState.Hidden),
        (IslandState.Palette, IslandState.Palette),
        (IslandState.ShelfDrop, IslandState.Panels),
        (IslandState.ShelfDrop, IslandState.Palette),
        (IslandState.ShelfDrop, IslandState.Hidden),
        (IslandState.Palette, IslandState.Hidden),
        (IslandState.Capture, IslandState.Palette),
        (IslandState.Capture, IslandState.Hidden),
    ];

    [Fact]
    public void OnlySpecTransitionsAreAllowed()
    {
        var states = Enum.GetValues<IslandState>();
        var allowed = states
            .SelectMany(from => states.Select(to => (From: from, To: to)))
            .Where(pair => IslandStateMachine.CanTransition(pair.From, pair.To))
            .ToHashSet();

        Assert.Equal(SpecTransitions.ToHashSet(), allowed);
    }

    [Fact]
    public void StartsHiddenAndRejectsDisallowedTransition()
    {
        var machine = new IslandStateMachine();

        Assert.Equal(IslandState.Hidden, machine.State);
        machine.TransitionTo(IslandState.Compact);
        Assert.Equal(IslandState.Compact, machine.State);
        Assert.Throws<InvalidOperationException>(() => machine.TransitionTo(IslandState.Capture));
        Assert.Equal(IslandState.Compact, machine.State);
    }
}
