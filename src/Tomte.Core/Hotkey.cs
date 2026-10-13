using System.Runtime.InteropServices;
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

/// <param name="Key">A virtual-key code.</param>
public readonly record struct Hotkey(HotkeyModifiers Modifiers, uint Key)
{
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
    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(HotkeyModifiers.Win)) parts.Add("Win");
        if (Modifiers.HasFlag(HotkeyModifiers.Ctrl)) parts.Add("Ctrl");
        if (Modifiers.HasFlag(HotkeyModifiers.Alt)) parts.Add("Alt");
        if (Modifiers.HasFlag(HotkeyModifiers.Shift)) parts.Add("Shift");
        parts.Add(KeyName(Key));
        return string.Join('+', parts);
    }

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
