using System.Data.Common;
using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Tomte.Core;
using Tomte.Core.Shortcuts;
using Windows.UI.Composition;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>
/// The Shortcuts tab: a grid of tiles laid out by <see cref="ShortcutGrid"/>, each a card with the target's shell
/// icon, then the add tile, which opens Settings. The wheel over the grid scrolls it by rows. Everything is a
/// composition visual in body DIPs and exists only while the tab shows. Showing returns at once: the tiles are read
/// on the thread pool (the last list is drawn meanwhile), then the icons of the tiles in view are extracted there
/// (each source and size once a session, kept as pixels) and put on surfaces, which go when the view is rebuilt or
/// the island hides. A click starts the tile on the thread pool, so a slow launch never holds the island's thread;
/// a script that may not run yet opens Settings, where it is confirmed.
/// </summary>
internal sealed class ShortcutsPanel(IslandWindow island, ShortcutStore store, Func<bool> scriptsOn, Action openSettings, TextRenderer text, Action<string>? log) : IPanel
{
    private const string Web = "web:";
    private const int WheelNotch = 120;

    // kept for the session and never refreshed, so an app whose icon changed on update shows the old one until restart.
    private readonly Dictionary<(string Source, int Pixels), ShellImage?> icons = [];
    private IReadOnlyList<Tile> tiles = [];
    private View? view;
    private int showing, firstRow, wheel, lastPressed = -1;
    private long lastPressMs;
    private bool drooped;

    public PanelId Id => PanelId.Shortcuts;

    public void Shown(ContainerVisual body, Vector2 size)
    {
        int generation = ++showing;
        view = new View(island.Compositor, text, body, size, island.Scale);
        firstRow = ShortcutGrid.FirstRow(firstRow, tiles.Count, size);
        view.Build(tiles, firstRow, scriptsOn());
        _ = LoadAsync(generation);
    }

    public void Hidden()
    {
        showing++;
        view?.Dispose();
        view = null;
        drooped = false;
        (wheel, lastPressed) = (0, -1);
    }

    public void Press(Vector2 point)
    {
        if (view is null || ShortcutGrid.At(point, view.Size, view.Count) is not int slot)
        {
            return;
        }

        int cell = view.First + slot;
        // A double-click starts a tile once: the second press of it within the double-click time is ignored.
        long now = Environment.TickCount64;
        if (cell == lastPressed && now - lastPressMs < Windows.Win32.PInvoke.GetDoubleClickTime())
        {
            return;
        }

        (lastPressed, lastPressMs) = (cell, now);
        if (cell == tiles.Count)
        {
            log?.Invoke("shortcuts: add tile, opening settings");
            openSettings();
            return;
        }

        view.Flash(slot);
        _ = LaunchAsync(tiles[cell]);
    }

    /// <summary>A notch away from the user scrolls up a row, toward the user down; a touchpad's small deltas add up.</summary>
    public void Wheel(Vector2 point, int delta)
    {
        if (view is null)
        {
            return;
        }

        wheel += delta;
        int rows = wheel / WheelNotch;
        wheel -= rows * WheelNotch;
        int next = ShortcutGrid.FirstRow(firstRow - rows, tiles.Count, view.Size);
        if (next != firstRow)
        {
            firstRow = next;
            view.Build(tiles, firstRow, scriptsOn());
            _ = IconsAsync(showing);
        }
    }

    // A web page always shows the browser's icon (a .url file's own icon file could be on a server); blocked and
    // missing tiles show none; anything else shows the icon of the local path or the Start app the target names.
    private static string? Source(Tile tile) => tile.Target.Kind == ShortcutKind.Url ? Web : tile.Target.Icon;

    private static ShellImage? Extract(string source, int pixels) => source == Web ? ShellIcon.ForWeb(pixels) : ShellIcon.For(source, pixels);

