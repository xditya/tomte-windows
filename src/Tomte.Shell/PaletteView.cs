using System.Drawing;
using System.Numerics;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Text;
using Windows.UI.Composition;
using Color = Windows.UI.Color;

namespace Tomte.Shell;

/// <summary>
/// The palette's content on the island, laid out by <see cref="PaletteLayout"/> in a container in island space: the
/// prompt box, a text box when a session can send and the "AI layer is off" line when none can; the chips, which a
/// click turns on and off; the answer as it streams, drawn by the text renderer; and the footer with where the
/// request goes and what today cost. It fades in with the shape and goes when the island leaves the Palette state.
/// The answer is plain text runs, not a read-only text box: the palette needs no selection in it, and a third set
/// of text services would cost a module instance, input routing and a surface for nothing Tomte needs.
/// </summary>
internal sealed class PaletteView : IDisposable
{
    private static readonly Color Card = Color.FromArgb(15, 255, 255, 255), Line = Color.FromArgb(26, 255, 255, 255);
    private static readonly Color ChipOff = Color.FromArgb(15, 255, 255, 255), ChipOn = Color.FromArgb(51, 111, 168, 255);

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
    private readonly EditorView? prompt;
    private Action? retract;
    private bool fadeInPending = true, disposed;

    /// <param name="send">What Enter does with the prompt's text; null for a palette that only shows (a driven run).</param>
    public PaletteView(IslandWindow island, IslandStateMachine machine, TextRenderer text, TypingFocus? focus, IPaletteSession? session, float width, Action<string>? send)
    {
        this.island = island;
        this.machine = machine;
        this.text = text;
        this.focus = focus;
        this.session = session;
        this.width = width;
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
        placeholder = text.Create(root);
        provider = text.Create(root);
        spend = text.Create(root);
        if (session is not null && focus is not null)
        {
            prompt = focus.Create(root, multiline: true, PaletteLayout.PromptSize);
            prompt.Services.Text = session.Prompt;
            if (send is not null)
            {
                prompt.SendOnEnter(send);
            }

            // Inside the services' own notifications nothing may call back into them: the hint only, and a relayout later.
            prompt.Services.Changed += Hint;
            prompt.Services.Resized += Resize;
        }

        island.Dips.Children.InsertAtTop(root);
        island.ContentReady += FadeIn;
        island.Pressed += OnPressed;
        machine.Changed += OnState;
        if (session is not null)
        {
            session.Changed += Update;
        }

        Update();
    }

    /// <summary>The palette's height for what it shows now, in DIPs.</summary>
    public float Height { get; private set; }

    /// <summary>The height changed with the prompt's lines or the answer; the palette morphs to it, gently.</summary>
    public event Action? HeightChanged;

    /// <summary>The prompt takes the keys, after the palette hotkey. False when there is no prompt or Windows refused.</summary>
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

        // The chips are measured again only when the prompt's height or a chip changed, not for every streamed word.
        float promptText = prompt?.ContentHeight ?? 0;
        List<(string, bool)> chips = session is null ? [] : [.. session.Chips.Select(chip => (chip.Label, chip.On && chip.Available))];
        if (promptText != laidText || !chips.SequenceEqual(laidChips))
        {
            (laidText, laidChips) = (promptText, chips);
            layout = new PaletteLayout(width, island.Mascot.Enabled, promptText, chips);
        }

        var box = layout.Prompt;
        card.Offset = new Vector2(box.X, box.Y);
        card.Size = new Vector2(box.Width, box.Height);
        prompt?.Place(layout.Editor, island.Scale);
        Hint();

        if (session is null)
        {
            // No provider: what the palette needs, under the box, and nothing to type in.
            Show(0, new TextRun(PaletteLayout.Setup, TextStyle.Secondary, layout.AnswerWidth), new Vector2(PaletteLayout.Pad, layout.ChipsTop + 4));
            Trim(1);
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

        var blocks = Blocks(session);
        float height = layout.Height(layout.Measure(blocks));
        var answer = layout.Answer(blocks, height);
        for (int i = 0; i < answer.Count; i++)
        {
            Show(i, answer[i].Run, answer[i].At);
        }

        Trim(answer.Count);
        var foot = layout.Foot(session.ProviderIndicator, session.ToolSteps, session.SpentTodayUsd, session.DailyCapUsd, height);
        provider.Show(foot[0]);
        spend.Show(foot[1]);
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
        machine.Changed -= OnState;
        if (session is not null)
        {
            session.Changed -= Update;
        }

        if (retract is not null)
        {
            focus!.Lost -= retract;
        }

        prompt?.Dispose();
        foreach (var visual in labels.Concat(runs).Append(placeholder).Append(provider).Append(spend))
        {
            visual.Dispose();
        }

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
            runs.Add(text.Create(root));
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

    // A press on a chip turns it on or off; a press on the prompt is the typing focus's.
    private void OnPressed(Vector2 at)
    {
        if (session is null || session.Status == PaletteStatus.Sending || machine.State != IslandState.Palette)
        {
            return;
        }

        int i = layout.ChipAt(at - new Vector2(root.Offset.X, root.Offset.Y));
        if (i >= 0)
        {
            var chip = session.Chips[i];
            chip.On = !chip.On;
            Update();
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
    /// The palette hotkey, once the triggers have opened the Palette state: the prompt box takes the keys at once, while
    /// the hotkey is the last input, and Enter sends. Esc and focus loss while nothing is being sent hide the island.
    /// With no session, because no provider is set up, the palette says the AI layer is off and takes no keys.
    /// </summary>
    public void Type(PaletteSession? session)
    {
        // The triggers opened the palette at its smallest; growth starts from there.
        _targetHeight = PanelSize.PaletteMinHeight;
        PaletteView? view = null;
        view = new PaletteView(island, machine, text!, focus, session, (float)Width, session is null ? null : typed => _ = SendAsync(session, typed));
        view.HeightChanged += () => Grow(view.Height);
        if (hide is not null)
        {
            view.RetractOnFocusLoss(hide);
        }

        view.TakeKeys();
        Grow(view.Height);
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
