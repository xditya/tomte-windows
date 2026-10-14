using System.Runtime.InteropServices;
using Tomte.Core;
using Tomte.Core.Triggers;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Shell;

/// <summary>
/// Turns trigger messages into state changes on the UI thread. The pause and the suppression check run before
/// every MorphTo. Owns the retraction, hover, event, peek, first-run, drag, timer-done and quiet-recheck timers,
/// all SetTimer one-shots on the island window, so nothing runs while nothing happens.
/// </summary>
internal sealed unsafe partial class TriggerController : IDisposable
{
    private const nuint RetractTimer = 1, HoverTimer = 2, EventTimer = 3, PeekTimer = 4, FirstRunTimer = 5, DragTimer = 6, TimerDoneTimer = 7, QuietTimer = 8;
    private static readonly nuint[] HideTimers = [RetractTimer, HoverTimer, EventTimer, FirstRunTimer, DragTimer];
    private const int HitTestTimeoutMs = 50;

    // The timer's done event; the finished ring belongs to it and to no other event's pill.
    private const string TimerDoneText = "Timer done";

    // Timings that are fixed rather than exposed: Compact opens Panels after a hover dwell, an event shows for
    // four seconds, window changes are debounced before peek re-checks, the first-run hint stays ten seconds.
    private const uint HoverDwellMs = 350, EventHoldMs = 4000, PeekDebounceMs = 500, FirstRunHintMs = 10000;

    private readonly IslandWindow island;
    private readonly SettingsStore store;
    private readonly PauseState pause;
    private readonly IslandStateMachine machine = new();
    private readonly TriggerQueue events = new();
    private readonly DesktopWatch watch;
    private readonly DropTarget dropTarget;
    private readonly Action<string>? log;
    private readonly IslandTimer timer;
    private readonly TimerRing ring;
    private TimerRun? finished;
    private IReadOnlyList<IslandEvent> showing = [];
    private Rambler? rambler;
    private CaptureOverlay? overlay;
    private bool capturing, holdEvents;
    private TomteSettings settings;
    private IslandActivity activity;
    private bool armed, pointerInside, peekWanted, eventShowing, dragEntered, firstRunShowing;

    public TriggerController(IslandWindow island, SettingsStore store, PauseState pause, IslandTimer timer, Action<string>? log)
    {
        this.island = island;
        this.store = store;
        this.pause = pause;
        this.timer = timer;
        this.log = log;
        ring = new TimerRing(island.Compositor);
        island.Content.Children.InsertAtTop(ring.Visual);
        // The timer has no lock and is only touched on this thread. Changed is queued, not run in place, so the
        // caller (the debug switch, a completion inside OnTimer) finishes before the one-shot and the ring re-sync.
        timer.Changed += () => island.Queue.TryEnqueue(ArmTimer);
        ArmTimer();
        settings = store.Current;
        island.Message = HandleMessage;
        // Hover or scroll on Compact opens Panels; on the next turn, so the notch that opens them is not
        // also taken as a tab switch by the panel host.
        island.Wheel += (_, _) =>
        {
            if (RetractionRules.OpensPanels(machine.State, activity))
            {
                island.Queue.TryEnqueue(() =>
                {
                    if (RetractionRules.OpensPanels(machine.State, activity))
                    {
                        Go(IslandState.Panels);
                    }
                });
            }
        };
        island.LayoutChanged = () =>
        {
            PushZone();
            // A scale change while the pill rests; the ring is sized in DIPs times the scale.
            UpdateRing();
        };
        PushZone();

        watch = new DesktopWatch(island.Hwnd, island.Queue, () => settings.AppRules.Count > 0);
        watch.Changed += OnSuppressionChanged;
        watch.WindowsChanged += OnWindowsChanged;
        dropTarget = new DropTarget(island.Hwnd, () => island.Moving, log);
        dropTarget.Entered += OnDragEnter;
        dropTarget.Over += x => island.Mascot.Lean(x - island.MascotScreenX);
        dropTarget.Dropped += OnDropped;

        InputThread.Start(island.Hwnd, store);
        // Changed arrives on a pool thread; the input thread re-reads the store where it runs.
        store.Changed += s => island.Queue.TryEnqueue(() => OnSettingsChanged(s));
        // Events wait out a pause; a resume from the tray, or the hour running out, shows them.
        pause.Ended += ReleaseEvents;
        log?.Invoke($"input: mouse hook {(InputThread.HookInstalled ? "on" : "off")}, ui thread {Environment.CurrentManagedThreadId}, all monitors={settings.AllMonitors} (primary only until the island window can move)");

        SchedulePeek();
        if (!settings.FirstRunHintShown)
        {
            firstRunShowing = Reveal(RevealTrigger.Peek, IslandState.Compact);
            if (firstRunShowing)
            {
                PInvoke.SetTimer(island.Hwnd, FirstRunTimer, FirstRunHintMs, null);
                settings = settings with { FirstRunHintShown = true };
                store.Save(settings);
                // The hint's pill is settled only now; it carries no ring.
                UpdateRing();
            }
        }
    }

