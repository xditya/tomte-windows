using System.Globalization;
using System.Runtime.CompilerServices;
using Tomte.AI;
using Tomte.AI.Jobs;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Jobs;
using Tomte.Core.Storage;
using Windows.System;
using Windows.Win32;

namespace Tomte.Shell;

/// <summary>What the job wiring needs from the shell.</summary>
internal sealed record JobContext(
    IslandWindow Island,
    TriggerController Triggers,
    SettingsStore Store,
    TomteDb Db,
    IslandTimer Timer,
    ScratchpadNote Scratchpad,
    string SendLog,
    TextWriter? Writer,
    Action<string>? Log);

/// <summary>
/// Scheduled jobs in the shell. Every start tags the island window with its data folder, so a --run-job start can
/// hand its job over, and reads the small next-due file beside tomte.db on the pool: with no file there are no jobs
/// and nothing else happens. With one, the island window holds one SetTimer one-shot for the next run; the database
/// opens and Tomte.AI loads only when a run is due. A --run-job start with no Tomte running on its folder runs what is
/// due with no island and exits; the next start shows the results. Making a job needs the debug switch until the
/// island can show its confirmation as text.
/// </summary>
internal static class Jobs
{
    // One of the island window's timer ids; the controller uses 1 to 8 and the desktop watch 20.
    private const nuint DueTimer = 21;

    // A run that could not reach the database tries again after this, instead of firing again at once.
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    private static readonly JobClock Clock = new();
    private static bool running;

    private static TomteDb? db;

    /// <summary>The shelf the Shelf panel uses, so a job reads the same items. Set when the shelf starts.</summary>
    public static ShelfStore? Shelf { get; set; }

    /// <summary>The one-shot was armed for another time, or for none: the job due next may have changed. On the island's thread.</summary>
    public static event Action? NextChanged;

    /// <summary>What the one-shot is armed for: the next run over all jobs, or null with none. Read without the database.</summary>
    public static DateTimeOffset? NextDue => Clock.Next;

    /// <summary>The job due soonest after now, with its name: a read of the database on the pool. Null with no job ahead.</summary>
    public static async Task<(string Name, DateTimeOffset At)?> NextJobAsync()
    {
        if (db is not { } open)
        {
            return null;
        }

        var now = DateTimeOffset.Now;
        var jobs = await Task.Run(() => new JobStore(open).All());
        return jobs.Where(j => j.NextDue > now).MinBy(j => j.NextDue) is { NextDue: { } at } job ? (job.Name, at) : null;
    }

    /// <summary>
    /// A --run-job (id) start: handed to the Tomte running on the same data folder, which checks what is due, or, with
    /// none, run here with no island so a Tomte the user quit does not come back. Null for every other start.
    /// </summary>
    public static int? RunJobStart(string[] args)
    {
        if (JobTask.RunJobArg(args) is not long id)
        {
            return null;
        }

        string settings = Path.GetFullPath(Arg(args, "--debug-settings") ?? SettingsStore.DefaultPath);
        return JobTask.HandOff(Path.GetDirectoryName(settings)!, id) ? 0 : RunHeadless(settings, id, args);
    }

    public static async Task StartAsync(JobContext c, string[] args)
    {
        try
        {
            JobTask.Tag((nint)c.Island.Hwnd, Path.GetDirectoryName(c.Db.FilePath)!);
            db = c.Db;
            var inner = c.Island.Message;
            c.Island.Message = (message, wParam, lParam) => OnMessage(c, message, wParam, lParam) || (inner?.Invoke(message, wParam, lParam) ?? false);
            if (Arg(args, "--debug-remove-job") is { } remove)
            {
                await RemoveAsync(c, long.Parse(remove, CultureInfo.InvariantCulture));
                return;
            }

            var next = await Task.Run(() => JobStore.ReadMarker(JobStore.MarkerFor(c.Db.FilePath)));
            c.Log?.Invoke($"jobs: next due {next?.ToString("O") ?? "none"}");
            if (next is not null)
            {
                Arm(c, next);
            }

            if (Arg(args, "--debug-job") is { } request)
            {
                await ScheduleAsync(c, request, args);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            c.Log?.Invoke($"jobs: start failed: {e}");
        }
    }

    private static void Arm(JobContext c, DateTimeOffset? due)
    {
        SetOneShot(c, Clock.Arm(due));
        NextChanged?.Invoke();
    }

    private static unsafe void SetOneShot(JobContext c, uint? wait)
    {
        PInvoke.KillTimer(c.Island.Hwnd, DueTimer);
        if (wait is { } ms)
        {
            PInvoke.SetTimer(c.Island.Hwnd, DueTimer, ms, null);
        }
    }

    /// <summary>
    /// Ahead of the controller on the island window: the jobs' one-shot, a handed-over --run-job, clock and sleep
    /// changes, and the folder tag coming off as the window goes.
    /// </summary>
    private static bool OnMessage(JobContext c, uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case PInvoke.WM_TIMER when wParam == DueTimer:
                PInvoke.KillTimer(c.Island.Hwnd, DueTimer);
                if (Clock.Fired())
                {
                    _ = RunDueAsync(c, null);
                }
                else
                {
                    // Early: the clock was set back, or the wait was capped.
                    SetOneShot(c, Clock.Wait());
                }

                return true;
            case JobTask.MsgRunJob:
                _ = RunDueAsync(c, (long)wParam);
                return true;
            case PInvoke.WM_DESTROY:
                JobTask.Untag((nint)c.Island.Hwnd);
                return false;
            case PInvoke.WM_TIMECHANGE:
            case PInvoke.WM_POWERBROADCAST when wParam == PInvoke.PBT_APMRESUMEAUTOMATIC:
                // A time zone change arrives as a clock change too: drop the cached zone so times are worked out in the new one.
                TimeZoneInfo.ClearCachedData();
                SetOneShot(c, Clock.Wait());
                // The controller re-arms its own timer too.
                return false;
            default:
                return false;
        }
    }

