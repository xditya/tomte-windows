using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Search;
using Tomte.Core.Shortcuts;
using Tomte.Core.Text;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>
/// The search box on the island, in the Palette state and the AI box's look: a one-line text box after the tomte, the
/// results under it by group with a heading each, and a footer with the keys and where files come from. The box keeps
/// every key: Up and Down move the selection through all rows and wrap, Tab and Shift+Tab jump to the next or previous
/// group, Enter opens, Ctrl+Enter shows the row's file in its folder, Page Up and Page Down move a page, Esc clears the
/// box and then closes. Left, Right, Home and End stay the box's. The selected row always shows, filled and outlined;
/// the pointer's hover is the interaction host's, and a click opens a row. A card (getting Everything) can take the
/// list's place: Tab moves between its buttons, Enter presses the focused one, which starts on the safe one. Icons come
/// from the shell, read off this thread and kept for the session. Island thread only.
/// </summary>
internal sealed class SearchView : IDisposable, IInteractive
{
    private const string RowId = "row:", CardId = "card:";
    private const int MaxImages = 512;
    private const float WheelStep = 3 * SearchLayout.RowHeight;
    private static readonly Color CardFill = Color.FromArgb(15, 255, 255, 255);

    // Shell icons as pixels, by source and size, for the session: a new search shows them at once.
    // dropped all at once when full; an LRU if a session's searches ever need more.
    private static readonly Dictionary<(string Source, int Pixels), ShellImage?> Images = [];

    private readonly IslandWindow island;
    private readonly IslandStateMachine machine;
    private readonly TypingFocus focus;
    private readonly TextRenderer text;
    private readonly float width, tallest;
    private readonly ContainerVisual root, band, scrolled, cardLayer;
    private readonly ShapeVisual shapes, selection, cardShapes;
    private readonly CompositionRoundedRectangleGeometry cardBox;
    private readonly CompositionColorBrush boxLine;
    private readonly EditorView editor;
    private readonly TextVisual placeholder, footLeft, footRight, empty, cardTitle, cardBody;
    private readonly List<TextVisual> headings = [];
    private readonly List<RowParts> parts = [];
    private readonly List<(CompositionRoundedRectangleGeometry Pill, CompositionColorBrush Fill, TextVisual Label)> buttons = [];
    private readonly Dictionary<string, CompositionSurfaceBrush?> brushes = [];
    private IReadOnlyList<SearchItem> rows = [];
    private IReadOnlyList<SearchLine> lines = [];
    private IReadOnlyList<Target> targets = [];
    private SearchCard? card;
    private int cardFocus;
    private PixelSurfaces? surfaces;
    private SearchLayout layout;
    private string typed = "", source = "", hint = "";

    // The text the rows on show were found for.
    private string? shownFor;

    // The user moved the selection for this text (a key or a page); only then do later results keep it.
    private bool moved;
    private int select;
    private float scroll, content;
    private bool fadeInPending = true, disposed, loadingIcons, iconsAgain;