    /// <summary>Chords another app holds. Windows says nothing, so the island and settings must.</summary>
    public IReadOnlyList<string> Conflicts { get; private set; } = [];

    public IslandState State => machine.State;

    /// <summary>The island's drop target; the shelf takes what is dropped on it.</summary>
    internal DropTarget Drops => dropTarget;

    /// <summary>A drop on the island, raised inside the drop and before the island morphs to Panels.</summary>
    public event Action<DroppedItems>? Dropped;

    /// <summary>The one state machine; the palette host and the rambler drive their states on it.</summary>
    internal IslandStateMachine Machine => machine;

    /// <summary>
    /// The hold-to-talk session Win+Alt+V drives: Begin on the hold's start, End on its release. Null until the
    /// speech-to-text is configured; then the chord only logs.
    /// </summary>
    public Rambler? Rambler
    {
        get => rambler;
        set
        {
            rambler = value;
            if (value is not null)
            {
                value.Retracted += OnRamblerRetracted;
            }
        }
    }

    /// <summary>
    /// What a finished lasso does: crop the monitor region, then ask with it in the palette, which takes the state
    /// from Capture to Palette. Gets the overlay, the box in its monitor's pixels and the foreground window from
    /// before the overlay took focus. Null until a question can be supplied; then the chord only logs.
    /// </summary>
    public Func<CaptureOverlay, PixelRect, nint, Task>? Captured { get; set; }

    /// <summary>Which panel the Panels state shows and how large it is. Without one, Panels is an empty Shelf-sized shape.</summary>
    public PanelHost? Panels { get; set; }

    /// <summary>
    /// What the Compact pill is up for, so a feature with Compact content knows whether the pill is its to
    /// fill. Read it once the pill is up (from ContentReady on), not during the state change itself.
    /// </summary>
    public CompactReason Compact =>
        machine.State != IslandState.Compact ? CompactReason.None
        : activity.HasFlag(IslandActivity.Rambling) ? CompactReason.Rambler
        : firstRunShowing ? CompactReason.Hint
        : eventShowing ? CompactReason.Event
        : activity.HasFlag(IslandActivity.Peek) ? CompactReason.Peek
        : CompactReason.Summary;

    /// <summary>
    /// What the Compact pill's one line shows now, by <see cref="CompactRules.Content"/>: the ring and the
    /// now-playing row both ask here. <paramref name="playing"/> says whether something plays.
    /// </summary>
    public CompactContent CompactShows(bool playing) =>
        CompactRules.Content(Compact, eventShowing ? CompactRules.Owner(showing) : IslandEventSource.Other, (timer.Run ?? finished) is not null, playing);

    /// <summary>
    /// An internal event: shows Compact for the hold time. It waits while the tray pause, Focus, DND or fullscreen
    /// is on, and while the island is showing something else; then it shows once that ends. Nothing raised here is
    /// dropped for arriving at a busy moment; a pause until restart drops what waits when the app quits.
    /// </summary>
    public void Raise(IslandEvent e)
    {
        // Focus and quiet hours change with no event to hear, so the notification state is read for each event.
        watch.ReadNotificationState(() =>
        {
            if (events.Offer(e, EventsWait()))
            {
                ShowEvent([e]);
            }
            else
            {
                log?.Invoke($"events: \"{e.Text}\" waits, {events.Count} queued (island {machine.State}{(pause.IsPaused(DateTimeOffset.Now) ? ", paused" : "")})");
            }
        });
    }

    /// <summary>A drag out of the island holds it open like any drag, and its end arms the retract like any drag's.</summary>
    public void DraggingOut(bool on)
    {
        if (on)
        {
            activity |= IslandActivity.DragInProgress;
        }
        else
        {
            EndDrag();
        }
    }

    /// <summary>The tray's "Open island": opens Panels, or retracts. It ignores the pause, not the suppression.</summary>
    public void Toggle()
    {
        if (machine.State is not (IslandState.Panels or IslandState.Palette))
        {
            Reveal(RevealTrigger.Hotkey, IslandState.Panels, ignorePause: true);
        }
        else
        {
            Hide();
        }
    }

