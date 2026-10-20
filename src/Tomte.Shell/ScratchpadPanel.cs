using System.Drawing;
using System.Numerics;
using System.Text;
using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Text;
using Windows.UI.Composition;

namespace Tomte.Shell;

/// <summary>
/// The Scratchpad tab: a heading with a Preview and Edit switch, the card the note is shown or typed in, and a footer,
/// as composition visuals in DIPs laid out by <see cref="ScratchpadPanelLayout"/>.
/// In Preview the note is drawn as its Markdown reads (<see cref="NotePreviewLayout"/>) and scrolls with the wheel; its
/// task boxes are targets that tick on a click or Space, rewriting only that line's box. A click on its text, Enter, or a
/// typed character goes to Edit with the caret at that place. In Edit the card holds a text box bound to the note
/// (<see cref="NoteEditor"/>); Esc, a click outside the card, or the switch goes back to Preview. Ctrl+E switches either
/// way. The mode chosen holds for the session; until one is, a note with words in it shows in Preview and an empty one in
/// Edit. The scratchpad hotkey always lands in Edit.
/// The note loads on a worker, so <see cref="Shown"/> returns at once and what is typed before the first load goes in
/// once it lands. A load that fails says so in the card and keeps what was typed for the next show that can read the
/// note. Leaving the tab saves what is unsaved and releases the text box.
/// </summary>
internal sealed class ScratchpadPanel(IslandWindow island, ScratchpadNote note, TextRenderer text, TypingFocus focus, Action<string>? log) : IPanel, IInteractive
{
    private const string NoteId = "note";
    private const uint Char = 0x0102;

    // Typed while the note could not be read yet; kept across shows until it can be.
    private readonly StringBuilder kept = new();
    private ScratchpadPanelLayout? layout;
    private ContainerVisual? host;
    private Parts? chrome, shown;
    private ContainerVisual? scrolled;
    private NotePreviewLayout? preview;
    private TextVisual? notice;
    private EditorView? editor;
    private NoteEditor? bound;
    private float scroll;
    private int generation;
    private bool editing, pendingEdit;

    // The mode last chosen this session; null until the user picks one.
    private bool? chosen;

    public PanelId Id => PanelId.Scratchpad;

    public IReadOnlyList<Target> Targets { get; private set; } = [];

    public event Action? TargetsChanged;

    public void Shown(ContainerVisual body, Vector2 size)
    {
        layout = new ScratchpadPanelLayout(size);
        host = body;
        scroll = 0;
        editing = pendingEdit || (chosen ?? (note.IsLoaded && note.Text.Length == 0));
        chosen = pendingEdit ? true : chosen;
        BuildChrome();
        if (editing)
        {
            OpenEditor();
        }
        else
        {
            BuildPreview();
        }

        note.Changed += NoteChanged;
        focus.Typed = TypedInPreview;
        Load();
        // Switched or come back to while the island has the keys for its targets (not on the tab labels, where the arrows
        // go on along the tabs): the note is the tab's first useful thing. The scratchpad hotkey that came before the tab
        // showed gets it too.
        int shownAs = generation;
        bool wanted = pendingEdit;
        pendingEdit = false;
        island.Queue.TryEnqueue(() =>
        {
            if (shownAs == generation && editor is not null && (wanted || (focus.HasKeys && focus.Focused is null && !island.Interaction.HasFocus)))
            {
                focus.Take(editor);
            }
        });
        Retarget();
    }

    public void Hidden()
    {
        generation++;
        note.Changed -= NoteChanged;
        if (focus.Typed == TypedInPreview)
        {
            focus.Typed = null;
        }

        CloseEditor(keepKeys: false);
        ClosePreview();
        chrome?.Dispose();
        notice?.Dispose();
        (chrome, notice, layout, host) = (null, null, null, null);
        Targets = [];
        TargetsChanged?.Invoke();
        Observe(note.FlushAsync(), "save the note");
    }

    /// <summary>
    /// The scratchpad hotkey (<paramref name="edit"/>): the note in Edit takes the keys with the caret where it was left.
    /// The summon hotkey: the note takes the keys only if it is already in Edit; in Preview its targets have them.
    /// </summary>
    public void TakeKeys(bool edit = true)
    {
        if (layout is null)
        {
            pendingEdit |= edit;
            return;
        }

        if (edit)
        {
            SetMode(true);
        }

        if (editing && editor is not null)
        {
            focus.Take(editor);
        }
    }

