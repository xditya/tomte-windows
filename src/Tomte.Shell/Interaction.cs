using System.Drawing;
using System.Numerics;
using System.Runtime.InteropServices;
using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Text;
using Windows.System;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Shell;

/// <summary>
/// A view whose clickable parts the island's <see cref="InteractionHost"/> runs: hover, press and release, the cursor,
/// tooltips and keyboard focus. The view says where its targets are and what a click on one does, and draws no hover or
/// press look of its own.
/// </summary>
internal interface IInteractive
{
    /// <summary>The targets in Tab order, each in the view's own DIP space. Read on every pointer move: return a list kept from the last layout.</summary>
    IReadOnlyList<Target> Targets { get; }

    /// <summary>Raised when the list changes, or a target's bounds, <see cref="Target.Enabled"/> or <see cref="Target.Checked"/> do.</summary>
    event Action? TargetsChanged;

    /// <summary>A click released on the target, or Enter or Space while it has keyboard focus.</summary>
    void Activate(string id);

    /// <summary>
    /// The same, saying how: the host calls this one. A view where a click, a double click and Enter mean different
    /// things (a shelf tile: select, open, open) answers it; any other leaves it to <see cref="Activate(string)"/>.
    /// </summary>
    void Activate(string id, ActivateBy by) => Activate(id);

    /// <summary>
    /// A press on a <see cref="Target.Draggable"/> target, at this point of the view's own space. Runs DragDetect and the
    /// drag itself and returns true if a drag happened; false means the button came up without one, and the host takes
    /// it as a click.
    /// </summary>
    bool TryDrag(string id, Vector2 at) => false;

    /// <summary>
    /// The pointer moved onto another of this view's targets, or keyboard focus did; null when it left them all. For
    /// content that shows on hover, such as a tile's action row; the host draws the state layer itself.
    /// </summary>
    void Hovered(string? id) { }

    /// <summary>
    /// A key while the island has the keys and one of this view's targets has focus, before the host's own Tab, arrow,
    /// Enter and Space handling. True when the view took it: Delete on a row, Left and Right on a seek bar.
    /// <paramref name="repeat"/> is the keyboard's own repeat while the key is held, which must not act twice where a
    /// second press means more (Delete on a row).
    /// </summary>
    bool KeyDown(string id, NavKey key, bool repeat) => false;

    /// <summary>
    /// A key for the view as a whole, whatever has focus, after the focused target's view passed on it: Esc closing
    /// History's open conversation, Alt+Left on the AI box. Also Esc while the island does not have the keys. True when
    /// the view took it. Called on the views from the last registered back, so a view laid over another hears it first.
    /// </summary>
    bool Command(NavKey key) => false;

    /// <summary>
    /// The pointer moved over one of this view's targets, at this point of the view's own space. Not called during a
    /// press the host holds or a <see cref="TryDrag"/> loop. For content that follows the pointer, such as the time
    /// under it on a seek bar.
    /// </summary>
    void PointerMoved(string id, Vector2 at) { }
}

/// <summary>
/// The island's one owner of pointer and keyboard interaction with registered views. A view registers with the functions
/// from island space to its own space and back; it unregisters when it hides. The host lays one state layer over the
/// target under the pointer (or the pressed one), takes the mouse from a press until the release, which clicks only on
/// the same target, shows the hand over enabled targets, a tooltip after a rest, and the focus ring once a key has been
/// used. While nothing is registered it does nothing at all. Island thread only.
/// </summary>
internal sealed class InteractionHost
{
    private const nuint MkLButton = 1;
    private readonly IslandWindow island;
    private readonly List<View> views = [];
    private readonly List<(IReadOnlyList<Target> Targets, Func<Vector2, Vector2> ToView)> hits = [];
    private readonly PointerPress pointer = new();
    private readonly Dictionary<(IInteractive View, int Group), string> lastInGroup = [];
    private readonly DispatcherQueueTimer tipTimer;
    private Overlay? overlay;
    private TargetRef? focus, told, tipFor;
    private TipPhase tip;
    private string tipText = "";
    private bool ring, tracking, dragging, tipBlocked, tipTick;

    // Where the focused target last was in its view's list, so focus can go to what takes its place when it goes.
    private (int Index, Target Target)? focusAt;

    // The view whose first stop takes focus once it has one: a panel's body, after the island opened or came back from
    // the keyboard. Until it has one, the current tab's label holds focus in its place.
    private IInteractive? first;
    private TargetRef? standIn;

    // Left came into that view from the tab after it: its last item takes focus, not its first.
    private bool firstAtLast;

    // The view a text box with the keys belongs to (the palette's prompt), and how the box takes them back.
    private IInteractive? boxOwner;
    private Action? backToBox;

