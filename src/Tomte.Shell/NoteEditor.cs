using System.Text;
using Tomte.Core;
using Tomte.Core.Text;
using Windows.System;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;
using Windows.Win32.UI.Input.KeyboardAndMouse;

namespace Tomte.Shell;

/// <summary>
/// The scratchpad's text box bound to the note. The text services keep a copy of the text of their own, so the note
/// stays the one that decides: before each key the box takes in whatever changed the note since (a rambler note, an
/// AI write, another process's save), and the box's text goes to the note only if the note is still what the box
/// showed; otherwise the user's change is applied to the note as it is now and the box is brought there. A change
/// is taken on the next turn of the loop, never inside the services' own notification, and always before the next
/// key or note change is handled. What is typed before the note has been read waits in <c>kept</c> (the panel's, so
/// a load that fails does not lose it) and goes in once the note opens. Saving stays the note's own, debounced.
/// The box is rich text and shows the note's Markdown (<see cref="NoteStyle"/>): before each paint the lines that
/// changed are styled again, never while an input method composes. The text stays what was typed; copy, cut and
/// paste carry plain text and the box's formatting shortcuts do nothing (<see cref="NoteKeys"/>).
/// Island thread only, except <see cref="ScratchpadNote.Changed"/>, which is marshalled here.
/// </summary>
internal sealed unsafe class NoteEditor : IDisposable
{
    private const uint Char = 0x0102, UnicodeText = 13;

    // Lines styled at once around the caret, and then per paint at low priority: about 4 and 2 ms (measured offscreen).
    private const int AtOnce = 300, Piece = 100;
    private readonly EditorView editor;
    private readonly ScratchpadNote note;
    private readonly DispatcherQueue queue;
    private readonly StringBuilder kept;
    private readonly NoteBinding binding;
    private readonly NoteStyle style;

    // Line ranges (from a line's start) not styled yet since the note opened, kept in step with every edit.
    private readonly List<(int From, int To)> unstyled = [];

    // The box's text as it was last styled; null until the first styling after the note opens.
    private string? styled;
    private bool open, typed, applying, restyle, styling, disposed;

    public NoteEditor(EditorView editor, ScratchpadNote note, DispatcherQueue queue, StringBuilder kept)
    {
        this.editor = editor;
        this.note = note;
        this.queue = queue;
        this.kept = kept;
        binding = new NoteBinding(note);
        // While the box is still empty.
        editor.Services.Rich = true;
        style = new NoteStyle(editor.Services);
        editor.BeforeKey = (message, wParam, _) =>
        {
            if (!open)
            {
                if (message == Char)
                {
                    EditorText.Keep(kept, (char)wParam);
                }

                return true;
            }

            Sync();
            var key = NoteKeys.Of(message, wParam, Down(VIRTUAL_KEY.VK_CONTROL), Down(VIRTUAL_KEY.VK_SHIFT), Down(VIRTUAL_KEY.VK_MENU));
            if (key == NoteKey.Pass)
            {
                return false;
            }

            if (key is NoteKey.Indent or NoteKey.Outdent)
            {
                Indent(key == NoteKey.Outdent);
            }
            else if (key != NoteKey.Ignore)
            {
                Clipboard(key);
            }

            return true;
        };
        editor.BeforePaint = Restyle;
        editor.Services.Changed += OnTyped;
        editor.Services.Composed += OnComposed;
        note.Changed += OnNoteChanged;
        Open();
    }

    /// <summary>Whether the box shows the note; false until the note has been read once.</summary>
    public bool IsOpen => open;

    /// <summary>The note has been read: the box shows it with the caret where it was left, then takes what was kept.</summary>
    public void Open()
    {
        if (open || disposed || !note.IsLoaded)
        {
            return;
        }

        var (text, caret) = binding.Open();
        applying = true;
        editor.Services.Text = text;
        editor.Services.Selection = (caret, caret);
        applying = false;
        open = true;
        (styled, restyle) = (null, true);
        foreach (char c in kept.ToString())
        {
            editor.Key(Char, c == '\n' ? '\r' : c, 1, out _);
        }

        kept.Clear();
    }

