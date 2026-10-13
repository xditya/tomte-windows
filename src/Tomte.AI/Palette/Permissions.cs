using Tomte.Core.Device;

namespace Tomte.AI.Palette;

public enum PermissionAnswer { Deny, AllowOnce, AlwaysAllow }

/// <summary>What the renderer shows before an external-effect or destructive tool runs.</summary>
public sealed record ToolRequest(string Tool, string Summary, ActionTier Tier, bool OfferAlwaysAllow);

/// <summary>
/// The "always allow this tool" grants and the tier rules. Read and app-local writes run on their
/// own, external effects ask unless granted, destructive actions ask every time. In memory until
/// settings storage lands; the set is what Settings will show and edit.
/// </summary>
public sealed class PermissionGrants
{
    private readonly HashSet<string> _always = new(StringComparer.Ordinal);

    public IReadOnlyCollection<string> AlwaysAllowed => _always;

    public void AlwaysAllow(string tool) => _always.Add(tool);

    public void Revoke(string tool) => _always.Remove(tool);

    public bool NeedsConfirmation(string tool, ActionTier tier) => tier switch
    {
        ActionTier.Read or ActionTier.AppLocalWrite => false,
        ActionTier.ExternalEffect => !_always.Contains(tool),
        _ => true,
    };

    public static bool CanAlwaysAllow(ActionTier tier) => tier == ActionTier.ExternalEffect;
}
