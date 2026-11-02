using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using System.Runtime.CompilerServices;
using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Shortcuts;
using Tomte.Core.Text;
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
/// The Shelf panel's body: a head row, a grid of tiles under it, newest first, and a footer; two rows of four at the
/// Shelf panel's size, three columns on a narrow one, the wheel scrolling a row a notch. The head row shows the title and
/// the count, or the action buttons of the tile under the pointer or with keyboard focus, and always the filter, which
/// keeps its choice while Tomte runs. Each tile previews its item: the picture, the first lines of a text, a link's host,
/// a folder, or a page's sketch. A click selects (Ctrl adds or takes away), a double click or Enter opens, a press and
/// drag drags the tile out (the whole selection when it is one of it), and the buttons act on the same items. Remove
/// and Clear shelf ask once; Clear shelf and a drag out leave pinned items, which carry a pin on their preview. Missing items are dimmed, with their own meta line. The island's interaction host runs the
/// presses, hover, tooltips and focus.
/// The Sets button next to the filter swaps the grid for the saved sets: a name box and Save (the selection, or
/// everything on the shelf), then a row per set with its counts, Bring back, Rename (the name turns into a box) and
/// Delete, which asks once.
/// </summary>
internal sealed class ShelfPanel(IslandWindow island, TriggerController triggers, ShelfStore shelf, TextRenderer text, TypingFocus focus, Action<string>? log) : IPanel, IInteractive
{
    private readonly HashSet<long> selected = [];

    // The sets view: whether it shows, the first set row on the page, the set whose name is being edited, and the boxes.
    private bool setsOpen;
    private int setFirst;
    private long? renaming;
    private EditorView? nameBox, renameBox;
    private TextVisual? placeholder;
    private readonly RemoveQuestion deleteQuestion = new();
    private Windows.System.DispatcherQueueTimer? deleteWait;

    // What the tiles read about their items and the pictures on the page, for one showing of the panel.
    private readonly Dictionary<string, ShelfPeek> peeks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, CompositionSurfaceBrush> thumbs = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> loading = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<PixelSurfaces> surfaces = [];
    private int generation;

    private DataTransferManager? shareManager;
    private Windows.System.DispatcherQueueTimer? shareWait, bandWait, confirmWait, clearWait;
    private List<ShelfItem> sharing = [];
    private int shareAsked;
    private ContainerVisual? body;
    private Vector2 size;
    private Parts? parts, band;
    private int first, wheel;
    private IReadOnlyList<Target> targets = [];

    // The filter stays as it was left from one showing to the next; the items it lets through, as last built.
    private ShelfFilter filter;
    private List<ShelfItem> showing = [];

