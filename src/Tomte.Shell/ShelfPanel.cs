using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Triggers;
using Windows.ApplicationModel.DataTransfer;
using Windows.Storage;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>
/// The Shelf panel's body: a band and a grid of tiles under it, newest first, two rows of four at the Shelf panel's
/// 560 x 260, the wheel scrolling a row a notch. The band shows the head row, or the action buttons of the tile under
/// the pointer or with keyboard focus. A click selects (Ctrl adds or takes away), a double click or Enter opens, a press
/// and drag drags the tile out (the whole selection when it is one of it), and the buttons act on the same items.
/// Remove asks once. Missing items are dimmed, with their own meta line. The island's interaction host runs the
/// presses, hover, tooltips and focus.
/// </summary>
internal sealed class ShelfPanel(IslandWindow island, TriggerController triggers, ShelfStore shelf, TextRenderer text, Action<string>? log) : IPanel, IInteractive
{
    private readonly HashSet<long> selected = [];
    private DataTransferManager? shareManager;
    private Windows.System.DispatcherQueueTimer? shareWait, bandWait, confirmWait;
    private List<ShelfItem> sharing = [];
    private int shareAsked;
    private ContainerVisual? body;
    private Vector2 size;
    private Parts? parts, band;
    private int first, wheel;
    private IReadOnlyList<Target> targets = [];

    // The item whose actions the band shows, the one it changes to once the wait is over, and when that is due, in ms.
    private long? shown, pending, due;

    // Where the pointer came onto the tile or button it is on, in body DIPs: the point it heads for the band from. It
    // follows the pointer only while no change waits, so a wobble during a sweep across a tile is not an aim at the band.
    private Vector2? last;

    // The target the host says is hovered now, or null over empty grid.
    private string? hovering;

    private readonly RemoveQuestion question = new();

    public event Action? TargetsChanged;

    public PanelId Id => PanelId.Shelf;

    public IReadOnlyList<Target> Targets => targets;

    public void Shown(ContainerVisual body, Vector2 size)
    {
        (this.body, this.size) = (body, size);
        shelf.Changed += Rebuild;
        // Returns at once: the first showing loads the shelf, every showing looks for moved and deleted files, and
        // Changed brings the result.
        _ = shelf.Check();
        Rebuild();
    }

    public void Hidden()
    {
        shelf.Changed -= Rebuild;
        parts?.Dispose();
        band?.Dispose();
        (parts, band, body) = (null, null, null);
        selected.Clear();
        bandWait?.Stop();
        (shown, due, last, hovering) = (null, null, null, null);
        Drop();
        first = wheel = 0;
        targets = [];
    }

    /// <summary>A press on none of the targets: the selection goes. The host runs the presses on targets.</summary>
    public void Press(Vector2 point)
    {
        if (targets.Any(t => t.Bounds.Contains(point.X, point.Y)))
        {
            return;
        }

        if (selected.Count > 0)
        {
            selected.Clear();
            Rebuild();
        }
    }

    public void Wheel(Vector2 point, int delta)
    {
        // A touchpad's small deltas add up to notches.
        wheel += delta;
        int notches = wheel / 120;
        if (notches == 0)
        {
            return;
        }

        wheel -= notches * 120;
        int next = ShelfLayout.Scroll(first, notches, shelf.Items.Count, size);
        if (next != first)
        {
            first = next;
            Rebuild();
        }
    }

    public void Activate(string id) => Activate(id, ActivateBy.Click);

    public void Activate(string id, ActivateBy by)
    {
        if (ActionOf(id) is { } action)
        {
            // The first click of a double click already acted.
            if (by != ActivateBy.DoubleClick && Owner() is { } owner)
            {
                Act(action, owner);
            }

            return;
        }

        if (Item(id) is not { } item)
        {
            return;
        }

        // A double click or a key opens, as in File Explorer; a click selects.
        if (by != ActivateBy.Click)
        {
            Act(ShelfAction.Open, item);
            return;
        }

        bool ctrl = (PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_CONTROL) & 0x8000) != 0;
        if (ctrl && !selected.Remove(item.Id))
        {
            selected.Add(item.Id);
        }
        else if (!ctrl)
        {
            selected.Clear();
            selected.Add(item.Id);
        }

