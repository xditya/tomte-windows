using System.Runtime.InteropServices;
using Tomte.Core;
using Tomte.Core.Triggers;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Shell;

/// <summary>
/// Turns trigger messages into state changes on the UI thread. The suppression check runs before every
/// MorphTo. Owns the retraction, hover, event, peek, first-run and drag timers, all SetTimer one-shots on
/// the island window, so nothing runs while nothing happens.
/// </summary>
internal sealed unsafe class TriggerController : IDisposable
{
    private const nuint RetractTimer = 1, HoverTimer = 2, EventTimer = 3, PeekTimer = 4, FirstRunTimer = 5, DragTimer = 6;
    private const int HitTestTimeoutMs = 50;

    private readonly IslandWindow island;
    private readonly TriggerSettings settings;
    private readonly IslandStateMachine machine = new();
    private readonly TriggerQueue events = new();
    private readonly DesktopWatch watch;
    private readonly DropTarget dropTarget;
    private readonly Action<string>? log;
    private IslandActivity activity;
    private bool armed, pointerInside, peekWanted, eventShowing, dragEntered, firstRunShowing;

    public TriggerController(IslandWindow island, TriggerSettings settings, Action<string>? log)
    {
        this.island = island;
        this.settings = settings;
        this.log = log;
        island.Message = HandleMessage;
        island.LayoutChanged = PushZone;
        PushZone();

        watch = new DesktopWatch(island.Hwnd, island.Queue, settings);
        watch.Changed += OnSuppressionChanged;
        watch.WindowsChanged += SchedulePeek;
        dropTarget = new DropTarget(island.Hwnd);
        dropTarget.Entered += OnDragEnter;

        InputThread.Start(island.Hwnd, settings);
        Conflicts = HotkeyConflicts.Describe(InputThread.Registrations);
        log?.Invoke($"input: hotkeys {InputThread.Registrations.Count(r => r.Registered)}/{InputThread.Registrations.Count} registered, mouse hook {(InputThread.HookInstalled ? "on" : "FAILED")}, ui thread {Environment.CurrentManagedThreadId}");
        foreach (var line in Conflicts)
        {
            log?.Invoke("hotkey conflict: " + line);
        }

        SchedulePeek();
        if (!File.Exists(FirstRunMarker))
        {
            // a marker file until settings storage exists; it absorbs this flag.
            Directory.CreateDirectory(Path.GetDirectoryName(FirstRunMarker)!);
            File.WriteAllText(FirstRunMarker, DateTime.Now.ToString("O"));
            firstRunShowing = Reveal(TriggerKind.FirstRun, IslandState.Compact);
            if (firstRunShowing)
            {
                PInvoke.SetTimer(island.Hwnd, FirstRunTimer, (uint)settings.FirstRunHintMs, null);
            }
        }
    }

    /// <summary>Chords another app holds. Windows says nothing, so the island and settings must.</summary>
    public IReadOnlyList<string> Conflicts { get; }

    public IslandState State => machine.State;

    private static string FirstRunMarker => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tomte", "first-run-shown");

    /// <summary>An internal event: shows Compact for the hold time, or waits while Focus, DND or fullscreen is on.</summary>
    public void Raise(IslandEvent e)
    {
        if (events.Offer(e, !Suppression.Allows(TriggerKind.Event, watch.State, settings)))
        {
            ShowEvent();
        }
    }

    public void Dispose()
    {
        InputThread.Stop();
        dropTarget.Dispose();
        watch.Dispose();
        log?.Invoke($"input thread: hook calls={InputThread.HookCalls} max={Us(InputThread.HookTicksMax)}us avg={Us(InputThread.HookCalls == 0 ? 0 : InputThread.HookTicksSum / InputThread.HookCalls)}us; "
            + $"raw input calls={InputThread.InputCalls} max={Us(InputThread.InputTicksMax)}us avg={Us(InputThread.InputCalls == 0 ? 0 : InputThread.InputTicksSum / InputThread.InputCalls)}us");
    }

    private static string Us(long ticks) => (ticks * 1_000_000.0 / System.Diagnostics.Stopwatch.Frequency).ToString("F1");

