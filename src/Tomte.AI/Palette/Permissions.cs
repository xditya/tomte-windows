using Tomte.Core;
using Tomte.Core.Device;

namespace Tomte.AI.Palette;

public enum PermissionAnswer { Deny, AllowOnce, AlwaysAllow }

/// <summary>What the renderer shows before an external-effect or destructive tool runs.</summary>
public sealed record ToolRequest(string Tool, string Summary, ActionTier Tier, bool OfferAlwaysAllow);

/// <summary>
/// The "always allow this tool" grants and the tier rules. Read and app-local writes run on their
/// own, external effects ask unless granted, destructive actions ask every time. Bound to a
/// <see cref="SettingsStore"/> the grants live in settings.json, where the settings window shows
/// and removes them; without one they stay in memory (tests, the harness).
/// </summary>
public sealed class PermissionGrants
{
    private readonly SettingsStore? _store;
    private HashSet<string> _always = new(StringComparer.Ordinal);

    public PermissionGrants()
    {
    }

    public PermissionGrants(SettingsStore store)
    {
        _store = store;
        _always = new(store.Current.AlwaysAllowedTools, StringComparer.Ordinal);
        // The set is swapped whole, so a change from the settings process never races a read here.
        store.Changed += s => _always = new(s.AlwaysAllowedTools, StringComparer.Ordinal);
    }

    public IReadOnlyCollection<string> AlwaysAllowed => _always;

    public void AlwaysAllow(string tool) => Set(_always.Append(tool));

    public void Revoke(string tool) => Set(_always.Where(t => t != tool));

    public bool NeedsConfirmation(string tool, ActionTier tier) => tier switch
    {
        ActionTier.Read or ActionTier.AppLocalWrite => false,
        ActionTier.ExternalEffect => !_always.Contains(tool),
        _ => true,
    };

    public static bool CanAlwaysAllow(ActionTier tier) => tier == ActionTier.ExternalEffect;

    private void Set(IEnumerable<string> tools)
    {
        var set = new HashSet<string>(tools, StringComparer.Ordinal);
        if (_store is null)
            _always = set;
        else
            _store.Save(_store.Current with { AlwaysAllowedTools = [.. set.Order(StringComparer.Ordinal)] });
    }
}
