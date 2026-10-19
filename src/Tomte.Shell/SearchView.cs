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
/// The search box on the island, in the Palette state: a query field after the tomte with the magnifier, the text and where
/// files come from; the results under it, the top hit first and raised with its full path, then each group under its
/// heading, and the AI rows after a rule; and the key hints along the bottom. The box keeps every key: Up and Down move
/// the selection through all rows and wrap, Tab and Shift+Tab jump to the next or previous section and wrap, Enter opens,
/// Ctrl+Shift+Enter shows the row's file in its folder, Ctrl+Enter searches with AI, Page Up and Page Down move a page, Esc
/// clears the box, then goes back or closes. Left, Right, Home and End stay the box's. The selected row always shows,
/// raised; the pointer's hover is the interaction host's, and a click opens a row. A file or folder row under the pointer
/// shows a show in folder button at its right end. A card (getting Everything) can take the list's place: Tab moves
/// between its buttons, Enter presses the focused one, which starts on the safe one. Icons come from the shell, read off
/// this thread and kept for the session. Island thread only.
/// </summary>
internal sealed class SearchView : IDisposable, IInteractive
{
    /// <summary>A show in folder button's target id is this and its row's index.</summary>
    internal const string FolderId = "folder:";

    private const string RowId = "row:", CardId = "card:";
    private const int MaxImages = 512;
    private const float WheelStep = 3 * SearchLayout.RowHeight;
    private static Color CardFill => ShelfColors.Of(Theme.Card);

    // Shell icons as pixels, by source and size, for the session: a new search shows them at once.
    // dropped all at once when full; an LRU if a session's searches ever need more.
    private static readonly Dictionary<(string Source, int Pixels), ShellImage?> Images = [];

    private readonly IslandWindow island;
    private readonly IslandStateMachine machine;
    private readonly TypingFocus focus;
    private readonly TextRenderer text;
    private readonly float width, tallest;
    private readonly ContainerVisual root, band, scrolled, cardLayer, foot;
    private readonly ExpressionAnimation footEdge;
    private readonly ShapeVisual shapes, selection, cardShapes;
    private readonly CompositionRoundedRectangleGeometry cardBox, selectionBox;
    private readonly CompositionEllipseGeometry dot;
    private readonly CompositionColorBrush dotInk;
    private readonly SpriteVisual rule;
    private readonly EditorView editor;
    private readonly TextVisual placeholder, status, empty, cardTitle, cardBody;
    private readonly Keycaps rowKeys, footKeys;
    private readonly List<TextVisual> headings = [];
    private readonly List<RowParts> parts = [];

    // The show in folder button, on the row under the pointer when it is a file or folder.
    private readonly ShapeVisual folder;
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
    private string? placedFor;

    // AI search's results, or its progress or error, are showing; lead is its line on why above the rows.
    private bool ai;
    private string? lead;

    // The text the rows on show were found for.
    private string? shownFor;

    // The user moved the selection for this text (a key or a page); only then do later results keep it.
    private bool moved;
    private int select, hovered = -1;
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
        Rounded(shapes, layout.Box, SearchLayout.BoxCorner, c.CreateColorBrush(CardFill));
        var glass = c.CreateContainerShape();
        glass.Offset = new Vector2(layout.Glass.X, layout.Glass.Y);
        PaletteView.DrawGlyph(glass, Glyphs.Search, c.CreateColorBrush(Argb(Theme.Ink2)), SearchLayout.GlassSize);
        shapes.Shapes.Add(glass);
        dot = c.CreateEllipseGeometry();
        var dotShape = c.CreateSpriteShape(dot);
        dotShape.FillBrush = dotInk = c.CreateColorBrush();
        shapes.Shapes.Add(dotShape);
        status = text.Create(root);

