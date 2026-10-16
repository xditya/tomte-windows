using System.Runtime.InteropServices;
using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Rambler;
using Tomte.Core.Triggers;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Shell;

/// <summary>
/// Turns trigger messages into state changes on the UI thread. The pause and the suppression check run before
/// every MorphTo. Owns the retraction, hover, event, peek, first-run, drag, timer-done, quiet-recheck, undo and kept-pill
/// timers, all SetTimer one-shots on the island window, so nothing runs while nothing happens.
/// </summary>
internal sealed unsafe partial class TriggerController : IDisposable
{
    private const nuint RetractTimer = 1, HoverTimer = 2, EventTimer = 3, PeekTimer = 4, FirstRunTimer = 5, DragTimer = 6, TimerDoneTimer = 7, QuietTimer = 8, UndoTimer = 9, KeepTimer = 10;
    private static readonly nuint[] HideTimers = [RetractTimer, HoverTimer, EventTimer, FirstRunTimer, DragTimer];
    private const int HitTestTimeoutMs = 50;

    // Timings that are fixed rather than exposed: an event shows for four seconds, window changes are debounced
    // before peek re-checks, the first-run hint stays ten seconds. The hover dwell to Panels is a setting.
    private const uint EventHoldMs = 4000, PeekDebounceMs = 500, FirstRunHintMs = 10000;

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
    private readonly CompactTargets compact;
    private readonly UndoClose undo = new();
    private UndoPill? undoPill;
    private TimerRun? finished;
    private IReadOnlyList<IslandEvent> showing = [];
    private Rambler? rambler;
    private CaptureOverlay? overlay;
    private bool capturing, holdEvents, ownRetract;
    private double zoneHeight;
    private TomteSettings settings;
    private IslandActivity activity;
    private bool armed, pointerInside, pointerOn, peekWanted, eventShowing, dragEntered, firstRunShowing, arrivalShowing;
    private ArrivalFlight? arrival;
    private Task saving = Task.CompletedTask;

    // Something plays, as last heard while the pill kept while music plays is on, and when it last stopped (TickCount64);
    // the app that played last, and whether it has gone since.
    private bool mediaPlaying, playerGone;
    private long stoppedAt;
    private string? playedBy;

    public TriggerController(IslandWindow island, SettingsStore store, PauseState pause, IslandTimer timer, Action<string>? log)
    {
        this.island = island;
        this.store = store;
        this.pause = pause;
        this.timer = timer;
        this.log = log;
        ring = new TimerRing(island.Compositor);
        compact = new CompactTargets(this);
        island.Content.Children.InsertAtTop(ring.Visual);
        // The timer has no lock and is only touched on this thread. Changed is queued, not run in place, so the
        // caller (the debug switch, a completion inside OnTimer) finishes before the one-shot and the ring re-sync.
        timer.Changed += () => island.Queue.TryEnqueue(ArmTimer);
        ArmTimer();
        settings = store.Current;
        island.MoveTo(settings.IslandOffset);
        island.Message = HandleMessage;
        // Hover, a click or a scroll on Compact opens Panels, and a click on the tomte the palette; on the next turn, so
        // the click or notch that opens them is not also taken by the panel host as a tab switch or a press on the new body.
        // Compact's clicks come through its targets (CompactTargets).
        island.Wheel += (_, _) => OpenPanelsNextTurn();
        // A drag on the pill's own surface moves the island along the top edge, and holds it open like any drag.
        island.Grabs = Grabs;
        island.MoveStarted += () =>
        {
            activity |= IslandActivity.DragInProgress;
            PInvoke.KillTimer(island.Hwnd, HoverTimer);
            island.Swell(false);
        };
        island.Moved += OnMoved;
        island.LayoutChanged += () =>
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
        dropTarget.Over += (x, _) => island.Mascot.Lean(x - island.MascotScreenX);
        dropTarget.Dropped += OnDropped;

        InputThread.Start(island.Hwnd, store);
        // Changed arrives on a pool thread; the input thread re-reads the store where it runs.
        store.Changed += s => island.Queue.TryEnqueue(() => OnSettingsChanged(s));
        // Events wait out a pause; a resume from the tray, or the hour running out, shows them.
        pause.Ended += ReadThenRelease;
        log?.Invoke($"input: mouse hook {(InputThread.HookInstalled ? "on" : "off")}, ui thread {Environment.CurrentManagedThreadId}, all monitors={settings.AllMonitors} (primary only until the island window can move)");

        SchedulePeek();
        // Quiet hours and presenting are known only once the notification state has been read.
        watch.ReadNotificationState(Start);
    }

    /// <summary>Chords another app holds. Windows says nothing, so the island and settings must.</summary>
    public IReadOnlyList<string> Conflicts { get; private set; } = [];

    public IslandState State => machine.State;

    /// <summary>The island's one timer: the ring, the tray menu and the AI's set_timer all start and stop this one.</summary>
    internal IslandTimer Timer => timer;

    /// <summary>The lock screen or a secure desktop is up. Safe from any thread: the state is swapped whole.</summary>
    public bool OnSecureSurface => watch.State.Secure;

    /// <summary>The island's drop target; the shelf takes what is dropped on it.</summary>
    internal DropTarget Drops => dropTarget;

    /// <summary>A drop on the island, raised inside the drop and before the island morphs to Panels.</summary>
    public event Action<DroppedItems>? Dropped;

    /// <summary>An AI drop zone under the drop takes it instead: true when it did, and the palette shows it then, not the Shelf tab.</summary>
    public Func<DroppedItems, bool>? DropOnZone { get; set; }

    /// <summary>The one state machine; the palette host and the rambler drive their states on it.</summary>
    internal IslandStateMachine Machine => machine;

    /// <summary>Makes <see cref="Rambler"/> on the first hold; it returns null while there is no speech to text.</summary>
    public Func<Rambler?>? MakeRambler { get; set; }