    /// <param name="tallest">The most the box grows to: it opens at the palette's smallest and grows with what it shows.</param>
    public SearchView(IslandWindow island, IslandStateMachine machine, TextRenderer text, TypingFocus focus, float width, float tallest)
    {
        this.island = island;
        this.machine = machine;
        this.text = text;
        this.focus = focus;
        this.width = width;
        this.tallest = tallest;
        layout = new SearchLayout(width, island.Mascot.Enabled, (float)PanelSize.PaletteMinHeight);
        var c = island.Compositor;
        root = c.CreateContainerVisual();
        root.Offset = new Vector3(-width / 2, 0, 0);
        root.Opacity = 0;
        shapes = Shapes(root, new Vector2(width, tallest));
        Rounded(shapes, layout.Box, PaletteLayout.PromptCorner, c.CreateColorBrush(CardFill));
        boxLine = c.CreateColorBrush(Argb(PaletteLayout.PromptFocusLine));
        ((CompositionSpriteShape)shapes.Shapes[^1]).StrokeBrush = boxLine;
        ((CompositionSpriteShape)shapes.Shapes[^1]).StrokeThickness = 1;

        // The list scrolls in a band clipped to its box; the selection is drawn under the rows' text.
        var area = layout.Band;
        band = c.CreateContainerVisual();
        band.Offset = new Vector3(area.X, area.Y, 0);
        band.Size = new Vector2(area.Width, area.Height);
        band.Clip = c.CreateInsetClip();
        scrolled = c.CreateContainerVisual();
        band.Children.InsertAtTop(scrolled);
        root.Children.InsertAtTop(band);
        selection = Shapes(scrolled, new Vector2(area.Width, SearchLayout.RowHeight));
        Rounded(selection, new RectangleF(0.5f, 0.5f, area.Width - 1, SearchLayout.RowHeight - 1), SearchLayout.RowCorner, c.CreateColorBrush(Argb(SearchLayout.SelectedFill)));
        ((CompositionSpriteShape)selection.Shapes[^1]).StrokeBrush = c.CreateColorBrush(Argb(SearchLayout.SelectedLine));
        ((CompositionSpriteShape)selection.Shapes[^1]).StrokeThickness = 1;
        selection.IsVisible = false;
        empty = text.Create(root);

        cardLayer = c.CreateContainerVisual();
        root.Children.InsertAtTop(cardLayer);
        cardShapes = Shapes(cardLayer, new Vector2(width, tallest));
        cardBox = Rounded(cardShapes, RectangleF.Empty, PaletteLayout.PromptCorner, c.CreateColorBrush(CardFill));
        ((CompositionSpriteShape)cardShapes.Shapes[^1]).StrokeBrush = c.CreateColorBrush(Argb(PaletteLayout.AskOutline));
        ((CompositionSpriteShape)cardShapes.Shapes[^1]).StrokeThickness = 1;
        cardTitle = text.Create(cardLayer);
        cardBody = text.Create(cardLayer);
        cardLayer.IsVisible = false;

        placeholder = text.Create(root);
        footLeft = text.Create(root);
        footRight = text.Create(root);
        editor = focus.Create(root, multiline: false, PaletteLayout.PromptSize);
        editor.KeepsKeys = true;
        editor.Place(layout.Editor, island.Scale);
        editor.BeforeKey = OnKey;
        editor.Escape = OnEscape;
        // Read on the next turn: nothing may call back into the services inside their own notification.
        editor.Services.Changed += () => island.Queue.TryEnqueue(Read);

        island.Dips.Children.InsertAtTop(root);
        island.ContentReady += FadeIn;
        island.Pressed += OnPressed;
        island.Wheel += OnWheel;
        island.LayoutChanged += Relayout;
        machine.Changed += OnState;
        Draw();
    }

    /// <summary>The text changed: the host searches again.</summary>
    public event Action<string>? Typed;

    /// <summary>The box's height for what it shows, in DIPs. It only grows while the box is open, so the shape settles once.</summary>
    public float Height => layout.Height;

    /// <summary>The box grew; the island morphs to the new height, gently.</summary>
    public event Action? HeightChanged;

    /// <summary>A row was clicked: it opens.</summary>
    public event Action<SearchItem>? Clicked;

    /// <summary>Enter (false) or Ctrl+Enter (true) in the box: the host opens the selected row, or shows it in its folder.</summary>
    public event Action<bool>? Entered;

    /// <summary>A card's button, by its id: a click, or Enter on the focused one. Esc says null.</summary>
    public event Action<string?>? CardAnswered;

    public string Text => typed;

    public IReadOnlyList<SearchItem> Rows => rows;

    public SearchItem? Selected => select >= 0 && select < rows.Count ? rows[select] : null;

    /// <summary>The card in the list's place; null with none.</summary>
    public SearchCard? Card => card;