        // The list scrolls in a band clipped to its box; the selection is drawn under the rows' text.
        var area = layout.Band;
        band = c.CreateContainerVisual();
        band.Offset = new Vector3(area.X, area.Y, 0);
        band.Size = new Vector2(area.Width, area.Height);
        band.Clip = c.CreateInsetClip();
        scrolled = c.CreateContainerVisual();
        band.Children.InsertAtTop(scrolled);
        root.Children.InsertAtTop(band);
        selection = Shapes(scrolled, new Vector2(area.Width, SearchLayout.HitHeight));
        selectionBox = Rounded(selection, new RectangleF(0, 0, area.Width, SearchLayout.RowHeight), SearchLayout.RowCorner, c.CreateColorBrush(Argb(SearchLayout.SelectedFill)));
        selection.IsVisible = false;
        rule = c.CreateSpriteVisual();
        rule.Brush = c.CreateColorBrush(Argb(SearchLayout.RuleColor));
        rule.IsVisible = false;
        scrolled.Children.InsertAtTop(rule);
        rowKeys = new Keycaps(c, text, scrolled, new Vector2(width, 4096));
        folder = Shapes(scrolled, new Vector2(SearchLayout.FolderSize));
        Rounded(folder, new RectangleF(0, 0, SearchLayout.FolderSize, SearchLayout.FolderSize), SearchLayout.FolderCorner, c.CreateColorBrush(Argb(SearchLayout.FolderFill)));
        var folderGlyph = c.CreateContainerShape();
        folderGlyph.Offset = new Vector2((SearchLayout.FolderSize - SearchLayout.FolderGlyphSize) / 2);
        PaletteView.DrawGlyph(folderGlyph, Glyphs.Folder, c.CreateColorBrush(Argb(SearchLayout.FolderInk)), SearchLayout.FolderGlyphSize);
        folder.Shapes.Add(folderGlyph);
        folder.IsVisible = false;
        empty = text.Create(root);

        cardLayer = c.CreateContainerVisual();
        root.Children.InsertAtTop(cardLayer);
        cardShapes = Shapes(cardLayer, new Vector2(width, tallest));
        cardBox = Rounded(cardShapes, RectangleF.Empty, SearchLayout.BoxCorner, c.CreateColorBrush(CardFill));
        ((CompositionSpriteShape)cardShapes.Shapes[^1]).StrokeBrush = c.CreateColorBrush(Argb(PaletteLayout.AskOutline));
        ((CompositionSpriteShape)cardShapes.Shapes[^1]).StrokeThickness = 1;
        cardTitle = text.Create(cardLayer);
        cardBody = text.Create(cardLayer);
        cardLayer.IsVisible = false;

        placeholder = text.Create(root);

        // While the shape is deeper than the box, the footer rides its live bottom edge, on whole pixels: a box that settles
        // back once cleared takes it up with the edge instead of leaving it halfway up a shape still closing. Otherwise,
        // growing or closing, it stays where the layout puts it and the outline clips it, as the list is clipped.
        foot = c.CreateContainerVisual();
        root.Children.InsertAtTop(foot);
        footEdge = c.CreateExpressionAnimation("Vector3(0, Floor(Max(live.Depth - h, 0) * dips.Scale.Y) / dips.Scale.Y, 0)");
        footEdge.SetReferenceParameter("live", island.Live);
        footEdge.SetReferenceParameter("dips", island.Dips);
        footEdge.SetScalarParameter("h", layout.Height);
        foot.StartAnimation("Offset", footEdge);
        footKeys = new Keycaps(c, text, foot, new Vector2(width, tallest));
        editor = focus.Create(root, multiline: false, SearchLayout.BoxText, weight: 500);
        editor.CaretInk = Theme.Accent;
        editor.KeepsKeys = true;
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

    /// <summary>The box's height for what it shows, in DIPs. It only grows while there is text, so the shape settles once.</summary>
    public float Height => layout.Height;

    /// <summary>The box grew, or settled back once cleared; the island morphs to the new height, gently.</summary>
    public event Action? HeightChanged;

    /// <summary>A row was clicked: it opens.</summary>
    public event Action<SearchItem>? Clicked;

    /// <summary>A row's show in folder button was clicked: its file or folder shows in File Explorer.</summary>
    public event Action<SearchItem>? FolderClicked;

    /// <summary>Enter in the box, with what its modifiers ask for: open the selected row, show it in its folder, or search with AI.</summary>
    public event Action<SearchEnter>? Entered;

