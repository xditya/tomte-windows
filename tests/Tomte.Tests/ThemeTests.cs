using Tomte.Core;
using Tomte.Core.Interaction;
using Xunit;

namespace Tomte.Tests;

// The accent is process-wide: nothing else may run while a test changes it.
[CollectionDefinition(nameof(ThemeTests), DisableParallelization = true)]
public class ThemeCollection;

[Collection(nameof(ThemeTests))]
public class ThemeTests
{
    [Theory]
    [InlineData(0xFF6B4Au)]
    [InlineData(0xF2B84Bu)]
    [InlineData(0x7CC4FFu)]
    [InlineData(0x9BE08Fu)]
    public void EachLightAccentCarriesDarkInkAndAFifthOfItselfAsTheSoftAccent(uint rgb)
    {
        try
        {
            Theme.UseAccent(rgb);
            Assert.Equal(0xFF000000 | rgb, Theme.Accent);
            Assert.Equal(0x33000000 | rgb, Theme.AccentSoft);
            Assert.Equal(0xFF1A0A05u, Theme.OnAccent);
            Assert.Equal(Theme.Accent, StateLook.Accent);
            Assert.Equal(Theme.OnAccent, StateLook.CheckedText);
        }
        finally
        {
            Theme.UseAccent(Theme.DefaultAccent);
        }
    }

    [Fact]
    public void ADarkAccentCarriesLightInkAndItsAlphaIsIgnored()
    {
        try
        {
            Theme.UseAccent(0x803A5BC7);
            Assert.Equal(0xFF3A5BC7u, Theme.Accent);
            Assert.Equal(0x333A5BC7u, Theme.AccentSoft);
            Assert.Equal(Theme.Ink, Theme.OnAccent);
        }
        finally
        {
            Theme.UseAccent(Theme.DefaultAccent);
        }

        Assert.Equal(0xFFFF6B4Au, Theme.Accent);
        Assert.Equal(0x33FF6B4Au, Theme.AccentSoft);
    }

    // Windows' default blue as the taskbar shows it: its AccentDark2 and AccentLight2.
    public static TheoryData<ThemeScheme, uint> Schemes => new()
    {
        { ThemeScheme.Dark, Theme.DefaultAccent },
        { ThemeScheme.Light, Theme.DefaultAccent },
        { ThemeScheme.Dark, 0xF2B84B },
        { ThemeScheme.Light, 0xF2B84B },
        { ThemeScheme.Light, 0x7CC4FF },
        { ThemeScheme.Light, 0x9BE08F },
        { ThemeScheme.Dark, 0x0078D4 },
        { ThemeScheme.Accent, 0 },
    };

    [Theory]
    [MemberData(nameof(Schemes))]
    public void EverySchemeKeepsTextAtFourAndAHalfToOneAndTheRingAtThree(ThemeScheme scheme, uint accent)
    {
        try
        {
            Theme.UseAccent(accent);
            Theme.UseScheme(scheme, accentDark: 0x003E92, accentLight: 0x4CC2FF);
            foreach (uint surface in new[] { Theme.Island, Theme.Card, Theme.Raised })
            {
                foreach (uint ink in new[] { Theme.Ink, Theme.Ink2, Theme.Ink3 })
                {
                    Assert.True(Theme.Contrast(ink, surface) >= 4.5, $"{scheme}: {ink:X8} on {surface:X8} is {Theme.Contrast(ink, surface):F2}");
                }
            }

            Assert.True(Theme.Contrast(Theme.Accent, Theme.Island) >= 3, $"{scheme}: accent {Theme.Accent:X8} is {Theme.Contrast(Theme.Accent, Theme.Island):F2}");
            Assert.True(Theme.Contrast(Theme.OnAccent, Theme.Accent) >= 3, $"{scheme}: ink on the accent");
        }
        finally
        {
            Theme.UseScheme(ThemeScheme.Dark);
            Theme.UseAccent(Theme.DefaultAccent);
        }
    }

    [Fact]
    public void ALightIslandDeepensTheAccentAndTheAccentIslandTakesTheSystemTint()
    {
        try
        {
            Theme.UseScheme(ThemeScheme.Light);
            Assert.Equal(Theme.DeepCoral, Theme.Accent);
            Assert.Equal(0xFFFFFFFFu, Theme.OnAccent);
            Theme.UseAccent(0x7CC4FF);
            Assert.True(Theme.Contrast(Theme.Accent, Theme.Island) >= 4.5);

            Theme.UseScheme(ThemeScheme.Accent, accentDark: 0x003E92, accentLight: 0x4CC2FF);
            Assert.Equal(0xFF4CC2FFu, Theme.Accent);
        }
        finally
        {
            Theme.UseScheme(ThemeScheme.Dark);
            Theme.UseAccent(Theme.DefaultAccent);
        }

        Assert.Equal(0xFF111214u, Theme.Island);
        Assert.Equal(0xFFFF6B4Au, Theme.Accent);
    }

    [Fact]
    public void AChangeOfSchemeIsAnnounced()
    {
        int changes = 0;
        void Count() => changes++;
        Theme.Changed += Count;
        try
        {
            Theme.UseScheme(ThemeScheme.Light);
            Assert.Equal(0xFFF3F2EFu, Theme.Island);
            Assert.Equal(0x14000000u, Theme.RaisedOver);
        }
        finally
        {
            Theme.UseScheme(ThemeScheme.Dark);
            Theme.Changed -= Count;
        }

        Assert.Equal(2, changes);
        Assert.Equal(0x14FFFFFFu, Theme.RaisedOver);
    }

    [Theory]
    [InlineData(ThemeChoice.FollowTaskbar, false, false, ThemeScheme.Dark)]
    [InlineData(ThemeChoice.FollowTaskbar, true, false, ThemeScheme.Light)]
    [InlineData(ThemeChoice.FollowTaskbar, false, true, ThemeScheme.Accent)]
    [InlineData(ThemeChoice.FollowTaskbar, true, true, ThemeScheme.Light)]
    [InlineData(ThemeChoice.Dark, true, true, ThemeScheme.Dark)]
    [InlineData(ThemeChoice.Light, false, true, ThemeScheme.Light)]
    public void TheChoiceFollowsTheTaskbarUnlessSet(ThemeChoice choice, bool light, bool accentOnTaskbar, ThemeScheme expected) =>
        Assert.Equal(expected, Theme.SchemeFor(choice, light, accentOnTaskbar));

    [Fact]
    public void TheWindowsAccentIsAChoiceResolvedWhenDrawn()
    {
        Assert.Equal(AccentColors.Windows, AccentColors.Held(AccentColors.Windows));
        Assert.Equal(0x0078D4u, AccentColors.Resolve(AccentColors.Windows, 0xFF0078D4));
        Assert.Equal(0xF2B84Bu, AccentColors.Resolve(0xF2B84B, 0xFF0078D4));
        Assert.Equal(AccentColors.Default, AccentColors.Resolve(0x123456, 0xFF0078D4));
    }

    [Fact]
    public void OpacityReplacesTheAlpha() => Assert.Equal(0x66F2F0EBu, Theme.WithOpacity(Theme.Ink, 0.4f));
}
