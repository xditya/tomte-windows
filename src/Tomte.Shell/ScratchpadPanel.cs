using System.Numerics;
using Tomte.Core;
using Windows.UI.Composition;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>
/// The Scratchpad tab: a heading, the card the note is typed into and a footer, as composition visuals
/// in DIPs laid out by <see cref="ScratchpadPanelLayout"/>. The note cannot be typed into on the island
/// yet: the editing surface is an open decision. An editor binds to the note's Text, Caret, Edit and
/// Changed and sits in <see cref="ScratchpadPanelLayout.Note"/>; until then the note's start shows there as
/// text, wrapped and cut to the card. The note loads on a worker, so <see cref="Shown"/> returns at once.
/// Leaving the tab saves what is unsaved.
/// </summary>
internal sealed class ScratchpadPanel(IslandWindow island, ScratchpadNote note, TextRenderer text, Action<string>? log) : IPanel
{
    private static readonly Color Card = Color.FromArgb(15, 255, 255, 255);

    private readonly List<TextVisual> texts = [];
    private ShapeVisual? frame;
    private TextVisual? lines;
    private ScratchpadPanelLayout? layout;
    private int generation;

    public PanelId Id => PanelId.Scratchpad;

    public void Shown(ContainerVisual body, Vector2 size)
    {
        layout = new ScratchpadPanelLayout(size);
        frame = island.Compositor.CreateShapeVisual();
        frame.Size = size;
        body.Children.InsertAtTop(frame);
        DrawFrame(body);
        lines = Text(body);
        note.Changed += OnChanged;
        Load();
    }

    public void Hidden()
    {
        note.Changed -= OnChanged;
        generation++;
        foreach (var visual in texts)
        {
            visual.Dispose();
        }

        texts.Clear();
        frame?.Dispose();
        (frame, lines, layout) = (null, null, null);
        Observe(note.FlushAsync(), "save the note");
    }

    /// <summary>Where the editor will take the caret and the window will activate for typing. Nothing to do until it exists.</summary>
    public void Press(Vector2 point)
    {
    }

    public void Wheel(Vector2 point, int delta)
    {
    }

    private void OnChanged() => island.Queue.TryEnqueue(DrawLines);

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
            DrawLines();
        }
    }

    private TextVisual Text(ContainerVisual host)
    {
        var visual = text.Create(host);
        texts.Add(visual);
        return visual;
    }

    private void DrawFrame(ContainerVisual body)
    {
        var c = island.Compositor;
        var l = layout!;
        foreach (var line in l.FrameText())
        {
            Text(body).Show(line);
        }

        var card = l.Note;
        var geometry = c.CreateRoundedRectangleGeometry();
        geometry.Offset = new Vector2(card.X, card.Y);
        geometry.Size = new Vector2(card.Width, card.Height);
        geometry.CornerRadius = new Vector2(ScratchpadPanelLayout.Corner);
        var shape = c.CreateSpriteShape(geometry);
        shape.FillBrush = c.CreateColorBrush(Card);
        frame!.Shapes.Add(shape);
    }

    // The note's start, wrapped to the card and cut with an ellipsis where the card ends.
    private void DrawLines()
    {
        if (lines is null || layout is null || !note.IsLoaded)
        {
            return;
        }

        var (run, at) = layout.NoteRun(note.Text);
        lines.Show(run, at);
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
