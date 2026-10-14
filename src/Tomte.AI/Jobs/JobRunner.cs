using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Device;
using Tomte.Core.Jobs;
using Tomte.Core.Triggers;

namespace Tomte.AI.Jobs;

/// <summary>
/// Runs the jobs that are due, one after another, through the palette's agent loop with nobody at the keyboard. A run
/// may use only the tools approved when its job was created and never asks: a call to any other tool, or spend past
/// the daily cap, ends it with a result that says so. Each run writes one send-log line with the job's name and keeps
/// its result for the palette. "Running job: name" comes before a run and "Job done: name" or "Job failed: name" after
/// it; each device action a run made raises its own line, as device actions do in the palette.
/// </summary>
/// <param name="options">Asked at each run with the job's provider, so the cap and prices settings hold then apply.</param>
public sealed class JobRunner(JobStore store, IJobTasks tasks, Func<ProviderConfig, IChatProvider> provider, BuiltInTools tools, SendLog log, Func<ProviderConfig, PaletteOptions> options)
{
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;

    // Asked at every look, not kept: the resident runner lives as long as the process, and a new time zone reaches
    // TimeZoneInfo.Local only through a fresh read after ClearCachedData.
    public Func<TimeZoneInfo> Zone { get; init; } = () => TimeZoneInfo.Local;

    /// <summary>
    /// False for a --run-job start with no Tomte running: nobody sees its events, so its results are kept unshown and
    /// the next start raises them. True in the resident app, which raises results kept that way before it runs.
    /// </summary>
    public bool Resident { get; init; } = true;

    public Action<string>? Log { get; init; }

    /// <summary>Raised on the caller's context for each run as it starts and ends, each device action a run made, and each kept result.</summary>
    public event Action<IslandEvent>? Raised;

    /// <summary>The event line for a kept result.</summary>
    public static string EventText(JobRunRecord run) =>
        (run.Ok ? "Job done: " : run.Text == JobStore.Missed ? "Job missed: " : "Job failed: ") + run.JobName;

    /// <summary>Claims and runs every job due now, then returns the earliest next run over all jobs (null for none).</summary>
    public async Task<DateTimeOffset?> RunDueAsync()
    {
        var claims = await Task.Run(() => store.ClaimDue(Now(), Zone()));
        if (Resident)
        {
            foreach (var kept in await Task.Run(store.TakeUnshown))
            {
                Raised?.Invoke(new IslandEvent(EventText(kept), Now()));
            }
        }

        foreach (var (job, due, missed) in claims)
        {
            JobRunRecord run;
            if (missed)
            {
                run = Record(job, due, false, JobStore.Missed, $"Due {due:O}; Tomte did not run within {JobStore.OneOffGrace.TotalHours:0} hours.");
            }
            else
            {
                Raised?.Invoke(new IslandEvent("Running job: " + job.Name, Now()));
                run = await RunAsync(job, due);
            }

            await Try(() => store.Record(run), $"keep the result of job {job.Id}");
            // A one-off has had its turn: its entry would only start Tomte for nothing. Removed only once the result is
            // kept, because in a --run-job start it is the entry that started this very process.
            if (job.Schedule.Date is not null)
            {
                await Try(() => tasks.Remove(job.Id), $"remove the task of job {job.Id}");
            }

            if (Resident)
            {
                Raised?.Invoke(new IslandEvent(EventText(run), Now()));
            }
        }

        return await Task.Run(store.NextDue);
    }

    internal async Task<JobRunRecord> RunAsync(ScheduledJob job, DateTimeOffset due)
    {
        // Checked at every run as well as at creation: a row edited by hand, or a tool whose tier has changed since, must
        // not run unattended.
        if (job.Tools.FirstOrDefault(t => !BuiltInTools.AllowedInScheduledJobs(t)) is { } refused)
        {
            return Record(job, due, false, $"{refused} cannot run in a scheduled job, so this run was refused before anything was sent.", "");
        }

        PaletteSession session;
        try
        {
            session = new PaletteSession(provider(job.Provider), job.Provider, options(job.Provider), log, tools, new PermissionGrants(), [])
            {
                Job = job,
                Prompt = job.Prompt,
                Now = Now,
                // The user approved these tools when the job was made; nothing else is offered to the model.
                ConfirmTool = (request, _) => Task.FromResult(job.Tools.Contains(request.Tool) && request.Tier != ActionTier.Destructive
                    ? PermissionAnswer.AllowOnce
                    : PermissionAnswer.Deny),
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // No model in the stored provider, or its key could not be read.
            return Record(job, due, false, $"The job could not start: {e.Message}", "");
        }

        session.DeviceActionExecuted += e => Raised?.Invoke(new IslandEvent(e.Line, Now()));
        await session.SendAsync();
        var steps = session.Answer.OfType<ToolStepSegment>().Select(s => $"{s.Summary}: {(s.IsError ? "error: " : "")}{s.Result}");
        string text = session.Status == PaletteStatus.Done ? session.AnswerText.Trim() : session.Error ?? $"The run ended: {session.Status}.";
        return Record(job, due, session.Status == PaletteStatus.Done, text, string.Join("\n", [.. steps, session.ProviderIndicator]));
    }

    private JobRunRecord Record(ScheduledJob job, DateTimeOffset due, bool ok, string text, string details) =>
        new(job.Id, job.Name, due, Now(), ok, text, details, Shown: Resident);

    // A disk or database that says no for one run must not stop the others.
    private async Task Try(Action work, string what)
    {
        try
        {
            await Task.Run(work);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Log?.Invoke($"jobs: could not {what} ({e.GetType().Name}: {e.Message})");
        }
    }
}
