using System.Drawing;
using System.Numerics;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Device;
using Tomte.Core.Text;
using Windows.UI.Composition;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>
/// The palette's content on the island, laid out by <see cref="PaletteLayout"/> in a container in island space: the
/// prompt box, a text box when a session can send, what was said when the palette has no box (voice), and the "AI
/// layer is off" line when nothing can send; the chips, which a click turns on and off; the answer as it streams,
/// drawn by the text renderer in a band that scrolls with the wheel and follows the newest line until the user
/// scrolls up; a tool's question with Run, Cancel and, where the tier allows it, Always allow; and the footer with
/// where the request goes and what today cost. It fades in with the shape and goes when the island leaves the
/// Palette state.
/// The answer is plain text runs, not a read-only text box: nothing asks for selecting or copying it, and a third
/// set of text services would cost a module instance, input routing and a surface for that alone.
/// </summary>
internal sealed class PaletteView : IDisposable
{
    private static readonly Color Card = Color.FromArgb(15, 255, 255, 255), Line = Color.FromArgb(26, 255, 255, 255);
    private static readonly Color ChipOff = Color.FromArgb(15, 255, 255, 255), ChipOn = Color.FromArgb(51, 111, 168, 255);

    // The question's card carries the chip's blue in its outline so it reads as waiting for the user; Run is the
    // filled button, in the warning colour for what is asked every time.
    private static readonly Color AskOutline = Color.FromArgb(102, 111, 168, 255), ButtonOff = Color.FromArgb(26, 255, 255, 255);
    private static readonly Color RunFill = Color.FromArgb(255, 255, 255, 255), RunWarn = Color.FromArgb(255, 0xF0, 0xB3, 0x5A);

    private readonly IslandWindow island;
    private readonly IslandStateMachine machine;
    private readonly TextRenderer text;
    private readonly TypingFocus? focus;
    private readonly IPaletteSession? session;
    private readonly float width;
    private PaletteLayout layout;
    private float laidText = -1;
    private List<(string, bool)> laidChips = [];
    private readonly ContainerVisual root;
    private readonly ShapeVisual shapes;
    private readonly CompositionRoundedRectangleGeometry card;
    private readonly CompositionSpriteShape cardShape;
    private readonly List<(CompositionRoundedRectangleGeometry Geometry, CompositionColorBrush Brush)> pills = [];
    private readonly List<TextVisual> labels = [], runs = [];
    private readonly TextVisual placeholder, provider, spend;
    private readonly ContainerVisual band, scrolled;
    private readonly EditorView? prompt;
    private readonly ToolQuestion? question;
    private readonly bool typed;
    private TextVisual? setup;
    private float answerHeight, scroll;
    private bool pinned = true;
    private Action? retract;
    private bool fadeInPending = true, disposed;
    private ToolRequest? laidAsk;
    private Confirmation? confirm;
    private CompositionRoundedRectangleGeometry? askCard;
    private TextVisual? askSummary, askLine;
    private readonly List<(CompositionRoundedRectangleGeometry Geometry, CompositionColorBrush Brush, TextVisual Label)> buttons = [];

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
        typed = send is not null;
        layout = new PaletteLayout(width, island.Mascot.Enabled);
        var c = island.Compositor;
        root = c.CreateContainerVisual();
        root.Offset = new Vector3(-width / 2, 0, 0);
        root.Opacity = 0;
        shapes = c.CreateShapeVisual();
        shapes.Size = new Vector2(width, (float)PanelSize.PaletteMaxHeight);
        root.Children.InsertAtTop(shapes);
        card = c.CreateRoundedRectangleGeometry();
        card.CornerRadius = new Vector2(PaletteLayout.PromptCorner);
        cardShape = c.CreateSpriteShape(card);
        cardShape.FillBrush = c.CreateColorBrush(Card);
        cardShape.StrokeBrush = c.CreateColorBrush(Line);
        cardShape.StrokeThickness = 1;
        shapes.Shapes.Add(cardShape);
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

            // Inside the services' own notifications nothing may call back into them: the hint only, and a relayout later.
            prompt.Services.Changed += Hint;
            prompt.Services.Resized += Resize;
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

