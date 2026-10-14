using System.Runtime.CompilerServices;
using Tomte.AI;
using Tomte.AI.Jobs;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Jobs;
using Tomte.Core.Storage;
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
/// Scheduled jobs in the shell. At start the only work is a read of the small next-due file beside tomte.db, on the
/// pool: with no file there are no jobs and nothing else happens. With jobs, the job window arms one one-shot for the
/// next run; the database opens and Tomte.AI loads only when a run is due. A --run-job start finds a Tomte already
/// running on the same folder and hands the run over (see <see cref="HandedOff"/>); with none it becomes the resident
/// app and runs what is due. Making a job needs the debug switch until the island can show its confirmation as text.
/// </summary>
internal static class Jobs
{
    // A run that could not reach the database tries again after this, instead of firing again at once.
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(5);

    private static JobWindow? window;
    private static bool running;

    /// <summary>The shelf the Shelf panel uses, so a job reads the same items. Set when the shelf starts.</summary>
    public static ShelfStore? Shelf { get; set; }

    /// <summary>
    /// --run-job (id) while a Tomte with jobs runs on the same data folder: that Tomte gets the id and checks what is
    /// due, and this process quits. It looks at nothing but window titles, so a normal start costs one argument scan.
    /// </summary>
    public static bool HandedOff(string[] args) =>
        JobTask.RunJobArg(args) is long id
        && JobWindow.HandOff(Path.GetDirectoryName(Path.GetFullPath(Arg(args, "--debug-settings") ?? SettingsStore.DefaultPath))!, id);

    public static async Task StartAsync(JobContext c, string[] args)
    {
        try
        {
            if (Arg(args, "--debug-remove-job") is { } remove)
            {
                await RemoveAsync(c, long.Parse(remove, System.Globalization.CultureInfo.InvariantCulture));
                return;
            }

            long? asked = JobTask.RunJobArg(args);
            var next = await Task.Run(() => JobStore.ReadMarker(JobStore.MarkerFor(c.Db.FilePath)));
            c.Log?.Invoke($"jobs: next due {next?.ToString("O") ?? "none"}{(asked is null ? "" : $", started for job {asked}")}");
            if (next is not null || asked is not null)
            {
                // A --run-job start looks in the database at once, whatever the file says.
                Window(c).Arm(asked is null ? next : DateTimeOffset.MinValue);
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

    private static JobWindow Window(JobContext c)
    {
        if (window is null)
        {
            window = new JobWindow(Path.GetDirectoryName(c.Db.FilePath)!);
            window.Due += asked => _ = RunDueAsync(c, asked);
        }

        return window;
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
            next = await Runner.For(c).RunDueAsync();
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
        window!.Arm(next);
    }

    /// <summary>
    /// --debug-job "request" [--debug-answer yes]: the palette opens, the model turns the request into a job, and the
    /// confirmation is answered after 1.5 s: yes only with --debug-answer yes, which needs --debug-settings so the job
    /// lands in the run's own folder. The answer goes to the placeholder the palette shows while it asks, because the
    /// island cannot draw the confirmation's words yet, which is also why this stays a debug switch. A yes stores the
    /// job and registers its real Task Scheduler entry. Provider as for --rambler, local Ollama by default.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task ScheduleAsync(JobContext c, string request, string[] args)
    {
        var config = Arg(args, "--provider-id") is { } id
            ? new ProviderConfig(id, Enum.Parse<ProviderKind>(Arg(args, "--provider-kind") ?? nameof(ProviderKind.OpenAICompatible)), Arg(args, "--base-url"), Arg(args, "--model"))
            : new ProviderConfig("ollama", ProviderKind.OpenAICompatible, "http://127.0.0.1:11434/v1", "llama3.2:3b");
        bool yes = Arg(args, "--debug-answer") == "yes";
        using var http = new HttpClient();
        var planner = new JobPlanner(new JobStore(c.Db), new WindowsTaskScheduler(Environment.ProcessPath!, c.Store.FilePath));
        planner.Scheduled += next => Window(c).Arm(next);
        var host = new PaletteHost(c.Island, c.Triggers.Machine, line => c.Writer?.WriteLine(line));
        var session = new PaletteSession(ProviderSetup.Load(config, http), config, new PaletteOptions(), new SendLog(c.SendLog),
            new BuiltInTools(new InMemoryShelf(), new InMemoryScratchpad(), _ => { }, _ => { }), new PermissionGrants(), [])
        {
            Scheduling = planner,
            Prompt = request,
        };
        session.ConfirmTool = (asked, ct) => host.AskAsync(Answer(asked, ct));
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
        var planner = new JobPlanner(new JobStore(c.Db), new WindowsTaskScheduler(Environment.ProcessPath!, c.Store.FilePath));
        string? problem = await Task.Run(() => planner.Remove(id));
        c.Log?.Invoke(problem ?? $"jobs: removed job {id} and its task");
        PInvoke.PostQuitMessage(0);
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>The one runner, made on the first due run. Its own class, so its Tomte.AI types load only then.</summary>
    private static class Runner
    {
        private static JobRunner? runner;

        public static JobRunner For(JobContext c)
        {
            if (runner is null)
            {
                var http = new HttpClient();
                var tools = new BuiltInTools(Shelf ?? (IShelf)new InMemoryShelf(), c.Scratchpad, DesktopEffects.CopyToClipboard, DesktopEffects.OpenUrl, timer: c.Timer);
                runner = new JobRunner(new JobStore(c.Db), config => ProviderSetup.Load(config, http), tools, new SendLog(c.SendLog), new PaletteOptions());
                // Through the controller, like every event: it waits for a busy island and for Focus or fullscreen.
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
