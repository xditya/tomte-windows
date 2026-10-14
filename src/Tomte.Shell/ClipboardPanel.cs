using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Clipboard;
using Tomte.Core.Text;
using Windows.UI.Composition;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>
/// The Clipboard tab: the Keep history switch, the history rows newest first (a picked row is the
/// selected one) with a remove button each, and Clear all; or the off or empty state. Composition
/// visuals in DIPs laid out by <see cref="ClipboardPanelLayout"/>, built when the tab shows and
/// released when it hides. Rows come from the watch's worker, so <see cref="Shown"/> returns at once and
/// the rows are drawn when they land. Row text is the history's shortened form, one line each.
/// </summary>
internal sealed class ClipboardPanel(IslandWindow island, ClipboardWatch watch, Action<bool> keepHistory, TextRenderer text, Action<string>? log) : IPanel
{
    private View? view;
    private IReadOnlyList<ClipboardRow> rows = [];
    private float scroll;
    private long selected = -1;
    private int generation;

    public PanelId Id => PanelId.Clipboard;

    public void Shown(ContainerVisual body, Vector2 size)
    {
        view = new View(island.Compositor, text, body, new ClipboardPanelLayout(size));
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
    }

    public void Press(Vector2 point)
    {
        if (view is null)
        {
            return;
        }

        var (target, index) = view.Layout.Hit(point, rows.Count, scroll, watch.On);
        switch (target)
        {
            case ClipboardTarget.Switch:
                keepHistory(!watch.On);
                break;
            case ClipboardTarget.Row:
                selected = rows[index].Id;
                view.Select(selected);
                Observe(watch.PickAsync(selected), "put the item back");
                break;
            case ClipboardTarget.RemoveRow:
                Observe(watch.RemoveAsync(rows[index].Id), "remove the item");
                break;
            case ClipboardTarget.ClearAll:
                Observe(watch.ClearAsync(), "clear the history");
                break;
        }
    }

    public void Wheel(Vector2 point, int delta)
    {
        if (view is not null)
        {
            scroll = view.Layout.Scrolled(scroll, delta, rows.Count);
            view.Scroll(scroll);
        }
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

        rows = next;
        scroll = Math.Min(scroll, view.Layout.MaxScroll(rows.Count));
        view.Show(on, rows, selected, scroll, DateTimeOffset.Now, watch.Retention.MaxItems);
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
        private static readonly Color Card = Color.FromArgb(15, 255, 255, 255);
        private static readonly Color CardSelected = Color.FromArgb(28, 255, 255, 255);
        private static readonly Color Line = Color.FromArgb(46, 255, 255, 255);
        private static readonly Color Glyph = Color.FromArgb(153, 255, 255, 255);
        private static readonly Color Faint = Color.FromArgb(97, 255, 255, 255);
        private static readonly Color Off = Color.FromArgb(51, 255, 255, 255);
        private static readonly Color On = Color.FromArgb(255, 0x6F, 0xA8, 0xFF);
        private static readonly Color White = Color.FromArgb(255, 255, 255, 255);

        private readonly Compositor c;
        private readonly TextRenderer text;
        private readonly ContainerVisual body;
        private readonly ShapeVisual frame;
        private readonly ContainerVisual list;
        private readonly ContainerVisual rowsVisual;
        private readonly List<(long Id, CompositionColorBrush Fill)> rowBrushes = [];

        // Every text visual of the current build; the rows' own move with the scroll and land on whole pixels again.
        private readonly List<TextVisual> texts = [], rowTexts = [];

        public View(Compositor compositor, TextRenderer text, ContainerVisual body, ClipboardPanelLayout layout)
        {
            (c, this.text, this.body, Layout) = (compositor, text, body, layout);
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

        public void Show(bool on, IReadOnlyList<ClipboardRow> rows, long selected, float scroll, DateTimeOffset now, int keeps)
        {
            frame.Shapes.Clear();
            DisposeTexts();
            var old = rowsVisual.Children.ToList();
            rowsVisual.Children.RemoveAll();
            old.ForEach(row => row.Dispose());
            rowBrushes.Clear();
            foreach (var line in Layout.HeadText(on, keeps))
            {
                Text(texts, body).Show(line);
            }

            DrawSwitch(on);
            if (!on || rows.Count == 0)
            {
                Box(Layout.List, Layout.BoxText(on, keeps));
                return;
            }

            foreach (var line in Layout.FootText())
            {
                Text(texts, body).Show(line);
            }

            float width = Layout.List.Width;
            for (int i = 0; i < rows.Count; i++)
            {
                rowsVisual.Children.InsertAtTop(Row(rows[i], ClipboardPanelLayout.Row(i, width), rows[i].Id == selected, now));
            }

            Scroll(scroll);
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

            // The kind icon: three lines for text, two links for an address.
            const float icon = ClipboardPanelLayout.Icon, gap = ClipboardPanelLayout.Gap;
            float iconY = (at.Height - icon) / 2;
            Rounded(shapes, new RectangleF(gap, iconY, icon, icon), 6, c.CreateColorBrush(CardSelected));
            var glyph = c.CreateColorBrush(Glyph);
            if (row.IsLink)
            {
                Stroked(shapes, new RectangleF(gap + 5, iconY + 9, 9, 6), 3, glyph);
                Stroked(shapes, new RectangleF(gap + 10, iconY + 9, 9, 6), 3, glyph);
            }
            else
            {
                foreach (var (y, w) in (ReadOnlySpan<(float, float)>)[(7, 12), (11, 12), (15, 8)])
                {
                    Rounded(shapes, new RectangleF(gap + 6, iconY + y, w, 2), 1, glyph);
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
                bar.Size = new Vector2(9, 1.5f);
                bar.CornerRadius = new Vector2(0.75f);
                var shape = c.CreateSpriteShape(bar);
                shape.FillBrush = cross;
                shape.CenterPoint = new Vector2(4.5f, 0.75f);
                shape.RotationAngle = angle;
                shape.Offset = mid - new Vector2(4.5f, 0.75f);
                shapes.Shapes.Add(shape);
            }

            foreach (var line in ClipboardPanelLayout.RowText(row.Display, row.Ago(now), at.Width))
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
            shape.FillBrush = c.CreateColorBrush(White);
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
