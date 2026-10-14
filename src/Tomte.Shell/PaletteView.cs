using System.Drawing;
using System.Numerics;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Device;
using Tomte.Core.Interaction;
using Tomte.Core.Text;
using Windows.System;
using Windows.UI.Composition;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>
/// The palette's content on the island, laid out by <see cref="PaletteLayout"/> in a container in island space: the
/// prompt box, a text box with Send at its right end when a session can send, what was said when the palette has no
/// box (voice), and the "AI layer is off" line when nothing can send; "Include" and the chips that have something to
/// add, each a toggle; the answer as it streams, drawn by the text renderer in a band that scrolls with the wheel and
/// follows the newest line until the user scrolls up; a tool's question with Run, Cancel and, where the tier allows
/// it, Always allow; and the footer with where the request goes and what today cost. It fades in with the shape and
/// goes when the island leaves the Palette state. The island's interaction host runs its chips and buttons: hover,
/// press, the cursor, tooltips and the keys. While a request is sending the chips and Send are disabled; Run is
/// disabled, and drawn dim, until a yes would count.
/// The answer is plain text runs, not a read-only text box: nothing asks for selecting or copying it, and a third
/// set of text services would cost a module instance, input routing and a surface for that alone.
/// </summary>
internal sealed class PaletteView : IDisposable, IInteractive
{
    private const string ChipId = "chip:", SendId = "send";
    private static readonly Color Card = Color.FromArgb(15, 255, 255, 255), Clear = default;

    private readonly IslandWindow island;
    private readonly IslandStateMachine machine;
    private readonly TextRenderer text;
    private readonly TypingFocus? focus;
    private readonly IPaletteSession? session;
    private readonly float width;
    private readonly Action<string>? send;
    private PaletteLayout layout;
    private float laidText = -1;
    private List<ChipSpec> laidChips = [];
    private List<ContextChip> shownChips = [];
    private IReadOnlyList<Target> targets = [];
    private readonly ContainerVisual root, chipLayer;
    private readonly ShapeVisual shapes, chipShapes;
    private readonly CompositionRoundedRectangleGeometry card;
    private readonly CompositionColorBrush cardLine;
    private readonly List<ChipSlot> slots = [];
    private readonly List<TextVisual> runs = [];
    private readonly TextVisual placeholder, provider, spend, include;
    private readonly ShapeVisual? sendVisual;
    private readonly CompositionRoundedRectangleGeometry? sendButton;
    private readonly CompositionContainerShape? sendArrow;
    private readonly ContainerVisual band, scrolled;
    private readonly EditorView? prompt;
    private readonly ToolQuestion? question;
    private readonly bool typed;
    private readonly string hint;
    private TextVisual? setup;
    private float answerHeight, scroll;
    private bool pinned = true;
    private Action? retract;
    private bool fadeInPending = true, disposed;
    private ToolRequest? laidAsk;
    private ConfirmKeys laidKeys;
    private Confirmation? confirm;
    private (string Text, float Height) said = ("", 0);
    private CompositionRoundedRectangleGeometry? askCard;
    private TextVisual? askSummary, askLine;
    private readonly Dictionary<ConfirmButton, AskButton> buttons = [];

    // The question whose read guard the yes buttons show, and the one a yes counts for once it has run out.
    private ToolRequest? guarded, readable;
    private DispatcherQueueTimer? readTimer;

