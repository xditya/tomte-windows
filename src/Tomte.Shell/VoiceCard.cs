using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Rambler;
using Tomte.Core.Text;
using Windows.UI.Composition;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>
/// The rambler's pill grown into a card once the words are text: what was heard, up to three lines beside the tomte,
/// then Insert, Ask Tomte and Add to note in a row under it, each with the key that picks it, the highlighted one with
/// Enter. A line under the row says why Ask is off, or why Insert could not type, which turns Insert into Copy (Alt+I)
/// and leaves nothing highlighted, since Enter then goes back to the app in front. The buttons are the interaction
/// host's targets, so a click picks too, and the island still never takes the keys.
/// </summary>
internal sealed class VoiceCard : IInteractive, IDisposable
{
    public const float Width = 440;
    private const float Corner = 20, Top = 11, Right = 14, Gap = 10, LineGap = 6, LineHeight = 16, Bottom = 10, KeyGap = 6;
    private const uint DefaultKey = 0x8C000000;

    private static readonly VoiceChoice[] Order = [VoiceChoice.Insert, VoiceChoice.Ask, VoiceChoice.Note];

    /// <summary>The tomte beside the words: centred on their first line, as far in from the left edge as they end from the right.</summary>
    public static readonly MascotSeat Seat = new(Top + (PaletteLayout.AnswerText.LineHeight / 2), Right);

    private readonly IslandWindow island;
    private readonly string? askOff;
    private readonly ContainerVisual root;
    private readonly TextVisual words, line;
    private readonly List<Button> buttons = [];
    private readonly float left, rowY;
    private readonly VoiceChoice highlighted;
    private string? refused;

    public VoiceCard(IslandWindow island, TextRenderer text, string transcript, VoiceChoice highlighted, string? askOff)
    {
        this.island = island;
        this.highlighted = highlighted;
        this.askOff = askOff;
        var c = island.Compositor;
        root = c.CreateContainerVisual();
        root.Offset = new Vector3(-Width / 2, 0, 0);
        root.Opacity = 0;
        island.Dips.Children.InsertAtTop(root);
        // The words and the row start after the tomte, which they sit beside; with it off, where the pill's line did.
        left = island.Mascot.Enabled ? Seat.After(CompactLayout.Gap) : CompactLayout.EndInset;
        words = text.Create(root);
        words.Show(new TextRun(transcript, PaletteLayout.AnswerText, Width - left - Right, PaletteLayout.AskLines), new Vector2(left, Top));
        rowY = Top + words.Size.Y + Gap;
        foreach (var _ in Order)
        {
            var layer = c.CreateContainerVisual();
            root.Children.InsertAtTop(layer);
            var shapes = c.CreateShapeVisual();
            shapes.Size = new Vector2(Width, Depth);
            layer.Children.InsertAtTop(shapes);
            var pill = c.CreateRoundedRectangleGeometry();
            pill.CornerRadius = new Vector2(PaletteLayout.ButtonCorner);
            var shape = c.CreateSpriteShape(pill);
            shape.FillBrush = c.CreateColorBrush(Argb(PaletteLayout.ButtonFill));
            shape.StrokeBrush = c.CreateColorBrush(Argb(PaletteLayout.ButtonOutline));
            shapes.Shapes.Add(shape);
            buttons.Add(new Button(layer, pill, shape, text.Create(layer), text.Create(layer)));
        }

        line = text.Create(root);
        Draw();
        var fade = PanelHost.Fade(c, 1, PanelHost.FadeInMs);
        fade.DelayTime = TimeSpan.FromMilliseconds(100);
        root.StartAnimation("Opacity", fade);
        var corner = new Vector2(root.Offset.X, root.Offset.Y);
        island.Interaction.Register(this, p => p - corner, p => p + corner);
    }

    /// <summary>A button was clicked: Insert (Copy once Insert was refused), Ask or Add to note.</summary>
    public event Action<VoiceChoice>? Picked;

    public event Action? TargetsChanged;

    public IReadOnlyList<Target> Targets { get; private set; } = [];

    /// <summary>The choice Enter picks while Insert has not been refused.</summary>
    public VoiceChoice Highlighted => highlighted;

