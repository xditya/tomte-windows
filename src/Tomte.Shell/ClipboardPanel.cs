using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Clipboard;
using Tomte.Core.Interaction;
using Tomte.Core.Text;
using Windows.UI.Composition;
using Windows.Win32;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>
/// The Clipboard tab: the Keep history switch, the history rows newest first (a picked row is the
/// selected one) with a remove button each, and Clear all, which asks once before it clears; or the off or
/// empty state. Composition visuals in DIPs laid out by <see cref="ClipboardPanelLayout"/>, built when the tab
/// shows and released when it hides. Rows come from the watch's worker, so <see cref="Shown"/> returns at once
/// and the rows are drawn when they land. Row text is the history's shortened form, one line each. Clicks and
/// keys come through the island's interaction host: arrows move between rows and their remove buttons, Enter
/// copies a row again, Delete removes it.
/// </summary>
internal sealed class ClipboardPanel(IslandWindow island, ClipboardWatch watch, Action<bool> keepHistory, TextRenderer text, Action<string>? log) : IPanel, IInteractive
{
    private View? view;
    private IReadOnlyList<ClipboardRow> rows = [];
    private IReadOnlyList<Target> targets = [];
    private float scroll;
    private long selected = -1;
    private int generation;
    private bool historyOn, asking;
    private long askedAt, removedAt;
    private Windows.System.DispatcherQueueTimer? askTimer;

    public PanelId Id => PanelId.Clipboard;

    public IReadOnlyList<Target> Targets => targets;

    public event Action? TargetsChanged;

    public void Shown(ContainerVisual body, Vector2 size)
    {
        view = new View(island.Compositor, text, body, new ClipboardPanelLayout(size), island.Scale, log);
        watch.Changed += OnChanged;
        Reload();
    }

    public void Hidden()
    {
        watch.Changed -= OnChanged;
        generation++;
        view?.Dispose();
        view = null;
        rows = [];
        (scroll, selected) = (0, -1);
        Ask(false);
    }

    /// <summary>Presses come through the targets.</summary>
    public void Press(Vector2 point)
    {
    }

    /// <summary>
    /// A click within the double-click time of the last one is dropped on Clear all's question and on a remove button:
    /// a double or triple click would answer the question before it could be read, or take the next row too once the
    /// first has gone (each x is a target of its own, so the host sees two single clicks).
    /// </summary>
    public void Activate(string id)
    {
        if (view is null)
        {
            return;
        }

        if (id == ClipboardPanelLayout.SwitchId)
        {
            keepHistory(!watch.On);
        }
        else if (id == ClipboardPanelLayout.ClearId && asking)
        {
            if (!Soon(askedAt))
            {
                Ask(false);
                Observe(watch.ClearAsync(), "clear the history");
            }
        }
        else if (id == ClipboardPanelLayout.ClearId)
        {
            Ask(true);
        }
        else if (Find(id) is ({ } index, true))
        {
            if (!Soon(removedAt))
            {
                removedAt = Environment.TickCount64;
                Observe(watch.RemoveAsync(rows[index].Id), "remove the item");
            }
        }
        else if (Find(id) is ({ } picked, false))
        {
            Pick(rows[picked], id);
        }
    }

    /// <summary>
    /// On a row or its remove button, Delete removes the row and Ctrl+C copies its entry again, as Enter on the row does;
    /// a held key acts once.
    /// </summary>
    public bool KeyDown(string id, NavKey key, bool repeat)
    {
        if (key is not (NavKey.Remove or NavKey.Copy) || Find(id) is not ({ } index, _))
        {
            return false;
        }

        if (!repeat && key == NavKey.Remove)
        {
            Observe(watch.RemoveAsync(rows[index].Id), "remove the item");
        }
        else if (!repeat)
        {
            Pick(rows[index], ClipboardPanelLayout.RowId(rows[index].Id));
        }

        return true;
    }

    /// <summary>Focus or the pointer on a row not wholly in view scrolls the list just far enough to show all of it.</summary>
    public void Hovered(string? id)
    {
        if (view is null || id is null || Find(id) is not ({ } index, _) || view.Layout.ScrolledTo(index, scroll, rows.Count) is var to && to == scroll)
        {
            return;
        }

        scroll = to;
        view.Scroll(scroll);
        Retarget();
    }

    public void Wheel(Vector2 point, int delta)
    {
        if (view is not null)
        {
            scroll = view.Layout.Scrolled(scroll, delta, rows.Count);
            view.Scroll(scroll);
            Retarget();
        }
    }

    private static bool Soon(long since) => Environment.TickCount64 - since < PInvoke.GetDoubleClickTime();