    /// <summary>
    /// The hold-to-talk session Win+Alt+V drives: Begin on the hold's start, End on its release. Null until the
    /// first hold makes it, or with no speech to text; then the chord only logs.
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
                // Esc on the card threw the words away: Undo brings the card back; there is nothing to let go.
                value.Discarded += words => Closed(TranscriptClosed, () => ReopenCard(words), () => { });
            }
        }
    }

    /// <summary>
    /// What a finished lasso does: crop the monitor region, then ask with it in the palette, which takes the state
    /// from Capture to Palette (<see cref="PaletteFromCapture"/>). Gets the overlay, the box in its monitor's pixels and
    /// the foreground window from before the overlay took focus. Null until the shell sets it; then the chord only logs.
    /// </summary>
    public Func<CaptureOverlay, PixelRect, nint, Task>? Captured { get; set; }

    /// <summary>The lasso is cropped: the Palette state at its smallest, as the palette hotkey opens it.</summary>
    internal void PaletteFromCapture() => Go(IslandState.Palette, (PanelSize.PaletteWidth, PanelSize.PaletteMinHeight));

    /// <summary>After the palette hotkey or a click on the tomte opened the Palette state: its content and its prompt box, which takes the keys.</summary>
    public Action? PaletteOpened { get; set; }

    /// <summary>After the scratchpad hotkey opened its tab: the note takes the keys.</summary>
    public Action? ScratchpadOpened { get; set; }

    /// <summary>After the search hotkey opened the Palette state as the search box: its content, and its box takes the keys.</summary>
    public Action? SearchOpened { get; set; }

    /// <summary>The Palette state showing now is the search box, not the AI box. Anything else that takes the state ends it.</summary>
    internal bool Searching { get; private set; }

    /// <summary>The search box's size: the palette's width and its tallest, each capped by the monitor as every state is.</summary>
    internal (double W, double H) SearchSize => PanelSize.Fit((PanelSize.PaletteWidth, PanelSize.PaletteMaxHeight), island.MonitorSize);

    /// <summary>
    /// The search box's Ask row: the search goes and the AI box opens in its place, as the palette hotkey opens it, for the
    /// shell to fill with the words. The island was up, so the pause has had its say. False when it did not open.
    /// </summary>
    internal bool AskFromSearch()
    {
        LeaveSearch();
        return Reveal(RevealTrigger.Hotkey, IslandState.Palette, (PanelSize.PaletteWidth, PanelSize.PaletteMinHeight), ignorePause: true);
    }

    /// <summary>
    /// The search hotkey (and the --debug-search driver): the Palette state as the search box. Over the AI box or a drop
    /// zone's result, which share the state, the search takes their place. Opened by the search button on the open
    /// panels it can go back there (<see cref="FromPanels"/>); from the tray it ignores the pause, as the tray's other
    /// items do. False when it did not open.
    /// </summary>
    internal bool OpenSearch(bool fromPanels = false, bool ignorePause = false)
    {
        if (machine.State == IslandState.Palette)
        {
            HideForNext();
        }

        // At the palette's smallest; the search grows to what it shows. The island the button is on was up, so the pause
        // has had its say.
        if (!Reveal(RevealTrigger.Hotkey, IslandState.Palette, (SearchSize.W, PanelSize.PaletteMinHeight), ignorePause || fromPanels))
        {
            return false;
        }

        (Searching, FromPanels) = (true, fromPanels);
        SearchOpened?.Invoke();
        return true;
    }

    /// <summary>The search button on the open panels: the search box in their place, on the next turn, after the click or key.</summary>
    internal void SearchClicked() => island.Queue.TryEnqueue(() =>
    {
        if (machine.State == IslandState.Panels)
        {
            OpenSearch(fromPanels: true);
        }
    });

    // The search box and the AI box share the Palette state, and a view of one stays through Palette to Palette: the
    // search goes first, so the other opens in its place instead of on top of it.
    private void LeaveSearch()
    {
        if (Searching)
        {
            HideForNext();
        }
    }

    /// <summary>After the summon hotkey opened Panels: the island takes the keys, so Tab and the arrows reach its targets.</summary>
    public Action? SummonOpened { get; set; }

    /// <summary>Which panel the Panels state shows and how large it is. Without one, Panels is an empty Shelf-sized shape.</summary>
    public PanelHost? Panels { get; set; }

    /// <summary>Events that wait unseen for the island to be free, oldest first; the push pill's line mentions them.</summary>
    public IReadOnlyCollection<IslandEvent> Waiting => events.Waiting;

    /// <summary>Focus or Do Not Disturb is on, as last read.</summary>
    public bool Quiet => watch.State.Quiet;

    /// <summary>The tray pause is on now.</summary>
    public bool Paused => pause.IsPaused(DateTimeOffset.Now);

    /// <summary>
    /// What the Compact pill is up for, so a feature with Compact content knows whether the pill is its to
    /// fill. Read it once the pill is up (from ContentReady on), not during the state change itself.
    /// </summary>
    public CompactReason Compact =>
        machine.State != IslandState.Compact ? CompactReason.None
        : activity.HasFlag(IslandActivity.Rambling) ? CompactReason.Rambler
        : activity.HasFlag(IslandActivity.Undo) ? CompactReason.Undo
        : firstRunShowing ? CompactReason.Hint
        : arrivalShowing ? CompactReason.Arrival
        : eventShowing ? CompactReason.Event
        : activity.HasFlag(IslandActivity.Peek) ? CompactReason.Peek
        : activity.HasFlag(IslandActivity.Playing) ? CompactReason.Playing
        : CompactReason.Summary;

    /// <summary>
    /// What the Compact pill's one line shows now, by <see cref="CompactRules.Content"/>: the ring and the
    /// now-playing row both ask here. <paramref name="playing"/> says whether something plays.
    /// </summary>
    public CompactContent CompactShows(bool playing) =>
        CompactRules.Content(Compact, eventShowing ? CompactRules.Owner(showing) : IslandEventSource.Other, (timer.Run ?? finished) is not null, playing, island.Mascot.Enabled);

    /// <summary>
    /// The words the Compact pill shows now, by <see cref="CompactRules.Line"/>, and the time left when they are
    /// the running timer's, which changes every second. Read it once the pill is up, like <see cref="Compact"/>.
    /// </summary>
    public (string? Text, TimeSpan? TimerLeft) CompactLine(DateTimeOffset now, bool nowPlaying = false)
    {
        var left = (timer.Run ?? finished)?.Remaining(now);
        var text = CompactRules.Line(Compact, eventShowing ? showing : [], left, nowPlaying ? null : now);
        return (text, Compact == CompactReason.Summary ? left : null);
    }

    /// <summary>
    /// What the Compact pill shows may have changed without a state change: the reason for the pill was
    /// settled, or the timer started or ended. Raised wherever the ring is brought up to date.
    /// </summary>
    public event Action? CompactChanged;

    /// <summary>Whether the now-playing row is on the Compact pill now; a click on the pill then opens the Media tab.</summary>
    public Func<bool>? NowPlayingShows { get; set; }

    /// <summary>
    /// Whether something plays, raised with every media change. With "Keep showing while music plays" on, the pill comes
    /// when playback starts and goes <see cref="RetractionRules.KeptAfterPlayback"/> after it stops.
    /// </summary>
    public void MediaChanged(Tomte.Core.Media.NowPlaying now)
    {
        // Heard only while the setting is on, so turning it on never acts on what played when it was last read.
        bool playing = now.IsPlaying && settings.KeepWhilePlaying;
        if (playing)
        {
            playedBy = now.SessionId;
        }

        // The app that played quit, or another app's paused session took its place: the pill has nothing to wait for.
        bool gone = !playing && now.SessionId != playedBy;
        if (playing != mediaPlaying || gone != playerGone)
        {
            if (mediaPlaying && !playing)
            {
                stoppedAt = Environment.TickCount64;
            }

            (mediaPlaying, playerGone) = (playing, gone);
            KeepWhilePlaying();
        }
    }

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
                // The push pill up now may mention it.
                CompactChanged?.Invoke();
            }
        });
    }

    /// <summary>A drag out of the island lets the island retract once it has left it (<see cref="RetractionRules.DraggingOut"/>), and its end arms the retract like any drag's.</summary>
    public void DraggingOut(bool on)
    {
        if (on)
        {
            activity = RetractionRules.DraggingOut(activity);
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

    /// <summary>
    /// The tray's "Ask AI", after the menu has closed: the Palette state at its smallest, as the palette hotkey opens it,
    /// for the shell to fill. Like "Open island" it ignores the pause, not the suppression. False when it did not open.
    /// </summary>
    public bool Ask()
    {
        LeaveSearch();
        return Reveal(RevealTrigger.Hotkey, IslandState.Palette, (PanelSize.PaletteWidth, PanelSize.PaletteMinHeight), ignorePause: true);
    }

    /// <summary>The window the user last worked in, if it is still up, else 0 (<see cref="DesktopWatch.LastApp"/>).</summary>
    public nint LastApp => watch.LastApp;

    /// <summary>A button on the open island asked for a tab: Panels on it, from the palette or from another tab.</summary>
    internal void OpenPanel(PanelId id)
    {
        if (machine.State is IslandState.Panels or IslandState.Palette)
        {
            Panels?.Select(id);
            Go(IslandState.Panels);
        }
    }

    /// <summary>
    /// The palette showing now was opened by a panel action (the Ask button, Ctrl+K, History's Continue, the search
    /// button), so it can go back there. Not by a hotkey, the tray or anything else that opens it while Panels happen to
    /// show.
    /// </summary>
    internal bool FromPanels { get; private set; }

    /// <summary>That panel action was a key while the island had the keyboard: the AI box keeps the keyboard, prompt or not.</summary>
    internal bool AskedByKey { get; private set; }

    /// <summary>The AI box or the search box opened from Panels goes back to the tab it was opened from, as that tab was left.</summary>
    internal void Back()
    {
        if (machine.State == IslandState.Palette && FromPanels)
        {
            Panels?.GoingBack();
            Go(IslandState.Panels);
        }
    }

    /// <summary>A button on the open Panels asked for the AI box, as the Ask pill does; the shell fills it.</summary>
    internal void OpenPalette()
    {
        if (OpensPalette())
        {
            PaletteFromIsland();
        }
    }

    // The AI box from the open island (the pill's tomte, a panel action): one from a panel can go back there, and keeps
    // the keyboard when a key asked. Read before the morph, whose hop through Hidden lets the keys go.
    private void PaletteFromIsland()
    {
        bool panels = machine.State == IslandState.Panels;
        bool keys = panels && island.Interaction.UsingKeys && activity.HasFlag(IslandActivity.Typing);
        Go(IslandState.Palette, (PanelSize.PaletteWidth, PanelSize.PaletteMinHeight));
        (FromPanels, AskedByKey) = (panels, keys);
        PaletteOpened?.Invoke();
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
        // Centred under the island, wherever along the edge it has been moved.
        InputThread.SetZone(RevealZone.For(0, 0, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN), island.CenterX, reveal.ZoneFraction, reveal.DragApproachDip, island.Scale));
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

        // "Reset position" in the settings window; the island's own drags are already in the settings here.
        island.MoveTo(next.IslandOffset);

        watch.Refresh();
        if (!settings.DesktopPeek && activity.HasFlag(IslandActivity.Peek))
        {
            Hide();
        }

        if (!settings.KeepWhilePlaying)
        {
            mediaPlaying = false;
        }

        KeepWhilePlaying();
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
                if (RetractionRules.PushShows(machine.State, activity, Compact))
                {
                    log?.Invoke(wParam == 1 ? "edge push: dwell" : "edge push: counts");
                    // The launch arrival's pill gives way to a push as peek and the kept pill do: the pushed pill, with its line.
                    arrivalShowing = false;
                    if (Reveal(RevealTrigger.EdgePush, IslandState.Compact))
                    {
                        armed = true;
                    }
                }

                return true;
            case InputThread.MsgPointer:
                if ((wParam & 4) != 0)
                {
                    log?.Invoke($"pointer: a sample the window asked for changed it to inside={(wParam & 1) != 0} on={(wParam & 2) != 0}");
                }

                OnPointer((wParam & 1) != 0, (wParam & 2) != 0);
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
            case InputThread.MsgPressOutside:
                if (RetractionRules.DismissedByClickOutside(machine.State, activity))
                {
                    log?.Invoke("zone result: pressed outside, dismissed");
                    Hide();
                }

                return true;
            case InputThread.MsgHotkey:
                OnHotkey((int)wParam);
                return true;
            case InputThread.MsgHotkeyReleased:
                // The session retracts itself once the note is written; Retracted then releases the rest.
                rambler?.End(keep: wParam == 1);
                return true;
            case InputThread.MsgUndoUnavailable:
                log?.Invoke($"hotkey conflict: Win+Alt+Z could not be registered (error {wParam}); the undo pill shows the button only");
                if (undoPill is { } pill && activity.HasFlag(IslandActivity.Undo))
                {
                    pill.KeyTaken();
                    _ = island.MorphTo(IslandGeometry.Compact with { Width = pill.Width }, SpringPreset.Snappy);
                    InputThread.TrackPointer(island.TargetBounds, (int)Math.Round(settings.Reveal.RetractMarginDip * island.Scale));
                }

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
            case Hotkey.MsgPause:
                log?.Invoke(wParam == 1 ? "hotkeys: paused for a settings shortcut box" : "hotkeys: back from the settings window");
                InputThread.PauseHotkeys(wParam == 1);
                // The undo pill goes too and lets Win+Alt+Z go, so the box hears it and says it is taken; the kept view
                // runs out its time.
                if (wParam == 1 && activity.HasFlag(IslandActivity.Undo))
                {
                    Hide();
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
                ReadThenRelease();
                return false; // every window gets these; DefWindowProc gives the reply
            default:
                return false;
        }
    }

    /// <summary>Panels on the next turn, on <paramref name="tab"/> or else the tab last open.</summary>
    private void OpenPanelsNextTurn(PanelId? tab = null)
    {
        if (RetractionRules.OpensPanels(machine.State, activity))
        {
            island.Queue.TryEnqueue(() =>
            {
                if (RetractionRules.OpensPanels(machine.State, activity))
                {
                    if (tab is { } id)
                    {
                        Panels?.Select(id);
                    }

                    Go(IslandState.Panels);
                }
            });
        }
    }

    /// <summary>A click that asks the AI: the palette on the next turn, as the palette hotkey opens it; with no provider set up it says the AI layer is off.</summary>
    internal void AskClicked()
    {
        if (!OpensPalette())
        {
            return;
        }

        island.Queue.TryEnqueue(() =>
        {
            if (OpensPalette())
            {
                // The island is up and was clicked: the pause and the suppression had their say when it showed.
                PaletteFromIsland();
            }
        });
    }

    private bool OpensPalette() => machine.State is IslandState.Compact or IslandState.Panels && !activity.HasFlag(IslandActivity.Rambling);

    /// <summary>
    /// The pill's own surface, which a drag moves along the top edge: the rambler's pill, the tab strip's row outside its
    /// targets (the tabs, the search button and the Ask pill take their presses first; the tomte is part of the row), and
    /// the tomte over the palette. Compact's targets move it through their own drag. Panel bodies and the palette's
    /// controls keep their presses, and shelf and result tiles their drag out.
    /// </summary>
    private bool Grabs(System.Numerics.Vector2 at) => machine.State switch
    {
        IslandState.Compact => activity.HasFlag(IslandActivity.Rambling),
        IslandState.Panels => at.Y < PanelSpace.TabStripHeight,
        IslandState.Palette => island.OnMascot(at),
        _ => false,
    };

    /// <summary>
    /// The Compact pill and the peek badge for the interaction host, in island space: a click on the pill opens Panels,
    /// on the Media tab when it shows what plays, one on the tomte asks Tomte, a drag on either moves the island, and the
    /// tomte lifts while the pointer is on it. An event pill has no action of its own, so it is the pill. None on the
    /// rambler's pill, where a click does nothing, nor on the undo pill, whose one target is its Undo button.
    /// </summary>
    private sealed class CompactTargets(TriggerController owner) : IInteractive
    {
        // Where the pointer was when the pill came up: a pill that came up under a still pointer was not come onto.
        private System.Drawing.Point shownAt;

        public event Action? TargetsChanged;

        // Read on every pointer move, so built only when something changes.
        public IReadOnlyList<Target> Targets { get; private set; } = [];

        /// <summary>The state, the shape or the rambling changed: the targets again, registered while in Compact, and say so.</summary>
        public void Changed()
        {
            Targets = owner.machine.State == IslandState.Compact && (owner.activity & (IslandActivity.Rambling | IslandActivity.Undo)) == 0
                ? CompactLayout.Targets(owner.island.Heading, owner.island.Mascot.Enabled)
                : [];
            if (owner.machine.State == IslandState.Compact)
            {
                PInvoke.GetCursorPos(out shownAt);
                owner.island.Interaction.Register(this, p => p, p => p);
            }
            else
            {
                owner.island.Interaction.Unregister(this);
                owner.island.Mascot.Hover(false);
            }

            TargetsChanged?.Invoke();
        }

        public void Activate(string id)
        {
            if (id == CompactLayout.TomteId)
            {
                owner.AskClicked();
            }
            else
            {
                owner.OpenPanelsNextTurn(CompactRules.ClickOpens(owner.NowPlayingShows?.Invoke() == true));
            }
        }

        public bool TryDrag(string id, System.Numerics.Vector2 at) => owner.island.Drag(at);

        public void Hovered(string? id)
        {
            owner.island.Mascot.Hover(id == CompactLayout.TomteId);
            // The window's own mouse messages put the pointer on the pill or the badge. The rest that opens Panels runs
            // from the input thread's raw-input samples, which have been seen to miss a pointer resting on the peek
            // badge, so that thread looks again now.
            // guards a cause that was not found (an end-user test rested on the badge for 25 s with no rest
            // started, while the window saw the pointer there). The "pointer: a sample the window asked for changed it"
            // log line shows whether this ever fires; once the cause is found and fixed in the input thread's tracking
            // (log its samples and bounds while the badge is up to see it), this sample and that line can go.
            if (id is not null && PInvoke.GetCursorPos(out var at) && at != shownAt)
            {
                InputThread.SamplePointer();
            }
        }
    }

    /// <summary>
    /// A drag on the pill let go, or a double click sent the island back to the middle: the place is kept for the next
    /// start, and the pointer counts from where it is now, since the island moved under it.
    /// </summary>
    private void OnMoved(double offset)
    {
        // Off this thread, on the store's latest settings so a newer save from the settings window is kept, and one
        // after the other so the last place moved to is the one in the file.
        saving = saving.ContinueWith(
            _ =>
            {
                try
                {
                    if (store.Current.IslandOffset != offset)
                    {
                        store.Save(store.Current with { IslandOffset = offset });
                    }
                }
                catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                {
                    log?.Invoke($"settings: the island's place was not saved: {e.Message}");
                }
            },
            TaskScheduler.Default);

        // The bounds are where the island is going: after a double click sent it back to the middle, a pointer left where
        // it was is outside, so it retracts as for any pointer that left. The input thread tracks the new place at once.
        var b = island.TargetBounds;
        int margin = (int)Math.Round(settings.Reveal.RetractMarginDip * island.Scale);
        pointerInside = PInvoke.GetCursorPos(out var cursor) && RetractionRules.Inside(cursor.X, cursor.Y, b.Left, b.Top, b.Right, b.Bottom, margin);
        pointerOn = pointerInside && RetractionRules.Inside(cursor.X, cursor.Y, b.Left, b.Top, b.Right, b.Bottom, 0);
        PushZone();
        EndDrag();
        // A pointer that let go of the pill rests on it at its new place, and the dwell runs from now as for one that came
        // onto it. A double click that sent the island back to the middle leaves the pointer off it.
        Dwell(pointerOn ? HoverStep.Start : HoverStep.Cancel);
        // A window may be under the island's new place.
        SchedulePeek();
    }

    private void OnTimer(nuint id)
    {
        PInvoke.KillTimer(island.Hwnd, id);
        switch (id)
        {
            case RetractTimer when !pointerInside:
                if (RetractionRules.Released(activity, InteractionHost.PrimaryDown()) != activity)
                {
                    island.Interaction.DropPress();
                }
                else if (activity.HasFlag(IslandActivity.Pressing))
                {
                    // Still held outside: the release may never reach this window, so look again after the delay.
                    PInvoke.SetTimer(island.Hwnd, RetractTimer, (uint)settings.Reveal.RetractDelayMs, null);
                }

                if (RetractionRules.ShouldRetract(machine.State, activity))
                {
                    Hide();
                }

                break;
            case HoverTimer when pointerOn && !island.Holding && RetractionRules.OpensPanels(machine.State, activity):
                Go(IslandState.Panels);
                break;
            case EventTimer or FirstRunTimer when (id == EventTimer ? eventShowing : firstRunShowing) && machine.State == IslandState.Compact:
                // The pointer on the pill, or a drag moving it (the pointer tracking still has the bounds from before
                // the drag), holds it for another while.
                if (RetractionRules.HoldsPastItsTime(pointerInside, activity))
                {
                    PInvoke.SetTimer(island.Hwnd, id, EventHoldMs, null);
                }
                else
                {
                    Hide();
                }

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
            case KeepTimer:
                KeepWhilePlaying();
                break;
            case QuietTimer:
                // A change in the answer comes back through OnSuppressionChanged, which releases the queue.
                watch.ReadNotificationState();
                break;
            case UndoTimer:
                bool pill = activity.HasFlag(IslandActivity.Undo);
                log?.Invoke($"undo: time up for \"{undo.Line}\", let go");
                undo.End();
                if (pill)
                {
                    Hide();
                }

                break;
            case TimerDoneTimer:
                var run = timer.Run;
                if (timer.TryComplete(DateTimeOffset.Now))
                {
                    finished = run;
                    log?.Invoke($"timer: done, ended {run!.EndsAt:O}");
                    Raise(new IslandEvent(CompactRules.TimerDone(run!.Duration), DateTimeOffset.Now) { Source = IslandEventSource.Timer });
                }
                else
                {
                    // Early: the wall clock was set back, or the wait was capped. Wait for what is left.
                    ArmTimer();
                }

                break;
        }
    }

    /// <summary>
    /// The pointer is inside the island's bounds plus the retract margin, or not, and on the island itself, or not. The
    /// margin holds the island open; only resting on the pill counts toward the dwell that opens Panels, and on the push
    /// pill it swells a little while that dwell runs.
    /// </summary>
    private void OnPointer(bool inside, bool on)
    {
        var hover = RetractionRules.Hover(pointerOn, on);
        (pointerInside, pointerOn) = (inside, on);
        if (inside)
        {
            PInvoke.KillTimer(island.Hwnd, RetractTimer);
            armed = true;
        }
        else if (armed && machine.State != IslandState.Hidden)
        {
            PInvoke.SetTimer(island.Hwnd, RetractTimer, (uint)settings.Reveal.RetractDelayMs, null);
        }

        // Only a pointer coming onto the pill dwells; one moving about on it lets the dwell run.
        Dwell(hover);
        if ((activity & (IslandActivity.Result | IslandActivity.Pinned)) != 0)
        {
            InputThread.SetEscape(RetractionRules.TakesEscape(machine.State, activity, inside));
        }
    }

    /// <summary>The dwell that opens Panels from the pill starts, with the push pill's swell, or stops.</summary>
    private void Dwell(HoverStep step)
    {
        if (step == HoverStep.Start && RetractionRules.OpensPanels(machine.State, activity))
        {
            PInvoke.SetTimer(island.Hwnd, HoverTimer, (uint)settings.Reveal.ExpandDwellHeldMs, null);
            island.Swell(Compact is CompactReason.Summary or CompactReason.Playing);
        }
        else if (step == HoverStep.Cancel)
        {
            PInvoke.KillTimer(island.Hwnd, HoverTimer);
            island.Swell(false);
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
        if (machine.State == IslandState.ShelfDrop && DropOnZone?.Invoke(drop) == true)
        {
            return;
        }

        // The shelf takes the drop first: a temporary file's copy then runs before the morph, not during its first frames.
        Dropped?.Invoke(drop);
        if (rambler?.Recording == true)
        {
            log?.Invoke("drop: on the shelf; the voice key is held, so the Shelf tab does not open");
        }
        else if (machine.State is IslandState.ShelfDrop or IslandState.Compact or IslandState.Panels)
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
            // On the rambler's card Esc throws the words away, and the card retracts itself. Anywhere else it steps back
            // before it retracts: a question cancelled, a conversation closed, the AI box back to its tab.
            if (rambler?.Discard() == true || island.Interaction.Command(NavKey.Escape))
            {
                return;
            }

            peekWanted = false;
            Hide();
            return;
        }

        if (id == InputThread.UndoHotkeyId)
        {
            Undo();
            return;
        }

        if (id == InputThread.ConfirmHotkeyId)
        {
            rambler?.Answer(allow: true);
            return;
        }

        if (id is >= InputThread.ChoiceHotkeyId and <= InputThread.ChoiceEnterHotkeyId)
        {
            // Enter, Alt+I, Alt+A or Alt+N on the rambler's card.
            rambler?.Choose(id == InputThread.ChoiceEnterHotkeyId ? null : (VoiceChoice)(id - InputThread.ChoiceHotkeyId));
            return;
        }

        var action = (HotkeyAction)(id - 1);
        log?.Invoke($"hotkey: {action}");
        switch (action)
        {
            case HotkeyAction.Summon when machine.State == IslandState.Panels:
            case HotkeyAction.Palette when machine.State == IslandState.Palette && !Searching:
            case HotkeyAction.Search when Searching:
                Hide();
                break;
            case HotkeyAction.Search:
                OpenSearch();
                break;
            case HotkeyAction.Summon:
                if (Reveal(RevealTrigger.Hotkey, IslandState.Panels))
                {
                    SummonOpened?.Invoke();
                }

                break;
            case HotkeyAction.Scratchpad:
                // Panels opens on the Scratchpad tab; while Panels is open, the same press is a tab switch to it.
                if (Allowed(RevealTrigger.Hotkey))
                {
                    Panels?.Select(PanelId.Scratchpad);
                    Go(IslandState.Panels);
                    ScratchpadOpened?.Invoke();
                }

                break;
            case HotkeyAction.Palette:
                LeaveSearch();
                if (Reveal(RevealTrigger.Hotkey, IslandState.Palette, (PanelSize.PaletteWidth, PanelSize.PaletteMinHeight)))
                {
                    PaletteOpened?.Invoke();
                }

                break;
            case HotkeyAction.Rambler when !RetractionRules.VoiceOpens(machine.State):
                log?.Invoke("rambler: a file is being dragged onto the island, so the voice key does nothing");
                break;
            case HotkeyAction.Rambler:
                // Made on the first hold, so nothing of it loads while the island idles; tried again on a later hold
                // while there is no speech to text yet.
                Rambler ??= MakeRambler?.Invoke();
                if (rambler is null)
                {
                    log?.Invoke("rambler: no speech to text; install whisper with tools/whisper/Get-Whisper.ps1");
                }
                else if (rambler.CanBegin && Allowed(RevealTrigger.Hotkey))
                {
                    // On an open island what shows leaves as a retract takes it (its timers, the keys, a pin, a send
                    // or a question in the AI box, the lasso), and the shape morphs from where it is to the pill.
                    if (machine.State != IslandState.Hidden)
                    {
                        HideForNext();
                    }

                    // Begin opens the 262 wide pill and sets the mascot itself, without Go.
                    EndArrival();
                    rambler.Begin(watch.LastApp);
                    HandedToRambler();
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

        if (s.Fullscreen || s.Secure)
        {
            EndArrival();
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
    /// One digest for everything that waited, once nothing holds events back; its content is the panel work's. Then the
    /// pill kept while music plays, which waits for the island to come free in the same way. Called when the suppression
    /// changes, when the island hides and when a pause ends, the ways a wait can end.
    /// </summary>
    private void ReleaseEvents()
    {
        if (holdEvents)
        {
            return;
        }

        if (events.Count > 0 && !EventsWait())
        {
            var waited = events.Release();
            log?.Invoke($"events: showing digest of {waited.Count}");
            ShowEvent(waited);
        }

        KeepWhilePlaying();
    }

    /// <summary>
    /// The pill kept while music plays, by <see cref="RetractionRules.KeepsPill"/>: it comes where the island is free
    /// (hidden, the peek pill or the launch arrival's pill, which give way to it as to an edge push), and once playback
    /// stops it waits out the rest of <see cref="RetractionRules.KeptAfterPlayback"/> and goes, later while the pointer
    /// is on it. Called wherever the answer can change.
    /// </summary>
    private void KeepWhilePlaying()
    {
        bool kept = activity.HasFlag(IslandActivity.Playing);
        TimeSpan? stoppedFor = RetractionRules.StoppedFor(mediaPlaying, !playerGone, TimeSpan.FromMilliseconds(Environment.TickCount64 - stoppedAt));
        if (!RetractionRules.KeepsPill(settings.KeepWhilePlaying, kept, stoppedFor, Paused, watch.State, settings.AppRules))
        {
            if (kept && !playerGone && RetractionRules.HoldsKeptPill(settings.KeepWhilePlaying, Paused, watch.State, settings.AppRules, pointerInside, activity))
            {
                PInvoke.SetTimer(island.Hwnd, KeepTimer, EventHoldMs, null);
            }
            else if (kept)
            {
                log?.Invoke("kept pill: hidden");
                Hide();
            }
        }
        else if (kept && stoppedFor is { } stopped)
        {
            // What is left of the wait; a one-shot that fires early comes back here for the rest.
            PInvoke.SetTimer(island.Hwnd, KeepTimer, (uint)Math.Ceiling((RetractionRules.KeptAfterPlayback - stopped).TotalMilliseconds), null);
        }
        else if (!kept && arrival is null && (machine.State == IslandState.Hidden || activity.HasFlag(IslandActivity.Peek) || Compact == CompactReason.Arrival))
        {
            arrivalShowing = false;
            if (Reveal(RevealTrigger.Peek, IslandState.Compact))
            {
                activity |= IslandActivity.Playing;
                // The pill can stay up for hours: the tomte rests without its sway.
                island.Mascot.Still = true;
                log?.Invoke("kept pill: shown while music plays");
                // Settled only now, after Go; the kept pill carries no ring.
                UpdateRing();
            }
        }
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

    // While the launch flight is in the air the island is still Hidden, but an event pill would cut it short.
    private bool EventsWait() => arrival is not null ||
        TriggerQueue.Waits(pause.IsPaused(DateTimeOffset.Now), Suppression.Allows(RevealTrigger.Event, watch.State, settings.AppRules), machine.State, activity);

    /// <summary>
    /// A pause, a sleep or a clock change ended a wait. Focus and quiet hours may have started during it with no event
    /// to hear, so the notification state is read fresh before the queue goes out, as Raise does. No event waiting,
    /// no read, and only the pill kept while music plays is looked at again.
    /// </summary>
    private void ReadThenRelease()
    {
        if (events.Count > 0)
        {
            watch.ReadNotificationState(ReleaseEvents);
        }
        else
        {
            KeepWhilePlaying();
        }
    }

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

        CompactChanged?.Invoke();
    }

    private static bool IsTimerDone(IslandEvent e) => e.Source == IslandEventSource.Timer;

    private void OnWindowsChanged()
    {
        rambler?.ForegroundChanged();
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
        // With the tomte off there is nothing for the small pill to hold: peek is the full pill with what plays.
        bool tomte = island.Mascot.Enabled;
        string? under = watch.WindowUnder(island.RestBounds(tomte ? IslandGeometry.Peek : IslandGeometry.Compact));
        log?.Invoke(under is null ? "peek: nothing under the zone" : $"peek: {under} under the zone");
        peekWanted = under is null;
        if (peekWanted)
        {
            // Not during the launch flight: peek shows once the flight's pill has retracted.
            if (machine.State == IslandState.Hidden && arrival is null && Reveal(RevealTrigger.Peek, IslandState.Compact, peek: tomte))
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

    /// <summary>
    /// At launch: the tomte flies in from the middle of the screen and lands in the pill, unless Windows animation effects
    /// are off, the tomte is off or the desktop would hold an event back. Without the flight the island starts hidden, or
    /// with the first-run hint on a first start. Something that opened the island first keeps it; the hint then waits
    /// for the next start.
    /// </summary>
    private void Start()
    {
        if (machine.State != IslandState.Hidden)
        {
            return;
        }

        bool animations = !island.ReduceMotion, tomte = island.Mascot.Enabled && !island.Cloaked;
        if (!Arrival.Plays(animations, tomte, watch.State, settings.AppRules))
        {
            log?.Invoke($"arrival: skipped (animations={animations} tomte={tomte} {watch.State})");
        }
        else
        {
            try
            {
                arrival = new ArrivalFlight(island, ArrivalPill, ArrivalEnded, log);
                return;
            }
            catch (ExternalException e)
            {
                log?.Invoke($"arrival: not shown ({e.Message})");
            }
        }

        if (!settings.FirstRunHintShown)
        {
            FirstRunHint();
        }
    }

    /// <summary>The flying tomte nears the edge: the pill grows out of it to take the tomte in, as the first-run hint on a first start.</summary>
    private bool ArrivalPill()
    {
        var flight = arrival;
        // This morph is the flight's own, which must not end it.
        arrival = null;
        // Set before the morph, which reads it for the pill's content: the tomte alone.
        arrivalShowing = settings.FirstRunHintShown;
        bool shown = machine.State == IslandState.Hidden && (settings.FirstRunHintShown ? Reveal(RevealTrigger.Event, IslandState.Compact) : FirstRunHint());
        arrivalShowing &= shown;
        arrival = shown ? flight : null;
        if (!shown)
        {
            // Events held back for the flight.
            ReleaseEvents();
        }

        return shown;
    }

    /// <summary>
    /// The tomte rests in the pill. It retracts as a pushed pill does once the pointer is away, and the hint keeps its
    /// ten seconds. Events held back for the flight wait on for the pill as they would for any.
    /// </summary>
    private void ArrivalEnded()
    {
        arrival = null;
        if (machine.State == IslandState.Compact && !firstRunShowing)
        {
            armed = true;
            if (!pointerInside)
            {
                PInvoke.SetTimer(island.Hwnd, RetractTimer, (uint)settings.Reveal.RetractDelayMs, null);
            }
        }

        ReleaseEvents();
    }

    /// <summary>The one-time hint: the pill says where to push for ten seconds. False when it could not show.</summary>
    private bool FirstRunHint()
    {
        firstRunShowing = Reveal(RevealTrigger.Peek, IslandState.Compact);
        if (firstRunShowing)
        {
            // The ten seconds count from when the words show, so a pill held for the launch flight keeps them all.
            island.ContentReady += ArmFirstRun;
            settings = settings with { FirstRunHintShown = true };
            store.Save(settings);
            // The hint's pill is settled only now; it carries no ring.
            UpdateRing();
        }

        return firstRunShowing;
    }

    private void ArmFirstRun()
    {
        island.ContentReady -= ArmFirstRun;
        if (firstRunShowing && machine.State == IslandState.Compact)
        {
            PInvoke.SetTimer(island.Hwnd, FirstRunTimer, FirstRunHintMs, null);
        }
    }

    /// <summary>The pause, then the suppression check, then the morph. False when nothing was shown.</summary>
    private bool Reveal(RevealTrigger trigger, IslandState state, (double W, double H) size = default, bool ignorePause = false, bool peek = false)
    {
        if (!Allowed(trigger, ignorePause))
        {
            return false;
        }

        Go(state, size, peek);
        return true;
    }

    private bool Allowed(RevealTrigger trigger, bool ignorePause = false)
    {
        // Nothing opens over a recording; the voice key's own press checks CanBegin before it gets here.
        if (rambler?.Recording == true)
        {
            log?.Invoke($"{trigger}: the voice key is held, nothing else opens");
            return false;
        }

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

    /// <summary>What the undo pill says after Esc threw the card's words away.</summary>
    private const string TranscriptClosed = "Transcript closed";

    // The pill is the rambler's now: no click, dwell, event or edge push takes it away mid-recording or while its card
    // waits. The rambler's pill has no room for the ring, and it changes state without Go; nor does a click on it do anything.
    private void HandedToRambler()
    {
        activity |= IslandActivity.Rambling;
        UpdateRing();
        compact.Changed();
        pointerInside = pointerOn = false;
        InputThread.TrackPointer(island.TargetBounds, (int)Math.Round(settings.Reveal.RetractMarginDip * island.Scale));
    }

    // Undo after Esc on the card: the card again in the undo pill's place, as the voice key's hold left it.
    private bool ReopenCard(string words)
    {
        if (rambler is not { CanBegin: true } voice || !Allowed(RevealTrigger.Hotkey, ignorePause: true))
        {
            return false;
        }

        HideForNext();
        EndArrival();
        voice.Reopen(words);
        HandedToRambler();
        return true;
    }

    private void OnRamblerRetracted()
    {
        if (activity.HasFlag(IslandActivity.Rambling))
        {
            // The pill, or a voice reply that has had its time to be read, retracts on its own: nobody closed it, so
            // nothing is kept to undo.
            ownRetract = true;
            try
            {
                Hide();
            }
            finally
            {
                ownRetract = false;
            }
        }
    }

    /// <summary>
    /// One continuous morph to the state. A hop through Hidden keeps the state table honest for paths the
    /// interaction map adds (Compact to Shelf-drop, Panels to Palette); the shape still retargets directly and
    /// the mascot follows the real transition. Desktop peek is Compact in the small pill that holds the tomte alone.
    /// </summary>
    private void Go(IslandState state, (double W, double H) size = default, bool peek = false)
    {
        EndArrival();
        // The undo pill goes with any change; what it brings back keeps its time, and the pill comes back when the island
        // is free again before that is up.
        EndUndoPill();
        var from = machine.State;
        // A palette growing keeps where it came from; any other change forgets it, and a panel action sets it again. The
        // same for search, which the search hotkey sets again after this.
        if ((from, state) != (IslandState.Palette, IslandState.Palette))
        {
            (FromPanels, AskedByKey, Searching) = (false, false, false);
        }
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
            // The tomte sways again on whatever takes over from the pill kept while music plays.
            island.Mascot.Still = false;
        }

        if (state is not (IslandState.Panels or IslandState.Palette))
        {
            // A pin holds Panels and the AI box only, from one to the other included.
            Unpin();
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
        // Compact is the 220 pill unless it is given a width: the undo pill is as wide as its words and button.
        var geometry = peek ? IslandGeometry.Peek : state == IslandState.Compact && width > 0 ? IslandGeometry.Compact with { Width = width } : IslandGeometry.For(state, width, height);
        // WS_EX_TOPMOST is re-asserted inside MorphTo on every show.
        var morph = island.MorphTo(geometry, Spring.PresetFor(from, state));
        // After MorphTo, which picks up a new monitor scale.
        UpdateRing();
        // The pill's targets follow the shape it is heading for.
        compact.Changed();
        if (log is not null)
        {
            morph.ContinueWith(_ => log($"{from}->{state} settled in {island.LastMorphMs:F0}ms, {island.LastMorphTicks} ticks"), TaskScheduler.FromCurrentSynchronizationContext());
        }

        if (state == IslandState.Hidden)
        {
            return;
        }

        pointerInside = pointerOn = false;
        InputThread.TrackPointer(island.TargetBounds, (int)Math.Round(settings.Reveal.RetractMarginDip * island.Scale));
        // Esc as a global hotkey only for the states the user opened on purpose. Compact comes from a push, an event,
        // peek or the first-run hint, where swallowing the user's Esc in their own app would be a surprise. Once the
        // palette and the scratchpad activate the window they receive WM_KEYDOWN and this hotkey goes.
        InputThread.SetEscape(RetractionRules.TakesEscape(state, activity, pointerInside));
    }

    /// <summary>A text box has the keys, or no longer: typing holds the island open, and once it ends the pointer's leaving counts again.</summary>
    internal void Typing(bool on)
    {
        Hold(IslandActivity.Typing, on);
        // The keys went elsewhere (another window, a click-back from the AI box): Esc becomes a hotkey again, if the rule
        // takes it now; a pinned island away from the pointer leaves it to the user's app.
        if (machine.State is IslandState.Panels or IslandState.Palette)
        {
            InputThread.SetEscape(RetractionRules.TakesEscape(machine.State, activity, pointerInside));
        }
    }

    /// <summary>A press on one of the island's targets holds the mouse, or a view's drag from it runs: the island does not retract under it.</summary>
    internal void Pressing(bool on) => Hold(IslandActivity.Pressing, on);

    /// <summary>
    /// Ctrl+P in Panels or the AI box: pinned, the island stays when the pointer leaves or the keys go elsewhere, with a
    /// mark on the tomte, until Ctrl+P again, Esc or the hotkey; unpinned, the usual retraction applies again.
    /// </summary>
    internal void TogglePin()
    {
        if (machine.State is IslandState.Panels or IslandState.Palette)
        {
            bool on = !activity.HasFlag(IslandActivity.Pinned);
            Hold(IslandActivity.Pinned, on);
            island.Pinned = on;
            log?.Invoke(on ? "island: pinned" : "island: unpinned");
        }
    }

    private void Unpin()
    {
        activity &= ~IslandActivity.Pinned;
        island.Pinned = false;
    }

    /// <summary>The AI box lost the keys while nothing was being sent: it retracts, unless the island is pinned.</summary>
    internal void FocusLost()
    {
        if (RetractionRules.RetractsOnFocusLoss(activity))
        {
            Hide();
        }
    }

    private void Hold(IslandActivity what, bool on)
    {
        activity = on ? activity | what : activity & ~what;
        if (!on && armed && !pointerInside && machine.State != IslandState.Hidden)
        {
            PInvoke.SetTimer(island.Hwnd, RetractTimer, (uint)settings.Reveal.RetractDelayMs, null);
        }
    }

    /// <summary>
    /// A drop zone's panel: the Palette state at this height, held open while its request runs, and once it is done
    /// held by its result, which was paid for, until the user dismisses it: Esc while the pointer is on the island, the
    /// palette hotkey, a press outside the island or another state. The pointer being away never retracts it. <paramref name="hold"/> false leaves a panel
    /// that holds nothing of its own (Ask about it, which hands over to the typing palette) to the usual rules.
    /// </summary>
    internal void ZonePalette(double height, bool busy, bool hold = true)
    {
        if (machine.State == IslandState.ShelfDrop || (machine.State == IslandState.Palette && height != zoneHeight))
        {
            zoneHeight = height;
            Go(IslandState.Palette, (PanelSize.PaletteWidth, height));
        }

        if (machine.State == IslandState.Palette)
        {
            var was = activity;
            activity = (activity & ~(IslandActivity.AiTask | IslandActivity.Result)) | (busy ? IslandActivity.AiTask : hold ? IslandActivity.Result : IslandActivity.None);
            if ((was ^ activity).HasFlag(IslandActivity.Result))
            {
                InputThread.SetEscape(RetractionRules.TakesEscape(machine.State, activity, pointerInside));
            }
        }
    }

    /// <summary>
    /// Anything else that takes the island ends a launch flight still under way, also before its pill is up; the
    /// island's own tomte takes over. Go, Hide (and with it the lasso), the voice key and a fullscreen app or the lock
    /// screen call it; the flight's own pill does not.
    /// </summary>
    private void EndArrival()
    {
        if (arrival is { } flight)
        {
            arrival = null;
            flight.End();
        }
    }

    internal void Hide()
    {
        EndArrival();
        foreach (nuint id in HideTimers)
        {
            PInvoke.KillTimer(island.Hwnd, id);
        }

        bool wasPeek = activity.HasFlag(IslandActivity.Peek), wasUndo = EndUndoPill();
        activity = IslandActivity.None;
        if (eventShowing && showing.Any(IsTimerDone))
        {
            // The finished timer keeps its full ring until its done pill has been shown. A hide of anything else,
            // another event's pill included, while that event still waits, must not take it.
            finished = null;
        }

        armed = eventShowing = firstRunShowing = arrivalShowing = dragEntered = false;
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

        // A view closed with something from a provider: its undo pill, unless the pill is what went, another state opens
        // right after this hide, or an event pill took the island first (its own hide brings the undo pill back).
        if (!wasUndo && !holdEvents)
        {
            ShowUndo();
        }
    }

    /// <summary>
    /// A view holding something from a provider closed, whichever way: it is kept for the undo time from now in place of
    /// one kept before, with a pill saying <paramref name="line"/> and Undo once the island is free, and
    /// <paramref name="restore"/> brings it back in the pill's place (false when it could not). With undo off, and when
    /// the time is up, <paramref name="free"/> lets it go: a request it still runs is cancelled then. Called while the
    /// island leaves the view, so the pill comes at the end of the hide.
    /// </summary>
    internal void Closed(string line, Func<bool> restore, Action free)
    {
        // A close nobody made keeps nothing, and leaves a view kept before as it is.
        if (ownRetract)
        {
            free();
            return;
        }

        PInvoke.KillTimer(island.Hwnd, UndoTimer);
        var time = UndoClose.Time(settings.UndoSeconds);
        if (undo.Keep(line, restore, free, DateTimeOffset.Now, time))
        {
            PInvoke.SetTimer(island.Hwnd, UndoTimer, (uint)time!.Value.TotalMilliseconds, null);
            log?.Invoke($"undo: \"{line}\" kept for {time.Value.TotalSeconds:0} s");
        }
    }

    /// <summary>
    /// The Palette state at this height in the undo pill's place, for a closed view to come back into. The pill was up,
    /// so the pause has had its say. False when it did not open.
    /// </summary>
    internal bool Reopen(double height) => Reveal(RevealTrigger.Hotkey, IslandState.Palette, (PanelSize.PaletteWidth, height), ignorePause: true);

    /// <summary>The undo pill showing now, for the debug driver; null with none.</summary>
    internal UndoPill? UndoShowing => activity.HasFlag(IslandActivity.Undo) ? undoPill : null;

    /// <summary>Undo on the pill, or Win+Alt+Z while it shows: the closed view comes back in its place.</summary>
    internal void Undo()
    {
        if (!activity.HasFlag(IslandActivity.Undo))
        {
            return;
        }

        PInvoke.KillTimer(island.Hwnd, UndoTimer);
        log?.Invoke($"undo: \"{undo.Line}\" brought back");
        if (!undo.Undo(DateTimeOffset.Now) && activity.HasFlag(IslandActivity.Undo))
        {
            log?.Invoke("undo: it could not come back");
            Hide();
        }
    }

    // The pill for the kept view while the island is free and its time is not up: it holds the island until then, takes
    // Win+Alt+Z, and nothing on it but Undo takes a click. It follows the user's own close, so it opens as a hotkey would,
    // but never over a fullscreen app, which took the view down itself.
    private void ShowUndo()
    {
        if (undo.Line is not { } line || !undo.Live(DateTimeOffset.Now) || machine.State != IslandState.Hidden || Program.Text is not { } text
            || watch.State.Fullscreen || !Allowed(RevealTrigger.Hotkey))
        {
            return;
        }

        var pill = undoPill = new UndoPill(island, machine, text, line, () => island.Queue.TryEnqueue(Undo));
        Go(IslandState.Compact, (pill.Width, 0));
        activity |= IslandActivity.Undo;
        // Like an event pill: not armed, so the pointer's first "outside" does not retract it; the undo timer owns the end.
        armed = false;
        PInvoke.KillTimer(island.Hwnd, RetractTimer);
        InputThread.SetUndo(true);
        compact.Changed();
        UpdateRing();
        log?.Invoke($"undo: pill \"{line}\" for {undo.Left(DateTimeOffset.Now).TotalSeconds:0.0} s");
    }

    // The undo pill goes (its view leaves with the state change), and with it Win+Alt+Z. True when it was showing.
    private bool EndUndoPill()
    {
        if (!activity.HasFlag(IslandActivity.Undo))
        {
            return false;
        }

        activity &= ~IslandActivity.Undo;
        undoPill = null;
        InputThread.SetUndo(false);
        return true;
    }

    /// <summary>A hide for a state that opens right after it: a waiting event must not show in between.</summary>
    private void HideForNext()
    {
        holdEvents = true;
        try
        {
            Hide();
        }
        finally
        {
            holdEvents = false;
        }
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
            // The overlay comes next, and Compact cannot go to Capture. The capture's own exits release the queue.
            HideForNext();
            PInvoke.GetCursorPos(out var cursor);
            machine.TransitionTo(IslandState.Capture);
            island.Mascot.Follow(IslandState.Hidden, IslandState.Capture);
            var open = overlay = new CaptureOverlay(island.Compositor, cursor, Program.Text);
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
            // An event raised while the overlay was up, and an undo kept when the lasso took the island, waited for the
            // island to be free; this path skips Hide().
            ReleaseEvents();
            ShowUndo();
        }
    }
}
