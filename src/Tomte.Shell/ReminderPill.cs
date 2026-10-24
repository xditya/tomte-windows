using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Text;
using Windows.UI.Composition;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>
/// The Compact pill of a reminder that rang: its text, then Snooze 10 min, quiet, and Dismiss in the accent at the right
/// end, laid out by <see cref="CompactLayout.Reminder"/>. The two buttons are the only targets. It fades in with the shape
/// and goes with the next state change; the triggers bring it back while the reminder still rings.
/// </summary>
internal sealed class ReminderPill : IInteractive, IDisposable
{
    public const string SnoozeId = "snooze", DismissId = "dismiss";

    private readonly IslandWindow island;
    private readonly IslandStateMachine machine;
    private readonly Action<string> pressed;
    private readonly ContainerVisual root;
    private readonly ShapeVisual shapes;
    private readonly TextVisual words, snooze, dismiss;
    private bool fadeInPending = true, disposed;

    /// <param name="pressed">A button's id, <see cref="SnoozeId"/> or <see cref="DismissId"/>.</param>
    public ReminderPill(IslandWindow island, IslandStateMachine machine, TextRenderer text, string line, Action<string> pressed)
    {
        this.island = island;
        this.machine = machine;
        this.pressed = pressed;
        var c = island.Compositor;
        root = c.CreateContainerVisual();
        root.Opacity = 0;
        shapes = c.CreateShapeVisual();
        root.Children.InsertAtTop(shapes);
        words = text.Create(root);
        snooze = text.Create(root);
        dismiss = text.Create(root);
        island.Dips.Children.InsertAtTop(root);

        TextStyle quiet = CompactLayout.SnoozeStyle, accent = CompactLayout.DismissStyle;
        float lineWidth = TextSlot.Measure(line, CompactLayout.LineStyle, CompactLayout.ReminderLineMax);
        var (width, slot, left, right) = CompactLayout.Reminder(island.Mascot.Enabled, lineWidth,
            TextSlot.Measure(CompactLayout.SnoozeLabel, quiet, 200), TextSlot.Measure(CompactLayout.DismissLabel, accent, 200));
        Width = width;
        // The pill is offset by its left edge, so the shapes' visual covers the whole of it.
        shapes.Offset = new Vector3(-width / 2, 0, 0);
        shapes.Size = new Vector2(width, (float)IslandGeometry.Compact.Depth);
        words.Show(line, CompactLayout.LineStyle, slot);
        Button(left, Theme.Raised, snooze, CompactLayout.SnoozeLabel, quiet);
        Button(right, Theme.Accent, dismiss, CompactLayout.DismissLabel, accent);
        Targets = [new Target(SnoozeId, left, left.Height / 2, TargetKind.Button), new Target(DismissId, right, right.Height / 2, TargetKind.Button)];
        island.ContentReady += FadeIn;
        machine.Changed += OnState;

        void Button(RectangleF box, uint fill, TextVisual label, string says, TextStyle style)
        {
            var pill = c.CreateRoundedRectangleGeometry();
            pill.Offset = new Vector2(box.X + (width / 2), box.Y);
            pill.Size = new Vector2(box.Width, box.Height);
            pill.CornerRadius = new Vector2(box.Height / 2);
            var shape = c.CreateSpriteShape(pill);
            shape.FillBrush = c.CreateColorBrush(Argb(fill));
            shapes.Shapes.Add(shape);
            label.Show(says, style, new RectangleF(box.X + CompactLayout.UndoPadX, box.Y, box.Width - (2 * CompactLayout.UndoPadX), box.Height));
        }
    }

    /// <summary>How wide the pill is for what it shows.</summary>
    public float Width { get; }

    public IReadOnlyList<Target> Targets { get; }

    public event Action? TargetsChanged;

    public void Activate(string id)
    {
        if (!disposed && id is SnoozeId or DismissId)
        {
            pressed(id);
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
        snooze.Dispose();
        dismiss.Dispose();
        island.Dips.Children.Remove(root);
        shapes.Dispose();
        root.Dispose();
    }

    private static Color Argb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);

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

        // Out with the shape: no more clicks, and the content fades over 0.1 s, then goes.
        island.Interaction.Unregister(this);
        machine.Changed -= OnState;
        root.StartAnimation("Opacity", PanelHost.Fade(island.Compositor, 0, PanelHost.FadeOutMs));
        PanelHost.AfterFadeOut(island, Dispose).Start();
    }
}