    // The row a target stands for, and whether it is the row's remove button.
    private (int? Index, bool Remove) Find(string id)
    {
        for (int i = 0; i < rows.Count; i++)
        {
            if (id == ClipboardPanelLayout.RowId(rows[i].Id) || id == ClipboardPanelLayout.RemoveId(rows[i].Id))
            {
                return (i, id == ClipboardPanelLayout.RemoveId(rows[i].Id));
            }
        }

        return (null, false);
    }

    /// <summary>The row goes back on the clipboard; once it has gone to the listener, "Copied" shows at the row.</summary>
    private async void Pick(ClipboardRow row, string id)
    {
        int shownAs = generation;
        selected = row.Id;
        view?.Select(selected);
        try
        {
            if (await watch.PickAsync(row.Id) && shownAs == generation)
            {
                island.Interaction.Acknowledge(this, id);
            }
        }
        catch (Exception e)
        {
            log?.Invoke($"clipboard panel: could not put the item back ({e.GetType().Name})");
        }
    }

    /// <summary>Clear all asks, and stops asking by itself after a few seconds; or it stops asking now.</summary>
    private void Ask(bool on)
    {
        asking = on;
        askTimer?.Stop();
        if (on)
        {
            askedAt = Environment.TickCount64;
            if (askTimer is null)
            {
                askTimer = island.Queue.CreateTimer();
                askTimer.IsRepeating = false;
                askTimer.Interval = TimeSpan.FromSeconds(ClipboardPanelLayout.AskSeconds);
                askTimer.Tick += (_, _) => Ask(false);
            }

            askTimer.Start();
        }

        view?.ShowClearAll(asking);
        Retarget();
    }

    private void Retarget()
    {
        targets = view is null ? [] : view.Layout.Targets(historyOn, [.. rows.Select(row => row.Id)], scroll, asking);
        TargetsChanged?.Invoke();
    }

    // Raised on a worker thread or the UI thread; the redraw always runs on the UI thread.
    private void OnChanged() => island.Queue.TryEnqueue(Reload);

    private async void Reload()
    {
        int shownAs = generation;
        bool on = watch.On;
        IReadOnlyList<ClipboardRow> next = [];
        try
        {
            // Off, this returns none and clears anything left behind.
            next = await watch.RowsAsync(DateTimeOffset.Now);
        }
        catch (Exception e)
        {
            log?.Invoke($"clipboard panel: rows could not be read ({e.GetType().Name})");
        }

        if (view is null || shownAs != generation)
        {
            return;
        }

        (rows, historyOn) = (next, on);
        scroll = Math.Min(scroll, view.Layout.MaxScroll(rows.Count));
        view.Show(on, rows, selected, scroll, DateTimeOffset.Now, watch.Retention, asking);
        Retarget();
    }

    private async void Observe(Task work, string what)
    {
        try
        {
            await work;
        }
        catch (Exception e)
        {
            log?.Invoke($"clipboard panel: could not {what} ({e.GetType().Name})");
        }
    }

    private sealed class View : IDisposable
    {
        private static readonly Color Card = ShelfColors.Of(Theme.Card);
        private static readonly Color CardSelected = ShelfColors.Of(Theme.Raised);
        private static readonly Color IconFill = ShelfColors.Of(Theme.RaisedHi);
        private static readonly Color Line = ShelfColors.Of(Theme.LineStrong);
        private static readonly Color Glyph = ShelfColors.Of(Theme.Ink2);
        private static readonly Color Faint = ShelfColors.Of(Theme.Ink3);
        private static readonly Color Off = ShelfColors.Of(Theme.LineStrong);
        private static readonly Color Knob = ShelfColors.Of(Theme.Ink);
        private static readonly Color Outline = ShelfColors.Of(Theme.LineStrong);
        private static readonly Color Warn = ShelfColors.Of(Theme.Warn);

        private static Color On => ShelfColors.Of(Theme.Accent);

        private readonly Compositor c;
        private readonly TextRenderer text;
        private readonly ContainerVisual body;
        private readonly ShapeVisual frame;
        private readonly ContainerVisual list;
        private readonly ContainerVisual rowsVisual;
        private readonly List<(long Id, CompositionColorBrush Fill)> rowBrushes = [];

        // Every text visual of the current build; the rows' own move with the scroll and land on whole pixels again.
        private readonly List<TextVisual> texts = [], rowTexts = [];
        private readonly double scale;
        private readonly Action<string>? log;
        private ClipboardThumbs? thumbs;

        // Clear all, while history has rows: when it asks only its outline and word change.
        private CompositionRoundedRectangleGeometry? clearOutline;
        private CompositionColorBrush? clearStroke;
        private TextVisual? clearWord;

