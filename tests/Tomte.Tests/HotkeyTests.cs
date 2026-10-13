using Tomte.Core.Triggers;
using Xunit;

namespace Tomte.Tests;

public class HotkeyTests
{
    [Fact]
    public void DefaultsAreTheSpecsWinAltLetters()
    {
        var expected = new Dictionary<HotkeyAction, string>
        {
            [HotkeyAction.Summon] = "Win+Alt+I",
            [HotkeyAction.Palette] = "Win+Alt+A",
            [HotkeyAction.CircleToAsk] = "Win+Alt+C",
            [HotkeyAction.Rambler] = "Win+Alt+V",
            [HotkeyAction.Scratchpad] = "Win+Alt+N",
        };

        Assert.Equal(expected, Chord.Defaults.ToDictionary(p => p.Key, p => p.Value.ToString()));
        Assert.All(Chord.Defaults.Values, c => Assert.Equal(ChordModifiers.Win | ChordModifiers.Alt, c.Modifiers));
    }

    [Fact]
    public void ConflictReportNamesEachTakenChordAndNothingElse()
    {
        HotkeyRegistration[] results =
        [
            new(HotkeyAction.Summon, Chord.Defaults[HotkeyAction.Summon], 0),
            new(HotkeyAction.CircleToAsk, Chord.Defaults[HotkeyAction.CircleToAsk], HotkeyRegistration.AlreadyRegistered),
            new(HotkeyAction.Scratchpad, Chord.Defaults[HotkeyAction.Scratchpad], 1400),
        ];

        var lines = HotkeyConflicts.Describe(results);

        Assert.Equal(2, lines.Count);
        Assert.Equal("Win+Alt+C (Circle to ask) is used by another app. Rebind it in settings.", lines[0]);
        Assert.Equal("Win+Alt+N (Scratchpad) could not be registered (error 1400). Rebind it in settings.", lines[1]);
        Assert.Empty(HotkeyConflicts.Describe(results.Take(1)));
    }
}
