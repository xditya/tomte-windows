using Tomte.Core;
using Tomte.Core.Triggers;
using Xunit;

namespace Tomte.Tests;

public class SuppressionTests
{
    private static readonly IReadOnlyList<AppRule> NoRules = [];
    private static readonly RevealTrigger[] AllTriggers = Enum.GetValues<RevealTrigger>();

    [Fact]
    public void ClearDesktopAllowsEverything()
    {
        foreach (var trigger in AllTriggers)
        {
            Assert.True(Suppression.Allows(trigger, SuppressionState.Clear, NoRules));
        }
    }

    [Fact]
    public void FullscreenKeepsHotkeysAndHoldsTheRest()
    {
        var fullscreen = new SuppressionState { Fullscreen = true };
        foreach (var trigger in AllTriggers)
        {
            Assert.Equal(trigger == RevealTrigger.Hotkey, Suppression.Allows(trigger, fullscreen, NoRules));
        }
    }

    [Fact]
    public void ExclusiveFullscreenSuppressesHotkeysToo()
    {
        var exclusive = new SuppressionState { Fullscreen = true, ExclusiveFullscreen = true, ForegroundExe = "game.exe" };
        foreach (var trigger in AllTriggers)
        {
            Assert.False(Suppression.Allows(trigger, exclusive, NoRules));
            Assert.False(Suppression.Allows(trigger, exclusive, [new AppRule("game", AppRuleMode.Deny)]));
        }

        // An Allow rule is the user saying the island belongs over this game.
        Assert.True(Suppression.Allows(RevealTrigger.Hotkey, exclusive, [new AppRule("game.exe", AppRuleMode.Allow)]));
        Assert.True(Suppression.Allows(RevealTrigger.EdgePush, exclusive, [new AppRule("game.exe", AppRuleMode.Allow)]));
    }

    [Fact]
    public void SecureSurfacesShowNothing()
    {
        var secure = new SuppressionState { Secure = true, ForegroundExe = "game.exe" };
        foreach (var trigger in AllTriggers)
        {
            Assert.False(Suppression.Allows(trigger, secure, [new AppRule("game.exe", AppRuleMode.Allow)]));
        }
    }

    [Fact]
    public void QuietOnlyHoldsEvents()
    {
        var quiet = new SuppressionState { Quiet = true };
        foreach (var trigger in AllTriggers)
        {
            Assert.Equal(trigger != RevealTrigger.Event, Suppression.Allows(trigger, quiet, NoRules));
        }
    }

    [Fact]
    public void AppRulesDecideByModeForTheForegroundExe()
    {
        var inGame = new SuppressionState { ForegroundExe = @"C:\Games\Game.EXE" };
        var elsewhere = new SuppressionState { ForegroundExe = "code.exe" };
        IReadOnlyList<AppRule> deny = [new AppRule("game", AppRuleMode.Deny)];
        IReadOnlyList<AppRule> denyAll = [new AppRule("game", AppRuleMode.DenyWithHotkeys)];
        foreach (var trigger in AllTriggers)
        {
            Assert.Equal(trigger == RevealTrigger.Hotkey, Suppression.Allows(trigger, inGame, deny));
            Assert.False(Suppression.Allows(trigger, inGame, denyAll));
            Assert.True(Suppression.Allows(trigger, elsewhere, deny));
        }
    }
}