        public View(Compositor compositor, TextRenderer text, ContainerVisual body, ClipboardPanelLayout layout, double scale, Action<string>? log)
        {
            (c, this.text, this.body, Layout, this.scale, this.log) = (compositor, text, body, layout, scale, log);
            frame = c.CreateShapeVisual();
            frame.Size = layout.Body;
            body.Children.InsertAtTop(frame);

            // The list scrolls under an inset clip, so a row half out of view is cut at the list's edge.
            var area = layout.List;
            list = c.CreateContainerVisual();
            list.Offset = new Vector3(area.X, area.Y, 0);
            list.Size = new Vector2(area.Width, area.Height);
            list.Clip = c.CreateInsetClip();
            rowsVisual = c.CreateContainerVisual();
            list.Children.InsertAtTop(rowsVisual);
            body.Children.InsertAtTop(list);
        }

        public ClipboardPanelLayout Layout { get; }

        public void Show(bool on, IReadOnlyList<ClipboardRow> rows, long selected, float scroll, DateTimeOffset now, ClipboardRetention kept, bool asking)
        {
            frame.Shapes.Clear();
            DisposeTexts();
            (clearOutline, clearStroke, clearWord) = (null, null, null);
            thumbs?.Dispose();
            thumbs = new ClipboardThumbs(c, scale, log);
            var old = rowsVisual.Children.ToList();
            rowsVisual.Children.RemoveAll();
            old.ForEach(row => row.Dispose());
            rowBrushes.Clear();
            foreach (var line in Layout.HeadText(on, kept))
            {
                Text(texts, body).Show(line);
            }

            DrawSwitch(on);
            if (!on || rows.Count == 0)
            {
                Box(Layout.List, Layout.BoxText(on, kept));
                return;
            }

            Text(texts, body).Show(Layout.FootText(asking)[0]);
            clearOutline = c.CreateRoundedRectangleGeometry();
            clearOutline.CornerRadius = new Vector2(ClipboardPanelLayout.Corner);
            clearStroke = c.CreateColorBrush();
            var outline = c.CreateSpriteShape(clearOutline);
            outline.StrokeBrush = clearStroke;
            outline.StrokeThickness = 1;
            frame.Shapes.Add(outline);
            clearWord = Text(texts, body);
            ShowClearAll(asking);

            float width = Layout.List.Width;
            for (int i = 0; i < rows.Count; i++)
            {
                rowsVisual.Children.InsertAtTop(Row(rows[i], ClipboardPanelLayout.Row(i, width), rows[i].Id == selected, now));
            }

            Scroll(scroll);
            thumbs.Load();
        }

        /// <summary>Clear all at rest, or asking: the outline fits the word, and both turn the warn colour while it asks.</summary>
        public void ShowClearAll(bool asking)
        {
            if (clearOutline is null || clearStroke is null || clearWord is null)
            {
                return;
            }

            var button = Layout.ClearAll(asking);
            clearOutline.Offset = new Vector2(button.X + 0.5f, button.Y + 0.5f);
            clearOutline.Size = new Vector2(button.Width - 1, button.Height - 1);
            clearStroke.Color = asking ? Warn : Outline;
            clearWord.Show(Layout.FootText(asking)[1]);
        }

        public void Select(long selected)
        {
            // Recoloured, never swapped: a brush taken off its shape and put back does not draw again.
            foreach (var (id, fill) in rowBrushes)
            {
                fill.Color = id == selected ? CardSelected : Card;
            }
        }

        public void Scroll(float scroll)
        {
            rowsVisual.Offset = new Vector3(0, -scroll, 0);
            foreach (var visual in rowTexts)
            {
                visual.Refresh();
            }
        }

        public void Dispose()
        {
            DisposeTexts();
            thumbs?.Dispose();
            body.Children.RemoveAll();
            frame.Dispose();
            rowsVisual.Dispose();
            list.Dispose();
        }