    /// <summary>Kept out of the start path so Tomte.AI loads only when a job is due.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RunDueAsync(JobContext c, long? asked)
    {
        c.Log?.Invoke(asked is null ? "jobs: due" : $"jobs: handed job {asked}");
        if (running)
        {
            // The run in progress re-arms from the database when it ends, which takes this one in.
            return;
        }

        running = true;
        DateTimeOffset? next;
        try
        {
            // The tools read the shelf and the note from memory, so both are loaded first.
            if (Shelf is { } shelf)
            {
                await shelf.Load();
            }

            await c.Scratchpad.LoadAsync();
            next = await Resident.For(c).RunDueAsync();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            c.Log?.Invoke($"jobs: run failed ({e.GetType().Name}: {e.Message}); trying again in {RetryDelay.TotalMinutes:0} minutes");
            next = DateTimeOffset.Now + RetryDelay;
        }
        finally
        {
            running = false;
        }

        c.Log?.Invoke($"jobs: next due {next?.ToString("O") ?? "none"}");
        Arm(c, next);
    }

    /// <summary>
    /// The --run-job start with no Tomte to hand it to: the note, the shelf and the runner on one thread with a message
    /// loop (the clipboard's owner window must stay on the thread that made it), no island and no timer. That thread is
    /// STA already (CreateOnDedicatedThread makes it so), so nothing sets an apartment here. Results are kept unshown
    /// and the next-due file says 0, so the next start raises their events. --debug-log=path logs it.
    /// </summary>
    private static int RunHeadless(string settings, long id, string[] args)
    {
        string? logPath = args.FirstOrDefault(a => a.StartsWith("--debug-log=", StringComparison.Ordinal))?["--debug-log=".Length..];
        using var writer = logPath is null ? null : TextWriter.Synchronized(new StreamWriter(new FileStream(logPath, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true });
        Action<string>? log = writer is null ? null : line => writer.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
        log?.Invoke($"jobs: --run-job {id} with no Tomte running; running what is due here");
        var controller = DispatcherQueueController.CreateOnDedicatedThread();
        var done = new TaskCompletionSource();
        controller.DispatcherQueue.TryEnqueue(() =>
        {
            SynchronizationContext.SetSynchronizationContext(new Program.DispatcherQueueContext(controller.DispatcherQueue));
            _ = Headless(settings, controller.DispatcherQueue, log).ContinueWith(t =>
            {
                if (t.Exception is { } e)
                {
                    log?.Invoke($"jobs: the run failed: {e.InnerException}");
                }

                done.SetResult();
            }, TaskScheduler.Default);
        });
        done.Task.Wait();
        controller.ShutdownQueueAsync().AsTask().Wait();
        return 0;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task Headless(string settings, DispatcherQueue queue, Action<string>? log)
    {
        string data = Path.GetDirectoryName(settings)!;
        var db = new TomteDb(Path.Combine(data, "tomte.db"));
        var note = new ScratchpadNote(db, Path.Combine(data, "scratchpad.md"));
        var shelf = new ShelfStore(db, Path.Combine(data, "Shelf"), action => queue.TryEnqueue(() => action()), log);
        await shelf.Load();
        await note.LoadAsync();
        var next = await NewRunner(db, note, shelf, timer: null, Path.Combine(data, "send-log.jsonl"), settings, resident: false, log).RunDueAsync();
        log?.Invoke($"jobs: next due {next?.ToString("O") ?? "none"}");
        // Whatever the tools wrote is saved before the process ends.
        await note.FlushAsync();
        await shelf.Load();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static JobRunner NewRunner(TomteDb db, ScratchpadNote note, IShelf shelf, IslandTimer? timer, string sendLog, string settings, bool resident, Action<string>? log)
    {
        var http = ProviderSetup.NewHttp();
        var tools = new BuiltInTools(shelf, note, DesktopEffects.CopyToClipboard, DesktopEffects.OpenUrl, timer: timer);
        // The settings file is read at each run, so a cap lowered since a job was made applies to its next run.
        return new JobRunner(new JobStore(db, log), NoJobTasks.For(Environment.ProcessPath!, settings), config => ProviderSetup.Load(config, http), tools,
            new SendLog(sendLog), config => ProviderSetup.OptionsFor(SettingsStore.Load(settings), config))
        {
            Resident = resident,
            Log = log,
        };
    }

    /// <summary>
    /// --debug-job "request" [--debug-answer yes]: the palette opens, the model turns the request into a job, and the
    /// confirmation is answered after 1.5 s: yes only with --debug-answer yes, which needs --debug-settings so the job
    /// lands in the run's own folder. The typed palette does not make jobs yet, which is why this stays a debug switch.
    /// A yes stores the job and registers its real Task Scheduler entry (none in a packaged build). Provider as for
    /// voice: the command line, else settings, else local Ollama.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ScheduleAsync(JobContext c, string request, string[] args)
    {
        var configured = Arg(args, "--provider-id") is null ? Program.Configured(c.Store, c.Log) : null;
        var config = Arg(args, "--provider-id") is { } id
            ? new ProviderConfig(id, Enum.Parse<ProviderKind>(Arg(args, "--provider-kind") ?? nameof(ProviderKind.OpenAICompatible)), Arg(args, "--base-url"), Arg(args, "--model"))
            : configured?.Config ?? new ProviderConfig("ollama", ProviderKind.OpenAICompatible, "http://127.0.0.1:11434/v1", "llama3.2:3b");
        bool yes = Arg(args, "--debug-answer") == "yes";
        using var http = ProviderSetup.NewHttp();
        var planner = new JobPlanner(new JobStore(c.Db, c.Log), NoJobTasks.For(Environment.ProcessPath!, c.Store.FilePath));
        planner.Scheduled += next => Arm(c, next);
        var host = new PaletteHost(c.Island, c.Triggers.Machine, line => c.Writer?.WriteLine(line), Program.Text!);
        var session = new PaletteSession(configured?.Provider ?? ProviderSetup.Load(config, http), config, configured?.Options ?? new PaletteOptions(), new SendLog(c.SendLog),
            new BuiltInTools(new InMemoryShelf(), new InMemoryScratchpad(), _ => { }, _ => { }), new PermissionGrants(), [])
        {
            Scheduling = planner,
            Prompt = request,
        };
        session.ConfirmTool = Answer;
        await host.RunAsync(session);
        foreach (var step in session.Answer.OfType<ToolStepSegment>())
        {
            c.Log?.Invoke($"jobs: step {step.Summary} -> {(step.IsError ? "error: " : "")}{step.Result}");
        }

        c.Log?.Invoke($"jobs: answer {session.AnswerText.ReplaceLineEndings(" | ")}");
        await Task.Delay(2000);
        c.Triggers.Hide();
        PInvoke.PostQuitMessage(0);

        async Task<PermissionAnswer> Answer(ToolRequest asked, CancellationToken ct)
        {
            c.Log?.Invoke($"jobs: confirm \"{asked.Summary}\"");
            await Task.Delay(1500, ct);
            c.Log?.Invoke($"jobs: debug answers {(yes ? "yes" : "no")}");
            return yes ? PermissionAnswer.AllowOnce : PermissionAnswer.Deny;
        }
    }

    /// <summary>--debug-remove-job id: removes the job, its results and its Task Scheduler entry, then quits.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task RemoveAsync(JobContext c, long id)
    {
        var planner = new JobPlanner(new JobStore(c.Db, c.Log), NoJobTasks.For(Environment.ProcessPath!, c.Store.FilePath));
        string? problem = await Task.Run(() => planner.Remove(id));
        c.Log?.Invoke(problem ?? $"jobs: removed job {id} and its task");
        PInvoke.PostQuitMessage(0);
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>The resident app's runner, made on the first due run. Its own class, so its Tomte.AI types load only then.</summary>
    private static class Resident
    {
        private static JobRunner? runner;

        public static JobRunner For(JobContext c)
        {
            if (runner is null)
            {
                runner = NewRunner(c.Db, c.Scratchpad, Shelf ?? (IShelf)new InMemoryShelf(), c.Timer, c.SendLog, c.Store.FilePath, resident: true, c.Log);
                // Through the controller, like every event: it waits for a busy island, a pause, Focus or fullscreen.
                runner.Raised += e =>
                {
                    c.Log?.Invoke($"jobs: event \"{e.Text}\"");
                    c.Triggers.Raise(e);
                };
            }

            return runner;
        }
    }
}
