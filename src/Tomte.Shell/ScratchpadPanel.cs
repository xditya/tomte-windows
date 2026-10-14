using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Windows.UI.Composition;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>
/// The Scratchpad tab: a heading, the card the note is typed into and a footer, as composition visuals
/// in DIPs laid out by <see cref="ScratchpadPanelLayout"/>. The note cannot be typed into on the island
/// yet: the editing surface is an open decision. An editor binds to the note's Text, Caret, Edit and
/// Changed and sits in <see cref="ScratchpadPanelLayout.Note"/>; until then the note's first lines show
/// as text-seam placeholders. The note loads on a worker, so <see cref="Shown"/> returns at once.
/// Leaving the tab saves what is unsaved.
/// </summary>
internal sealed class ScratchpadPanel(IslandWindow island, ScratchpadNote note, Action<string>? log) : IPanel
{
    private static readonly Color Card = Color.FromArgb(15, 255, 255, 255);

    private Compositor? compositor;
    private ShapeVisual? frame, lines;
    private ScratchpadPanelLayout? layout;
    private int generation;

    public PanelId Id => PanelId.Scratchpad;

    public void Shown(ContainerVisual body, Vector2 size)
    {
        compositor = island.Compositor;
        layout = new ScratchpadPanelLayout(size);
        frame = compositor.CreateShapeVisual();
        frame.Size = size;
        DrawFrame();
        body.Children.InsertAtTop(frame);
        lines = compositor.CreateShapeVisual();
        lines.Size = size;
        body.Children.InsertAtTop(lines);
        note.Changed += OnChanged;
        Load();
    }

    public void Hidden()
    {
        note.Changed -= OnChanged;
        generation++;
        frame?.Dispose();
        lines?.Dispose();
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

    private void DrawFrame()
    {
        var c = compositor!;
        var l = layout!;
        var head = l.Head;
        float title = TextSeam.Slot(c, frame!, "Scratchpad", new(head.X, head.Y), head.Height, head.Width, TextLook.Title);
        TextSeam.Slot(c, frame!, "one note, saved locally", new(head.X + title + 10, head.Y), head.Height, 180, TextLook.Secondary);

        var card = l.Note;
        var geometry = c.CreateRoundedRectangleGeometry();
        geometry.Offset = new Vector2(card.X, card.Y);
        geometry.Size = new Vector2(card.Width, card.Height);
        geometry.CornerRadius = new Vector2(ScratchpadPanelLayout.Corner);
        var shape = c.CreateSpriteShape(geometry);
        shape.FillBrush = c.CreateColorBrush(Card);
        frame!.Shapes.Add(shape);

        var foot = l.Foot;
        TextSeam.Slot(c, frame, "Saved on this PC", new(foot.X, foot.Y), foot.Height, 140, TextLook.Small);
        TextSeam.Slot(c, frame, "Esc closes, Ctrl+P pins", new(foot.Right, foot.Y), foot.Height, 160, TextLook.Small, alignRight: true);
    }

    // The note's first lines, one placeholder bar each, where the editor will draw the text.
    private void DrawLines()
    {
        if (lines is null || layout is null || compositor is null || !note.IsLoaded)
        {
            return;
        }

        lines.Shapes.Clear();
        RectangleF card = layout.Note;
        float width = card.Width - 2 * ScratchpadPanelLayout.TextInset;
        int chars = (int)(width / TextSeam.CharWidth(TextLook.Primary));
        var shown = ScratchpadPanelLayout.Lines(note.Text, chars, layout.VisibleLines);
        for (int i = 0; i < shown.Count; i++)
        {
            var at = new Vector2(card.X + ScratchpadPanelLayout.TextInset, card.Y + 10 + i * ScratchpadPanelLayout.LineHeight);
            TextSeam.Slot(compositor, lines, shown[i], at, ScratchpadPanelLayout.LineHeight, width, TextLook.Primary);
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