        Rebuild();
    }

    /// <summary>
    /// On a focused tile or the band, the band's actions by key: Delete asks to remove, as the Remove button does, and a
    /// second Delete, not a repeat, removes; Ctrl+C copies the path; Ctrl+Shift+E shows it in its folder. An arrow off
    /// the page scrolls a row first, so the arrow finds the tile it heads for.
    /// </summary>
    public bool KeyDown(string id, NavKey key, bool repeat)
    {
        if (key is NavKey.Up or NavKey.Down or NavKey.Left or NavKey.Right && Item(id) is { } from)
        {
            var items = shelf.Items;
            int index = items.ToList().IndexOf(from);
            int next = key is NavKey.Up or NavKey.Down
                ? ShelfLayout.ScrollFor(first, index, key == NavKey.Down ? 1 : -1, items.Count, size)
                : ShelfLayout.ScrollSideways(first, index, key == NavKey.Right ? 1 : -1, items.Count, size);
            if (next != first)
            {
                first = next;
                Rebuild();
            }

            return false;
        }

        ShelfAction? action = key switch
        {
            NavKey.Remove => ShelfAction.Remove,
            NavKey.Copy => ShelfAction.CopyPath,
            NavKey.ShowInFolder => ShelfAction.Reveal,
            _ => null,
        };
        if (action is null || (Item(id) is null && ActionOf(id) is null))
        {
            return false;
        }

        if (Item(id) is { } item && shown != item.Id)
        {
            bandWait?.Stop();
            due = null;
            Show(item.Id);
        }

        // A held key copies or opens a folder once; Remove's question tells a repeat from a second press itself.
        if (Owner() is { } owner && (!repeat || action == ShelfAction.Remove))
        {
            Act(action.Value, owner, repeat);
        }

        return true;
    }

    /// <summary>Win32 runs the press, move and release loop with the system's drag threshold: true is a drag, false a click.</summary>
    public bool TryDrag(string id, Vector2 at)
    {
        if (Item(id) is not { } item)
        {
            return false;
        }

        PInvoke.GetCursorPos(out var cursor);
        if (!PInvoke.DragDetect(island.Hwnd, cursor))
        {
            return false;
        }

        _ = ShelfDrag.Run(island, triggers, shelf, Acting(item), log);
        return true;
    }

    public void PointerMoved(string id, Vector2 at)
    {
        Step(id, id, at, Environment.TickCount64);
        if (due is null)
        {
            last = at;
        }
    }

    public void Hovered(string? id)
    {
        // Keyboard focus takes the band at once: the waits are for a pointer crossing tiles on its way to the band.
        if (id is not null && Item(id) is { } focused && island.Interaction.IsFocus(this, id))
        {
            (hovering, pending, due) = (id, null, null);
            bandWait?.Stop();
            if (shown != focused.Id)
            {
                Show(focused.Id);
                Band();
            }

            return;
        }

        string? was = hovering;
        hovering = id;
        PInvoke.GetCursorPos(out var cursor);
        var at = PanelSpace.ToBody(island.ScreenToIsland(cursor.X, cursor.Y), size.X);
        Step(was, id, at, Environment.TickCount64);
        if (id is not null)
        {
            last = at;
        }
    }

    // The band's rules for the pointer, on `was` and now on `id` at `at` in body DIPs: the band follows the item they give,
    // and the timer fires when what waits is due.
    private void Step(string? was, string? id, Vector2 at, long now)
    {
        (var owner, pending, var next) = ShelfLayout.Step(shown, pending, due, now, was, id, last, at, size);
        if (next != due)
        {
            due = next;
            if (bandWait is null)
            {
                bandWait = island.Queue.CreateTimer();
                bandWait.IsRepeating = false;
                bandWait.Tick += (_, _) =>
                {
                    if (due is long when)
                    {
                        Step(hovering, hovering, default, when);
                    }
                };
            }

            bandWait.Stop();
            if (next is long d)
            {
                bandWait.Interval = TimeSpan.FromMilliseconds(d - now);
                bandWait.Start();
            }
        }

        if (owner != shown)
        {
            Show(owner);
            Band();
        }
    }

    private static unsafe nint Handle(HWND hwnd) => (nint)hwnd.Value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CopyText(string text) => Tomte.AI.Palette.DesktopEffects.CopyToClipboard(text);

    private static ShelfAction? ActionOf(string id) =>
        id.StartsWith("act:", StringComparison.Ordinal) && Enum.TryParse<ShelfAction>(id.AsSpan(4), out var action) ? action : null;

    private ShelfItem? Item(string id) => shelf.Items.FirstOrDefault(i => ShelfLayout.TileId(i) == id);

    private List<ShelfItem> Selection(IReadOnlyList<ShelfItem> items) => [.. items.Where(i => selected.Contains(i.Id))];

    /// <summary>What the band and a drag act on: the whole selection when the item is one of it, else the item alone.</summary>
    private List<ShelfItem> Acting(ShelfItem item) => selected.Contains(item.Id) ? Selection(shelf.Items) : [item];

    /// <summary>The item the band shows the actions of, while its tile is on the page.</summary>
    private ShelfItem? Owner()
    {
        var items = shelf.Items;
        for (int i = first; i < first + ShelfLayout.Visible(size, first, items.Count); i++)
        {
            if (items[i].Id == shown)
            {
                return items[i];
            }
        }

        return null;
    }

    /// <summary>Another tile's actions, or none: a question Remove asked for the last one is dropped.</summary>
    private void Show(long? id)
    {
        if (id != shown)
        {
            shown = id;
            Drop();
        }
    }

    private void Drop()
    {
        question.Drop();
        confirmWait?.Stop();
    }

    /// <summary>An action for the owner's tile, on the items it acts on. Remove asks first; a repeat of a held key never removes.</summary>
    private void Act(ShelfAction action, ShelfItem owner, bool repeat = false)
    {
        var items = Acting(owner);
        if (action != ShelfAction.Remove)
        {
            Drop();
        }
        else if (question.Click(owner.Id, Environment.TickCount64, PInvoke.GetDoubleClickTime(), repeat) is var step && step != RemoveStep.Remove)
        {
            if (step == RemoveStep.Ask)
            {
                if (confirmWait is null)
                {
                    confirmWait = island.Queue.CreateTimer();
                    confirmWait.Interval = TimeSpan.FromMilliseconds(RemoveQuestion.AskMs);
                    confirmWait.IsRepeating = false;
                    confirmWait.Tick += (_, _) =>
                    {
                        question.Drop();
                        Band();
                    };
                }

                confirmWait.Start();
                Band();
            }

            return;
        }

        var allowed = items.Where(i => ShelfStore.Allows(action, i)).ToList();
        log?.Invoke($"shelf: {action} on {allowed.Count} of {items.Count}");
        if (allowed.Count == 0)
        {
            return;
        }

        try
        {
            switch (action)
            {
                case ShelfAction.Open:
                    foreach (var item in allowed)
                    {
                        Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true })?.Dispose();
                    }

                    break;
                case ShelfAction.Reveal:
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{allowed[0].Path}\"") { UseShellExecute = true })?.Dispose();
                    break;
                case ShelfAction.CopyPath:
                    CopyText(ShelfStore.PathsText(allowed));
                    island.Interaction.Acknowledge(this, ShelfLayout.ActionId(action));
                    break;
                case ShelfAction.Share:
                    Share(allowed);
                    break;
                default:
                    selected.ExceptWith(allowed.Select(i => i.Id));
                    Show(null);
                    _ = shelf.Remove(allowed);
                    Band();
                    break;
            }
        }
        catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            log?.Invoke($"shelf: {action} failed: {e.Message}");
        }
    }

    /// <summary>
    /// The Windows share sheet for the items, anchored to the island. It takes focus, which the user asked for. The
    /// handler goes on once and stays: Windows may ask for the data from inside ShowShareUIForWindow, and a handler
    /// that takes itself off in that call leaves the share with no data and no sheet. Should Windows not ask within
    /// a few seconds, the island says so once it hides, so a share that never shows is not silent.
    /// </summary>
    private void Share(List<ShelfItem> items)
    {
        sharing = items;
        Volatile.Write(ref shareAsked, 0);
        // Started first, so a refusal thrown below (which Act only logs) still ends in the line for the user.
        if (shareWait is null)
        {
            shareWait = island.Queue.CreateTimer();
            shareWait.Interval = TimeSpan.FromSeconds(4);
            shareWait.IsRepeating = false;
            shareWait.Tick += (_, _) =>
            {
                if (Volatile.Read(ref shareAsked) == 0)
                {
                    log?.Invoke("shelf: Windows did not ask for the shared items");
                    triggers.Raise(new IslandEvent("Windows did not open Share. Reveal the file and share it from File Explorer.", DateTimeOffset.Now));
                }
            };
        }

        shareWait.Start();
        nint hwnd = Handle(island.Hwnd);
        if (shareManager is null)
        {
            shareManager = DataTransferManagerInterop.GetForWindow(hwnd);
            shareManager.DataRequested += OnShareRequested;
        }

        DataTransferManagerInterop.ShowShareUIForWindow(hwnd);
    }

    // Possibly on another thread than the island's; it reads only the list Share set and writes only the log.
    private async void OnShareRequested(DataTransferManager sender, DataRequestedEventArgs e)
    {
        Volatile.Write(ref shareAsked, 1);
        var items = sharing;
        log?.Invoke($"shelf: the share sheet asked for {items.Count} items");
        var deferral = e.Request.GetDeferral();
        try
        {
            var storage = new List<IStorageItem>();
            foreach (var item in items)
            {
                storage.Add(item.IsFolder ? await StorageFolder.GetFolderFromPathAsync(item.Path) : await StorageFile.GetFileFromPathAsync(item.Path));
            }

            e.Request.Data.Properties.Title = items.Count == 1 ? items[0].Name : $"{items.Count} items";
            e.Request.Data.SetStorageItems(storage);
        }
        catch (Exception ex)
        {
            // An async event handler: nothing may escape it.
            log?.Invoke($"shelf: share failed: {ex.Message}");
            e.Request.FailWithDisplayText("Tomte could not share this item.");
        }
        finally
        {
            deferral.Complete();
        }
    }

    private void Rebuild()
    {
        if (body is null)
        {
            return;
        }

        parts?.Dispose();
        var p = parts = new Parts(island.Compositor, text);
        var items = shelf.Items;
        selected.RemoveWhere(id => !items.Any(i => i.Id == id));
        first = ShelfLayout.Scroll(first, 0, items.Count, size);
        if (items.Count == 0)
        {
            var empty = new RectangleF(ShelfLayout.PadX, ShelfLayout.GridTop, size.X - 2 * ShelfLayout.PadX, size.Y - ShelfLayout.GridTop - ShelfLayout.PadBottom);
            var box = p.Shapes(body, empty);
            p.Box(box, new RectangleF(0, 0, empty.Width, empty.Height), 8, null, p.Brush(Color.FromArgb(46, 255, 255, 255)), 1).StrokeDashArray.Add(3);
            foreach (var line in ShelfLayout.EmptyText(empty.Size))
            {
                p.Text(box, line);
            }
        }

        for (int slot = 0; slot < ShelfLayout.Visible(size, first, items.Count); slot++)
        {
            Tile(p, items[first + slot], ShelfLayout.Tile(slot, size), selected.Contains(items[first + slot].Id));
        }

        Band();
    }

    /// <summary>
    /// The band over the grid, built apart from the tiles so that the pointer moving between tiles redraws only it: the
    /// head row, or the action buttons of the owner's tile, which is outlined unless the selection's outline is on it.
    /// </summary>
    private void Band()
    {
        band?.Dispose();
        band = null;
        if (body is null)
        {
            return;
        }

        var b = band = new Parts(island.Compositor, text);
        var into = b.Shapes(body, new RectangleF(0, 0, size.X, ShelfLayout.GridTop));
        var items = shelf.Items;
        var owner = Owner();
        var acting = owner is null ? [] : Acting(owner);
        bool confirming = question.Asks(owner?.Id, Environment.TickCount64);
        targets = ShelfLayout.Targets(size, first, items, owner, acting, confirming);
        if (owner is null)
        {
            foreach (var line in ShelfLayout.HeadText(size, items.Count))
            {
                b.Text(into, line);
            }

            TargetsChanged?.Invoke();
            return;
        }

        if (!selected.Contains(owner.Id) && targets.FirstOrDefault(t => t.Id == ShelfLayout.TileId(owner)) is { Id: not null } tile)
        {
            var mark = b.Shapes(body, tile.Bounds);
            b.Box(mark, new RectangleF(0, 0, tile.Bounds.Width, tile.Bounds.Height), ShelfLayout.TileCorner, null, b.Brush(ShelfColors.Mark), 1);
        }

        foreach (var slot in ShelfLayout.Actions(size, confirming))
        {
            bool asking = confirming && slot.Action == ShelfAction.Remove;
            bool allowed = acting.Any(i => ShelfStore.Allows(slot.Action, i));
            b.Box(into, slot.Box, ShelfLayout.ActionCorner, b.Brush(asking ? ShelfColors.WarnFill : ShelfColors.Card));
            var ink = asking ? ShelfColors.Warn : allowed ? ShelfColors.Ink : ShelfColors.Disabled;
            Glyph(b, into, slot.Action, slot.Icon, b.Brush(ink));
            if (slot.Label is { } label)
            {
                b.Text(into, label with { Style = label.Style with { Color = ShelfColors.Argb(ink) } });
            }
        }

        TargetsChanged?.Invoke();
    }

    private void Tile(Parts p, ShelfItem item, RectangleF r, bool isSelected)
    {
        var v = p.Shapes(body!, r);
        v.Opacity = item.Missing ? 0.55f : 1;
        var local = new RectangleF(0, 0, r.Width, r.Height);
        p.Box(v, local, ShelfLayout.TileCorner, p.Brush(isSelected ? ShelfColors.CardSelected : ShelfColors.Card), isSelected ? p.Brush(ShelfColors.Info) : null, 1);
        const float pad = ShelfLayout.TilePad, icon = ShelfLayout.Icon;
        // a coloured square by kind stands in for the file's own icon and thumbnail, which go on image
        // surfaces through the panel host's art device once it merges.
        p.Box(v, new RectangleF(pad, pad, icon, icon), 7, p.Brush(ShelfColors.Rgb(ShelfLayout.KindColor(item))));
        foreach (var line in ShelfLayout.TileText(item, r.Size))
        {
            p.Text(v, line);
        }
    }

    /// <summary>An action's line icon in its square.</summary>
    private static void Glyph(Parts p, ShapeVisual into, ShelfAction action, RectangleF icon, CompositionBrush ink)
    {
        var at = new Vector2(icon.X, icon.Y);
        foreach (var s in ShelfLayout.Glyph(action))
        {
            if (s.Corner < 0)
            {
                p.Line(into, at + s.A, at + s.B, ink, ShelfLayout.GlyphStrokeWidth);
            }
            else
            {
                p.Box(into, new RectangleF(at.X + s.A.X, at.Y + s.A.Y, s.B.X - s.A.X, s.B.Y - s.A.Y), s.Corner, null, ink, ShelfLayout.GlyphStrokeWidth);
            }
        }
    }
}