    /// <summary>Insert could not type: Copy is in its place, and Enter picks nothing.</summary>
    public bool Refused => refused is not null;

    /// <summary>A provider could be used when the card opened, so Ask is on.</summary>
    public bool CanAsk => askOff is null;

    /// <summary>The island's shape around the card.</summary>
    public IslandGeometry Shape => new(Width, Depth, Corner);

    private float Depth => rowY + PaletteLayout.ButtonHeight + LineGap + LineHeight + Bottom;

    /// <summary>Insert could not type, for this reason: Copy takes its place, and nothing is highlighted.</summary>
    public void Refuse(string why)
    {
        refused = why;
        Draw();
    }

    public void Activate(string id)
    {
        if (Targets.FirstOrDefault(t => t.Id == id) is { Enabled: true })
            Picked?.Invoke(Enum.Parse<VoiceChoice>(id));
    }

    /// <summary>The card goes with the pill: no more clicks, and its content fades while the shape retracts. Dispose it after.</summary>
    public void FadeOut()
    {
        island.Interaction.Unregister(this);
        root.StartAnimation("Opacity", PanelHost.Fade(island.Compositor, 0, PanelHost.FadeOutMs));
    }

    public void Dispose()
    {
        island.Interaction.Unregister(this);
        island.Dips.Children.Remove(root);
        words.Dispose();
        line.Dispose();
        foreach (var b in buttons)
        {
            b.Label.Dispose();
            b.Key.Dispose();
        }

        root.Dispose();
    }

    private void Draw()
    {
        float width = (Width - left - Right - (Order.Length - 1) * PaletteLayout.ButtonGap) / Order.Length;
        var targets = new List<Target>();
        for (int i = 0; i < Order.Length; i++)
        {
            var choice = Order[i];
            var b = buttons[i];
            bool on = refused is null && choice == highlighted, enabled = choice != VoiceChoice.Ask || askOff is null;
            var box = new RectangleF(left + i * (width + PaletteLayout.ButtonGap), rowY, width, PaletteLayout.ButtonHeight);
            b.Pill.Offset = new Vector2(box.X, box.Y);
            b.Pill.Size = new Vector2(box.Width, box.Height);
            // Filled white as the palette's Run is, the others filled and outlined as an off chip is.
            ((CompositionColorBrush)b.Shape.FillBrush).Color = Argb(on ? PaletteLayout.RunFill : PaletteLayout.ButtonFill);
            b.Shape.StrokeThickness = on ? 0 : 1;
            b.Layer.Opacity = enabled ? 1 : StateLook.DisabledOpacity;
            string label = choice == VoiceChoice.Insert && refused is not null ? "Copy" : VoiceChoices.Label(choice);
            string key = on ? "Enter" : VoiceChoices.Key(choice);
            var labelStyle = (on ? PaletteLayout.RunButton : PaletteLayout.Button) with { Align = TextAlign.Leading };
            var keyStyle = on ? TextStyle.Small with { Color = DefaultKey } : TextStyle.Small;
            // The word and its key centred together in the button.
            float lw = TextSlot.Measure(label, labelStyle, box.Width), kw = TextSlot.Measure(key, keyStyle, box.Width);
            float x = box.X + Math.Max(0, (box.Width - lw - KeyGap - kw) / 2);
            b.Label.Show(label, labelStyle, new RectangleF(x, box.Y, lw + 1, box.Height));
            b.Key.Show(key, keyStyle, new RectangleF(x + lw + KeyGap, box.Y, kw + 1, box.Height));
            targets.Add(new Target(choice.ToString(), box, PaletteLayout.ButtonCorner, TargetKind.Button, enabled, enabled ? null : askOff));
        }

        string say = refused ?? (askOff is null ? "" : askOff + " ") + "Esc throws the words away.";
        line.Show(say, PaletteLayout.AskLine, new RectangleF(left, rowY + PaletteLayout.ButtonHeight + LineGap, Width - left - Right, LineHeight));
        Targets = targets;
        TargetsChanged?.Invoke();
    }

    private static Color Argb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);

    private sealed record Button(ContainerVisual Layer, CompositionRoundedRectangleGeometry Pill, CompositionSpriteShape Shape, TextVisual Label, TextVisual Key);
}