    /// <summary>The current panel changed while Panels is open (a tab switch): Panels re-enters itself and the shape morphs to the new size.</summary>
    public void ResizePanels()
    {
        if (machine.State == IslandState.Panels)
        {
            Go(IslandState.Panels);
        }
    }

    public void Dispose()
    {
        overlay?.Dispose();
        InputThread.Stop();
        dropTarget.Dispose();
        watch.Dispose();
        log?.Invoke($"input thread: hook calls={InputThread.HookCalls} max={Us(InputThread.HookTicksMax)}us avg={Us(InputThread.HookCalls == 0 ? 0 : InputThread.HookTicksSum / InputThread.HookCalls)}us; "
            + $"raw input calls={InputThread.InputCalls} max={Us(InputThread.InputTicksMax)}us avg={Us(InputThread.InputCalls == 0 ? 0 : InputThread.InputTicksSum / InputThread.InputCalls)}us");
    }

    private static string Us(long ticks) => (ticks * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F1");

    private void PushZone()
    {
        var reveal = settings.Reveal;
        // The silhouette is a setting too; the window takes it up with its next morph.
        island.Attached = settings.AttachToTopEdge;
        InputThread.SetZone(RevealZone.For(0, 0, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN), reveal.ZoneFraction, reveal.DragApproachDip, island.Scale));
        if (machine.State != IslandState.Hidden)
        {
            InputThread.TrackPointer(island.TargetBounds, (int)Math.Round(reveal.RetractMarginDip * island.Scale));
        }
    }

    private void OnSettingsChanged(TomteSettings next)
    {
        var previous = settings;
        settings = next;
        log?.Invoke("settings changed");
        // The input thread applies only the parts whose settings changed. Pointer tracking restarts only for a change
        // in what PushZone reads: records compare by value, so the first-run flag, a tool grant or the media switch
        // leaves it as it is.
        InputThread.ApplySettings();
        if (next.Reveal != previous.Reveal || next.AttachToTopEdge != previous.AttachToTopEdge)
        {
            PushZone();
        }

        watch.Refresh();
        if (!settings.DesktopPeek && activity.HasFlag(IslandActivity.Peek))
        {
            Hide();
        }

        SchedulePeek();
    }

    private bool HandleMessage(uint message, nuint wParam, nint lParam)
    {
        if (watch.HandleMessage(message, wParam, lParam))
        {
            return true;
        }

        switch (message)
        {
            case InputThread.MsgEdgeReveal:
                if (machine.State == IslandState.Hidden || activity.HasFlag(IslandActivity.Peek))
                {
                    log?.Invoke(wParam == 1 ? "edge push: dwell" : "edge push: counts");
                    if (Reveal(RevealTrigger.EdgePush, IslandState.Compact))
                    {
                        armed = true;
                    }
                }

                return true;
            case InputThread.MsgPointer:
                OnPointer(wParam == 1);
                return true;
            case InputThread.MsgDragCandidate:
                OnDragCandidate(InputThread.Unpack(lParam));
                return true;
            case InputThread.MsgDragInZone:
                log?.Invoke("drag: zone reached");
                if (Reveal(RevealTrigger.Drag, IslandState.ShelfDrop))
                {
                    activity |= IslandActivity.DragInProgress;
                    dragEntered = false;
                    PInvoke.SetTimer(island.Hwnd, DragTimer, (uint)settings.Reveal.DragEnterTimeoutMs, null);
                }

                return true;
            case InputThread.MsgDragEnd:
                EndDrag();
                return true;
            case InputThread.MsgHotkey:
                OnHotkey((int)wParam);
                return true;
            case InputThread.MsgHotkeyReleased:
                // The session retracts itself once the note is written; Retracted then releases the rest.
                rambler?.End(keep: wParam == 1);
                return true;
            case InputThread.MsgConfirmUnavailable:
                log?.Invoke($"hotkey conflict: Win+Alt+Enter could not be registered (error {wParam})");
                rambler?.Answer(allow: false);
                return true;
            case InputThread.MsgHotkeysRegistered:
                Conflicts = InputThread.Registrations
                    .Where(r => r.Error != 0)
                    .Select(r => r.Error == 1409 ? Hotkey.ConflictMessage(r.Action, r.Chord) : $"Tomte could not register {r.Chord} for {Hotkey.Name(r.Action)} (error {r.Error}).")
                    .ToList();
                log?.Invoke($"hotkeys: {InputThread.Registrations.Count(r => r.Error == 0)}/{InputThread.Registrations.Count} registered");
                foreach (var line in Conflicts)
                {
                    log?.Invoke("hotkey conflict: " + line);
                }

                return true;
            case PInvoke.WM_TIMER:
                OnTimer(wParam);
                return true;
            case PInvoke.WM_TIMECHANGE:
            case PInvoke.WM_POWERBROADCAST when wParam == PInvoke.PBT_APMRESUMEAUTOMATIC:
                // The end time is absolute but the one-shot is an interval, and it neither follows a clock that was
                // set nor counts on time across a sleep. Work the wait out again; an end that has passed fires at once.
                log?.Invoke(message == PInvoke.WM_TIMECHANGE ? "clock changed" : "resumed from sleep");
                ArmTimer();
                // The tray's pause one-shot has the same blind spot; an hour that ran out on the wall clock frees the queue.
                ReleaseEvents();
                return false; // every window gets these; DefWindowProc gives the reply
            default:
                return false;
        }
    }

