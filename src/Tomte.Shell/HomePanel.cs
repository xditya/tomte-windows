using System.Data.Common;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Media;
using Tomte.Core.Shortcuts;
using Tomte.Core.Text;
using Windows.UI.Composition;

namespace Tomte.Shell;

/// <summary>What the Home panel's parts do, wired by the shell to the features they stand for.</summary>
internal sealed class HomeActions
{
    /// <summary>A click on a section: its tab.</summary>
    public required Action<PanelId> Open { get; init; }

    /// <summary>Enter in the Ask bar: the words, and whether the Window and Shelf chips are on. Nothing goes before this.</summary>
    public required Action<string, bool, bool> Ask { get; init; }

    /// <summary>The Region chip: circle part of the screen to ask about, as circle-to-ask does.</summary>
    public required Action Region { get; init; }

    /// <summary>A press on the mic: the voice hold starts, and ends when the button comes up.</summary>
    public required Action Talk { get; init; }

    /// <summary>The Shortcuts section's plus: Settings, where shortcuts are added.</summary>
    public required Action AddShortcut { get; init; }

    /// <summary>The provider and model set up in Settings ("anthropic · claude-haiku-4-5"), or null with none.</summary>
    public required Func<string?> Provider { get; init; }

    /// <summary>What the provider cost today against its cap, read off the island's thread; null when it cannot be read.</summary>
    public required Func<Task<string?>> Spend { get; init; }

    /// <summary>The scratchpad shortcut as set in Settings, for the Scratchpad section's keycap.</summary>
    public required Func<string> NoteKey { get; init; }

    /// <summary>The voice shortcut as set in Settings, for the mic's tooltip.</summary>
    public required Func<string> TalkKey { get; init; }
}