    /// <param name="send">What Enter does with the prompt's text; null for a palette that only shows (voice, a driven run).</param>
    /// <param name="question">The session's open question, drawn with its buttons; null where nobody can answer (a driven run).</param>
    public PaletteView(IslandWindow island, IslandStateMachine machine, TextRenderer text, TypingFocus? focus, IPaletteSession? session, float width, Action<string>? send, ToolQuestion? question = null)
    {
        this.island = island;
        this.machine = machine;
        this.text = text;
        this.focus = focus;
        this.session = session;
        this.width = width;
        this.question = question;
        this.send = send;
        typed = send is not null;
        hint = PaletteLayout.HintFor(session?.Commands.Select(c => c.Skill) ?? []);
        layout = new PaletteLayout(width, island.Mascot.Enabled);
        var c = island.Compositor;
        root = c.CreateContainerVisual();
        root.Offset = new Vector3(-width / 2, 0, 0);
        root.Opacity = 0;
        shapes = Shapes(root);
        card = c.CreateRoundedRectangleGeometry();
        card.CornerRadius = new Vector2(PaletteLayout.PromptCorner);
        var cardShape = c.CreateSpriteShape(card);
        cardShape.FillBrush = c.CreateColorBrush(Card);
        cardShape.StrokeBrush = cardLine = c.CreateColorBrush(Argb(PaletteLayout.PromptLine));
        cardShape.StrokeThickness = 1;
        shapes.Shapes.Add(cardShape);
        // The chips and "Include" in one layer, which dims as one while a request is sending.
        chipLayer = c.CreateContainerVisual();
        root.Children.InsertAtTop(chipLayer);
        chipShapes = Shapes(chipLayer);
        include = text.Create(chipLayer);
        // The answer's runs sit in one container clipped to the answer's band and moved by the scroll.
        band = c.CreateContainerVisual();
        band.Clip = c.CreateInsetClip();
        scrolled = c.CreateContainerVisual();
        band.Children.InsertAtTop(scrolled);
        root.Children.InsertAtTop(band);
        placeholder = text.Create(root);
        provider = text.Create(root);
        spend = text.Create(root);
        if (session is not null && focus is not null)
        {
            prompt = focus.Create(root, multiline: true, PaletteLayout.PromptSize);
            prompt.Services.Text = session.Prompt;
            if (send is not null)
            {
                // While a question is open Enter runs what it asks; otherwise Enter sends.
                prompt.SendOnEnter(entered =>
                {
                    if (!Answer(ConfirmButton.Run))
                    {
                        send(entered);
                    }
                });
            }

            // Esc cancels an open question and leaves the palette up; with none it hides the island as before.
            prompt.Escape = () => Answer(ConfirmButton.Cancel);

            // Inside the services' own notifications nothing may call back into them: the hint and Send's state only,
            // and a relayout later.
            prompt.Services.Changed += Typed;
            prompt.Services.Resized += Resize;
            if (typed)
            {
                // Send: a white button in a visual of its own, which dims as one while there is nothing to send.
                sendVisual = Shapes(root);
                sendButton = c.CreateRoundedRectangleGeometry();
                sendButton.CornerRadius = new Vector2(PaletteLayout.SendCorner);
                var button = c.CreateSpriteShape(sendButton);
                button.FillBrush = c.CreateColorBrush(Argb(PaletteLayout.SendFill));
                sendVisual.Shapes.Add(button);
                sendArrow = c.CreateContainerShape();
                Draw(sendArrow, PaletteGlyphs.Send, c.CreateColorBrush(Argb(PaletteLayout.SendInk)));
                sendVisual.Shapes.Add(sendArrow);
            }
        }

        island.Dips.Children.InsertAtTop(root);
        island.ContentReady += FadeIn;
        island.Pressed += OnPressed;
        island.Wheel += OnWheel;
        machine.Changed += OnState;
        if (session is not null)
        {
            session.Changed += Update;
        }

        if (question is not null)
        {
            question.Changed += Update;
        }

        if (prompt is not null)
        {
            // An open question's line drops "Enter runs it" once the box no longer has the keys.
            focus!.Lost += Update;
        }

        Update();
    }

    /// <summary>The prompt box's text, set for the user to read before Enter sends it (a skill's command).</summary>
    public void Fill(string prompted)
    {
        if (prompt is not null && !disposed)
        {
            prompt.Services.Text = prompted;
            // The caret after it, where the user would go on typing.
            prompt.Services.Selection = (prompted.Length, prompted.Length);
        }
    }

    /// <summary>The palette's height for what it shows now, in DIPs.</summary>
    public float Height { get; private set; }

    /// <summary>The height changed with the prompt's lines or the answer; the palette morphs to it, gently.</summary>
    public event Action? HeightChanged;

    /// <summary>The prompt takes the keys, after the palette hotkey or a click on the tomte. False when there is no prompt or Windows refused.</summary>
    public bool TakeKeys()
    {
        bool took = !disposed && prompt is not null && focus!.Take(prompt);
        // The card's outline shows that the box has the keys.
        Update();
        return took;
    }

    public IReadOnlyList<Target> Targets => targets;

    public event Action? TargetsChanged;

    /// <summary>A chip turns on or off, Send sends, or a question's button answers it; nothing while that target is disabled.</summary>
    public void Activate(string id)
    {
        if (disposed || session is null || machine.State != IslandState.Palette || targets.FirstOrDefault(t => t.Id == id) is not { Enabled: true })
        {
            return;
        }

        if (id == SendId)
        {
            send!(prompt!.Services.Text);
        }
        else if (shownChips.FirstOrDefault(chip => ChipId + chip.Label == id) is { } chip)
        {
            chip.On = !chip.On;
        }
        else
        {
            Answer(Enum.Parse<ConfirmButton>(id));
        }

        Update();
    }

