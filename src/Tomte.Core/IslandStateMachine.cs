namespace Tomte.Core;

public sealed class IslandStateMachine
{
    // Out of Hidden each trigger reaches a different state; every state can retract to Hidden.
    // Panels and Palette re-enter themselves: a tab switch or the answer growing morphs the size.
    // The palette goes back to the panels it was opened from.
    private static readonly HashSet<(IslandState From, IslandState To)> Allowed =
    [
        (IslandState.Panels, IslandState.Panels),
        (IslandState.Palette, IslandState.Palette),
        (IslandState.Palette, IslandState.Panels),
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

    /// <summary>
    /// Every transition, self-transitions included, as (from, to), raised before the shape starts to morph.
    /// Whatever draws content inside the island hears every driver of the machine here.
    /// </summary>
    public event Action<IslandState, IslandState>? Changed;

    public static bool CanTransition(IslandState from, IslandState to) => Allowed.Contains((from, to));

    public void TransitionTo(IslandState to)
    {
        if (!CanTransition(State, to))
        {
            throw new InvalidOperationException($"No transition from {State} to {to}.");
        }

        var from = State;
        State = to;
        Changed?.Invoke(from, to);
    }
}
