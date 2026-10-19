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
            Theme.UseAccent(0x80203A8C);
            Assert.Equal(0xFF203A8Cu, Theme.Accent);
            Assert.Equal(0x33203A8Cu, Theme.AccentSoft);
            Assert.Equal(Theme.Ink, Theme.OnAccent);
        }
        finally
        {
            Theme.UseAccent(Theme.DefaultAccent);
        }

        Assert.Equal(0xFFFF6B4Au, Theme.Accent);
        Assert.Equal(0x33FF6B4Au, Theme.AccentSoft);
    }

    [Fact]
    public void OpacityReplacesTheAlpha() => Assert.Equal(0x66F2F0EBu, Theme.WithOpacity(Theme.Ink, 0.4f));
}