    /// <summary>The question the card shows now, which a yes from the chord may answer; null with none.</summary>
    public ToolRequest? Shown => laidAsk;

    /// <summary>The palette goes when it loses the keys while nothing is being sent.</summary>
    public void RetractOnFocusLoss(Action hide)
    {
        if (focus is null)
        {
            return;
        }

        retract = () =>
        {
            if (machine.State == IslandState.Palette && session?.Status != PaletteStatus.Sending)
            {
                hide();
            }
        };
        focus.Lost += retract;
    }

    public void Update()
    {
        if (disposed)
        {
            return;
        }

        // The chips are measured again only when the prompt's height, a chip or the question changed, not for every streamed word.
        float promptText = prompt?.ContentHeight ?? Said();
        // A chip with nothing to add (no selection, an empty shelf, a window that could not be read) is not shown.
        shownChips = session is null ? [] : [.. session.Chips.Where(chip => chip.Available)];
        List<ChipSpec> chips = [.. shownChips.Select(chip => chip.Spec)];
        var asked = question?.Asked;
        // Enter answers only while the prompt box has the keys; the line says so, and the card's outline shows it.
        bool focused = prompt is not null && focus?.Focused == prompt;
        var keys = !typed ? ConfirmKeys.Voice : focused ? ConfirmKeys.Prompt : ConfirmKeys.Pointer;
        if (promptText != laidText || !chips.SequenceEqual(laidChips) || !ReferenceEquals(asked, laidAsk) || keys != laidKeys)
        {
            (laidText, laidChips, laidAsk, laidKeys) = (promptText, chips, asked, keys);
            confirm = asked is null ? null : new Confirmation(asked.Summary, Confirmation.LineFor(asked.Tier, keys), asked.OfferAlwaysAllow, asked.Tier == ActionTier.Destructive);
            layout = new PaletteLayout(width, island.Mascot.Enabled, promptText, chips, confirm, send: sendVisual is not null);
        }

        var box = layout.Prompt;
        card.Offset = new Vector2(box.X, box.Y);
        card.Size = new Vector2(box.Width, box.Height);
        cardLine.Color = Argb(focused ? PaletteLayout.PromptFocusLine : PaletteLayout.PromptLine);
        prompt?.Place(layout.Editor, island.Scale);
        Hint();

        if (session is null)
        {
            // No provider: what the palette needs, under the box, and nothing to type in.
            setup ??= text.Create(root);
            setup.Show(new TextRun(PaletteLayout.Setup, TextStyle.Secondary, layout.AnswerWidth), new Vector2(PaletteLayout.Pad, layout.ChipsTop + 4));
            SetHeight(layout.Height(0));
            return;
        }

        ShowChips();

        // every streamed word lays the answer out again (and its run once more to draw it); cache the blocks
        // that did not change if long answers cost frames.
        var (answer, measured) = layout.Answer(Blocks(session));
        float height = layout.Height(measured);
        var area = layout.AnswerBand(height);
        band.Offset = new Vector3(area.X, area.Y, 0);
        band.Size = new Vector2(area.Width, area.Height);
        // A new answer starts at the top and follows its newest line again.
        pinned |= measured == 0;
        answerHeight = measured;
        scroll = layout.Follow(scroll, pinned, measured, height);
        scrolled.Offset = new Vector3(0, -scroll, 0);
        for (int i = 0; i < answer.Count; i++)
        {
            Show(i, answer[i].Run, answer[i].At);
        }

        Trim(answer.Count);
        var foot = layout.Foot(session.ProviderIndicator, session.ToolSteps, session.SpentTodayUsd, session.DailyCapUsd, height);
        provider.Show(foot[0]);
        spend.Show(foot[1]);
        ShowAsk(height);
        SetHeight(height);
        Retarget();
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        island.Interaction.Unregister(this);
        readTimer?.Stop();
        island.ContentReady -= FadeIn;
        island.Pressed -= OnPressed;
        island.Wheel -= OnWheel;
        machine.Changed -= OnState;
        if (session is not null)
        {
            session.Changed -= Update;
        }

        if (question is not null)
        {
            question.Changed -= Update;
        }

        if (prompt is not null)
        {
            focus!.Lost -= Update;
        }

        if (retract is not null)
        {
            focus!.Lost -= retract;
        }

        prompt?.Dispose();
        foreach (var visual in slots.Select(s => s.Label).Concat(runs).Concat(buttons.Values.Select(b => b.Label)).Append(placeholder).Append(provider).Append(spend).Append(include))
        {
            visual.Dispose();
        }

        setup?.Dispose();
        askSummary?.Dispose();
        askLine?.Dispose();
        foreach (var button in buttons.Values)
        {
            button.Layer.Dispose();
        }

        scrolled.Dispose();
        band.Dispose();
        island.Dips.Children.Remove(root);
        chipShapes.Dispose();
        chipLayer.Dispose();
        sendVisual?.Dispose();
        shapes.Dispose();
        root.Dispose();
    }

