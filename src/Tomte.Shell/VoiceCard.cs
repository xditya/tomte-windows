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
/// The rambler's pill grown into a card once the words are text (<see cref="VoiceCardLayout"/>): a header with how long
/// the take was and where it was turned into text, the take's waveform, the words, then Insert, Ask Tomte and Add to note,
/// the highlighted one in the accent with Enter. A line under the row says why Ask is off, or why Insert could not type,
/// which turns Insert into Copy (Alt+I) and leaves nothing highlighted, since Enter then goes back to the app in front.
/// The buttons are the interaction host's targets, so a click picks too, and the island takes no keys until the words
/// are edited: E, or a click on them, puts them in a text box that takes the keys, where Enter picks the highlighted
/// choice with the words as edited and Esc throws them away.
/// </summary>
internal sealed class VoiceCard : IInteractive, IDisposable
{
    internal const string WordsId = "words";

    private static readonly VoiceChoice[] Order = [VoiceChoice.Insert, VoiceChoice.Ask, VoiceChoice.Note];

    /// <summary>The tomte beside the header.</summary>
    public static readonly MascotSeat Seat = VoiceCardLayout.Seat;

    private readonly IslandWindow island;
    private readonly TextRenderer text;
    private readonly TypingFocus? focus;
    private readonly string? askOff, target;
    private readonly float width;
    private readonly ContainerVisual root;
    private readonly ShapeVisual shapes;
    private readonly CompositionContainerShape bars;
    private readonly TextVisual words, heard, length, where, reason;
    private readonly Keycaps keys;
    private readonly List<Button> buttons = [];
    private readonly VoiceChoice highlighted;
    private readonly (float Height, bool Silent)[] take;
    private readonly TimeSpan took;
    private readonly string privacy, hold;
    private EditorView? editor;
    private string said;
    private string? refused;
    private VoiceCardLayout layout;

    public VoiceCard(IslandWindow island, TextRenderer text, TypingFocus? focus, string transcript, VoiceChoice highlighted, string? askOff, VoiceTake shown)
    {
        this.island = island;
        this.text = text;
        this.focus = focus;
        this.highlighted = highlighted;
        this.askOff = askOff;
        said = transcript;
        (take, took, privacy, target, hold) = (shown.Bars, shown.Length, shown.Privacy, shown.Target, shown.HoldKeys.Replace('+', ' '));
        width = (float)Math.Min(PanelSize.VoiceWidth, 0.6 * island.MonitorSize.Width);
        var c = island.Compositor;
        root = c.CreateContainerVisual();
        root.Offset = new Vector3(-width / 2, 0, 0);
        root.Opacity = 0;
        island.Dips.Children.InsertAtTop(root);
        shapes = c.CreateShapeVisual();
        shapes.Size = new Vector2(width, (float)PanelSize.PaletteMaxHeight);
        root.Children.InsertAtTop(shapes);
        layout = Lay();

        // The header: the mic in its disc, the lock before where the words were made.
        var mic = layout.Mic;
        var disc = c.CreateEllipseGeometry();
        disc.Center = new Vector2(mic.X + (mic.Width / 2), mic.Y + (mic.Height / 2));
        disc.Radius = new Vector2(mic.Width / 2);
        var discShape = c.CreateSpriteShape(disc);
        discShape.FillBrush = c.CreateColorBrush(Argb(Theme.Raised));
        shapes.Shapes.Add(discShape);
        Glyph(Glyphs.Mic, Theme.Ink, VoiceCardLayout.MicSize, new Vector2(mic.X + ((mic.Width - VoiceCardLayout.MicSize) / 2), mic.Y + ((mic.Height - VoiceCardLayout.MicSize) / 2)));
        var (lockAt, line) = layout.Where(privacy);
        Glyph(Glyphs.Lock, Theme.Ink3, VoiceCardLayout.LockSize, new Vector2(lockAt.X, lockAt.Y));
        heard = text.Create(root);
        length = text.Create(root);
        where = text.Create(root);
        var (heardLine, lengthLine) = layout.Header(took);
        heard.Show(heardLine);
        length.Show(lengthLine);
        where.Show(line);

        bars = c.CreateContainerShape();
        shapes.Shapes.Add(bars);
        DrawTake();
        words = text.Create(root);
        foreach (var _ in Order)
        {
            var layer = c.CreateContainerVisual();
            root.Children.InsertAtTop(layer);
            var buttonShapes = c.CreateShapeVisual();
            buttonShapes.Size = shapes.Size;
            layer.Children.InsertAtTop(buttonShapes);
            var pill = c.CreateRoundedRectangleGeometry();
            pill.CornerRadius = new Vector2(VoiceCardLayout.ActionCorner);
            var fill = c.CreateColorBrush();
            var shape = c.CreateSpriteShape(pill);
            shape.FillBrush = fill;
            buttonShapes.Shapes.Add(shape);
            var cap = c.CreateRoundedRectangleGeometry();
            cap.CornerRadius = new Vector2(Keycap.Corner);
            var capFill = c.CreateColorBrush();
            var capShape = c.CreateSpriteShape(cap);
            capShape.FillBrush = capFill;
            buttonShapes.Shapes.Add(capShape);
            var glyph = c.CreateContainerShape();
            buttonShapes.Shapes.Add(glyph);
            buttons.Add(new Button(layer, pill, fill, cap, capFill, glyph, c.CreateColorBrush(), text.Create(layer), text.Create(layer), text.Create(layer)));
        }

        reason = text.Create(root);
        keys = new Keycaps(c, text, root, shapes.Size);
        Draw();
        var fade = PanelHost.Fade(c, 1, PanelHost.FadeInMs);
        fade.DelayTime = TimeSpan.FromMilliseconds(100);
        root.StartAnimation("Opacity", fade);
        var corner = new Vector2(root.Offset.X, root.Offset.Y);
        island.Interaction.Register(this, p => p - corner, p => p + corner);
    }