    private void PushZone()
    {
        InputThread.SetZone(RevealZone.For(0, 0, PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN), settings.ZoneFraction, settings.DragBand, island.Scale));
        if (machine.State != IslandState.Hidden)
        {
            InputThread.TrackPointer(island.TargetBounds, (int)Math.Round(settings.RetractMargin * island.Scale));
        }
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
                    activity &= ~IslandActivity.Peek;
                    if (Reveal(TriggerKind.EdgePush, IslandState.Compact))
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
                if (Reveal(TriggerKind.Drag, IslandState.ShelfDrop))
                {
                    activity |= IslandActivity.DragInProgress;
                    dragEntered = false;
                    PInvoke.SetTimer(island.Hwnd, DragTimer, (uint)settings.DragTimeoutMs, null);
                }

                return true;
            case InputThread.MsgDragEnd:
                activity &= ~IslandActivity.DragInProgress;
                armed = true;
                if (machine.State != IslandState.Hidden && !pointerInside)
                {
                    PInvoke.SetTimer(island.Hwnd, RetractTimer, (uint)settings.RetractDelayMs, null);
                }

                return true;
            case InputThread.MsgHotkey:
                OnHotkey((int)wParam);
                return true;
            case InputThread.MsgHotkeyReleased:
                activity &= ~IslandActivity.Rambling;
                if (machine.State == IslandState.Compact)
                {
                    Hide();
                }

