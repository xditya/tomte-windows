namespace Tomte.Core.Text;

/// <summary>
/// How the windowless editor is scaled. The system's text services lay out in units that are pixels at the
/// process's system DPI. Drawn with the device context at that same DPI, one unit is one device pixel, and any other
/// monitor scale reaches them as a zoom: their extent is the client size over the zoom. Scaling any other way puts
/// glyphs and selection edges on a grid that moves with the surface's offset, so partial repaints would not match.
/// </summary>
public static class EditorScale
{
    /// <summary>A DIP is 1/96 inch, a twip 1/1440.</summary>
    public const int TwipsPerDip = 15;

    /// <summary>A HIMETRIC unit is 1/100 mm; an inch is 2540 of them.</summary>
    private const float HimetricPerInch = 2540;

    /// <summary>The zoom for a monitor's scale when the services' own DPI is <paramref name="systemDpi"/>.</summary>
    public static float Zoom(double monitorScale, uint systemDpi) => (float)(monitorScale * 96 / systemDpi);

    /// <summary>Whether a zoom is close enough to 1 that the services are better left at their own size.</summary>
    public static bool IsNone(float zoom) => MathF.Abs(zoom - 1) < 0.001f;

    /// <summary>The extent the host reports for a client length in pixels, in HIMETRIC, so the services zoom by <paramref name="zoom"/>.</summary>
    public static int Extent(int pixels, uint systemDpi, float zoom) => (int)MathF.Round(pixels * HimetricPerInch / (systemDpi * zoom));

    /// <summary>A BeginDraw offset in pixels as a translation in DIPs on a context set to the services' DPI.</summary>
    public static float Translation(int pixels, uint systemDpi) => pixels * 96f / systemDpi;

    /// <summary>A font size in DIPs as the services take it.</summary>
    public static int Twips(float dips) => (int)MathF.Round(dips * TwipsPerDip);
}

/// <summary>
/// The caret's blink as a compositor animation: one cycle is twice the system blink time (on, then off), repeated
/// until the system's caret timeout has passed, ending on. Null when the user turned blinking off.
/// </summary>
/// <param name="Iterations">0 for a caret that never stops blinking.</param>
public readonly record struct CaretBlink(TimeSpan Cycle, int Iterations)
{
    /// <param name="blinkMs">GetCaretBlinkTime: 0 or INFINITE when the caret does not blink.</param>
    /// <param name="timeoutMs">SPI_GETCARETTIMEOUT: how long the caret blinks after the last key, INFINITE for ever.</param>
    public static CaretBlink? Plan(uint blinkMs, uint timeoutMs)
    {
        if (blinkMs is 0 or uint.MaxValue)
        {
            return null;
        }

        uint cycle = 2 * blinkMs;
        int iterations = timeoutMs == uint.MaxValue ? 0 : Math.Max(1, (int)Math.Ceiling(timeoutMs / (double)cycle));
        return new CaretBlink(TimeSpan.FromMilliseconds(cycle), iterations);
    }
}

/// <summary>
/// Repaints at most once per turn of the message loop. What the services invalidate and where the caret was and
/// is are gathered here; the first rectangle since the last paint tells the caller to queue one paint, which takes
/// the union. Painting more often without returning to the loop leaves composition memory behind for good.
/// </summary>
public sealed class PaintGate
{
    private PixelRect? pending;

    public bool Pending => pending is not null;

    /// <summary>True when this is the first rectangle since the last <see cref="Take"/>: queue a paint then.</summary>
    public bool Invalidate(PixelRect rect)
    {
        if (rect.Width <= 0 || rect.Height <= 0)
        {
            return false;
        }

        bool first = pending is null;
        pending = pending is { } p ? Union(p, rect) : rect;
        return first;
    }

    /// <summary>The union since the last take, cut to the surface; null when there is nothing to paint.</summary>
    public PixelRect? Take(int width, int height)
    {
        var p = pending;
        pending = null;
        if (p is not { } r)
        {
            return null;
        }

        int left = Math.Clamp(r.X, 0, width), top = Math.Clamp(r.Y, 0, height);
        int right = Math.Clamp(r.X + r.Width, 0, width), bottom = Math.Clamp(r.Y + r.Height, 0, height);
        return right > left && bottom > top ? new PixelRect(left, top, right - left, bottom - top) : null;
    }

    public static PixelRect Union(PixelRect a, PixelRect b)
    {
        int left = Math.Min(a.X, b.X), top = Math.Min(a.Y, b.Y);
        int right = Math.Max(a.X + a.Width, b.X + b.Width), bottom = Math.Max(a.Y + a.Height, b.Y + b.Height);
        return new PixelRect(left, top, right - left, bottom - top);
    }
}