    /// <summary>A button was clicked: Insert (Copy once Insert was refused), Ask or Add to note.</summary>
    public event Action<VoiceChoice>? Picked;

    /// <summary>Enter in the edited words: the highlighted choice, with the words as they are now.</summary>
    public event Action? Entered;

    /// <summary>Esc in the edited words: they are thrown away, as Esc on the card does.</summary>
    public event Action? Discarded;

    /// <summary>The words became editable and the island took the keys; the card's own keys are the text box's now.</summary>
    public event Action? Editing;

    /// <summary>The card's height changed with the edited words; the island morphs to <see cref="Shape"/>.</summary>
    public event Action? Resized;

    public event Action? TargetsChanged;

    public IReadOnlyList<Target> Targets { get; private set; } = [];

    /// <summary>The choice Enter picks while Insert has not been refused.</summary>
    public VoiceChoice Highlighted => highlighted;

    /// <summary>Insert could not type: Copy is in its place, and Enter picks nothing.</summary>
    public bool Refused => refused is not null;

    /// <summary>A provider could be used when the card opened, so Ask is on.</summary>
    public bool CanAsk => askOff is null;

    /// <summary>The words as the choice takes them: as heard, or as edited.</summary>
    public string Words => editor?.Services.Text.Trim() is { Length: > 0 } edited ? edited : said;

    /// <summary>The words are in the text box, which has the keys or had them.</summary>
    public bool IsEditing => editor is not null;

    /// <summary>The words can be edited here: a text box can take the keys.</summary>
    public bool CanEdit => focus is not null;

    /// <summary>The island's shape around the card.</summary>
    public IslandGeometry Shape => new(width, layout.Height, VoiceCardLayout.Corner);

    /// <summary>Insert could not type, for this reason: Copy takes its place, and nothing is highlighted.</summary>
    public void Refuse(string why)
    {
        refused = why;
        Draw();
    }

    public void Activate(string id)
    {
        if (id == WordsId)
        {
            Edit();
        }
        else if (Targets.FirstOrDefault(t => t.Id == id) is { Enabled: true })
        {
            Picked?.Invoke(Enum.Parse<VoiceChoice>(id));
        }
    }