    public void Activate(string id)
    {
        if (id == NoteId)
        {
            // Focus reached the note with the keys: it takes them. Never on its own, so the island takes no foreground here.
            if (editor is not null && focus.HasKeys)
            {
                focus.Take(editor);
            }
        }
        else if (id == ScratchpadPanelLayout.PreviewId)
        {
            SetMode(false);
        }
        else if (id == ScratchpadPanelLayout.EditId)
        {
            EditAt(null);
        }
        else if (NotePreviewLayout.TaskLine(id) is int line && note.IsLoaded)
        {
            note.ToggleTask(line);
        }
    }

    /// <summary>Enter on a task box goes to Edit at the end of its line; Space falls through and ticks it.</summary>
    public bool KeyDown(string id, NavKey key, bool repeat)
    {
        if (key != NavKey.Activate)
        {
            return false;
        }

        if (NotePreviewLayout.TaskLine(id) is int line && note.IsLoaded)
        {
            string now = note.Text;
            int end = now.IndexOf('\n', Math.Min(line, now.Length));
            EditAt(end < 0 ? now.Length : end > 0 && now[end - 1] == '\r' ? end - 1 : end);
            return true;
        }

        // The switch's own Enter, before the panel's Enter below takes it.
        if (id is ScratchpadPanelLayout.PreviewId or ScratchpadPanelLayout.EditId)
        {
            Activate(id);
            return true;
        }

        return false;
    }

    /// <summary>Ctrl+E switches; Enter with nothing focused goes from Preview to Edit.</summary>
    public bool Command(NavKey key)
    {
        if (layout is null || (key != NavKey.Edit && !(key == NavKey.Activate && !editing && !island.Interaction.HasFocus)))
        {
            return false;
        }

        // From the box's own key handling: switch on the next turn, once that handling is done with the box.
        int shownAs = generation;
        bool edit = !editing;
        island.Queue.TryEnqueue(() =>
        {
            if (shownAs != generation)
            {
                return;
            }

            if (edit)
            {
                EditAt(null, takeFront: false);
            }
            else
            {
                SetMode(false);
            }
        });
        return true;
    }

    /// <summary>Keyboard focus on a task box scrolled out of the card brings it into view.</summary>
    public void Hovered(string? id)
    {
        if (id is null || editing || preview is null || layout is null || NotePreviewLayout.TaskLine(id) is not int line || !island.Interaction.IsFocus(this, id))
        {
            return;
        }

        foreach (var task in preview.Tasks.Where(t => t.Line == line))
        {
            float band = layout.Editor.Height, top = task.Box.Y - 4, bottom = task.Box.Bottom + 4;
            float to = top < scroll ? top : bottom > scroll + band ? bottom - band : scroll;
            if (to != scroll)
            {
                ScrollTo(to);
            }
        }
    }

    // In Edit, a click into the card's padding gives the note the keys, and one outside the card goes back to Preview.
    // In Preview, a click on the note goes to Edit with the caret at the character clicked.
    public void Press(Vector2 point)
    {
        if (layout is null)
        {
            return;
        }

        bool inCard = layout.Note.Contains(point.X, point.Y);
        if (editing)
        {
            if (editor is not null && inCard && !editor.Box.Contains(point.X, point.Y))
            {
                focus.Take(editor);
            }
            else if (!inCard)
            {
                SetMode(false);
            }

            return;
        }

        if (!inCard || !note.IsLoaded)
        {
            return;
        }

        var box = layout.Editor;
        int caret = preview?.SourceAt(new Vector2(point.X - box.X, point.Y - box.Y + scroll), note.Text.Length) ?? note.Text.Length;
        // On the next turn, so the text box made for it does not also take this same press.
        int shownAs = generation;
        island.Queue.TryEnqueue(() =>
        {
            if (shownAs == generation)
            {
                EditAt(caret);
            }
        });
    }

    public void Wheel(Vector2 point, int delta)
    {
        if (editing)
        {
            if (editor is not null && editor.Box.Contains(point.X, point.Y))
            {
                editor.Wheel(delta);
            }
        }
        else if (layout is not null && preview is not null && layout.Note.Contains(point.X, point.Y))
        {
            ScrollTo(scroll - delta / 120f * NotePreviewLayout.WheelStep);
        }
    }

    // A typed character while the note shows: Edit, and the character goes in at the caret.
    private bool TypedInPreview(char c)
    {
        if (editing || layout is null || !note.IsLoaded)
        {
            return false;
        }

        EditAt(null, takeFront: false);
        if (editor is null || focus.Focused != editor)
        {
            return false;
        }

        editor.Key(Char, c, 1, out _);
        return true;
    }

    // Edit, the caret at caret (a place in the note) or where it was left, and the keys to the box.
    private void EditAt(int? caret, bool takeFront = true)
    {
        if (layout is null)
        {
            return;
        }

        if (caret is int at && note.IsLoaded && !editing)
        {
            note.Edit(note.Text, at);
        }

        SetMode(true);
        if (editor is not null && (takeFront || focus.HasKeys))
        {
            focus.Take(editor);
        }
    }