    private async Task LoadAsync(int generation)
    {
        try
        {
            var loaded = await Task.Run(() => store.Load().Select(row => new Tile(row, ShortcutTarget.Read(row.Target))).ToList());
            if (generation != showing)
            {
                return;
            }

            if (!loaded.SequenceEqual(tiles))
            {
                tiles = loaded;
                firstRow = ShortcutGrid.FirstRow(firstRow, tiles.Count, view!.Size);
                view.Build(tiles, firstRow, scriptsOn());
            }

            await IconsAsync(generation);
        }
        catch (Exception e) when (e is DbException or IOException or UnauthorizedAccessException)
        {
            // The database is out of reach: the grid keeps what it drew.
            log?.Invoke($"shortcuts: load failed: {e.Message}");
        }
    }

    /// <summary>Icons for the tiles in view: extracted where the cache has none, then put on this build's surfaces.</summary>
    private async Task IconsAsync(int generation)
    {
        if (view is null)
        {
            return;
        }

        var built = view;
        int build = built.Version;
        int pixels = ShortcutGrid.IconPixels(built.Scale);
        var inView = built.Tiles;
        var wanted = inView.Select(Source).OfType<string>().Select(source => (Source: source, Pixels: pixels)).Distinct().Where(k => !icons.ContainsKey(k)).ToList();
        var extracted = await Task.Run(() => wanted.Select(k => (Key: k, Image: Extract(k.Source, k.Pixels))).ToList());
        foreach (var (key, image) in extracted)
        {
            icons[key] = image;
        }

        var images = inView.Select(t => Source(t) is { } source ? icons.GetValueOrDefault((source, pixels)) : null).ToList();
        if (generation != showing || built != view || build != built.Version || images.All(image => image is null))
        {
            // Hidden, scrolled or rebuilt meanwhile, or no icon to show: no device either.
            return;
        }

        var compositor = island.Compositor;
        try
        {
            // The device and the surfaces are agile; making them here keeps the first device's cost off the island's thread.
            var (surfaces, brushes) = await Task.Run(() =>
            {
                var made = new PixelSurfaces(compositor);
                try
                {
                    return (made, images.Select(image => image is null ? null : made.Brush(image.Width, image.Height, image.Pixels)).ToList());
                }
                catch
                {
                    made.Dispose();
                    throw;
                }
            });
            if (generation != showing || built != view || build != built.Version)
            {
                surfaces.Dispose();
                return;
            }

            built.SetIcons(surfaces, brushes);
            log?.Invoke($"shortcuts: {tiles.Count} tiles, rows from {firstRow}, {brushes.Count(b => b is not null)} icons at {pixels} px ({wanted.Count} extracted now)");
        }
        catch (COMException e)
        {
            // No device (a driver restarting, a session without one): the neutral squares stay.
            log?.Invoke($"shortcuts: no surfaces for the icons: {e.Message}");
        }
    }

    private async Task LaunchAsync(Tile tile)
    {
        ShortcutLaunch result;
        try
        {
            result = await Task.Run(() => store.Launch(tile.Row.Id));
        }
        catch (Exception e) when (e is DbException or IOException or UnauthorizedAccessException)
        {
            result = new(ShortcutOutcome.Failed, e.Message);
        }

        log?.Invoke($"shortcuts: {tile.Row.Name}: {result.Outcome}: {result.Message}");
        if (result.Outcome == ShortcutOutcome.Opened)
        {
            if (drooped)
            {
                island.Mascot.Set(MascotState.Compact);
                drooped = false;
            }

            return;
        }

        // Anything that did not open is the mascot's blocked action: the cap droops until a tile opens.
        island.Mascot.Set(MascotState.Error);
        drooped = true;
        if (result.Outcome is ShortcutOutcome.ScriptsOff or ShortcutOutcome.NeedsConfirm or ShortcutOutcome.Changed)
        {
            openSettings();
        }
    }

    private sealed record Tile(Shortcut Row, ShortcutTarget Target);