    /// <summary>A card's button, by its id: a click, or Enter on the focused one. Esc says null.</summary>
    public event Action<string?>? CardAnswered;

    /// <summary>Esc while AI search shows: back to the results for the typed text.</summary>
    public event Action? Back;

    /// <summary>Opened from a tab: Esc with nothing else to step back goes back to it. Set before the view first shows.</summary>
    public Action? Leave { get; set; }

    /// <summary>The line above AI search's rows; null when AI search is not showing.</summary>
    public string? Lead => ai ? lead ?? hint : null;

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

    /// <summary>Where the list is scrolled to.</summary>
    public float Scroll => scroll;

    /// <summary>The box's text as it was when the search closed, taken as already searched: nothing is searched or ended again.</summary>
    public void Restore(string words)
    {
        if (!disposed)
        {
            typed = words;
            Fill(words);
            Draw();
        }
    }

    /// <summary>The row with this key selected again, once the rows are back, and the list scrolled where it was.</summary>
    public void Reselect(string? key, float at)
    {
        int row = Enumerable.Range(0, rows.Count).FirstOrDefault(i => rows[i].Key == key, -1);
        if (!disposed && row >= 0)
        {
            scroll = at;
            Select(row);
        }
    }

    /// <summary>
    /// New results. The first row is selected, so Enter opens the best match, never the row that offers Everything; once
    /// the user has moved the selection, results that arrive later for the same text (files, the app list) keep that row,
    /// wherever it now is, so Enter never opens something else than what is highlighted. <paramref name="note"/> shows
    /// when there are none. With <paramref name="aiSearch"/> the rows are AI search's picks (or none, with its progress or
    /// error as the note), under <paramref name="why"/>; Esc then goes back.
    /// </summary>
    public void Show(IReadOnlyList<SearchItem> results, string files, string note, bool aiSearch = false, string? why = null)
    {
        if (disposed)
        {
            return;
        }

        (ai, lead) = (aiSearch, why);
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

        lines = SearchLayout.Lines(rows, lead, Hit);
        content = lines.Count > 0 ? lines[^1].Top + lines[^1].Height : 0;
        Draw();
        _ = IconsAsync();
    }

