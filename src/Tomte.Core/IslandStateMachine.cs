namespace Tomte.Core;

public sealed class IslandStateMachine
{
    // Out of Hidden each trigger reaches a different state; every state can retract to Hidden.
    private static readonly HashSet<(IslandState From, IslandState To)> Allowed =
    [
        (IslandState.Hidden, IslandState.Compact),
        (IslandState.Hidden, IslandState.Panels),
        (IslandState.Hidden, IslandState.ShelfDrop),
        (IslandState.Hidden, IslandState.Palette),
        (IslandState.Hidden, IslandState.Capture),
        (IslandState.Compact, IslandState.Panels),
        (IslandState.ShelfDrop, IslandState.Panels),
        (IslandState.ShelfDrop, IslandState.Palette),
        (IslandState.Capture, IslandState.Palette),
        (IslandState.Compact, IslandState.Hidden),
        (IslandState.Panels, IslandState.Hidden),
        (IslandState.ShelfDrop, IslandState.Hidden),
        (IslandState.Palette, IslandState.Hidden),
        (IslandState.Capture, IslandState.Hidden),
    ];

    public IslandState State { get; private set; } = IslandState.Hidden;

    public static bool CanTransition(IslandState from, IslandState to) => Allowed.Contains((from, to));

    public void TransitionTo(IslandState to)
    {
        if (!CanTransition(State, to))
        {
            throw new InvalidOperationException($"No transition from {State} to {to}.");
        }

        State = to;
    }
}