    public InteractionHost(IslandWindow island)
    {
        this.island = island;
        tipTimer = island.Queue.CreateTimer();
        tipTimer.IsRepeating = false;
        tipTimer.Tick += (_, _) => TipTick();
        // The hold follows the press itself, so a click acts with the island no longer held by its own press.
        pointer.Holding += on => Pressing?.Invoke(on);
        island.Settled += Refresh;
        island.LayoutChanged += Refresh;
        // Targets are placed from the shape as it is now, which a morph changes every frame: nothing shows until it rests.
        island.Morphing += () => overlay?.HideAll();
    }

    private enum TipPhase { None, Waiting, Showing, Acknowledging }

    /// <summary>The island's text renderer, which draws the tooltips; until it is set no tip shows.</summary>
    public TextRenderer? Text { get; set; }

    /// <summary>The island has the keyboard, for a text box or for the targets: tooltips name their keys only then.</summary>
    public bool HasKeys { get; set; }

    /// <summary>Ctrl+Tab (+1) and Ctrl+Shift+Tab (-1) switch panel tabs through this.</summary>
    public Action<int>? SwitchTab { get; set; }

    /// <summary>True while a press on a target holds the mouse or a view's drag runs, then false: the island must not retract under it.</summary>
    public Action<bool>? Pressing { get; set; }

    /// <summary>Starts running <paramref name="view"/>'s targets; registering it again replaces its functions.</summary>
    public void Register(IInteractive view, Func<Vector2, Vector2> toView, Func<Vector2, Vector2> fromView)
    {
        Unregister(view);
        views.Add(new View(view, toView, fromView));
        view.TargetsChanged += Refresh;
        Refresh();
    }

    /// <summary>The view hid: its targets are nobody's now. A view that is not registered is ignored.</summary>
    public void Unregister(IInteractive view)
    {
        if (views.RemoveAll(v => ReferenceEquals(v.Interactive, view)) == 0)
        {
            return;
        }

        view.TargetsChanged -= Refresh;
        foreach (var key in lastInGroup.Keys.Where(k => ReferenceEquals(k.View, view)).ToList())
        {
            lastInGroup.Remove(key);
        }

        if (told is { } t && ReferenceEquals(t.View, view))
        {
            told = null;
        }

        if (ReferenceEquals(boxOwner, view))
        {
            (boxOwner, backToBox) = (null, null);
        }

        if (ReferenceEquals(first, view))
        {
            (first, standIn, firstAtLast) = (null, null, false);
        }

        if (views.Count == 0)
        {
            Reset();
            return;
        }

        Refresh();
    }

    /// <summary>
    /// A short confirmation at one of the view's targets, after an action that shows nothing by itself (a copy): a tick
    /// and <paramref name="text"/> on the tooltip's card for <see cref="StateLook.AcknowledgeMs"/>, in place of any tooltip.
    /// Without <paramref name="tick"/> and for <paramref name="ms"/>, the same card asks a question a key answers (Delete
    /// again removes).
    /// </summary>
    public void Acknowledge(IInteractive view, string id, string text = "Copied", bool tick = true, int ms = StateLook.AcknowledgeMs)
    {
        var at = new TargetRef(view, id);
        HideTip();
        if (Find(at, out var v, out var t) && ShowCard(v, t, text, tick))
        {
            (tipFor, tip, tipText, tipTick) = (at, TipPhase.Acknowledging, text, tick);
            Wait(ms);
        }
    }

    /// <summary>
    /// A key down while the island has the keys and no text box takes it: first the focused target's view, then the views
    /// as a whole (<see cref="Command"/>), then Tab and Shift+Tab between stops, Up and Down inside a group, Left and
    /// Right sideways (<see cref="Target.Sideways"/>, else to the previous or next panel tab), Home and End to a group's
    /// ends, Enter and Space, and Ctrl+Tab between panel tabs. True when it was used.
    /// </summary>
    public bool Key(NavKey key, bool repeat)
    {
        if (views.Count == 0 || key == NavKey.None)
        {
            return false;
        }

        // A key of the user's own: the first stop of a body still loading no longer takes focus from where it is.
        (first, standIn, firstAtLast) = (null, null, false);
        bool onTab = focus is { } a && Find(a, out _, out var tab) && tab.Kind == TargetKind.Tab;
        if ((focus is { } f && Find(f, out var fv, out var ft) && fv.Interactive.KeyDown(ft.Id, key, repeat)) || Command(key))
        {
            ring = views.Count > 0;
            // Ctrl+1 to Ctrl+6 switch tabs through the strip's own keys.
            FollowTab(onTab);
            Update();
            return true;
        }

        if (key is not (NavKey.NextTab or NavKey.PreviousTab or NavKey.Next or NavKey.Previous or NavKey.Activate or NavKey.Space
            or NavKey.Left or NavKey.Right or NavKey.Up or NavKey.Down or NavKey.Home or NavKey.End) || (key is NavKey.Activate or NavKey.Space && focus is null))
        {
            return false;
        }

        ring = true;
        switch (key)
        {
            case NavKey.NextTab or NavKey.PreviousTab:
                SwitchTab?.Invoke(key == NavKey.NextTab ? 1 : -1);
                FollowTab(onTab);
                break;
            case NavKey.Next or NavKey.Previous:
                Step(key == NavKey.Next ? 1 : -1);
                break;
            case NavKey.Activate or NavKey.Space:
                // A held key clicks once.
                if (!repeat && focus is { } c && Find(c, out var cv, out var ct) && ct.Enabled)
                {
                    cv.Interactive.Activate(ct.Id, ActivateBy.Key);
                }

                break;
            case NavKey.Home or NavKey.End:
                Arrow((list, from) => Target.Edge(list, from, last: key == NavKey.End));
                break;
            case NavKey.Left or NavKey.Right:
                Sideways(key == NavKey.Right ? 1 : -1, onTab);
                break;
            case NavKey.Up or NavKey.Down:
                Vertical(key == NavKey.Down);
                break;
            default:
                Arrow((list, from) => Target.Arrow(list, from, NavKeys.Direction(key)));
                break;
        }

        Update();
        return true;
    }