    private void OnTimer(nuint id)
    {
        PInvoke.KillTimer(island.Hwnd, id);
        switch (id)
        {
            case RetractTimer when !pointerInside && RetractionRules.ShouldRetract(machine.State, activity):
                Hide();
                break;
            case HoverTimer when pointerInside && RetractionRules.OpensPanels(machine.State, activity):
                Go(IslandState.Panels);
                break;
            case EventTimer when eventShowing && machine.State == IslandState.Compact:
                if (pointerInside)
                {
                    PInvoke.SetTimer(island.Hwnd, EventTimer, EventHoldMs, null);
                }
                else
                {
                    Hide();
                }

                break;
            case FirstRunTimer when firstRunShowing && machine.State == IslandState.Compact:
                Hide();
                break;
            case DragTimer when machine.State == IslandState.ShelfDrop && !dragEntered:
                // A window move, a tab drag or a text selection: nothing OLE ever arrived.
                log?.Invoke("drag: no DragEnter, retracting");
                activity &= ~IslandActivity.DragInProgress;
                Hide();
                break;
            case PeekTimer:
                EvaluatePeek();
                break;
            case QuietTimer:
                // A change in the answer comes back through OnSuppressionChanged, which releases the queue.
                watch.ReadNotificationState();
                break;
            case TimerDoneTimer:
                var run = timer.Run;
                if (timer.TryComplete(DateTimeOffset.Now))
                {
                    finished = run;
                    log?.Invoke($"timer: done, ended {run!.EndsAt:O}");
                    Raise(new IslandEvent(TimerDoneText, DateTimeOffset.Now) { Source = IslandEventSource.Timer });
                }
                else
                {
                    // Early: the wall clock was set back, or the wait was capped. Wait for what is left.
                    ArmTimer();
                }

                break;
        }
    }

    private void OnPointer(bool inside)
    {
        pointerInside = inside;
        if (inside)
        {
            PInvoke.KillTimer(island.Hwnd, RetractTimer);
            armed = true;
            if (machine.State == IslandState.Compact && !activity.HasFlag(IslandActivity.Rambling))
            {
                PInvoke.SetTimer(island.Hwnd, HoverTimer, HoverDwellMs, null);
            }
        }
        else
        {
            PInvoke.KillTimer(island.Hwnd, HoverTimer);
            if (armed && machine.State != IslandState.Hidden)
            {
                PInvoke.SetTimer(island.Hwnd, RetractTimer, (uint)settings.Reveal.RetractDelayMs, null);
            }
        }
    }

    /// <summary>
    /// The button went down and moved past the drag threshold. Ask the window under the button-down point what it
    /// is: only a client-area press can start an OLE drag. Captions, sizing borders and caption buttons are window
    /// moves and the Snap Layouts bar; an access-denied probe is an elevated window, whose drops UIPI blocks anyway.
    /// </summary>
    private void OnDragCandidate((int X, int Y) at)
    {
        // The overlay answers HTCLIENT, and a lasso through the reveal zone is not a file drag.
        if (machine.State == IslandState.Capture)
        {
            return;
        }
        var child = PInvoke.WindowFromPoint(new System.Drawing.Point(at.X, at.Y));
        // A drag that starts on the island is the shelf's own drag out; it must not turn the island into its drop surface.
        if (child == HWND.Null || child == island.Hwnd)
        {
            return;
        }

        nuint hit;
        var packed = (nint)(((at.Y & 0xFFFF) << 16) | (at.X & 0xFFFF));
        const SEND_MESSAGE_TIMEOUT_FLAGS flags = SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG | SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_BLOCK;
        if (PInvoke.SendMessageTimeout(child, PInvoke.WM_NCHITTEST, 0, packed, flags, HitTestTimeoutMs, &hit).Value == 0)
        {
            log?.Invoke(Marshal.GetLastPInvokeError() == 5 ? "drag: elevated window under the cursor" : "drag: hit test failed");
            return;
        }

        var root = PInvoke.GetAncestor(child, GET_ANCESTOR_FLAGS.GA_ROOT);
        if ((int)hit == PInvoke.HTTRANSPARENT && root != child && PInvoke.SendMessageTimeout(root, PInvoke.WM_NCHITTEST, 0, packed, flags, HitTestTimeoutMs, &hit).Value == 0)
        {
            return;
        }

        if (hit == PInvoke.HTCLIENT)
        {
            InputThread.ArmDrag();
            log?.Invoke("drag: candidate, client area");
        }
        else
        {
            log?.Invoke($"drag: non-client hit {hit}, ignored");
        }
    }