    // Clear shelf asks first; when it asked, so a double click does not answer it.
    private bool clearing;
    private long clearAskedAt;

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
        CloseSets();
        selected.Clear();
        bandWait?.Stop();
        (shown, due, last, hovering) = (null, null, null, null);
        Drop();
        Clearing(false);
        first = wheel = 0;
        targets = [];
        generation++;
        peeks.Clear();
        thumbs.Clear();
        loading.Clear();
        surfaces.ForEach(s => s.Dispose());
        surfaces.Clear();
    }

    /// <summary>A press on none of the targets: the selection goes. The host runs the presses on targets.</summary>
    public void Press(Vector2 point)
    {
        // A press on the name field's padding gives its box the keys.
        if (setsOpen && nameBox is not null && !nameBox.Box.Contains(point.X, point.Y)
            && ShelfLayout.SaveRow(size, "").Field.Contains(point.X, point.Y) && !SaveButton().Contains(point.X, point.Y))
        {
            focus.Take(nameBox);
            return;
        }

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
        if (setsOpen)
        {
            int to = ShelfLayout.ScrollSets(setFirst, notches, shelf.Sets.Count, size);
            if (to != setFirst)
            {
                setFirst = to;
                EndRename(keepKeys: false);
                Rebuild();
            }

            return;
        }

        int next = ShelfLayout.Scroll(first, notches, showing.Count, size);
        if (next != first)
        {
            first = next;
            Rebuild();
        }
    }

    public void Activate(string id) => Activate(id, ActivateBy.Click);

    public void Activate(string id, ActivateBy by)
    {
        if (Enum.GetValues<ShelfFilter>().FirstOrDefault(f => ShelfLayout.FilterId(f) == id) is var picked && ShelfLayout.FilterId(picked) == id)
        {
            if (picked != filter || setsOpen)
            {
                CloseSets();
                (filter, first) = (picked, 0);
                selected.Clear();
                Show(null);
                Rebuild();
            }

            return;
        }

        if (SetsActivate(id, by))
        {
            return;
        }

        if (id == ShelfLayout.ClearId)
        {
            Clear();
            return;
        }

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
    /// second Delete, not a repeat, removes; Ctrl+C copies the path; Ctrl+Shift+E shows it in its folder; P pins or unpins. An arrow off
    /// the page scrolls a row first, so the arrow finds the tile it heads for.
    /// </summary>
    public bool KeyDown(string id, NavKey key, bool repeat)
    {
        if (key is NavKey.Up or NavKey.Down or NavKey.Left or NavKey.Right && Item(id) is { } from)
        {
            var items = showing;
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
            NavKey.PinItem => ShelfAction.Pin,
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
        // Keyboard focus takes the band at once: the waits are for a pointer crossing tiles on its way to the band, and a
        // press sets the focus too.
        if (id is not null && Item(id) is { } focused && island.Interaction.UsingKeys && island.Interaction.IsFocus(this, id))
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

    /// <summary>
    /// Clear shelf: the first click asks, a second within a few seconds takes every item but the pinned ones off the
    /// shelf. Files stay where they are. A second click sooner than the double-click time is the first one doubled and does not count.
    /// </summary>
    private void Clear()
    {
        long now = Environment.TickCount64;
        if (!clearing)
        {
            clearAskedAt = now;
            Clearing(true);
            return;
        }

        if (now - clearAskedAt < PInvoke.GetDoubleClickTime())
        {
            return;
        }

        Clearing(false);
        var all = ShelfLayout.Clearable(shelf.Items);
        log?.Invoke($"shelf: clearing {all.Count} items");
        selected.Clear();
        Show(null);
        _ = shelf.Remove(all);
    }

    private void Clearing(bool on)
    {
        bool was = clearing;
        clearing = on;
        clearWait?.Stop();
        if (on)
        {
            if (clearWait is null)
            {
                clearWait = island.Queue.CreateTimer();
                clearWait.Interval = TimeSpan.FromMilliseconds(RemoveQuestion.AskMs);
                clearWait.IsRepeating = false;
                clearWait.Tick += (_, _) => Clearing(false);
            }

            clearWait.Start();
        }

        if (was != on)
        {
            Rebuild();
        }
    }

    private static unsafe nint Handle(HWND hwnd) => (nint)hwnd.Value;

    /// <summary>Text onto the clipboard; the AI layer's assembly, where the clipboard code lives, loads with the first copy, not before.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void CopyText(string text) => Tomte.AI.Palette.DesktopEffects.CopyToClipboard(text);

    private static ShelfAction? ActionOf(string id) =>
        id.StartsWith("act:", StringComparison.Ordinal) && Enum.TryParse<ShelfAction>(id.AsSpan(4), out var action) ? action : null;

    private ShelfItem? Item(string id) => shelf.Items.FirstOrDefault(i => ShelfLayout.TileId(i) == id);

    private List<ShelfItem> Selection(IReadOnlyList<ShelfItem> items) => [.. items.Where(i => selected.Contains(i.Id))];

    /// <summary>What the band and a drag act on: the whole selection when the item is one of it, else the item alone.</summary>
    private List<ShelfItem> Acting(ShelfItem item) => selected.Contains(item.Id) ? Selection(shelf.Items) : [item];

    /// <summary>The item the band shows the actions of, while its tile is on the page.</summary>
    private ShelfItem? Owner()
    {
        if (setsOpen)
        {
            return null;
        }

        var items = showing;
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
                    Open(allowed);
                    break;
                case ShelfAction.Reveal:
                    // Explorer by its full path, never a name looked up where something else could stand in for it.
                    Process.Start(new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe"), $"/select,\"{allowed[0].Path}\"") { UseShellExecute = true })?.Dispose();
                    break;
                case ShelfAction.CopyPath:
                    CopyText(ShelfStore.PathsText(allowed));
                    island.Interaction.Acknowledge(this, ShelfLayout.ActionId(action));
                    break;
                case ShelfAction.Share:
                    Share(allowed);
                    break;
                case ShelfAction.Pin:
                    _ = shelf.SetPinned(allowed, !ShelfLayout.AllPinned(allowed));
                    break;
                default:
                    // Focus on the band's Remove goes to the tile first, so it moves on to the tile that takes its place.
                    if (island.Interaction.UsingKeys && island.Interaction.IsFocus(this, ShelfLayout.ActionId(ShelfAction.Remove)))
                    {
                        island.Interaction.Focus(this, ShelfLayout.TileId(owner));
                    }

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

    /// <summary>Opens each item as File Explorer would. Throws what <see cref="Process.Start(ProcessStartInfo)"/> throws.</summary>
    internal static void Open(IEnumerable<ShelfItem> items)
    {
        foreach (var item in items)
        {
            Process.Start(new ProcessStartInfo(item.Path) { UseShellExecute = true })?.Dispose();
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

    /// <summary>What Save puts in a set: the selection when there is one, else everything on the shelf.</summary>
    private List<ShelfItem> SaveItems() => selected.Count > 0 ? Selection(shelf.Items) : [.. shelf.Items];

    private string SaveLabel() => ShelfLayout.SaveLabel(SaveItems().Count, selected.Count > 0);

    private RectangleF SaveButton() => ShelfLayout.SaveRow(size, SaveLabel()).Button;

    /// <summary>The set Delete asks about now, or null.</summary>
    private long? Deleting() => deleteQuestion.Item is long id && deleteQuestion.Asks(id, Environment.TickCount64) ? id : null;

    /// <summary>The sets view's targets; false for an id that is none of them.</summary>
    private bool SetsActivate(string id, ActivateBy by)
    {
        bool ours = id is ShelfLayout.SetsId or ShelfLayout.SetNameId or ShelfLayout.RenameBoxId or ShelfLayout.SaveSetId || ShelfLayout.SetPart(id) is not null;
        if (!ours || by == ActivateBy.DoubleClick)
        {
            // The first click of a double click already acted.
            return ours;
        }

        switch (id)
        {
            case ShelfLayout.SetsId:
                if (setsOpen)
                {
                    CloseSets();
                }
                else
                {
                    (setsOpen, setFirst) = (true, 0);
                    Show(null);
                }

                Rebuild();
                return true;
            case ShelfLayout.SetNameId:
                // Focus reached the box with the keys: it takes them. Never on its own, so no foreground is taken here.
                if (nameBox is not null && focus.HasKeys)
                {
                    focus.Take(nameBox);
                }

                return true;
            case ShelfLayout.RenameBoxId:
                if (renameBox is not null && focus.HasKeys)
                {
                    focus.Take(renameBox);
                }

                return true;
            case ShelfLayout.SaveSetId:
                Save();
                return true;
        }

        if (ShelfLayout.SetPart(id) is not (long set, string part))
        {
            return false;
        }

        switch (part)
        {
            case "back":
                // Back to the items, at the top, where the set's items land once the worker has put them there.
                CloseSets();
                (first, filter) = (0, ShelfFilter.All);
                selected.Clear();
                Rebuild();
                _ = shelf.BringBack(set);
                break;
            case "rename":
                StartRename(set);
                break;
            default:
                AskDelete(set);
                break;
        }

        return true;
    }

    /// <summary>Saves the selection, or the whole shelf, as a set named what the box holds.</summary>
    private void Save()
    {
        var items = SaveItems();
        if (items.Count == 0 || nameBox is null)
        {
            return;
        }

        string name = nameBox.Services.Text;
        nameBox.Services.Text = "";
        selected.Clear();
        island.Interaction.Acknowledge(this, ShelfLayout.SaveSetId, "Saved");
        _ = shelf.SaveSet(name, items);
        Rebuild();
    }

    private void StartRename(long set)
    {
        EndRename(keepKeys: false);
        deleteQuestion.Drop();
        renaming = set;
        Rebuild();
        if (renameBox is not null)
        {
            focus.Take(renameBox);
            renameBox.Services.Selection = (0, -1);
        }
    }

    /// <summary>
    /// Closes the rename box. With <paramref name="keepKeys"/> (Enter or Esc in it) the island keeps the keys for its
    /// targets; otherwise a box that had them lets them go, as any box going does.
    /// </summary>
    private void EndRename(bool keepKeys)
    {
        if (renameBox is { } box)
        {
            if (keepKeys && focus.Focused == box)
            {
                focus.TakeKeys();
            }

            renameBox = null;
            box.Dispose();
        }

        renaming = null;
    }

    private void AskDelete(long set)
    {
        EndRename(keepKeys: false);
        var step = deleteQuestion.Click(set, Environment.TickCount64, PInvoke.GetDoubleClickTime());
        if (step == RemoveStep.Remove)
        {
            _ = shelf.DeleteSet(set);
        }
        else if (step == RemoveStep.Ask)
        {
            if (deleteWait is null)
            {
                deleteWait = island.Queue.CreateTimer();
                deleteWait.Interval = TimeSpan.FromMilliseconds(RemoveQuestion.AskMs);
                deleteWait.IsRepeating = false;
                deleteWait.Tick += (_, _) =>
                {
                    deleteQuestion.Drop();
                    Rebuild();
                };
            }

            deleteWait.Start();
        }

        Rebuild();
    }

    /// <summary>Back to the items: the boxes go, and a box that had the keys lets them go.</summary>
    private void CloseSets()
    {
        EndRename(keepKeys: false);
        nameBox?.Dispose();
        placeholder?.Dispose();
        (nameBox, placeholder, setsOpen) = (null, null, false);
        deleteQuestion.Drop();
        deleteWait?.Stop();
    }

    /// <summary>The sets view in the grid's place: the save row, the sets' rows or a line saying there are none, the footer line, and the boxes over them.</summary>
    private void BuildSets(Parts p)
    {
        var into = p.Shapes(body!, new RectangleF(0, 0, size.X, size.Y));
        var save = ShelfLayout.SaveRow(size, SaveLabel());
        bool canSave = SaveItems().Count > 0;
        p.Box(into, save.Field, ShelfLayout.SetCorner, p.Brush(ShelfColors.Card), p.Brush(ShelfColors.Line));
        p.Box(into, save.Button, ShelfLayout.ActionCorner, p.Brush(ShelfColors.Raised));
        p.Text(into, save.Label with { Style = save.Label.Style with { Color = ShelfColors.Argb(canSave ? ShelfColors.Ink : ShelfColors.Disabled) } });

        var sets = shelf.Sets;
        setFirst = ShelfLayout.ScrollSets(setFirst, 0, sets.Count, size);
        if (sets.Count == 0)
        {
            foreach (var line in ShelfLayout.NoSetsText(size))
            {
                p.Text(into, line);
            }
        }

        RectangleF? renameAt = null;
        foreach (var slot in ShelfLayout.SetSlots(size, sets, setFirst, Deleting()))
        {
            p.Box(into, slot.Row, ShelfLayout.SetCorner, p.Brush(ShelfColors.Card));
            if (renaming == slot.Set.Id)
            {
                renameAt = slot.NameBox;
                p.Box(into, RectangleF.Inflate(slot.NameBox, 6, 4), 8, p.Brush(ShelfColors.Raised), p.Brush(ShelfColors.Mark));
            }
            else
            {
                p.Text(into, slot.Name);
                p.Text(into, slot.Meta);
            }

            p.Box(into, slot.Back, ShelfLayout.ActionCorner, p.Brush(ShelfColors.Raised));
            p.Text(into, slot.BackLabel);
            p.Box(into, slot.Rename, ShelfLayout.ActionCorner, p.Brush(ShelfColors.Raised));
            p.Glyph(into, Glyphs.Rename, ShelfColors.Ink, Centred(slot.Rename));
            if (slot.DeleteLabel is { } asking)
            {
                p.Box(into, slot.Delete, ShelfLayout.ActionCorner, p.Brush(ShelfColors.WarnFill));
                p.Text(into, asking);
            }
            else
            {
                p.Box(into, slot.Delete, ShelfLayout.ActionCorner, p.Brush(ShelfColors.Raised));
                Glyph(p, into, ShelfAction.Remove, Centred(slot.Delete), ShelfColors.Ink);
            }
        }

        p.Text(into, ShelfLayout.SetsFoot(size));

        if (nameBox is null)
        {
            placeholder = text.Create(body!);
            var box = nameBox = focus.Create(body!, multiline: false, TextStyle.Primary.Size);
            box.SendOnEnter(_ => island.Queue.TryEnqueue(Save));
            box.Services.Changed += () => island.Queue.TryEnqueue(ShowPlaceholder);
            // Esc empties the box first; with nothing in it, it leaves the box for the tab's label, and a second Esc
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
        }

        nameBox.Place(save.Box, island.Scale);
        ShowPlaceholder();
        if (renameAt is { } at && renaming is long set)
        {
            if (renameBox is null)
            {
                var box = renameBox = focus.Create(body!, multiline: false, TextStyle.Primary.Size);
                box.Services.Text = sets.First(s => s.Id == set).Name;
                box.SendOnEnter(words => island.Queue.TryEnqueue(() =>
                {
                    EndRename(keepKeys: true);
                    _ = shelf.RenameSet(set, words);
                    Rebuild();
                    island.Interaction.Focus(this, ShelfLayout.SetId(set, "rename"));
                }));
                box.Escape = () =>
                {
                    island.Queue.TryEnqueue(() =>
                    {
                        EndRename(keepKeys: true);
                        Rebuild();
                        island.Interaction.Focus(this, ShelfLayout.SetId(set, "rename"));
                    });
                    return true;
                };
            }

            renameBox.Place(at, island.Scale);
        }
        else if (renameBox is not null)
        {
            // Its row scrolled away or its set went.
            EndRename(keepKeys: false);
        }

        // The hint and the boxes over the rows just drawn.
        foreach (Visual top in new Visual?[] { placeholder?.Visual, nameBox.Visual, renameBox?.Visual }.OfType<Visual>())
        {
            body!.Children.Remove(top);
            body.Children.InsertAtTop(top);
        }

        static RectangleF Centred(RectangleF button) =>
            new(button.X + (button.Width - ShelfLayout.ActionIcon) / 2, button.Y + (button.Height - ShelfLayout.ActionIcon) / 2, ShelfLayout.ActionIcon, ShelfLayout.ActionIcon);
    }

    private void ShowPlaceholder()
    {
        if (nameBox is not null && placeholder is not null)
        {
            placeholder.Show(nameBox.Services.Text.Length == 0 ? ShelfLayout.SetPlaceholder : "", ShelfLayout.CountStyle, nameBox.Box);
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
        var all = shelf.Items;
        showing = [.. all.Where(i => ShelfLayout.Matches(i, filter))];
        var items = showing;
        selected.RemoveWhere(id => !items.Any(i => i.Id == id));
        first = ShelfLayout.Scroll(first, 0, items.Count, size);
        if (setsOpen)
        {
            BuildSets(p);
            Band();
            return;
        }

        if (items.Count == 0)
        {
            var empty = RectangleF.FromLTRB(ShelfLayout.PadX, ShelfLayout.GridTop, size.X - ShelfLayout.PadX, ShelfLayout.GridBottom(size));
            var box = p.Shapes(body, empty);
            var outline = p.Box(box, new RectangleF(0, 0, empty.Width, empty.Height), ShelfLayout.TileCorner, null, p.Brush(ShelfColors.Dash), 1.5f);
            outline.StrokeDashArray.Add(3);
            outline.StrokeDashArray.Add(2);
            foreach (var line in ShelfLayout.EmptyText(empty.Size))
            {
                p.Text(box, line);
            }
        }

        int visible = ShelfLayout.Visible(size, first, items.Count);
        for (int slot = 0; slot < visible; slot++)
        {
            Tile(p, items[first + slot], ShelfLayout.Tile(slot, size), selected.Contains(items[first + slot].Id));
        }

        if (ShelfLayout.DropSlotAt(size, first, items.Count) is int free)
        {
            var r = ShelfLayout.Tile(free, size);
            var dash = p.Shapes(body, r);
            var outline = p.Box(dash, new RectangleF(0, 0, r.Width, r.Height), ShelfLayout.TileCorner, null, p.Brush(ShelfColors.Dash), 1.5f);
            outline.StrokeDashArray.Add(3);
            outline.StrokeDashArray.Add(2);
            var (plus, words, at) = ShelfLayout.DropSlotText(r.Size);
            p.Glyph(dash, Glyphs.Plus, ShelfColors.Ink2, plus);
            p.Text(dash, words, at);
        }

        Foot(p, all.Any(i => !i.Pinned));
        Band();
        Load(items.Skip(first).Take(visible));
    }

    /// <summary>The footer: the hint, the keys a focused tile takes, and Clear shelf while there is anything to clear.</summary>
    private void Foot(Parts p, bool any)
    {
        var foot = ShelfLayout.Foot(size, clearing);
        var into = p.Shapes(body!, new RectangleF(0, 0, size.X, size.Y));
        p.Text(into, foot.Hint);
        foreach (var (cap, keys, does) in foot.Keys)
        {
            p.Keycap(into, cap, keys.Text);
            p.Text(into, does);
        }

        if (any)
        {
            p.Text(into, foot.ClearLabel);
        }
    }

    /// <summary>
    /// The band over the grid, built apart from the tiles so that the pointer moving between tiles redraws only it: the
    /// title and the count, or the action buttons of the owner's tile, which is outlined unless the selection's ring is
    /// on it; and the filter at the right end.
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
        var all = shelf.Items;
        var owner = Owner();
        var acting = owner is null ? [] : Acting(owner);
        bool confirming = question.Asks(owner?.Id, Environment.TickCount64);
        targets = setsOpen
            ? ShelfLayout.SetTargets(size, shelf.Sets, setFirst, Deleting(), SaveLabel(), SaveItems().Count > 0, renaming, filter)
            : ShelfLayout.Targets(size, first, showing, owner, acting, confirming, filter, clearing, all.Any(i => !i.Pinned));
        var (track, segments) = ShelfLayout.Filter(size, filter);
        b.Box(into, track, track.Height / 2, b.Brush(ShelfColors.Card));
        foreach (var segment in segments)
        {
            if (segment.Filter == filter && !setsOpen)
            {
                b.Box(into, segment.Box, segment.Box.Height / 2, b.Brush(ShelfColors.RaisedHi));
            }

            b.Text(into, setsOpen ? segment.Label with { Style = ShelfLayout.FilterOff } : segment.Label);
        }

        if (owner is null)
        {
            // The Sets button: a one-segment track like the filter's, its segment raised while the sets show.
            var sets = ShelfLayout.SetsButton(size);
            b.Box(into, sets, sets.Height / 2, b.Brush(ShelfColors.Card));
            var segmentBox = RectangleF.Inflate(sets, -ShelfLayout.SegmentInset, -ShelfLayout.SegmentInset);
            if (setsOpen)
            {
                b.Box(into, segmentBox, segmentBox.Height / 2, b.Brush(ShelfColors.RaisedHi));
            }

            b.Text(into, new TextSlot(ShelfLayout.SetsLabel, setsOpen ? ShelfLayout.FilterOn : ShelfLayout.FilterOff, segmentBox));

            foreach (var line in ShelfLayout.HeadText(size, all.Count, setsOpen ? ShelfLayout.SetsHead(shelf.Sets.Count) : null))
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

        foreach (var slot in ShelfLayout.Actions(size, confirming, ShelfLayout.AllPinned(acting)))
        {
            bool asking = confirming && slot.Action == ShelfAction.Remove;
            bool allowed = acting.Any(i => ShelfStore.Allows(slot.Action, i));
            b.Box(into, slot.Box, ShelfLayout.ActionCorner, b.Brush(asking ? ShelfColors.WarnFill : ShelfColors.Raised));
            var ink = asking ? ShelfColors.Warn : allowed ? ShelfColors.Ink : ShelfColors.Disabled;
            Glyph(b, into, slot.Action, slot.Icon, ink);
            if (slot.Label is { } label)
            {
                b.Text(into, label with { Style = label.Style with { Color = ShelfColors.Argb(ink) } });
            }
        }

        TargetsChanged?.Invoke();
    }

    private void Tile(Parts p, ShelfItem item, RectangleF r, bool isSelected)
    {
        if (isSelected)
        {
            // The ring sits outside the tile: the box's stroke is drawn inside its own edge.
            var ring = RectangleF.Inflate(r, ShelfLayout.RingWidth, ShelfLayout.RingWidth);
            var around = p.Shapes(body!, ring);
            p.Box(around, new RectangleF(0, 0, ring.Width, ring.Height), ShelfLayout.TileCorner + ShelfLayout.RingWidth, null, p.Brush(ShelfColors.Accent), ShelfLayout.RingWidth);
        }

        var v = p.Shapes(body!, r);
        v.Opacity = item.Missing ? 0.55f : 1;
        p.Box(v, new RectangleF(0, 0, r.Width, r.Height), ShelfLayout.TileCorner, p.Brush(ShelfColors.Card));
        var peek = peeks.GetValueOrDefault(item.Path);
        var preview = ShelfLayout.PreviewIn(r.Size);
        Preview(p, v, item, preview, peek);
        if (item.Pinned)
        {
            PinBadge(p, v, preview);
        }
        foreach (var line in ShelfLayout.TileText(item, r.Size, peek))
        {
            p.Text(v, line);
        }
    }

    /// <summary>The square at a tile's top: the picture, the first lines, the link, the folder, or a page's or a sheet's sketch.</summary>
    private void Preview(Parts p, ShapeVisual v, ShelfItem item, RectangleF at, ShelfPeek? peek)
    {
        const float corner = ShelfLayout.PreviewCorner;
        var kind = ShelfLayout.PreviewOf(item);
        p.Box(v, at, corner, p.Brush(kind == ShelfPreview.Document ? ShelfColors.Of(Theme.Paper) : ShelfColors.Raised));
        var offset = new Vector2(at.X, at.Y);
        switch (kind)
        {
            case ShelfPreview.Image when thumbs.TryGetValue(item.Path, out var picture):
                p.Sprite(v, at, corner, picture);
                break;
            case ShelfPreview.Text when peek?.Text is { } words:
                var (run, from) = ShelfLayout.PeekText(words, at.Size);
                p.Text(v, run, offset + from);
                break;
            case ShelfPreview.Link:
                var (glyph, host) = ShelfLayout.LinkPreview(peek?.Link?.Host ?? ShelfLayout.DisplayName(item), at.Size);
                glyph.Offset(at.X, at.Y);
                p.Glyph(v, Glyphs.Link, ShelfColors.Of(Theme.Link), glyph);
                p.Text(v, host with { Slot = host.Slot with { X = host.Slot.X + at.X, Y = host.Slot.Y + at.Y } });
                break;
            case ShelfPreview.Folder:
                var folder = ShelfLayout.FolderGlyph(at.Size);
                folder.Offset(at.X, at.Y);
                p.Glyph(v, Glyphs.Folder, ShelfColors.Of(Theme.Folder), folder);
                break;
            case ShelfPreview.Document:
                foreach (var (bar, heading) in ShelfLayout.PageSketch(at.Size))
                {
                    p.Box(v, bar with { X = bar.X + at.X, Y = bar.Y + at.Y }, 2, p.Brush(ShelfColors.Of(heading ? Theme.PaperInk : Theme.PaperLine)));
                }

                break;
            case ShelfPreview.Sheet:
                foreach (var (cell, header) in ShelfLayout.SheetSketch(at.Size))
                {
                    p.Box(v, cell with { X = cell.X + at.X, Y = cell.Y + at.Y }, 2, p.Brush(ShelfColors.Of(header ? Theme.KindPicture : Theme.LineStrong)));
                }

                break;
        }
    }

    /// <summary>
    /// Reads what the tiles on the page show beyond their names, on a pool thread, once per showing of the panel: a text's
    /// start, a link's address, a folder's count, a picture's size and the shell's thumbnail for it. The tiles are built
    /// again once it lands.
    /// </summary>
    private async void Load(IEnumerable<ShelfItem> items)
    {
        var wanted = items.Where(i => !i.Missing && !peeks.ContainsKey(i.Path) && loading.Add(i.Path)).ToList();
        if (wanted.Count == 0)
        {
            return;
        }

        int shownAs = generation;
        var compositor = island.Compositor;
        int px = (int)Math.Ceiling(Math.Max(ShelfLayout.TileWidth(size) - 2 * ShelfLayout.TilePad, ShelfLayout.Preview) * island.Scale);
        try
        {
            var (made, found) = await Task.Run(async () =>
            {
                PixelSurfaces? made = null;
                var found = new List<(ShelfItem Item, ShelfPeek Peek, CompositionSurfaceBrush? Picture)>();
                foreach (var item in wanted)
                {
                    var peek = await Peek(item);
                    CompositionSurfaceBrush? picture = null;
                    // Simplification: the shell fits the picture into a square, so a tall photo is scaled up to cover the
                    // wide preview and looks soft; asking for a larger square would fix it at more memory.
                    if (ShelfLayout.PreviewOf(item) == ShelfPreview.Image && ShellIcon.Thumbnail(item.Path, px) is { } image)
                    {
                        made ??= new PixelSurfaces(compositor);
                        picture = made.Brush(image.Width, image.Height, image.Pixels);
                        picture.Stretch = CompositionStretch.UniformToFill;
                    }

                    found.Add((item, peek, picture));
                }

                return (Made: made, Found: found);
            });
            if (shownAs != generation)
            {
                made?.Dispose();
                return;
            }

            if (made is not null)
            {
                surfaces.Add(made);
            }

            foreach (var (item, peek, picture) in found)
            {
                peeks[item.Path] = peek;
                if (picture is not null)
                {
                    thumbs[item.Path] = picture;
                }
            }

            Rebuild();
        }
        catch (Exception e)
        {
            // No device, or a file that went: the tiles keep their sketches.
            log?.Invoke($"shelf: previews not read ({e.GetType().Name})");
        }
    }

    // On a pool thread. Anything that cannot be read is left out of the peek.
    private static async Task<ShelfPeek> Peek(ShelfItem item)
    {
        try
        {
            switch (ShelfLayout.PreviewOf(item))
            {
                case ShelfPreview.Text when ShelfStore.ReadTextFile(item.Path) is { } text:
                    return new ShelfPeek(text.Length > 400 ? text[..400] : text, text.Length);
                case ShelfPreview.Link when ShelfStore.ReadTextFile(item.Path) is { } shortcut:
                    var line = shortcut.Split('\n').Select(l => l.Trim()).FirstOrDefault(l => l.StartsWith("URL=", StringComparison.OrdinalIgnoreCase));
                    return new ShelfPeek(Link: line is not null && Uri.TryCreate(line[4..], UriKind.Absolute, out var uri) ? uri : null);
                case ShelfPreview.Folder:
                    return new ShelfPeek(Entries: Directory.EnumerateFileSystemEntries(item.Path).Count());
                case ShelfPreview.Image:
                    using (var stream = File.OpenRead(item.Path).AsRandomAccessStream())
                    {
                        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
                        return new ShelfPeek(Pixels: ((int)decoder.OrientedPixelWidth, (int)decoder.OrientedPixelHeight));
                    }
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or ArgumentException)
        {
        }

        return new ShelfPeek();
    }

    /// <summary>
    /// The pin on a pinned tile's preview, at its top-right corner: the accent pin on a disc of the tile's own grey, in a
    /// visual of its own so it sits over a picture too.
    /// </summary>
    internal static void PinBadge(Parts p, ContainerVisual into, RectangleF preview)
    {
        var (disc, glyph) = ShelfLayout.PinBadge(preview);
        var badge = p.Shapes(into, disc);
        p.Box(badge, new RectangleF(0, 0, disc.Width, disc.Height), disc.Width / 2, p.Brush(ShelfColors.Card));
        p.Glyph(badge, Glyphs.Pin, ShelfColors.Accent, new RectangleF(glyph.X - disc.X, glyph.Y - disc.Y, glyph.Width, glyph.Height));
    }

    /// <summary>An action's line icon in its square; Pin's from the icon set.</summary>
    private static void Glyph(Parts p, ShapeVisual into, ShelfAction action, RectangleF icon, Color color)
    {
        if (action == ShelfAction.Pin)
        {
            p.Glyph(into, Glyphs.Pin, color, icon);
            return;
        }

        var ink = p.Brush(color);
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
