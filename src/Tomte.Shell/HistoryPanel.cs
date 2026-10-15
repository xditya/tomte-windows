using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Text;
using Tomte.Core.Triggers;
using Windows.UI.Composition;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>
/// The History tab: the AI conversations and drop zone results kept on this PC, newest first, or the off or empty state.
/// A click on a row (or Enter or Right on it) opens its conversation under it (Left or Esc closes it), read as Markdown, with Continue, which opens the
/// AI box with the conversation loaded and nothing sent, and Copy answer, which puts the last answer on the clipboard.
/// The wheel scrolls the list. Rows come from the history's worker, so <see cref="Shown"/> returns at once and the rows
/// are drawn when they land; only the rows in and near the list's view have visuals. Composition visuals in DIPs laid
/// out by <see cref="HistoryPanelLayout"/>, built when the tab shows and released when it hides.
/// </summary>
internal sealed class HistoryPanel(IslandWindow island, AiHistory history, TextRenderer text, Action<AiExchange> continueWith, Func<string, bool> copy, Action<string>? log) : IPanel, IInteractive
{
    private View? view;
    private HistoryPanelLayout? layout;
    private IReadOnlyList<AiExchange> rows = [];
    private IReadOnlyList<Target> targets = [];
    private string? opened;
    private float scroll;
    private int generation;

    // The conversation that was open and the scroll when the tab last hid, for the AI box going back to it.
    private (string? Opened, float Scroll) left;

    public PanelId Id => PanelId.History;

    public IReadOnlyList<Target> Targets => targets;

    public event Action? TargetsChanged;

    public void Shown(ContainerVisual body, Vector2 size)
    {
        view = new View(island.Compositor, text, body, size);
        Reload(size);
    }

    /// <summary>Back from the AI box Continue opened: the same conversation open, scrolled where it was.</summary>
    public void ShownBack(ContainerVisual body, Vector2 size)
    {
        (opened, scroll) = left;
        Shown(body, size);
    }

    public void Hidden()
    {
        generation++;
        view?.Dispose();
        left = (opened, scroll);
        (view, layout, rows, targets, opened, scroll) = (null, null, [], [], null, 0);
    }

    /// <summary>Esc closes the open conversation to the list, before it retracts the island.</summary>
    public bool Command(NavKey key)
    {
        if (key != NavKey.Escape || RetractionRules.Escape(asking: false, opened is not null, fromPanels: false) != EscapeStep.Close || layout is null)
        {
            return false;
        }

        opened = null;
        Lay();
        Draw();
        return true;
    }

    // The rows and buttons are the interaction host's.
    public void Press(Vector2 point)
    {
    }

    public void Wheel(Vector2 point, int delta)
    {
        if (view is not null && layout is not null)
        {
            scroll = layout.Scrolled(scroll, delta);
            view.Scroll(layout, scroll);
            Retarget();
        }
    }

    /// <summary>Focus that reaches a row or button out of view (only the arrow keys and Tab can) scrolls the list to show it.</summary>
    public void Hovered(string? id)
    {
        if (view is null || layout is null || id is null || targets.FirstOrDefault(t => t.Id == id).Bounds.Height > 0 || layout.Place(id) is not { IsEmpty: false } place)
        {
            return;
        }

        scroll = layout.Reveal(scroll, place);
        view.Scroll(layout, scroll);
        Retarget();
    }

    /// <summary>
    /// On a row, Right opens it and Left closes it; Ctrl+C on a row copies its answer, and on the open conversation's
    /// buttons the open one's, as Copy answer does. Held keys do it once.
    /// </summary>
    public bool KeyDown(string id, NavKey key, bool repeat)
    {
        if (key == NavKey.Copy)
        {
            var of = rows.FirstOrDefault(r => HistoryPanelLayout.RowId(r) == id) ?? (layout is { Opened: >= 0 } at ? rows[at.Opened] : null);
            if (!repeat && of is { LastAnswer.Length: > 0 } && copy(of.LastAnswer))
            {
                island.Interaction.Acknowledge(this, id);
            }

            return of is not null;
        }

        bool right = key == NavKey.Right;
        if ((!right && key != NavKey.Left) || rows.FirstOrDefault(row => HistoryPanelLayout.RowId(row) == id) is not { } row)
        {
            return false;
        }

        if (!repeat && right != (row.Key == opened))
        {
            Activate(id);
        }

        return true;
    }

    /// <summary>A double click's second click would close the row the first opened: only the first counts.</summary>
    public void Activate(string id, ActivateBy by)
    {
        if (by != ActivateBy.DoubleClick)
        {
            Activate(id);
        }
    }