    private void EndDrag()
    {
        activity &= ~IslandActivity.DragInProgress;
        armed = true;
        // A lasso is a drag too. Its release must not arm a retract that fires into the palette it opens.
        if (machine.State is not (IslandState.Hidden or IslandState.Capture) && !pointerInside)
        {
            PInvoke.SetTimer(island.Hwnd, RetractTimer, (uint)settings.Reveal.RetractDelayMs, null);
        }
    }

    /// <summary>A drop morphs the drop surface (or an open island) to Panels on the Shelf tab; the mascot nods on the way.</summary>
    private void OnDropped(DroppedItems drop)
    {
        // The shelf takes the drop first: a temporary file's copy then runs before the morph, not during its first frames.
        Dropped?.Invoke(drop);
        if (machine.State is IslandState.ShelfDrop or IslandState.Compact or IslandState.Panels)
        {
            // While Panels is open on another tab this is a tab switch.
            Panels?.Select(PanelId.Shelf);
            Go(IslandState.Panels);
        }
    }

    private void OnDragEnter()
    {
        dragEntered = true;
        PInvoke.KillTimer(island.Hwnd, DragTimer);
        log?.Invoke("drag: DragEnter");
    }

    private void OnHotkey(int id)
    {
        if (id == InputThread.EscapeHotkeyId)
        {
            peekWanted = false;
            Hide();
            return;
        }

        if (id == InputThread.ConfirmHotkeyId)
        {
            rambler?.Answer(allow: true);
            return;
        }

        var action = (HotkeyAction)(id - 1);
        log?.Invoke($"hotkey: {action}");
        switch (action)
        {
            case HotkeyAction.Summon when machine.State == IslandState.Panels:
            case HotkeyAction.Palette when machine.State == IslandState.Palette:
                Hide();
                break;
            case HotkeyAction.Summon:
                Reveal(RevealTrigger.Hotkey, IslandState.Panels);
                break;
            case HotkeyAction.Scratchpad:
                // Panels opens on the Scratchpad tab; while Panels is open, the same press is a tab switch to it.
                if (Allowed(RevealTrigger.Hotkey))
                {
                    Panels?.Select(PanelId.Scratchpad);
                    Go(IslandState.Panels);
                }

                break;
            case HotkeyAction.Palette:
                Reveal(RevealTrigger.Hotkey, IslandState.Palette, (PanelSize.PaletteWidth, PanelSize.PaletteMinHeight));
                break;
            case HotkeyAction.Rambler when rambler is null:
                log?.Invoke("rambler: no session configured (start with --rambler)");
                break;
            case HotkeyAction.Rambler:
                if (Allowed(RevealTrigger.Hotkey))
                {
                    // Begin opens the 262 wide pill and sets the mascot itself.
                    rambler.Begin();
                    // A hold started on the peek pill is no longer peek: an event, a window under the zone or an edge
                    // push would otherwise take the pill away mid-recording.
                    activity = (activity & ~IslandActivity.Peek) | IslandActivity.Rambling;
                    // The rambler's pill has no room for the ring, and it changes state without Go.
                    UpdateRing();
                    pointerInside = false;
                    InputThread.TrackPointer(island.TargetBounds, (int)Math.Round(settings.Reveal.RetractMarginDip * island.Scale));
                }

                break;
            case HotkeyAction.CircleToAsk when Captured is null:
                log?.Invoke("circle to ask: no question configured (start with --debug-capture)");
                break;
            case HotkeyAction.CircleToAsk when capturing:
                break;
            case HotkeyAction.CircleToAsk when watch.State.ExclusiveFullscreen:
                // Unlike the island the overlay takes the foreground, which would drop a game out of exclusive
                // mode, so an allow rule for the app does not apply here.
                log?.Invoke("circle to ask: exclusive fullscreen, nothing shown");
                break;
            case HotkeyAction.CircleToAsk:
                if (Allowed(RevealTrigger.Hotkey))
                {
                    _ = Capture(Captured);
                }

                break;
        }
    }

