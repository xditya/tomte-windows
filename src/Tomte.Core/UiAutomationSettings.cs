namespace Tomte.Core;

/// <summary>
/// Which apps the AI's UI Automation tools may read and act on. Executables are matched as <see cref="AppRules"/>
/// matches them: "KeePass", "keepass.exe" and a full path are the same app. A deny entry always wins; an empty
/// allow list means every app that is not denied.
/// </summary>
public sealed record UiAutomationSettings
{
    /// <summary>Off by default: then the palette offers no UI Automation tool and nothing of UI Automation is made.</summary>
    public bool Enabled { get; init; }

    public IReadOnlyList<string> AllowedApps { get; init; } = [];

    public IReadOnlyList<string> DeniedApps { get; init; } = UiAutomationRules.DefaultDenied;
}

public static class UiAutomationRules
{
    /// <summary>
    /// Never read or acted on, whatever the lists say: the UAC prompt, the lock screen and sign-in, credential and
    /// Windows Hello prompts and enrolment, a work or school sign-in, the Windows Security app and SmartScreen's warnings.
    /// </summary>
    public static readonly IReadOnlySet<string> SecureSurfaces = new HashSet<string>(StringComparer.Ordinal)
    {
        "consent", "lockapp", "logonui", "credentialuibroker", "creddialoghost", "bioenrollmenthost", "microsoft.aad.brokerplugin",
        "sechealthui", "securityhealthhost", "smartscreen", "chxsmartscreen",
    };

    /// <summary>
    /// Password managers, Credential Manager's own dialogs (keymgr.dll runs in rundll32, and credwiz is its backup
    /// wizard) and management consoles (certificates, security policy, the advanced firewall). The user may take
    /// any of them off the list.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultDenied =
    [
        "1password", "bitwarden", "keepass", "keepassxc", "lastpass", "dashlane", "enpass", "nordpass", "proton pass",
        "roboform", "keeperpasswordmanager", "rundll32", "credwiz", "mmc",
    ];

    /// <summary>Store apps draw inside this frame process; the app itself is checked through its own process.</summary>
    public const string FrameHost = "applicationframehost";

    /// <summary>
    /// Why the tools must not touch <paramref name="exe"/>, or null when they may. An app the reveal rules deny
    /// (<paramref name="appRules"/>) is refused too; a reveal Allow rule allows nothing here.
    /// </summary>
    public static string? Refusal(UiAutomationSettings settings, IReadOnlyList<AppRule> appRules, string exe)
    {
        string name = AppRules.Normalize(exe);
        if (name == "tomte.shell")
            return "Tomte does not act on itself.";
        if (SecureSurfaces.Contains(name))
            return $"{name} is a secure surface (sign-in, a permission or credential prompt, or Windows Security). Tomte never reads or acts on it.";
        if (settings.DeniedApps.Any(d => AppRules.Normalize(d) == name))
            return $"{name} is on the deny list for UI Automation, so Tomte does not read or act on it.";
        if (AppRules.Find(appRules, exe)?.Mode is AppRuleMode.Deny or AppRuleMode.DenyWithHotkeys)
            return $"{name} has a Deny rule in Tomte's per-app rules, so Tomte does not read or act on it.";
        if (name != FrameHost && settings.AllowedApps.Count > 0 && !settings.AllowedApps.Any(a => AppRules.Normalize(a) == name))
            return $"{name} is not on the allow list for UI Automation.";
        return null;
    }
}
