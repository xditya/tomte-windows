using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using Tomte.Core;
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
/// The Shelf panel's body: a head row and a grid of tiles, newest first, two rows of four at the Shelf panel's 560 x 260, the
/// wheel scrolling a row a notch. A press selects (Ctrl adds or takes away), a press and drag drags the selection out,
/// and a selected tile shows its five action buttons. Missing items are dimmed, with their own meta line.
/// </summary>
internal sealed class ShelfPanel(IslandWindow island, TriggerController triggers, ShelfStore shelf, TextRenderer text, Action<string>? log) : IPanel
{
    private readonly HashSet<long> selected = [];
    private DataTransferManager? shareManager;
    private Windows.System.DispatcherQueueTimer? shareWait;
    private List<ShelfItem> sharing = [];
    private int shareAsked;
    private ContainerVisual? body;
    private Vector2 size;
    private Parts? parts;
    private int first, wheel;

    public PanelId Id => PanelId.Shelf;

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
        parts = null;
        body = null;
        selected.Clear();
        first = wheel = 0;
    }

    public void Press(Vector2 point)
    {
        var items = shelf.Items;
        var (index, action) = ShelfLayout.At(point, size, first, items.Count, i => selected.Contains(items[i].Id));
        if (index < 0)
        {
            if (selected.Count > 0)
            {
                selected.Clear();
                Rebuild();
            }

            return;
        }

        if (action is ShelfAction act)
        {
            Act(act, Selection(items));
            return;
        }

        long id = items[index].Id;
        bool ctrl = (PInvoke.GetKeyState((int)VIRTUAL_KEY.VK_CONTROL) & 0x8000) != 0;
        bool was = selected.Contains(id);
        if (ctrl && was)
        {
            selected.Remove(id);
        }
        else if (ctrl || !was)
        {
            if (!ctrl)
            {
                selected.Clear();
            }

            selected.Add(id);
        }

        Rebuild();
        // Win32 runs the press, move and release loop with the system's drag threshold: true is a drag, false a click.
        PInvoke.GetCursorPos(out var at);
        if (selected.Contains(id) && PInvoke.DragDetect(island.Hwnd, at))
        {
            ShelfDrag.Run(island, triggers, shelf, Selection(shelf.Items), log);
        }
        else if (!ctrl && was && selected.Count > 1)
        {
            // A click on one of several selected tiles keeps only that one, as Explorer does on release.
            selected.Clear();
            selected.Add(id);
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

    private static unsafe nint Handle(HWND hwnd) => (nint)hwnd.Value;

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CopyText(string text) => Tomte.AI.Palette.DesktopEffects.CopyToClipboard(text);

    private List<ShelfItem> Selection(IReadOnlyList<ShelfItem> items) => [.. items.Where(i => selected.Contains(i.Id))];

    private void Act(ShelfAction action, List<ShelfItem> items)
    {
        var targets = items.Where(i => ShelfStore.Allows(action, i)).ToList();
        log?.Invoke($"shelf: {action} on {targets.Count} of {items.Count}");
        if (targets.Count == 0)
        {
            return;
        }

        try
        {
            switch (action)
            {
                case ShelfAction.Open:
                    foreach (var item in targets)
                    {
                        Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true })?.Dispose();
                    }

                    break;
                case ShelfAction.Reveal:
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{targets[0].Path}\"") { UseShellExecute = true })?.Dispose();
                    break;
                case ShelfAction.CopyPath:
                    CopyText(ShelfStore.PathsText(targets));
                    break;
                case ShelfAction.Share:
                    Share(targets);
                    break;
                default:
                    selected.ExceptWith(targets.Select(i => i.Id));
                    _ = shelf.Remove(targets);
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

        var head = p.Shapes(body, new RectangleF(0, 0, size.X, ShelfLayout.GridTop));
        foreach (var line in ShelfLayout.HeadText(size, items.Count))
        {
            p.Text(head, line);
        }

        if (items.Count == 0)
        {
            var empty = new RectangleF(ShelfLayout.PadX, ShelfLayout.GridTop, size.X - 2 * ShelfLayout.PadX, size.Y - ShelfLayout.GridTop - ShelfLayout.PadY);
            var box = p.Shapes(body, empty);
            p.Box(box, new RectangleF(0, 0, empty.Width, empty.Height), 8, null, p.Brush(Color.FromArgb(46, 255, 255, 255)), 1).StrokeDashArray.Add(3);
            foreach (var line in ShelfLayout.EmptyText(empty.Size))
            {
                p.Text(box, line);
            }

            return;
        }

        int visible = ShelfLayout.Rows(size) * ShelfLayout.Columns;
        for (int slot = 0; slot < visible && first + slot < items.Count; slot++)
        {
            Tile(p, items[first + slot], ShelfLayout.Tile(slot, size), selected.Contains(items[first + slot].Id));
        }
    }

    private void Tile(Parts p, ShelfItem item, RectangleF r, bool isSelected)
    {
        var v = p.Shapes(body!, r);
        v.Opacity = item.Missing ? 0.55f : 1;
        var local = new RectangleF(0, 0, r.Width, r.Height);
        p.Box(v, local, 8, p.Brush(isSelected ? ShelfColors.CardSelected : ShelfColors.Card), isSelected ? p.Brush(ShelfColors.Info) : null, 1);
        const float pad = ShelfLayout.TilePad, icon = ShelfLayout.Icon;
        // a coloured square by kind stands in for the file's own icon and thumbnail, which go on image
        // surfaces through the panel host's art device once it merges.
        p.Box(v, new RectangleF(pad, pad, icon, icon), 7, p.Brush(ShelfColors.Rgb(ShelfLayout.KindColor(item))));
        foreach (var line in ShelfLayout.TileText(item, r.Size))
        {
            p.Text(v, line);
        }

        if (isSelected)
        {
            foreach (var action in Enum.GetValues<ShelfAction>())
            {
                Button(p, v, action, ShelfLayout.ActionButton(action, local), ShelfStore.Allows(action, item));
            }
        }
    }

    /// <summary>A dark rounded button with a line glyph; an action the item does not allow is dimmed.</summary>
    private static void Button(Parts p, ShapeVisual into, ShelfAction action, RectangleF b, bool allowed)
    {
        p.Box(into, b, 5, p.Brush(ShelfColors.ButtonFill));
        var ink = p.Brush(allowed ? ShelfColors.Ink : ShelfColors.Ink3);
        Vector2 At(float x, float y) => new(b.X + x, b.Y + y);
        switch (action)
        {
            case ShelfAction.Open:
                p.Box(into, new RectangleF(b.X + 5, b.Y + 8, 9, 9), 1.5f, null, ink, 1.3f);
                p.Line(into, At(10, 12), At(16, 6), ink);
                p.Line(into, At(12, 6), At(16, 6), ink);
                p.Line(into, At(16, 6), At(16, 10), ink);
                break;
            case ShelfAction.Reveal:
                p.Box(into, new RectangleF(b.X + 5, b.Y + 6, 6, 3), 1, null, ink, 1.3f);
                p.Box(into, new RectangleF(b.X + 5, b.Y + 8, 12, 9), 1.5f, null, ink, 1.3f);
                break;
            case ShelfAction.CopyPath:
                p.Box(into, new RectangleF(b.X + 8, b.Y + 5, 8, 9), 1.5f, null, ink, 1.3f);
                p.Box(into, new RectangleF(b.X + 5, b.Y + 8, 8, 9), 1.5f, p.Brush(Color.FromArgb(255, 20, 20, 22)), ink, 1.3f);
                break;
            case ShelfAction.Share:
                p.Line(into, At(11, 5), At(11, 13), ink);
                p.Line(into, At(8, 8), At(11, 5), ink);
                p.Line(into, At(11, 5), At(14, 8), ink);
                p.Line(into, At(6, 11), At(6, 16), ink);
                p.Line(into, At(6, 16), At(16, 16), ink);
                p.Line(into, At(16, 16), At(16, 11), ink);
                break;
            default:
                p.Line(into, At(5, 7), At(17, 7), ink);
                p.Line(into, At(9, 5), At(13, 5), ink);
                p.Box(into, new RectangleF(b.X + 7, b.Y + 8, 8, 9), 1.5f, null, ink, 1.3f);
                break;
        }
    }
}
