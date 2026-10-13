using Tomte.Core.Triggers;
using Xunit;

namespace Tomte.Tests;

public class SuppressionTests
{
    private static readonly TriggerSettings Defaults = new();
    private static readonly TriggerKind[] AllKinds = Enum.GetValues<TriggerKind>();

    [Fact]
    public void ClearDesktopAllowsEverything()
    {
        foreach (var kind in AllKinds)
        {
            Assert.True(Suppression.Allows(kind, SuppressionState.Clear, Defaults));
        }
    }

    [Fact]
    public void FullscreenKeepsHotkeysAndQueuesTheRest()
    {
        var fullscreen = new SuppressionState { Fullscreen = true };
        foreach (var kind in AllKinds)
        {
            Assert.Equal(kind == TriggerKind.Hotkey, Suppression.Allows(kind, fullscreen, Defaults));
        }
    }

    [Fact]
    public void ExclusiveFullscreenSuppressesHotkeysToo()
    {
        var exclusive = new SuppressionState { Fullscreen = true, ExclusiveFullscreen = true };
        foreach (var kind in AllKinds)
        {
            Assert.False(Suppression.Allows(kind, exclusive, Defaults));
        }
    }

    [Fact]
    public void SecureSurfacesShowNothing()
    {
        var secure = new SuppressionState { Secure = true, ForegroundExe = "game.exe" };
        var settings = Defaults with { AllowApps = ["game.exe"] };
        foreach (var kind in AllKinds)
        {
            Assert.False(Suppression.Allows(kind, secure, settings));
        }
    }

    [Fact]
    public void QuietOnlyHoldsEvents()
    {
        var quiet = new SuppressionState { Quiet = true };
        foreach (var kind in AllKinds)
        {
            Assert.Equal(kind != TriggerKind.Event, Suppression.Allows(kind, quiet, Defaults));
        }
    }

    [Fact]
    public void DenyListBlocksTheAppCaseInsensitively()
    {
        var settings = Defaults with { DenyApps = ["Game.exe"] };
        var inGame = new SuppressionState { ForegroundExe = "game.exe" };
        var elsewhere = new SuppressionState { ForegroundExe = "code.exe" };
        foreach (var kind in AllKinds)
        {
            Assert.False(Suppression.Allows(kind, inGame, settings));
            Assert.True(Suppression.Allows(kind, elsewhere, settings));
        }
    }

    [Fact]
    public void AllowListLiftsFullscreenButNotDenyOrSecure()
    {
        var settings = Defaults with { AllowApps = ["game.exe"], DenyApps = ["game.exe"] };
        var allowedOnly = Defaults with { AllowApps = ["game.exe"] };
        var fullscreenGame = new SuppressionState { Fullscreen = true, ExclusiveFullscreen = true, ForegroundExe = "game.exe" };

        Assert.True(Suppression.Allows(TriggerKind.EdgePush, fullscreenGame, allowedOnly));
        Assert.True(Suppression.Allows(TriggerKind.Hotkey, fullscreenGame, allowedOnly));
        Assert.False(Suppression.Allows(TriggerKind.Hotkey, fullscreenGame, settings));
    }
}
