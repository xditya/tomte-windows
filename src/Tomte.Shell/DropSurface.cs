using System.Diagnostics;
using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Text;
using Windows.System;
using Windows.UI.Composition;

namespace Tomte.Shell;

/// <summary>
/// The Shelf-drop state's content. First the pill a drag to the top edge opens: a dashed accent ring, the tray glyph and
/// "Drop on the shelf", which is what a drop there does. With a provider set up it adds "or hold for actions": a drag
/// held still on the pill for <see cref="DropHold.HoldMs"/> opens the drop zones in its place, still in the Shelf-drop
/// state, and the drag goes on over them. The zone under the cursor rises and fills with the accent; a drop anywhere
/// that is not an AI zone goes on the shelf. Built when the state is entered and released 0.1 s after it is left; the
/// hold's timer runs only while a drag rests on the pill.
/// </summary>
internal sealed class ShelfDropSurface
{
    private readonly IslandWindow island;
    private readonly IslandStateMachine machine;
    private readonly DropTarget drops;
    private readonly TextRenderer text;
    private readonly Func<double, DropZoneLayout?> zones;
    private readonly Action<double, double> open;
    private readonly Func<DragItem, Task<int?>> estimate;
    private readonly DispatcherQueueTimer release, hold;
    private readonly DropHold still = new();
    private readonly Stopwatch clock = Stopwatch.StartNew();
    private readonly Dictionary<DropZone, Tile> tiles = [];
    private readonly List<Parts> leaving = [];
    private Parts? parts;
    private ContainerVisual? space;
    private CompositionColorBrush? ring;
    private (TextVisual Name, TextVisual Facts, TextVisual Hint)? head;
    private bool fadeInPending, accepting, holdable;
    private int shown;

    public ShelfDropSurface(IslandWindow island, IslandStateMachine machine, DropTarget drops, TextRenderer text,
        Func<double, DropZoneLayout?>? zones = null, Action<double, double>? open = null, Func<DragItem, Task<int?>>? estimate = null)
    {
        this.island = island;
        this.machine = machine;
        this.drops = drops;
        this.text = text;
        this.zones = zones ?? (_ => null);
        this.open = open ?? ((_, _) => { });
        this.estimate = estimate ?? (_ => Task.FromResult<int?>(null));
        release = island.Queue.CreateTimer();
        release.IsRepeating = false;
        release.Interval = TimeSpan.FromMilliseconds(PanelHost.FadeOutMs);
        release.Tick += (_, _) => Release();
        hold = island.Queue.CreateTimer();
        hold.IsRepeating = false;
        hold.Tick += (_, _) => Held();
        machine.Changed += (from, to) =>
        {
            if (to == IslandState.ShelfDrop && from != IslandState.ShelfDrop)
            {
                Show();
            }
            else if (from == IslandState.ShelfDrop && to != IslandState.ShelfDrop)
            {
                Hide();
            }
        };
        // A drag the island refuses (its own drag out, a format it does not take) does not light the ring.
        drops.Entered += () =>
        {
            accepting = drops.Accepting;
            if (ring is not null)
            {
                ring.Color = accepting ? ShelfColors.Accent : ShelfColors.Dash;
            }
        };
        drops.Left += () =>
        {
            StopHold();
            Light(null);
        };
        drops.Over += Over;
        island.ContentReady += () =>
        {
            if (fadeInPending && space is not null)
            {
                fadeInPending = false;
                Fades.Run(space, parts!, show: true);
            }
        };
    }

    /// <summary>The zones drawn now; null while the surface is the pill.</summary>
    public DropZoneLayout? Layout { get; private set; }

    /// <summary>Where a drop now would go: the zone lit under the cursor, null while the zones are closed or nothing the island takes is over them.</summary>
    public DropZone? Hot { get; private set; }

    private void Show()
    {
        Release();
        shown++;
        accepting = drops.Accepting;
        holdable = zones(DropZoneLayout.Width) is not null;
        var p = Begin();
        var g = IslandGeometry.ShelfDrop;
        float w = (float)g.Width, d = (float)g.Depth, inset = DropZoneLayout.RingInset;
        var shapes = p.Shapes(space!, new RectangleF(-w / 2, 0, w, d));
        ring = p.Brush(accepting ? ShelfColors.Accent : ShelfColors.Dash);
        var dashed = p.Box(shapes, new RectangleF(inset, inset, w - (2 * inset), d - (2 * inset)), (d / 2) - inset, null, ring, DropZoneLayout.RingWidth);
        dashed.StrokeDashArray.Add(3);
        dashed.StrokeDashArray.Add(2);
        dashed.StrokeDashCap = CompositionStrokeCap.Round;

        var (glyph, label, hint) = DropZoneLayout.Pill(holdable);
        var tray = p.Keep(island.Compositor.CreateContainerShape());
        tray.Offset = new Vector2(glyph.X + (w / 2), glyph.Y);
        shapes.Shapes.Add(tray);
        PaletteView.DrawGlyph(tray, Glyphs.Shelf, p.Brush(ShelfColors.Accent), glyph.Width);
        p.Text(space!, label);
        p.Text(space!, hint);
    }

