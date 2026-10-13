namespace Tomte.Core.Triggers;

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

    /// <summary>File name of the foreground process, when any app rule exists.</summary>
    public string? ForegroundExe { get; init; }
}

public static class Suppression
{
    /// <summary>
    /// The decision table, in order: nothing on a secure surface; nothing over an exclusive D3D swap chain unless
    /// the app is allow-listed (the island is not on the scanout there, so a hotkey would show nothing); then the
    /// per-app rules with the fullscreen rule (hotkeys keep working over ordinary fullscreen); then quiet holds events.
    /// </summary>
    public static bool Allows(RevealTrigger trigger, SuppressionState state, IReadOnlyList<AppRule> rules)
    {
        if (state.Secure)
        {
            return false;
        }

        if (state.ExclusiveFullscreen && AppRules.Find(rules, state.ForegroundExe)?.Mode != AppRuleMode.Allow)
        {
            return false;
        }

        if (!AppRules.Allows(rules, state.ForegroundExe, trigger, state.Fullscreen))
        {
            return false;
        }

        return trigger != RevealTrigger.Event || !state.Quiet;
    }
}