        private ShapeVisual Row(ClipboardRow row, RectangleF at, bool selected, DateTimeOffset now)
        {
            var shapes = c.CreateShapeVisual();
            shapes.Size = new Vector2(at.Width, at.Height);
            shapes.Offset = new Vector3(at.X, at.Y, 0);
            var fill = c.CreateColorBrush(selected ? CardSelected : Card);
            rowBrushes.Add((row.Id, fill));
            Rounded(shapes, new RectangleF(0, 0, at.Width, at.Height), ClipboardPanelLayout.Corner, fill);

            // The kind icon: three lines for text, two links for an address; an image shows its thumbnail.
            var icon = ClipboardPanelLayout.IconIn;
            if (row.Thumb is { } thumb)
            {
                thumbs!.Add(shapes, thumb);
            }
            else
            {
                Rounded(shapes, icon, 6, c.CreateColorBrush(IconFill));
            }

            var glyph = c.CreateColorBrush(Glyph);
            if (row.IsLink)
            {
                Stroked(shapes, new RectangleF(icon.X + 5, icon.Y + 9, 9, 6), 3, glyph);
                Stroked(shapes, new RectangleF(icon.X + 10, icon.Y + 9, 9, 6), 3, glyph);
            }
            else if (!row.IsImage)
            {
                foreach (var (y, w) in (ReadOnlySpan<(float, float)>)[(7, 12), (11, 12), (15, 8)])
                {
                    Rounded(shapes, new RectangleF(icon.X + 6, icon.Y + y, w, 2), 1, glyph);
                }
            }

            // Remove: an x in a small circle at the row's right end, the age left of it.
            var remove = ClipboardPanelLayout.RemoveIn(at.Width);
            Rounded(shapes, remove, remove.Width / 2, c.CreateColorBrush(Card));
            var cross = c.CreateColorBrush(Faint);
            var mid = new Vector2(remove.X + remove.Width / 2, remove.Y + remove.Height / 2);
            foreach (float angle in (ReadOnlySpan<float>)[MathF.PI / 4, -MathF.PI / 4])
            {
                var bar = c.CreateRoundedRectangleGeometry();
                bar.Size = new Vector2(10, 1.5f);
                bar.CornerRadius = new Vector2(0.75f);
                var shape = c.CreateSpriteShape(bar);
                shape.FillBrush = cross;
                shape.CenterPoint = new Vector2(5, 0.75f);
                shape.RotationAngle = angle;
                shape.Offset = mid - new Vector2(5, 0.75f);
                shapes.Shapes.Add(shape);
            }

            foreach (var line in ClipboardPanelLayout.RowText(row.Display, row.Ago(now), at.Width, row.IsImage))
            {
                Text(rowTexts, shapes).Show(line);
            }

            return shapes;
        }

        private TextVisual Text(List<TextVisual> into, ContainerVisual host)
        {
            var visual = text.Create(host);
            into.Add(visual);
            return visual;
        }

        private void DisposeTexts()
        {
            foreach (var visual in texts.Concat(rowTexts))
            {
                visual.Dispose();
            }

            texts.Clear();
            rowTexts.Clear();
        }

        private void DrawSwitch(bool on)
        {
            var track = Layout.Switch;
            Rounded(frame, track, track.Height / 2, c.CreateColorBrush(on ? On : Off));
            var knob = c.CreateEllipseGeometry();
            knob.Radius = new Vector2(7);
            knob.Center = new Vector2(on ? track.Right - 9 : track.X + 9, track.Y + track.Height / 2);
            var shape = c.CreateSpriteShape(knob);
            shape.FillBrush = c.CreateColorBrush(Knob);
            frame.Shapes.Add(shape);
        }

        private void Box(RectangleF area, IReadOnlyList<TextSlot> lines)
        {
            var outline = c.CreateRoundedRectangleGeometry();
            outline.Offset = new Vector2(area.X + 0.5f, area.Y + 0.5f);
            outline.Size = new Vector2(area.Width - 1, area.Height - 1);
            outline.CornerRadius = new Vector2(ClipboardPanelLayout.Corner);
            var shape = c.CreateSpriteShape(outline);
            shape.StrokeBrush = c.CreateColorBrush(Line);
            shape.StrokeThickness = 1;
            frame.Shapes.Add(shape);
            foreach (var line in lines)
            {
                Text(texts, body).Show(line);
            }
        }

        private void Rounded(ShapeVisual into, RectangleF r, float radius, CompositionBrush fill)
        {
            var geometry = c.CreateRoundedRectangleGeometry();
            geometry.Offset = new Vector2(r.X, r.Y);
            geometry.Size = new Vector2(r.Width, r.Height);
            geometry.CornerRadius = new Vector2(radius);
            var shape = c.CreateSpriteShape(geometry);
            shape.FillBrush = fill;
            into.Shapes.Add(shape);
        }

        private void Stroked(ShapeVisual into, RectangleF r, float radius, CompositionBrush stroke)
        {
            var geometry = c.CreateRoundedRectangleGeometry();
            geometry.Offset = new Vector2(r.X, r.Y);
            geometry.Size = new Vector2(r.Width, r.Height);
            geometry.CornerRadius = new Vector2(radius);
            var shape = c.CreateSpriteShape(geometry);
            shape.StrokeBrush = stroke;
            shape.StrokeThickness = 1.5f;
            into.Shapes.Add(shape);
        }
    }
}
