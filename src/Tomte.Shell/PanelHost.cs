using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Text;
using Windows.System;
using Windows.UI.Composition;

namespace Tomte.Shell;

/// <summary>
/// One tab of the Panels state. The host owns the tab strip row at the top (the mascot sits in it) and
/// gives the panel the body under it. Everything is in DIPs with the origin at the body's top-left
/// corner; the host carries the monitor's scale, so a panel never sees pixels.
/// </summary>
internal interface IPanel
{
    /// <summary>Which tab this is. Its size is <see cref="PanelSize.Of"/> this id.</summary>
    PanelId Id { get; }

    /// <summary>
    /// The panel is about to show. Build the visuals into <paramref name="body"/>, which is
    /// <paramref name="size"/> DIPs (the panel's size, capped to the monitor, less the tab strip), and
    /// start whatever work the view needs. The host fades the body in and out. This runs before the
    /// shape starts to move, so it must return quickly: start anything slow as a task and build when
    /// it lands.
    /// </summary>
    void Shown(ContainerVisual body, Vector2 size);

    /// <summary>
    /// The same, for the AI box going back to the tab it was opened from: a panel that keeps how it was left (History
    /// with the same conversation open) shows it that way; any other shows as on <see cref="Shown"/>.
    /// </summary>
    void ShownBack(ContainerVisual body, Vector2 size) => Shown(body, size);

    /// <summary>The island hid or another tab took over. Stop the work and release every visual and surface.</summary>
    void Hidden();

    /// <summary>The left button went down at this point of the body.</summary>
    void Press(Vector2 point);

    /// <summary>
    /// The wheel turned over this point of the body: the raw delta, 120 a notch, positive away from the
    /// user. Over the tab strip the wheel switches tabs instead, and never reaches the panel.
    /// </summary>
    void Wheel(Vector2 point, int delta);
}

/// <summary>
/// Says which panel the Panels state shows. Panels register once at start-up; the current one is built
/// when Panels opens and released when the island leaves Panels. Picking another tab while Panels is
/// open is the state table's Panels to Panels transition: the old body fades out, the shape morphs to
/// the new panel's size and the new body fades in once the shape is 60% of the way there. The strip row
/// carries the tabs' labels, which stay through a tab switch and fade with the body when Panels closes.
/// </summary>
internal sealed class PanelHost
{
    // Content fades in over 0.15 s and out over 0.1 s.
    public const int FadeInMs = 150, FadeOutMs = 100;

    private readonly IslandWindow island;
    private readonly IslandStateMachine machine;
    private readonly Action<string>? log;
    private readonly PanelTabs tabs = new();
    private readonly Dictionary<PanelId, IPanel> panels = [];
    private readonly ScalarKeyFrameAnimation fadeIn, fadeOut;
    private readonly DispatcherQueueTimer release;
    private readonly TextRenderer text;
    private IPanel? shown, leaving;
    private ContainerVisual? body, leavingBody;
    private Strip? strip, leavingStrip;
    private Vector2 bodySize;
    private bool fadeInPending, stripFadePending, back;
    private int wheel;

    public PanelHost(IslandWindow island, IslandStateMachine machine, TextRenderer text, Action<string>? log = null)
    {
        this.island = island;
        this.machine = machine;
        this.text = text;
        this.log = log;
        fadeIn = Fade(island.Compositor, 1, FadeInMs);
        fadeOut = Fade(island.Compositor, 0, FadeOutMs);
        release = AfterFadeOut(island, FinishLeaving);
        machine.Changed += OnState;
        island.ContentReady += OnContentReady;
        island.Pressed += OnPressed;
        island.Wheel += OnWheel;
        island.Interaction.SwitchTab = Step;
    }

    /// <summary>Called when the current panel changes while Panels is open: the triggers re-enter Panels and morph the shape.</summary>
    public Action? Resize { get; set; }

    /// <summary>A click on the Ask pill: the triggers open the palette.</summary>
    public Action? Ask { get; set; }

    /// <summary>The current panel's size in DIPs, before the monitor cap (<see cref="PanelSize.Fit"/>).</summary>
    public (double W, double H) Size => PanelSize.Of(tabs.Current ?? PanelId.Shelf);

    public void Register(IPanel panel)
    {
        tabs.Register(panel.Id);
        panels.Add(panel.Id, panel);
    }

    /// <summary>The next state change is the AI box going back to the tab it was opened from (<see cref="IPanel.ShownBack"/>).</summary>
    public void GoingBack() => back = true;

    /// <summary>Make a panel the current one. Open, that is a tab switch; closed, Panels opens on it next time.</summary>
    public void Select(PanelId id)
    {
        if (tabs.Select(id))
        {
            Switched();
        }
    }

