using Tomte.Core;
using Tomte.Core.Jobs;
using Tomte.Core.Triggers;
using Windows.Win32;

namespace Tomte.Shell;

/// <summary>
/// Reminders that rang. Each shows on its own pill (<see cref="ReminderPill"/>) until Snooze or Dismiss: it waits, as an
/// event does, for the pause, Focus, a fullscreen app or a busy island, gives way to an event pill or anything the user
/// opens, and comes back each time the island is free again.
/// </summary>
internal sealed partial class TriggerController
{
    // Rung and not yet snoozed or dismissed, oldest first; the pill shows the first.
    private readonly List<Reminder> ringing = [];

    /// <summary>Snooze (true) or Dismiss (false) on a reminder's pill, on this thread, once the reminder left the pill.</summary>
    internal Action<Reminder, bool>? ReminderDone { get; set; }

    /// <summary>
    /// What rings now, as the store has it: each takes the place of the same reminder rung before, and the rest wait
    /// behind the one showing. The notification state is read first, as for an event.
    /// </summary>
    internal void Ring(IReadOnlyList<Reminder> rung)
    {
        foreach (var reminder in rung)
        {
            ringing.RemoveAll(r => r.Id == reminder.Id);
            ringing.Add(reminder);
        }

        log?.Invoke($"reminders: {ringing.Count} ringing");
        UpdateLipDot();
        watch.ReadNotificationState(ShowReminder);
    }

    // The first that rang, on its pill, where an event would show now. Not armed, so the pointer leaving does not retract
    // it, and no timer ends it: only its buttons, or something taking the island over, do.
    private void ShowReminder()
    {
        if (ringing.Count == 0 || activity.HasFlag(IslandActivity.Reminder) || holdEvents || EventsWait() || Program.Text is not { } text)
        {
            return;
        }

        var shown = ringing[0];
        var pill = new ReminderPill(island, machine, text, shown.Line(DateTimeOffset.Now, TimeZoneInfo.Local),
            id => island.Queue.TryEnqueue(() => EndReminder(shown, snoozed: id == ReminderPill.SnoozeId)));
        Go(IslandState.Compact, (pill.Width, 0));
        activity |= IslandActivity.Reminder;
        armed = false;
        PInvoke.KillTimer(island.Hwnd, RetractTimer);
        island.Mascot.Set(MascotState.Result);
        compact.Changed();
        UpdateRing();
        log?.Invoke($"reminders: pill for reminder {shown.Id}");
    }

    private void EndReminder(Reminder shown, bool snoozed)
    {
        if (ringing.RemoveAll(r => r.Id == shown.Id) == 0)
        {
            return;
        }

        log?.Invoke($"reminders: reminder {shown.Id} {(snoozed ? "snoozed" : "dismissed")}");
        ReminderDone?.Invoke(shown, snoozed);
        // The island hides, and the next that rang, if any, comes on its own pill.
        if (activity.HasFlag(IslandActivity.Reminder))
        {
            Hide();
        }
    }
}