    private sealed class View(Compositor compositor, TextRenderer text, ContainerVisual body, Vector2 size, double scale) : IDisposable
    {
        private static readonly Color Card = Color.FromArgb(15, 255, 255, 255);
        private static readonly Color Pressed = Color.FromArgb(28, 255, 255, 255);
        private static readonly Color Placeholder = Color.FromArgb(28, 255, 255, 255);
        private static readonly Color Faint = Color.FromArgb(64, 255, 255, 255);
        private static readonly Color Glyph = Color.FromArgb(97, 255, 255, 255);

        // A missing target's tile is drawn at 55% opacity.
        private const float MissingOpacity = 0.55f;

        private readonly List<CompositionColorBrush> cards = [];
        private readonly List<SpriteVisual> slots = [];

        // Every composition object this view made, so a rebuild or hiding releases them now rather than when the collector runs.
        private readonly List<IDisposable> made = [];
        private ContainerVisual? grid;
        private PixelSurfaces? surfaces;
        private ColorKeyFrameAnimation? flash;

        public Vector2 Size => size;

        public double Scale => scale;

        /// <summary>Counts the builds, so icons made for an earlier one are not put on this one.</summary>
        public int Version { get; private set; }

        /// <summary>The number of the first cell in view, and how many are in view, the add tile counted.</summary>
        public int First { get; private set; }

        public int Count { get; private set; }

        /// <summary>The tiles in view, in cell order.</summary>
        public IReadOnlyList<Tile> Tiles { get; private set; } = [];

        public void Build(IReadOnlyList<Tile> all, int firstRow, bool scriptsOn)
        {
            Clear();
            Version++;
            (First, Count) = ShortcutGrid.Visible(firstRow, all.Count, size);
            Tiles = [.. all.Skip(First).Take(Math.Min(Count, all.Count - First))];
            grid = Keep(compositor.CreateContainerVisual());
            body.Children.InsertAtTop(grid);
            foreach (var line in ShortcutGrid.HeadText(size))
            {
                Text(grid).Show(line);
            }

            for (int i = 0; i < Tiles.Count; i++)
            {
                var tile = Tiles[i];
                var cell = ShortcutGrid.Cell(i, size);
                var (holder, shapes) = Shapes(new Vector2(cell.Width, cell.Height), new Vector2(cell.X, cell.Y));
                var card = Brush(Card);
                cards.Add(card);
                Rounded(shapes, new RectangleF(0, 0, cell.Width, cell.Height), ShortcutGrid.Corner, card);
                // A neutral square under the icon: what shows while the icon loads, for a missing target, or with no icon.
                var icon = ShortcutGrid.Icon(cell, scale, PanelSpace.TabStripHeight);
                Rounded(shapes, new RectangleF(icon.X - cell.X, icon.Y - cell.Y, icon.Width, icon.Height), 8, Brush(Placeholder));
                foreach (var line in ShortcutGrid.TileText(cell, tile.Row.Name, ShortcutStore.Gate(tile.Row, tile.Target, scriptsOn)))
                {
                    Text(holder).Show(line);
                }

                if (tile.Target.Missing)
                {
                    holder.Opacity = MissingOpacity;
                }

                var slot = Keep(compositor.CreateSpriteVisual());
                slot.Offset = new Vector3(icon.X, icon.Y, 0);
                slot.Size = new Vector2(icon.Width, icon.Height);
                grid.Children.InsertAtTop(slot);
                slots.Add(slot);
            }

            if (Tiles.Count < Count)
            {
                AddTile(ShortcutGrid.Cell(Tiles.Count, size));
            }
        }

        /// <summary>One brush per tile in view, null for a tile with no icon; the view owns the surfaces from here.</summary>
        public void SetIcons(PixelSurfaces made, IReadOnlyList<CompositionSurfaceBrush?> brushes)
        {
            surfaces?.Dispose();
            surfaces = made;
            for (int i = 0; i < slots.Count && i < brushes.Count; i++)
            {
                slots[i].Brush = brushes[i];
            }
        }

