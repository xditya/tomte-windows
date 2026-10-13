namespace Tomte.Core.Rambler;

/// <summary>
/// Hold-to-talk on a registered Win+Alt+key chord. RegisterHotKey reports only the press, so the
/// release comes from raw keyboard input that <paramref name="armRawInput"/> turns on at the press
/// and off at the first key-up of the key, Alt or Win. No keyboard hook. A hold shorter than
/// <paramref name="minimumHoldMs"/> is a tap and ends as cancelled, so nothing is sent for it.
/// </summary>
public sealed class HoldDetector(Action<bool> armRawInput, ushort key = 'V', int minimumHoldMs = 300)
{
    private const ushort Alt = 0x12, LeftAlt = 0xA4, RightAlt = 0xA5, LeftWin = 0x5B, RightWin = 0x5C;
    private long pressedAtMs;

    public bool Held { get; private set; }

    public event Action? Started;

    /// <summary>True when the chord was held long enough to keep the recording.</summary>
    public event Action<bool>? Ended;

    /// <summary>WM_HOTKEY for the chord. A second press while held (auto-repeat) is ignored.</summary>
    public void Press(long nowMs)
    {
        if (Held)
            return;
        Held = true;
        pressedAtMs = nowMs;
        armRawInput(true);
        Started?.Invoke();
    }

    /// <summary>A raw-input key-up. Only the chord's own keys end the hold.</summary>
    public void KeyUp(ushort vk, long nowMs)
    {
        if (!Held || (vk != key && vk is not (Alt or LeftAlt or RightAlt or LeftWin or RightWin)))
            return;
        Held = false;
        armRawInput(false);
        Ended?.Invoke(nowMs - pressedAtMs >= minimumHoldMs);
    }
}
