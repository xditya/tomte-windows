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
/// icon, plus the add tile, which opens Settings. Everything is a composition visual in body DIPs and exists only
/// while the tab shows. Showing returns at once: the tiles are read on the thread pool (the last list is drawn
/// meanwhile), then the icons are extracted there (each target and size once a session, kept as pixels) and put on
/// surfaces, which go with the view when the island hides. A click starts the tile on the thread pool, so a slow
/// launch never holds the island's thread; a script that may not run yet opens Settings, where it is confirmed.
/// </summary>
internal sealed class ShortcutsPanel(IslandWindow island, ShortcutStore store, Func<bool> scriptsOn, Action openSettings, Action<string>? log)
{
    private const string Web = "web:";

    // How far the body starts below the island's top: the host's tab strip.
    private const float BodyTop = 42;

    // kept for the session and never refreshed, so an app whose icon changed on update shows the old one until restart.
    private readonly Dictionary<(string Source, int Pixels), ShellImage?> icons = [];
    private IReadOnlyList<Tile> tiles = [];
    private View? view;
    private int showing;
    private bool drooped;
    private int lastCell = -1;
    private long lastPressMs;

    /// <summary>The current showing's load: tiles, then icons. The debug driver waits on it to measure.</summary>
    public Task Ready { get; private set; } = Task.CompletedTask;

    public void Shown(ContainerVisual body, Vector2 size)
    {
        int generation = ++showing;
        double scale = island.Scale;
        view = new View(island.Compositor, body, size, scale);
        view.Build(tiles, scriptsOn());
        Ready = LoadAsync(generation, scale);
    }

    public void Hidden()
    {
        showing++;
        view?.Dispose();
        view = null;
        drooped = false;
        lastCell = -1;
    }

    public void Press(Vector2 point)
    {
        if (view is null || ShortcutGrid.At(point, view.Size, view.Shown + 1) is not int cell)
        {
            return;
        }

        // A double-click starts a tile once: the second press of it within the double-click time is ignored.
        long now = Environment.TickCount64;
        if (cell == lastCell && now - lastPressMs < Windows.Win32.PInvoke.GetDoubleClickTime())
        {
            return;
        }

        (lastCell, lastPressMs) = (cell, now);

        if (cell == view.Shown)
        {
            log?.Invoke("shortcuts: add tile, opening settings");
            openSettings();
            return;
        }

        view.Flash(cell);
        _ = LaunchAsync(view.Tiles[cell]);
    }

    /// <summary>The grid fits its body, so the wheel has nothing to scroll.</summary>
    public void Wheel(Vector2 point, int delta)
    {
    }

    // A web page always shows the browser's icon (a .url file's own icon file could be on a server); blocked and
    // missing tiles show none; anything else shows the icon of the local path the target names.
    private static string? Source(Tile tile) => tile.Target.Kind == ShortcutKind.Url ? Web : tile.Target.Icon;

    private static ShellImage? Extract(string source, int pixels) => source == Web ? ShellIcon.ForWeb(pixels) : ShellIcon.For(source, pixels);