/// <summary>Text as the island's text boxes hold it.</summary>
public static class EditorText
{
    /// <summary>Every line break as one '\n': CR LF, a lone CR and RichEdit's soft break (VT) alike.</summary>
    public static string Lines(string text) =>
        text.IndexOf('\r') < 0 && text.IndexOf('\v') < 0 ? text : text.Replace("\r\n", "\n").Replace('\r', '\n').Replace('\v', '\n');

    /// <summary>
    /// A character typed before the note could be read, kept to go in once it can: Backspace takes the last one back,
    /// Enter is a new line, other control characters (Ctrl shortcuts) are not text and are left out.
    /// </summary>
    public static void Keep(System.Text.StringBuilder kept, char typed)
    {
        if (typed == '\b')
        {
            if (kept.Length > 0)
            {
                kept.Length--;
            }
        }
        else if (typed is '\r' or '\n')
        {
            kept.Append('\n');
        }
        else if (typed == '\t' || !char.IsControl(typed))
        {
            kept.Append(typed);
        }
    }
}

/// <summary>
/// Keys the island takes for itself. Alt+F4 always hides it, whatever has the keys: the island's window would close
/// and Tomte with it. Esc hides it only while a text box has the keys; otherwise it is a hotkey, or not the island's.
/// </summary>
public static class IslandKeys
{
    public const uint KeyDown = 0x0100, SysKeyDown = 0x0104;
    private const int Escape = 0x1B, F4 = 0x73;

    public static bool Hides(uint message, nuint wParam, bool boxHasKeys) =>
        (message == SysKeyDown && (int)(wParam & 0xFFFF) == F4) || (boxHasKeys && message == KeyDown && (int)(wParam & 0xFFFF) == Escape);
}

/// <summary>
/// The palette's prompt keys: Enter sends, Shift+Enter is a new line. A send swallows the Enter key and the carriage
/// return the message loop makes from it, so the prompt never gets the line break. Keys an input method is composing
/// with arrive as VK_PROCESSKEY, never as Enter, so committing a composition never sends. A held Enter sends once: its
/// autorepeat is swallowed, so it cannot also run a question that opens while the key is down.
/// </summary>
public sealed class PromptKeys
{
    public const uint KeyDown = 0x0100, Char = 0x0102;

    /// <summary>A key message's lParam bit 30: the key was already down, so this one is autorepeat.</summary>
    public const nint WasDown = 1 << 30;

    private const int Return = 0x0D;
    private bool swallowReturn;

    /// <summary>What to do with a key message: false passes it to the editor.</summary>
    public bool Sends(uint message, nuint wParam, bool shift, out bool swallow, nint lParam = 0)
    {
        swallow = false;
        if (message == KeyDown && (int)wParam == Return && !shift)
        {
            swallowReturn = swallow = true;
            return (lParam & WasDown) == 0;
        }

        // Ctrl+Enter makes a line feed instead of a carriage return.
        if (message == Char && (int)wParam is 0x0D or 0x0A && swallowReturn)
        {
            swallowReturn = false;
            swallow = true;
            return false;
        }

        if (message == KeyDown)
        {
            swallowReturn = false;
        }

        return false;
    }
}

/// <summary>What the note does with a key it takes from its box.</summary>
public enum NoteKey
{
    /// <summary>The box has it.</summary>
    Pass,

    /// <summary>One of the box's formatting shortcuts: nothing happens.</summary>
    Ignore,
    Copy,
    Cut,
    Paste,
}

/// <summary>
/// The note's keys while its box is rich text, so the note only ever looks like its Markdown: copy, cut and paste
/// carry plain text (rich text would bring a web page's fonts in and carry the box's white text into Word), and the
/// box's own formatting shortcuts (Ctrl+B, Ctrl+E, Ctrl+Shift+L, Ctrl+1 and the rest) do nothing. Ctrl with Alt is
/// AltGr on many layouts, which types, so it passes.
/// </summary>
public static class NoteKeys
{
    public const uint KeyDown = 0x0100;
    private const int Back = 0x08, Tab = 0x09, Return = 0x0D, Shift = 0x10, Control = 0x11, Menu = 0x12, Escape = 0x1B;
    private const int PageUp = 0x21, Down = 0x28, Insert = 0x2D, Delete = 0x2E, LeftShift = 0xA0, RightAlt = 0xA5, ProcessKey = 0xE5;

    public static NoteKey Of(uint message, nuint wParam, bool ctrl, bool shift, bool alt)
    {
        if (message != KeyDown || alt)
        {
            return NoteKey.Pass;
        }

        int key = (int)(wParam & 0xFFFF);
        if (!ctrl)
        {
            return !shift ? NoteKey.Pass : key == Insert ? NoteKey.Paste : key == Delete ? NoteKey.Cut : NoteKey.Pass;
        }

        return key switch
        {
            'C' or Insert => NoteKey.Copy,
            'X' => NoteKey.Cut,
            'V' => NoteKey.Paste,
            // Select all, redo, undo, the word and line keys, moving, and the modifiers themselves.
            'A' or 'Y' or 'Z' or Back or Tab or Return or Escape or Delete or (>= PageUp and <= Down) => NoteKey.Pass,
            Shift or Control or Menu or (>= LeftShift and <= RightAlt) or ProcessKey => NoteKey.Pass,
            _ => NoteKey.Ignore,
        };
    }
}