    private static Color Argb(uint c) => Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);

    // A shape visual as large as the palette can grow, at the top of a container.
    private ShapeVisual Shapes(ContainerVisual host)
    {
        var visual = island.Compositor.CreateShapeVisual();
        visual.Size = new Vector2(width, (float)PanelSize.PaletteMaxHeight);
        host.Children.InsertAtTop(visual);
        return visual;
    }

    // A glyph's figures in a container shape, all in one ink: filled, or stroked along their outlines.
    private void Draw(CompositionContainerShape into, GlyphFigure[] figures, CompositionColorBrush ink)
    {
        into.Shapes.Clear();
        foreach (var figure in figures)
        {
            var shape = island.Compositor.CreateSpriteShape(island.Compositor.CreatePathGeometry(IslandWindow.BuildPath(figure.Path, 1)));
            if (figure.Stroked)
            {
                shape.StrokeBrush = ink;
                shape.StrokeThickness = PaletteGlyphs.Stroke;
            }
            else
            {
                shape.FillBrush = ink;
            }

            into.Shapes.Add(shape);
        }
    }

    // One drawn chip: its pill with a fill and an outline, its picture and mark in one ink, its name, and which picture
    // and mark are drawn now. A slot past the chips shown is drawn empty.
    private sealed class ChipSlot(CompositionRoundedRectangleGeometry pill, CompositionColorBrush fill, CompositionColorBrush outline, CompositionColorBrush ink, CompositionContainerShape icon, CompositionContainerShape mark, TextVisual label)
    {
        public CompositionRoundedRectangleGeometry Pill { get; } = pill;

        public CompositionColorBrush Fill { get; } = fill;

        public CompositionColorBrush Outline { get; } = outline;

        public CompositionColorBrush Ink { get; } = ink;

        public CompositionContainerShape Icon { get; } = icon;

        public CompositionContainerShape Mark { get; } = mark;

        public TextVisual Label { get; } = label;

        public (ChipIcon Icon, bool On)? Drawn { get; set; }
    }

    private ChipSlot Slot()
    {
        var c = island.Compositor;
        var pill = c.CreateRoundedRectangleGeometry();
        pill.CornerRadius = new Vector2(PaletteLayout.ChipCorner);
        var shape = c.CreateSpriteShape(pill);
        var slot = new ChipSlot(pill, c.CreateColorBrush(), c.CreateColorBrush(), c.CreateColorBrush(), c.CreateContainerShape(), c.CreateContainerShape(), text.Create(chipLayer));
        shape.FillBrush = slot.Fill;
        shape.StrokeBrush = slot.Outline;
        shape.StrokeThickness = 1;
        chipShapes.Shapes.Add(shape);
        chipShapes.Shapes.Add(slot.Icon);
        chipShapes.Shapes.Add(slot.Mark);
        return slot;
    }

    // Each chip shown: off, an outline with a "+"; on, the accent with a tick. All of them dim while a request is sending,
    // when a click on one changes nothing.
    private void ShowChips()
    {
        var row = layout.Chips;
        include.Show(layout.IncludeLabel);
        while (slots.Count < row.Count)
        {
            slots.Add(Slot());
        }

        for (int i = 0; i < slots.Count; i++)
        {
            var slot = slots[i];
            if (i >= row.Count)
            {
                slot.Pill.Size = default;
                slot.Outline.Color = Clear;
                slot.Icon.Shapes.Clear();
                slot.Mark.Shapes.Clear();
                slot.Drawn = null;
                slot.Label.Show(new TextSlot("", PaletteLayout.Chip, default));
                continue;
            }

            var chip = row[i];
            slot.Pill.Offset = new Vector2(chip.Box.X, chip.Box.Y);
            slot.Pill.Size = new Vector2(chip.Box.Width, chip.Box.Height);
            slot.Fill.Color = Argb(chip.On ? StateLook.CheckedFill : PaletteLayout.ChipFill);
            slot.Outline.Color = Argb(chip.On ? StateLook.CheckedFill : PaletteLayout.ChipOutline);
            slot.Ink.Color = Argb(chip.On ? StateLook.CheckedText : PaletteLayout.ChipInk);
            var drawn = (laidChips[i].Icon, chip.On);
            if (slot.Drawn != drawn)
            {
                slot.Drawn = drawn;
                Draw(slot.Icon, PaletteGlyphs.Icon(drawn.Icon), slot.Ink);
                Draw(slot.Mark, chip.On ? PaletteGlyphs.Tick : PaletteGlyphs.Plus, slot.Ink);
            }

            slot.Icon.Offset = new Vector2(chip.Icon.X, chip.Icon.Y);
            slot.Mark.Offset = new Vector2(chip.Mark.X, chip.Mark.Y);
            slot.Label.Show(chip.Label);
        }

        chipLayer.Opacity = Sending ? StateLook.DisabledOpacity : 1;
    }

    private bool Sending => session?.Status == PaletteStatus.Sending;

    // The targets the island's interaction host runs, in Tab order: the chips as one stop (the arrows move among them),
    // Send, then each of the question's buttons as a stop of its own, the yes buttons once a yes would count. Raised to
    // the host only when something changed.
    private void Retarget()
    {
        var list = new List<Target>();
        if (session is not null && !disposed)
        {
            bool sending = Sending;
            for (int i = 0; i < layout.Chips.Count; i++)
            {
                var chip = layout.Chips[i];
                list.Add(new Target(ChipId + shownChips[i].Label, chip.Box, PaletteLayout.ChipCorner, TargetKind.Toggle, !sending, shownChips[i].Tip, Group: 1, Checked: chip.On));
            }

            if (sendVisual is not null)
            {
                list.Add(new Target(SendId, layout.Send, PaletteLayout.SendCorner, TargetKind.Button, PaletteLayout.CanSend(prompt!.Services.Text, sending), PaletteLayout.SendTip));
            }

            foreach (var (button, at, _) in layout.Ask(Height)?.Buttons ?? [])
            {
                list.Add(new Target(button.ToString(), at, PaletteLayout.ButtonCorner, TargetKind.Button, button == ConfirmButton.Cancel || ReferenceEquals(readable, laidAsk)));
            }
        }

        if (!list.SequenceEqual(targets))
        {
            targets = list;
            TargetsChanged?.Invoke();
        }
    }

    // A change to the prompt's text: the hint, and whether Send can send it.
    private void Typed()
    {
        Hint();
        Retarget();
    }

    // The hint shows while the prompt is empty, as an input's placeholder does; Send dims while there is nothing to send.
    private void Hint()
    {
        if (disposed)
        {
            return;
        }

        var box = layout.Prompt;
        var editorBox = layout.Editor;
        if (sendVisual is not null)
        {
            var button = layout.Send;
            sendButton!.Offset = new Vector2(button.X, button.Y);
            sendButton.Size = new Vector2(button.Width, button.Height);
            sendArrow!.Offset = new Vector2(button.X + (button.Width - PaletteGlyphs.SendSize) / 2, button.Y + (button.Height - PaletteGlyphs.SendSize) / 2);
            sendVisual.Opacity = PaletteLayout.CanSend(prompt!.Services.Text, Sending) ? 1 : StateLook.DisabledOpacity;
        }

        if (prompt is null && session is not null)
        {
            placeholder.Show(new TextRun(session.Prompt, PaletteLayout.PromptText, editorBox.Width, PaletteLayout.PromptLines), new Vector2(editorBox.X, editorBox.Y));
            return;
        }

        bool empty = prompt is null || prompt.Services.Text.Length == 0;
        placeholder.Show(empty ? (session is null ? PaletteLayout.Off : hint) : "", PaletteLayout.Placeholder, new RectangleF(editorBox.X, box.Y, editorBox.Width, box.Height));
    }

    // A palette with no text box (the voice palette) shows what was said in the box's place, up to four lines; its
    // height is measured once per prompt, not for every streamed word.
    private float Said()
    {
        if (session is null || session.Prompt == said.Text)
        {
            return said.Height;
        }

        using var laid = TextLayout.Of(new TextRun(session.Prompt, PaletteLayout.PromptText, layout.Editor.Width, PaletteLayout.PromptLines));
        said = (session.Prompt, session.Prompt.Length == 0 ? 0 : laid.Height);
        return said.Height;
    }

    // The open question's card, summary, line and buttons, made on the first question; with none they are emptied.
    private void ShowAsk(float height)
    {
        var ask = layout.Ask(height);
        if (ask is null && askCard is null)
        {
            return;
        }

        var c = island.Compositor;
        if (askCard is null)
        {
            askCard = c.CreateRoundedRectangleGeometry();
            askCard.CornerRadius = new Vector2(PaletteLayout.PromptCorner);
            var shape = c.CreateSpriteShape(askCard);
            shape.FillBrush = c.CreateColorBrush(Card);
            shape.StrokeBrush = c.CreateColorBrush(Argb(PaletteLayout.AskOutline));
            shape.StrokeThickness = 1;
            shapes.Shapes.Add(shape);
            askSummary = text.Create(root);
            askLine = text.Create(root);
            // Each button in a layer of its own, so a yes button can show the read guard as one: Run filled, the others
            // filled and outlined as an off chip is.
            foreach (var kind in Enum.GetValues<ConfirmButton>())
            {
                var layer = c.CreateContainerVisual();
                root.Children.InsertAtTop(layer);
                var geometry = c.CreateRoundedRectangleGeometry();
                geometry.CornerRadius = new Vector2(PaletteLayout.ButtonCorner);
                var pill = c.CreateSpriteShape(geometry);
                var fill = c.CreateColorBrush(Argb(PaletteLayout.ButtonFill));
                pill.FillBrush = fill;
                if (kind != ConfirmButton.Run)
                {
                    pill.StrokeBrush = c.CreateColorBrush(Argb(PaletteLayout.ButtonOutline));
                }

                Shapes(layer).Shapes.Add(pill);
                buttons[kind] = new AskButton(layer, geometry, pill, fill, text.Create(layer));
            }

            readTimer = island.Queue.CreateTimer();
            readTimer.IsRepeating = false;
            readTimer.Interval = TimeSpan.FromMilliseconds(ToolQuestion.ReadMs);
            readTimer.Tick += (_, _) =>
            {
                // A yes counts now: Run and Always allow take clicks, Enter and Space.
                readable = guarded;
                Retarget();
            };
        }

        askCard.Offset = ask is { } a ? new Vector2(a.Card.X, a.Card.Y) : default;
        askCard.Size = ask is { } b ? new Vector2(b.Card.Width, b.Card.Height) : default;
        askSummary!.Show(ask?.Summary ?? new TextRun("", PaletteLayout.AskSummary, 1), ask?.SummaryAt ?? default);
        askLine!.Show(ask?.Line ?? new TextSlot("", PaletteLayout.AskLine, default));
        var shown = ask?.Buttons ?? [];
        foreach (var (kind, button) in buttons)
        {
            var at = shown.FirstOrDefault(s => s.Button == kind);
            bool on = at.Label.Text is not null;
            button.Geometry.Offset = new Vector2(at.Box.X, at.Box.Y);
            button.Geometry.Size = new Vector2(at.Box.Width, at.Box.Height);
            button.Shape.StrokeThickness = on && kind != ConfirmButton.Run ? 1 : 0;
            if (kind == ConfirmButton.Run)
            {
                button.Fill.Color = Argb(confirm?.Destructive == true ? PaletteLayout.RunWarn : PaletteLayout.RunFill);
            }

            button.Label.Show(on ? at.Label : new TextSlot("", PaletteLayout.Button, default));
        }

        if (laidAsk is { } open && !ReferenceEquals(open, guarded))
        {
            // A new question: the yes buttons show the read guard, dim while a yes would not count yet, brightening as
            // it starts to. Cancel works at once.
            guarded = open;
            var fade = c.CreateScalarKeyFrameAnimation();
            fade.InsertKeyFrame(0, StateLook.DisabledOpacity);
            fade.InsertKeyFrame(0.7f, StateLook.DisabledOpacity);
            fade.InsertKeyFrame(1, 1);
            fade.Duration = TimeSpan.FromMilliseconds(ToolQuestion.ReadMs);
            buttons[ConfirmButton.Run].Layer.StartAnimation("Opacity", fade);
            buttons[ConfirmButton.AlwaysAllow].Layer.StartAnimation("Opacity", fade);
            readTimer!.Stop();
            readTimer.Start();
        }
    }

    // One of the question's buttons: its layer, its pill with a fill (and, but for Run, an outline), and its label.
    private sealed record AskButton(ContainerVisual Layer, CompositionRoundedRectangleGeometry Geometry, CompositionSpriteShape Shape, CompositionColorBrush Fill, TextVisual Label);

    // The answer from a button, Enter or Esc, for the question the card shows: never for one that opened after it was
    // drawn. False when none is shown, or when a yes comes before the card has shown for long enough to be read.
    private bool Answer(ConfirmButton button) =>
        laidAsk is { } shown && question!.Answer(button switch
        {
            ConfirmButton.Run => PermissionAnswer.AllowOnce,
            ConfirmButton.AlwaysAllow => PermissionAnswer.AlwaysAllow,
            _ => PermissionAnswer.Deny,
        }, shown);

    /// <summary>The answer as blocks: the model's text, each tool step on its own line, and an error last.</summary>
    internal static List<(string Text, TextStyle Style)> Blocks(IPaletteSession session)
    {
        var blocks = session.Answer.Select(segment => segment switch
        {
            TextSegment t => (t.Text.Trim(), PaletteLayout.AnswerText),
            ToolStepSegment step => (step.IsError ? $"{step.Summary}: {step.Result}" : step.Summary, PaletteLayout.StepText),
            _ => ("", PaletteLayout.AnswerText),
        }).ToList();
        if (session.Error is { } error)
        {
            blocks.Add((error, PaletteLayout.ErrorText));
        }

        return blocks;
    }

    private void Show(int i, TextRun run, Vector2 at)
    {
        while (runs.Count <= i)
        {
            runs.Add(text.Create(scrolled));
        }

        runs[i].Show(run, at);
    }

    // Runs past what is shown now are emptied, not disposed, so a new answer reuses them.
    private void Trim(int shown)
    {
        for (int i = shown; i < runs.Count; i++)
        {
            runs[i].Show(new TextRun("", PaletteLayout.AnswerText, 1), default);
        }
    }

    private void SetHeight(float height)
    {
        if (height != Height)
        {
            Height = height;
            HeightChanged?.Invoke();
        }
    }

    private void Resize() => island.Queue.TryEnqueue(Update);

    private void FadeIn()
    {
        if (fadeInPending && machine.State == IslandState.Palette)
        {
            fadeInPending = false;
            root.StartAnimation("Opacity", PanelHost.Fade(island.Compositor, 1, PanelHost.FadeInMs));
            // Its targets go live as it starts to show, in the palette's own space from its top-left corner.
            var corner = new Vector2(root.Offset.X, root.Offset.Y);
            island.Interaction.Register(this, p => p - corner, p => p + corner);
        }
    }

    // The wheel over the answer scrolls it, three lines a notch; scrolled back to its end, it follows a streaming answer again.
    private void OnWheel(Vector2 at, int delta)
    {
        if (disposed || session is null || machine.State != IslandState.Palette)
        {
            return;
        }

        var local = at - new Vector2(root.Offset.X, root.Offset.Y);
        if (!layout.AnswerBand(Height).Contains(local.X, local.Y))
        {
            return;
        }

        scroll = layout.Scrolled(scroll, delta, answerHeight, Height);
        pinned = layout.AtEnd(scroll, answerHeight, Height);
        scrolled.Offset = new Vector3(0, -scroll, 0);
        foreach (var run in runs)
        {
            run.Refresh();
        }
    }

    // The chips and buttons are the interaction host's. A press on the prompt's card is the typing focus's: the box
    // takes its own presses, and its padding looks like part of it, so a press there gives the box the keys too.
    private void OnPressed(Vector2 at)
    {
        if (disposed || session is null || machine.State != IslandState.Palette)
        {
            return;
        }

        var local = at - new Vector2(root.Offset.X, root.Offset.Y);
        if (prompt is not null && !Sending && layout.Prompt.Contains(local.X, local.Y) && !layout.Send.Contains(local.X, local.Y) && !prompt.Contains(at))
        {
            focus?.Take(prompt);
        }

        // The typing focus saw the press first: a click into the box gave it the keys, and the outline and the line say so.
        Update();
    }

    private void OnState(IslandState from, IslandState to)
    {
        if (to == IslandState.Palette)
        {
            return;
        }

        // Out with the shape: the content fades over 0.1 s, then goes; a timer, because a sleeping display finishes no animation.
        // Its targets are nobody's from now.
        island.Interaction.Unregister(this);
        machine.Changed -= OnState;
        root.StartAnimation("Opacity", PanelHost.Fade(island.Compositor, 0, PanelHost.FadeOutMs));
        PanelHost.AfterFadeOut(island, Dispose).Start();
    }
}