    private void OnSuppressionChanged()
    {
        var s = watch.State;
        log?.Invoke($"desktop: fullscreen={s.Fullscreen} exclusive={s.ExclusiveFullscreen} secure={s.Secure} quiet={s.Quiet} exe={s.ForegroundExe}");
        // The capture overlay is itself a monitor-sized topmost window, so the shell reports it as fullscreen and
        // that must not hide anything. A secure surface closes it; Capture() then returns to Hidden.
        if (machine.State == IslandState.Capture && s.Secure)
        {
            overlay?.Dispose();
        }

        if (machine.State is not (IslandState.Hidden or IslandState.Capture) && !activity.HasFlag(IslandActivity.DragInProgress) && (s.Fullscreen || s.Secure))
        {
            // An event pill taken down by the lock screen was never seen: after a sleep the lock can land a moment
            // after the pill. Its events go back in the queue for the unlock, and a finished timer keeps its ring.
            var unseen = eventShowing && s.Secure ? showing : [];
            var ringKept = finished;
            Hide();
            foreach (var e in unseen)
            {
                events.Offer(e, suppressed: true);
            }

            if (unseen.Count > 0)
            {
                finished = ringKept;
                log?.Invoke($"events: {unseen.Count} hidden by the lock before the hold ran out, queued again");
            }
        }

        ReleaseEvents();
    }

    /// <summary>
    /// One digest for everything that waited, once nothing holds events back; its content is the panel work's.
    /// Called when the suppression changes, when the island hides and when a pause ends, the ways a wait can end.
    /// </summary>
    private void ReleaseEvents()
    {
        if (events.Count == 0 || holdEvents || EventsWait())
        {
            return;
        }

        var waited = events.Release();
        log?.Invoke($"events: showing digest of {waited.Count}");
        ShowEvent(waited);
    }

    private void ShowEvent(IReadOnlyList<IslandEvent> shown)
    {
        // Callers queue while the island is busy; this is only the last guard against morphing over a panel.
        if (TriggerQueue.IslandBusy(machine.State, activity))
        {
            return;
        }

        if (Reveal(RevealTrigger.Event, IslandState.Compact))
        {
            eventShowing = true;
            showing = shown;
            // Not armed. Pointer tracking reports "outside" on its first sample, and an armed pill would take that
            // as the pointer leaving and retract 400 ms later, well inside the hold. Only a pointer that has been
            // on this pill may retract it early; a peek pill the pointer brushed before may have left the flag set,
            // and a retract it started in its last 400 ms would fire into this pill. The event timer owns the end.
            armed = false;
            PInvoke.KillTimer(island.Hwnd, RetractTimer);
            PInvoke.SetTimer(island.Hwnd, EventTimer, EventHoldMs, null);
            island.Mascot.Set(MascotState.Result);
            UpdateRing();
        }
        else
        {
            // The callers asked about the pause and the suppression a moment ago. Should either have changed since,
            // the events wait again, and a finished timer keeps its ring for its own pill.
            foreach (var e in shown)
            {
                events.Offer(e, suppressed: true);
            }
        }
    }

    private bool EventsWait() =>
        TriggerQueue.Waits(pause.IsPaused(DateTimeOffset.Now), Suppression.Allows(RevealTrigger.Event, watch.State, settings.AppRules), machine.State, activity);

    /// <summary>
    /// One SetTimer one-shot for the time left to the absolute end, or none. Called at start-up, after a resume
    /// from sleep and after a clock change, so an end that passed while the app was down or the PC slept fires
    /// at once; an early fire comes back here for what is left. Also re-syncs the ring to the wall clock.
    /// </summary>
    private void ArmTimer()
    {
        PInvoke.KillTimer(island.Hwnd, TimerDoneTimer);
        if (timer.DueIn(DateTimeOffset.Now) is TimeSpan due)
        {
            PInvoke.SetTimer(island.Hwnd, TimerDoneTimer, (uint)Math.Clamp(Math.Ceiling(due.TotalMilliseconds), 0, int.MaxValue), null);
            log?.Invoke($"timer: ends {timer.Run!.EndsAt:O}, in {due}");
        }

        UpdateRing();
    }

