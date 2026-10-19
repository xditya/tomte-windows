using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Interaction;
using Windows.UI.Composition;
using Windows.Win32;

namespace Tomte.Shell;

/// <summary>
/// A drop zone's panel in the Palette state, laid out by <see cref="DropResultLayout"/>: the zone and the file, where
/// the request went, a line of progress or trouble, the answer as it streams, the size question's choices and the
/// result files, which a press and drag takes out of the island through the shelf's own drag and a double click or
/// Enter opens. The choices and the tiles are the interaction host's targets. Text is the text renderer's; only a run
/// that changed is drawn again. It fades in with the shape and goes when the island leaves the Palette state, which
/// also ends the request and answers an open size question with cancel. The answer scrolls with the wheel when it is
/// longer than its band.
/// </summary>
internal sealed class DropResultView : IInteractive
{
    private readonly IslandWindow island;
    private readonly TriggerController triggers;
    private readonly ShelfStore shelf;
    private readonly TextRenderer text;
    private readonly Action<string>? log;
    private readonly ContainerVisual root;
    private readonly List<TextVisual> slots = [], runs = [];
    private readonly ContainerVisual band, scrolled;
    private readonly float width, maxHeight;
    private float scroll;
    private Parts? shapes;
    private string shapesKey = "";
    private DropResultLayout? layout;
    private DropResult? shown;
    private IReadOnlyList<ShelfItem> files = [];
    private IReadOnlyList<Target> targets = [];
    private bool fadeInPending = true;

    public DropResultView(IslandWindow island, TriggerController triggers, ShelfStore shelf, TextRenderer text, Action<string>? log)
    {
        this.island = island;
        this.triggers = triggers;
        this.shelf = shelf;
        this.text = text;
        this.log = log;
        // The palette's size within the 60% by 50% monitor cap the shape is held to.
        width = (float)Math.Min(PanelSize.PaletteWidth, 0.6 * island.MonitorSize.Width);
        maxHeight = (float)Math.Min(PanelSize.PaletteMaxHeight, 0.5 * island.MonitorSize.Height);
        root = island.Compositor.CreateContainerVisual();
        root.Offset = new Vector3(-width / 2, 0, 0);
        root.Opacity = 0;
        // The answer's runs sit in one container clipped to the answer's band and moved by the scroll.
        band = island.Compositor.CreateContainerVisual();
        band.Clip = island.Compositor.CreateInsetClip();
        scrolled = island.Compositor.CreateContainerVisual();
        band.Children.InsertAtTop(scrolled);
        root.Children.InsertAtTop(band);
        island.Dips.Children.InsertAtTop(root);
        island.ContentReady += FadeIn;
        island.Wheel += OnWheel;
        triggers.Machine.Changed += OnState;
        // In the panel's own DIPs: x from its left edge.
        island.Interaction.Register(this, p => p + new Vector2(width / 2, 0), p => p - new Vector2(width / 2, 0));
    }

    /// <summary>The island left the Palette state; the view is gone.</summary>
    public event Action? Closed;

    /// <summary>A choice of the size question was clicked, by its index.</summary>
    public event Action<int>? Chose;

    public event Action? TargetsChanged;

    public IReadOnlyList<Target> Targets => targets;

    public bool Open { get; private set; } = true;

    /// <summary>Where the answer is scrolled to; set before the first <see cref="Show"/> to start there.</summary>
    public float Scroll
    {
        get => scroll;
        set => scroll = value;
    }

    /// <summary>The panel's height for what it shows now, in DIPs.</summary>
    public float Height => layout?.Height ?? (float)PanelSize.PaletteMinHeight;

    /// <summary>Shows what the panel holds now; <paramref name="items"/> are the result files behind its tiles.</summary>
    public void Show(DropResult result, IReadOnlyList<ShelfItem> items)
    {
        if (!Open)
        {
            return;
        }

        (shown, files) = (result, items);
        var next = new DropResultLayout(result, width, island.Mascot.Enabled, maxHeight);
        layout = next;
        while (slots.Count < next.Slots.Count)
        {
            slots.Add(text.Create(root));
        }

        while (slots.Count > next.Slots.Count)
        {
            slots[^1].Dispose();
            slots.RemoveAt(slots.Count - 1);
        }

        for (int i = 0; i < next.Slots.Count; i++)
        {
            slots[i].Show(next.Slots[i]);
        }

        while (runs.Count < next.Body.Count)
        {
            runs.Add(text.Create(scrolled));
        }

        while (runs.Count > next.Body.Count)
        {
            runs[^1].Dispose();
            runs.RemoveAt(runs.Count - 1);
        }

        band.Offset = new Vector3(next.Band.X, next.Band.Y, 0);
        band.Size = new Vector2(next.Band.Width, next.Band.Height);
        // Where the user left it, as far as the answer now allows; a new answer starts at its top.
        scroll = Math.Min(scroll, next.MaxScroll);
        scrolled.Offset = new Vector3(0, -scroll, 0);
        for (int i = 0; i < next.Body.Count; i++)
        {
            runs[i].Show(next.Body[i].Run, next.Body[i].At);
        }

        Shapes(next, result);
        var now = next.Targets(files.Count);
        if (!now.SequenceEqual(targets))
        {
            targets = now;
            TargetsChanged?.Invoke();
        }
    }

