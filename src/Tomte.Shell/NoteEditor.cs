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
    private readonly EditorView editor;
    private readonly ScratchpadNote note;
    private readonly DispatcherQueue queue;
    private readonly StringBuilder kept;
    private readonly NoteBinding binding;
    private readonly NoteStyle style;

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

            if (key != NoteKey.Ignore)
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

    // Before each paint: the lines that changed since the last styling get their look, so what is drawn never shows a
    // character in the look it took from the one before it. Not while an input method composes: the composition keeps
    // its own look, and its end styles what it left.
    private void Restyle()
    {
        var services = editor.Services;
        if (!open || disposed || services.Composing)
        {
            return;
        }

        if (styled is not null && services.Zoom != style.Zoom)
        {
            // Another monitor scale: the indents are set for it, the rest with them.
            (styled, restyle) = (null, true);
        }

        if (!restyle)
        {
            return;
        }

        restyle = false;
        // the first styling after the note opens takes all of it, about 50 ms for 2,000 lines on the island's
        // thread; style the lines in view first if that ever shows in the panel's morph.
        string now = services.Text;
        if ((styled is null ? (0, now.Length) : NoteMarkdown.Changed(styled, now)) is var (from, to))
        {
            styling = true;
            try
            {
                style.Apply(now, from, to);
            }
            finally
            {
                styling = false;
            }
        }

        styled = now;
    }

    // Raised inside the services' notification: styled on the next turn.
    private void OnComposed()
    {
        restyle = true;
        queue.TryEnqueue(Restyle);
    }

    // Copy, cut and paste of plain text only, whatever is on the clipboard or selected; line breaks on the clipboard as
    // Windows has them.
    private void Clipboard(NoteKey key)
    {
        var services = editor.Services;
        if (key == NoteKey.Paste)
        {
            services.Send(PInvoke.EM_PASTESPECIAL, UnicodeText);
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