    private void SetMode(bool edit)
    {
        if (layout is null)
        {
            return;
        }

        chosen = edit;
        if (edit == editing)
        {
            return;
        }

        editing = edit;
        if (edit)
        {
            ClosePreview();
            OpenEditor();
        }
        else
        {
            CloseEditor(keepKeys: true);
            BuildPreview();
        }

        BuildChrome();
        Retarget();
    }

    private void OpenEditor()
    {
        if (host is null || layout is null)
        {
            return;
        }

        var box = focus.Create(host, multiline: true, ScratchpadPanelLayout.NoteSize);
        editor = box;
        box.Place(layout.Editor, island.Scale);
        // Esc leaves the note for Preview, with focus on the tab's label; a second Esc closes the island as before.
        box.Escape = () =>
        {
            if (!focus.TakeKeys())
            {
                return false;
            }

            island.Interaction.FocusTab();
            int shownAs = generation;
            island.Queue.TryEnqueue(() =>
            {
                if (shownAs == generation && editor == box)
                {
                    SetMode(false);
                }
            });
            return true;
        };
        // The note is styled once the Panels morph is at rest, never during it.
        bound = new NoteEditor(box, note, island.Queue, kept) { Moving = () => island.Moving };
        island.Settled += bound.Settled;
    }

    // What was typed goes to the note. With keepKeys, a box that had the keys hands them to the targets rather than
    // back to the app the user came from.
    private void CloseEditor(bool keepKeys)
    {
        if (editor is null)
        {
            return;
        }

        if (keepKeys && focus.Focused == editor)
        {
            focus.TakeKeys();
        }

        if (bound is not null)
        {
            island.Settled -= bound.Settled;
            bound.Dispose();
        }

        editor.Dispose();
        (editor, bound) = (null, null);
    }

    // The note as it reads now, its runs and task boxes in a container that scrolls inside the text box's place.
    private void BuildPreview()
    {
        ClosePreview();
        if (host is null || layout is null)
        {
            return;
        }

        var place = layout.Editor;
        var p = shown = new Parts(island.Compositor, text);
        var band = p.Attach(host, island.Compositor.CreateContainerVisual());
        band.Offset = new Vector3(place.X, place.Y, 0);
        band.Size = new Vector2(place.Width, place.Height);
        band.Clip = p.Keep(island.Compositor.CreateInsetClip());
        scrolled = p.Attach(band, island.Compositor.CreateContainerVisual());
        if (!note.IsLoaded)
        {
            preview = null;
            return;
        }

        string now = note.Text;
        if (now.Trim().Length == 0)
        {
            preview = null;
            scroll = 0;
            p.Text(scrolled, new TextSlot(ScratchpadPanelLayout.Empty, NotePreviewLayout.Style with { Color = Theme.Ink3 }, new RectangleF(0, 0, place.Width, 21)));
            return;
        }

        var laid = preview = new NotePreviewLayout(now, place.Width);
        var shapes = p.Shapes(scrolled, new RectangleF(0, 0, place.Width, Math.Max(1, laid.Height)));
        foreach (var task in laid.Tasks)
        {
            if (task.Done)
            {
                p.Box(shapes, task.Box, DropResultLayout.TaskCorner, p.Brush(Theme.Accent));
                const float tick = DropResultLayout.TaskTick;
                p.Glyph(shapes, Glyphs.Check, ShelfColors.Of(Theme.OnAccent), new RectangleF(task.Box.X + (task.Box.Width - tick) / 2, task.Box.Y + (task.Box.Height - tick) / 2, tick, tick));
            }
            else
            {
                p.Box(shapes, task.Box, DropResultLayout.TaskCorner, null, p.Brush(Theme.Silence), 1.5f);
            }
        }

        foreach (var run in laid.Runs)
        {
            p.Text(scrolled, run.Run, run.At);
        }

        scroll = Math.Clamp(scroll, 0, laid.MaxScroll(place.Height));
        scrolled.Offset = new Vector3(0, -scroll, 0);
    }

    private void ClosePreview()
    {
        shown?.Dispose();
        (shown, scrolled, preview) = (null, null, null);
    }

    private void ScrollTo(float to)
    {
        if (layout is null || preview is null || scrolled is null || shown is null)
        {
            return;
        }

        scroll = Math.Clamp(to, 0, preview.MaxScroll(layout.Editor.Height));
        scrolled.Offset = new Vector3(0, -scroll, 0);
        // Back on whole pixels at the new offset.
        foreach (var run in shown.Texts)
        {
            run.Refresh();
        }

        Retarget();
    }