    /// <summary>
    /// Buttons for the choices, raised, the last (cancel, the default) filled with the accent, and cards for the tiles;
    /// built again only when they move.
    /// </summary>
    private void Shapes(DropResultLayout next, DropResult result)
    {
        string key = string.Join(';', next.Choices.Concat(next.Tiles).Select(r => $"{r.X},{r.Y},{r.Width},{r.Height}")) + string.Join(';', result.Tiles.Select(t => t.Color));
        if (key == shapesKey)
        {
            return;
        }

        shapesKey = key;
        shapes?.Dispose();
        var p = shapes = new Parts(island.Compositor, text);
        var canvas = p.Shapes(root, new RectangleF(0, 0, width, (float)PanelSize.PaletteMaxHeight));
        // Under the text: the sprites were attached first, and the shapes go to the bottom of the container.
        root.Children.Remove(canvas);
        root.Children.InsertAtBottom(canvas);
        for (int i = 0; i < next.Choices.Count; i++)
        {
            bool cancel = i == next.Choices.Count - 1;
            p.Box(canvas, next.Choices[i], DropResultLayout.Corner, p.Brush(cancel ? ShelfColors.Accent : ShelfColors.CardSelected));
        }

        for (int i = 0; i < next.Tiles.Count; i++)
        {
            p.Box(canvas, next.Tiles[i], DropResultLayout.TileCorner, p.Brush(ShelfColors.Card));
            p.Box(canvas, DropResultLayout.Icon(next.Tiles[i]), DropResultLayout.IconCorner, p.Brush(ShelfColors.Rgb(result.Tiles[i].Color)));
        }
    }

    public void Activate(string id) => Activate(id, ActivateBy.Click);

    /// <summary>A choice answers on a click or a key; a tile opens its file on a double click or Enter, as on the shelf.</summary>
    public void Activate(string id, ActivateBy by)
    {
        if (!Live)
        {
            return;
        }

        var (tile, choice) = DropResultLayout.Of(id);
        if (choice >= 0)
        {
            Chose?.Invoke(choice);
        }
        else if (tile >= 0 && tile < files.Count && by != ActivateBy.Click && ShelfStore.Allows(ShelfAction.Open, files[tile]))
        {
            try
            {
                Process.Start(new ProcessStartInfo(files[tile].Path) { UseShellExecute = true })?.Dispose();
            }
            // What the shelf's own Open catches.
            catch (Exception e) when (e is Win32Exception or IOException or UnauthorizedAccessException or InvalidOperationException or System.Runtime.InteropServices.COMException)
            {
                log?.Invoke($"zone result: open failed: {e.Message}");
            }
        }
    }

    private void OffShelf()
    {
        if (!Open || shown is null)
        {
            return;
        }

        var tiles = shown.Tiles.Select((t, i) => i < files.Count && !shelf.Items.Any(s => s.Id == files[i].Id) ? t with { Meta = ResultTile.OffShelf } : t).ToList();
        if (!tiles.SequenceEqual(shown.Tiles))
        {
            Show(shown with { Tiles = tiles }, files);
        }
    }

    /// <summary>A press on a tile: Win32 runs the press, move and release loop with the system's drag threshold, and past it the shelf's own drag takes the file out.</summary>
    public bool TryDrag(string id, Vector2 at)
    {
        var (tile, _) = DropResultLayout.Of(id);
        if (!Live || tile < 0 || tile >= files.Count)
        {
            return false;
        }

        PInvoke.GetCursorPos(out var cursor);
        if (!PInvoke.DragDetect(island.Hwnd, cursor))
        {
            return false;
        }

        // A drag the target took may take the file off the shelf; its tile then says so.
        _ = ShelfDrag.Run(island, triggers, shelf, [files[tile]], log).ContinueWith(_ => island.Queue.TryEnqueue(OffShelf), TaskScheduler.Default);
        return true;
    }

    private bool Live => Open && layout is not null && triggers.Machine.State == IslandState.Palette;

    // The wheel over the answer scrolls it, three lines a notch.
    private void OnWheel(Vector2 point, int delta)
    {
        if (!Open || layout is null || triggers.Machine.State != IslandState.Palette)
        {
            return;
        }

        var local = point + new Vector2(width / 2, 0);
        if (!layout.Band.Contains(local.X, local.Y))
        {
            return;
        }

        scroll = layout.Scrolled(scroll, delta);
        scrolled.Offset = new Vector3(0, -scroll, 0);
        foreach (var run in runs)
        {
            run.Refresh();
        }
    }

    private void FadeIn()
    {
        if (fadeInPending && Open)
        {
            fadeInPending = false;
            root.StartAnimation("Opacity", PanelHost.Fade(island.Compositor, 1, PanelHost.FadeInMs));
        }
    }

    private void OnState(IslandState from, IslandState to)
    {
        if (to == IslandState.Palette || !Open)
        {
            return;
        }

        Open = false;
        triggers.Machine.Changed -= OnState;
        island.ContentReady -= FadeIn;
        island.Interaction.Unregister(this);
        island.Wheel -= OnWheel;
        // Out with the shape: the content fades over 0.1 s, then goes; a timer, because a sleeping display finishes no animation.
        root.StartAnimation("Opacity", PanelHost.Fade(island.Compositor, 0, PanelHost.FadeOutMs));
        PanelHost.AfterFadeOut(island, Dispose).Start();
        Closed?.Invoke();
    }

    private void Dispose()
    {
        shapes?.Dispose();
        foreach (var visual in slots.Concat(runs))
        {
            visual.Dispose();
        }

        scrolled.Dispose();
        band.Dispose();
        island.Dips.Children.Remove(root);
        root.Dispose();
    }
}