    /// <summary>What was typed and where the caret was left go to the note; nothing is listened to after this.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        if (open)
        {
            Sync();
            binding.Typed(editor.Services.Text, editor.Services.Selection.End);
        }

        disposed = true;
        note.Changed -= OnNoteChanged;
        editor.Services.Changed -= OnTyped;
        editor.Services.Composed -= OnComposed;
        editor.BeforePaint = null;
        style.Dispose();
    }

    // Raised on whichever thread changed the note.
    private void OnNoteChanged() => queue.TryEnqueue(() =>
    {
        if (open)
        {
            Sync();
        }
        else
        {
            Open();
        }
    });

    // Inside the services' change notification: only note that the box changed, and take it on the next turn. Styling
    // changes no text, so what it notifies is not a change.
    private void OnTyped()
    {
        if (styling)
        {
            return;
        }

        restyle = true;
        if (applying || !open || disposed || typed)
        {
            return;
        }

        typed = true;
        queue.TryEnqueue(Sync);
    }

    /// <summary>The island's shape is moving: styling waits, as it would drop frames of the morph.</summary>
    public Func<bool>? Moving { get; set; }

    /// <summary>Pieces of the note not styled yet since it opened; 0 once all of it is.</summary>
    public int Unstyled => unstyled.Count;

    /// <summary>The island came to rest: what waited is styled now.</summary>
    public void Settled() => editor.Repaint();

    // Before each paint: the lines that changed since the last styling get their look, so what is drawn never shows a
    // character in the look it took from the one before it. Not while an input method composes (the composition keeps
    // its own look, and its end styles what it left) and not while the island moves (its rest does). A note that opens
    // gets the lines around the caret at once and the rest a piece per paint at low priority, so no paint takes long.
    private void Restyle()
    {
        var services = editor.Services;
        if (!open || disposed || services.Composing || Moving?.Invoke() == true)
        {
            return;
        }

        if (styled is not null && services.Zoom != style.Zoom)
        {
            // Another monitor scale: the indents are set for it, the rest with them.
            (styled, restyle) = (null, true);
        }

        if (restyle)
        {
            restyle = false;
            string now = services.Text;
            if (styled is null)
            {
                var (from, to) = NoteMarkdown.Around(now, services.Selection.End, AtOnce);
                unstyled.Clear();
                if (from > 0)
                {
                    unstyled.Add((0, from - 1));
                }

                if (to < now.Length)
                {
                    unstyled.Add((to + 1, now.Length));
                }

                Apply(now, from, to);
                // Lines that grew or shrank must not leave the caret out of view.
                services.Send(PInvoke.EM_SCROLLCARET);
            }
            else
            {
                if (unstyled.Count > 0)
                {
                    var splice = Splice.Between(styled, now);
                    for (int i = unstyled.Count - 1; i >= 0; i--)
                    {
                        var (from, to) = (splice.Map(unstyled[i].From), splice.Map(unstyled[i].To));
                        if (from > to || from > now.Length)
                        {
                            unstyled.RemoveAt(i);
                        }
                        else
                        {
                            unstyled[i] = (from, to);
                        }
                    }
                }

                if (NoteMarkdown.Changed(styled, now) is var (start, end))
                {
                    // A fence restyles every line after it: the first ones now, the rest a piece at a time.
                    var first = NoteMarkdown.Lines(now, start, AtOnce);
                    if (first.To < end)
                    {
                        unstyled.Insert(0, (first.To + 1, end));
                    }

                    Apply(now, start, Math.Min(first.To, end));
                }
            }

            styled = now;
        }
        else if (unstyled.Count > 0)
        {
            var (from, to) = unstyled[0];
            var piece = NoteMarkdown.Lines(styled!, from, Piece);
            Apply(styled!, piece.From, piece.To);
            if (piece.To >= to)
            {
                unstyled.RemoveAt(0);
            }
            else
            {
                unstyled[0] = (piece.To + 1, to);
            }
        }

        if (unstyled.Count > 0)
        {
            editor.Repaint(idle: true);
        }
    }

    private void Apply(string text, int from, int to)
    {
        styling = true;
        try
        {
            style.Apply(text, from, to);
        }
        finally
        {
            styling = false;
        }
    }

    // Raised inside the services' notification: styled in the next paint.
    private void OnComposed()
    {
        restyle = true;
        editor.Repaint();
    }

    // Copy, cut and paste of plain text only, whatever is on the clipboard or selected; line breaks on the clipboard as
    // Windows has them.
    private void Clipboard(NoteKey key)
    {
        var services = editor.Services;
        if (key == NoteKey.Paste)
        {
            // The clipboard's Unicode text, put in as the user's own insertion: never read as RTF or any other format.
            if (Get(services.Window) is { } pasted)
            {
                services.Insert(pasted);
            }

            return;
        }

        // A selection of all of it reaches past the text, onto rich text's own last paragraph mark.
        string text = services.Text;
        var (start, end) = services.Selection;
        (start, end) = (Math.Min(start, text.Length), Math.Min(end, text.Length));
        bool copied = start != end && Put(services.Window, text[start..end].Replace("\n", "\r\n"));
        if (copied && key == NoteKey.Cut)
        {
            fixed (char* nothing = "")
            {
                // Undoable, as the box's own cut is.
                services.Send(PInvoke.EM_REPLACESEL, 1, (nint)nothing);
            }
        }
    }

    // Tab and Shift+Tab, as the user's own edit (one undo step), with the selection kept on what it was on.
    private void Indent(bool outdent)
    {
        var services = editor.Services;
        var (start, end) = services.Selection;
        if (NoteMarkdown.Indent(services.Text, start, end, outdent) is not var (change, from, to))
        {
            return;
        }

        services.Selection = (change.Start, change.End);
        services.Insert(change.Text);
        services.Selection = (from, to);
    }

    private static bool Put(HWND owner, string text)
    {
        if (!PInvoke.OpenClipboard(owner))
        {
            return false;
        }

        try
        {
            PInvoke.EmptyClipboard();
            var memory = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)(text.Length + 1) * 2);
            if (memory.IsNull)
            {
                return false;
            }

            char* p = (char*)PInvoke.GlobalLock(memory);
            text.CopyTo(new Span<char>(p, text.Length));
            p[text.Length] = '\0';
            PInvoke.GlobalUnlock(memory);
            // On success the clipboard owns the memory; otherwise it is still ours to free.
            if (PInvoke.SetClipboardData(UnicodeText, (HANDLE)memory.Value).IsNull)
            {
                PInvoke.GlobalFree(memory);
                return false;
            }

            return true;
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    private static string? Get(HWND owner)
    {
        if (!PInvoke.OpenClipboard(owner))
        {
            return null;
        }

        try
        {
            var data = (HGLOBAL)PInvoke.GetClipboardData(UnicodeText).Value;
            char* p = data.IsNull ? null : (char*)PInvoke.GlobalLock(data);
            if (p is null)
            {
                return null;
            }

            try
            {
                // Up to the terminating null, never past the memory's own size.
                var text = new ReadOnlySpan<char>(p, (int)Math.Min(int.MaxValue, PInvoke.GlobalSize(data) / 2));
                int end = text.IndexOf('\0');
                return (end < 0 ? text : text[..end]).ToString();
            }
            finally
            {
                PInvoke.GlobalUnlock(data);
            }
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    private static bool Down(VIRTUAL_KEY key) => PInvoke.GetKeyState((int)key) < 0;

    // The box's change goes to the note first, then what changed the note comes into the box.
    private void Sync()
    {
        if (!open || disposed)
        {
            return;
        }

        var services = editor.Services;
        if (typed)
        {
            typed = false;
            if (binding.Typed(services.Text, services.Selection.End) is { } now)
            {
                Apply(Splice.Between(services.Text, now));
            }
        }

        if (binding.Pull() is { } change)
        {
            Apply(change);
        }
    }

    private void Apply(Splice change)
    {
        applying = true;
        editor.Services.Replace(change);
        applying = false;
    }
}
