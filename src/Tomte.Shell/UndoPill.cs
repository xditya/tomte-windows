using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Text;
using Tomte.Core.Triggers;
using Windows.UI.Composition;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>
/// The Compact pill after an AI view closed: what closed, quiet, then an accent button at the right end with the undo
/// glyph and "Undo · 8s", the seconds left counting down while it shows. Laid out by <see cref="CompactLayout.Undo"/>.
/// A click on the button is the only target; the rest of the pill does nothing. Win+Alt+Z does the same while Tomte holds
/// that chord. It fades in with the shape and goes with the next state change; its one-second tick runs only while it
/// shows.
/// </summary>
internal sealed class UndoPill : IInteractive, IDisposable
{
    private const string UndoId = "undo";
    private static TextStyle Label => new(12, 600, Color: Theme.OnAccent);

    private readonly IslandWindow island;
    private readonly IslandStateMachine machine;
    private readonly Func<TimeSpan> left;
    private readonly Action undo;
    private readonly ContainerVisual root, button;
    private readonly ShapeVisual shapes;
    private readonly CompositionContainerShape glyph;
    private readonly TextVisual words, label;
    private readonly Windows.System.DispatcherQueueTimer tick;
    private RectangleF labelSlot;
    private bool fadeInPending = true, disposed;

    public UndoPill(IslandWindow island, IslandStateMachine machine, TextRenderer text, string line, Func<TimeSpan> left, Action undo)
    {
        this.island = island;
        this.machine = machine;
        this.left = left;
        this.undo = undo;
        var c = island.Compositor;
        root = c.CreateContainerVisual();
        root.Opacity = 0;
        button = c.CreateContainerVisual();
        root.Children.InsertAtTop(button);
        shapes = c.CreateShapeVisual();
        button.Children.InsertAtTop(shapes);
        var pill = c.CreateRoundedRectangleGeometry();
        var shape = c.CreateSpriteShape(pill);
        shape.FillBrush = c.CreateColorBrush(Argb(Theme.Accent));
        shapes.Shapes.Add(shape);
        glyph = c.CreateContainerShape();
        shapes.Shapes.Add(glyph);
        PaletteView.DrawGlyph(glyph, Glyphs.Undo, c.CreateColorBrush(Argb(Theme.OnAccent)), CompactLayout.UndoGlyph);
        words = text.Create(root);
        label = text.Create(root);
        island.Dips.Children.InsertAtTop(root);
        tick = island.Queue.CreateTimer();
        tick.IsRepeating = false;
        tick.Tick += (_, _) => Count();

        // Laid out once, for the label it starts with: the pill keeps its width as the seconds count down.
        string first = CompactLayout.UndoLabel(left());
        float lw = TextSlot.Measure(first, Label, 200), lineWidth = TextSlot.Measure(line, CompactLayout.UndoLineStyle, 400);
        var (width, slot, box) = CompactLayout.Undo(island.Mascot.Enabled, lineWidth, CompactLayout.UndoGlyph + CompactLayout.UndoKeyGap + lw);
        Width = width;
        words.Show(line, CompactLayout.UndoLineStyle, slot);
        button.Offset = new Vector3(box.X, box.Y, 0);
        shapes.Size = new Vector2(box.Width, box.Height);
        pill.Size = shapes.Size;
        pill.CornerRadius = new Vector2(box.Height / 2);
        glyph.Offset = new Vector2(CompactLayout.UndoPadX, (box.Height - CompactLayout.UndoGlyph) / 2);
        float x = box.X + CompactLayout.UndoPadX + CompactLayout.UndoGlyph + CompactLayout.UndoKeyGap;
        labelSlot = new RectangleF(x, box.Y, box.Right - x, box.Height);
        Targets = [new Target(UndoId, box, box.Height / 2, TargetKind.Button)];
        island.ContentReady += FadeIn;
        machine.Changed += OnState;
        Count();
    }

    /// <summary>How wide the pill is for what it shows.</summary>
    public float Width { get; }

    public IReadOnlyList<Target> Targets { get; }

    public event Action? TargetsChanged;

    public void Activate(string id)
    {
        if (!disposed && id == UndoId)
        {
            undo();
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        tick.Stop();
        island.Interaction.Unregister(this);
        island.ContentReady -= FadeIn;
        machine.Changed -= OnState;
        words.Dispose();
        label.Dispose();
        island.Dips.Children.Remove(root);
        shapes.Dispose();
        button.Dispose();
        root.Dispose();
    }

    private static Color Argb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);

    /// <summary>The label for the time left now, and a one-shot to the moment it next changes.</summary>
    private void Count()
    {
        if (disposed)
        {
            return;
        }

        var now = left();
        label.Show(CompactLayout.UndoLabel(now), Label, labelSlot);
        double fraction = now.TotalSeconds - Math.Floor(now.TotalSeconds);
        if (now > TimeSpan.FromSeconds(1))
        {
            tick.Interval = TimeSpan.FromSeconds((fraction > 0 ? fraction : 1) + 0.01);
            tick.Start();
        }
    }

    private void FadeIn()
    {
        if (fadeInPending && !disposed && machine.State == IslandState.Compact)
        {
            fadeInPending = false;
            root.StartAnimation("Opacity", PanelHost.Fade(island.Compositor, 1, PanelHost.FadeInMs));
            island.Interaction.Register(this, p => p, p => p);
            TargetsChanged?.Invoke();
        }
    }

    private void OnState(IslandState from, IslandState to)
    {
        if (to == IslandState.Compact)
        {
            return;
        }

        // Out with the shape: no more clicks or ticks, and the content fades over 0.1 s, then goes.
        tick.Stop();
        island.Interaction.Unregister(this);
        machine.Changed -= OnState;
        root.StartAnimation("Opacity", PanelHost.Fade(island.Compositor, 0, PanelHost.FadeOutMs));
        PanelHost.AfterFadeOut(island, Dispose).Start();
    }
}
