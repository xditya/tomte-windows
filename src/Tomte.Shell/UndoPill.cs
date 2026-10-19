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
/// The Compact pill after an AI view closed: what closed, then Undo at the right end, in the accent as the palette's Run, with
/// Win+Alt+Z beside the word while Tomte holds that chord. Laid out by <see cref="CompactLayout.Undo"/>. A click on Undo
/// is the only target; the rest of the pill does nothing. It fades in with the shape and goes with the next state change.
/// </summary>
internal sealed class UndoPill : IInteractive, IDisposable
{
    private const string UndoId = "undo", Undo = "Undo";
    private static TextStyle Label => PaletteLayout.RunButton with { Align = TextAlign.Leading };
    private static TextStyle Key => TextStyle.Small with { Color = Theme.WithOpacity(Theme.OnAccent, 0.55f) };

    private readonly IslandWindow island;
    private readonly IslandStateMachine machine;
    private readonly string line;
    private readonly Action undo;
    private readonly ContainerVisual root, button;
    private readonly ShapeVisual shapes;
    private readonly CompositionRoundedRectangleGeometry pill;
    private readonly TextVisual words, label, key;
    private bool keyHeld = true, fadeInPending = true, disposed;

    public UndoPill(IslandWindow island, IslandStateMachine machine, TextRenderer text, string line, Action undo)
    {
        this.island = island;
        this.machine = machine;
        this.line = line;
        this.undo = undo;
        var c = island.Compositor;
        root = c.CreateContainerVisual();
        root.Opacity = 0;
        button = c.CreateContainerVisual();
        root.Children.InsertAtTop(button);
        shapes = c.CreateShapeVisual();
        button.Children.InsertAtTop(shapes);
        pill = c.CreateRoundedRectangleGeometry();
        var shape = c.CreateSpriteShape(pill);
        shape.FillBrush = c.CreateColorBrush(Argb(PaletteLayout.RunFill));
        shapes.Shapes.Add(shape);
        words = text.Create(root);
        label = text.Create(root);
        key = text.Create(root);
        island.Dips.Children.InsertAtTop(root);
        island.ContentReady += FadeIn;
        machine.Changed += OnState;
        Lay();
    }

    /// <summary>How wide the pill is for what it shows.</summary>
    public float Width { get; private set; }

    public IReadOnlyList<Target> Targets { get; private set; } = [];

    public event Action? TargetsChanged;

    /// <summary>Win+Alt+Z could not be registered: the button says Undo alone, and the pill narrows to fit.</summary>
    public void KeyTaken()
    {
        keyHeld = false;
        Lay();
    }

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
        island.Interaction.Unregister(this);
        island.ContentReady -= FadeIn;
        machine.Changed -= OnState;
        words.Dispose();
        label.Dispose();
        key.Dispose();
        island.Dips.Children.Remove(root);
        shapes.Dispose();
        button.Dispose();
        root.Dispose();
    }

    private static Color Argb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);

    private void Lay()
    {
        string chord = UndoClose.Chord.ToString();
        float lw = TextSlot.Measure(Undo, Label, 200), kw = keyHeld ? TextSlot.Measure(chord, Key, 200) : 0;
        float lineWidth = TextSlot.Measure(line, CompactLayout.LineStyle, 400);
        var (width, slot, box) = CompactLayout.Undo(island.Mascot.Enabled, lineWidth, lw + (keyHeld ? CompactLayout.UndoKeyGap + kw : 0));
        Width = width;
        words.Show(line, CompactLayout.LineStyle, slot);
        button.Offset = new Vector3(box.X, box.Y, 0);
        shapes.Size = new Vector2(box.Width, box.Height);
        pill.Size = shapes.Size;
        pill.CornerRadius = new Vector2(box.Height / 2);
        float x = box.X + CompactLayout.UndoPadX;
        label.Show(Undo, Label, new RectangleF(x, box.Y, lw + 1, box.Height));
        key.Show(keyHeld ? chord : "", Key, new RectangleF(x + lw + CompactLayout.UndoKeyGap, box.Y, kw + 1, box.Height));
        Targets = [new Target(UndoId, box, box.Height / 2, TargetKind.Button)];
        TargetsChanged?.Invoke();
    }

    private void FadeIn()
    {
        if (fadeInPending && !disposed && machine.State == IslandState.Compact)
        {
            fadeInPending = false;
            root.StartAnimation("Opacity", PanelHost.Fade(island.Compositor, 1, PanelHost.FadeInMs));
            island.Interaction.Register(this, p => p, p => p);
        }
    }

    private void OnState(IslandState from, IslandState to)
    {
        if (to == IslandState.Compact)
        {
            return;
        }

        // Out with the shape: no more clicks, and the content fades over 0.1 s, then goes.
        island.Interaction.Unregister(this);
        machine.Changed -= OnState;
        root.StartAnimation("Opacity", PanelHost.Fade(island.Compositor, 0, PanelHost.FadeOutMs));
        PanelHost.AfterFadeOut(island, Dispose).Start();
    }
}
