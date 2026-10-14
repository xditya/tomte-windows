using System.Globalization;
using System.Text.Json;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Device;
using Tomte.Core.Jobs;

namespace Tomte.AI.Jobs;

/// <summary>
/// Turns a request such as "every weekday at 9, summarise notes.txt" into a stored job. The model gets one tool,
/// schedule_job, and fills in a strict structure: a name, the prompt each run sends, a time with days or a date, and
/// the tools the job needs. Tomte checks it, shows it to the user in plain words, and stores it (and its Task Scheduler
/// entry) only after the user says yes. That yes is the pre-approval each later run relies on: a run may use those
/// tools and no others, and never a destructive one.
/// </summary>
public sealed class JobPlanner(JobStore store, IJobTasks tasks)
{
    public const string Tool = "schedule_job";

    public const int MaxName = 60;
    public const int MaxPrompt = 2000;

    // The prompt in the confirmation; the whole prompt is stored and sent.
    private const int PromptPreview = 80;

    /// <summary>Clock for "tomorrow" and for the first run; tests set it.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;

    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;

    /// <summary>Raised on the session's context after a job was stored, with the earliest next run over all jobs.</summary>
    public event Action<DateTimeOffset?>? Scheduled;

    /// <summary>The tools a job may ask for: the typed palette's, without the destructive ones.</summary>
    public static IReadOnlyList<ToolDefinition> JobTools { get; } = [.. BuiltInTools.Definitions.Where(d => BuiltInTools.AllowedInScheduledJobs(d.Name))];

    public string Prompt
    {
        get
        {
            var now = TimeZoneInfo.ConvertTime(Now(), Zone);
            return "You are Tomte, a quiet helper on the user's Windows PC. The user wants something done later, once or " +
                "on a repeating schedule. Turn the request into one call of schedule_job. " +
                $"It is now {now.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture)} local time. " +
                "prompt is the instruction Tomte follows at each run with nobody at the keyboard, so write it in full. " +
                "tools lists every tool that instruction needs, and only those, by name: " +
                string.Join("; ", JobTools.Select(t => $"{t.Name} ({t.Description.TrimEnd('.')})")) + ". " +
                "Tools that lock, sleep, restart or shut down the PC never run in a job. If the request does not say when, " +
                "or you are not sure, call no tool and ask one short question.";
        }
    }

    public ToolDefinition Definition { get; } = new(Tool,
        "Schedule a job that Tomte runs later on its own: once on a date, or on chosen days of the week, at a time.",
        JsonDocument.Parse($$"""
            {"type":"object","properties":{
              "name":{"type":"string","description":"A short name for the job, a few words."},
              "prompt":{"type":"string","description":"What Tomte does at each run, as a complete instruction."},
              "time":{"type":"string","description":"Local time of day, 24-hour HH:MM."},
              "days":{"type":"array","items":{"type":"string","enum":["mon","tue","wed","thu","fri","sat","sun"]},"description":"For a repeating job: the days of the week it runs."},
              "date":{"type":"string","description":"For a one-off job: the day it runs, YYYY-MM-DD."},
              "tools":{"type":"array","items":{"type":"string","enum":[{{string.Join(",", JobTools.Select(t => $"\"{t.Name}\""))}}]},"description":"Every tool the job needs; empty when it needs none."}
            },"required":["name","prompt","time","tools"]}
            """).RootElement);