    /// <summary>
    /// The ring shows where <see cref="CompactShows"/> puts it: on the push pill while a timer runs, and full
    /// from the moment it ends until its done pill has been shown; on the done pill itself; nowhere else. A push
    /// pill that is up when the timer ends keeps the ring through the thread hop the done event takes. Called
    /// again after the reason for the pill is settled (peek, the first-run hint, an event, the rambler).
    /// </summary>
    private void UpdateRing()
    {
        var run = timer.Run ?? finished;
        if (run is not null && CompactShows(playing: false) == CompactContent.TimerRing)
        {
            ring.Show(run, DateTimeOffset.Now, island.Scale, island.Mascot.Enabled);
        }
        else
        {
            ring.Hide();
        }
    }

    private static bool IsTimerDone(IslandEvent e) => e.Source == IslandEventSource.Timer;

    private void OnWindowsChanged()
    {
        SchedulePeek();
        if (events.Count > 0)
        {
            // Focus and quiet hours end with no event to hear, and the notification state was last read when the
            // waiting event was raised. A window change is the next sign of the user doing something, so the
            // state is read again then, after the same debounce as peek. No event waiting, no read.
            PInvoke.SetTimer(island.Hwnd, QuietTimer, PeekDebounceMs, null);
        }
    }

    private void SchedulePeek()
    {
        if (settings.DesktopPeek)
        {
            // Restarted on every window change: moving windows around never makes the pill flicker.
            PInvoke.SetTimer(island.Hwnd, PeekTimer, PeekDebounceMs, null);
        }
    }

    private void EvaluatePeek()
    {
        string? under = watch.WindowUnder(island.RestBounds(IslandGeometry.Compact));
        log?.Invoke(under is null ? "peek: nothing under the zone" : $"peek: {under} under the zone");
        peekWanted = under is null;
        if (peekWanted)
        {
            if (machine.State == IslandState.Hidden && Reveal(RevealTrigger.Peek, IslandState.Compact))
            {
                activity |= IslandActivity.Peek;
                // Peek is settled only now, after Go; the peek pill carries no ring.
                UpdateRing();
            }
        }
        else if (activity.HasFlag(IslandActivity.Peek))
        {
            Hide();
        }
    }

    /// <summary>The pause, then the suppression check, then the morph. False when nothing was shown.</summary>
    private bool Reveal(RevealTrigger trigger, IslandState state, (double W, double H) size = default, bool ignorePause = false)
    {
        if (!Allowed(trigger, ignorePause))
        {
            return false;
        }

        Go(state, size);
        return true;
    }

    private bool Allowed(RevealTrigger trigger, bool ignorePause = false)
    {
        if (!ignorePause && pause.IsPaused(DateTimeOffset.Now))
        {
            log?.Invoke($"{trigger}: paused");
            return false;
        }

        if (!Suppression.Allows(trigger, watch.State, settings.AppRules))
        {
            log?.Invoke($"{trigger}: suppressed ({watch.State})");
            return false;
        }

        return true;
    }

    private void OnRamblerRetracted()
    {
        if (activity.HasFlag(IslandActivity.Rambling))
        {
            Hide();
        }
    }

    /// <summary>
    /// One continuous morph to the state. A hop through Hidden keeps the state table honest for paths the
    /// interaction map adds (Compact to Shelf-drop, Panels to Palette); the shape still retargets directly and
    /// the mascot follows the real transition.
    /// </summary>
    private void Go(IslandState state, (double W, double H) size = default)
    {
        var from = machine.State;
        if (from == IslandState.Capture)
        {
            // A hotkey, the tray or the pause took over while the overlay was up; Capture() resumes as a cancel.
            overlay?.Dispose();
        }

        if (from == IslandState.Palette && state != IslandState.Palette)
        {
            // A voice command still in the palette must not ask or run once the palette is gone.
            rambler?.Cancel();
        }

        if (!IslandStateMachine.CanTransition(from, state))
        {
            machine.TransitionTo(IslandState.Hidden);
        }

        if (state != IslandState.Hidden)
        {
            // Whatever shows now was asked for: the peek pill's window rule and the rambler's hold on the island end here.
            activity = RetractionRules.TakenOver(activity);
        }

        if (state == IslandState.Panels)
        {
            // The current panel decides the size; a tab switch comes through here again with another one.
            size = Panels?.Size ?? PanelSize.Shelf;
        }

        machine.TransitionTo(state);
        island.Mascot.Follow(from, state);
        island.PanelLook = state is IslandState.Panels or IslandState.Palette;
        var (width, height) = PanelSize.Fit(size, island.MonitorSize);
        var geometry = IslandGeometry.For(state, width, height);
        // WS_EX_TOPMOST is re-asserted inside MorphTo on every show.
        var morph = island.MorphTo(geometry, Spring.PresetFor(from, state));
        // After MorphTo, which picks up a new monitor scale.
        UpdateRing();
        if (log is not null)
        {
            morph.ContinueWith(_ => log($"{from}->{state} settled in {island.LastMorphMs:F0}ms, {island.LastMorphTicks} ticks"), TaskScheduler.FromCurrentSynchronizationContext());
        }

        if (state == IslandState.Hidden)
        {
            return;
        }

        pointerInside = false;
        InputThread.TrackPointer(island.TargetBounds, (int)Math.Round(settings.Reveal.RetractMarginDip * island.Scale));
        // Esc as a global hotkey only for the states the user opened on purpose. Compact comes from a push, an event,
        // peek or the first-run hint, where swallowing the user's Esc in their own app would be a surprise. Once the
        // palette and the scratchpad activate the window they receive WM_KEYDOWN and this hotkey goes.
        InputThread.SetEscape(state is IslandState.Panels or IslandState.Palette);
    }

