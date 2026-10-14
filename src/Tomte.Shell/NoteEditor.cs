using System.Text;
using Tomte.Core;
using Tomte.Core.Text;
using Windows.System;

namespace Tomte.Shell;

/// <summary>
/// The scratchpad's text box bound to the note. The text services keep a copy of the text of their own, so the note
/// stays the one that decides: before each key the box takes in whatever changed the note since (a rambler note, an
/// AI write, another process's save), and the box's text goes to the note only if the note is still what the box
/// showed; otherwise the user's change is applied to the note as it is now and the box is brought there. A change
/// is taken on the next turn of the loop, never inside the services' own notification, and always before the next
/// key or note change is handled. What is typed before the note has been read waits in <c>kept</c> (the panel's, so
/// a load that fails does not lose it) and goes in once the note opens. Saving stays the note's own, debounced.
/// Island thread only, except <see cref="ScratchpadNote.Changed"/>, which is marshalled here.
/// </summary>
internal sealed class NoteEditor : IDisposable
{
    private const uint Char = 0x0102;
    private readonly EditorView editor;
    private readonly ScratchpadNote note;
    private readonly DispatcherQueue queue;
    private readonly StringBuilder kept;
    private readonly NoteBinding binding;
    private bool open, typed, applying, disposed;

    public NoteEditor(EditorView editor, ScratchpadNote note, DispatcherQueue queue, StringBuilder kept)
    {
        this.editor = editor;
        this.note = note;
        this.queue = queue;
        this.kept = kept;
        binding = new NoteBinding(note);
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
            return false;
        };
        editor.Services.Changed += OnTyped;
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

    // Inside the services' change notification: only note that the box changed, and take it on the next turn.
    private void OnTyped()
    {
        if (applying || !open || disposed || typed)
        {
            return;
        }

        typed = true;
        queue.TryEnqueue(Sync);
    }

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
