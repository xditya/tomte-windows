using System.Globalization;
using Tomte.Core.Jobs;
using Tomte.Core.Storage;
using Windows.Win32;

namespace Tomte.Shell;

/// <summary>
/// Reminders in the shell, armed as the jobs are. A start reads the small next-due file beside tomte.db on the pool, and
/// with none nothing else happens. With one, the island window holds one SetTimer one-shot for the earliest reminder,
/// worked out again after a sleep or a clock change, so neither skips one. When it fires, what is due rings on the
/// island (<see cref="TriggerController.Ring"/>); one due while Tomte was closed, or one left ringing when it closed,
/// rings at the next start, marked missed. No Task Scheduler entry starts Tomte for a reminder: Tomte starts with
/// Windows, and a Tomte the user quit has no island to ring on. Reminders are made in the AI box, from the typed words
/// or the model's set_reminder, and removed in Settings.
/// </summary>
internal static class Reminders
{
    // One of the island window's timer ids; the controller uses 1 to 10, the desktop watch 20 and the jobs 21.
    private const nuint DueTimer = 22;

    private static readonly JobClock Clock = new();
    private static IslandWindow? island;
    private static TriggerController? triggers;
    private static ReminderStore? store;
    private static Action<string>? log;

    public static async Task StartAsync(IslandWindow i, TriggerController t, TomteDb db, Action<string>? l)
    {
        (island, triggers, store, log) = (i, t, new ReminderStore(db), l);
        try
        {
            var inner = i.Message;
            i.Message = (message, wParam, lParam) => OnMessage(message, wParam) || (inner?.Invoke(message, wParam, lParam) ?? false);
            t.ReminderDone = (reminder, snoozed) => _ = DoneAsync(reminder, snoozed);
            // The file holds 0 while one still rings, so it rings again now.
            Arm(await Task.Run(() => JobStore.ReadMarker(store.MarkerPath)));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            l?.Invoke($"reminders: start failed: {e}");
        }
    }

    /// <summary>
    /// The AI box's own words, before any provider: "remind me ..." sets a reminder and "reminders" lists them. The line
    /// to show, or null when the words are neither (a reminder Tomte cannot read included: the AI may). On the island's
    /// thread; the database work runs on the pool.
    /// </summary>
    public static async Task<string?> CommandAsync(string typed)
    {
        if (store is not { } reminders)
        {
            return null;
        }

        bool list = typed.Trim().TrimEnd('.', '?').ToLowerInvariant() is "reminders" or "list reminders" or "show reminders" or "my reminders";
        var request = list ? null : ReminderParser.Parse(typed, DateTimeOffset.Now, TimeZoneInfo.Local);
        if (!list && request is null)
        {
            return null;
        }

        try
        {
            if (list)
            {
                var all = await Task.Run(reminders.All);
                return all.Count == 0 ? "No reminders." : string.Join('\n', all.Select(r => $"{r.Text}: {r.Describe(TimeZoneInfo.Local)}"));
            }

            Arm(await Task.Run(() =>
            {
                reminders.Add(request!.Text, request.Schedule, request.Due, DateTimeOffset.Now);
                return reminders.NextDue();
            }));
            return $"Reminder set. {request!.Schedule.Describe()}: {request.Text}.";
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log?.Invoke($"reminders: {e}");
            return $"Tomte could not reach its reminders: {e.Message}";
        }
    }

    /// <summary>
    /// set_reminder's store: the reminder from its first time after now, and the one-shot armed again. Null, and nothing
    /// stored, when its time has passed. Blocks: the pool runs it.
    /// </summary>
    public static DateTimeOffset? Set(string text, JobSchedule schedule)
    {
        if (store is not { } reminders || JobStore.FirstRun(schedule, DateTimeOffset.Now, TimeZoneInfo.Local) is not { } due)
        {
            return null;
        }

        reminders.Add(text, schedule, due, DateTimeOffset.Now);
        var next = reminders.NextDue();
        island?.Queue.TryEnqueue(() => Arm(next));
        return due;
    }

    private static bool OnMessage(uint message, nuint wParam)
    {
        switch (message)
        {
            case PInvoke.WM_TIMER when wParam == DueTimer:
                PInvoke.KillTimer(island!.Hwnd, DueTimer);
                if (Clock.Fired())
                {
                    _ = RingAsync();
                }
                else
                {
                    // Early: the clock was set back, or the wait was capped.
                    SetOneShot(Clock.Wait());
                }

                return true;
            case JobTask.MsgJobsChanged:
                // The settings window removed a job or a reminder; the jobs read their own file too.
                _ = AfterAsync(() => { });
                return false;
            case PInvoke.WM_TIMECHANGE:
            case PInvoke.WM_POWERBROADCAST when wParam == PInvoke.PBT_APMRESUMEAUTOMATIC:
                SetOneShot(Clock.Wait());
                return false;
            default:
                return false;
        }
    }

    private static void Arm(DateTimeOffset? next)
    {
        log?.Invoke($"reminders: next due {next?.ToString("O", CultureInfo.InvariantCulture) ?? "none"}");
        SetOneShot(Clock.Arm(next));
    }

    private static Task RingAsync() => AfterAsync(() =>
    {
        var rung = store!.Ring(DateTimeOffset.Now, TimeZoneInfo.Local);
        island!.Queue.TryEnqueue(() => triggers!.Ring(rung));
    });

    private static Task DoneAsync(Reminder reminder, bool snoozed) => AfterAsync(() =>
    {
        if (snoozed)
        {
            store!.Snooze(reminder.Id, DateTimeOffset.Now, TimeZoneInfo.Local);
        }
        else
        {
            store!.Dismiss(reminder.Id);
        }
    });

    // Database work on the pool, then the one-shot for what is due next. A database that cannot be reached tries again a
    // minute on, instead of firing again at once.
    private static async Task AfterAsync(Action work)
    {
        DateTimeOffset? next;
        try
        {
            next = await Task.Run(() =>
            {
                work();
                return store!.NextDue();
            });
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            log?.Invoke($"reminders: {e.GetType().Name}: {e.Message}");
            next = DateTimeOffset.Now.AddMinutes(1);
        }

        Arm(next);
    }

    private static unsafe void SetOneShot(uint? wait)
    {
        PInvoke.KillTimer(island!.Hwnd, DueTimer);
        if (wait is { } ms)
        {
            PInvoke.SetTimer(island.Hwnd, DueTimer, ms, null);
        }
    }
}
