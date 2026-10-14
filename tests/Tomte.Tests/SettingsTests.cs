using Tomte.Core;
using Xunit;

namespace Tomte.Tests;

public sealed class SettingsTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "tomte-tests-" + Guid.NewGuid().ToString("N"));

    private string File => Path.Combine(dir, "settings.json");

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AMissingFileGivesTheSpecDefaults()
    {
        using var store = new SettingsStore(File);
        var s = store.Current;
        Assert.Equal("Win+Alt+I", s.Hotkeys.Summon);
        Assert.Equal("Win+Alt+A", s.Hotkeys.Palette);
        Assert.Equal("Win+Alt+C", s.Hotkeys.CircleToAsk);
        Assert.Equal("Win+Alt+V", s.Hotkeys.Rambler);
        Assert.Equal("Win+Alt+N", s.Hotkeys.Scratchpad);
        Assert.Equal(0.30, s.Reveal.ZoneFraction);
        Assert.Equal(60, s.Reveal.PushCounts);
        Assert.Equal(400, s.Reveal.PushWindowMs);
        Assert.Equal(350, s.Reveal.DwellMs);
        Assert.Equal(48, s.Reveal.DragApproachDip);
        Assert.Equal(600, s.Reveal.DragEnterTimeoutMs);
        Assert.Equal(24, s.Reveal.RetractMarginDip);
        Assert.Equal(400, s.Reveal.RetractDelayMs);
        Assert.True(s.DesktopPeek);
        Assert.False(s.AttachToTopEdge);
        Assert.Empty(s.AppRules);
    }

    [Fact]
    public void SavedSettingsComeBackEqual()
    {
        var changed = new TomteSettings
        {
            Hotkeys = new HotkeySettings().With(HotkeyAction.Summon, "Win+Alt+Shift+I"),
            Reveal = new RevealSettings { PushCounts = 90, DwellMs = 0 },
            AppRules = [new AppRule("chrome.exe", AppRuleMode.Deny)],
            AllMonitors = false,
            AttachToTopEdge = true,
        };
        using (var store = new SettingsStore(File))
        {
            store.Save(changed);
        }

        var loaded = SettingsStore.Load(File);
        Assert.Equal(SettingsStore.Serialize(changed), SettingsStore.Serialize(loaded));
        string text = System.IO.File.ReadAllText(File);
        Assert.Contains("\"mode\": \"Deny\"", text);
        Assert.Contains("\"summon\": \"Win+Alt+Shift+I\"", text);
        Assert.Contains("\"attachToTopEdge\": true", text);
    }

    [Fact]
    public void FieldsMissingFromTheFileKeepTheirDefaults()
    {
        Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(File, """{ "reveal": { "pushCounts": 80 }, "hotkeys": { "summon": "Win+Alt+J" } }""");
        var s = SettingsStore.Load(File);
        Assert.Equal(80, s.Reveal.PushCounts);
        Assert.Equal(400, s.Reveal.PushWindowMs);
        Assert.Equal("Win+Alt+J", s.Hotkeys.Summon);
        Assert.Equal("Win+Alt+N", s.Hotkeys.Scratchpad);
        Assert.True(s.DesktopPeek);
    }

    [Fact]
    public void AFileThatDoesNotParseIsKeptAsideAndTheDefaultsAreUsed()
    {
        Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(File, "{ not json");
        var s = SettingsStore.Load(File);
        Assert.Equal(new RevealSettings(), s.Reveal);
        Assert.Equal("{ not json", System.IO.File.ReadAllText(File + ".bad"));
    }

    [Fact]
    public void AWriteFromAnotherProcessRaisesChanged()
    {
        using var reader = new SettingsStore(File);
        using var got = new ManualResetEventSlim();
        TomteSettings? seen = null;
        reader.Changed += s =>
        {
            seen = s;
            got.Set();
        };

        // A second store on the same file stands in for the settings window's process.
        using (var writer = new SettingsStore(File))
        {
            writer.Save(writer.Current with { Reveal = new RevealSettings { DwellMs = 500 } });
        }

        Assert.True(got.Wait(TimeSpan.FromSeconds(5)), "no change notification");
        Assert.Equal(500, seen!.Reveal.DwellMs);
        Assert.Equal(500, reader.Current.Reveal.DwellMs);
    }

    [Theory]
    [InlineData("chrome", "chrome.exe")]
    [InlineData("Chrome.EXE", @"C:\Program Files\Google\Chrome\Application\chrome.exe")]
    [InlineData("\"C:\\Tools\\obs64.exe\"", "OBS64")]
    public void RulesMatchByFileNameWithoutCaseOrExtension(string ruleExe, string foreground)
    {
        var rules = new[] { new AppRule("notepad", AppRuleMode.Allow), new AppRule(ruleExe, AppRuleMode.Deny) };
        Assert.Equal(AppRuleMode.Deny, AppRules.Find(rules, foreground)?.Mode);
        Assert.Null(AppRules.Find(rules, "chromium.exe"));
        Assert.Null(AppRules.Find(rules, null));
    }

    [Fact]
    public void RulesDecideWhichTriggersMayReveal()
    {
        AppRule[] rules =
        [
            new("game", AppRuleMode.DenyWithHotkeys),
            new("chrome", AppRuleMode.Deny),
            new("vlc", AppRuleMode.Allow),
        ];

        Assert.False(AppRules.Allows(rules, "game.exe", RevealTrigger.Hotkey, fullscreen: false));
        Assert.True(AppRules.Allows(rules, "chrome.exe", RevealTrigger.Hotkey, fullscreen: false));
        Assert.False(AppRules.Allows(rules, "chrome.exe", RevealTrigger.EdgePush, fullscreen: false));
        Assert.False(AppRules.Allows(rules, "chrome.exe", RevealTrigger.Event, fullscreen: false));
        Assert.True(AppRules.Allows(rules, "vlc.exe", RevealTrigger.EdgePush, fullscreen: true));

        // No rule: everything outside fullscreen, only hotkeys in fullscreen.
        Assert.True(AppRules.Allows(rules, "code.exe", RevealTrigger.Drag, fullscreen: false));
        Assert.False(AppRules.Allows(rules, "code.exe", RevealTrigger.Drag, fullscreen: true));
        Assert.True(AppRules.Allows(rules, "code.exe", RevealTrigger.Hotkey, fullscreen: true));
    }

    [Fact]
    public void AddingARuleRejectsBlanksAndDuplicates()
    {
        var rules = new[] { new AppRule("chrome.exe", AppRuleMode.Deny) };
        Assert.NotNull(AppRules.Validate(rules, "  "));
        Assert.Equal("chrome already has a rule.", AppRules.Validate(rules, "CHROME"));
        Assert.Null(AppRules.Validate(rules, "code.exe"));
    }

    [Theory]
    [InlineData("Win+Alt+I", HotkeyModifiers.Win | HotkeyModifiers.Alt, 'I')]
    [InlineData("alt + win + i", HotkeyModifiers.Win | HotkeyModifiers.Alt, 'I')]
    [InlineData("Win+Ctrl+Alt+F13", HotkeyModifiers.Win | HotkeyModifiers.Ctrl | HotkeyModifiers.Alt, 0x7C)]
    [InlineData("Win+Shift+Space", HotkeyModifiers.Win | HotkeyModifiers.Shift, 0x20)]
    public void ChordsParseAndPrintCanonically(string text, HotkeyModifiers modifiers, uint key)
    {
        Assert.True(Hotkey.TryParse(text, out var hotkey, out _));
        Assert.Equal(new Hotkey(modifiers, key), hotkey);
        Assert.True(Hotkey.TryParse(hotkey.ToString(), out var again, out _));
        Assert.Equal(hotkey, again);
    }

    [Theory]
    [InlineData("Win+Alt", "Add a key")]
    [InlineData("Win+Alt+", "Add a key")]
    [InlineData("Win+Alt+Foo", "is not a key Tomte knows")]
    [InlineData("Win+Alt+I+J", "one key")]
    [InlineData("Shift+I", "Start with Win, Ctrl or Alt")]
    [InlineData("I", "Start with Win, Ctrl or Alt")]
    [InlineData("Ctrl+Alt+I", "AltGr")]
    [InlineData("Alt+Win+N", "Scratchpad already uses Win+Alt+N.")]
    public void RebindingRejectsBadChords(string chord, string message)
    {
        string? error = Hotkey.Validate(new HotkeySettings(), HotkeyAction.Summon, chord);
        Assert.NotNull(error);
        Assert.Contains(message, error);
    }

    [Theory]
    [InlineData("Win+Alt+Shift+I")]
    [InlineData("Win+Ctrl+Alt+I")]
    [InlineData("Win+Alt+I")]
    [InlineData("")]
    public void RebindingAcceptsGoodChordsAndBlank(string chord) =>
        Assert.Null(Hotkey.Validate(new HotkeySettings(), HotkeyAction.Summon, chord));

    [Fact]
    public void AConflictNamesTheChordAndTheAction() =>
        Assert.Equal(
            "Win+Alt+C is used by another app. Pick another shortcut for Circle to ask in Settings.",
            Hotkey.ConflictMessage(HotkeyAction.CircleToAsk, "Win+Alt+C"));

    [Fact]
    public void TheProbeReleasesWhatItRegisters()
    {
        // Win+Alt+Shift+F24 is unlikely to be taken by anything on a test machine.
        var chord = new Hotkey(HotkeyModifiers.Win | HotkeyModifiers.Alt | HotkeyModifiers.Shift, 0x87);
        Assert.Null(chord.CheckFree(HotkeyAction.Summon));
        Assert.Null(chord.CheckFree(HotkeyAction.Summon));
    }

    [Fact]
    public void APauseExpires()
    {
        var now = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        var pause = new PauseState();
        Assert.False(pause.IsPaused(now));

        pause.PauseFor(TimeSpan.FromHours(1), now);
        Assert.True(pause.IsPaused(now.AddMinutes(59)));
        Assert.False(pause.IsPaused(now.AddHours(1)));
        Assert.Equal("Tomte", pause.Describe(now.AddHours(2)));

        pause.PauseUntilRestart();
        Assert.True(pause.IsPaused(now.AddDays(30)));
        Assert.Equal("Tomte, paused until restart", pause.Describe(now));

        pause.Resume();
        Assert.False(pause.IsPaused(now));
    }
}