    /// <summary>The next tab (+1) or the previous one (-1) while Panels is open.</summary>
    public void Step(int direction)
    {
        if (machine.State == IslandState.Panels && tabs.Step(direction))
        {
            Switched();
        }
    }

    public static ScalarKeyFrameAnimation Fade(Compositor compositor, float to, int milliseconds)
    {
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, to);
        fade.Duration = TimeSpan.FromMilliseconds(milliseconds);
        return fade;
    }

    /// <summary>A one-shot timer that runs <paramref name="then"/> once content has had its fade-out. Start it with the fade.</summary>
    public static DispatcherQueueTimer AfterFadeOut(IslandWindow island, Action then)
    {
        var timer = island.Queue.CreateTimer();
        timer.IsRepeating = false;
        timer.Interval = TimeSpan.FromMilliseconds(FadeOutMs);
        timer.Tick += (_, _) => then();
        return timer;
    }

    private void Switched()
    {
        if (machine.State == IslandState.Panels)
        {
            Resize?.Invoke();
        }
    }

    private void OnState(IslandState from, IslandState to)
    {
        bool again = back;
        back = false;
        var next = to == IslandState.Panels && tabs.Current is { } id ? panels[id] : null;
        if (ReferenceEquals(next, shown))
        {
            return;
        }

        // One body fades out at a time; an earlier one still fading is dropped now.
        FinishLeaving();
        if (shown is not null)
        {
            // The panel is released by a one-shot timer, not when the fade completes: with the display
            // asleep the compositor finishes no animation, and the panel would keep working while hidden.
            if (shown is IInteractive gone)
            {
                island.Interaction.Unregister(gone);
            }

            (leaving, leavingBody, shown, body) = (shown, body, null, null);
            leavingBody!.StartAnimation("Opacity", fadeOut);
            release.Start();
        }

        if (next is null)
        {
            if (strip is not null)
            {
                // The labels fade and go with the body; nothing on them can be clicked meanwhile.
                island.Interaction.Unregister(strip);
                (leavingStrip, strip) = (strip, null);
                leavingStrip.Visual.StartAnimation("Opacity", fadeOut);
            }

            return;
        }

        var (width, height) = PanelSize.Fit(Size, island.MonitorSize);
        if (strip is null)
        {
            strip = new Strip(island, text, Select, () => Ask?.Invoke());
            strip.Visual.Opacity = 0;
            stripFadePending = true;
            // From the shape's live body edge, as the strip is drawn, so a press mid-morph finds what is under it.
            island.Interaction.Register(strip, p => new Vector2(PanelSpace.ToStrip(p, island.Current), p.Y), p => new Vector2(p.X - ((float)island.Current.Width / 2), p.Y));
        }

        // On a tab switch the strip stays and the highlight moves; the labels are set for the new width.
        strip.Show(tabs.Order, tabs.Current, (float)width, island.Mascot.Enabled);
        bodySize = new Vector2((float)width, (float)height - PanelSpace.TabStripHeight);
        body = island.Compositor.CreateContainerVisual();
        body.Offset = new Vector3(-bodySize.X / 2, PanelSpace.TabStripHeight, 0);
        body.Opacity = 0;
        island.Dips.Children.InsertAtTop(body);
        shown = next;
        fadeInPending = true;
        log?.Invoke($"panels: {next.Id} shown, body {bodySize.X} x {bodySize.Y}");
        // This runs inside the state change, before the shape moves: a panel that throws must not stop
        // the morph and leave the machine in Panels with nothing on screen.
        try
        {
            if (again)
            {
                next.ShownBack(body, bodySize);
            }
            else
            {
                next.Shown(body, bodySize);
            }
        }
        catch (Exception e)
        {
            log?.Invoke($"panels: {next.Id} failed to show: {e}");
        }
    }

    private void FinishLeaving()
    {
        release.Stop();
        leavingStrip?.Dispose();
        leavingStrip = null;
        if (leaving is null)
        {
            return;
        }

        log?.Invoke($"panels: {leaving.Id} released");
        try
        {
            leaving.Hidden();
        }
        catch (Exception e)
        {
            log?.Invoke($"panels: {leaving.Id} failed to hide: {e}");
        }

        island.Dips.Children.Remove(leavingBody);
        leavingBody!.Dispose();
        (leaving, leavingBody) = (null, null);
    }

    private void OnContentReady()
    {
        if (fadeInPending && body is not null)
        {
            fadeInPending = false;
            body.StartAnimation("Opacity", fadeIn);
            // Its targets go live as it starts to show, as presses on the body do, in body space.
            if (shown is IInteractive view)
            {
                float width = bodySize.X;
                island.Interaction.Register(view, p => PanelSpace.ToBody(p, width), p => new(p.X - (width / 2), p.Y + PanelSpace.TabStripHeight));
            }

            if (stripFadePending && strip is not null)
            {
                stripFadePending = false;
                strip.Visual.StartAnimation("Opacity", fadeIn);
            }
        }
    }

    /// <summary>
    /// A press on the body that no registered target took is the panel's once the body shows, so a quick click after
    /// opening or switching cannot hit a button that is not drawn yet. The strip's tabs and Ask pill are targets.
    /// </summary>
    private void OnPressed(Vector2 point)
    {
        if (shown is null || point.Y < PanelSpace.TabStripHeight)
        {
            return;
        }

        if (!fadeInPending && InBody(point, out var at))
        {
            shown.Press(at);
        }
    }

    /// <summary>
    /// Over the tab strip the wheel switches tabs (<see cref="PanelSpace.TabStep"/>). Over the body it is the
    /// panel's, for its lists to scroll. Until the new body starts to show, the wheel is nobody's: one notch
    /// comes as several messages, and the Compact pill that a scroll opens Panels from sits over the strip.
    /// </summary>
    private void OnWheel(Vector2 point, int delta)
    {
        if (machine.State != IslandState.Panels || shown is null)
        {
            wheel = 0;
            return;
        }

        if (point.Y < PanelSpace.TabStripHeight)
        {
            Step(PanelSpace.TabStep(ref wheel, delta, fadeInPending));
            return;
        }

        wheel = 0;
        if (!fadeInPending && InBody(point, out var at))
        {
            shown.Wheel(at, delta);
        }
    }

    private bool InBody(Vector2 island, out Vector2 at)
    {
        at = PanelSpace.ToBody(island, bodySize.X);
        return at.X >= 0 && at.Y >= 0 && at.X < bodySize.X && at.Y < bodySize.Y;
    }

    /// <summary>
    /// The tab labels in the strip row, with a card behind the current one, and the Ask pill at its right end. The
    /// container sits on the shape's live body left edge in every frame of a morph (island space's origin less its own
    /// offset, plus the ramp an attached shape flares into the top edge with, whole pixels), so the labels move with the
    /// edge they are laid out from; the Ask pill rides the live right edge the same way. Its targets are the tab labels,
    /// one group the arrow keys switch along, and the Ask pill, the one control here that opens the palette. The tomte is
    /// no target: like the rest of the row it moves the island. The wheel is the host's.
    /// </summary>
    private sealed class Strip : IDisposable, IInteractive
    {
        private const string AskId = "ask";
        private readonly IslandWindow island;
        private readonly Action<PanelId> select;
        private readonly Action openAsk;
        private readonly TextRenderer text;
        private readonly CompositionRoundedRectangleGeometry pill, askCard;
        private readonly CompositionContainerShape sparkle;
        private readonly ShapeVisual askVisual;
        private readonly ExpressionAnimation askEdge;
        private readonly TextVisual ask;
        private readonly List<TextVisual> labels = [];
        private Target[] targets = [];
        private float askWidth, askFor;

        public Strip(IslandWindow island, TextRenderer text, Action<PanelId> select, Action onAsk)
        {
            this.island = island;
            this.text = text;
            this.select = select;
            openAsk = onAsk;
            var compositor = island.Compositor;
            Visual = compositor.CreateShapeVisual();
            var edge = compositor.CreateExpressionAnimation("Vector3((Floor(live.RampW * dips.Scale.X + 0.5) - dips.Offset.X) / dips.Scale.X, 0, 0)");
            edge.SetReferenceParameter("dips", island.Dips);
            edge.SetReferenceParameter("live", island.Live);
            Visual.StartAnimation("Offset", edge);
            pill = compositor.CreateRoundedRectangleGeometry();
            pill.CornerRadius = new Vector2(TabStrip.Corner);
            var shape = compositor.CreateSpriteShape(pill);
            shape.FillBrush = compositor.CreateColorBrush(Windows.UI.Color.FromArgb(28, 255, 255, 255));
            Visual.Shapes.Add(shape);

            // The Ask pill in a visual of its own, "right" DIPs (its width and the right padding) short of the body's
            // live right edge, which is as far from island space's origin as the left one.
            askVisual = compositor.CreateShapeVisual();
            askEdge = compositor.CreateExpressionAnimation(
                "Vector3((dips.Offset.X * 2 - Floor(live.RampW * dips.Scale.X + 0.5) * 2 - Floor(right * dips.Scale.X + 0.5)) / dips.Scale.X, 0, 0)");
            askEdge.SetReferenceParameter("dips", island.Dips);
            askEdge.SetReferenceParameter("live", island.Live);
            askCard = compositor.CreateRoundedRectangleGeometry();
            askCard.CornerRadius = new Vector2(TabStrip.Corner);
            var card = compositor.CreateSpriteShape(askCard);
            card.FillBrush = compositor.CreateColorBrush(Argb(TabStrip.AskCard));
            askVisual.Shapes.Add(card);
            sparkle = compositor.CreateContainerShape();
            var ink = compositor.CreateColorBrush(Argb(TabStrip.AskColor));
            foreach (var star in TabStrip.Sparkle)
            {
                var figure = compositor.CreateSpriteShape(compositor.CreatePathGeometry(IslandWindow.BuildPath(star, 1)));
                figure.FillBrush = ink;
                sparkle.Shapes.Add(figure);
            }

            askVisual.Shapes.Add(sparkle);
            Visual.Children.InsertAtTop(askVisual);
            ask = text.Create(askVisual);
            island.Dips.Children.InsertAtTop(Visual);
        }

        public event Action? TargetsChanged;

        public ShapeVisual Visual { get; }

        /// <summary>The tab labels and the Ask pill, which rides the live right edge as it is drawn.</summary>
        public IReadOnlyList<Target> Targets
        {
            get
            {
                float width = (float)island.Current.Width;
                if (targets.Length > 0 && width != askFor)
                {
                    askFor = width;
                    targets[^1] = targets[^1] with { Bounds = AskCard(width) };
                }

                return targets;
            }
        }

        private RectangleF AskCard(float width) =>
            new(width - TabStrip.RightPad - askWidth, (PanelSpace.TabStripHeight - TabStrip.Height) / 2, askWidth, TabStrip.Height);

        public void Activate(string id)
        {
            if (id == AskId)
            {
                openAsk();
            }
            else if (Enum.TryParse<PanelId>(id, out var tab))
            {
                select(tab);
            }
        }

        public void Show(IEnumerable<PanelId> tabs, PanelId? current, float width, bool mascot)
        {
            var lines = TabStrip.Labels(tabs, current, width, mascot);
            var list = new List<Target>();
            foreach (var (id, line) in tabs.Zip(lines))
            {
                list.Add(new(id.ToString(), line.Slot, TabStrip.Corner, TargetKind.Tab, Group: 1, Checked: id == current));
            }

            Visual.Size = new Vector2(width, PanelSpace.TabStripHeight);
            while (labels.Count < lines.Count)
            {
                labels.Add(text.Create(Visual));
            }

            // New labels go on top; the Ask pill stays over them while a morph slides it across their row.
            Visual.Children.Remove(askVisual);
            Visual.Children.InsertAtTop(askVisual);

            for (int i = 0; i < labels.Count; i++)
            {
                labels[i].Show(i < lines.Count ? lines[i] : new TextSlot("", TabStrip.Label, default));
            }

            var on = lines.FirstOrDefault(line => line.Style == TabStrip.Current).Slot;
            pill.Offset = new Vector2(on.X, on.Y);
            pill.Size = new Vector2(on.Width, on.Height);

            // Laid out from the card's left edge, which the expression keeps on the live right end.
            var (card, glyph, word) = TabStrip.AskPill(width);
            askWidth = card.Width;
            askFor = (float)island.Current.Width;
            list.Add(new(AskId, AskCard(askFor), TabStrip.Corner, TargetKind.Button, Tip: "Ask Tomte anything"));
            targets = [.. list];
            askVisual.Size = new Vector2(card.Width, PanelSpace.TabStripHeight);
            askEdge.SetScalarParameter("right", width - card.X);
            askVisual.StartAnimation("Offset", askEdge);
            askCard.Offset = new Vector2(0, card.Y);
            askCard.Size = new Vector2(card.Width, card.Height);
            sparkle.Offset = new Vector2(glyph.X - card.X, glyph.Y);
            sparkle.Scale = new Vector2(glyph.Height / TabStrip.SparkleBox.Y);
            ask.Show(word.Text, word.Style, new System.Drawing.RectangleF(word.Slot.X - card.X, word.Slot.Y, word.Slot.Width, word.Slot.Height));
            TargetsChanged?.Invoke();
        }

        public void Dispose()
        {
            foreach (var label in labels)
            {
                label.Dispose();
            }

            ask.Dispose();
            askVisual.Dispose();
            Visual.Parent?.Children.Remove(Visual);
            Visual.Dispose();
        }

        private static Windows.UI.Color Argb(uint c) => Windows.UI.Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
    }
}