                return true;
            case PInvoke.WM_TIMER:
                OnTimer(wParam);
                return true;
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
            case HoverTimer when machine.State == IslandState.Compact && pointerInside && !activity.HasFlag(IslandActivity.Rambling):
                activity &= ~IslandActivity.Peek;
                Go(IslandState.Panels, PanelSize.Shelf);
                break;
            case EventTimer when eventShowing && machine.State == IslandState.Compact:
                if (pointerInside)
                {
                    PInvoke.SetTimer(island.Hwnd, EventTimer, (uint)settings.EventHoldMs, null);
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
                PInvoke.SetTimer(island.Hwnd, HoverTimer, (uint)settings.HoverDwellMs, null);
            }
        }
        else
        {
            PInvoke.KillTimer(island.Hwnd, HoverTimer);
            if (armed && machine.State != IslandState.Hidden)
            {
                PInvoke.SetTimer(island.Hwnd, RetractTimer, (uint)settings.RetractDelayMs, null);
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
        var child = PInvoke.WindowFromPoint(new System.Drawing.Point(at.X, at.Y));
        if (child == HWND.Null)
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
        }
        else
        {
            log?.Invoke($"drag: non-client hit {hit}, ignored");
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

        var action = (HotkeyAction)(id - 1);
        log?.Invoke($"hotkey: {action}");
        switch (action)
        {
            case HotkeyAction.Summon when machine.State == IslandState.Panels:
            case HotkeyAction.Palette when machine.State == IslandState.Palette:
                Hide();
                break;
            case HotkeyAction.Summon:
                Reveal(TriggerKind.Hotkey, IslandState.Panels, PanelSize.Shelf);
                break;
            case HotkeyAction.Scratchpad:
                Reveal(TriggerKind.Hotkey, IslandState.Panels, PanelSize.Scratchpad);
                break;
            case HotkeyAction.Palette:
                Reveal(TriggerKind.Hotkey, IslandState.Palette, (PanelSize.PaletteWidth, PanelSize.PaletteMinHeight));
                break;
            case HotkeyAction.Rambler:
                if (Reveal(TriggerKind.Hotkey, IslandState.Compact))
                {
                    activity |= IslandActivity.Rambling;
                }

                break;
            case HotkeyAction.CircleToAsk:
                log?.Invoke("circle to ask: capture overlay not built yet");
                break;
        }
    }

    private void OnSuppressionChanged()
    {
        var s = watch.State;
        log?.Invoke($"desktop: fullscreen={s.Fullscreen} exclusive={s.ExclusiveFullscreen} secure={s.Secure} quiet={s.Quiet} exe={s.ForegroundExe}");
        if (machine.State != IslandState.Hidden && !activity.HasFlag(IslandActivity.DragInProgress) && (s.Fullscreen || s.Secure))
        {
            Hide();
        }

        if (events.Count > 0 && Suppression.Allows(TriggerKind.Event, s, settings))
        {
            // One digest for everything that waited; its content is the panel work's.
            var waited = events.Release();
            log?.Invoke($"events: showing digest of {waited.Count}");
            ShowEvent();
        }
    }

    private void ShowEvent()
    {
        if (machine.State != IslandState.Hidden && !activity.HasFlag(IslandActivity.Peek))
        {
            return;
        }

        activity &= ~IslandActivity.Peek;
        if (Reveal(TriggerKind.Event, IslandState.Compact))
        {
            eventShowing = true;
            armed = true;
            PInvoke.SetTimer(island.Hwnd, EventTimer, (uint)settings.EventHoldMs, null);
        }
    }

    private void SchedulePeek()
    {
        if (settings.DesktopPeek)
        {
            // Restarted on every window change: moving windows around never makes the pill flicker.
            PInvoke.SetTimer(island.Hwnd, PeekTimer, (uint)settings.PeekDebounceMs, null);
        }
    }

    private void EvaluatePeek()
    {
        double s = island.Scale;
        int w = (int)Math.Ceiling(IslandGeometry.Compact.OuterWidth * s), h = (int)Math.Ceiling(IslandGeometry.Compact.Depth * s);
        var pill = new System.Drawing.Rectangle(PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXSCREEN) / 2 - w / 2, 0, w, h);
        string? under = watch.WindowUnder(pill);
        log?.Invoke(under is null ? "peek: nothing under the zone" : $"peek: {under} under the zone");
        peekWanted = under is null;
        if (peekWanted)
        {
            if (machine.State == IslandState.Hidden && Reveal(TriggerKind.Peek, IslandState.Compact))
            {
                activity |= IslandActivity.Peek;
            }
        }
        else if (activity.HasFlag(IslandActivity.Peek))
        {
            Hide();
        }
    }

    /// <summary>The suppression check, then the morph. False when nothing was shown.</summary>
    private bool Reveal(TriggerKind kind, IslandState state, (double W, double H) size = default)
    {
        if (!Suppression.Allows(kind, watch.State, settings))
        {
            log?.Invoke($"{kind}: suppressed ({watch.State})");
            return false;
        }

        Go(state, size);
        return true;
    }

    /// <summary>
    /// One continuous morph to the state. A hop through Hidden keeps the state table honest for paths the
    /// interaction map adds (Compact to Shelf-drop, Panels to Palette); the shape still retargets directly.
    /// </summary>
    private void Go(IslandState state, (double W, double H) size = default)
    {
        var from = machine.State;
        if (!IslandStateMachine.CanTransition(from, state))
        {
            machine.TransitionTo(IslandState.Hidden);
        }

        machine.TransitionTo(state);
        island.PanelLook = state is IslandState.Panels or IslandState.Palette;
        var (monitorW, monitorH) = island.MonitorSize;
        var geometry = IslandGeometry.For(state, Math.Min(size.W, 0.6 * monitorW), Math.Min(size.H, 0.5 * monitorH));
        // WS_EX_TOPMOST is re-asserted inside MorphTo on every show.
        var morph = island.MorphTo(geometry, Spring.PresetFor(from, state));
        if (log is not null)
        {
            morph.ContinueWith(_ => log($"{from}->{state} settled in {island.LastMorphMs:F0}ms, {island.LastMorphTicks} ticks"), TaskScheduler.FromCurrentSynchronizationContext());
        }

        if (state == IslandState.Hidden)
        {
            return;
        }

        pointerInside = false;
        InputThread.TrackPointer(island.TargetBounds, (int)Math.Round(settings.RetractMargin * island.Scale));
        // Esc retracts; it is a global hotkey only while the island shows (peek excluded) and is released on hide.
        InputThread.SetEscape(!activity.HasFlag(IslandActivity.Peek));
    }

    private void Hide()
    {
        foreach (nuint id in new[] { RetractTimer, HoverTimer, EventTimer, FirstRunTimer, DragTimer })
        {
            PInvoke.KillTimer(island.Hwnd, id);
        }

        bool wasPeek = activity.HasFlag(IslandActivity.Peek);
        activity = IslandActivity.None;
        armed = eventShowing = firstRunShowing = dragEntered = false;
        InputThread.StopTrackingPointer();
        InputThread.SetEscape(false);
        if (machine.State != IslandState.Hidden)
        {
            Go(IslandState.Hidden);
        }

        // The pill was peeking before something opened on top of it; nothing has moved under it, so it comes back.
        if (peekWanted && !wasPeek)
        {
            SchedulePeek();
        }
    }
}