    private async Task LoadAsync(int generation, double scale)
    {
        try
        {
            var loaded = await Task.Run(() => store.Load().Select(row => new Tile(row, ShortcutTarget.Read(row.Target))).ToList());
            if (generation != showing)
            {
                return;
            }

            bool on = scriptsOn();
            if (!loaded.SequenceEqual(tiles))
            {
                tiles = loaded;
                view!.Build(tiles, on);
            }

            int pixels = ShortcutGrid.IconPixels(scale);
            var shown = view!.Tiles;
            var wanted = shown.Select(Source).OfType<string>().Select(source => (Source: source, Pixels: pixels)).Distinct().Where(k => !icons.ContainsKey(k)).ToList();
            var extracted = await Task.Run(() => wanted.Select(k => (Key: k, Image: Extract(k.Source, k.Pixels))).ToList());
            foreach (var (key, image) in extracted)
            {
                icons[key] = image;
            }

            if (generation != showing)
            {
                return;
            }

            var images = shown.Select(t => Source(t) is { } source ? icons.GetValueOrDefault((source, pixels)) : null).ToList();
            if (images.All(image => image is null))
            {
                // No icon to show: no device either.
                return;
            }

            var compositor = island.Compositor;
            // The device and the surfaces are agile; making them here keeps the first device's cost off the island's thread.
            var (surfaces, brushes) = await Task.Run(() =>
            {
                var made = new IconSurfaces(compositor);
                return (made, images.Select(image => image is null ? null : made.Brush(image)).ToList());
            });
            if (generation != showing || view is null)
            {
                surfaces.Dispose();
                return;
            }

            view.SetIcons(surfaces, brushes);
            log?.Invoke($"shortcuts: {tiles.Count} tiles, {shown.Count} shown, {brushes.Count(b => b is not null)} icons at {pixels} px ({wanted.Count} extracted now)");
        }
        catch (Exception e) when (e is DbException or IOException or UnauthorizedAccessException or COMException)
        {
            // The database or the device is out of reach: the grid keeps what it drew, neutral tiles included.
            log?.Invoke($"shortcuts: load failed: {e.Message}");
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

    private sealed class View(Compositor compositor, ContainerVisual body, Vector2 size, double scale) : IDisposable
    {
        private static readonly Color Card = Color.FromArgb(15, 255, 255, 255);
        private static readonly Color Pressed = Color.FromArgb(28, 255, 255, 255);
        private static readonly Color Placeholder = Color.FromArgb(28, 255, 255, 255);
        private static readonly Color Faint = Color.FromArgb(64, 255, 255, 255);
        private static readonly Color Glyph = Color.FromArgb(97, 255, 255, 255);
        private static readonly Color Warn = Color.FromArgb(255, 0xF0, 0xB3, 0x5A);

        // A missing target's tile is drawn at 55% opacity.
        private const float MissingOpacity = 0.55f;

        private readonly List<CompositionColorBrush> cards = [];
        private readonly List<SpriteVisual> slots = [];

        // Every composition object this view made, so hiding releases them now rather than when the collector runs.
        private readonly List<IDisposable> made = [];
        private ContainerVisual? grid;
        private IconSurfaces? surfaces;
        private ColorKeyFrameAnimation? flash;

        public Vector2 Size => size;

        public IReadOnlyList<Tile> Tiles { get; private set; } = [];

        public int Shown => Tiles.Count;

        public void Build(IReadOnlyList<Tile> all, bool scriptsOn)
        {
            Clear();
            Tiles = [.. all.Take(ShortcutGrid.Shown(all.Count, size))];
            grid = Keep(compositor.CreateContainerVisual());
            body.Children.InsertAtTop(grid);
            var (_, head) = Shapes(new Vector2(size.X, ShortcutGrid.GridTop), Vector2.Zero);
            Rounded(head, new RectangleF(ShortcutGrid.Inset, ShortcutGrid.Top + 3.5f, 72, 10), 5, Brush(Placeholder));

            for (int i = 0; i < Tiles.Count; i++)
            {
                var tile = Tiles[i];
                var cell = ShortcutGrid.Cell(i, size);
                var (holder, shapes) = Shapes(new Vector2(cell.Width, cell.Height), new Vector2(cell.X, cell.Y));
                var card = Brush(Card);
                cards.Add(card);
                Rounded(shapes, new RectangleF(0, 0, cell.Width, cell.Height), ShortcutGrid.Corner, card);
                // A neutral square under the icon: what shows while the icon loads, for a missing target, or with no icon.
                var icon = ShortcutGrid.Icon(cell, scale, BodyTop);
                Rounded(shapes, new RectangleF(icon.X - cell.X, icon.Y - cell.Y, icon.Width, icon.Height), 8, Brush(Placeholder));
                Label(shapes, cell, tile.Row.Name.Length);
                if (ShortcutStore.Gate(tile.Row, tile.Target, scriptsOn) is not null)
                {
                    var dot = Keep(compositor.CreateEllipseGeometry());
                    dot.Center = new Vector2(cell.Width - 12, 12);
                    dot.Radius = new Vector2(3);
                    Fill(shapes, dot, Brush(Warn));
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

            AddTile(ShortcutGrid.Cell(Tiles.Count, size));
            DrawText();
        }

        /// <summary>One brush per shown tile, null for a tile with no icon; the view owns the surfaces from here.</summary>
        public void SetIcons(IconSurfaces made, IReadOnlyList<CompositionSurfaceBrush?> brushes)
        {
            surfaces?.Dispose();
            surfaces = made;
            for (int i = 0; i < slots.Count && i < brushes.Count; i++)
            {
                slots[i].Brush = brushes[i];
            }
        }

        /// <summary>The press shows on the card and fades back on the compositor; nothing here ticks for it.</summary>
        public void Flash(int cell)
        {
            if (flash is null)
            {
                flash = compositor.CreateColorKeyFrameAnimation();
                flash.InsertKeyFrame(0, Pressed);
                flash.InsertKeyFrame(1, Card);
                flash.Duration = TimeSpan.FromMilliseconds(250);
            }

            cards[cell].StartAnimation("Color", flash);
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

        /// <summary>
        /// TEXT SEAM. Nothing draws text on the island yet; which text surface to use is an open owner decision. The
        /// renderer draws these runs, each clipped to its slot:
        ///   Head: "Shortcuts", 13 DIP semibold, white, at (Inset, Top), then "apps, files, folders, web pages, scripts",
        ///         12 DIP, 60% white, on the same line.
        ///   Each tile: tile.Row.Name, 12 DIP, #e9e9ee, one line centred in ShortcutGrid.Label(cell), ellipsis.
        ///   A tile with the warn dot: why it would not open, 10 DIP in the warn colour, in place of the dot:
        ///         "Scripts off", "Confirm in Settings", "Changed, confirm again", "Moved or deleted", "Can't open".
        ///         A line under the name instead would need about 14 DIP more than the 80 DIP tile has.
        ///   The add tile: "Add", centred in its label slot, 38% white.
        /// Until then a neutral bar stands where each name goes and a warn-coloured dot marks a tile that would not open.
        /// </summary>
        private static void DrawText()
        {
        }

        private void AddTile(RectangleF cell)
        {
            var (_, shapes) = Shapes(new Vector2(cell.Width, cell.Height), new Vector2(cell.X, cell.Y));
            var outline = Rounded(shapes, new RectangleF(0.5f, 0.5f, cell.Width - 1, cell.Height - 1), ShortcutGrid.Corner, null);
            Dashed(outline, Faint);
            var icon = ShortcutGrid.Icon(cell, scale, BodyTop);
            float x = icon.X - cell.X, y = icon.Y - cell.Y;
            Dashed(Rounded(shapes, new RectangleF(x + 0.5f, y + 0.5f, icon.Width - 1, icon.Height - 1), 8, null), Faint);
            var plus = Brush(Glyph);
            float mid = icon.Width / 2;
            Rounded(shapes, new RectangleF(x + mid - 7, y + mid - 1, 14, 2), 1, plus);
            Rounded(shapes, new RectangleF(x + mid - 1, y + mid - 7, 2, 14), 1, plus);
            Label(shapes, cell, 3);
        }

        private void Label(CompositionShapeCollection shapes, RectangleF cell, int length)
        {
            // About the width the name will take at 12 DIP, so tiles do not all look alike before text renders.
            var slot = ShortcutGrid.Label(cell);
            float width = Math.Min(slot.Width, 12 + 6 * length);
            Rounded(shapes, new RectangleF(slot.X - cell.X + (slot.Width - width) / 2, slot.Y - cell.Y + 4, width, 8), 4, Brush(Placeholder));
        }

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
