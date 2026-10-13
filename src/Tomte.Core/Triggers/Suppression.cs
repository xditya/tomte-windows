namespace Tomte.Core.Triggers;

public enum TriggerKind
{
    EdgePush,
    Drag,
    Hotkey,
    Event,
    Peek,
    FirstRun,
}

/// <summary>What the desktop watch knows about the island's monitor. Immutable so the UI thread can swap it whole.</summary>
public sealed record SuppressionState
{
    public static readonly SuppressionState Clear = new();

    /// <summary>The shell reports a fullscreen app, or the foreground window's client rect equals the monitor.</summary>
    public bool Fullscreen { get; init; }

    /// <summary>QUNS_RUNNING_D3D_FULL_SCREEN: the island cannot be composed over it, so even a hotkey shows nothing.</summary>
    public bool ExclusiveFullscreen { get; init; }

    /// <summary>Lock screen, secure desktop or a UAC prompt.</summary>
    public bool Secure { get; init; }

    /// <summary>Quiet hours or Focus: events wait.</summary>
    public bool Quiet { get; init; }

    /// <summary>File name of the foreground process, lower case, when the per-app lists need it.</summary>
    public string? ForegroundExe { get; init; }
}

public static class Suppression
{
    /// <summary>
    /// The decision table. Deny list wins over everything but the secure check. The allow list lifts the fullscreen
    /// rules for that app (a game the user wants the island over). Hotkeys still work over ordinary fullscreen,
    /// but not over an exclusive D3D swap chain, where the island would not be visible anyway.
    /// </summary>
    public static bool Allows(TriggerKind kind, SuppressionState state, TriggerSettings settings)
    {
        if (state.Secure)
        {
            return false;
        }

        if (Listed(settings.DenyApps, state.ForegroundExe))
        {
            return false;
        }

        if (!Listed(settings.AllowApps, state.ForegroundExe))
        {
            if (state.ExclusiveFullscreen)
            {
                return false;
            }

            if (state.Fullscreen && kind != TriggerKind.Hotkey)
            {
                return false;
            }
        }

        return kind != TriggerKind.Event || !state.Quiet;
    }

    private static bool Listed(IReadOnlyList<string> apps, string? exe) =>
        exe is not null && apps.Count > 0 && apps.Any(a => string.Equals(a, exe, StringComparison.OrdinalIgnoreCase));
}