    public void Activate(string id)
    {
        if (layout is null || view is null)
        {
            return;
        }

        var item = layout.Opened >= 0 ? rows[layout.Opened] : null;
        switch (id)
        {
            case HistoryPanelLayout.ContinueId when item is not null:
                continueWith(item);
                break;
            case HistoryPanelLayout.CopyId when item is { LastAnswer.Length: > 0 }:
                // The user's own action, as a copy is; the tick says it happened, since nothing else on the panel changes.
                if (copy(item.LastAnswer))
                {
                    island.Interaction.Acknowledge(this, id);
                }

                break;
            default:
                if (rows.FirstOrDefault(row => HistoryPanelLayout.RowId(row) == id) is { } picked)
                {
                    opened = picked.Key == opened ? null : picked.Key;
                    Lay();
                    if (layout.Opened >= 0)
                    {
                        scroll = layout.Reveal(scroll, layout.Opened);
                    }

                    Draw();
                }

                break;
        }
    }

    private async void Reload(Vector2 size)
    {
        int shownAs = generation;
        IReadOnlyList<AiExchange> next = [];
        try
        {
            // Off, this returns none and clears anything left behind.
            next = await history.All(DateTimeOffset.Now);
        }
        catch (Exception e)
        {
            log?.Invoke($"history panel: rows could not be read ({e.GetType().Name})");
        }

        if (view is null || shownAs != generation)
        {
            return;
        }

        rows = next;
        Lay(size);
        Draw();
    }

    private void Lay(Vector2? size = null) => layout = new HistoryPanelLayout(size ?? layout!.Body, rows, opened);

    private void Draw()
    {
        scroll = Math.Min(scroll, layout!.MaxScroll);
        view!.Show(layout, history.On, scroll);
        Retarget();
    }

    // The host reads the targets on every pointer move, so they are built once per layout or scroll.
    private void Retarget()
    {
        targets = layout?.Targets(scroll) ?? [];
        TargetsChanged?.Invoke();
    }

    private sealed class View : IDisposable
    {
        private static readonly Color Card = Color.FromArgb(15, 255, 255, 255);
        private static readonly Color Lit = Color.FromArgb(28, 255, 255, 255);
        private static readonly Color Line = Color.FromArgb(46, 255, 255, 255);
        private static readonly Color Glyph = Color.FromArgb(153, 255, 255, 255);

        // Rows this far above and below the list's view keep their visuals, so a notch of the wheel rarely builds one.
        private const float Margin = 300;

        private readonly Compositor c;
        private readonly TextRenderer text;
        private readonly ContainerVisual body;
        private readonly ShapeVisual frame;
        private readonly ContainerVisual list, rowsVisual;
        private readonly List<TextVisual> texts = [];
        private readonly Dictionary<int, Row> built = [];

        public View(Compositor compositor, TextRenderer text, ContainerVisual body, Vector2 size)
        {
            (c, this.text, this.body) = (compositor, text, body);
            frame = c.CreateShapeVisual();
            frame.Size = size;
            body.Children.InsertAtTop(frame);
            list = c.CreateContainerVisual();
            // The list scrolls under an inset clip, so a row half out of view is cut at the list's edge.
            list.Clip = c.CreateInsetClip();
            rowsVisual = c.CreateContainerVisual();
            list.Children.InsertAtTop(rowsVisual);
            body.Children.InsertAtTop(list);
        }

        public void Show(HistoryPanelLayout layout, bool on, float scroll)
        {
            frame.Shapes.Clear();
            foreach (var visual in texts)
            {
                visual.Dispose();
            }

            texts.Clear();
            Clear();
            var area = layout.List;
            list.Offset = new Vector3(area.X, area.Y, 0);
            list.Size = new Vector2(area.Width, area.Height);
            foreach (var line in layout.HeadText(on))
            {
                Text(body).Show(line);
            }

            if (!on || layout.Items.Count == 0)
            {
                var outline = c.CreateRoundedRectangleGeometry();
                outline.Offset = new Vector2(area.X + 0.5f, area.Y + 0.5f);
                outline.Size = new Vector2(area.Width - 1, area.Height - 1);
                outline.CornerRadius = new Vector2(HistoryPanelLayout.Corner);
                var shape = c.CreateSpriteShape(outline);
                shape.StrokeBrush = c.CreateColorBrush(Line);
                shape.StrokeThickness = 1;
                frame.Shapes.Add(shape);
                foreach (var line in layout.BoxText(on))
                {
                    Text(body).Show(line);
                }

                return;
            }

            foreach (var line in layout.FootText())
            {
                Text(body).Show(line);
            }

            Scroll(layout, scroll);
        }

        /// <summary>The list moved: rows that came near the view are built, those far from it released, and the rest land on whole pixels again.</summary>
        public void Scroll(HistoryPanelLayout layout, float scroll)
        {
            rowsVisual.Offset = new Vector3(0, -scroll, 0);
            float top = scroll - Margin, bottom = scroll + layout.List.Height + Margin;
            foreach (int i in built.Keys.Where(i => layout.Rows[i].Bottom < top || layout.Rows[i].Y > bottom).ToList())
            {
                built[i].Dispose();
                built.Remove(i);
            }

            for (int i = 0; i < layout.Rows.Count; i++)
            {
                var at = layout.Rows[i];
                if (at.Bottom >= top && at.Y <= bottom && !built.ContainsKey(i))
                {
                    built[i] = Build(layout, i);
                }
            }

            foreach (var row in built.Values)
            {
                row.Refresh();
            }
        }

