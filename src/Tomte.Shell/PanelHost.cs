using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Text;
using Windows.System;
using Windows.UI.Composition;

namespace Tomte.Shell;

/// <summary>
/// One tab of the Panels state. The host owns the tab row at the top (the mascot sits in it) and
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
        // Once the island has gone, it opens on the first tab (Home) again; a tab asked for by name is selected after.
        island.WentHidden += () =>
        {
            if (machine.State == IslandState.Hidden)
            {
                tabs.Reset();
            }
        };
    }

    /// <summary>Called when the current panel changes while Panels is open: the triggers re-enter Panels and morph the shape.</summary>
    public Action? Resize { get; set; }

    /// <summary>Ctrl+K or "/" on the open panels: the triggers open the palette.</summary>
    public Action? Ask { get; set; }

    /// <summary>A click on the settings button: the island goes and the settings window opens.</summary>
    public Action? Settings { get; set; }

    /// <summary>What is on the shelf, for the Shelf tab's count; 0 until the shelf has been read.</summary>
    public Func<int>? ShelfCount { get; set; }

    /// <summary>A click on the search button: the triggers open the search box.</summary>
    public Action? Search { get; set; }

    /// <summary>The search shortcut as set in Settings, which the search button's tooltip names.</summary>
    public Func<string>? SearchKey { get; set; }

    /// <summary>The current panel's size in DIPs, before the monitor cap (<see cref="PanelSize.Fit"/>).</summary>
    public (double W, double H) Size => PanelSize.Of(tabs.Current ?? PanelId.Home);

    public void Register(IPanel panel)
    {
        tabs.Register(panel.Id);
        panels.Add(panel.Id, panel);
    }

    /// <summary>The next state change is the AI box going back to the tab it was opened from (<see cref="IPanel.ShownBack"/>).</summary>
    public void GoingBack() => back = true;

    /// <summary>The tab Panels shows, or opens on next time.</summary>
    public PanelId? Current => tabs.Current;

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

    /// <summary>The shelf changed: the Shelf tab's count follows while the panels show.</summary>
    public void ShelfChanged()
    {
        if (strip is not null && body is not null)
        {
            ShowStrip(bodySize.X);
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
            strip = new Strip(island, text, Select, () => Ask?.Invoke(), () => Search?.Invoke(), () => Settings?.Invoke());
            strip.Visual.Opacity = 0;
            stripFadePending = true;
            // From the shape's live body edge, as the strip is drawn, so a press mid-morph finds what is under it.
            island.Interaction.Register(strip, p => new Vector2(PanelSpace.ToStrip(p, island.Current), p.Y), p => new Vector2(p.X - ((float)island.Current.Width / 2), p.Y));
        }

        // On a tab switch the strip stays and the highlight moves; the labels are set for the new width.
        ShowStrip((float)width);
        bodySize = new Vector2((float)width, (float)height - PanelSpace.Head);
        body = island.Compositor.CreateContainerVisual();
        body.Offset = new Vector3(-bodySize.X / 2, PanelSpace.Head, 0);
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

    private void ShowStrip(float width) =>
        strip!.Show(tabs.Order, tabs.Current, width, island.Mascot.Enabled, SearchKey?.Invoke() ?? "", ShelfCount?.Invoke() ?? 0);

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
                island.Interaction.Register(view, p => PanelSpace.ToBody(p, width), p => new(p.X - (width / 2), p.Y + PanelSpace.Head));
                // Opened, back or switched to from the keyboard: the tab's first item takes focus.
                island.Interaction.FocusFirst(view);
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
    /// opening or switching cannot hit a button that is not drawn yet. The strip's tabs and buttons are targets.
    /// </summary>
    private void OnPressed(Vector2 point)
    {
        if (shown is null || point.Y < PanelSpace.Head)
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

        if (point.Y < PanelSpace.Head)
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
    /// The tab row (<see cref="TabStrip"/>): the rail with a capsule behind the current tab, each tab's icon, the current
    /// one's name and the Shelf tab's count, and at the right end the search button and the settings button. The
    /// container sits on the shape's live body left edge in every frame of a morph (island space's origin less its own
    /// offset, plus the ramp an attached shape flares into the top edge with, whole pixels), so the rail moves with the
    /// edge it is laid out from; the two buttons ride the live right edge the same way. Its targets are the tabs, one
    /// group the arrow keys switch along, each named in its tooltip, the search button, which opens the search box, and
    /// the settings button. The tomte is no target: like the rest of the row it moves the island. The wheel is the host's.
    /// </summary>
    private sealed class Strip : IDisposable, IInteractive
    {
        private const string SearchId = "search", SettingsId = "settings";
        private readonly IslandWindow island;
        private readonly Action<PanelId> select;
        private readonly Action openAsk, openSearch, openSettings;
        private readonly TextRenderer text;
        private readonly ShapeVisual right;
        private readonly ExpressionAnimation rightEdge;
        private Parts? parts;
        private TabStrip.Row? row;
        private Target[] targets = [];
        private float laidFor, liveFor;

        public Strip(IslandWindow island, TextRenderer text, Action<PanelId> select, Action onAsk, Action onSearch, Action onSettings)
        {
            this.island = island;
            this.text = text;
            this.select = select;
            openAsk = onAsk;
            openSearch = onSearch;
            openSettings = onSettings;
            var compositor = island.Compositor;
            Visual = compositor.CreateShapeVisual();
            var edge = compositor.CreateExpressionAnimation("Vector3((Floor(live.RampW * dips.Scale.X + 0.5) - dips.Offset.X) / dips.Scale.X, 0, 0)");
            edge.SetReferenceParameter("dips", island.Dips);
            edge.SetReferenceParameter("live", island.Live);
            Visual.StartAnimation("Offset", edge);

            // The two buttons in a visual of their own, "right" DIPs (their width and the right padding) short of the body's
            // live right edge, which is as far from island space's origin as the left one.
            right = compositor.CreateShapeVisual();
            rightEdge = compositor.CreateExpressionAnimation(
                "Vector3((dips.Offset.X * 2 - Floor(live.RampW * dips.Scale.X + 0.5) * 2 - Floor(right * dips.Scale.X + 0.5)) / dips.Scale.X, 0, 0)");
            rightEdge.SetReferenceParameter("dips", island.Dips);
            rightEdge.SetReferenceParameter("live", island.Live);
            Visual.Children.InsertAtTop(right);
            island.Dips.Children.InsertAtTop(Visual);
        }

        public event Action? TargetsChanged;

        public ShapeVisual Visual { get; }

        /// <summary>The tabs, then the search and settings buttons, which ride the live right edge as they are drawn.</summary>
        public IReadOnlyList<Target> Targets
        {
            get
            {
                float live = (float)island.Current.Width;
                if (row is { } r && targets.Length >= 2 && live != liveFor)
                {
                    liveFor = live;
                    targets[^2] = targets[^2] with { Bounds = Shift(r.Search, live) };
                    targets[^1] = targets[^1] with { Bounds = Shift(r.Settings, live) };
                }

                return targets;
            }
        }

        public void Activate(string id)
        {
            if (id == SearchId)
            {
                openSearch();
            }
            else if (id == SettingsId)
            {
                openSettings();
            }
            else if (Enum.TryParse<PanelId>(id, out var tab))
            {
                select(tab);
            }
        }

        /// <summary>
        /// Ctrl+1 to Ctrl+7 pick the tab in that place, and leave the current one's to whoever has the keys there (the
        /// note keeps them); Ctrl+K and "/" ask, as the Ask bar does.
        /// </summary>
        public bool Command(NavKey key)
        {
            if (key == NavKey.Ask)
            {
                openAsk();
                return true;
            }

            var tab = targets.Where(t => t.Kind == TargetKind.Tab).ElementAtOrDefault(key - NavKey.Tab1);
            if (key is < NavKey.Tab1 or > NavKey.Tab7 || tab.Id is null || tab.Checked)
            {
                return false;
            }

            Activate(tab.Id);
            return true;
        }

        public void Show(IReadOnlyCollection<PanelId> tabs, PanelId? current, float width, bool mascot, string searchKey, int shelfCount)
        {
            var r = TabStrip.Layout(tabs, current, width, mascot, searchKey, shelfCount);
            row = r;
            parts?.Dispose();
            var p = parts = new Parts(island.Compositor, text);
            Visual.Shapes.Clear();
            right.Shapes.Clear();
            var card = p.Brush(Theme.Card);
            Visual.Size = new Vector2(width, PanelSpace.Head);
            p.Capsule(Visual, r.Rail, card);
            var list = new List<Target>();
            foreach (var tab in r.Tabs)
            {
                bool on = tab.Id == current;
                if (on)
                {
                    p.Capsule(Visual, tab.Bounds, p.Brush(Theme.RaisedHi));
                }

                p.Glyph(Visual, TabStrip.Glyph(tab.Id), tab.Icon, p.Brush(on ? Theme.Ink : Theme.Ink2));
                if (tab.Label is { } label)
                {
                    p.Text(Visual, label);
                }

                if (tab.Badge is { } badge)
                {
                    p.Capsule(Visual, badge, p.Brush(Theme.Accent));
                    p.Text(Visual, new TextSlot(tab.Count!, TabStrip.BadgeLabel, badge));
                }

                list.Add(new(tab.Id.ToString(), tab.Bounds, TabStrip.TabHeight / 2, TargetKind.Tab, Tip: TabStrip.Name(tab.Id), Group: 1, Checked: on, Key: $"Ctrl+{list.Count + 1}"));
            }

            // Laid out from the search button's left edge, which the expression keeps that far from the live right end.
            float origin = r.Search.X;
            right.Size = new Vector2(r.Settings.Right - origin, PanelSpace.Head);
            rightEdge.SetScalarParameter("right", width - origin);
            right.StartAnimation("Offset", rightEdge);
            p.Capsule(right, Moved(r.Search, -origin), card);
            p.Glyph(right, Glyphs.Search, Moved(r.SearchIcon, -origin), p.Brush(Theme.Ink2));
            if (r.SearchLabel is { } word)
            {
                p.Text(right, word with { Slot = Moved(word.Slot, -origin) });
            }

            if (r.Keycap is { } cap)
            {
                p.Keycap(right, Moved(cap, -origin), r.KeycapText!);
            }

            p.Capsule(right, Moved(r.Settings, -origin), card);
            p.Glyph(right, Glyphs.Settings, Moved(r.SettingsIcon, -origin), p.Brush(Theme.Ink2));

            (laidFor, liveFor) = (width, (float)island.Current.Width);
            list.Add(new(SearchId, Shift(r.Search, liveFor), TabStrip.Button / 2, TargetKind.Button, Tip: TabStrip.SearchTip(searchKey)));
            list.Add(new(SettingsId, Shift(r.Settings, liveFor), TabStrip.Button / 2, TargetKind.Button, Tip: TabStrip.SettingsText));
            targets = [.. list];
            TargetsChanged?.Invoke();
        }

        public void Dispose()
        {
            parts?.Dispose();
            right.Dispose();
            Visual.Parent?.Children.Remove(Visual);
            Visual.Dispose();
        }

        // A rectangle laid out for the panel's width, where the live right edge has it now.
        private RectangleF Shift(RectangleF at, float live) => Moved(at, live - laidFor);

        private static RectangleF Moved(RectangleF at, float dx) => new(at.X + dx, at.Y, at.Width, at.Height);
    }
}
