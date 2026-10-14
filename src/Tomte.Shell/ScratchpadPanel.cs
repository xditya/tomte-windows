using System.Numerics;
using Tomte.Core;
using Windows.UI.Composition;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>
/// The Scratchpad tab: a heading, the card the note is typed into and a footer, as composition visuals in DIPs laid
/// out by <see cref="ScratchpadPanelLayout"/>. The card holds a text box bound to the note (<see cref="NoteEditor"/>);
/// a click into it, or the scratchpad hotkey, gives it the keys. The note loads on a worker, so <see cref="Shown"/>
/// returns at once and keys typed before the first load go in once it lands. Leaving the tab saves what is unsaved
/// and releases the text box.
/// </summary>
internal sealed class ScratchpadPanel(IslandWindow island, ScratchpadNote note, TextRenderer text, TypingFocus focus, Action<string>? log) : IPanel
{
    private static readonly Color Card = Color.FromArgb(15, 255, 255, 255);

    private readonly List<TextVisual> texts = [];
    private ShapeVisual? frame;
    private EditorView? editor;
    private NoteEditor? bound;
    private int generation;

    public PanelId Id => PanelId.Scratchpad;

    public void Shown(ContainerVisual body, Vector2 size)
    {
        var layout = new ScratchpadPanelLayout(size);
        frame = island.Compositor.CreateShapeVisual();
        frame.Size = size;
        body.Children.InsertAtTop(frame);
        DrawFrame(body, layout);
        editor = focus.Create(body, multiline: true, ScratchpadPanelLayout.NoteSize);
        editor.Place(layout.Editor, island.Scale);
        bound = new NoteEditor(editor, note, island.Queue);
        Load();
    }

    public void Hidden()
    {
        generation++;
        bound?.Dispose();
        editor?.Dispose();
        foreach (var visual in texts)
        {
            visual.Dispose();
        }

        texts.Clear();
        frame?.Dispose();
        (frame, editor, bound) = (null, null, null);
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

    // A click into the card goes to the text box through the typing focus, which takes the keys.
    public void Press(Vector2 point)
    {
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
            log?.Invoke($"scratchpad panel: the note could not be loaded ({e.GetType().Name})");
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