        public void Dispose()
        {
            foreach (var visual in texts)
            {
                visual.Dispose();
            }

            Clear();
            body.Children.RemoveAll();
            frame.Dispose();
            rowsVisual.Dispose();
            list.Dispose();
        }

        private void Clear()
        {
            foreach (var row in built.Values)
            {
                row.Dispose();
            }

            built.Clear();
        }

        // One row's card, its icon and two lines, and for the opened one its conversation and buttons, in list coordinates.
        private Row Build(HistoryPanelLayout layout, int i)
        {
            var item = layout.Items[i];
            var at = layout.Rows[i];
            var shapes = c.CreateShapeVisual();
            shapes.Size = new Vector2(at.Width, at.Height);
            shapes.Offset = new Vector3(at.X, at.Y, 0);
            rowsVisual.Children.InsertAtTop(shapes);
            Rounded(shapes, new RectangleF(0, 0, at.Width, at.Height), HistoryPanelLayout.Corner, i == layout.Opened ? Lit : Card);
            var icon = HistoryPanelLayout.IconIn;
            Rounded(shapes, icon, 6, Lit);
            var ink = c.CreateColorBrush(Glyph);
            var glyph = c.CreateContainerShape();
            glyph.Offset = new Vector2(icon.X + (icon.Width - HistoryPanelLayout.IconGlyph) / 2, icon.Y + (icon.Height - HistoryPanelLayout.IconGlyph) / 2);
            glyph.Scale = new Vector2(HistoryPanelLayout.IconGlyph / 16);
            foreach (var figure in HistoryPanelLayout.Glyph(item.Source))
            {
                var path = c.CreateSpriteShape(c.CreatePathGeometry(IslandWindow.BuildPath(figure, 1)));
                path.FillBrush = ink;
                glyph.Shapes.Add(path);
            }

            shapes.Shapes.Add(glyph);
            var row = new Row(shapes);
            foreach (var line in HistoryPanelLayout.RowText(item, at.Width, opened: i == layout.Opened))
            {
                row.Text(text, shapes).Show(line);
            }

            if (i == layout.Opened)
            {
                // The conversation's runs and the buttons sit in the card, so they move with it.
                var origin = new Vector2(at.X, at.Y);
                foreach (var (run, runAt) in layout.Conversation)
                {
                    row.Text(text, shapes).Show(run, runAt - origin);
                }

                foreach (var button in new[] { layout.ContinueButton, layout.CopyButton })
                {
                    Rounded(shapes, new RectangleF(button.X - at.X, button.Y - at.Y, button.Width, button.Height), HistoryPanelLayout.Corner, Lit);
                }

                foreach (var label in layout.ButtonLabels())
                {
                    var slot = label.Slot;
                    bool off = label.Text == HistoryPanelLayout.Copy && item.LastAnswer.Length == 0;
                    var shown = label with { Slot = new RectangleF(slot.X - at.X, slot.Y - at.Y, slot.Width, slot.Height) };
                    row.Text(text, shapes).Show(off ? shown with { Style = shown.Style with { Color = TextStyle.TertiaryColor } } : shown);
                }
            }

            return row;
        }

        private TextVisual Text(ContainerVisual host)
        {
            var visual = text.Create(host);
            texts.Add(visual);
            return visual;
        }

        private void Rounded(ShapeVisual into, RectangleF r, float radius, Color fill)
        {
            var geometry = c.CreateRoundedRectangleGeometry();
            geometry.Offset = new Vector2(r.X, r.Y);
            geometry.Size = new Vector2(r.Width, r.Height);
            geometry.CornerRadius = new Vector2(radius);
            var shape = c.CreateSpriteShape(geometry);
            shape.FillBrush = c.CreateColorBrush(fill);
            into.Shapes.Add(shape);
        }

        // A row's visuals: its card and shapes, and its text runs, which land on whole pixels again when the list moves.
        private sealed class Row(ShapeVisual shapes) : IDisposable
        {
            private readonly List<TextVisual> texts = [];

            public TextVisual Text(TextRenderer renderer, ContainerVisual host)
            {
                var visual = renderer.Create(host);
                texts.Add(visual);
                return visual;
            }

            public void Refresh()
            {
                foreach (var visual in texts)
                {
                    visual.Refresh();
                }
            }

            public void Dispose()
            {
                foreach (var visual in texts)
                {
                    visual.Dispose();
                }

                shapes.Parent?.Children.Remove(shapes);
                shapes.Dispose();
            }
        }
    }
}
