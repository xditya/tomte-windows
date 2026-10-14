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
/// its result for the palette; each finished run raises "Job done: name" (or "Job failed: name"), and each device
/// action a run made raises its own line, as device actions do in the palette.
/// </summary>
public sealed class JobRunner(JobStore store, Func<ProviderConfig, IChatProvider> provider, BuiltInTools tools, SendLog log, PaletteOptions options)
{
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;

    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;

    /// <summary>Raised on the caller's context for each finished run and each device action a run made.</summary>
    public event Action<IslandEvent>? Raised;

    /// <summary>Claims and runs every job due now, then returns the earliest next run over all jobs (null for none).</summary>
    public async Task<DateTimeOffset?> RunDueAsync()
    {
        var due = await Task.Run(() => store.ClaimDue(Now(), Zone));
        foreach (var (job, at) in due)
        {
            var run = await RunAsync(job, at);
            await Task.Run(() => store.Record(run));
            Raised?.Invoke(new IslandEvent((run.Ok ? "Job done: " : "Job failed: ") + job.Name, Now()));
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
            session = new PaletteSession(provider(job.Provider), job.Provider, options, log, tools, new PermissionGrants(), [])
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
        new(job.Id, job.Name, due, Now(), ok, text, details);
}