    /// <summary>The card's button Enter presses now; null with no card.</summary>
    public string? CardButton => card is { Buttons.Count: > 0 } c ? c.Buttons[Math.Clamp(cardFocus, 0, c.Buttons.Count - 1)].Label : null;

    public IReadOnlyList<Target> Targets => targets;

    public event Action? TargetsChanged;

    /// <summary>The box takes the keys. False when Windows refused.</summary>
    public bool TakeKeys() => !disposed && focus.Take(editor);

    /// <summary>Puts text in the box with the caret after it, as typing would (the debug driver).</summary>
    public void Fill(string words)
    {
        if (!disposed)
        {
            editor.Services.Text = words;
            editor.Services.Selection = (words.Length, words.Length);
        }
    }

    /// <summary>
    /// New results. The first row is selected, so Enter opens the best match, never the row that offers Everything; once
    /// the user has moved the selection, results that arrive later for the same text (files, the app list) keep that row,
    /// wherever it now is, so Enter never opens something else than what is highlighted. <paramref name="note"/> shows
    /// when there are none.
    /// </summary>
    public void Show(IReadOnlyList<SearchItem> results, string files, string note)
    {
        if (disposed)
        {
            return;
        }

        moved &= shownFor == typed;
        string? kept = moved ? Selected?.Key : null;
        bool same = results.SequenceEqual(rows);
        rows = results;
        source = files;
        hint = note;
        if (!same)
        {
            var list = rows.ToList();
            int at = kept is null ? -1 : list.FindIndex(r => r.Key == kept);
            select = at >= 0 ? at : list.FindIndex(r => r.Kind != SearchKind.GetEverything);
            scroll = at >= 0 ? scroll : 0;
        }

        shownFor = typed;

        lines = SearchLayout.Lines(rows);
        content = lines.Count > 0 ? lines[^1].Top + lines[^1].Height : 0;
        Draw();
        _ = IconsAsync();
    }

    /// <summary>A card in the list's place, its safe button focused, or null for the list again.</summary>
    public void ShowCard(SearchCard? shown)
    {
        if (!disposed)
        {
            card = shown;
            cardFocus = shown?.Focus ?? 0;
            Draw();
        }
    }

    public void Activate(string id)
    {
        if (disposed || machine.State != IslandState.Palette)
        {
            return;
        }

        if (id.StartsWith(CardId, StringComparison.Ordinal))
        {
            CardAnswered?.Invoke(id[CardId.Length..]);
        }
        else if (int.TryParse(id.AsSpan(RowId.Length), out int i) && i < rows.Count)
        {
            select = i;
            Draw();
            Clicked?.Invoke(rows[i]);
        }
    }

    /// <summary>Page Up and Page Down reach here from the box: the selection moves a page.</summary>
    public bool Command(NavKey key)
    {
        if (disposed || card is not null || key is not (NavKey.PageUp or NavKey.PageDown) || rows.Count == 0)
        {
            return false;
        }

        int page = Math.Max(1, (int)(layout.Band.Height / SearchLayout.RowHeight) - 1);
        Select(Math.Clamp(select + (key == NavKey.PageDown ? page : -page), 0, rows.Count - 1));
        return true;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        island.Interaction.Unregister(this);
        island.ContentReady -= FadeIn;
        island.Pressed -= OnPressed;
        island.Wheel -= OnWheel;
        island.LayoutChanged -= Relayout;
        machine.Changed -= OnState;
        editor.Dispose();
        foreach (var visual in headings.Concat(parts.SelectMany(p => new[] { p.Name, p.Detail, p.Kind })).Concat(buttons.Select(b => b.Label))
            .Append(placeholder).Append(footLeft).Append(footRight).Append(empty).Append(cardTitle).Append(cardBody))
        {
            visual.Dispose();
        }

        foreach (var part in parts)
        {
            part.Icon.Dispose();
        }

        surfaces?.Dispose();
        island.Dips.Children.Remove(root);
        root.Dispose();
    }