    /// <summary>A new part in a new space, to fade in at the next ContentReady; what showed before fades out and goes.</summary>
    private Parts Begin()
    {
        if (parts is not null)
        {
            Fades.Run(space!, parts, show: false);
            leaving.Add(parts);
            release.Start();
        }

        var p = parts = new Parts(island.Compositor, text);
        // Island space: DIPs from the island's centre line and top edge, scaled and kept centred by the island.
        space = p.Attach(island.Dips, island.Compositor.CreateContainerVisual());
        space.Opacity = 0;
        fadeInPending = true;
        (ring, head, Hot) = (null, null, null);
        tiles.Clear();
        return p;
    }

    private void Over(int x, int y)
    {
        if (space is null || !accepting)
        {
            return;
        }

        var at = island.ScreenToIsland(x, y);
        if (Layout is { } layout)
        {
            Light(layout.At(at));
            return;
        }

        // Over the pill or the strip above it, which takes a drag pushed up to the top row: the wait starts, and starts
        // again whenever the cursor moves on.
        var g = IslandGeometry.ShelfDrop;
        bool on = holdable && Math.Abs(at.X) <= g.Width / 2 && at.Y >= -IslandOutline.Gap && at.Y <= g.Depth;
        if (!on)
        {
            StopHold();
        }
        else if (still.Over(at, clock.ElapsedMilliseconds))
        {
            hold.Stop();
            hold.Interval = TimeSpan.FromMilliseconds(DropHold.HoldMs);
            hold.Start();
        }
    }

    private void StopHold()
    {
        still.Reset();
        hold.Stop();
    }

    private void Held()
    {
        long now = clock.ElapsedMilliseconds;
        if (!still.Due(now))
        {
            if (still.Left(now) is > 0 and var left)
            {
                hold.Interval = TimeSpan.FromMilliseconds(left);
                hold.Start();
            }

            return;
        }

        still.Reset();
        OpenZones();
    }

    /// <summary>The drop zones in the pill's place: the panel as wide as the monitor allows, its tiles reflowed to fit.</summary>
    private void OpenZones()
    {
        if (Layout is not null || machine.State != IslandState.ShelfDrop)
        {
            return;
        }

        double width = PanelSize.Fit((DropZoneLayout.Width, 0), island.MonitorSize).W;
        if (zones(width) is not { } layout)
        {
            return;
        }

        var p = Begin();
        Layout = layout;
        var item = drops.Peek() ?? new DragItem("", null, null);
        head = (p.Keep(text.Create(space!)), p.Keep(text.Create(space!)), p.Keep(text.Create(space!)));
        ShowHead(layout, item, null);
        int asked = shown;
        _ = estimate(item).ContinueWith(
            t =>
            {
                if (asked == shown && Layout == layout && t.Status == TaskStatus.RanToCompletion && t.Result is { } tokens)
                {
                    ShowHead(layout, item, tokens);
                }
            },
            TaskScheduler.FromCurrentSynchronizationContext());

        foreach (var (zone, rect) in layout.Zones)
        {
            tiles[zone] = new Tile(p, text, space!, layout, zone, rect);
        }

        p.Text(space!, layout.FootSlot);
        open(width, layout.Height);
        // After the morph is asked for, so the tomte travels to the head row on the shape's springs.
        island.Seat = DropZoneLayout.Seat;
    }

    private void ShowHead(DropZoneLayout layout, DragItem item, int? tokens)
    {
        if (head is not var (name, facts, hint))
        {
            return;
        }

        var slots = layout.Head(item.Name, DropZoneLayout.Facts(item.Bytes, tokens), island.Mascot.Enabled);
        name.Show(slots[0]);
        facts.Show(slots[1]);
        hint.Show(slots[2]);
    }

    private void Light(DropZone? hot)
    {
        if (hot == Hot || space is null)
        {
            return;
        }

        if (Hot is { } was && tiles.TryGetValue(was, out var cold))
        {
            cold.Light(false);
        }

        Hot = hot;
        if (hot is { } now && tiles.TryGetValue(now, out var lit))
        {
            lit.Light(true);
        }
    }

    private void Hide()
    {
        StopHold();
        if (parts is null)
        {
            return;
        }

        Fades.Run(space!, parts, show: false);
        leaving.Add(parts);
        (parts, space, ring, head, Layout, Hot) = (null, null, null, null, null, null);
        tiles.Clear();

        // A one-shot timer, not the fade's end: with the display asleep the compositor finishes no animation.
        release.Start();
    }

    private void Release()
    {
        release.Stop();
        foreach (var gone in leaving)
        {
            gone.Dispose();
        }

        leaving.Clear();
    }