    // A search with text leads with its top hit.
    private bool Hit => typed.Length > 0;

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
        else if (RowOf(id) is int i)
        {
            select = i;
            Draw();
            (id.StartsWith(FolderId, StringComparison.Ordinal) ? FolderClicked : Clicked)?.Invoke(rows[i]);
        }
    }

    /// <summary>The pointer is on a row or its button, or on neither: a file row under it shows its folder button.</summary>
    public void Hovered(string? id)
    {
        int now = id is null ? -1 : RowOf(id) ?? -1;
        if (now != hovered && !disposed)
        {
            hovered = now;
            Draw();
        }
    }

    /// <summary>
    /// Page Up and Page Down reach here from the box: the selection moves a page. In a search opened from a tab, so does
    /// the Esc hotkey while the island does not have the keys; it steps back as Esc in the box does.
    /// </summary>
    public bool Command(NavKey key)
    {
        if (key == NavKey.Escape && !disposed && Leave is not null)
        {
            return OnEscape();
        }

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
        foreach (var visual in headings.Concat(parts.SelectMany(p => p.Texts)).Concat(buttons.Select(b => b.Label))
            .Append(placeholder).Append(status).Append(empty).Append(cardTitle).Append(cardBody))
        {
            visual.Dispose();
        }

        foreach (var part in parts)
        {
            part.Icon.Dispose();
            part.Badge.Dispose();
        }

        rowKeys.Dispose();
        footKeys.Dispose();
        folder.Dispose();

        surfaces?.Dispose();
        island.Dips.Children.Remove(root);
        root.Dispose();
    }

    private static Color Argb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);

    // The row a row's or its button's target id names, or null.
    private int? RowOf(string id)
    {
        string prefix = id.StartsWith(FolderId, StringComparison.Ordinal) ? FolderId : RowId;
        return id.StartsWith(prefix, StringComparison.Ordinal) && int.TryParse(id.AsSpan(prefix.Length), out int i) && i >= 0 && i < rows.Count ? i : null;
    }

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
        string shownSource = card is null ? source : "";
        var (at, line) = layout.Source(shownSource);
        dot.Center = new Vector2(at.X + (at.Width / 2), at.Y + (at.Height / 2));
        dot.Radius = new Vector2(at.Width / 2);
        dotInk.Color = Argb(SearchLayout.SourceDot(shownSource));
        status.Show(line);
        var box = layout.EditorFor(shownSource);
        if (placedFor != shownSource)
        {
            // The text box ends short of where files come from, which is measured; it moves only when that changes.
            placedFor = shownSource;
            editor.Place(box, island.Scale);
        }

        placeholder.Show(typed.Length == 0 ? SearchLayout.PlaceholderText : "", SearchLayout.Placeholder, box);
        footKeys.Begin();
        if (card is null)
        {
            var (left, right) = SearchLayout.Keys(ai, typed.Length > 0, Leave is not null);
            foreach (var (cap, keys, word) in layout.Footer(left, right))
            {
                footKeys.Add(cap, keys, word);
            }
        }

        footKeys.End();
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
        rule.IsVisible = false;
        folder.IsVisible = false;
        rowKeys.Begin();
        foreach (var l in lines)
        {
            if (l.Rule)
            {
                rule.Offset = new Vector3(SearchLayout.RowPadX, l.Top + SearchLayout.RuleInset, 0);
                rule.Size = new Vector2(area.Width - (2 * SearchLayout.RowPadX), 1);
                rule.IsVisible = true;
                continue;
            }

            if (l.Heading is { } label)
            {
                var style = lead is not null && label == lead ? SearchLayout.Lead : SearchLayout.Heading;
                Heading(heading++).Show(label, style, new RectangleF(SearchLayout.RowPadX, l.Top, area.Width - (2 * SearchLayout.RowPadX), l.Height));
                continue;
            }

            var item = rows[l.Row];
            // A file row under the pointer shows its folder button at its right end, over its detail and key hint.
            bool button = SearchLayout.HasFolder(item) && l.Row == hovered;
            var laid = layout.Row(l, item, l.Row == select);
            DrawRow(Row(row++), laid, item, l.Hit, button);
            if (laid.Key is { } key && !button)
            {
                rowKeys.Add(laid.KeyCap, key);
            }

            if (button)
            {
                var at2 = layout.Folder(l).Card;
                folder.Offset = new Vector3(at2.X, at2.Y, 0);
                folder.IsVisible = true;
            }
        }

        rowKeys.End();
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

    // One row: its badge (a file's type on its colour, a shell icon, or a glyph), the name with the typed letters in the
    // accent, and its detail.
    private void DrawRow(RowParts p, SearchRow laid, SearchItem item, bool hit, bool button)
    {
        var b = laid.Badge;
        p.Badge.Offset = new Vector3(b.X, b.Y, 0);
        p.Badge.IsVisible = true;
        p.BadgeBox.Size = new Vector2(b.Width, b.Height);
        p.BadgeBox.CornerRadius = new Vector2(laid.BadgeCorner);
        uint fill = SearchLayout.BadgeFill(item);
        p.BadgeFill.Color = Argb(fill);
        string? kind = SearchLayout.BadgeText(item);
        bool icon = kind is null && item.Icon is not null && item.Kind is not (SearchKind.Folder or SearchKind.Ask or SearchKind.AiSearch);
        p.BadgeText.Show(kind ?? "", hit ? SearchLayout.HitBadgeText : SearchLayout.RowBadgeText, b);
        p.Source = icon ? item.Icon : null;
        float inset = (b.Width - SearchLayout.IconSize) / 2;
        p.Icon.Offset = new Vector3(b.X + inset, b.Y + inset, 0);
        p.Icon.Size = new Vector2(SearchLayout.IconSize);
        p.Icon.Brush = icon ? brushes.GetValueOrDefault(item.Icon!) : null;
        p.Icon.IsVisible = icon;
        var (glyph, ink) = kind is null && !icon ? SearchLayout.BadgeGlyph(item) : ([], 0u);
        // The AI rows' glyph stands alone and a little larger; a badge's glyph sits in its middle.
        float size = fill == 0 || hit ? 18 : 15;
        if (!ReferenceEquals(glyph, p.Glyph) || size != p.GlyphSize)
        {
            (p.Glyph, p.GlyphSize) = (glyph, size);
            PaletteView.DrawGlyph(p.GlyphShape, glyph, p.GlyphInk, size);
        }

        p.GlyphInk.Color = Argb(ink);
        p.GlyphShape.Offset = new Vector2((b.Width - size) / 2);
        var name = laid.Name;
        IReadOnlyList<(int Start, int Length)> matched = ai ? [] : SearchLayout.Matched(item.Name, typed);
        p.Name.ShowIn(new TextRun(name.Text, name.Style, name.Slot.Width) { Bold = matched, Tint = matched, TintColor = Theme.Accent }, name.Slot);
        p.Detail.Show(button && !hit ? laid.Detail with { Text = "" } : laid.Detail);
    }

    // The box grows to what it shows (the list, the hint or a card), up to its tallest; it never shrinks while there is
    // text, so a typed letter that shortens the list does not move the shape back and forth. Once the field is cleared and
    // the rows for the empty field are in, it settles back to their height, so an empty box is not left mostly blank.
    private void Grow()
    {
        float next = SearchLayout.Fit(width, island.Mascot.Enabled, rows, card, tallest, lead, Hit);
        if (next == layout.Height || (next < layout.Height && !SearchLayout.Settles(typed, shownFor)))
        {
            return;
        }

        layout = new SearchLayout(width, island.Mascot.Enabled, next);
        var area = layout.Band;
        band.Offset = new Vector3(area.X, area.Y, 0);
        band.Size = new Vector2(area.Width, area.Height);
        footEdge.SetScalarParameter("h", next);
        foot.StartAnimation("Offset", footEdge);
        HeightChanged?.Invoke();
    }

    // The selection's box, the scroll that keeps it in view, and the targets for the rows that show.
    private void Scrolled()
    {
        var line = lines.FirstOrDefault(l => l.Heading is null && !l.Rule && l.Row == select);
        selection.IsVisible = select >= 0 && line.Height > 0;
        if (selection.IsVisible)
        {
            // The heading or rule above the first row of a group comes into view with it.
            float top = lines.LastOrDefault(l => l.Top < line.Top && (l.Heading is not null || l.Rule) && l.Top + l.Height + SearchLayout.LineGap >= line.Top - 0.01f) is { Height: > 0 } above ? above.Top : line.Top;
            scroll = layout.ScrollTo(scroll, top, line.Top + line.Height, content);
            selection.Offset = new Vector3(0, line.Top, 0);
            selectionBox.Size = new Vector2(layout.Band.Width, line.Height);
            selectionBox.CornerRadius = new Vector2(line.Hit ? SearchLayout.HitCorner : SearchLayout.RowCorner);
        }

        scroll = Math.Clamp(scroll, 0, Math.Max(0, content - layout.Band.Height));
        scrolled.Offset = new Vector3(0, -scroll, 0);
        RefreshRows();
        Retarget();
    }

    private void RefreshRows()
    {
        foreach (var visual in headings.Concat(parts.SelectMany(p => p.Texts)))
        {
            visual.Refresh();
        }

        rowKeys.Refresh();
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
            foreach (var line in lines.Where(l => l.Heading is null && !l.Rule))
            {
                // Only the part of a row inside the band takes the pointer.
                var at = RectangleF.Intersect(new RectangleF(area.X, area.Y + line.Top - scroll, area.Width, line.Height), area);
                if (at.Height > 0)
                {
                    list.Add(new Target(RowId + line.Row, at, line.Hit ? SearchLayout.HitCorner : SearchLayout.RowCorner, TargetKind.Row));
                }

                // A file row's folder button, after its row so it is on top. Always there, so the pointer reaching it
                // shows it on a row that is not selected; Ctrl+Shift+Enter is the keyboard's way, not a Tab stop of its own.
                var button = layout.Folder(line).Card;
                button = RectangleF.Intersect(new RectangleF(area.X + button.X, area.Y + button.Y - scroll, button.Width, button.Height), area);
                if (button.Height > 0 && SearchLayout.HasFolder(rows[line.Row]))
                {
                    list.Add(new Target(FolderId + line.Row, button, SearchLayout.FolderCorner, TargetKind.Button, Tip: SearchLayout.FolderTip, Key: SearchLayout.FolderKey));
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
            // The focused button is the accent one: what Enter presses is what stands out.
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
            var c = island.Compositor;
            var badge = c.CreateShapeVisual();
            badge.Size = new Vector2(SearchLayout.HitBadge);
            scrolled.Children.InsertAtTop(badge);
            var box = c.CreateRoundedRectangleGeometry();
            var fill = c.CreateColorBrush();
            var shape = c.CreateSpriteShape(box);
            shape.FillBrush = fill;
            badge.Shapes.Add(shape);
            var glyph = c.CreateContainerShape();
            badge.Shapes.Add(glyph);
            var icon = c.CreateSpriteVisual();
            scrolled.Children.InsertAtTop(icon);
            parts.Add(new RowParts(badge, box, fill, glyph, c.CreateColorBrush(), icon, text.Create(scrolled), text.Create(scrolled), text.Create(scrolled)));
        }

        return parts[i];
    }

    // The selection goes with its row's key hint, so the rows are drawn again, not only the highlight moved.
    private void Select(int row)
    {
        select = row;
        moved = true;
        Draw();
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
                Entered?.Invoke(SearchLayout.Enter(ctrl, shift));
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
                    Select(SearchLayout.NextSection(rows, select, shift ? -1 : 1));
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

    // Esc closes a card first, then leaves AI search for the results it came from, then clears the box, then goes back to
    // the tab the search was opened from, else leaves the island to hide. The back is here, not only in Command, so it
    // works from the first frame: the view joins the interaction host only as it fades in.
    internal bool OnEscape()
    {
        if (card is not null)
        {
            CardAnswered?.Invoke(null);
            return true;
        }

        if (ai)
        {
            Back?.Invoke();
            return true;
        }

        if (typed.Length > 0)
        {
            island.Queue.TryEnqueue(() => Fill(""));
            return true;
        }

        if (Leave is { } leave)
        {
            // Esc pressed in the box is a key, so focus shows on the tab, as after any key there.
            if (focus.Focused == editor)
            {
                island.Interaction.ShowFocus();
            }

            leave();
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
        RefreshRows();
        Retarget();
    }

    // A new scale: the box lands on whole pixels again, and the icons are read at the new size.
    private void Relayout()
    {
        editor.Place(layout.EditorFor(placedFor ?? ""), island.Scale);
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
        // The footer stays where it is while the view fades: a deeper panel taking the shape over must not carry it down.
        float s = (float)island.Scale;
        foot.StopAnimation("Offset");
        foot.Offset = new Vector3(0, MathF.Floor(MathF.Max((float)island.Current.Depth - layout.Height, 0) * s) / s, 0);
        root.StartAnimation("Opacity", PanelHost.Fade(island.Compositor, 0, PanelHost.FadeOutMs));
        PanelHost.AfterFadeOut(island, Dispose).Start();
    }

    private static bool Down(VIRTUAL_KEY key) => PInvoke.GetKeyState((int)key) < 0;

    private sealed record RowParts(ShapeVisual Badge, CompositionRoundedRectangleGeometry BadgeBox, CompositionColorBrush BadgeFill, CompositionContainerShape GlyphShape, CompositionColorBrush GlyphInk, SpriteVisual Icon, TextVisual BadgeText, TextVisual Name, TextVisual Detail)
    {
        public string? Source { get; set; }

        public GlyphFigure[]? Glyph { get; set; }

        public float GlyphSize { get; set; }

        public IEnumerable<TextVisual> Texts => [BadgeText, Name, Detail];

        public void Hide()
        {
            Source = null;
            Icon.IsVisible = false;
            Badge.IsVisible = false;
            BadgeText.Show("", SearchLayout.RowBadgeText, RectangleF.Empty);
            Name.Show("", SearchLayout.Name, RectangleF.Empty);
            Detail.Show("", SearchLayout.Detail, RectangleF.Empty);
        }
    }
}