    /// <summary>
    /// The words go into a text box that takes the keys, with the caret at their end, so they can be put right before
    /// they are typed, asked or saved. False when there is no box to take them or Windows refused the keys.
    /// </summary>
    public bool Edit()
    {
        if (focus is null)
        {
            return false;
        }

        if (editor is null)
        {
            editor = focus.Create(root, multiline: true, VoiceCardLayout.Words.Size, weight: VoiceCardLayout.Words.Weight);
            editor.CaretInk = Theme.Accent;
            editor.Services.Text = said;
            editor.Services.Selection = (said.Length, said.Length);
            editor.SendOnEnter(_ => Entered?.Invoke());
            editor.Escape = () =>
            {
                Discarded?.Invoke();
                return true;
            };
            // Read on the next turn: nothing may call back into the services inside their own notification.
            editor.Services.Resized += () => island.Queue.TryEnqueue(Relayout);
            words.Show(new TextRun("", VoiceCardLayout.Words, 1), default);
        }

        if (!focus.Take(editor))
        {
            return false;
        }

        Relayout();
        Editing?.Invoke();
        return true;
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
        editor?.Dispose();
        island.Dips.Children.Remove(root);
        foreach (var visual in new[] { words, heard, length, where, reason }.Concat(buttons.SelectMany(b => new[] { b.Label, b.Into, b.Key })))
        {
            visual.Dispose();
        }

        keys.Dispose();
        root.Dispose();
    }