/// <summary>
/// The Home tab, laid out by <see cref="HomeLayout"/>: the first shelf tiles, Now playing (the Media panel's own card,
/// stacked to fit), the start of the note, the first shortcuts, and the Ask bar with the provider's line under it.
/// Everything is read from the stores the other tabs use, when the tab shows, and the parts are rebuilt when those
/// change while it shows. A click on a section opens its tab; the controls inside a section act there. The Ask bar's
/// text box sends nothing until Enter, and then through the AI box as Ctrl+K opens it, with the chips that are on.
/// </summary>
internal sealed class HomePanel(IslandWindow island, TextRenderer text, TypingFocus focus, ShelfStore shelf, MediaTracker tracker,
    ScratchpadNote note, ShortcutStore shortcutStore, HomeActions actions, Func<DateTimeOffset> now, Action<string>? log) : IPanel, IInteractive
{
    private const string Web = "web:";

    private static readonly HashSet<string> MediaIds = [MediaPanelLayout.SeekId, MediaPanelLayout.PreviousId, MediaPanelLayout.PlayPauseId, MediaPanelLayout.NextId];

    private readonly MediaPanel media = new(island, tracker, text, now, log) { LayoutFor = size => new HomeLayout(size).NowPlayingCard };

    // Kept for the session: an icon extracted once is not extracted again.
    private readonly Dictionary<(string Source, int Pixels), ShellImage?> icons = [];
    private HomeLayout? layout;
    private ContainerVisual? body;
    private Parts? shelfParts, noteParts, shortcutParts, askParts, footParts;
    private PixelSurfaces? surfaces;
    private readonly List<SpriteVisual> iconSlots = [];
    private EditorView? editor;
    private TextVisual? placeholder;
    private IReadOnlyList<(Shortcut Row, ShortcutTarget Target)> tiles = [];
    private IReadOnlyList<Target> targets = [];
    private string? spend;
    private bool window, withShelf, showsMedia;
    private int showing;

    public PanelId Id => PanelId.Home;

    public IReadOnlyList<Target> Targets => targets;

    public event Action? TargetsChanged;

    public void Shown(ContainerVisual body, Vector2 size)
    {
        int generation = ++showing;
        this.body = body;
        layout = new HomeLayout(size);
        (window, withShelf, spend) = (false, false, null);
        showsMedia = layout.Rows >= 1;
        if (showsMedia)
        {
            media.TargetsChanged += Retarget;
            media.Shown(body, size);
        }

        BuildAsk();
        BuildFoot();
        if (layout.Rows >= 1)
        {
            shelf.Changed += BuildShelf;
            BuildShelf();
            Observe(shelf.Load(), "read the shelf");
        }

        if (layout.Rows >= 2)
        {
            note.Changed += NoteChanged;
            BuildNote();
            _ = LoadNoteAsync(generation);
            BuildShortcuts();
            _ = LoadShortcutsAsync(generation);
        }

        _ = SpendAsync(generation);
        Retarget();
    }

    public void Hidden()
    {
        showing++;
        if (showsMedia)
        {
            media.TargetsChanged -= Retarget;
            media.Hidden();
        }

        shelf.Changed -= BuildShelf;
        note.Changed -= NoteChanged;
        editor?.Dispose();
        placeholder?.Dispose();
        foreach (var parts in new[] { shelfParts, noteParts, shortcutParts, askParts, footParts })
        {
            parts?.Dispose();
        }

        surfaces?.Dispose();
        iconSlots.Clear();
        (shelfParts, noteParts, shortcutParts, askParts, footParts, surfaces, editor, placeholder, layout, body) = (null, null, null, null, null, null, null, null, null, null);
        targets = [];
        TargetsChanged?.Invoke();
    }

    /// <summary>A click in the Ask bar away from its chips and its text box gives the text box the keys.</summary>
    public void Press(Vector2 point)
    {
        if (editor is not null && layout is not null && layout.Ask.Contains(point.X, point.Y) && !editor.Box.Contains(point.X, point.Y))
        {
            focus.Take(editor);
        }
    }

    public void Wheel(Vector2 point, int delta)
    {
    }

    public void Activate(string id) => Activate(id, ActivateBy.Click);

    public void Activate(string id, ActivateBy by)
    {
        if (MediaIds.Contains(id))
        {
            ((IInteractive)media).Activate(id, by);
            return;
        }

        switch (id)
        {
            case HomeLayout.ShelfId:
                actions.Open(PanelId.Shelf);
                break;
            case HomeLayout.MediaId:
                actions.Open(PanelId.Media);
                break;
            case HomeLayout.NoteId:
                actions.Open(PanelId.Scratchpad);
                break;
            case HomeLayout.ShortcutsId:
                actions.Open(PanelId.Shortcuts);
                break;
            case HomeLayout.AddId:
                actions.AddShortcut();
                break;
            case HomeLayout.AskId:
                // Focus reached the text box with the keys: it takes them. Never on its own, so no foreground is taken here.
                if (editor is not null && focus.HasKeys)
                {
                    focus.Take(editor);
                }

                break;
            case HomeLayout.RegionId:
                actions.Region();
                break;
            case HomeLayout.WindowId:
                window = !window;
                BuildAsk();
                Retarget();
                break;
            case HomeLayout.ShelfChipId:
                withShelf = !withShelf;
                BuildAsk();
                Retarget();
                break;
            case HomeLayout.MicId:
                // Enter cannot be held the way the mic is: say how to talk instead.
                string key = actions.TalkKey();
                island.Interaction.Acknowledge(this, id, Hotkey.TryParse(key, out var chord, out _) ? $"Hold the mic, or hold {chord}" : "Hold the mic to talk", tick: false);
                break;
            default:
                if (by != ActivateBy.DoubleClick && tiles.FirstOrDefault(t => HomeLayout.ShortcutId(t.Row.Id) == id) is { Row: not null } tile)
                {
                    _ = LaunchAsync(tile.Row);
                }

                break;
        }
    }

    public bool TryDrag(string id, Vector2 at)
    {
        if (id == HomeLayout.MicId)
        {
            actions.Talk();
            return true;
        }

        return MediaIds.Contains(id) && media.TryDrag(id, at);
    }

    public void Hovered(string? id) => media.Hovered(id);

    public bool KeyDown(string id, NavKey key, bool repeat) => MediaIds.Contains(id) && media.KeyDown(id, key, repeat);

    public void PointerMoved(string id, Vector2 at)
    {
        if (MediaIds.Contains(id))
        {
            media.PointerMoved(id, at);
        }
    }

    private void Retarget()
    {
        if (layout is null)
        {
            return;
        }

        targets = layout.Targets(showsMedia ? media.Targets : [], [.. tiles.Select(t => (t.Row.Id, t.Row.Name))], window, withShelf);
        TargetsChanged?.Invoke();
    }

    // ---- Shelf ----

    private void BuildShelf()
    {
        if (layout is null || body is null || layout.Rows < 1)
        {
            return;
        }

        shelfParts?.Dispose();
        var p = shelfParts = new Parts(island.Compositor, text);
        var v = p.Shapes(body, new RectangleF(0, 0, layout.Body.X, layout.Body.Y));
        var section = layout.Shelf;
        p.SectionCard(v, section);
        var items = shelf.Items;
        string count = items.Count == 1 ? "1 item" : $"{items.Count} items";
        foreach (var line in HomeLayout.HeadText(section, HomeLayout.ShelfTitle, count, HomeLayout.ShelfHint))
        {
            p.Text(v, line);
        }

        var grid = layout.ShelfGrid;
        var (shown, more) = HomeLayout.ShelfShows(items.Count);
        if (items.Count == 0)
        {
            Dashed(p.Box(v, grid, Section.TileCorner, null, p.Brush(Theme.Dash)));
            p.Text(v, new TextSlot(HomeLayout.ShelfEmpty, HomeLayout.EmptyWords, grid));
            return;
        }

        var pictures = new List<(ShelfItem Item, RectangleF Preview)>();
        for (int i = 0; i < shown; i++)
        {
            var tile = layout.ShelfTile(i);
            Tile(p, v, items[i], tile);
            if (ShelfLayout.PreviewOf(items[i]) == ShelfPreview.Image)
            {
                pictures.Add((items[i], Moved(HomeLayout.Preview(tile), tile)));
            }
        }

        _ = PicturesAsync(p, pictures, showing);

        if (more > 0)
        {
            var tile = layout.ShelfTile(shown);
            Dashed(p.Box(v, tile, Section.TileCorner, null, p.Brush(Theme.Dash)));
            foreach (var line in HomeLayout.MoreText(tile, more))
            {
                p.Text(v, line with { Slot = Moved(line.Slot, tile) });
            }
        }
    }

    private static void Tile(Parts p, ShapeVisual v, ShelfItem item, RectangleF tile)
    {
        p.Box(v, tile, Section.TileCorner, p.Brush(Theme.Raised));
        var preview = Moved(HomeLayout.Preview(tile), tile);
        p.Box(v, preview, HomeLayout.PreviewCorner, p.Brush(Theme.Track));
        var (glyph, word) = HomeLayout.PreviewOf(item);
        if (glyph is not null)
        {
            const float size = 24;
            p.Glyph(v, glyph, ShelfColors.Of(item.IsFolder ? Theme.Folder : Theme.Link), new RectangleF(preview.X + ((preview.Width - size) / 2), preview.Y + ((preview.Height - size) / 2), size, size));
        }
        else
        {
            p.Text(v, new TextSlot(word, HomeLayout.PreviewLabel, preview));
        }

        foreach (var line in HomeLayout.TileText(tile, item.Name, ShelfLayout.Meta(item)))
        {
            p.Text(v, line with { Slot = Moved(line.Slot, tile) });
        }
    }

    // The pictures' thumbnails, read off the island's thread, over their previews, cut to the preview's corners. They go
    // with the build that asked for them.
    private async Task PicturesAsync(Parts built, List<(ShelfItem Item, RectangleF Preview)> pictures, int generation)
    {
        if (pictures.Count == 0 || body is not { } host)
        {
            return;
        }

        int px = (int)Math.Ceiling(pictures.Max(x => x.Preview.Width) * island.Scale);
        var compositor = island.Compositor;
        try
        {
            var (made, brushes) = await Task.Run(() =>
            {
                var s = new PixelSurfaces(compositor);
                var list = pictures.Select(x => ShellIcon.Thumbnail(x.Item.Path, px) is { } i ? s.Brush(i.Width, i.Height, i.Pixels) : null).ToList();
                return (s, list);
            });
            if (generation != showing || built != shelfParts)
            {
                made.Dispose();
                return;
            }

            built.Keep(made);
            for (int i = 0; i < brushes.Count; i++)
            {
                if (brushes[i] is { } brush)
                {
                    brush.Stretch = CompositionStretch.UniformToFill;
                    built.Sprite(host, pictures[i].Preview, HomeLayout.PreviewCorner, brush);
                }
            }
        }
        catch (COMException e)
        {
            log?.Invoke($"home: no surfaces for the shelf's pictures: {e.Message}");
        }
    }

    // ---- Scratchpad ----

    private void NoteChanged() => island.Queue.TryEnqueue(BuildNote);

    private async Task LoadNoteAsync(int generation)
    {
        try
        {
            await note.LoadAsync();
        }
        catch (Exception e)
        {
            log?.Invoke($"home: the note could not be read ({e.GetType().Name})");
            return;
        }

        if (generation == showing)
        {
            BuildNote();
        }
    }

    private void BuildNote()
    {
        if (layout is null || body is null || layout.Rows < 2)
        {
            return;
        }

        noteParts?.Dispose();
        var p = noteParts = new Parts(island.Compositor, text);
        var v = p.Shapes(body, new RectangleF(0, 0, layout.Body.X, layout.Body.Y));
        var section = layout.Scratchpad;
        p.SectionCard(v, section);
        string? key = Keycap.Words(actions.NoteKey());
        float capWidth = key is null ? 0 : Keycap.Width(key);
        foreach (var line in HomeLayout.HeadText(section, HomeLayout.NoteTitle, HomeLayout.NoteHint, rightInset: key is null ? 0 : capWidth + 8))
        {
            p.Text(v, line);
        }

        if (key is not null)
        {
            var head = HomeLayout.Head(section);
            p.Keycap(v, new RectangleF(head.Right - capWidth, head.Y + ((head.Height - Keycap.Height) / 2), capWidth, Keycap.Height), key);
        }

        var (title, lines) = note.IsLoaded ? HomeLayout.NotePreview(note.Text) : ("", []);
        if (title.Length == 0)
        {
            p.Text(v, new TextSlot(note.IsLoaded ? HomeLayout.NoteEmpty : "", Section.Note, layout.NoteHeadingBox));
            return;
        }

        p.Text(v, new TextSlot(title, HomeLayout.NoteHeading, layout.NoteHeadingBox));
        for (int i = 0; i < lines.Count && i < layout.NoteLinesShown; i++)
        {
            var (words, done) = lines[i];
            var (square, slot) = layout.NoteLineAt(i, done is not null);
            if (done == true)
            {
                p.Box(v, square, HomeLayout.BoxCorner, p.Brush(Theme.Accent));
                const float tick = HomeLayout.Tick;
                p.Glyph(v, Glyphs.Check, ShelfColors.Of(Theme.OnAccent), new RectangleF(square.X + ((square.Width - tick) / 2), square.Y + ((square.Height - tick) / 2), tick, tick));
            }
            else if (done == false)
            {
                p.Box(v, square, HomeLayout.BoxCorner, null, p.Brush(Theme.Silence), 1.5f);
            }

            p.Text(v, new TextSlot(words, done == true ? HomeLayout.NoteDone : HomeLayout.NoteLine, slot));
        }
    }

    // ---- Shortcuts ----

    private async Task LoadShortcutsAsync(int generation)
    {
        try
        {
            int room = HomeLayout.ShortcutColumns * layout!.ShortcutRowsShown;
            var loaded = await Task.Run(() => shortcutStore.Load().Take(room).Select(row => (row, ShortcutTarget.Read(row.Target))).ToList());
            if (generation != showing)
            {
                return;
            }

            tiles = loaded;
            BuildShortcuts();
            Retarget();
            await IconsAsync(generation);
        }
        catch (Exception e) when (e is DbException or IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"home: shortcuts not read: {e.Message}");
        }
    }

    private void BuildShortcuts()
    {
        if (layout is null || body is null || layout.Rows < 2)
        {
            return;
        }

        shortcutParts?.Dispose();
        surfaces?.Dispose();
        surfaces = null;
        iconSlots.Clear();
        var p = shortcutParts = new Parts(island.Compositor, text);
        var v = p.Shapes(body, new RectangleF(0, 0, layout.Body.X, layout.Body.Y));
        var section = layout.Shortcuts;
        p.SectionCard(v, section);
        foreach (var line in HomeLayout.HeadText(section, HomeLayout.ShortcutsTitle, null, rightInset: HomeLayout.Add))
        {
            p.Text(v, line);
        }

        p.RoundButton(v, layout.AddButton, Glyphs.Plus, HomeLayout.AddIcon, null, Theme.Ink2);
        if (tiles.Count == 0)
        {
            var area = RectangleF.FromLTRB(layout.ShortcutTile(0).X, layout.ShortcutTile(0).Y, layout.ShortcutTile(HomeLayout.ShortcutColumns - 1).Right, layout.Shortcuts.Bottom - Section.Pad);
            p.Text(v, new TextSlot(HomeLayout.ShortcutsEmpty, HomeLayout.EmptyWords, area));
            return;
        }

        for (int i = 0; i < tiles.Count; i++)
        {
            var tile = layout.ShortcutTile(i);
            p.Box(v, tile, Section.TileCorner, p.Brush(Theme.Raised));
            var (icon, name) = HomeLayout.ShortcutParts(tile, tiles[i].Row.Name);
            p.Box(v, icon, 7, p.Brush(Theme.RaisedHi));
            p.Text(v, name);
            var slot = p.Attach(body, island.Compositor.CreateSpriteVisual());
            slot.Offset = new Vector3(icon.X, icon.Y, 0);
            slot.Size = new Vector2(icon.Width, icon.Height);
            iconSlots.Add(slot);
        }
    }

    // A web page shows the browser's icon; blocked and missing tiles show none; anything else the icon of what it opens.
    private static string? Source(ShortcutTarget target) => target.Kind == ShortcutKind.Url ? Web : target.Icon;

    private async Task IconsAsync(int generation)
    {
        int pixels = (int)Math.Round(HomeLayout.ShortcutIcon * island.Scale, MidpointRounding.AwayFromZero);
        var wanted = tiles.Select(t => Source(t.Target)).OfType<string>().Distinct().Where(s => !icons.ContainsKey((s, pixels))).ToList();
        var extracted = await Task.Run(() => wanted.Select(s => (s, s == Web ? ShellIcon.ForWeb(pixels) : ShellIcon.For(s, pixels))).ToList());
        foreach (var (source, image) in extracted)
        {
            icons[(source, pixels)] = image;
        }

        var images = tiles.Select(t => Source(t.Target) is { } s ? icons.GetValueOrDefault((s, pixels)) : null).ToList();
        if (generation != showing || images.All(i => i is null))
        {
            return;
        }

        var compositor = island.Compositor;
        try
        {
            // The device and the surfaces are agile; making them off the island's thread keeps the first device's cost off it.
            var (made, brushes) = await Task.Run(() =>
            {
                var s = new PixelSurfaces(compositor);
                try
                {
                    return (s, images.Select(i => i is null ? null : s.Brush(i.Width, i.Height, i.Pixels)).ToList());
                }
                catch
                {
                    s.Dispose();
                    throw;
                }
            });
            if (generation != showing || iconSlots.Count != brushes.Count)
            {
                made.Dispose();
                return;
            }

            surfaces?.Dispose();
            surfaces = made;
            for (int i = 0; i < brushes.Count; i++)
            {
                iconSlots[i].Brush = brushes[i];
            }
        }
        catch (COMException e)
        {
            log?.Invoke($"home: no surfaces for the shortcut icons: {e.Message}");
        }
    }

    private async Task LaunchAsync(Shortcut row)
    {
        ShortcutLaunch result;
        try
        {
            result = await Task.Run(() => shortcutStore.Launch(row.Id));
        }
        catch (Exception e) when (e is DbException or IOException or UnauthorizedAccessException)
        {
            result = new(ShortcutOutcome.Failed, e.Message);
        }

        log?.Invoke($"home: {row.Name}: {result.Outcome}");
        // One that has to be confirmed first is confirmed in Settings, as on the Shortcuts tab.
        if (result.Outcome is ShortcutOutcome.ScriptsOff or ShortcutOutcome.NeedsConfirm or ShortcutOutcome.Changed)
        {
            actions.AddShortcut();
        }
    }

    // ---- Ask bar ----

    private void BuildAsk()
    {
        if (layout is null || body is null)
        {
            return;
        }

        askParts?.Dispose();
        var p = askParts = new Parts(island.Compositor, text);
        var v = p.Shapes(body, new RectangleF(0, 0, layout.Body.X, layout.Body.Y));
        p.Box(v, layout.Ask, HomeLayout.AskCorner, p.Brush(Theme.Card), p.Brush(Theme.Line));
        p.Glyph(v, Glyphs.Sparkle, ShelfColors.Of(Theme.Accent), layout.SparkleBox);
        foreach (var (id, label, box) in layout.Chips())
        {
            var icon = id switch
            {
                HomeLayout.RegionId => Glyphs.Region,
                HomeLayout.WindowId => Glyphs.Window,
                _ => Glyphs.Shelf,
            };
            p.Chip(v, box, icon, label, id == HomeLayout.WindowId ? window : id == HomeLayout.ShelfChipId && withShelf);
        }

        p.RoundButton(v, layout.MicButton, Glyphs.Mic, HomeLayout.MicIcon, Theme.RaisedHi, Theme.Ink);
        if (editor is null)
        {
            placeholder = text.Create(body);
            editor = focus.Create(body, multiline: false, HomeLayout.PromptText.Size, owner: this);
            editor.Place(layout.Prompt, island.Scale);
            editor.SendOnEnter(words =>
            {
                if (words.Trim() is { Length: > 0 } asked)
                {
                    actions.Ask(asked, window, withShelf);
                }
            });
            // Esc leaves the box for the tab's label, the ring on it; a second Esc closes the island as before.
            editor.Escape = () =>
            {
                if (!focus.TakeKeys())
                {
                    return false;
                }

                island.Interaction.FocusTab();
                return true;
            };
            editor.Services.Changed += () => island.Queue.TryEnqueue(ShowPlaceholder);
        }

        // The hint and the text box over the bar just drawn.
        foreach (Visual top in new Visual[] { placeholder!.Visual, editor.Visual })
        {
            body.Children.Remove(top);
            body.Children.InsertAtTop(top);
        }

        ShowPlaceholder();
    }

    private void ShowPlaceholder()
    {
        if (editor is not null && placeholder is not null && layout is not null)
        {
            placeholder.Show(editor.Services.Text.Length == 0 ? HomeLayout.Placeholder : "", HomeLayout.PromptHint, layout.Prompt);
        }
    }

    private void BuildFoot()
    {
        if (layout is null || body is null)
        {
            return;
        }

        footParts?.Dispose();
        var p = footParts = new Parts(island.Compositor, text);
        var v = p.Shapes(body, new RectangleF(0, 0, layout.Body.X, layout.Body.Y));
        string? provider = actions.Provider();
        var (dot, lines) = layout.FootText(provider, provider is null ? null : spend);
        if (dot is { } d)
        {
            p.Box(v, d, d.Width / 2, p.Brush(Theme.Ok));
        }

        foreach (var line in lines)
        {
            p.Text(v, line);
        }
    }

    private async Task SpendAsync(int generation)
    {
        if (actions.Provider() is null)
        {
            return;
        }

        try
        {
            string? read = await actions.Spend();
            if (generation == showing)
            {
                spend = read;
                BuildFoot();
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            log?.Invoke($"home: today's spend not read ({e.GetType().Name})");
        }
    }

    private static RectangleF Moved(RectangleF r, RectangleF by) => new(r.X + by.X, r.Y + by.Y, r.Width, r.Height);

    private static void Dashed(CompositionSpriteShape shape)
    {
        shape.StrokeDashArray.Add(4);
        shape.StrokeDashArray.Add(3);
    }

    private async void Observe(Task work, string what)
    {
        try
        {
            await work;
        }
        catch (Exception e)
        {
            log?.Invoke($"home: could not {what} ({e.GetType().Name})");
        }
    }
}