    /// <summary>
    /// One zone's tile: a Card with its glyph at the top and its name and line at the foot, or a dashed outline for the
    /// empty skill slot. Lit, it rises <see cref="DropZoneLayout.Lift"/>, fills with the accent and casts a soft accent
    /// glow, over <see cref="StateLook.FadeMs"/>; its words and glyph turn to the ink on the accent.
    /// </summary>
    private sealed class Tile
    {
        private readonly Parts parts;
        private readonly ContainerVisual visual;
        private readonly SpriteVisual glow;
        private readonly CompositionColorBrush fill, ink;
        private readonly TextVisual name, sub;
        private readonly TextSlot nameSlot, subSlot;
        private readonly float top;

        public Tile(Parts p, TextRenderer text, ContainerVisual space, DropZoneLayout layout, DropZone zone, RectangleF rect)
        {
            parts = p;
            var c = p.Compositor;
            top = rect.Y;
            visual = p.Attach(space, c.CreateContainerVisual());
            visual.Offset = new Vector3(rect.X, rect.Y, 0);
            visual.Size = new Vector2(rect.Width, rect.Height);
            var box = new RectangleF(0, 0, rect.Width, rect.Height);
            var shapes = p.Shapes(visual, box);
            bool empty = zone == DropZone.Skill && layout.SlotEmpty;
            fill = p.Brush(empty ? ShelfColors.Clear : ShelfColors.Card);
            ink = p.Brush(empty ? ShelfColors.Ink2 : ShelfColors.Ink);
            var card = p.Box(shapes, box, DropZoneLayout.TileCorner, fill, empty ? p.Brush(ShelfColors.Dash) : null, 1.5f);
            if (empty)
            {
                card.StrokeDashArray.Add(3);
                card.StrokeDashArray.Add(2);
            }

            var (glyphBox, label, line) = layout.TileText(zone, rect.Size);
            var glyph = p.Keep(c.CreateContainerShape());
            glyph.Offset = new Vector2(glyphBox.X, glyphBox.Y);
            shapes.Shapes.Add(glyph);
            PaletteView.DrawGlyph(glyph, GlyphOf(zone, empty), ink, glyphBox.Width);
            (nameSlot, subSlot) = (empty ? label with { Style = label.Style with { Color = Theme.Ink2 } } : label, line);
            name = p.Keep(text.Create(visual));
            sub = p.Keep(text.Create(visual));
            name.Show(nameSlot);
            sub.Show(subSlot);

            // The glow is the tile's own outline, blurred, in the soft accent, a little below it; it shows only while lit.
            var copy = p.Keep(c.CreateVisualSurface());
            copy.SourceVisual = shapes;
            copy.SourceSize = new Vector2(rect.Width, rect.Height);
            var shadow = p.Keep(c.CreateDropShadow());
            shadow.Mask = p.Keep(c.CreateSurfaceBrush(copy));
            shadow.Color = ShelfColors.Of(Theme.AccentSoft);
            shadow.BlurRadius = 30;
            shadow.Offset = new Vector3(0, 10, 0);
            glow = p.Keep(c.CreateSpriteVisual());
            glow.Size = new Vector2(rect.Width, rect.Height);
            glow.Shadow = shadow;
            glow.Opacity = 0;
            visual.Children.InsertAtBottom(glow);
        }

        public void Light(bool on)
        {
            var c = parts.Compositor;
            var color = parts.Keep(c.CreateColorKeyFrameAnimation());
            color.InsertKeyFrame(1, on ? ShelfColors.Accent : ShelfColors.Card);
            color.Duration = TimeSpan.FromMilliseconds(StateLook.FadeMs);
            fill.StartAnimation("Color", color);
            var lift = parts.Keep(c.CreateScalarKeyFrameAnimation());
            lift.InsertKeyFrame(1, on ? top - DropZoneLayout.Lift : top);
            lift.Duration = color.Duration;
            visual.StartAnimation("Offset.Y", lift);
            glow.StartAnimation("Opacity", Fade(c, on ? 1 : 0));
            ink.Color = on ? ShelfColors.Of(Theme.OnAccent) : ShelfColors.Ink;
            uint onAccent = Theme.OnAccent;
            name.Show(on ? nameSlot with { Style = nameSlot.Style with { Color = onAccent } } : nameSlot);
            sub.Show(on ? subSlot with { Style = subSlot.Style with { Color = Theme.WithOpacity(onAccent, 0.8f) } } : subSlot);
        }

        private ScalarKeyFrameAnimation Fade(Compositor c, float to)
        {
            var fade = parts.Keep(c.CreateScalarKeyFrameAnimation());
            fade.InsertKeyFrame(1, to);
            fade.Duration = TimeSpan.FromMilliseconds(StateLook.FadeMs);
            return fade;
        }

        private static GlyphFigure[] GlyphOf(DropZone zone, bool empty) => zone switch
        {
            DropZone.Summarize => Glyphs.Summarize,
            DropZone.Ask => Glyphs.Ask,
            DropZone.Tables => Glyphs.Table,
            DropZone.Rename => Glyphs.Rename,
            DropZone.Skill => empty ? Glyphs.Plus : Glyphs.Wrench,
            _ => Glyphs.Shelf,
        };
    }
}