    private static Color Argb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);

    private VoiceCardLayout Lay()
    {
        float inner = width - (2 * VoiceCardLayout.Side);
        float height;
        if (editor is not null)
        {
            height = Math.Max(editor.ContentHeight, VoiceCardLayout.Words.LineHeight);
        }
        else
        {
            using var laid = TextLayout.Of(new TextRun(said, VoiceCardLayout.Words, inner, VoiceCardLayout.WordLines));
            height = laid.Height;
        }

        string line = refused ?? askOff ?? "";
        return new VoiceCardLayout(width, island.Mascot.Enabled, height, line);
    }

    private void Relayout()
    {
        float was = layout.Height;
        layout = Lay();
        Draw();
        if (layout.Height != was)
        {
            Resized?.Invoke();
        }
    }

    private void Glyph(GlyphFigure[] figures, uint ink, float size, Vector2 at)
    {
        var shape = island.Compositor.CreateContainerShape();
        shape.Offset = at;
        PaletteView.DrawGlyph(shape, figures, island.Compositor.CreateColorBrush(Argb(ink)), size);
        shapes.Shapes.Add(shape);
    }

    // The take as bars, centred down the waveform's band; the silence that ends it flat and dim.
    private void DrawTake()
    {
        var c = island.Compositor;
        var band = layout.Wave;
        CompositionColorBrush loud = c.CreateColorBrush(Argb(Theme.Ink)), quiet = c.CreateColorBrush(Argb(Theme.Silence));
        for (int i = 0; i < take.Length; i++)
        {
            var (h, silent) = take[i];
            var bar = c.CreateRoundedRectangleGeometry();
            bar.Size = new Vector2(Waveform.TakeBar, h);
            bar.CornerRadius = new Vector2(Waveform.TakeBar / 2);
            bar.Offset = new Vector2(band.X + (i * (Waveform.TakeBar + Waveform.TakeGap)), band.Y + ((band.Height - h) / 2));
            var shape = c.CreateSpriteShape(bar);
            shape.FillBrush = silent ? quiet : loud;
            bars.Shapes.Add(shape);
        }
    }

    private void Draw()
    {
        var box = layout.Text;
        if (editor is null)
        {
            words.Show(new TextRun(said, VoiceCardLayout.Words, box.Width, VoiceCardLayout.WordLines), new Vector2(box.X, box.Y));
        }
        else
        {
            editor.Place(box, island.Scale);
        }

        var targets = new List<Target>();
        var at = layout.Actions;
        for (int i = 0; i < Order.Length; i++)
        {
            var choice = Order[i];
            var b = buttons[i];
            var r = at[i];
            bool on = refused is null && choice == highlighted, enabled = choice != VoiceChoice.Ask || askOff is null;
            b.Pill.Offset = new Vector2(r.X, r.Y);
            b.Pill.Size = new Vector2(r.Width, r.Height);
            b.Fill.Color = Argb(on ? Theme.Accent : Theme.Raised);
            b.Layer.Opacity = enabled ? 1 : StateLook.DisabledOpacity;
            uint ink = on ? Theme.OnAccent : Theme.Ink;
            string label = choice == VoiceChoice.Insert && refused is not null ? "Copy" : VoiceChoices.Label(choice);
            float x = r.X + VoiceCardLayout.ActionPadX, right = r.Right - VoiceCardLayout.ActionPadX;

            // The highlighted choice carries Enter at its right end.
            string cap = on ? "Enter" : "";
            var capBox = on ? Keycap.EndingAt(cap, right, r) : RectangleF.Empty;
            b.Cap.Offset = new Vector2(capBox.X, capBox.Y);
            b.Cap.Size = new Vector2(capBox.Width, capBox.Height);
            b.CapFill.Color = Argb(VoiceCardLayout.PrimaryKeyFill);
            b.Key.Show(cap, VoiceCardLayout.PrimaryKey, capBox);
            if (on)
            {
                right = capBox.X - VoiceCardLayout.GlyphGap;
            }

            // Ask Tomte and Add to note lead with their glyph; Insert says where it types when that is known.
            var glyph = choice switch
            {
                VoiceChoice.Ask => Glyphs.Sparkle,
                VoiceChoice.Note => Glyphs.Pencil,
                _ => null,
            };
            b.Glyph.Shapes.Clear();
            if (glyph is not null)
            {
                b.GlyphInk.Color = Argb(choice == VoiceChoice.Ask && !on ? Theme.Accent : ink);
                PaletteView.DrawGlyph(b.Glyph, glyph, b.GlyphInk, VoiceCardLayout.GlyphSize);
                b.Glyph.Offset = new Vector2(x, r.Y + ((r.Height - VoiceCardLayout.GlyphSize) / 2));
                x += VoiceCardLayout.GlyphSize + VoiceCardLayout.GlyphGap;
            }

            var style = on ? VoiceCardLayout.Primary : VoiceCardLayout.Action;
            bool into = choice == VoiceChoice.Insert && refused is null && target is not null;
            if (into)
            {
                // Two lines: Insert, and where to, centred down the button together.
                const float Top = 19, Bottom = 15;
                float y = r.Y + ((r.Height - Top - Bottom) / 2);
                b.Label.Show(label, style, new RectangleF(x, y, Math.Max(0, right - x), Top));
                b.Into.Show($"into {target}", on ? VoiceCardLayout.Into : TextStyle.Small, new RectangleF(x, y + Top, Math.Max(0, right - x), Bottom));
            }
            else
            {
                b.Label.Show(label, style, new RectangleF(x, r.Y, Math.Max(0, right - x), r.Height));
                b.Into.Show("", TextStyle.Small, RectangleF.Empty);
            }

            targets.Add(new Target(choice.ToString(), r, VoiceCardLayout.ActionCorner, TargetKind.Button, enabled, enabled ? null : askOff, Key: VoiceChoices.Key(choice)));
        }

        // A click on the words edits them.
        if (focus is not null && editor is null)
        {
            targets.Add(new Target(WordsId, box, 6, TargetKind.Button, Tip: "Edit the words", Key: "E"));
        }

        reason.Show(layout.ReasonLine with { Style = refused is null ? VoiceCardLayout.Reason : VoiceCardLayout.Reason with { Color = Theme.Warn } });
        // The hold chord again at the left, E and Esc at the right; while the words are edited, Enter and Esc.
        keys.Begin();
        (string, string)[] end = editor is null && focus is not null ? [("E", "edit words"), ("Esc", "discard")] : [("Esc", "discard")];
        (string, string)? first = editor is null ? (hold, "hold to talk again") : refused is null ? ("Enter", VoiceChoices.Label(highlighted).ToLowerInvariant()) : null;
        foreach (var (capAt, capKeys, word) in layout.Footer(first, end))
        {
            keys.Add(capAt, capKeys, word);
        }

        keys.End();
        Targets = targets;
        TargetsChanged?.Invoke();
    }

    private sealed record Button(ContainerVisual Layer, CompositionRoundedRectangleGeometry Pill, CompositionColorBrush Fill, CompositionRoundedRectangleGeometry Cap, CompositionColorBrush CapFill, CompositionContainerShape Glyph, CompositionColorBrush GlyphInk, TextVisual Label, TextVisual Into, TextVisual Key);
}

/// <summary>
/// What the card says about the take besides its words: its bars and length, where it was turned into text, the name of
/// what Insert types into when it is known, and the hold chord as settings has it.
/// </summary>
internal sealed record VoiceTake((float Height, bool Silent)[] Bars, TimeSpan Length, string Privacy, string? Target, string HoldKeys);
