using System.Runtime.InteropServices;
using Tomte.Core.Jobs;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Tomte.Core;

public enum HotkeyAction
{
    Summon,
    Palette,
    CircleToAsk,
    Rambler,
    Scratchpad,
}

/// <summary>Same bit values as RegisterHotKey's MOD_ALT, MOD_CONTROL, MOD_SHIFT and MOD_WIN.</summary>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Ctrl = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>What a key pressed in a shortcut box does; <see cref="Hotkey.Record"/> works it out.</summary>
public enum HotkeyPress
{
    /// <summary>Tab: not taken, so focus moves on.</summary>
    Pass,

    /// <summary>Backspace or Delete: no shortcut.</summary>
    Clear,

    /// <summary>Esc: the saved shortcut again.</summary>
    Restore,

    /// <summary>A modifier: the box shows what is held, such as "Win+Alt+...", and nothing is saved.</summary>
    Preview,

    /// <summary>A key a shortcut can use, with the modifiers held: the chord to save.</summary>
    Chord,

    /// <summary>A key no shortcut can use.</summary>
    Unusable,
}

/// <param name="Key">A virtual-key code.</param>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, uint Key)
{
    /// <summary>What the settings window posts to the running Tomte while a shortcut box records: wParam 1 pauses Tomte's own hotkeys, 0 brings them back.</summary>
    public const uint MsgPause = PInvoke.WM_APP + 0x4C;

    private const int ProbeId = 0xB000;

    private static readonly Dictionary<string, uint> NamedKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Space"] = 0x20,
        ["Enter"] = 0x0D,
        ["Comma"] = 0xBC,
        ["Period"] = 0xBE,
        ["Minus"] = 0xBD,
        ["Plus"] = 0xBB,
        ["Slash"] = 0xBF,
        ["Backtick"] = 0xC0,
    };

    public static string Name(HotkeyAction action) => action switch
    {
        HotkeyAction.Summon => "Summon island",
        HotkeyAction.Palette => "AI palette",
        HotkeyAction.CircleToAsk => "Circle to ask",
        HotkeyAction.Rambler => "Rambler (hold)",
        HotkeyAction.Scratchpad => "Scratchpad",
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    /// <summary>What the island and the settings window say when RegisterHotKey fails with ERROR_HOTKEY_ALREADY_REGISTERED. Windows itself tells the user nothing.</summary>
    public static string ConflictMessage(HotkeyAction action, string chord) =>
        $"{chord} is used by another app. Pick another shortcut for {Name(action)} in Settings.";

    /// <summary>Parses "Win+Alt+I". Modifiers in any order; the key is a letter, a digit, F1 to F24 or one of a few named keys.</summary>
    public static bool TryParse(string text, out Hotkey hotkey, out string? error)
    {
        hotkey = default;
        var modifiers = HotkeyModifiers.None;
        uint key = 0;
        foreach (string raw in text.Split('+', StringSplitOptions.TrimEntries))
        {
            HotkeyModifiers modifier = raw.ToLowerInvariant() switch
            {
                "win" => HotkeyModifiers.Win,
                "ctrl" or "control" => HotkeyModifiers.Ctrl,
                "alt" => HotkeyModifiers.Alt,
                "shift" => HotkeyModifiers.Shift,
                _ => HotkeyModifiers.None,
            };
            if (modifier != HotkeyModifiers.None)
            {
                modifiers |= modifier;
                continue;
            }

            if (key != 0)
            {
                error = "Use one key with the modifiers, for example Win+Alt+I.";
                return false;
            }

            key = KeyCode(raw);
            if (key == 0)
            {
                error = raw.Length == 0
                    ? "Add a key after the modifiers, for example Win+Alt+I."
                    : $"\"{raw}\" is not a key Tomte knows. Use a letter, a digit, F1 to F24 or Space.";
                return false;
            }
        }

        if (key == 0)
        {
            error = "Add a key after the modifiers, for example Win+Alt+I.";
            return false;
        }

        hotkey = new Hotkey(modifiers, key);
        error = null;
        return true;
    }

    /// <summary>
    /// Checks a new chord for <paramref name="action"/> against the rules for Tomte's shortcuts and
    /// against the other bindings. Returns the message to show, or null when it can be saved. An empty
    /// chord means "no shortcut" and is allowed. Whether another app owns the chord is <see cref="CheckFree"/>.
    /// </summary>
    public static string? Validate(HotkeySettings bindings, HotkeyAction action, string chord)
    {
        if (string.IsNullOrWhiteSpace(chord))
        {
            return null;
        }

        if (!TryParse(chord, out var hotkey, out string? error))
        {
            return error;
        }

        if ((hotkey.Modifiers & (HotkeyModifiers.Win | HotkeyModifiers.Ctrl | HotkeyModifiers.Alt)) == 0)
        {
            return "Start with Win, Ctrl or Alt, so the shortcut does not get in the way of typing.";
        }

        if (hotkey.Modifiers.HasFlag(HotkeyModifiers.Ctrl | HotkeyModifiers.Alt) && !hotkey.Modifiers.HasFlag(HotkeyModifiers.Win))
        {
            return "Ctrl+Alt shortcuts type characters on keyboards with AltGr. Add Win, or use Win+Alt.";
        }

        foreach (var other in Enum.GetValues<HotkeyAction>())
        {
            if (other != action && TryParse(bindings[other], out var taken, out _) && taken == hotkey)
            {
                return $"{Name(other)} already uses {taken}.";
            }
        }

        return null;
    }

    /// <summary>
    /// A key going down in a shortcut box, with the modifiers held: what it does, and the text to show for a preview or
    /// a chord. A modifier key counts as held itself, whatever the keyboard state said.
    /// </summary>
    public static (HotkeyPress Press, string Text) Record(uint key, HotkeyModifiers held)
    {
        HotkeyModifiers own = key switch
        {
            0x10 or 0xA0 or 0xA1 => HotkeyModifiers.Shift,
            0x11 or 0xA2 or 0xA3 => HotkeyModifiers.Ctrl,
            0x12 or 0xA4 or 0xA5 => HotkeyModifiers.Alt,
            0x5B or 0x5C => HotkeyModifiers.Win,
            _ => HotkeyModifiers.None,
        };
        return key switch
        {
            _ when own != HotkeyModifiers.None => (HotkeyPress.Preview, Preview(held | own)),
            0x09 => (HotkeyPress.Pass, ""),
            0x08 or 0x2E => (HotkeyPress.Clear, ""),
            0x1B => (HotkeyPress.Restore, ""),
            // A key that prints and parses back to itself: a letter, a digit, F1 to F24 or a named key.
            _ when key != 0 && KeyCode(KeyName(key)) == key => (HotkeyPress.Chord, new Hotkey(held, key).ToString()),
            _ => (HotkeyPress.Unusable, ""),
        };
    }

    /// <summary>Modifiers held with no key yet, as "Win+Alt+...".</summary>
    public static string Preview(HotkeyModifiers held) => Prefix(held) + "...";

    /// <summary>Pauses (or brings back) the hotkeys of the Tomte running on <paramref name="dataFolder"/>. False with none running.</summary>
    public static bool Pause(string dataFolder, bool on) => JobTask.Post(dataFolder, MsgPause, on ? 1u : 0u);

    /// <summary>
    /// Registers and releases the chord on the calling thread. Null when it is free; the conflict
    /// message when another app holds it (ERROR_HOTKEY_ALREADY_REGISTERED); otherwise a message with
    /// the error code. Do not probe a chord this process has registered itself: it reads as taken.
    /// </summary>
    public string? CheckFree(HotkeyAction action)
    {
        const int AlreadyRegistered = 1409;
        var mods = (HOT_KEY_MODIFIERS)Modifiers | HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        if (!PInvoke.RegisterHotKey(HWND.Null, ProbeId, mods, Key))
        {
            int error = Marshal.GetLastPInvokeError();
            return error == AlreadyRegistered
                ? ConflictMessage(action, ToString())
                : $"Tomte could not register {this} (error {error}). Pick another shortcut for {Name(action)}.";
        }

        PInvoke.UnregisterHotKey(HWND.Null, ProbeId);
        return null;
    }

    /// <summary>The canonical text, modifiers in the order Win, Ctrl, Alt, Shift.</summary>
    public override string ToString() => Prefix(Modifiers) + KeyName(Key);

    private static string Prefix(HotkeyModifiers m) =>
        (m.HasFlag(HotkeyModifiers.Win) ? "Win+" : "")
        + (m.HasFlag(HotkeyModifiers.Ctrl) ? "Ctrl+" : "")
        + (m.HasFlag(HotkeyModifiers.Alt) ? "Alt+" : "")
        + (m.HasFlag(HotkeyModifiers.Shift) ? "Shift+" : "");

    private static uint KeyCode(string name)
    {
        if (name.Length == 1 && char.IsAsciiLetterOrDigit(name[0]))
        {
            return char.ToUpperInvariant(name[0]);
        }

        if (name.Length is 2 or 3 && name[0] is 'F' or 'f' && int.TryParse(name.AsSpan(1), out int n) && n is >= 1 and <= 24)
        {
            return (uint)(0x70 + n - 1);
        }

        return NamedKeys.TryGetValue(name, out uint code) ? code : 0;
    }

    private static string KeyName(uint key) =>
        key is >= 0x70 and <= 0x87 ? $"F{key - 0x70 + 1}"
        : NamedKeys.FirstOrDefault(p => p.Value == key).Key ?? ((char)key).ToString();
}