        Update();
    }

    /// <summary>The palette's height for what it shows now, in DIPs.</summary>
    public float Height { get; private set; }

    /// <summary>The height changed with the prompt's lines or the answer; the palette morphs to it, gently.</summary>
    public event Action? HeightChanged;

    /// <summary>The prompt takes the keys, after the palette hotkey or a click on the tomte. False when there is no prompt or Windows refused.</summary>
    public bool TakeKeys() => prompt is not null && focus!.Take(prompt);

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
        float promptText = prompt?.ContentHeight ?? 0;
        List<(string, bool)> chips = session is null ? [] : [.. session.Chips.Select(chip => (chip.Label, chip.On && chip.Available))];
        var asked = question?.Asked;
        if (promptText != laidText || !chips.SequenceEqual(laidChips) || !ReferenceEquals(asked, laidAsk))
        {
            (laidText, laidChips, laidAsk) = (promptText, chips, asked);
            confirm = asked is null ? null : new Confirmation(asked.Summary, Confirmation.LineFor(asked.Tier, typed), asked.OfferAlwaysAllow, asked.Tier == ActionTier.Destructive);
            layout = new PaletteLayout(width, island.Mascot.Enabled, promptText, chips, confirm);
        }

        var box = layout.Prompt;
        card.Offset = new Vector2(box.X, box.Y);
        card.Size = new Vector2(box.Width, box.Height);
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

        var chipRow = layout.Chips;
        while (pills.Count < chipRow.Count)
        {
            var geometry = island.Compositor.CreateRoundedRectangleGeometry();
            geometry.CornerRadius = new Vector2(PaletteLayout.ChipCorner);
            var brush = island.Compositor.CreateColorBrush(ChipOff);
            var shape = island.Compositor.CreateSpriteShape(geometry);
            shape.FillBrush = brush;
            shapes.Shapes.Add(shape);
            pills.Add((geometry, brush));
            labels.Add(text.Create(root));
        }

        for (int i = 0; i < pills.Count; i++)
        {
            var (geometry, brush) = pills[i];
            bool shown = i < chipRow.Count;
            geometry.Offset = shown ? new Vector2(chipRow[i].Box.X, chipRow[i].Box.Y) : default;
            geometry.Size = shown ? new Vector2(chipRow[i].Box.Width, chipRow[i].Box.Height) : default;
            brush.Color = shown && chipRow[i].Label.Style == PaletteLayout.ChipOn ? ChipOn : ChipOff;
            labels[i].Show(shown ? chipRow[i].Label : new TextSlot("", PaletteLayout.Chip, default));
        }

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
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
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

        if (retract is not null)
        {
            focus!.Lost -= retract;
        }

        prompt?.Dispose();
        foreach (var visual in labels.Concat(runs).Concat(buttons.Select(b => b.Label)).Append(placeholder).Append(provider).Append(spend))
        {
            visual.Dispose();
        }

        setup?.Dispose();
        askSummary?.Dispose();
        askLine?.Dispose();
        scrolled.Dispose();
        band.Dispose();
        island.Dips.Children.Remove(root);
        shapes.Dispose();
        root.Dispose();
    }

    // The hint shows while the prompt is empty, as an input's placeholder does.
    private void Hint()
    {
        if (disposed)
        {
            return;
        }

        var box = layout.Prompt;
        var editorBox = layout.Editor;
        bool empty = prompt is null || prompt.Services.Text.Length == 0;
        placeholder.Show(empty ? (session is null ? PaletteLayout.Off : PaletteLayout.Hint) : "", PaletteLayout.Placeholder, new RectangleF(editorBox.X, box.Y, editorBox.Width, box.Height));
    }

    // The open question's card, summary, line and buttons, made on the first question; with none they are emptied.
    private void ShowAsk(float height)
    {
        var ask = layout.Ask(height);
        if (ask is null && askCard is null)
        {
            return;
        }

        if (askCard is null)
        {
            var c = island.Compositor;
            askCard = c.CreateRoundedRectangleGeometry();
            askCard.CornerRadius = new Vector2(PaletteLayout.PromptCorner);
            var shape = c.CreateSpriteShape(askCard);
            shape.FillBrush = c.CreateColorBrush(Card);
            shape.StrokeBrush = c.CreateColorBrush(AskOutline);
            shape.StrokeThickness = 1;
            shapes.Shapes.Add(shape);
            askSummary = text.Create(root);
            askLine = text.Create(root);
            for (int i = 0; i < 3; i++)
            {
                var geometry = c.CreateRoundedRectangleGeometry();
                geometry.CornerRadius = new Vector2(PaletteLayout.ButtonCorner);
                var brush = c.CreateColorBrush(ButtonOff);
                var button = c.CreateSpriteShape(geometry);
                button.FillBrush = brush;
                shapes.Shapes.Add(button);
                buttons.Add((geometry, brush, text.Create(root)));
            }
        }

        askCard.Offset = ask is { } a ? new Vector2(a.Card.X, a.Card.Y) : default;
        askCard.Size = ask is { } b ? new Vector2(b.Card.Width, b.Card.Height) : default;
        askSummary!.Show(ask?.Summary ?? new TextRun("", PaletteLayout.AskSummary, 1), ask?.SummaryAt ?? default);
        askLine!.Show(ask?.Line ?? new TextSlot("", PaletteLayout.AskLine, default));
        var shown = ask?.Buttons ?? [];
        for (int i = 0; i < buttons.Count; i++)
        {
            var (geometry, brush, label) = buttons[i];
            bool on = i < shown.Count;
            geometry.Offset = on ? new Vector2(shown[i].Box.X, shown[i].Box.Y) : default;
            geometry.Size = on ? new Vector2(shown[i].Box.Width, shown[i].Box.Height) : default;
            brush.Color = on && shown[i].Button == ConfirmButton.Run ? (confirm!.Destructive ? RunWarn : RunFill) : ButtonOff;
            label.Show(on ? shown[i].Label : new TextSlot("", PaletteLayout.Button, default));
        }
    }

    // The open question's answer from a button, Enter or Esc; false when no question is open.
    private bool Answer(ConfirmButton button) =>
        question?.Asked is { } asked && question.Answer(button switch
        {
            ConfirmButton.Run => PermissionAnswer.AllowOnce,
            ConfirmButton.AlwaysAllow => PermissionAnswer.AlwaysAllow,
            _ => PermissionAnswer.Deny,
        }, asked);

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

    // A press on a question's button answers it; a press on a chip turns it on or off; a press on the prompt is the
    // typing focus's.
    private void OnPressed(Vector2 at)
    {
        if (disposed || session is null || machine.State != IslandState.Palette)
        {
            return;
        }

        var local = at - new Vector2(root.Offset.X, root.Offset.Y);
        if (layout.ButtonAt(local, Height) is { } button)
        {
            Answer(button);
            return;
        }

        if (session.Status == PaletteStatus.Sending)
        {
            return;
        }

        int i = layout.ChipAt(local);
        if (i >= 0)
        {
            var chip = session.Chips[i];
            chip.On = !chip.On;
            Update();
        }
        else if (prompt is not null && layout.Prompt.Contains(local.X, local.Y) && !prompt.Contains(at))
        {
            // The card's padding around the text box looks like part of it; the box itself takes its own presses.
            focus?.Take(prompt);
        }
    }

    private void OnState(IslandState from, IslandState to)
    {
        if (to == IslandState.Palette)
        {
            return;
        }

        // Out with the shape: the content fades over 0.1 s, then goes; a timer, because a sleeping display finishes no animation.
        machine.Changed -= OnState;
        root.StartAnimation("Opacity", PanelHost.Fade(island.Compositor, 0, PanelHost.FadeOutMs));
        PanelHost.AfterFadeOut(island, Dispose).Start();
    }
}

