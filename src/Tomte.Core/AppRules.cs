namespace Tomte.Core;

public enum AppRuleMode
{
    /// <summary>Edge push and drag reveal work even when this app is fullscreen.</summary>
    Allow,

    /// <summary>No edge push, drag reveal, peek or event while this app is in front; hotkeys still work.</summary>
    Deny,

    /// <summary>As Deny, and Tomte's hotkeys pass through to the app too.</summary>
    DenyWithHotkeys,
}

/// <param name="Exe">An executable name; "chrome", "chrome.exe" and a full path all mean the same app.</param>
public sealed record AppRule(string Exe, AppRuleMode Mode);

public enum RevealTrigger
{
    EdgePush,
    Hotkey,
    Drag,
    Event,
    Peek,
}

public static class AppRules
{
    /// <summary>"C:\Program Files\Google\Chrome\chrome.EXE" and "Chrome" both give "chrome".</summary>
    public static string Normalize(string exe)
    {
        string name = Path.GetFileName(exe.Trim().Trim('"'));
        return (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name).ToLowerInvariant();
    }

    /// <summary>The first rule for the foreground app's executable, or null.</summary>
    public static AppRule? Find(IReadOnlyList<AppRule> rules, string? foregroundExe)
    {
        if (string.IsNullOrWhiteSpace(foregroundExe))
        {
            return null;
        }

        string name = Normalize(foregroundExe);
        return rules.FirstOrDefault(r => Normalize(r.Exe) == name);
    }

    /// <summary>
    /// Whether a trigger may reveal the island while <paramref name="foregroundExe"/> is in front.
    /// Fullscreen with no rule keeps only hotkeys. An event that is refused here is queued by the
    /// caller, not dropped.
    /// </summary>
    public static bool Allows(IReadOnlyList<AppRule> rules, string? foregroundExe, RevealTrigger trigger, bool fullscreen) =>
        Find(rules, foregroundExe)?.Mode switch
        {
            AppRuleMode.DenyWithHotkeys => false,
            AppRuleMode.Deny => trigger == RevealTrigger.Hotkey,
            AppRuleMode.Allow => true,
            _ => !fullscreen || trigger == RevealTrigger.Hotkey,
        };

    /// <summary>An error for the settings window, or null when the name can be added.</summary>
    public static string? Validate(IReadOnlyList<AppRule> rules, string exe)
    {
        if (string.IsNullOrWhiteSpace(exe) || Normalize(exe).Length == 0)
        {
            return "Type an app's file name, for example chrome.exe.";
        }

        if (exe.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
        {
            return "That is not a file name.";
        }

        return Find(rules, exe) is { } existing ? $"{Normalize(existing.Exe)} already has a rule." : null;
    }
}
