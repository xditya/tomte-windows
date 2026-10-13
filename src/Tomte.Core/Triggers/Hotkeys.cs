namespace Tomte.Core.Triggers;

public enum HotkeyAction
{
    Summon,
    Palette,
    CircleToAsk,
    Rambler,
    Scratchpad,
}

[Flags]
public enum ChordModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Win = 8,
}

/// <summary>A hotkey chord as RegisterHotKey wants it: modifier flags (same values as MOD_*) and a virtual key.</summary>
public readonly record struct Chord(ChordModifiers Modifiers, ushort VirtualKey)
{
    private const ChordModifiers WinAlt = ChordModifiers.Win | ChordModifiers.Alt;

    /// <summary>
    /// The defaults: Win+Alt plus a letter, which were found free of Windows, Game Bar and PowerToys.
    /// The Copilot key cannot be registered (the shell owns it), so the palette always gets Win+Alt+A and the
    /// Copilot key is a separate opt-in.
    /// </summary>
    public static readonly IReadOnlyDictionary<HotkeyAction, Chord> Defaults = new Dictionary<HotkeyAction, Chord>
    {
        [HotkeyAction.Summon] = new(WinAlt, 'I'),
        [HotkeyAction.Palette] = new(WinAlt, 'A'),
        [HotkeyAction.CircleToAsk] = new(WinAlt, 'C'),
        [HotkeyAction.Rambler] = new(WinAlt, 'V'),
        [HotkeyAction.Scratchpad] = new(WinAlt, 'N'),
    };

    public override string ToString()
    {
        var parts = new List<string>(5);
        if (Modifiers.HasFlag(ChordModifiers.Win))
        {
            parts.Add("Win");
        }

        if (Modifiers.HasFlag(ChordModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(ChordModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(ChordModifiers.Shift))
        {
            parts.Add("Shift");
        }

        parts.Add(VirtualKey is >= 'A' and <= 'Z' or >= '0' and <= '9' ? ((char)VirtualKey).ToString() : $"0x{VirtualKey:X2}");
        return string.Join('+', parts);
    }
}

/// <summary>What RegisterHotKey said for one action. A Win32 error of 1409 means another app holds the chord.</summary>
public readonly record struct HotkeyRegistration(HotkeyAction Action, Chord Chord, int Error)
{
    public const int AlreadyRegistered = 1409;

    public bool Registered => Error == 0;
}

public static class HotkeyConflicts
{
    /// <summary>
    /// Windows tells the user nothing when a chord is taken, so the island has to. One line per failed action,
    /// in the order they were registered; empty when all registered.
    /// </summary>
    public static IReadOnlyList<string> Describe(IEnumerable<HotkeyRegistration> results)
    {
        var lines = new List<string>();
        foreach (var r in results)
        {
            if (r.Registered)
            {
                continue;
            }

            string reason = r.Error == HotkeyRegistration.AlreadyRegistered ? "is used by another app" : $"could not be registered (error {r.Error})";
            lines.Add($"{r.Chord} ({Name(r.Action)}) {reason}. Rebind it in settings.");
        }

        return lines;
    }

    public static string Name(HotkeyAction action) => action switch
    {
        HotkeyAction.Summon => "Summon island",
        HotkeyAction.Palette => "AI palette",
        HotkeyAction.CircleToAsk => "Circle to ask",
        HotkeyAction.Rambler => "Rambler",
        HotkeyAction.Scratchpad => "Scratchpad",
        _ => action.ToString(),
    };
}