/// <summary>One replacement in a text: <see cref="Length"/> characters at <see cref="Start"/> become <see cref="Text"/>.</summary>
public readonly record struct Splice(int Start, int Length, string Text)
{
    public int End => Start + Length;

    public bool IsEmpty => Length == 0 && Text.Length == 0;

    /// <summary>The one replacement that turns <paramref name="from"/> into <paramref name="to"/>: what lies between their common start and end. Never splits a surrogate pair.</summary>
    public static Splice Between(string from, string to)
    {
        int max = Math.Min(from.Length, to.Length);
        int prefix = 0;
        while (prefix < max && from[prefix] == to[prefix])
        {
            prefix++;
        }

        if (prefix > 0 && prefix < from.Length && char.IsLowSurrogate(from[prefix]))
        {
            prefix--;
        }

        int suffix = 0;
        while (suffix < max - prefix && from[from.Length - 1 - suffix] == to[to.Length - 1 - suffix])
        {
            suffix++;
        }

        if (suffix > 0 && from.Length - suffix > prefix && char.IsHighSurrogate(from[from.Length - suffix - 1]))
        {
            suffix--;
        }

        return new Splice(prefix, from.Length - prefix - suffix, to.Substring(prefix, to.Length - prefix - suffix));
    }

    public string Apply(string text) => string.Concat(text.AsSpan(0, Start), Text, text.AsSpan(End));

    /// <summary>Where an index into the old text lands in the new one. An index inside the replaced part goes to its end; one at <see cref="Start"/> stays before what was inserted.</summary>
    public int Map(int index) => index <= Start ? index : index >= End ? index + Text.Length - Length : Start + Text.Length;
}

/// <summary>
/// Keeps an editor that holds its own copy of the text, as the system's text services do, in step with the
/// scratchpad note, which may change on a worker while the user types (a rambler note, an AI write, another
/// process's save). The editor never decides the note on its own: before a key it takes in what changed, and after a
/// key its text goes to the note only if the note is still what the editor last showed. If a change landed in
/// between, the key is applied to the note as it is now, so the change is kept, and the editor is brought there.
/// All on the island's thread.
/// </summary>
public sealed class NoteBinding(ScratchpadNote note)
{
    // The note's text as last seen, and the same with every line break one '\n', as the editor holds it.
    private string basis = "", shown = "";

    /// <summary>The note as the editor last showed it, every line break one '\n'.</summary>
    public string Shown => shown;

    /// <summary>The note's text and caret for an editor that is about to show it, every line break one '\n'.</summary>
    public (string Text, int Caret) Open()
    {
        basis = note.Text;
        shown = EditorText.Lines(basis);
        int caret = Math.Min(note.Caret, basis.Length);
        return (shown, EditorText.Lines(basis[..caret]).Length);
    }

    /// <summary>What the editor must replace to show the note as it is now; null when it already does.</summary>
    public Splice? Pull()
    {
        string now = note.Text;
        if (ReferenceEquals(now, basis) || now == basis)
        {
            return null;
        }

        string lines = EditorText.Lines(now);
        var change = Splice.Between(shown, lines);
        (basis, shown) = (now, lines);
        return change.IsEmpty ? null : change;
    }

    /// <summary>
    /// The editor's text and caret after the user typed. Returns null when the note took it as it is, else the text
    /// the editor must show now: the key applied to a note that changed meanwhile.
    /// </summary>
    public string? Typed(string text, int caret)
    {
        string? fixedText = null;
        while (!note.TryEdit(basis, text, caret))
        {
            string now = note.Text;
            string theirs = EditorText.Lines(now);
            string merged = Merge(shown, text, theirs);
            caret = Splice.Between(text, merged).Map(caret);
            (basis, shown, text, fixedText) = (now, theirs, merged, merged);
        }

        (basis, shown) = (text, text);
        return fixedText;
    }

    /// <summary>
    /// The user's change and another's, both made to <paramref name="basis"/>. When they touch different parts the
    /// result has both, the user's first where they meet; when they overlap the other's wins, because a rambler note or
    /// an AI write must never be lost and the user sees that the key did not land.
    /// </summary>
    public static string Merge(string basis, string mine, string theirs)
    {
        var my = Splice.Between(basis, mine);
        var other = Splice.Between(basis, theirs);
        if (my.IsEmpty)
        {
            return theirs;
        }

        if (my.End <= other.Start)
        {
            return my.Apply(theirs);
        }

        if (my.Start >= other.End)
        {
            return (my with { Start = my.Start + other.Text.Length - other.Length }).Apply(theirs);
        }

        return theirs;
    }
}
