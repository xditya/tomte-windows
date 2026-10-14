using System.Numerics;
using Tomte.Core;
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
/// the new panel's size and the new body fades in once the shape is 60% of the way there.
/// </summary>
internal sealed class PanelHost
{
    // Content fades in over 0.15 s and out over 0.1 s.
    public const int FadeInMs = 150, FadeOutMs = 100;
    private const int WheelNotch = 120;

    private readonly IslandWindow island;
    private readonly IslandStateMachine machine;
    private readonly Action<string>? log;
    private readonly PanelTabs tabs = new();
    private readonly Dictionary<PanelId, IPanel> panels = [];
    private readonly ScalarKeyFrameAnimation fadeIn, fadeOut;
    private readonly DispatcherQueueTimer release;
    private IPanel? shown, leaving;
    private ContainerVisual? body, leavingBody;
    private Vector2 bodySize;
    private bool fadeInPending;
    private int wheel;

    public PanelHost(IslandWindow island, IslandStateMachine machine, Action<string>? log = null)
    {
        this.island = island;
        this.machine = machine;
        this.log = log;
        fadeIn = Fade(island.Compositor, 1, FadeInMs);
        fadeOut = Fade(island.Compositor, 0, FadeOutMs);
        release = AfterFadeOut(island, FinishLeaving);
        machine.Changed += OnState;
        island.ContentReady += OnContentReady;
        island.Pressed += OnPressed;
        island.Wheel += OnWheel;
    }

    /// <summary>Called when the current panel changes while Panels is open: the triggers re-enter Panels and morph the shape.</summary>
    public Action? Resize { get; set; }

    /// <summary>The current panel's size in DIPs, before the monitor cap (<see cref="PanelSize.Fit"/>).</summary>
    public (double W, double H) Size => PanelSize.Of(tabs.Current ?? PanelId.Shelf);

    public void Register(IPanel panel)
    {
        tabs.Register(panel.Id);
        panels.Add(panel.Id, panel);
    }

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
            (leaving, leavingBody, shown, body) = (shown, body, null, null);
            leavingBody!.StartAnimation("Opacity", fadeOut);
            release.Start();
        }

        if (next is null)
        {
            return;
        }

        var (width, height) = PanelSize.Fit(Size, island.MonitorSize);
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
            next.Shown(body, bodySize);
        }
        catch (Exception e)
        {
            log?.Invoke($"panels: {next.Id} failed to show: {e}");
        }
    }

    private void FinishLeaving()
    {
        release.Stop();
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
        }
    }

    private void OnPressed(Vector2 point)
    {
        if (shown is not null && InBody(point, out var at))
        {
            shown.Press(at);
        }
    }

    /// <summary>
    /// Over the tab strip the wheel switches tabs: a notch away from the user is the previous tab, toward
    /// the user the next, and a touchpad's small deltas add up. Over the body it is the panel's, for its
    /// lists to scroll.
    /// </summary>
    private void OnWheel(Vector2 point, int delta)
    {
        if (machine.State != IslandState.Panels || shown is null)
        {
            wheel = 0;
            return;
        }

        if (point.Y >= PanelSpace.TabStripHeight)
        {
            wheel = 0;
            if (InBody(point, out var at))
            {
                shown.Wheel(at, delta);
            }

            return;
        }

        wheel += delta;
        while (Math.Abs(wheel) >= WheelNotch)
        {
            Step(wheel > 0 ? -1 : 1);
            wheel -= Math.Sign(wheel) * WheelNotch;
        }
    }

    private bool InBody(Vector2 island, out Vector2 at)
    {
        at = PanelSpace.ToBody(island, bodySize.X);
        return at.X >= 0 && at.Y >= 0 && at.X < bodySize.X && at.Y < bodySize.Y;
    }
}
