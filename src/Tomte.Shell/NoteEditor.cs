using Tomte.Core;
using Tomte.Core.Text;
using Windows.System;

namespace Tomte.Shell;

/// <summary>
/// The scratchpad's text box bound to the note. The text services keep a copy of the text of their own, so the note
/// stays the one that decides: before each key the box takes in whatever changed the note since (a rambler note, an
/// AI write, another process's save), and after it the box's text goes to the note only if the note is still what
/// the box showed; otherwise the key is applied to the note as it is now and the box is brought there. Keys typed
/// before the first load has landed wait and go in after it, so the note is never typed over before it is read.
/// Saving stays the note's own, debounced. Island thread only, except <see cref="ScratchpadNote.Changed"/>, which is
/// marshalled here.
/// </summary>
internal sealed class NoteEditor : IDisposable
{
    private readonly EditorView editor;
    private readonly ScratchpadNote note;
    private readonly DispatcherQueue queue;
    private readonly NoteBinding binding;
    private readonly List<(uint Message, nuint WParam, nint LParam)> early = [];
    private bool open, applying, disposed;

    public NoteEditor(EditorView editor, ScratchpadNote note, DispatcherQueue queue)
    {
        this.editor = editor;
        this.note = note;
        this.queue = queue;
        binding = new NoteBinding(note);
        editor.BeforeKey = (message, wParam, lParam) =>
        {
            if (!open)
            {
                early.Add((message, wParam, lParam));
                return true;
            }

            Pull();
            return false;
        };
        editor.Services.Changed += OnTyped;
        note.Changed += OnNoteChanged;
        Open();
    }

    /// <summary>Whether the box shows the note; false until the note has loaded once.</summary>
    public bool IsOpen => open;

    /// <summary>The note has loaded: the box shows it with the caret where it was left, then takes what was typed before.</summary>
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
        foreach (var (message, wParam, lParam) in early)
        {
            editor.Key(message, wParam, lParam, out _);
        }

        early.Clear();
    }

    /// <summary>Where the caret was left goes to the note; nothing is listened to after this.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        note.Changed -= OnNoteChanged;
        editor.Services.Changed -= OnTyped;
        if (open)
        {
            binding.Typed(editor.Services.Text, editor.Services.Selection.End);
        }
    }

    // Raised on whichever thread changed the note.
    private void OnNoteChanged() => queue.TryEnqueue(() =>
    {
        if (open)
        {
            Pull();
        }
        else
        {
            Open();
        }
    });

    private void Pull()
    {
        if (!open || disposed || binding.Pull() is not { } change)
        {
            return;
        }

        applying = true;
        editor.Services.Replace(change);
        applying = false;
    }

    private void OnTyped()
    {
        if (applying || !open || disposed)
        {
            return;
        }

        var services = editor.Services;
        if (binding.Typed(services.Text, services.Selection.End) is { } now)
        {
            applying = true;
            services.Replace(Splice.Between(services.Text, now));
            applying = false;
        }
    }
}