    /// <summary>
    /// The model's call as a job, or a message saying what is wrong with it. Nothing is stored. A destructive tool or a
    /// one-off whose time has passed is refused here, before the user is asked.
    /// </summary>
    public (JobDraft? Draft, string? Error) Read(JsonElement input)
    {
        if (input.ValueKind != JsonValueKind.Object)
        {
            return (null, "Arguments must be a JSON object.");
        }

        string name = Text(input, "name");
        if (name.Length is 0 or > MaxName)
        {
            return (null, $"name must be a short name of 1 to {MaxName} characters.");
        }

        string prompt = Text(input, "prompt");
        if (prompt.Length is 0 or > MaxPrompt)
        {
            return (null, $"prompt must say what the job does, in 1 to {MaxPrompt} characters.");
        }

        var tools = JobSchedule.Strings(input, "tools").Distinct(StringComparer.Ordinal).ToList();
        foreach (var tool in tools)
        {
            if (!BuiltInTools.Definitions.Any(d => d.Name == tool))
            {
                return (null, $"There is no tool named {tool}.");
            }

            if (!BuiltInTools.AllowedInScheduledJobs(tool))
            {
                return (null, $"{tool} is destructive and never runs in a scheduled job. Tell the user it cannot be scheduled.");
            }
        }

        if (!JobSchedule.TryRead(input, out var schedule, out var error))
        {
            return (null, error);
        }

        if (schedule!.Next(Now(), Zone) is null)
        {
            return (null, "That time has already passed. Ask the user for a time in the future.");
        }

        return (new JobDraft(name, prompt, schedule, tools), null);
    }

    /// <summary>
    /// What the user is asked to confirm: "Every weekday at 09:00: Summarise notes.txt using read_shelf_item. Sends to
    /// ollama · llama3.2:3b each time."
    /// </summary>
    public static string Confirmation(JobDraft draft, ProviderConfig provider)
    {
        string prompt = draft.Prompt.ReplaceLineEndings(" ").Trim().TrimEnd('.', '!', '?');
        if (prompt.Length > PromptPreview)
        {
            prompt = prompt[..PromptPreview].TrimEnd() + "...";
        }

        string tools = draft.Tools.Count == 0 ? "with no tools" : "using " + JobSchedule.JoinWords(draft.Tools);
        return $"{draft.Schedule.Describe()}: {prompt} {tools}. Sends to {provider.Id} · {provider.DefaultModel} each time.";
    }

    /// <summary>
    /// The schedule_job step of a palette session: check the call, ask the user with the plain-words summary (never with
    /// "always allow"), and only on a yes store the job and its task. Returns the step's summary and its result.
    /// </summary>
    internal async Task<(string Summary, string Result, bool IsError)> ScheduleAsync(
        ToolCallPart call, ProviderConfig provider, Func<ToolRequest, CancellationToken, Task<PermissionAnswer>> confirm, CancellationToken ct)
    {
        var (draft, error) = Read(call.Input);
        if (draft is null)
        {
            return (Tool, error!, true);
        }

        string summary = Confirmation(draft, provider);
        if (await confirm(new ToolRequest(Tool, summary, ActionTier.ExternalEffect, OfferAlwaysAllow: false), ct) == PermissionAnswer.Deny)
        {
            return (summary, PaletteSession.NotAllowed, true);
        }

        var (job, taskError, next) = await Task.Run(() =>
        {
            var (made, problem) = Create(draft, provider);
            return (made, problem, store.NextDue());
        }, ct);
        Scheduled?.Invoke(next);
        string first = TimeZoneInfo.ConvertTime(job.NextDue!.Value, Zone).ToString("dddd d MMMM 'at' HH:mm", CultureInfo.InvariantCulture);
        // Small models call the tool again after a success; the result tells them the work is done.
        return (summary, $"Scheduled as job {job.Id}; the first run is {first}." + (taskError is null ? ""
            : $" Windows did not take its task ({taskError}), so it runs only while Tomte is running.") +
            " Do not call schedule_job again for this request; tell the user in one short sentence.", false);
    }

    /// <summary>Stores a confirmed job, then its Task Scheduler entry. Blocks: worker thread only. A refused entry keeps the job.</summary>
    public (ScheduledJob Job, string? TaskError) Create(JobDraft draft, ProviderConfig provider)
    {
        var job = store.Add(draft, provider, Now(), Zone);
        try
        {
            tasks.Register(job);
            return (job, null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return (job, e.Message);
        }
    }

    /// <summary>Removes a job, its results and its Task Scheduler entry. Blocks: worker thread only. Null, or what went wrong.</summary>
    public string? Remove(long id)
    {
        if (!store.Remove(id))
        {
            return $"There is no job {id}.";
        }

        try
        {
            tasks.Remove(id);
            return null;
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return $"Job {id} is removed, but Windows did not remove its task ({e.Message}).";
        }
    }

    private static string Text(JsonElement input, string name) =>
        input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : "";
}
