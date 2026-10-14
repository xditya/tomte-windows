using Tomte.Core;
using Tomte.Core.Device;

namespace Tomte.AI.Palette;

public enum PermissionAnswer { Deny, AllowOnce, AlwaysAllow }

/// <summary>What the renderer shows before an external-effect or destructive tool runs.</summary>
public sealed record ToolRequest(string Tool, string Summary, ActionTier Tier, bool OfferAlwaysAllow);

/// <summary>
/// The one question a palette has open, typed or by voice: the session asks it through <see cref="AskAsync"/> as its
/// ConfirmTool, the palette draws <see cref="Asked"/>, and the user's answer settles it. So does anything that ends
/// the session (its cancel answers no). An answer given for one question never lands on a later one. One thread.
/// </summary>
public sealed class ToolQuestion
{
    private TaskCompletionSource<PermissionAnswer>? _answer;

    /// <summary>What is being asked now, or null.</summary>
    public ToolRequest? Asked { get; private set; }

    /// <summary>A question opened or closed; raised on the session's thread.</summary>
    public event Action? Changed;

    /// <summary>Opens the question; <see cref="Asked"/> is set before this returns.</summary>
    public async Task<PermissionAnswer> AskAsync(ToolRequest request, CancellationToken ct)
    {
        var answer = _answer = new TaskCompletionSource<PermissionAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        Asked = request;
        Changed?.Invoke();
        using var cancel = ct.Register(() => answer.TrySetResult(PermissionAnswer.Deny));
        try
        {
            return await answer.Task;
        }
        finally
        {
            if (_answer == answer)
            {
                (_answer, Asked) = (null, null);
                Changed?.Invoke();
            }
        }
    }

    /// <summary>
    /// Answers the open question. With <paramref name="to"/>, only while that request is still the one asked. "Always
    /// allow" for a request that does not offer it counts once. False when nothing was answered.
    /// </summary>
    public bool Answer(PermissionAnswer given, ToolRequest? to = null) =>
        _answer is { } open && (to is null || ReferenceEquals(to, Asked))
        && open.TrySetResult(given == PermissionAnswer.AlwaysAllow && Asked?.OfferAlwaysAllow != true ? PermissionAnswer.AllowOnce : given);

    public bool Answer(bool allow, ToolRequest? to = null) => Answer(allow ? PermissionAnswer.AllowOnce : PermissionAnswer.Deny, to);
}

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

    /// <summary>
    /// One per store, kept for as long as the store: it listens to the store's Changed and never lets go.
    /// The shell makes it once and hands it to every palette session; a new one per session would pile up handlers.
    /// </summary>
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