    // The note changed while it shows: drawn again, scrolled where it was. Raised on whichever thread changed it.
    private void NoteChanged() => island.Queue.TryEnqueue(() =>
    {
        if (!editing && layout is not null)
        {
            BuildPreview();
            Retarget();
        }
    });

    // The card, the heading and footer for the mode shown, and the switch with the current mode raised, under the rest.
    private void BuildChrome()
    {
        chrome?.Dispose();
        if (host is null || layout is null)
        {
            return;
        }

        var p = chrome = new Parts(island.Compositor, text);
        var v = p.Shapes(host, new RectangleF(0, 0, layout.Body.X, layout.Body.Y));
        host.Children.Remove(v);
        host.Children.InsertAtBottom(v);
        p.Box(v, layout.Note, ScratchpadPanelLayout.Corner, p.Brush(Theme.Card));
        var (track, previewSegment, editSegment) = layout.Segments;
        p.Capsule(v, track, p.Brush(Theme.Card));
        p.Capsule(v, editing ? editSegment : previewSegment, p.Brush(Theme.RaisedHi));
        p.Text(host, new TextSlot(ScratchpadPanelLayout.PreviewLabel, ScratchpadPanelLayout.SegmentStyle(!editing), previewSegment));
        p.Text(host, new TextSlot(ScratchpadPanelLayout.EditLabel, ScratchpadPanelLayout.SegmentStyle(editing), editSegment));
        foreach (var line in layout.FrameText(editing))
        {
            p.Text(host, line);
        }
    }

    // In Edit the note's box and then the switch; in Preview each task box that shows, then the switch. A box scrolled out
    // of the card stays in the Tab order with no area, so the pointer cannot reach it behind the heading or footer, and
    // focus on it scrolls it into view (Hovered).
    private void Retarget()
    {
        if (layout is null)
        {
            return;
        }

        var (_, previewSegment, editSegment) = layout.Segments;
        Target[] modes =
        [
            new(ScratchpadPanelLayout.PreviewId, previewSegment, previewSegment.Height / 2, TargetKind.Toggle, Tip: "Show the note", Checked: !editing, Key: "Ctrl+E"),
            new(ScratchpadPanelLayout.EditId, editSegment, editSegment.Height / 2, TargetKind.Toggle, Tip: "Edit the note", Checked: editing, Key: "Ctrl+E"),
        ];
        var all = new List<Target>();
        if (editing)
        {
            all.Add(new(NoteId, layout.Note, ScratchpadPanelLayout.Corner, TargetKind.Box));
        }
        else if (preview is not null)
        {
            var place = layout.Editor;
            foreach (var task in preview.Tasks)
            {
                var box = new RectangleF(place.X + task.Box.X - 3, place.Y + task.Box.Y - scroll - 3, task.Box.Width + 6, task.Box.Height + 6);
                if (box.Y < place.Y || box.Bottom > place.Bottom)
                {
                    box = new RectangleF(box.X, box.Y < place.Y ? place.Y : place.Bottom, 0, 0);
                }

                all.Add(new(task.Id, box, DropResultLayout.TaskCorner + 2, TargetKind.Toggle, Tip: task.Name, Checked: task.Done));
            }
        }

        all.AddRange(modes);
        Targets = all;
        TargetsChanged?.Invoke();
    }

    // Every show reads the note again, so a change another process saved shows; the first one also opens the box, or
    // draws the note, or with no mode chosen yet sends an empty note to Edit.
    private async void Load()
    {
        int shownAs = generation;
        try
        {
            await note.LoadAsync();
        }
        catch (Exception e)
        {
            log?.Invoke($"scratchpad panel: the note could not be loaded ({e.GetType().Name}); {kept.Length} typed characters kept");
            if (shownAs == generation && host is not null && layout is not null)
            {
                notice ??= text.Create(host);
                var box = layout.Editor;
                notice.Show(ScratchpadPanelLayout.Unreadable, TextStyle.Secondary with { Color = Theme.Warn }, new RectangleF(box.X, box.Y, box.Width, 18));
            }

            return;
        }

        if (shownAs != generation)
        {
            return;
        }

        if (editing)
        {
            bound?.Open();
        }
        else if (chosen is null && note.Text.Length == 0)
        {
            SetMode(true);
            chosen = null;
            if (editor is not null && focus.HasKeys && focus.Focused is null && !island.Interaction.HasFocus)
            {
                focus.Take(editor);
            }
        }
        else
        {
            BuildPreview();
            Retarget();
        }
    }

    private async void Observe(Task work, string what)
    {
        try
        {
            await work;
        }
        catch (Exception e)
        {
            log?.Invoke($"scratchpad panel: could not {what} ({e.GetType().Name})");
        }
    }
}