/// <summary>The palette hotkey's half of the host: the prompt box that takes the keys.</summary>
internal sealed partial class PaletteHost
{
    private PaletteView? typed;

    /// <summary>The prompt of the palette <see cref="Type"/> last opened takes the keys again, while that palette is still up. False when it has none or Windows refused.</summary>
    public bool TakeKeys() => typed?.TakeKeys() == true;

    /// <summary>
    /// The palette hotkey or a click on the tomte, once the triggers have opened the Palette state: the prompt box takes
    /// the keys at once, while that press is the last input, and Enter sends. A tool that asks shows its question with
    /// Run, Cancel and, where its tier allows, Always allow; Enter and Esc answer it while the box has the keys. "/name"
    /// puts that skill's command in the box, and the next Enter sends it. Esc and focus loss while nothing is being sent
    /// hide the island; leaving the palette cancels what is in flight (an open question is answered no), then stops the
    /// MCP servers and the UI Automation thread the session started. With no session, because no provider is set up,
    /// the palette says the AI layer is off and takes no keys.
    /// </summary>
    public void Type(PaletteSession? session)
    {
        // The triggers opened the palette at its smallest; growth starts from there.
        _targetHeight = PanelSize.PaletteMinHeight;
        var question = new ToolQuestion();
        Task sending = Task.CompletedTask;
        PaletteView? view = null;
        view = new PaletteView(island, machine, text, focus, session, (float)Width, session is null ? null : Entered, question);
        typed = view;
        view.HeightChanged += () => Grow(view.Height);
        if (hide is not null)
        {
            view.RetractOnFocusLoss(hide);
        }

        if (session is not null)
        {
            session.ConfirmTool = question.AskAsync;
            question.Changed += () => RestWhileAsked(question);
            machine.Changed += Closed;
        }

        view.TakeKeys();
        Grow(view.Height);

        void Entered(string typed)
        {
            // "/name" puts that skill's command in the box for the user to read; the next Enter sends it. Set on the next
            // turn: this Enter is still inside the box's key handling.
            if (Tomte.AI.Skills.SkillSet.Expand(session!.Commands, typed) is { } command)
            {
                island.Queue.TryEnqueue(() => view!.Fill(command));
                return;
            }

            var send = SendAsync(session, typed);
            if (!send.IsCompleted)
            {
                sending = send;
            }
        }

        void Closed(IslandState from, IslandState to)
        {
            if (to == IslandState.Palette)
            {
                return;
            }

            machine.Changed -= Closed;
            session!.Cancel();
            _ = ReleaseAsync(session, sending);
        }
    }

