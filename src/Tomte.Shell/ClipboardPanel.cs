using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Clipboard;
using Tomte.Core.Interaction;
using Tomte.Core.Text;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>
/// The Clipboard tab: the Keep history switch and the filter field in the head row; the history rows newest first,
/// grouped under when they were copied, the selected one raised; beside them the selected row's preview with Paste, Pin,
/// To shelf and Remove; and Clear all, which asks once and keeps pinned items. Or the off or empty state. Composition
/// visuals in DIPs laid out by <see cref="ClipboardPanelLayout"/>, built when the tab shows and released when it hides.
/// Rows come from the watch's worker, so <see cref="Shown"/> returns at once and the rows are drawn when they land. Clicks
/// and keys come through the island's interaction host: arrows move between rows and the preview follows the focus, a
/// click or Enter copies a row again, Delete removes it, and typing in the filter narrows the list.
/// </summary>
internal sealed class ClipboardPanel(IslandWindow island, ClipboardWatch watch, Action<bool> keepHistory, TextRenderer text, TypingFocus focus, Action paste,
    Func<ShelfEntry, Task> toShelf, Action<string>? log) : IPanel, IInteractive
{
    private View? view;
    private EditorView? filterBox;
    private IReadOnlyList<ClipboardRow> rows = [], shown = [];
    private IReadOnlyList<ClipboardLine> lines = [];
    private IReadOnlyList<Target> targets = [];
    private string filter = "";
    private float scroll;
    private long selected = -1;
    private int generation, previewing;
    private bool historyOn, asking;
    private long askedAt, removedAt;
    private Windows.System.DispatcherQueueTimer? askTimer;

    public PanelId Id => PanelId.Clipboard;

    public IReadOnlyList<Target> Targets => targets;

    public event Action? TargetsChanged;

    private ClipboardRow? Selected => shown.FirstOrDefault(r => r.Id == selected);

    public void Shown(ContainerVisual body, Vector2 size)
    {
        var layout = new ClipboardPanelLayout(size);
        view = new View(island.Compositor, text, body, layout, island.Scale, log);
        var box = filterBox = focus.Create(body, multiline: false, TextStyle.Primary.Size);
        box.Place(layout.FilterBox, island.Scale);
        box.Visual.IsVisible = false;
        // Tab and Down leave the filter for the first row it lets through, Shift+Tab for the switch before it.
        box.BeforeKey = (message, wParam, _) =>
        {
            var key = (VIRTUAL_KEY)wParam;
            if (message != PInvoke.WM_KEYDOWN || key is not (VIRTUAL_KEY.VK_TAB or VIRTUAL_KEY.VK_DOWN))
            {
                return false;
            }

            bool back = key == VIRTUAL_KEY.VK_TAB && PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_SHIFT) < 0;
            string to = !back && shown.Count > 0 ? ClipboardPanelLayout.RowId(shown[0].Id) : ClipboardPanelLayout.SwitchId;
            if (!focus.TakeKeys())
            {
                return false;
            }

            island.Interaction.ShowFocus();
            island.Interaction.Focus(this, to);
            return true;
        };
        box.Services.Changed += () => island.Queue.TryEnqueue(() =>
        {
            if (filterBox == box)
            {
                Filtered(box.Services.Text);
            }
        });
        // Esc empties the filter first; with nothing in it, it leaves the box for the tab's label, and a second Esc
        // closes the island as before.
        box.Escape = () =>
        {
            if (box.Services.Text.Length > 0)
            {
                box.Services.Text = "";
                return true;
            }

            if (!focus.TakeKeys())
            {
                return false;
            }

            island.Interaction.FocusTab();
            return true;
        };
        watch.Changed += OnChanged;
        Reload();
    }

    public void Hidden()
    {
        watch.Changed -= OnChanged;
        generation++;
        filterBox?.Dispose();
        filterBox = null;
        view?.Dispose();
        view = null;
        (rows, shown, lines, filter) = ([], [], [], "");
        (scroll, selected) = (0, -1);
        Ask(false);
    }

    /// <summary>A press on the filter field's padding gives its box the keys; everything else comes through the targets.</summary>
    public void Press(Vector2 point)
    {
        if (filterBox is not null && view is not null && historyOn && view.Layout.Filter.Contains(point.X, point.Y) && !filterBox.Box.Contains(point.X, point.Y))
        {
            focus.Take(filterBox);
        }
    }

    /// <summary>
    /// A click within the double-click time of the last one is dropped on Clear all's question and on Remove: a double or
    /// triple click would answer the question before it could be read, or take the next row too once the first has gone.
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
        else if (id == ClipboardPanelLayout.FilterId)
        {
            if (filterBox is not null && focus.HasKeys)
            {
                focus.Take(filterBox);
            }
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
        else if (Selected is { } row && Enum.GetValues<ClipboardPanelLayout.PreviewAction>().FirstOrDefault(a => ClipboardPanelLayout.ActionId(a) == id) is var action && ClipboardPanelLayout.ActionId(action) == id)
        {
            Act(action, row, id);
        }
        else if (Find(id) is { } index)
        {
            Select(shown[index].Id);
            Pick(shown[index], id);
        }
    }

    /// <summary>On a row, Delete removes it and Ctrl+C copies its entry again, as Enter does; a held key acts once.</summary>
    public bool KeyDown(string id, NavKey key, bool repeat)
    {
        if (key is not (NavKey.Remove or NavKey.Copy) || Find(id) is not { } index)
        {
            return false;
        }

        if (!repeat && key == NavKey.Remove)
        {
            Observe(watch.RemoveAsync(shown[index].Id), "remove the item");
        }
        else if (!repeat)
        {
            Pick(shown[index], ClipboardPanelLayout.RowId(shown[index].Id));
        }

        return true;
    }

    /// <summary>
    /// Focus or the pointer on a row not wholly in view scrolls the list just far enough to show all of it; keyboard focus
    /// on a row also selects it, so the preview follows the arrows.
    /// </summary>
    public void Hovered(string? id)
    {
        if (view is null || id is null || Find(id) is not { } index)
        {
            return;
        }

        if (island.Interaction.UsingKeys && island.Interaction.IsFocus(this, id))
        {
            Select(shown[index].Id);
        }

        if (view.Layout.ScrolledTo(lines, index, scroll) is var to && to != scroll)
        {
            scroll = to;
            view.Scroll(scroll);
            Retarget();
        }
    }

    public void Wheel(Vector2 point, int delta)
    {
        if (view is not null)
        {
            scroll = view.Layout.Scrolled(scroll, delta, lines);
            view.Scroll(scroll);
            Retarget();
        }
    }

    private static bool Soon(long since) => Environment.TickCount64 - since < PInvoke.GetDoubleClickTime();

    // The shown row a target stands for.
    private int? Find(string id)
    {
        for (int i = 0; i < shown.Count; i++)
        {
            if (id == ClipboardPanelLayout.RowId(shown[i].Id))
            {
                return i;
            }
        }

        return null;
    }

    /// <summary>The row goes back on the clipboard; once it has gone to the listener, "Copied" shows at the row.</summary>
    private async void Pick(ClipboardRow row, string id)
    {
        int shownAs = generation;
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

    /// <summary>
    /// A preview button. Paste puts the item back on the clipboard and pastes it into the app that had the keys, once the
    /// island has gone; Pin pins or unpins; To shelf puts the whole item on the shelf; Remove deletes it.
    /// </summary>
    private async void Act(ClipboardPanelLayout.PreviewAction action, ClipboardRow row, string id)
    {
        int shownAs = generation;
        try
        {
            switch (action)
            {
                case ClipboardPanelLayout.PreviewAction.Paste:
                    if (await watch.PickAsync(row.Id) && shownAs == generation)
                    {
                        paste();
                    }

                    break;
                case ClipboardPanelLayout.PreviewAction.Pin:
                    await watch.PinAsync(row.Id, !row.Pinned);
                    break;
                case ClipboardPanelLayout.PreviewAction.Shelf:
                    ShelfEntry? entry = null;
                    if (row.IsImage && await watch.ImageAsync(row.Id) is { } png)
                    {
                        entry = new ShelfEntry(ShelfKind.Content, $"Image {row.Width}x{row.Height}.png", Content: png);
                    }
                    else if (!row.IsImage && await watch.TextAsync(row.Id) is { } words)
                    {
                        var (name, bytes) = ShelfFormats.FromText(words);
                        entry = new ShelfEntry(ShelfKind.Content, name, Content: bytes);
                    }

                    if (entry is not null)
                    {
                        await toShelf(entry);
                        if (shownAs == generation)
                        {
                            island.Interaction.Acknowledge(this, id, "On the shelf");
                        }
                    }

                    break;
                default:
                    if (!Soon(removedAt))
                    {
                        removedAt = Environment.TickCount64;
                        await watch.RemoveAsync(row.Id);
                    }

                    break;
            }
        }
        catch (Exception e)
        {
            log?.Invoke($"clipboard panel: {action} did not work ({e.GetType().Name})");
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

        view?.ShowClearAll(asking, watch.Retention);
        Retarget();
    }

    private void Retarget()
    {
        targets = view is null ? [] : view.Layout.Targets(historyOn, shown, lines, scroll, asking, Selected, rows.Count > 0);
        TargetsChanged?.Invoke();
    }

    /// <summary>Another row is selected: its fill and keycap, and its preview.</summary>
    private void Select(long id)
    {
        if (id == selected || view is null)
        {
            return;
        }

        long was = selected;
        selected = id;
        view.Select(was, selected, DateTimeOffset.Now);
        Preview();
        Retarget();
    }

    /// <summary>The selected row's preview: drawn at once from the row, then again with its whole text or its picture.</summary>
    private async void Preview()
    {
        int asked = ++previewing, shownAs = generation;
        var row = Selected;
        view?.Preview(row, null, null);
        if (row is null || view is null || !view.Layout.HasPreview)
        {
            return;
        }

        try
        {
            if (row.IsImage)
            {
                var at = view.Layout.PreviewImage(row.Width, row.Height);
                int w = Math.Max(1, (int)Math.Round(at.Width * island.Scale)), h = Math.Max(1, (int)Math.Round(at.Height * island.Scale));
                if (await watch.ImageAsync(row.Id) is { } png && asked == previewing && shownAs == generation)
                {
                    var compositor = island.Compositor;
                    var (surfaces, brush) = await Task.Run(() =>
                    {
                        var pixels = ClipboardImages.Thumbnail(png, w, h);
                        var made = new PixelSurfaces(compositor);
                        return (made, made.Brush(w, h, pixels));
                    });
                    if (asked != previewing || shownAs != generation || view is null)
                    {
                        surfaces.Dispose();
                        return;
                    }

                    view.Preview(row, null, (surfaces, brush));
                }
            }
            else if (await watch.TextAsync(row.Id) is { } whole && asked == previewing && shownAs == generation)
            {
                view?.Preview(row, whole, null);
            }
        }
        catch (Exception e)
        {
            log?.Invoke($"clipboard panel: no preview ({e.GetType().Name})");
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

        (rows, historyOn) = (next, on);
        Draw(keepScroll: true);
    }

    private void Filtered(string typed)
    {
        if (typed == filter || view is null)
        {
            return;
        }

        filter = typed;
        scroll = 0;
        Draw(keepScroll: false);
    }

    /// <summary>Draws the rows the filter lets through; the selection stays on its row while it shows, else goes to the newest.</summary>
    private void Draw(bool keepScroll)
    {
        if (view is null)
        {
            return;
        }

        var now = DateTimeOffset.Now;
        shown = [.. rows.Where(r => r.Matches(filter))];
        lines = ClipboardPanelLayout.Lines(shown, now);
        if (!shown.Any(r => r.Id == selected))
        {
            selected = shown.Count > 0 ? shown[0].Id : -1;
        }

        scroll = keepScroll ? Math.Min(scroll, view.Layout.MaxScroll(lines)) : 0;
        if (filterBox is not null)
        {
            filterBox.Visual.IsVisible = historyOn;
            if (!historyOn && focus.Focused == filterBox)
            {
                focus.TakeKeys();
            }
        }

        view.Show(historyOn, shown, lines, selected, scroll, now, watch.Retention, asking, filter.Length > 0, rows.Count > 0);
        if (filterBox?.Visual.Parent is { } parent)
        {
            // The field's capsule was drawn again, on top: the box goes back over it.
            parent.Children.Remove(filterBox.Visual);
            parent.Children.InsertAtTop(filterBox.Visual);
        }

        Preview();
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
        private static readonly Color Clear = default;
        private static Color Card => ShelfColors.Of(Theme.Card);
        private static Color Raised => ShelfColors.Of(Theme.Raised);
        private static Color Edge => ShelfColors.Of(Theme.Dash);
        private static Color Glyph => ShelfColors.Of(Theme.Ink2);
        private static Color Faint => ShelfColors.Of(Theme.Ink3);
        private static Color Off => ShelfColors.Of(Theme.LineStrong);
        private static Color Knob => ShelfColors.Of(Theme.Ink);
        private static Color Ink => ShelfColors.Of(Theme.Ink);
        private static Color Link => ShelfColors.Of(Theme.Link);

        private readonly Compositor c;
        private readonly TextRenderer text;
        private readonly ContainerVisual body;
        private readonly ContainerVisual list;
        private readonly ContainerVisual rowsVisual;
        private readonly Dictionary<long, RowParts> rowParts = [];
        private readonly double scale;
        private readonly Action<string>? log;
        private Parts? frame, listParts, preview;
        private PixelSurfaces? previewSurfaces;
        private ClipboardThumbs? thumbs;
        private TextVisual? clearWord, footWords;

        public View(Compositor compositor, TextRenderer text, ContainerVisual body, ClipboardPanelLayout layout, double scale, Action<string>? log)
        {
            (c, this.text, this.body, Layout, this.scale, this.log) = (compositor, text, body, layout, scale, log);

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

        // One row's parts: its own shapes, its fill and inner line (recoloured when it is selected), and what changes
        // with that (its words and its end), rebuilt then.
        private sealed record RowParts(ClipboardRow Row, ShapeVisual Shapes, CompositionColorBrush Fill, CompositionColorBrush Line)
        {
            public Parts? Words { get; set; }
        }

        public void Show(bool on, IReadOnlyList<ClipboardRow> rows, IReadOnlyList<ClipboardLine> lines, long selected, float scroll, DateTimeOffset now, ClipboardRetention kept, bool asking, bool filtered, bool any)
        {
            ClearRows();
            frame?.Dispose();
            var f = frame = new Parts(c, text);
            var into = f.Shapes(body, new RectangleF(0, 0, Layout.Body.X, Layout.Body.Y));
            thumbs = new ClipboardThumbs(c, scale, log);

            // The head row: the switch's label and track, and while history is on the filter field.
            f.Text(into, new TextSlot(ClipboardPanelLayout.KeepHistory, TextStyle.Secondary, RectangleF.FromLTRB(ClipboardPanelLayout.Pad, Layout.Head.Y, Layout.Switch.X, Layout.Head.Bottom)));
            var track = Layout.Switch;
            f.Box(into, track, track.Height / 2, f.Brush(on ? ShelfColors.Accent : Off));
            const float knob = 14;
            f.Box(into, new RectangleF(on ? track.Right - 3 - knob : track.X + 3, track.Y + (track.Height - knob) / 2, knob, knob), knob / 2, f.Brush(Knob));
            if (on)
            {
                var field = Layout.Filter;
                f.Box(into, field, field.Height / 2, f.Brush(Card));
                f.Glyph(into, Glyphs.Search, Faint, Layout.FilterGlyphBox);
                Hint = f.Text(into, new TextSlot(filtered ? "" : ClipboardPanelLayout.FilterHint, ClipboardPanelLayout.HintStyle, Layout.FilterBox));
            }

            if (!on || rows.Count == 0)
            {
                var box = Layout.Box;
                f.Box(into, box, ClipboardPanelLayout.PreviewCorner, null, f.Brush(Off), 1);
                foreach (var line in Layout.BoxText(on, kept, filtered && any))
                {
                    f.Text(into, line);
                }
            }

            if (on && any)
            {
                f.Glyph(into, Glyphs.Lock, Faint, Layout.LockBox);
                var foot = Layout.FootText(asking, kept);
                footWords = f.Text(into, foot[0]);
                clearWord = f.Text(into, foot[1]);
            }

            if (!on || rows.Count == 0)
            {
                Preview(null, null, null);
                return;
            }

            var lp = listParts = new Parts(c, text);
            float width = Layout.List.Width;
            foreach (var line in lines)
            {
                if (line.IsHeading)
                {
                    lp.Text(rowsVisual, ClipboardPanelLayout.HeadingSlot(line, width));
                    continue;
                }

                var row = rows[line.Row];
                rowParts[row.Id] = Row(row, new RectangleF(0, line.Y, width, line.Height), row.Id == selected, now);
            }

            Scroll(scroll);
            thumbs.Load();
        }

        /// <summary>The filter's hint, shown while nothing is typed.</summary>
        public TextVisual? Hint { get; private set; }

        /// <summary>Clear all's word, or its question in the warn colour.</summary>
        public void ShowClearAll(bool asking, ClipboardRetention kept)
        {
            if (clearWord is not null && footWords is not null)
            {
                var foot = Layout.FootText(asking, kept);
                footWords.Show(foot[0]);
                clearWord.Show(foot[1]);
            }
        }

        /// <summary>The selection moved: both rows recoloured (never swapped: a brush taken off its shape and put back does not draw again) and their words rebuilt.</summary>
        public void Select(long was, long now, DateTimeOffset at)
        {
            foreach (long id in (ReadOnlySpan<long>)[was, now])
            {
                if (rowParts.TryGetValue(id, out var parts))
                {
                    bool on = id == now;
                    parts.Fill.Color = on ? Raised : Clear;
                    parts.Line.Color = on ? Edge : Clear;
                    Words(parts, on, at);
                }
            }
        }

        public void Scroll(float scroll)
        {
            rowsVisual.Offset = new Vector3(0, -scroll, 0);
            foreach (var visual in (listParts?.Texts ?? []).Concat(rowParts.Values.SelectMany(r => r.Words?.Texts ?? [])))
            {
                visual.Refresh();
            }
        }

        /// <summary>
        /// The preview card for <paramref name="row"/>: its meta line, its text (the row's start until the whole text came)
        /// or its picture, and the buttons. Nothing without a row or the room for it.
        /// </summary>
        public void Preview(ClipboardRow? row, string? whole, (PixelSurfaces Surfaces, CompositionSurfaceBrush Brush)? picture)
        {
            preview?.Dispose();
            preview = null;
            previewSurfaces?.Dispose();
            previewSurfaces = picture?.Surfaces;
            if (row is null || !Layout.HasPreview)
            {
                picture?.Surfaces.Dispose();
                previewSurfaces = null;
                return;
            }

            var p = preview = new Parts(c, text);
            var card = Layout.Preview;
            var into = p.Shapes(body, new RectangleF(0, 0, Layout.Body.X, Layout.Body.Y));
            p.Box(into, card, ClipboardPanelLayout.PreviewCorner, p.Brush(Card));
            p.Text(into, Layout.PreviewMeta(row.Meta()));
            var area = Layout.PreviewArea;
            if (row.IsImage)
            {
                if (picture is { } shown)
                {
                    p.Sprite(body, Layout.PreviewImage(row.Width, row.Height), 6, shown.Brush);
                }
            }
            else if (row.Colour is { } colour)
            {
                p.Box(into, new RectangleF(area.X, area.Y, Math.Min(area.Width, 96), Math.Min(area.Height, 64)), 10, p.Brush(ShelfColors.Rgb(colour)));
            }
            else
            {
                p.Text(into, Layout.PreviewText(whole ?? row.Display), new Vector2(area.X, area.Y));
            }

            foreach (var (action, box) in Layout.Actions())
            {
                bool paste = action == ClipboardPanelLayout.PreviewAction.Paste;
                p.Box(into, box, ClipboardPanelLayout.ButtonCorner, p.Brush(paste ? Ink : Raised));
                if (action == ClipboardPanelLayout.PreviewAction.Remove)
                {
                    const float glyph = 16;
                    p.Glyph(into, Glyphs.Close, Glyph, new RectangleF(box.X + (box.Width - glyph) / 2, box.Y + (box.Height - glyph) / 2, glyph, glyph));
                }
                else
                {
                    p.Text(into, new TextSlot(ClipboardPanelLayout.ActionText(action, row.Pinned), paste ? ClipboardPanelLayout.PasteStyle : ClipboardPanelLayout.SecondaryButtonStyle, box));
                }
            }
        }

        public void Dispose()
        {
            ClearRows();
            frame?.Dispose();
            Preview(null, null, null);
            body.Children.RemoveAll();
            rowsVisual.Dispose();
            list.Dispose();
        }

        private void ClearRows()
        {
            thumbs?.Dispose();
            thumbs = null;
            listParts?.Dispose();
            listParts = null;
            foreach (var parts in rowParts.Values)
            {
                parts.Words?.Dispose();
            }

            rowParts.Clear();
            var old = rowsVisual.Children.ToList();
            rowsVisual.Children.RemoveAll();
            old.ForEach(row => row.Dispose());
            (clearWord, footWords, Hint) = (null, null, null);
        }

        private RowParts Row(ClipboardRow row, RectangleF at, bool selected, DateTimeOffset now)
        {
            var shapes = c.CreateShapeVisual();
            shapes.Size = new Vector2(at.Width, at.Height);
            shapes.Offset = new Vector3(at.X, at.Y, 0);
            rowsVisual.Children.InsertAtTop(shapes);
            var fill = c.CreateColorBrush(selected ? Raised : Clear);
            var line = c.CreateColorBrush(selected ? Edge : Clear);
            var local = new RectangleF(0, 0, at.Width, at.Height);
            Rounded(shapes, local, ClipboardPanelLayout.Corner, fill, null);
            Rounded(shapes, RectangleF.Inflate(local, -0.5f, -0.5f), ClipboardPanelLayout.Corner - 0.5f, null, line);

            // What kind of item it is: the thumbnail, the colour, the link in the link colour, or lines of text.
            var icon = ClipboardPanelLayout.IconIn;
            if (row.Thumb is { } thumb)
            {
                thumbs!.Add(shapes, thumb);
            }
            else if (row.Colour is { } colour)
            {
                Rounded(shapes, icon, ClipboardPanelLayout.SwatchCorner, c.CreateColorBrush(ShelfColors.Rgb(colour)), null);
            }
            else
            {
                var holder = c.CreateContainerShape();
                holder.Offset = new Vector2(icon.X, icon.Y);
                shapes.Shapes.Add(holder);
                PaletteView.DrawGlyph(holder, row.IsLink ? Glyphs.Link : Glyphs.Text, c.CreateColorBrush(row.IsLink ? Link : Glyph), icon.Width);
            }

            var parts = new RowParts(row, shapes, fill, line);
            Words(parts, selected, now);
            return parts;
        }

        // A row's words and its end: the age, the pin, or the Enter keycap while it is selected.
        private void Words(RowParts parts, bool selected, DateTimeOffset now)
        {
            parts.Words?.Dispose();
            var w = parts.Words = new Parts(c, text);
            var row = parts.Row;
            float width = parts.Shapes.Size.X;
            var end = selected ? ClipboardPanelLayout.RowEnd.Enter : row.Pinned ? ClipboardPanelLayout.RowEnd.Pin : ClipboardPanelLayout.RowEnd.Age;
            var into = w.Shapes(parts.Shapes, new RectangleF(0, 0, width, ClipboardPanelLayout.RowHeight));
            foreach (var line in ClipboardPanelLayout.RowText(row, row.When(now), end, width))
            {
                w.Text(into, line);
            }

            if (end == ClipboardPanelLayout.RowEnd.Enter)
            {
                w.Keycap(into, ClipboardPanelLayout.EnterIn(width), ClipboardPanelLayout.Enter);
            }
            else if (end == ClipboardPanelLayout.RowEnd.Pin)
            {
                w.Glyph(into, Glyphs.Pin, ShelfColors.Accent, ClipboardPanelLayout.PinIn(width));
            }
        }

        private void Rounded(ShapeVisual into, RectangleF r, float radius, CompositionBrush? fill, CompositionBrush? stroke)
        {
            var geometry = c.CreateRoundedRectangleGeometry();
            geometry.Offset = new Vector2(r.X, r.Y);
            geometry.Size = new Vector2(r.Width, r.Height);
            geometry.CornerRadius = new Vector2(radius);
            var shape = c.CreateSpriteShape(geometry);
            shape.FillBrush = fill;
            shape.StrokeBrush = stroke;
            shape.StrokeThickness = stroke is null ? 0 : 1;
            into.Shapes.Add(shape);
        }
    }
}
