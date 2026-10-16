namespace Tomte.Core.Triggers;

/// <summary>
/// The view closed last that can come back: what its pill says, how to bring it back, and how to let it go. Kept for the
/// undo time from the close; Undo, the time running out or a newer close ends it, and only an end that is not Undo lets
/// it go (a request it still runs is cancelled then). One at a time. Island thread only.
/// </summary>
public sealed class UndoClose
{
    /// <summary>Win+Alt+Z: Undo while the pill shows. Registered only then, and refused as a shortcut in Settings.</summary>
    public static readonly Hotkey Chord = new(HotkeyModifiers.Win | HotkeyModifiers.Alt, 'Z');

    private Kept? kept;

    /// <summary>What the pill says for the kept view; null with none.</summary>
    public string? Line => kept?.Line;

    /// <summary>The undo time a setting holds: 0 or less is off (null), up to 7 is 5 seconds, more is 10.</summary>
    public static TimeSpan? Time(int seconds) => seconds <= 0 ? null : TimeSpan.FromSeconds(seconds < 8 ? 5 : 10);

    /// <summary>
    /// A view closed. Kept for <paramref name="time"/> from <paramref name="now"/>, in place of the one kept before, which
    /// lets go; with no time (undo off) it lets go at once. True when it is kept.
    /// </summary>
    public bool Keep(string line, Func<bool> restore, Action free, DateTimeOffset now, TimeSpan? time)
    {
        End();
        if (time is not { } length)
        {
            free();
            return false;
        }

        kept = new Kept(line, restore, free, now + length);
        return true;
    }

    /// <summary>A view is kept and its time is not up.</summary>
    public bool Live(DateTimeOffset now) => kept is { } k && now < k.Ends;

    /// <summary>What is left of the undo time; zero with nothing kept.</summary>
    public TimeSpan Left(DateTimeOffset now) => kept is { } k && now < k.Ends ? k.Ends - now : TimeSpan.Zero;

    /// <summary>
    /// Undo: the kept view comes back and is no longer kept. One that could not come back, or whose time is up, lets go.
    /// True when it came back.
    /// </summary>
    public bool Undo(DateTimeOffset now)
    {
        if (kept is not { } k || now >= k.Ends)
        {
            End();
            return false;
        }

        kept = null;
        if (k.Restore())
        {
            return true;
        }

        k.Free();
        return false;
    }

    /// <summary>The time is up, or the kept view is wanted no more: it lets go. Nothing kept, nothing happens.</summary>
    public void End()
    {
        var k = kept;
        kept = null;
        k?.Free();
    }

    private sealed record Kept(string Line, Func<bool> Restore, Action Free, DateTimeOffset Ends);
}