    /// <summary>
    /// The island opened, came back or switched tabs from the keyboard: focus goes to <paramref name="view"/>'s first stop
    /// (the tab's first item) once it has one, unless focus is somewhere already (on the tab labels, say).
    /// </summary>
    public void FocusFirst(IInteractive view)
    {
        if (ring && focus is null)
        {
            first = view;
            Refresh();
        }
    }

    /// <summary>The island took the keys on a key (the summon hotkey): focus shows from the start, as after any key.</summary>
    public void ShowFocus() => ring = true;

    /// <summary>One of the targets has keyboard focus.</summary>
    public bool HasFocus => focus is not null;

    /// <summary>This target of <paramref name="view"/> has the focus, set by a key or a press, not just the pointer over it.</summary>
    public bool IsFocus(IInteractive view, string id) => focus is { } f && ReferenceEquals(f.View, view) && f.Id == id;

    /// <summary>Focus to this target of <paramref name="view"/>, as a key would put it there; nothing when it has none such.</summary>
    public void Focus(IInteractive view, string id)
    {
        if (views.Find(v => ReferenceEquals(v.Interactive, view)) is { } v && Find(new TargetRef(view, id), out _, out var t))
        {
            FocusOn(v, t);
            Update();
        }
    }

    /// <summary>A text box let go of the keys with Esc (the note): focus goes to the current tab's label, with the ring.</summary>
    public void FocusTab()
    {
        if (TabLabel() is { } tab)
        {
            ring = true;
            FocusOn(tab.View, tab.Tab);
            Update();
        }
    }

    /// <summary>The last input on the targets was a key, not the pointer, so focus shows.</summary>
    public bool UsingKeys => ring;

    // Focus on a tab moves with a tab switch to the new current one.
    private void FollowTab(bool onTab)
    {
        if (onTab && focus is { } b && Find(b, out var strip, out _) && CheckedTab(strip) is { } current)
        {
            FocusOn(strip, current);
        }
    }