    internal void Hide()
    {
        foreach (nuint id in HideTimers)
        {
            PInvoke.KillTimer(island.Hwnd, id);
        }

        bool wasPeek = activity.HasFlag(IslandActivity.Peek);
        activity = IslandActivity.None;
        if (eventShowing && showing.Any(IsTimerDone))
        {
            // The finished timer keeps its full ring until its done pill has been shown. A hide of anything else,
            // another event's pill included, while that event still waits, must not take it.
            finished = null;
        }

        armed = eventShowing = firstRunShowing = dragEntered = false;
        showing = [];
        InputThread.StopTrackingPointer();
        InputThread.SetEscape(false);
        if (machine.State != IslandState.Hidden)
        {
            Go(IslandState.Hidden);
        }

        // The rambler retracts on its own, so Go may not have run: make sure no ring animation outlives the pill.
        UpdateRing();

        // The pill was peeking before something opened on top of it; nothing has moved under it, so it comes back.
        if (peekWanted && !wasPeek)
        {
            SchedulePeek();
        }

        // Events that arrived while the island was showing something waited for this.
        ReleaseEvents();
    }
}

// Await is not allowed in an unsafe context, so the capture flow lives in a part without the modifier.
internal sealed partial class TriggerController
{
    /// <summary>
    /// The overlay on the monitor under the cursor with the island hidden under it. The guard holds from the
    /// overlay through the crop and the palette run, so a second press never lands under a running ask.
    /// </summary>
    private async Task Capture(Func<CaptureOverlay, PixelRect, nint, Task> captured)
    {
        capturing = true;
        long triggered = System.Diagnostics.Stopwatch.GetTimestamp();
        try
        {
            nint foreground = (nint)PInvoke.GetForegroundWindow();
            // This hide must not show a waiting event: the overlay comes next, and Compact cannot go to Capture.
            // The capture's own exits release the queue.
            holdEvents = true;
            try
            {
                Hide();
            }
            finally
            {
                holdEvents = false;
            }

            PInvoke.GetCursorPos(out var cursor);
            machine.TransitionTo(IslandState.Capture);
            island.Mascot.Follow(IslandState.Hidden, IslandState.Capture);
            var open = overlay = new CaptureOverlay(island.Compositor, cursor);
            log?.Invoke($"capture overlay shown in {System.Diagnostics.Stopwatch.GetElapsedTime(triggered).TotalMilliseconds:F1}ms monitor={open.Bounds} scale={open.Lasso.Scale} activated={open.Activated}");
            var box = await open.Result;
            overlay = null;
            if (box is not PixelRect crop)
            {
                log?.Invoke("capture cancelled");
                ToHiddenFromCapture();
                return;
            }

            // Nothing armed before or during the lasso may retract the palette this opens.
            PInvoke.KillTimer(island.Hwnd, RetractTimer);
            armed = false;
            await captured(open, crop, foreground);
        }
        catch (Exception e)
        {
            overlay?.Dispose();
            overlay = null;
            log?.Invoke($"capture failed: {e}");
            ToHiddenFromCapture();
        }
        finally
        {
            capturing = false;
        }
    }

    private void ToHiddenFromCapture()
    {
        if (machine.State == IslandState.Capture)
        {
            machine.TransitionTo(IslandState.Hidden);
            island.Mascot.Follow(IslandState.Capture, IslandState.Hidden);
            // An event raised while the overlay was up waited for the island to be free; this path skips Hide().
            ReleaseEvents();
        }
    }
}