        /// <summary>The press shows on the card and fades back on the compositor; nothing here ticks for it.</summary>
        public void Flash(int slot)
        {
            if (flash is null)
            {
                flash = compositor.CreateColorKeyFrameAnimation();
                flash.InsertKeyFrame(0, Pressed);
                flash.InsertKeyFrame(1, Card);
                flash.Duration = TimeSpan.FromMilliseconds(250);
            }

            cards[slot].StartAnimation("Color", flash);
        }

        public void Dispose()
        {
            Clear();
            flash?.Dispose();
            flash = null;
        }

        private void Clear()
        {
            foreach (var card in cards)
            {
                card.StopAnimation("Color");
            }

            cards.Clear();
            slots.Clear();
            if (grid is not null)
            {
                body.Children.Remove(grid);
                grid = null;
            }

            foreach (var item in made)
            {
                item.Dispose();
            }

            made.Clear();
            surfaces?.Dispose();
            surfaces = null;
        }

        private void AddTile(RectangleF cell)
        {
            var (holder, shapes) = Shapes(new Vector2(cell.Width, cell.Height), new Vector2(cell.X, cell.Y));
            var outline = Rounded(shapes, new RectangleF(0.5f, 0.5f, cell.Width - 1, cell.Height - 1), ShortcutGrid.Corner, null);
            Dashed(outline, Faint);
            var icon = ShortcutGrid.Icon(cell, scale, PanelSpace.TabStripHeight);
            float x = icon.X - cell.X, y = icon.Y - cell.Y;
            Dashed(Rounded(shapes, new RectangleF(x + 0.5f, y + 0.5f, icon.Width - 1, icon.Height - 1), 8, null), Faint);
            var plus = Brush(Glyph);
            float mid = icon.Width / 2;
            Rounded(shapes, new RectangleF(x + mid - 7, y + mid - 1, 14, 2), 1, plus);
            Rounded(shapes, new RectangleF(x + mid - 1, y + mid - 7, 2, 14), 1, plus);
            Text(holder).Show(ShortcutGrid.AddText(cell));
        }

        /// <summary>A text visual that goes with this build, like every other part of it.</summary>
        private TextVisual Text(ContainerVisual host) => Keep(text.Create(host));

        private (ContainerVisual Holder, CompositionShapeCollection Shapes) Shapes(Vector2 extent, Vector2 offset)
        {
            var holder = Keep(compositor.CreateContainerVisual());
            holder.Offset = new Vector3(offset, 0);
            holder.Size = extent;
            var shapes = Keep(compositor.CreateShapeVisual());
            shapes.Size = extent;
            holder.Children.InsertAtTop(shapes);
            grid!.Children.InsertAtTop(holder);
            return (holder, shapes.Shapes);
        }

        private CompositionSpriteShape Rounded(CompositionShapeCollection into, RectangleF rect, float radius, CompositionColorBrush? brush)
        {
            var geometry = Keep(compositor.CreateRoundedRectangleGeometry());
            geometry.Offset = new Vector2(rect.X, rect.Y);
            geometry.Size = new Vector2(rect.Width, rect.Height);
            geometry.CornerRadius = new Vector2(radius);
            return Fill(into, geometry, brush);
        }

        private CompositionSpriteShape Fill(CompositionShapeCollection into, CompositionGeometry geometry, CompositionColorBrush? brush)
        {
            var shape = Keep(compositor.CreateSpriteShape(geometry));
            shape.FillBrush = brush;
            into.Add(shape);
            return shape;
        }

        private void Dashed(CompositionSpriteShape shape, Color color)
        {
            shape.StrokeBrush = Brush(color);
            shape.StrokeThickness = 1;
            shape.StrokeDashArray.Add(4);
            shape.StrokeDashArray.Add(3);
        }

        private CompositionColorBrush Brush(Color color) => Keep(compositor.CreateColorBrush(color));

        private T Keep<T>(T item)
            where T : IDisposable
        {
            made.Add(item);
            return item;
        }
    }
}