    /// <summary>
    /// A key for the views as a whole (<see cref="IInteractive.Command"/>), the last registered first: true when one took
    /// it. Esc comes here alone while the island does not have the keys, as the hotkey it is then.
    /// </summary>
    public bool Command(NavKey key)
    {
        // A view that takes the key may hide others, so the list is read as it was.
        var all = views.ToArray();
        for (int i = all.Length - 1; i >= 0; i--)
        {
            if (all[i].Interactive.Command(key))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Tab (+1) or Shift+Tab (-1) out of a text box that belongs to <paramref name="owner"/> (the palette's prompt): focus
    /// goes to the owner's first or last stop, and Tab past its last or Shift+Tab before its first runs
    /// <paramref name="back"/>, which gives the box the keys again. False when the owner has nothing to focus.
    /// </summary>
    public bool FromBox(IInteractive owner, int direction, Action back)
    {
        int v = views.FindIndex(x => ReferenceEquals(x.Interactive, owner));
        var list = v >= 0 ? owner.Targets : [];
        if (v < 0 || Target.Stop([list], null, direction, (_, g) => lastInGroup.GetValueOrDefault((owner, g))) is not { } to)
        {
            return false;
        }

        (boxOwner, backToBox, ring) = (owner, back, true);
        FocusOn(views[v], list[to.Index]);
        Update();
        return true;
    }

    /// <summary>The primary button (the right one after a left-handed swap) is down now, physically.</summary>
    public static bool PrimaryDown() =>
        (PInvoke.GetAsyncKeyState(PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_SWAPBUTTON) != 0 ? (int)VIRTUAL_KEY.VK_RBUTTON : (int)VIRTUAL_KEY.VK_LBUTTON) & 0x8000) != 0;

    /// <summary>The button of the press the host holds came up where the island did not hear it: the press clicks nothing.</summary>
    public void DropPress()
    {
        if (pointer.Pressed is not null && !dragging)
        {
            pointer.Cancel();
            PInvoke.ReleaseCapture();
            Update();
        }
    }

    /// <summary>A text box took the keys, or the island lost them: no target has focus and the ring goes.</summary>
    public void ClearFocus()
    {
        if (focus is null && !ring)
        {
            return;
        }

        (focus, ring) = (null, false);
        Tell(pointer.Hovered);
        if (tip is TipPhase.Waiting or TipPhase.Showing && tipFor != pointer.Hovered)
        {
            HideTip();
        }

        Update();
    }

    /// <summary>
    /// The island's pointer and cursor messages, before the old handlers: the answer, or null when no registered target
    /// took the message and the old handlers should have it.
    /// </summary>
    internal LRESULT? OnMessage(uint message, nuint wParam, nint lParam)
    {
        if (message == PInvoke.WM_MOUSELEAVE)
        {
            tracking = false;
        }

        if (views.Count == 0 || dragging)
        {
            return null;
        }

        switch (message)
        {
            case PInvoke.WM_MOUSEMOVE:
                return Moved(wParam, At(lParam));
            case PInvoke.WM_MOUSELEAVE:
                Left();
                return null;
            case PInvoke.WM_LBUTTONDOWN:
                return Down(At(lParam));
            case PInvoke.WM_LBUTTONUP:
                return Up(At(lParam));
            case PInvoke.WM_CAPTURECHANGED:
                if (pointer.Pressed is not null)
                {
                    pointer.Cancel();
                    Update();
                }

                return null;
            case PInvoke.WM_SETCURSOR:
                return Cursor(lParam);
            default:
                return null;
        }
    }

    private Vector2 At(nint lParam) => island.ToIsland((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF));

    private LRESULT? Moved(nuint wParam, Vector2 at)
    {
        if (!tracking)
        {
            // Armed again after every leave, so the hover goes when the pointer leaves the window or its region.
            var track = new TRACKMOUSEEVENT { cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(), dwFlags = TRACKMOUSEEVENT_FLAGS.TME_LEAVE, hwndTrack = island.Hwnd };
            tracking = PInvoke.TrackMouseEvent(ref track);
        }

        bool held = pointer.Pressed is not null;
        if (held && (wParam & MkLButton) == 0)
        {
            // The button came up where this window did not hear it (it does not have the foreground, so its capture
            // only covers its own area): the press is cancelled.
            pointer.Cancel();
            PInvoke.ReleaseCapture();
            held = false;
            Update();
        }
        else if (!held && (wParam & MkLButton) != 0)
        {
            // The button is down for someone else: a text box selecting, the pill being dragged.
            return null;
        }

        var under = Under(at);
        Hover(under);
        if (!held && under is { } u && Find(u, out var v, out _))
        {
            v.Interactive.PointerMoved(u.Id, v.ToView(at));
        }

        return held ? new LRESULT(0) : null;
    }

    private void Left()
    {
        bool held = pointer.Pressed is not null;
        if (held && !PrimaryDown())
        {
            // Let go outside the island, where this window, not being the foreground one, does not hear the release.
            pointer.Cancel();
            PInvoke.ReleaseCapture();
            Update();
            return;
        }

        if (!pointer.Leave())
        {
            return;
        }

        if (!held)
        {
            Tell(ring ? focus : null);
            if (tip != TipPhase.Acknowledging)
            {
                HideTip();
            }
        }

        Update();
    }

    private LRESULT? Down(Vector2 at)
    {
        // The mouse hides the focus ring until a key is used again.
        ring = false;
        if (Under(at) is not { } pressed || !Find(pressed, out var v, out var t, out int index))
        {
            Update();
            return null;
        }

        HideTip();
        tipBlocked = true;
        if (!t.Enabled)
        {
            // Nobody else's either: a press on a disabled target does nothing.
            Update();
            return new LRESULT(0);
        }

        PInvoke.GetCursorPos(out var screen);
        pointer.Down(pressed, screen.X, screen.Y, Environment.TickCount64, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXDOUBLECLK),
            PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CYDOUBLECLK), PInvoke.GetDoubleClickTime());
        Remember(v, t);
        (focus, focusAt) = (pressed, (index, t));
        Tell(pressed);
        Update();
        if (!t.Draggable)
        {
            PInvoke.SetCapture(island.Hwnd);
            return new LRESULT(0);
        }

        // The view runs DragDetect and the drag, or its own capture loop, and the button is up when it returns. A view
        // that throws has used the press up: nothing clicks.
        bool dragged = true;
        dragging = true;
        try
        {
            dragged = v.Interactive.TryDrag(t.Id, v.ToView(at));
        }
        finally
        {
            // The pointer may have left meanwhile, unseen: the next move tracks it again.
            (dragging, tracking) = (false, false);
            if (dragged)
            {
                pointer.Cancel();
            }
        }

        Activate(pointer.Up(pressed));
        Refresh();
        return new LRESULT(0);
    }

