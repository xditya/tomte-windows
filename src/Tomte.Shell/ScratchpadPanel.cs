using System.Numerics;
using System.Text;
using Tomte.Core;
using Tomte.Core.Text;
using Windows.UI.Composition;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>
/// The Scratchpad tab: a heading, the card the note is typed into and a footer, as composition visuals in DIPs laid
/// out by <see cref="ScratchpadPanelLayout"/>. The card holds a text box bound to the note (<see cref="NoteEditor"/>);
/// a click into it, or the scratchpad hotkey, gives it the keys. The note loads on a worker, so <see cref="Shown"/>
/// returns at once and what is typed before the first load goes in once it lands. A load that fails says so in the
/// card and keeps what was typed for the next show that can read the note. Leaving the tab saves what is unsaved and
/// releases the text box.
/// </summary>
internal sealed class ScratchpadPanel(IslandWindow island, ScratchpadNote note, TextRenderer text, TypingFocus focus, Action<string>? log) : IPanel
{
    private static readonly Color Card = Color.FromArgb(15, 255, 255, 255);

    private readonly List<TextVisual> texts = [];

    // Typed while the note could not be read yet; kept across shows until it can be.
    private readonly StringBuilder kept = new();
    private ScratchpadPanelLayout? layout;
    private ContainerVisual? host;
    private ShapeVisual? frame;
    private EditorView? editor;
    private NoteEditor? bound;
    private int generation;

    public PanelId Id => PanelId.Scratchpad;

    public void Shown(ContainerVisual body, Vector2 size)
    {
        layout = new ScratchpadPanelLayout(size);
        host = body;
        frame = island.Compositor.CreateShapeVisual();
        frame.Size = size;
        body.Children.InsertAtTop(frame);
        DrawFrame(body, layout);
        editor = focus.Create(body, multiline: true, ScratchpadPanelLayout.NoteSize);
        editor.Place(layout.Editor, island.Scale);
        // The note is styled once the Panels morph is at rest, never during it.
        bound = new NoteEditor(editor, note, island.Queue, kept) { Moving = () => island.Moving };
        island.Settled += bound.Settled;
        Load();
        // Switched or come back to while the island has the keys for its targets (not on the tab labels, where the arrows
        // go on along the tabs): the note is the tab's first useful thing.
        var shownBox = editor;
        island.Queue.TryEnqueue(() =>
        {
            if (editor == shownBox && focus.HasKeys && focus.Focused is null && !island.Interaction.HasFocus)
            {
                focus.Take(shownBox);
            }
        });
    }

    public void Hidden()
    {
        generation++;
        if (bound is not null)
        {
            island.Settled -= bound.Settled;
            bound.Dispose();
        }

        editor?.Dispose();
        foreach (var visual in texts)
        {
            visual.Dispose();
        }

        texts.Clear();
        frame?.Dispose();
        (frame, editor, bound, layout, host) = (null, null, null, null, null);
        Observe(note.FlushAsync(), "save the note");
    }

    /// <summary>The scratchpad hotkey: the note takes the keys with the caret where it was left.</summary>
    public void TakeKeys()
    {
        if (editor is not null)
        {
            focus.Take(editor);
        }
    }

    // A click into the text box goes to it through the typing focus. The card's padding around the box looks like
    // part of it, so a click there gives the note the keys too.
    public void Press(Vector2 point)
    {
        if (editor is not null && layout is not null && layout.Note.Contains(point.X, point.Y) && !editor.Box.Contains(point.X, point.Y))
        {
            focus.Take(editor);
        }
    }

    public void Wheel(Vector2 point, int delta)
    {
        if (editor is not null && editor.Box.Contains(point.X, point.Y))
        {
            editor.Wheel(delta);
        }
    }

    // Every show reads the note again, so a change another process saved shows; the first one also opens the box.
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
                var notice = text.Create(host);
                texts.Add(notice);
                var box = layout.Editor;
                notice.Show(ScratchpadPanelLayout.Unreadable, TextStyle.Secondary with { Color = TextStyle.WarnColor }, new System.Drawing.RectangleF(box.X, box.Y, box.Width, 18));
            }

            return;
        }

        if (shownAs == generation)
        {
            bound?.Open();
        }
    }

    private void DrawFrame(ContainerVisual body, ScratchpadPanelLayout layout)
    {
        var c = island.Compositor;
        foreach (var line in layout.FrameText())
        {
            var label = text.Create(body);
            texts.Add(label);
            label.Show(line);
        }

        var card = layout.Note;
        var geometry = c.CreateRoundedRectangleGeometry();
        geometry.Offset = new Vector2(card.X, card.Y);
        geometry.Size = new Vector2(card.Width, card.Height);
        geometry.CornerRadius = new Vector2(ScratchpadPanelLayout.Corner);
        var shape = c.CreateSpriteShape(geometry);
        shape.FillBrush = c.CreateColorBrush(Card);
        frame!.Shapes.Add(shape);
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