/// <summary>The palette hotkey's half of the host: the prompt box that takes the keys.</summary>
internal sealed partial class PaletteHost
{
    /// <summary>
    /// The palette hotkey or a click on the tomte, once the triggers have opened the Palette state: the prompt box takes
    /// the keys at once, while that press is the last input, and Enter sends. A tool that asks shows its question with
    /// Run, Cancel and, where its tier allows, Always allow; Enter and Esc answer it while the box has the keys. Esc and
    /// focus loss while nothing is being sent hide the island; leaving the palette cancels what is in flight (an open
    /// question is answered no), then stops the MCP servers and the UI Automation thread the session started. With no
    /// session, because no provider is set up, the palette says the AI layer is off and takes no keys.
    /// </summary>
    public void Type(PaletteSession? session)
    {
        // The triggers opened the palette at its smallest; growth starts from there.
        _targetHeight = PanelSize.PaletteMinHeight;
        var question = new ToolQuestion();
        Task sending = Task.CompletedTask;
        PaletteView? view = null;
        view = new PaletteView(island, machine, text, focus, session, (float)Width, session is null ? null : Entered, question);
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
            var send = SendAsync(session!, typed);
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

    // Once the last send has ended, nothing uses the session's servers or its automation thread any more.
    private async Task ReleaseAsync(PaletteSession session, Task sending)
    {
        await sending;
        session.Automation?.Dispose();
        if (session.Mcp is { } mcp)
        {
            await mcp.DisposeAsync();
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