    private LRESULT? Up(Vector2 at)
    {
        if (pointer.Pressed is null)
        {
            return null;
        }

        var click = pointer.Up(Under(at));
        // Sends WM_CAPTURECHANGED at once, which finds no press any more.
        PInvoke.ReleaseCapture();
        Tell(pointer.Hovered ?? (ring ? focus : null));
        Update();
        Activate(click);
        return new LRESULT(0);
    }

    private LRESULT? Cursor(nint lParam)
    {
        if ((lParam & 0xFFFF) != PInvoke.HTCLIENT || !PInvoke.GetCursorPos(out var c))
        {
            return null;
        }

        if (Under(island.ScreenToIsland(c.X, c.Y)) is { } under && Find(under, out _, out var t) && t.Enabled)
        {
            PInvoke.SetCursor(PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_HAND));
            return new LRESULT(1);
        }

        return null;
    }

    private void Activate((TargetRef Target, ActivateBy By)? click)
    {
        if (click is { } c && Find(c.Target, out var v, out var t) && t.Enabled)
        {
            v.Interactive.Activate(t.Id, c.By);
        }
    }

    private void Hover(TargetRef? under)
    {
        var was = pointer.Hovered;
        if (!pointer.Move(under))
        {
            return;
        }

        if (pointer.Hovered != was)
        {
            tipBlocked = false;
            Tell(pointer.Hovered ?? (ring ? focus : null));
            StartTip(pointer.Hovered);
        }

        Update();
    }

    // Lists changed, the shape settled or the island moved: content may have moved under a still pointer.
    private void Refresh()
    {
        if (views.Count == 0 || dragging)
        {
            return;
        }

        if (pointer.Pressed is { } pressed && !Find(pressed, out _, out _))
        {
            pointer.Cancel();
            PInvoke.ReleaseCapture();
        }

        if (focus is { } f && Find(f, out _, out var ft, out int fi))
        {
            focusAt = (fi, ft);
        }
        else if (focus is { } gone)
        {
            // The focused target went from a view that is still here (a removed row, a closed conversation's buttons):
            // focus that shows goes to what took its place, or to the current tab's label when the view has nothing left.
            focus = null;
            int at = ring ? views.FindIndex(v => ReferenceEquals(v.Interactive, gone.View)) : -1;
            var list = at >= 0 ? views[at].Interactive.Targets : [];
            if (at >= 0 && focusAt is { } was && Target.Refocus(list, was.Index, was.Target, g => LastIn(at, g)) is int to and >= 0)
            {
                FocusOn(views[at], list[to]);
            }
            else if (at >= 0 && TabLabel() is { } label)
            {
                FocusOn(label.View, label.Tab);
            }
        }

        // The first useful thing takes focus once its view has one, unless a key or the pointer put focus elsewhere first;
        // until then the current tab's label holds it in its place.
        if (first is not null && (!ring || (focus is not null && focus != standIn)))
        {
            (first, standIn, firstAtLast) = (null, null, false);
        }
        else if (first is not null && views.Find(v => ReferenceEquals(v.Interactive, first)) is { } fv
            && (firstAtLast ? Target.Last(fv.Interactive.Targets)
                : Target.Stop([fv.Interactive.Targets], null, 1, (_, g) => lastInGroup.GetValueOrDefault((fv.Interactive, g)))?.Index ?? -1) is int pick and >= 0)
        {
            (first, standIn, firstAtLast) = (null, null, false);
            FocusOn(fv, fv.Interactive.Targets[pick]);
        }
        else if (first is not null && focus is null && TabLabel() is { } tab)
        {
            FocusOn(tab.View, tab.Tab);
            standIn = focus;
        }

        if (pointer.Pressed is null)
        {
            Hover(tracking && PInvoke.GetCursorPos(out var c) ? Under(island.ScreenToIsland(c.X, c.Y)) : null);
        }

        if (tip is TipPhase.Showing or TipPhase.Acknowledging)
        {
            if (tipFor is { } r && Find(r, out var v, out var t) && ShowCard(v, t, tipText, tip == TipPhase.Acknowledging && tipTick))
            {
                Update();
                return;
            }

            HideTip();
        }

        Update();
    }

    private void Step(int direction)
    {
        var lists = views.Select(v => v.Interactive.Targets).ToList();
        (int View, int Index)? from = focus is { } f ? IndexOf(f, lists) : null;
        if (from is { } at && ReferenceEquals(views[at.View].Interactive, boxOwner))
        {
            // Inside the prompt's own view: past its last stop, or before its first, the prompt takes the keys back.
            var list = lists[at.View];
            var next = Target.Stop([list], (0, at.Index), direction, (_, g) => lastInGroup.GetValueOrDefault((boxOwner!, g)));
            if (next is not { } n || (direction > 0 ? n.Index <= at.Index : n.Index >= at.Index))
            {
                backToBox?.Invoke();
                return;
            }

            FocusOn(views[at.View], list[n.Index]);
            return;
        }

        if (Target.Stop(lists, from, direction, (v, g) => lastInGroup.GetValueOrDefault((views[v].Interactive, g))) is { } to)
        {
            FocusOn(views[to.View], lists[to.View][to.Index]);
        }
    }

    // An arrow, Home or End: focus to where `next` says inside the focused target's group; with nothing focused, the first stop.
    private void Arrow(Func<IReadOnlyList<Target>, int, int> next)
    {
        var lists = views.Select(v => v.Interactive.Targets).ToList();
        if (focus is not { } f || IndexOf(f, lists) is not { } at)
        {
            Step(1);
            return;
        }

        int to = next(lists[at.View], at.Index);
        if (to >= 0)
        {
            var view = views[at.View];
            var t = lists[at.View][to];
            FocusOn(view, t);
            if (t.Kind == TargetKind.Tab && !t.Checked)
            {
                view.Interactive.Activate(t.Id, ActivateBy.Key);
            }
        }
    }

    // Left (-1) or Right (+1): along the focused target's grid or group (Target.Sideways); where it has nothing to do there
    // (a tab, a list, the end of a grid, nothing focused) the previous or next panel tab while the tab strip is up, with
    // focus on the tab labels following the tab. With no strip and nothing focused, the first stop.
    private void Sideways(int step, bool onTab)
    {
        var lists = views.Select(v => v.Interactive.Targets).ToList();
        if (focus is { } f && IndexOf(f, lists) is { } at && Target.Sideways(lists[at.View], at.Index, step) is int to and >= 0)
        {
            FocusOn(views[at.View], lists[at.View][to]);
        }
        else if (TabLabel() is not null)
        {
            // Left past a body's first item lands on the previous tab's last one.
            firstAtLast = step < 0 && !onTab;
            SwitchTab?.Invoke(step);
            FollowTab(onTab);
        }
        else if (focus is null)
        {
            Step(1);
        }
    }

    // Up and Down inside a group. Down from the tab labels goes into the tab's first item; Up with nothing above it in
    // the tab's body goes back up to the labels.
    private void Vertical(bool down)
    {
        var lists = views.Select(v => v.Interactive.Targets).ToList();
        if (focus is { } f && IndexOf(f, lists) is { } at && TabLabel() is { } label)
        {
            bool onLabels = ReferenceEquals(label.View, views[at.View]);
            if (down && onLabels)
            {
                for (int v = 0; v < views.Count; v++)
                {
                    if (v != at.View && Target.Stop([lists[v]], null, 1, (_, _) => null) is { } stop)
                    {
                        FocusOn(views[v], lists[v][stop.Index]);
                        return;
                    }
                }

                return;
            }

            if (!down && !onLabels && Target.AtTop(lists[at.View], at.Index))
            {
                FocusOn(label.View, label.Tab);
                return;
            }
        }

        Arrow((list, from) => Target.Arrow(list, from, NavKeys.Direction(down ? NavKey.Down : NavKey.Up)));
    }

    private void FocusOn(View view, Target t)
    {
        var at = new TargetRef(view.Interactive, t.Id);
        focus = at;
        focusAt = Find(at, out _, out _, out int index) ? (index, t) : null;
        Remember(view, t);
        tipBlocked = false;
        Tell(at);
        StartTip(at);
        // Focus that reaches a text box gives the box the keys, and its caret shows where focus is from then on.
        if (t.Kind == TargetKind.Box)
        {
            view.Interactive.Activate(t.Id, ActivateBy.Key);
        }
    }

    private void Remember(View view, Target t)
    {
        if (t.Group != 0)
        {
            lastInGroup[(view.Interactive, t.Group)] = t.Id;
        }
    }

    // The views hear of the target the pointer or the focus is on, once per change.
    private void Tell(TargetRef? now)
    {
        if (now == told)
        {
            return;
        }

        var was = told;
        told = now;
        if (was is { } w && (now is not { } n || !ReferenceEquals(n.View, w.View)))
        {
            ((IInteractive)w.View).Hovered(null);
        }

        if (now is { } m)
        {
            ((IInteractive)m.View).Hovered(m.Id);
        }
    }

    private void StartTip(TargetRef? at)
    {
        if (tip == TipPhase.Acknowledging)
        {
            return;
        }

        HideTip();
        if (at is { } r && !tipBlocked && Find(r, out _, out var t) && t.TipWith(HasKeys) is { } text)
        {
            (tipFor, tip, tipText) = (r, TipPhase.Waiting, text);
            Wait(StateLook.TipDelayMs);
        }
    }

    private void TipTick()
    {
        if (tip == TipPhase.Waiting && tipFor is { } r && Find(r, out var v, out var t) && ShowCard(v, t, tipText, tick: false))
        {
            tip = TipPhase.Showing;
            Wait(StateLook.TipLifetimeMs);
            return;
        }

        HideTip();
    }

    private void Wait(int milliseconds)
    {
        tipTimer.Interval = TimeSpan.FromMilliseconds(milliseconds);
        tipTimer.Start();
    }

    private void HideTip()
    {
        tipTimer.Stop();
        (tip, tipFor) = (TipPhase.None, null);
        overlay?.HideCard();
    }

    private bool ShowCard(View view, Target target, string text, bool tick)
    {
        var at = IslandRect(view, target.Bounds);
        if (Text is not { } renderer || at.Height <= 0 || island.Moving)
        {
            return false;
        }

        var g = island.Current;
        var parts = StateLook.Tip(text, tick, at, new RectangleF(-(float)g.Width / 2, 0, (float)g.Width, (float)g.Depth));
        (overlay ??= new Overlay(island.Compositor)).ShowCard(renderer, parts, island.Scale);
        overlay.Raise(island.Dips);
        return true;
    }

    // The state layer on the pressed or hovered target and the ring on the focused one, from where they are now.
    private void Update()
    {
        RectangleF? layer = null, focused = null;
        float layerCorner = 0, focusCorner = 0;
        uint fill = 0;
        // A slider draws its own hover and press on its knob.
        if (pointer.Layer(r => Find(r, out _, out var t) && t.Enabled) is { } l && Find(l.Target, out var lv, out var lt) && lt.Kind != TargetKind.Slider)
        {
            (layer, layerCorner, fill) = (IslandRect(lv, lt.Bounds), lt.Corner, l.Fill);
        }

        if (ring && focus is { } f && Find(f, out var fv, out var ft))
        {
            (focused, focusCorner) = (IslandRect(fv, ft.Bounds), ft.Corner);
        }

        if (island.Moving)
        {
            // Settled places them again.
            (layer, focused) = (null, null);
        }

        if (overlay is null && layer is null && focused is null)
        {
            return;
        }

        overlay ??= new Overlay(island.Compositor);
        if (overlay.Show(layer, layerCorner, fill, focused, focusCorner))
        {
            overlay.Raise(island.Dips);
        }
    }

    // The island hid with every view: nothing is left over for the next show, and the tip's text lets the device go.
    private void Reset()
    {
        // A view's drag (a drag out of the shelf) may hide the island while it runs; the capture is the drag's then.
        if (pointer.Pressed is not null && !dragging)
        {
            PInvoke.ReleaseCapture();
        }

        pointer.Reset();
        (focus, focusAt, told, ring, tracking, tipBlocked, boxOwner, backToBox, first, standIn, firstAtLast) = (null, null, null, false, false, false, null, null, null, null, false);
        lastInGroup.Clear();
        HideTip();
        overlay?.Release();
    }

    private TargetRef? Under(Vector2 at)
    {
        if (!PanelSpace.OnShape(at, island.Current))
        {
            return null;
        }

        hits.Clear();
        foreach (var v in views)
        {
            hits.Add((v.Interactive.Targets, v.ToView));
        }

        return Target.Hit(hits, at) is { } hit ? new TargetRef(views[hit.View].Interactive, hits[hit.View].Targets[hit.Index].Id) : null;
    }

    private bool Find(TargetRef at, out View view, out Target target) => Find(at, out view, out target, out _);

    private bool Find(TargetRef at, out View view, out Target target, out int index)
    {
        foreach (var v in views)
        {
            if (ReferenceEquals(v.Interactive, at.View))
            {
                var targets = v.Interactive.Targets;
                for (int i = 0; i < targets.Count; i++)
                {
                    if (targets[i].Id == at.Id)
                    {
                        (view, target, index) = (v, targets[i], i);
                        return true;
                    }
                }

                break;
            }
        }

        (view, target, index) = (null!, default, -1);
        return false;
    }

    private string? LastIn(int view, int group) => lastInGroup.GetValueOrDefault((views[view].Interactive, group));

    // The current tab's label, which holds focus when the tab's body has nothing to focus.
    private (View View, Target Tab)? TabLabel() =>
        views.Select(v => (v, CheckedTab(v))).FirstOrDefault(p => p.Item2 is not null) is ({ } v, { } tab) ? (v, tab) : null;

    private (int View, int Index)? IndexOf(TargetRef at, List<IReadOnlyList<Target>> lists)
    {
        for (int v = 0; v < views.Count; v++)
        {
            if (ReferenceEquals(views[v].Interactive, at.View))
            {
                for (int i = 0; i < lists[v].Count; i++)
                {
                    if (lists[v][i].Id == at.Id)
                    {
                        return (v, i);
                    }
                }
            }
        }

        return null;
    }

    private static Target? CheckedTab(View view)
    {
        var targets = view.Interactive.Targets;
        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i] is { Kind: TargetKind.Tab, Checked: true } t)
            {
                return t;
            }
        }

        return null;
    }

    private static RectangleF IslandRect(View view, RectangleF b)
    {
        var a = view.FromView(new Vector2(b.Left, b.Top));
        var z = view.FromView(new Vector2(b.Right, b.Bottom));
        return RectangleF.FromLTRB(a.X, a.Y, z.X, z.Y);
    }

    private sealed record View(IInteractive Interactive, Func<Vector2, Vector2> ToView, Func<Vector2, Vector2> FromView);

    /// <summary>
    /// What the host draws, in island space above every view: the state layer (its opacity fades in, its shape jumps), the
    /// focus ring, and the tooltip's card with its tick and its line of text.
    /// </summary>
    private sealed class Overlay
    {
        private readonly ContainerVisual root;
        private readonly ShapeVisual layer, ring;
        private readonly CompositionRoundedRectangleGeometry layerBox, ringBox;
        private readonly CompositionColorBrush layerFill, accent;
        private readonly TipCard card;
        private readonly ScalarKeyFrameAnimation fade;
        private TextVisual? label;

        public Overlay(Compositor compositor)
        {
            root = compositor.CreateContainerVisual();
            accent = compositor.CreateColorBrush(Argb(StateLook.Accent));
            layerFill = compositor.CreateColorBrush();
            (layer, layerBox) = Box(compositor, out var layerShape);
            layerShape.FillBrush = layerFill;
            (ring, ringBox) = Box(compositor, out var ringShape);
            ringShape.StrokeBrush = accent;
            ringShape.StrokeThickness = StateLook.FocusWidth;
            card = new TipCard(compositor, accent, compositor.CreatePathGeometry(IslandWindow.BuildPath(StateLook.Tick, 1)));
            fade = PanelHost.Fade(compositor, 1, StateLook.FadeMs);
            root.Children.InsertAtTop(layer);
            root.Children.InsertAtTop(ring);
            root.Children.InsertAtTop(card.Visual);
        }

        /// <summary>Moves the layer and the ring; true when either shows.</summary>
        public bool Show(RectangleF? at, float corner, uint fill, RectangleF? focused, float focusCorner)
        {
            if (at is { Height: > 0 } r)
            {
                Place(layer, layerBox, r, 0);
                layerBox.CornerRadius = new Vector2(corner);
                layerFill.Color = Argb(fill);
                if (!layer.IsVisible)
                {
                    layer.IsVisible = true;
                    layer.Opacity = 0;
                    layer.StartAnimation("Opacity", fade);
                }
            }
            else
            {
                layer.StopAnimation("Opacity");
                layer.IsVisible = false;
            }

            if (focused is { Height: > 0 } f)
            {
                var (line, round) = StateLook.FocusRing(f, focusCorner);
                Place(ring, ringBox, line, StateLook.FocusWidth / 2);
                ringBox.CornerRadius = new Vector2(round);
                ring.IsVisible = true;
            }
            else
            {
                ring.IsVisible = false;
            }

            return layer.IsVisible || ring.IsVisible;
        }

        public void ShowCard(TextRenderer text, (RectangleF Card, RectangleF Tick, TextSlot Label) parts, double scale)
        {
            var (at, mark, line) = parts;
            card.Show(at, mark, scale);
            label ??= text.Create(card.Visual);
            label.Show(line.Text, line.Style, new RectangleF(line.Slot.X - at.X, line.Slot.Y - at.Y, line.Slot.Width, line.Slot.Height));
        }

        public void HideCard() => card.Visual.IsVisible = false;

        public void HideAll()
        {
            layer.StopAnimation("Opacity");
            layer.IsVisible = ring.IsVisible = card.Visual.IsVisible = false;
        }

        /// <summary>Above whatever views put on top of island space since.</summary>
        public void Raise(ContainerVisual dips)
        {
            if (root.Parent is not null && ReferenceEquals(dips.Children.LastOrDefault(), root))
            {
                return;
            }

            if (root.Parent is not null)
            {
                dips.Children.Remove(root);
            }

            dips.Children.InsertAtTop(root);
        }

        public void Release()
        {
            HideAll();
            label?.Dispose();
            label = null;
        }

        private static (ShapeVisual Visual, CompositionRoundedRectangleGeometry Box) Box(Compositor c, out CompositionSpriteShape shape)
        {
            var visual = c.CreateShapeVisual();
            var box = c.CreateRoundedRectangleGeometry();
            shape = c.CreateSpriteShape(box);
            visual.Shapes.Add(shape);
            visual.IsVisible = false;
            return (visual, box);
        }

        // The visual covers the box plus the room a stroke centred on its edge needs.
        private static void Place(ShapeVisual visual, CompositionRoundedRectangleGeometry box, RectangleF r, float stroke)
        {
            visual.Offset = new Vector3(r.X - stroke, r.Y - stroke, 0);
            visual.Size = new Vector2(r.Width + (2 * stroke), r.Height + (2 * stroke));
            box.Offset = new Vector2(stroke);
            box.Size = new Vector2(r.Width, r.Height);
        }

        private static Windows.UI.Color Argb(uint c) => Windows.UI.Color.FromArgb((byte)(c >> 24), (byte)(c >> 16), (byte)(c >> 8), (byte)c);
    }
}