    private static Color Argb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);

    private ShapeVisual Shapes(ContainerVisual host, Vector2 size)
    {
        var visual = island.Compositor.CreateShapeVisual();
        visual.Size = size;
        host.Children.InsertAtTop(visual);
        return visual;
    }

    private CompositionRoundedRectangleGeometry Rounded(ShapeVisual into, RectangleF at, float corner, CompositionColorBrush fill)
    {
        var geometry = island.Compositor.CreateRoundedRectangleGeometry();
        geometry.CornerRadius = new Vector2(corner);
        Place(geometry, at);
        var shape = island.Compositor.CreateSpriteShape(geometry);
        shape.FillBrush = fill;
        into.Shapes.Add(shape);
        return geometry;
    }

    private static void Place(CompositionRoundedRectangleGeometry geometry, RectangleF at)
    {
        geometry.Offset = new Vector2(at.X, at.Y);
        geometry.Size = new Vector2(at.Width, at.Height);
    }

    // Everything drawn again from the rows, the selection, the scroll and the card; cheap enough for each keystroke.
    private void Draw()
    {
        if (disposed)
        {
            return;
        }

        Grow();
        placeholder.Show(typed.Length == 0 ? SearchLayout.Placeholder : "", PaletteLayout.Placeholder, layout.Editor);
        boxLine.Color = Argb(focus.Focused == editor ? PaletteLayout.PromptFocusLine : PaletteLayout.PromptLine);
        var (left, right) = layout.Footer(card is not null ? "" : typed.Length > 0 ? SearchLayout.Keys : SearchLayout.KeysEmpty, card is null ? source : "");
        footLeft.Show(left);
        footRight.Show(right);
        band.IsVisible = card is null;
        cardLayer.IsVisible = card is not null;
        if (card is not null)
        {
            DrawCard(card);
            empty.Show("", TextStyle.Secondary, RectangleF.Empty);
            Retarget();
            return;
        }

        var area = layout.Band;
        empty.Show(new TextRun(rows.Count == 0 ? hint : "", TextStyle.Secondary, area.Width - (2 * SearchLayout.RowPadX), 3), new Vector2(area.X + SearchLayout.RowPadX, area.Y + 8));
        int heading = 0, row = 0;
        foreach (var line in lines)
        {
            if (line.Heading is { } label)
            {
                Heading(heading++).Show(label, SearchLayout.Heading, new RectangleF(SearchLayout.RowPadX, line.Top, area.Width - (2 * SearchLayout.RowPadX), line.Height));
                continue;
            }

            var item = rows[line.Row];
            var (_, icon, name, detail, kind) = layout.Row(line.Top, item);
            var p = Row(row++);
            p.Source = item.Icon;
            p.Icon.Offset = new Vector3(icon.X, icon.Y, 0);
            p.Icon.Size = new Vector2(icon.Width, icon.Height);
            p.Icon.Brush = item.Icon is { } iconSource ? brushes.GetValueOrDefault(iconSource) : null;
            p.Icon.IsVisible = true;
            p.Name.Show(name);
            p.Detail.Show(detail);
            p.Kind.Show(kind);
        }

        for (int i = heading; i < headings.Count; i++)
        {
            headings[i].Show("", SearchLayout.Heading, RectangleF.Empty);
        }

        for (int i = row; i < parts.Count; i++)
        {
            parts[i].Hide();
        }

        Scrolled();
    }

    // The box grows to what it shows (the list, the hint or a card), up to its tallest; it never shrinks while open, so a
    // typed letter that shortens the list does not move the shape back and forth.
    private void Grow()
    {
        float next = SearchLayout.Fit(width, island.Mascot.Enabled, rows, card, tallest);
        if (next <= layout.Height)
        {
            return;
        }

        layout = new SearchLayout(width, island.Mascot.Enabled, next);
        var area = layout.Band;
        band.Offset = new Vector3(area.X, area.Y, 0);
        band.Size = new Vector2(area.Width, area.Height);
        HeightChanged?.Invoke();
    }

    // The selection's box, the scroll that keeps it in view, and the targets for the rows that show.
    private void Scrolled()
    {
        var line = lines.FirstOrDefault(l => l.Heading is null && l.Row == select);
        selection.IsVisible = select >= 0 && line.Height > 0;
        if (selection.IsVisible)
        {
            // The heading above the first row of a group comes into view with it.
            float top = lines.LastOrDefault(l => l.Top < line.Top && l.Heading is not null && l.Top + l.Height == line.Top) is { Height: > 0 } above ? above.Top : line.Top;
            scroll = layout.ScrollTo(scroll, top, line.Top + line.Height, content);
            selection.Offset = new Vector3(0, line.Top, 0);
        }

        scroll = Math.Clamp(scroll, 0, Math.Max(0, content - layout.Band.Height));
        scrolled.Offset = new Vector3(0, -scroll, 0);
        foreach (var visual in headings.Concat(parts.SelectMany(p => new[] { p.Name, p.Detail, p.Kind })))
        {
            visual.Refresh();
        }

        Retarget();
    }

    private void Retarget()
    {
        var list = new List<Target>();
        var area = layout.Band;
        if (card is not null)
        {
            foreach (var (id, at, _) in layout.Card(card, cardFocus).Buttons)
            {
                list.Add(new Target(CardId + id, at, PaletteLayout.ButtonCorner, TargetKind.Button));
            }
        }
        else
        {
            foreach (var line in lines.Where(l => l.Heading is null))
            {
                // Only the part of a row inside the band takes the pointer.
                var at = RectangleF.Intersect(new RectangleF(area.X, area.Y + line.Top - scroll, area.Width, line.Height), area);
                if (at.Height > 0)
                {
                    list.Add(new Target(RowId + line.Row, at, SearchLayout.RowCorner, TargetKind.Row));
                }
            }
        }

        if (!list.SequenceEqual(targets))
        {
            targets = list;
            TargetsChanged?.Invoke();
        }
    }

    private void DrawCard(SearchCard shown)
    {
        var (at, title, body, bodyAt, laid) = layout.Card(shown, cardFocus);
        Place(cardBox, at);
        cardTitle.Show(title);
        cardBody.Show(body, new Vector2(bodyAt.X, bodyAt.Y));
        while (buttons.Count < laid.Count)
        {
            var c = island.Compositor;
            var fill = c.CreateColorBrush();
            var pill = Rounded(cardShapes, RectangleF.Empty, PaletteLayout.ButtonCorner, fill);
            buttons.Add((pill, fill, text.Create(cardLayer)));
        }

        for (int i = 0; i < buttons.Count; i++)
        {
            var (pill, fill, label) = buttons[i];
            bool shownButton = i < laid.Count;
            Place(pill, shownButton ? laid[i].Box : RectangleF.Empty);
            // The focused button is the white one: what Enter presses is what stands out.
            fill.Color = Argb(i == cardFocus ? PaletteLayout.RunFill : PaletteLayout.ButtonFill);
            label.Show(shownButton ? laid[i].Label : new TextSlot("", PaletteLayout.Button, RectangleF.Empty));
        }
    }

    private TextVisual Heading(int i)
    {
        while (headings.Count <= i)
        {
            headings.Add(text.Create(scrolled));
        }

        return headings[i];
    }

    private RowParts Row(int i)
    {
        while (parts.Count <= i)
        {
            var icon = island.Compositor.CreateSpriteVisual();
            scrolled.Children.InsertAtTop(icon);
            parts.Add(new RowParts(icon, text.Create(scrolled), text.Create(scrolled), text.Create(scrolled)));
        }

        return parts[i];
    }

    private void Select(int row)
    {
        select = row;
        moved = true;
        Scrolled();
    }

    // Keys the box gives the list: Up, Down, Tab, Enter; their characters go nowhere, so the box gets no tab or line break.
    private bool OnKey(uint message, nuint wParam, nint lParam)
    {
        int key = (int)(wParam & 0xFFFF);
        if (message == PInvoke.WM_CHAR)
        {
            return key is '\t' or '\r' or '\n';
        }

        return message == PInvoke.WM_KEYDOWN && Press(key, Down(VIRTUAL_KEY.VK_CONTROL), Down(VIRTUAL_KEY.VK_SHIFT));
    }

    /// <summary>
    /// A key down in the box that the list may take: Up, Down, Tab and Enter. On a card, Tab and Shift+Tab move between
    /// its buttons and Enter presses the focused one. True when it took it.
    /// </summary>
    internal bool Press(int key, bool ctrl, bool shift)
    {
        switch (key)
        {
            case NavKeys.Return when card is not null:
                CardAnswered?.Invoke(card.Buttons.Count > 0 ? card.Buttons[Math.Clamp(cardFocus, 0, card.Buttons.Count - 1)].Id : null);
                return true;
            case NavKeys.Return:
                Entered?.Invoke(ctrl);
                return true;
            case NavKeys.Tab when card is not null:
                if (card.Buttons.Count > 0)
                {
                    cardFocus = (((cardFocus + (shift ? -1 : 1)) % card.Buttons.Count) + card.Buttons.Count) % card.Buttons.Count;
                    Draw();
                }

                return true;
            case NavKeys.Tab:
                if (rows.Count > 0)
                {
                    Select(NextGroup(shift ? -1 : 1));
                }

                return true;
            case 0x26 or 0x28:
                if (card is null && rows.Count > 0)
                {
                    Select((((select + (key == 0x28 ? 1 : -1)) % rows.Count) + rows.Count) % rows.Count);
                }

                return true;
            default:
                return false;
        }
    }

    // The first row of the next group, or with -1 of the previous one, wrapping at both ends. From inside a group, -1 goes
    // to that group's first row first.
    private int NextGroup(int direction)
    {
        var firsts = Enumerable.Range(0, rows.Count).Where(i => i == 0 || rows[i].Group != rows[i - 1].Group).ToList();
        int current = firsts.LastOrDefault(i => i <= select);
        int at = firsts.IndexOf(current);
        return direction > 0 ? firsts[(at + 1) % firsts.Count]
            : select != current ? current
            : firsts[(at - 1 + firsts.Count) % firsts.Count];
    }

    // Esc closes a card first, then clears the box, then leaves the island to hide.
    internal bool OnEscape()
    {
        if (card is not null)
        {
            CardAnswered?.Invoke(null);
            return true;
        }

        if (typed.Length > 0)
        {
            island.Queue.TryEnqueue(() => Fill(""));
            return true;
        }

        return false;
    }

    private void Read()
    {
        if (disposed)
        {
            return;
        }

        string now = editor.Services.Text;
        if (now != typed)
        {
            typed = now;
            // Typing goes back to the results: a card in their place is closed, as Esc closes it.
            if (card is not null)
            {
                CardAnswered?.Invoke(null);
            }

            Draw();
            Typed?.Invoke(now);
        }
    }

    private async Task IconsAsync()
    {
        if (loadingIcons)
        {
            iconsAgain = true;
            return;
        }

        loadingIcons = true;
        try
        {
            do
            {
                iconsAgain = false;
                int pixels = (int)Math.Round(SearchLayout.IconSize * island.Scale);
                var wanted = rows.Select(r => r.Icon).OfType<string>().Distinct().Where(s => !brushes.ContainsKey(s)).ToList();
                var missing = wanted.Where(s => !Images.ContainsKey((s, pixels))).ToList();
                if (missing.Count > 0)
                {
                    var read = await Task.Run(() => missing.Select(s => (s, ShellIcon.For(s, pixels))).ToList());
                    if (Images.Count + read.Count > MaxImages)
                    {
                        Images.Clear();
                    }

                    foreach (var (s, image) in read)
                    {
                        Images[(s, pixels)] = image;
                    }
                }

                if (disposed || wanted.Count == 0)
                {
                    return;
                }

                // The first device in a process takes tens of milliseconds, so it is made off this thread, once per search.
                if (surfaces is null)
                {
                    var compositor = island.Compositor;
                    var made = await Task.Run(() => new PixelSurfaces(compositor));
                    if (disposed)
                    {
                        made.Dispose();
                        return;
                    }

                    surfaces = made;
                }

                foreach (string s in wanted)
                {
                    brushes[s] = Images.GetValueOrDefault((s, pixels)) is { } image ? surfaces.Brush(image.Width, image.Height, image.Pixels) : null;
                }

                foreach (var part in parts)
                {
                    if (part.Source is { } s && brushes.TryGetValue(s, out var brush))
                    {
                        part.Icon.Brush = brush;
                    }
                }
            }
            while (iconsAgain && !disposed);
        }
        catch (COMException)
        {
            // No device for the surfaces: rows show without icons, the kind says what each is.
        }
        finally
        {
            loadingIcons = false;
        }
    }

    private void FadeIn()
    {
        if (fadeInPending && machine.State == IslandState.Palette)
        {
            fadeInPending = false;
            root.StartAnimation("Opacity", PanelHost.Fade(island.Compositor, 1, PanelHost.FadeInMs));
            var corner = new Vector2(root.Offset.X, root.Offset.Y);
            island.Interaction.Register(this, p => p - corner, p => p + corner);
        }
    }

    // A press on the box's padding gives the box the keys, as a press on its text does.
    private void OnPressed(Vector2 at)
    {
        var local = at - new Vector2(root.Offset.X, root.Offset.Y);
        if (!disposed && machine.State == IslandState.Palette && layout.Box.Contains(local.X, local.Y) && !editor.Contains(at))
        {
            focus.Take(editor);
        }

        Draw();
    }

    private void OnWheel(Vector2 at, int delta)
    {
        var local = at - new Vector2(root.Offset.X, root.Offset.Y);
        if (disposed || card is not null || !layout.Band.Contains(local.X, local.Y))
        {
            return;
        }

        scroll = Math.Clamp(scroll - (delta / 120f * WheelStep), 0, Math.Max(0, content - layout.Band.Height));
        scrolled.Offset = new Vector3(0, -scroll, 0);
        foreach (var visual in headings.Concat(parts.SelectMany(p => new[] { p.Name, p.Detail, p.Kind })))
        {
            visual.Refresh();
        }

        Retarget();
    }

    // A new scale: the box lands on whole pixels again, and the icons are read at the new size.
    private void Relayout()
    {
        editor.Place(layout.Editor, island.Scale);
        brushes.Clear();
        Draw();
        _ = IconsAsync();
    }

    private void OnState(IslandState from, IslandState to)
    {
        if (to == IslandState.Palette)
        {
            return;
        }

        island.Interaction.Unregister(this);
        machine.Changed -= OnState;
        root.StartAnimation("Opacity", PanelHost.Fade(island.Compositor, 0, PanelHost.FadeOutMs));
        PanelHost.AfterFadeOut(island, Dispose).Start();
    }

    private static bool Down(VIRTUAL_KEY key) => PInvoke.GetKeyState((int)key) < 0;

    private sealed record RowParts(SpriteVisual Icon, TextVisual Name, TextVisual Detail, TextVisual Kind)
    {
        public string? Source { get; set; }

        public void Hide()
        {
            Source = null;
            Icon.IsVisible = false;
            Name.Show("", SearchLayout.Name, RectangleF.Empty);
            Detail.Show("", SearchLayout.Detail, RectangleF.Empty);
            Kind.Show("", SearchLayout.Kind, RectangleF.Empty);
        }
    }
}
