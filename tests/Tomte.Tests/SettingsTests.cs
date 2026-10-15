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
        Assert.Equal(1000, s.Reveal.ExpandDwellMs);
        Assert.Equal(48, s.Reveal.DragApproachDip);
        Assert.Equal(600, s.Reveal.DragEnterTimeoutMs);
        Assert.Equal(24, s.Reveal.RetractMarginDip);
        Assert.Equal(400, s.Reveal.RetractDelayMs);
        Assert.True(s.DesktopPeek);
        Assert.False(s.AttachToTopEdge);
        Assert.Equal(0, s.IslandOffset);
        Assert.Empty(s.AppRules);
    }

    [Fact]
    public void DesktopAutomationIsOffUntilTurnedOnAndKeepsItsLists()
    {
        Assert.False(new TomteSettings().UiAutomation.Enabled);
        using (var store = new SettingsStore(File))
        {
            store.Save(store.Current with { UiAutomation = store.Current.UiAutomation with { Enabled = true } });
        }

        var loaded = SettingsStore.Load(File).UiAutomation;
        Assert.True(loaded.Enabled);
        Assert.Equal(new UiAutomationSettings().DeniedApps, loaded.DeniedApps);
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
            IslandOffset = -312.5,
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
    public void TheProviderComesBackWithItsCapAndAFileWithoutACapGetsTheDefault()
    {
        var provider = new ProviderConfig("localhost:11434", ProviderKind.OpenAICompatible, "http://localhost:11434/v1", "llama3.2:3b", DailyCapUsd: null);
        using (var store = new SettingsStore(File))
        {
            store.Save(new TomteSettings { Provider = provider });
        }

        Assert.Equal(provider, SettingsStore.Load(File).Provider);
        Assert.Contains("\"kind\": \"OpenAICompatible\"", System.IO.File.ReadAllText(File));

        System.IO.File.WriteAllText(File, """{ "provider": { "id": "api.anthropic.com", "kind": "Anthropic", "defaultModel": "m" } }""");
        Assert.Equal(new ProviderConfig("api.anthropic.com", ProviderKind.Anthropic, DefaultModel: "m", DailyCapUsd: 2m), SettingsStore.Load(File).Provider);
        Assert.Null(new TomteSettings().Provider);
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
    public void AFileFromBeforeTheExpandDwellGetsTheDefaultAndKeepsItsOwnDwell()
    {
        Directory.CreateDirectory(dir);
        System.IO.File.WriteAllText(File, """{ "reveal": { "dwellMs": 500, "retractDelayMs": 600 } }""");
        var s = SettingsStore.Load(File);
        Assert.Equal(1000, s.Reveal.ExpandDwellMs);
        Assert.Equal(500, s.Reveal.DwellMs);
        Assert.Equal(600, s.Reveal.RetractDelayMs);
        // Saved and read again it is in the file under its own name, and the held value is not.
        string text = SettingsStore.Serialize(s with { Reveal = s.Reveal with { ExpandDwellMs = 1500 } });
        Assert.Contains("\"expandDwellMs\": 1500", text);
        Assert.DoesNotContain("expandDwellHeldMs", text);
    }

    [Theory]
    [InlineData(1000, 1000)]
    [InlineData(300, 300)]
    [InlineData(3000, 3000)]
    [InlineData(0, 300)]
    [InlineData(-5, 300)]
    [InlineData(299, 300)]
    [InlineData(3001, 3000)]
    [InlineData(60000, 3000)]
    public void TheExpandDwellIsHeldToItsRange(int written, int used)
    {
        Assert.Equal(used, new RevealSettings { ExpandDwellMs = written }.ExpandDwellHeldMs);
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
    [InlineData("Shift+I", "Add Win")]
    [InlineData("I", "Add Win")]
    [InlineData("Ctrl+Alt+I", "Add Win")]
    [InlineData("Ctrl+C", "Add Win")]
    [InlineData("Ctrl+Shift+T", "Add Win")]
    [InlineData("Alt+F4", "Add Win")]
    [InlineData("Alt+I", "Add Win")]
    [InlineData("Win+Alt+Enter", "says yes to a voice command")]
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
    [InlineData("Win+Alt+K")]
    [InlineData("Win+Alt+Shift+Enter")]
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

    [Theory]
    [InlineData(0x4B, HotkeyModifiers.Win | HotkeyModifiers.Alt, HotkeyPress.Chord, "Win+Alt+K")]
    [InlineData(0x37, HotkeyModifiers.Alt | HotkeyModifiers.Shift | HotkeyModifiers.Ctrl | HotkeyModifiers.Win, HotkeyPress.Chord, "Win+Ctrl+Alt+Shift+7")]
    [InlineData(0x87, HotkeyModifiers.Win, HotkeyPress.Chord, "Win+F24")]
    [InlineData(0x20, HotkeyModifiers.Win | HotkeyModifiers.Shift, HotkeyPress.Chord, "Win+Shift+Space")]
    [InlineData(0xBC, HotkeyModifiers.Alt, HotkeyPress.Chord, "Alt+Comma")]
    [InlineData(0x4B, HotkeyModifiers.None, HotkeyPress.Chord, "K")]
    [InlineData(0x5B, HotkeyModifiers.None, HotkeyPress.Preview, "Win+...")]
    [InlineData(0x12, HotkeyModifiers.Win, HotkeyPress.Preview, "Win+Alt+...")]
    [InlineData(0xA5, HotkeyModifiers.Win | HotkeyModifiers.Alt, HotkeyPress.Preview, "Win+Alt+...")]
    [InlineData(0xA0, HotkeyModifiers.Ctrl, HotkeyPress.Preview, "Ctrl+Shift+...")]
    [InlineData(0x09, HotkeyModifiers.None, HotkeyPress.Pass, "")]
    [InlineData(0x09, HotkeyModifiers.Shift, HotkeyPress.Pass, "")]
    [InlineData(0x08, HotkeyModifiers.None, HotkeyPress.Clear, "")]
    [InlineData(0x2E, HotkeyModifiers.Win, HotkeyPress.Clear, "")]
    [InlineData(0x1B, HotkeyModifiers.None, HotkeyPress.Restore, "")]
    [InlineData(0x25, HotkeyModifiers.Win | HotkeyModifiers.Alt, HotkeyPress.Unusable, "")] // Left arrow
    [InlineData(0x61, HotkeyModifiers.Win | HotkeyModifiers.Alt, HotkeyPress.Unusable, "")] // numpad 1, which is 'a' as a char
    [InlineData(0x14, HotkeyModifiers.None, HotkeyPress.Unusable, "")] // Caps Lock
    [InlineData(0, HotkeyModifiers.Win, HotkeyPress.Unusable, "")]
    public void AKeyPressedInAShortcutBoxRecordsWhatTheChordParsesTo(uint key, HotkeyModifiers held, HotkeyPress press, string text)
    {
        Assert.Equal((press, text), Hotkey.Record(key, held));
        if (press == HotkeyPress.Chord)
        {
            Assert.True(Hotkey.TryParse(text, out var parsed, out _));
            Assert.Equal(new Hotkey(held, key), parsed);
        }
    }

    [Fact]
    public void OnlyLettersDigitsFunctionKeysAndTheNamedKeysMakeAChord() =>
        Assert.Equal(26 + 10 + 24 + 8, Enumerable.Range(1, 0xFE).Count(k => Hotkey.Record((uint)k, HotkeyModifiers.Win).Press == HotkeyPress.Chord));

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

    [Fact]
    public void ASaveOfSomethingElseLeavesTheChordsAndTheRevealSettingsEqual()
    {
        // The shell re-registers hotkeys and restarts pointer tracking only when these compare unequal, and a save
        // from the settings window reaches it as new objects read back from the file. A collection in either record
        // would compare by reference and make every save look like a change.
        using var store = new SettingsStore(File);
        var before = store.Current;
        store.Save(before with { FirstRunHintShown = true, AlwaysAllowedTools = ["set_dark_mode"] });
        var after = SettingsStore.Load(File);

        Assert.NotSame(before.Reveal, after.Reveal);
        Assert.True(after.Hotkeys == before.Hotkeys);
        Assert.True(after.Reveal == before.Reveal);
        Assert.True(after.Hotkeys.With(HotkeyAction.Summon, "") != before.Hotkeys);
        Assert.True((after.Reveal with { DwellMs = 0 }) != before.Reveal);
    }

    [Fact]
    public void EndingAPauseIsAnnouncedOnceAndSwitchingBetweenPausesIsNot()
    {
        var now = new DateTimeOffset(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);
        var pause = new PauseState();
        int ended = 0;
        pause.Ended += () => ended++;

        pause.Resume();
        Assert.Equal(0, ended);

        pause.PauseFor(TimeSpan.FromHours(1), now);
        pause.PauseUntilRestart();
        pause.PauseFor(TimeSpan.FromHours(1), now);
        Assert.Equal(0, ended);

        pause.Resume();
        pause.Resume();
        Assert.Equal(1, ended);

        pause.PauseUntilRestart();
        pause.Resume();
        Assert.Equal(2, ended);
    }
}
