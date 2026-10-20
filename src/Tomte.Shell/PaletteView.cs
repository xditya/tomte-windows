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
    internal const string SendId = "send";
    private const string ChipId = "chip:";
    private static Color Card => ShelfColors.Of(Theme.Card);

    private static Color Clear => default;

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
    private string laidSends = "";
    private List<ContextChip> shownChips = [];
    private IReadOnlyList<Target> targets = [];
    private readonly ContainerVisual root, chipLayer;
    private readonly ShapeVisual shapes, chipShapes;
    private readonly CompositionRoundedRectangleGeometry card;
    private readonly List<ChipSlot> slots = [];
    private readonly List<TextVisual> runs = [];
    private readonly TextVisual placeholder, provider, cost, spend, sendsLine;
    private readonly ShapeVisual footShapes;
    private readonly CompositionRoundedRectangleGeometry divider, meterTrack, meterFill;
    private readonly CompositionEllipseGeometry dot;
    private readonly CompositionColorBrush dotInk;
    private readonly ShapeVisual sparkle;
    private readonly ContainerVisual actionLayer;
    private readonly ShapeVisual actionShapes;
    private readonly List<(CompositionRoundedRectangleGeometry Pill, CompositionSpriteShape Shape, TextVisual Label)> actionSlots = [];
    private readonly HashSet<string> done = [];
    private IReadOnlyList<LaidAction> laidActions = [];
    private PixelSurfaces? thumbs;
    private SpriteVisual? thumb;
    private bool thumbLoading;
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
    // The questions opened so far, which numbers the buttons' ids.
    private ToolRequest? guarded, readable;
    private int questions;
    private DispatcherQueueTimer? readTimer;

    // What the targets were last built from: rebuilt only when one of these changes, not for every streamed word.
    private (PaletteLayout? Layout, float Height, bool Sending, bool Readable, bool CanSend, bool History, bool Back) targeted;

    // The footer's AI history button: its clock, and what a click does; neither while nothing opens the tab (voice).
    private CompositionContainerShape? clock;
    private Action? openHistory;

    // The back arrow before the prompt, and what it does; neither for a palette opened from Hidden.
    private CompositionContainerShape? backArrow;
    private Action? back;
    private bool laidBack;
    private IReadOnlyList<string> laidOffered = [];

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
        shapes.Shapes.Add(cardShape);
        // The chips and the line on what they send in one layer, which dims as one while a request is sending.
        chipLayer = c.CreateContainerVisual();
        root.Children.InsertAtTop(chipLayer);
        chipShapes = Shapes(chipLayer);
        sendsLine = text.Create(chipLayer);
        // The answer's runs sit in one container clipped to the answer's band and moved by the scroll.
        band = c.CreateContainerVisual();
        band.Clip = c.CreateInsetClip();
        scrolled = c.CreateContainerVisual();
        band.Children.InsertAtTop(scrolled);
        root.Children.InsertAtTop(band);
        // The sparkle before the answer scrolls with it.
        sparkle = c.CreateShapeVisual();
        sparkle.Size = new Vector2(PaletteLayout.SparkleSize);
        var star = c.CreateContainerShape();
        DrawGlyph(star, Glyphs.Sparkle, c.CreateColorBrush(Argb(Theme.Accent)), PaletteLayout.SparkleSize);
        sparkle.Shapes.Add(star);
        sparkle.IsVisible = false;
        scrolled.Children.InsertAtTop(sparkle);
        actionLayer = c.CreateContainerVisual();
        root.Children.InsertAtTop(actionLayer);
        actionShapes = Shapes(actionLayer);
        placeholder = text.Create(root);
        // The footer: its divider, the status dot and the meter, and its three lines.
        footShapes = Shapes(root);
        divider = Rounded(footShapes, Argb(PaletteLayout.Divider), 0);
        dot = c.CreateEllipseGeometry();
        var dotShape = c.CreateSpriteShape(dot);
        dotShape.FillBrush = dotInk = c.CreateColorBrush();
        footShapes.Shapes.Add(dotShape);
        meterTrack = Rounded(footShapes, Argb(PaletteLayout.MeterTrack), PaletteLayout.MeterHeight / 2);
        meterFill = Rounded(footShapes, Argb(PaletteLayout.MeterFill), PaletteLayout.MeterHeight / 2);
        provider = text.Create(root);
        cost = text.Create(root);
        spend = text.Create(root);
        if (session is not null && focus is not null)
        {
            // The prompt hands Tab and Shift+Tab to the palette's chips, Send and buttons, and takes the keys back after them.
            prompt = focus.Create(root, multiline: true, PaletteLayout.PromptSize, owner: this);
            prompt.CaretInk = Theme.Accent;
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
                // Send: an accent button in a visual of its own, which dims as one while there is nothing to send.
                sendVisual = Shapes(root);
                sendButton = c.CreateRoundedRectangleGeometry();
                sendButton.CornerRadius = new Vector2(PaletteLayout.SendCorner);
                var button = c.CreateSpriteShape(sendButton);
                button.FillBrush = c.CreateColorBrush(Argb(PaletteLayout.SendFill));
                sendVisual.Shapes.Add(button);
                sendArrow = c.CreateContainerShape();
                DrawGlyph(sendArrow, Glyphs.Send, c.CreateColorBrush(Argb(PaletteLayout.SendInk)), PaletteLayout.SendGlyph);
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
            // An open question's line drops "Enter runs it" once the box no longer has the keys, and the card's outline
            // stops saying it has them once Tab moved on to a chip.
            focus!.Lost += Update;
            prompt.FocusChanged += () => island.Queue.TryEnqueue(Update);
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

    /// <summary>The words in the prompt, sent at once as the Send button sends them: the search box's Ask row.</summary>
    public void FillAndSend(string words)
    {
        Fill(words);
        if (send is not null && prompt is not null && !disposed && words.Trim().Length > 0)
        {
            send(prompt.Services.Text);
        }
    }

    /// <summary>What the palette shows, for the debug driver.</summary>
    internal IPaletteSession? Session => session;

    /// <summary>What the prompt box holds, typed and not sent; empty with no box.</summary>
    public string PromptText => prompt is not null && !disposed ? prompt.Services.Text : "";

    /// <summary>Where the answer is scrolled to, and whether it follows its newest line.</summary>
    public (float Scroll, bool Pinned) Scrolled => (scroll, pinned);

    /// <summary>The palette as it was when it closed: the prompt's text, and the answer scrolled where it was.</summary>
    public void Restore(string typed, (float Scroll, bool Pinned) scrolled)
    {
        Fill(typed);
        (scroll, pinned) = scrolled;
        Update();
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

    /// <summary>Add to note under a finished answer: the answer goes on the note. Without it the action is not offered.</summary>
    public Action<string>? AddToNote { get; init; }

    /// <summary>Keep on shelf under a finished answer: the answer goes on the shelf as a file. Without it the action is not offered.</summary>
    public Action<string>? KeepOnShelf { get; init; }

    /// <summary>The footer's AI history button: Panels on the History tab. Without it the palette shows no button (voice).</summary>
    public Action? OpenHistory
    {
        get => openHistory;
        set
        {
            openHistory = value;
            Update();
        }
    }

    /// <summary>
    /// The back arrow: Panels on the tab the AI box was opened from, as that tab was left. Without it the palette shows no
    /// arrow, and Esc with no question open hides the island (a palette opened from Hidden).
    /// </summary>
    public Action? Back
    {
        get => back;
        set
        {
            back = value;
            Update();
        }
    }

    /// <summary>A chip turns on or off, Send sends, a question's button answers it, or the arrow goes back; nothing while that target is disabled.</summary>
    public void Activate(string id)
    {
        if (disposed || machine.State != IslandState.Palette || targets.FirstOrDefault(t => t.Id == id) is not { Enabled: true })
        {
            return;
        }

        if (id == PaletteLayout.BackId)
        {
            back?.Invoke();
            return;
        }

        if (session is null)
        {
            return;
        }

        if (id == SendId)
        {
            send!(prompt!.Services.Text);
        }
        else if (laidActions.Any(a => a.Id == id))
        {
            Act(id);
        }
        else if (shownChips.FirstOrDefault(chip => ChipId + chip.Label == id) is { } chip)
        {
            chip.On = !chip.On;
        }
        else if (id == PaletteLayout.HistoryId)
        {
            openHistory?.Invoke();
            return;
        }
        else if (PaletteLayout.ButtonFor(id, questions) is { } button)
        {
            Answer(button);
        }

        Update();
    }

    /// <summary>
    /// The AI box's keys, whatever has focus: Alt+W, Alt+S, Alt+H and Alt+N turn their chips on or off, Alt+A always
    /// allows the open question, Ctrl+H opens History, Page Up and Page Down scroll the answer, Alt+Left goes back. Esc
    /// cancels an open question, else goes back, else leaves the island to hide.
    /// </summary>
    public bool Command(NavKey key)
    {
        if (disposed || machine.State != IslandState.Palette)
        {
            return false;
        }

        if (NavKeys.Chip(key) is { } icon)
        {
            return shownChips.FirstOrDefault(chip => chip.Spec.Icon == icon) is { } chip && Press(ChipId + chip.Label);
        }

        switch (key)
        {
            case NavKey.AlwaysAllow:
                return Press(PaletteLayout.ButtonId(ConfirmButton.AlwaysAllow, questions));
            case NavKey.History when openHistory is not null && session is not null:
                openHistory();
                return true;
            case NavKey.PageUp or NavKey.PageDown when session is not null:
                ScrollTo(layout.Paged(scroll, key == NavKey.PageDown ? 1 : -1, answerHeight, Height));
                return true;
            // Esc cancels an open question first, then goes back to the tab; with neither, the island hides.
            case NavKey.Escape when laidAsk is not null:
                return Answer(ConfirmButton.Cancel);
            case NavKey.Escape or NavKey.Back when back is not null:
                back();
                return true;
            default:
                return false;
        }
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
        string sends = ContextChips.Summary(shownChips);
        var asked = question?.Asked;
        // Enter answers only while the prompt box has the keys; the line says so, and the card's outline shows it.
        bool focused = prompt is not null && focus?.Focused == prompt;
        var keys = !typed ? ConfirmKeys.Voice : focused ? ConfirmKeys.Prompt : ConfirmKeys.Pointer;
        var actions = Offered(asked);
        if (promptText != laidText || !chips.SequenceEqual(laidChips) || sends != laidSends || !ReferenceEquals(asked, laidAsk) || keys != laidKeys
            || (back is not null) != laidBack || !actions.SequenceEqual(laidOffered))
        {
            (laidText, laidChips, laidSends, laidAsk, laidKeys, laidBack, laidOffered) = (promptText, chips, sends, asked, keys, back is not null, actions);
            bool job = asked?.Tool == Tomte.AI.Jobs.JobPlanner.Tool;
            confirm = asked is null ? null : new Confirmation(asked.Summary, Confirmation.LineFor(asked.Tier, keys, job), asked.OfferAlwaysAllow, asked.Tier == ActionTier.Destructive, job);
            layout = new PaletteLayout(width, island.Mascot.Enabled, promptText, chips, confirm, send: sendVisual is not null, sends, laidBack, actions);
        }

        var box = layout.Prompt;
        card.Offset = new Vector2(box.X, box.Y);
        card.Size = new Vector2(box.Width, box.Height);
        prompt?.Place(layout.Editor, island.Scale);
        Hint();
        BackArrow();

        if (session is null)
        {
            // No provider: what the palette needs, under the box, and nothing to type in.
            setup ??= text.Create(root);
            setup.Show(new TextRun(PaletteLayout.Setup, TextStyle.Secondary, layout.AnswerWidth), new Vector2(PaletteLayout.Pad + PaletteLayout.AnswerInset, layout.AnswerTop));
            SetHeight(layout.Height(0));
            Retarget();
            return;
        }

        ShowChips();

        // While a tool's question is open the model has stopped writing, so its text is whole even though the send goes on.
        var (answer, measured) = layout.Answer(Blocks(session, followUp: typed), streaming: session.Status == PaletteStatus.Sending && asked is null);
        // The host caps the island at half the monitor's height; the answer band gives way first, so a tall card and its
        // footer are never cut off.
        float height = Math.Min(layout.Height(measured), (float)(0.5 * island.MonitorSize.Height));
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
        sparkle.IsVisible = answer.Count > 0;
        var spark = layout.Sparkle;
        sparkle.Offset = new Vector3(spark.X, spark.Y, 0);
        ShowActions(height);
        ShowFoot(height);
        Clock(layout.History(height));
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
        foreach (var visual in slots.Select(s => s.Label).Concat(runs).Concat(buttons.Values.Select(b => b.Label)).Append(placeholder).Append(provider).Append(cost).Append(spend).Append(sendsLine).Concat(actionSlots.Select(a => a.Label)))
        {
            visual.Dispose();
        }

        setup?.Dispose();
        thumb?.Dispose();
        thumbs?.Dispose();
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

    /// <summary>A glyph's figures in a container shape, all in one ink: filled, or stroked along their outlines.</summary>
    internal static void Draw(CompositionContainerShape into, GlyphFigure[] figures, CompositionColorBrush ink)
    {
        into.Shapes.Clear();
        foreach (var figure in figures)
        {
            var shape = ink.Compositor.CreateSpriteShape(ink.Compositor.CreatePathGeometry(IslandWindow.BuildPath(figure.Path, 1)));
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

    /// <summary>
    /// A <see cref="Glyphs"/> entry in a container shape, <paramref name="size"/> DIPs square from the container's
    /// origin, in one ink: stroked figures at 1.75/24 of the size with round caps and joins, filled figures filled.
    /// </summary>
    internal static void DrawGlyph(CompositionContainerShape into, GlyphFigure[] figures, CompositionColorBrush ink, float size)
    {
        float scale = size / Glyphs.Box;
        into.Shapes.Clear();
        foreach (var figure in figures)
        {
            var shape = ink.Compositor.CreateSpriteShape(ink.Compositor.CreatePathGeometry(IslandWindow.BuildPath(figure.Path, scale, !figure.Open)));
            if (figure.Stroked)
            {
                shape.StrokeBrush = ink;
                shape.StrokeThickness = Glyphs.StrokeWidth * scale;
                shape.StrokeStartCap = shape.StrokeEndCap = CompositionStrokeCap.Round;
                shape.StrokeLineJoin = CompositionStrokeLineJoin.Round;
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
    private sealed record ChipSlot(CompositionRoundedRectangleGeometry Pill, CompositionSpriteShape Stroke, CompositionColorBrush Fill, CompositionColorBrush Outline, CompositionColorBrush Ink, CompositionContainerShape Icon, CompositionContainerShape Mark, TextVisual Label)
    {
        public (ChipIcon Icon, bool On, bool Thumb)? Drawn { get; set; }
    }

    private ChipSlot Slot()
    {
        var c = island.Compositor;
        var pill = c.CreateRoundedRectangleGeometry();
        var shape = c.CreateSpriteShape(pill);
        var slot = new ChipSlot(pill, shape, c.CreateColorBrush(), c.CreateColorBrush(), c.CreateColorBrush(), c.CreateContainerShape(), c.CreateContainerShape(), text.Create(chipLayer));
        shape.FillBrush = slot.Fill;
        shape.StrokeBrush = slot.Outline;
        chipShapes.Shapes.Add(shape);
        chipShapes.Shapes.Add(slot.Icon);
        chipShapes.Shapes.Add(slot.Mark);
        return slot;
    }

    // Each chip shown, first in the prompt card: off, an outline with a "+"; on, raised with a close cross. The circled
    // region shows its thumbnail. All of them dim while a request is sending, when a click on one changes nothing.
    private void ShowChips()
    {
        var row = layout.Chips;
        sendsLine.Show(layout.SendsLine);
        while (slots.Count < row.Count)
        {
            slots.Add(Slot());
        }

        bool pictured = false;
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
            var (path, corner, stroke) = PaletteLayout.ChipStroke(chip, new Vector2(root.Offset.X, root.Offset.Y), island.Scale);
            slot.Pill.Offset = new Vector2(path.X, path.Y);
            slot.Pill.Size = new Vector2(path.Width, path.Height);
            slot.Pill.CornerRadius = new Vector2(corner);
            slot.Stroke.StrokeThickness = chip.On ? 0 : stroke;
            slot.Fill.Color = chip.On ? Argb(PaletteLayout.ChipOnFill) : Clear;
            slot.Outline.Color = Argb(PaletteLayout.ChipOutline);
            slot.Ink.Color = Argb(chip.On ? PaletteLayout.ChipOnInk : PaletteLayout.ChipInk);
            var drawn = (laidChips[i].Icon, chip.On, chip.Thumb);
            if (slot.Drawn != drawn)
            {
                slot.Drawn = drawn;
                if (chip.Thumb)
                {
                    slot.Icon.Shapes.Clear();
                }
                else
                {
                    DrawGlyph(slot.Icon, ChipGlyph(drawn.Icon), slot.Ink, PaletteLayout.IconSize);
                }

                DrawGlyph(slot.Mark, chip.On ? Glyphs.Close : Glyphs.Plus, slot.Ink, PaletteLayout.MarkSize);
            }

            slot.Icon.Offset = new Vector2(chip.Icon.X, chip.Icon.Y);
            slot.Mark.Offset = new Vector2(chip.Mark.X, chip.Mark.Y);
            slot.Label.Show(chip.Label);
            if (chip.Thumb && shownChips[i].Picture is { } png)
            {
                pictured = true;
                Thumb(chip.Icon, png);
            }
        }

        if (thumb is not null)
        {
            thumb.IsVisible = pictured;
        }

        chipLayer.Opacity = Sending ? StateLook.DisabledOpacity : 1;
    }

    // A chip's picture from the icon set.
    private static GlyphFigure[] ChipGlyph(ChipIcon icon) => icon switch
    {
        ChipIcon.Selection => Glyphs.Text,
        ChipIcon.Window => Glyphs.Window,
        ChipIcon.Screenshot => Glyphs.Region,
        ChipIcon.Shelf => Glyphs.Shelf,
        ChipIcon.Note => Glyphs.Pencil,
        _ => Glyphs.Region,
    };

    // The circled region's thumbnail in its chip, decoded to the slot's pixels off this thread once per palette.
    private async void Thumb(RectangleF at, byte[] png)
    {
        if (thumb is null)
        {
            var c = island.Compositor;
            var round = c.CreateRoundedRectangleGeometry();
            round.Size = new Vector2(at.Width, at.Height);
            round.CornerRadius = new Vector2(PaletteLayout.ThumbCorner);
            thumb = c.CreateSpriteVisual();
            thumb.Size = round.Size;
            thumb.Clip = c.CreateGeometricClip(round);
            thumb.Brush = c.CreateColorBrush(Argb(Theme.RaisedHi));
            chipLayer.Children.InsertAtTop(thumb);
        }

        thumb.Offset = new Vector3(at.X, at.Y, 0);
        if (thumbLoading || thumbs is not null)
        {
            return;
        }

        thumbLoading = true;
        int w = (int)Math.Round(at.Width * island.Scale), h = (int)Math.Round(at.Height * island.Scale);
        var compositor = island.Compositor;
        try
        {
            var (made, brush) = await Task.Run(() =>
            {
                var pixels = Tomte.Core.Clipboard.ClipboardImages.Thumbnail(png, w, h);
                var surfaces = new PixelSurfaces(compositor);
                return (surfaces, surfaces.Brush(w, h, pixels));
            });
            if (disposed)
            {
                made.Dispose();
                return;
            }

            thumbs = made;
            thumb.Brush = brush;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // A picture that does not decode, or no device: the chip keeps its neutral fill.
        }
    }

    private bool Sending => session?.Status == PaletteStatus.Sending;

    // The targets the island's interaction host runs, in Tab order: the chips as one stop (the arrows move among them),
    // Send, then each of the question's buttons as a stop of its own, the yes buttons once a yes would count, the history
    // button and the back arrow. With no session, the back arrow alone. Built
    // again only when the layout, the sending state, the read guard or Send's state changed; raised to the host only
    // when the list did. A chip's tooltip is read then, so a note that loads later shows in it at the next change.
    private void Retarget()
    {
        bool sending = Sending, ready = ReferenceEquals(readable, laidAsk);
        bool canSend = sendVisual is not null && PaletteLayout.CanSend(prompt!.Services.Text, sending);
        var from = (layout, Height, sending, ready, canSend, openHistory is not null, back is not null);
        if (disposed || from == targeted)
        {
            return;
        }

        targeted = from;
        var list = new List<Target>();
        // In the order they are drawn: Send at the prompt's right end, then the chips under it.
        if (sendVisual is not null)
        {
            list.Add(new Target(SendId, layout.Send, PaletteLayout.SendCorner, TargetKind.Button, canSend, PaletteLayout.SendTip, Key: "Enter"));
        }

        for (int i = 0; i < layout.Chips.Count && session is not null; i++)
        {
            var chip = layout.Chips[i];
            list.Add(new Target(ChipId + shownChips[i].Label, chip.Box, PaletteLayout.ChipCorner, TargetKind.Toggle, !sending, shownChips[i].Tip, Group: 1, Checked: chip.On,
                Key: NavKeys.ChipKey(shownChips[i].Spec.Icon)));
        }

        foreach (var action in layout.Actions(Height))
        {
            list.Add(new Target(action.Id, action.Box, PaletteLayout.ActionHeight / 2, TargetKind.Button, !sending));
        }

        foreach (var (button, at, _) in layout.Ask(Height)?.Buttons ?? [])
        {
            list.Add(new Target(PaletteLayout.ButtonId(button, questions), at, PaletteLayout.ButtonCorner, TargetKind.Button, button == ConfirmButton.Cancel || ready, Key: PaletteLayout.ButtonKey(button)));
        }

        if (openHistory is not null && session is not null)
        {
            list.Add(layout.HistoryTarget(Height));
        }

        // Last, so Shift+Tab from the prompt reaches the arrow drawn before it.
        if (back is not null)
        {
            list.Add(new Target(PaletteLayout.BackId, layout.Back, PaletteLayout.BackCorner, TargetKind.Button, Tip: PaletteLayout.BackTip, Key: PaletteLayout.BackKey));
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

        var editorBox = layout.Editor;
        if (sendVisual is not null)
        {
            var button = layout.Send;
            sendButton!.Offset = new Vector2(button.X, button.Y);
            sendButton.Size = new Vector2(button.Width, button.Height);
            sendArrow!.Offset = new Vector2(button.X + (button.Width - PaletteLayout.SendGlyph) / 2, button.Y + (button.Height - PaletteLayout.SendGlyph) / 2);
            sendVisual.Opacity = PaletteLayout.CanSend(prompt!.Services.Text, Sending) ? 1 : StateLook.DisabledOpacity;
        }

        if (prompt is null && session is not null)
        {
            placeholder.Show(new TextRun(session.Prompt, PaletteLayout.PromptText, editorBox.Width, PaletteLayout.PromptLines), new Vector2(editorBox.X, editorBox.Y));
            return;
        }

        bool empty = prompt is null || prompt.Services.Text.Length == 0;
        placeholder.Show(empty ? (session is null ? PaletteLayout.Off : hint) : "", PaletteLayout.Placeholder, layout.HintLine);
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
            questions++;
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

    // The actions a finished answer offers in a palette with a text box: Copy, Add to note and Keep on shelf where the
    // host gave them a place to go, and Follow up, which puts the caret back in the prompt. None while a question is open.
    private IReadOnlyList<string> Offered(ToolRequest? asked)
    {
        if (!typed || asked is not null || session is not { Status: PaletteStatus.Done } || session.AnswerText.Trim().Length == 0)
        {
            return [];
        }

        return [PaletteLayout.CopyId, .. AddToNote is null ? Array.Empty<string>() : [PaletteLayout.NoteId], .. KeepOnShelf is null ? Array.Empty<string>() : [PaletteLayout.ShelfId], PaletteLayout.FollowId];
    }

    // An action under the answer: what it does with the answer's words, and the pill says it was done.
    private void Act(string id)
    {
        string words = session!.AnswerText.Trim();
        switch (id)
        {
            case PaletteLayout.CopyId:
                try
                {
                    DesktopEffects.CopyToClipboard(words);
                }
                catch (InvalidOperationException)
                {
                    // Another app holds the clipboard open: nothing was copied, and the pill does not say otherwise.
                    return;
                }

                break;
            case PaletteLayout.NoteId:
                AddToNote?.Invoke(words);
                break;
            case PaletteLayout.ShelfId:
                KeepOnShelf?.Invoke(words);
                break;
            default:
                TakeKeys();
                return;
        }

        done.Add(id);
    }

    // The action pills under the answer: filled, Follow up outlined; made as they are first needed.
    private void ShowActions(float height)
    {
        laidActions = layout.Actions(height, done);
        var c = island.Compositor;
        while (actionSlots.Count < laidActions.Count)
        {
            var pill = c.CreateRoundedRectangleGeometry();
            pill.CornerRadius = new Vector2(PaletteLayout.ActionHeight / 2);
            var shape = c.CreateSpriteShape(pill);
            actionShapes.Shapes.Add(shape);
            actionSlots.Add((pill, shape, text.Create(actionLayer)));
        }

        for (int i = 0; i < actionSlots.Count; i++)
        {
            var (pill, shape, label) = actionSlots[i];
            if (i >= laidActions.Count)
            {
                pill.Size = default;
                label.Show(new TextSlot("", PaletteLayout.Action, default));
                continue;
            }

            var action = laidActions[i];
            pill.Offset = new Vector2(action.Box.X + (action.Outlined ? 0.5f : 0), action.Box.Y + (action.Outlined ? 0.5f : 0));
            pill.Size = new Vector2(action.Box.Width - (action.Outlined ? 1 : 0), action.Box.Height - (action.Outlined ? 1 : 0));
            shape.FillBrush = action.Outlined ? null : c.CreateColorBrush(Argb(PaletteLayout.ActionFill));
            shape.StrokeBrush = action.Outlined ? c.CreateColorBrush(Argb(PaletteLayout.ActionOutline)) : null;
            shape.StrokeThickness = action.Outlined ? 1 : 0;
            label.Show(action.Label);
        }
    }

    // The footer under its divider: the dot, where the request went and what it cost, today's spend and the meter.
    private void ShowFoot(float height)
    {
        var foot = layout.Foot(session!.ProviderIndicator, session.ToolSteps, session.LastCostUsd, session.SpentTodayUsd, session.DailyCapUsd, height, history: openHistory is not null);
        Place(divider, foot.Divider);
        dot.Center = new Vector2(foot.Dot.X + (foot.Dot.Width / 2), foot.Dot.Y + (foot.Dot.Height / 2));
        dot.Radius = new Vector2(foot.Dot.Width / 2);
        dotInk.Color = Argb(PaletteLayout.StatusDot(session.ProviderIndicator, session.Status == PaletteStatus.Failed));
        Place(meterTrack, foot.Meter);
        Place(meterFill, foot.Meter with { Width = foot.Fill });
        provider.Show(foot.Provider);
        cost.Show(foot.Cost);
        spend.Show(foot.Spend);
    }

    private CompositionRoundedRectangleGeometry Rounded(ShapeVisual into, Color fill, float corner)
    {
        var geometry = island.Compositor.CreateRoundedRectangleGeometry();
        geometry.CornerRadius = new Vector2(corner);
        var shape = island.Compositor.CreateSpriteShape(geometry);
        shape.FillBrush = island.Compositor.CreateColorBrush(fill);
        into.Shapes.Add(shape);
        return geometry;
    }

    private static void Place(CompositionRoundedRectangleGeometry geometry, RectangleF at)
    {
        geometry.Offset = new Vector2(at.X, at.Y);
        geometry.Size = new Vector2(at.Width, at.Height);
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

    /// <summary>
    /// The answer as blocks: the model's text, each tool step on its own line, and an error last. In a palette that can
    /// ask again (<paramref name="followUp"/>), a conversation so far adds the line that says the next question takes its
    /// text along.
    /// </summary>
    internal static List<(string Text, TextStyle Style)> Blocks(IPaletteSession session, bool followUp = false)
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

        if (followUp && session.Conversation.Count > 0 && session.Status != PaletteStatus.Sending)
        {
            bool fresh = session.Continuing is not null && session.Conversation.Count == session.Continuing.Turns.Count;
            blocks.Add((PaletteLayout.ContinueLine(fresh ? session.Continuing!.Question : null), PaletteLayout.StepText));
        }

        return blocks;
    }

    // The history button's clock in the footer: a ring and two hands, made once, moved with the palette's height.
    private void Clock(RectangleF at)
    {
        if (openHistory is null)
        {
            return;
        }

        if (clock is null)
        {
            var c = island.Compositor;
            clock = c.CreateContainerShape();
            var ink = c.CreateColorBrush(Argb(Theme.Ink2));
            var ring = c.CreateEllipseGeometry();
            ring.Center = new Vector2(at.Width / 2);
            ring.Radius = new Vector2(PaletteLayout.ClockRadius);
            var stroke = c.CreateSpriteShape(ring);
            stroke.StrokeBrush = ink;
            stroke.StrokeThickness = PaletteLayout.ClockStroke;
            clock.Shapes.Add(stroke);
            foreach (var hand in PaletteLayout.ClockHands)
            {
                var bar = c.CreateRoundedRectangleGeometry();
                (bar.Offset, bar.Size, bar.CornerRadius) = (new Vector2(hand.X, hand.Y), new Vector2(hand.Width, hand.Height), new Vector2(0.75f));
                var fill = c.CreateSpriteShape(bar);
                fill.FillBrush = ink;
                clock.Shapes.Add(fill);
            }

            shapes.Shapes.Add(clock);
        }

        clock.Offset = new Vector2(at.X, at.Y);
    }

    // The back arrow before the prompt, in the clock's grey, made the first time there is one.
    private void BackArrow()
    {
        if (back is null)
        {
            return;
        }

        if (backArrow is null)
        {
            backArrow = island.Compositor.CreateContainerShape();
            Draw(backArrow, PaletteGlyphs.Back, island.Compositor.CreateColorBrush(Argb(Theme.Ink2)));
            shapes.Shapes.Add(backArrow);
        }

        var box = layout.Back;
        backArrow.Offset = new Vector2(box.X + ((box.Width - PaletteGlyphs.SendSize) / 2), box.Y + ((box.Height - PaletteGlyphs.SendSize) / 2));
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
        if (layout.AnswerBand(Height).Contains(local.X, local.Y))
        {
            ScrollTo(layout.Scrolled(scroll, delta, answerHeight, Height));
        }
    }

    // The answer at a new scroll; scrolled back to its end, it follows a streaming answer again.
    private void ScrollTo(float to)
    {
        scroll = to;
        pinned = layout.AtEnd(scroll, answerHeight, Height);
        scrolled.Offset = new Vector3(0, -scroll, 0);
        foreach (var run in runs)
        {
            run.Refresh();
        }
    }

    // A key that clicks one of the targets (a chip, Always allow): taken while the target shows, done while it is enabled.
    private bool Press(string id)
    {
        if (!targets.Any(t => t.Id == id))
        {
            return false;
        }

        Activate(id);
        return true;
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

    /// <summary>The palette <see cref="Type"/> last opened, for the debug driver.</summary>
    internal PaletteView? View => typed;

    /// <summary>What a closed AI box gives the undo: <see cref="TriggerController.Closed"/>. Null keeps nothing (a driven run).</summary>
    public TriggerController? Triggers { get; init; }

    /// <summary>The prompt of the palette <see cref="Type"/> last opened takes the keys again, while that palette is still up. False when it has none or Windows refused.</summary>
    public bool TakeKeys() => typed?.TakeKeys() == true;

    /// <summary>Words in the prompt of the palette <see cref="Type"/> last opened, with the caret after them; nothing is sent.</summary>
    public void Fill(string words) => typed?.Fill(words);

    /// <summary>Sends the words from the open AI box, as Enter in its prompt would.</summary>
    public void Send(string words) => typed?.FillAndSend(words);

    /// <summary>
    /// The palette hotkey or a click on the tomte, once the triggers have opened the Palette state: the prompt box takes
    /// the keys at once, while that press is the last input, and Enter sends. A tool that asks shows its question with
    /// Run, Cancel and, where its tier allows, Always allow; Enter and Esc answer it while the box has the keys. "/name"
    /// puts that skill's command in the box, and the next Enter sends it. Esc and focus loss while nothing is being sent
    /// hide the island; leaving the palette cancels what is in flight (an open question is answered no), then stops the
    /// MCP servers and the UI Automation thread the session started. With no session, because no provider is set up,
    /// the palette says the AI layer is off and takes no keys. <paramref name="back"/> is the back arrow's action, for a
    /// palette opened from the panels; with none, Esc hides the island as before.
    /// </summary>
    public void Type(PaletteSession? session, Action? back = null) => Type(session, back, null);

    // Shut is the AI box as it closed, for Undo: the same session, question and send, the prompt's text and the scroll.
    private void Type(PaletteSession? session, Action? back, Shut? shut)
    {
        // The triggers opened the palette at its smallest; growth starts from there.
        _targetHeight = PanelSize.PaletteMinHeight;
        var question = shut?.Question ?? new ToolQuestion();
        Task sending = shut?.Sending ?? Task.CompletedTask;
        PaletteView? view = null;
        view = new PaletteView(island, machine, text, focus, session, (float)Width, session is null ? null : Entered, question) { OpenHistory = OpenHistory, Back = back, AddToNote = AddToNote, KeepOnShelf = KeepOnShelf };
        if (shut is not null)
        {
            view.Restore(shut.Typed, shut.Scrolled);
        }

        typed = view;
        view.HeightChanged += () => Grow(view.Height);
        if (hide is not null)
        {
            view.RetractOnFocusLoss(hide);
        }

        if (session is not null)
        {
            session.ConfirmTool = question.AskAsync;
            if (shut is null)
            {
                question.Changed += () => RestWhileAsked(question);
            }

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
            // Something from the provider, or a send on its way, is kept for Undo: the send goes on meanwhile, and an
            // open question waits for the box to come back. Undo off, or the time up, ends it as a close always did.
            var closing = new Shut(question, sending, view!.PromptText, view.Scrolled);
            if (Triggers is { } triggers && session!.Holds)
            {
                triggers.Closed(AnswerClosed, () => triggers.Reopen(PanelSize.PaletteMinHeight) && Reopened(session, closing), Free);
            }
            else
            {
                Free();
            }
        }

        void Free()
        {
            session!.Cancel();
            _ = ReleaseAsync(session, sending);
        }
    }

    /// <summary>What the undo pill says after an AI answer closed.</summary>
    internal const string AnswerClosed = "Answer closed";

    // Undo: the AI box in the pill's place, as it closed. It opened from the pill, so it has no back arrow.
    private bool Reopened(PaletteSession session, Shut shut)
    {
        Type(session, null, shut);
        Face(session);
        return true;
    }

    private sealed record Shut(ToolQuestion Question, Task Sending, string Typed, (float Scroll, bool Pinned) Scrolled);

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