    // Once the last send has ended, nothing uses the session's servers or its automation thread any more. Each is
    // stopped on its own, so one that fails does not keep the other running.
    private async Task ReleaseAsync(PaletteSession session, Task sending)
    {
        try
        {
            await sending;
            session.Automation?.Dispose();
        }
        catch (Exception e)
        {
            log($"{DateTime.Now:HH:mm:ss.fff} palette: UI Automation not stopped: {e.Message}");
        }

        try
        {
            if (session.Mcp is { } mcp)
            {
                await mcp.DisposeAsync();
            }
        }
        catch (Exception e)
        {
            log($"{DateTime.Now:HH:mm:ss.fff} palette: MCP servers not stopped: {e.Message}");
        }
    }

    private double Width => Math.Min(PanelSize.PaletteWidth, 0.6 * island.MonitorSize.Width);

    // Every send is the user's Enter; the session logs it and shows where it goes.
    private async Task SendAsync(PaletteSession session, string typed)
    {
        if (session.Status == PaletteStatus.Sending || string.IsNullOrWhiteSpace(typed) || machine.State != IslandState.Palette)
        {
            return;
        }

        session.Prompt = typed;
        island.Mascot.Set(MascotState.Thinking);
        await session.SendAsync();
        if (machine.State == IslandState.Palette)
        {
            island.Mascot.Set(session.Status == PaletteStatus.Done ? MascotState.Result : MascotState.Error);
        }

        log($"{DateTime.Now:HH:mm:ss.fff} palette {session.Status}{(session.Error is null ? "" : ": " + session.Error)} indicator=\"{session.ProviderIndicator}\" steps={session.ToolSteps}");
    }

    private void Grow(double height)
    {
        if (machine.State == IslandState.Palette && height > 0 && height != _targetHeight)
        {
            _targetHeight = height;
            _ = island.MorphTo(Geometry(height), SpringPreset.Gentle);
        }
    }
}
