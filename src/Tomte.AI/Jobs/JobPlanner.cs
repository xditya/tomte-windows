using System.Globalization;
using System.Text.Json;
using Tomte.AI.Automation;
using Tomte.AI.Browser;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Device;
using Tomte.Core.Jobs;

namespace Tomte.AI.Jobs;

/// <summary>
/// Turns a request such as "every weekday at 9, summarise notes.txt" into a stored job. The model gets schedule_job
/// beside its other tools, with <see cref="Guidance"/> in its system prompt, and fills in a strict structure: a name,
/// the prompt each run sends, a time with days or a date, and the tools the job needs. Tomte checks it, shows it to the
/// user in plain words, and stores it (and its Task Scheduler entry) only after the user says yes. That yes is the
/// pre-approval each later run relies on: a run may use those tools and no others, and never a destructive one. A job
/// that works in Tomte's browser or in the user's apps also carries a plan (its steps), the sites the browser may
/// load and the apps it may work in; the user reads them all on the same card, and a run reaches nothing else.
/// </summary>
public sealed class JobPlanner(JobStore store, IJobTasks tasks)
{
    public const string Tool = "schedule_job";

    public const int MaxName = 60;
    public const int MaxPrompt = 2000;
    public const int MaxSteps = 10;
    public const int MaxStep = 200;

    /// <summary>Clock for "tomorrow" and for the first run; tests set it.</summary>
    public Func<DateTimeOffset> Now { get; init; } = () => DateTimeOffset.Now;

    public TimeZoneInfo Zone { get; init; } = TimeZoneInfo.Local;

    /// <summary>Raised on the session's context after a job was stored, with the earliest next run over all jobs.</summary>
    public event Action<DateTimeOffset?>? Scheduled;

    /// <summary>
    /// The tools a job may ask for: the typed palette's without the destructive ones, the browser tools, and the UI
    /// Automation tools for the apps its plan names.
    /// </summary>
    public static IReadOnlyList<ToolDefinition> JobTools { get; } =
        [.. BuiltInTools.Definitions.Where(d => BuiltInTools.AllowedInScheduledJobs(d.Name)), .. BrowserTools.Definitions, .. AutomationTools.Definitions];

    public static bool IsJobTool(string name) => BuiltInTools.AllowedInScheduledJobs(name) || BrowserTools.Owns(name) || AutomationTools.Owns(name);

    /// <summary>
    /// Added to the system prompt of a session that offers schedule_job beside its other tools: the local date and time,
    /// so "in one minute" and "tomorrow" can be worked out, when to make a job instead of acting now, and its rules. The
    /// tools are not listed: the session offers them, and schedule_job's own list names those a job may use.
    /// </summary>
    public string Guidance
    {
        get
        {
            var now = TimeZoneInfo.ConvertTime(Now(), Zone);
            return $" It is now {now.ToString("dddd d MMMM yyyy, HH:mm", CultureInfo.InvariantCulture)} local time. " +
                "When the user wants something done later, once or on a repeating schedule, do not do it now: turn the request " +
                "into one call of schedule_job. Its prompt is the instruction Tomte follows at each run with nobody at the " +
                "keyboard, so write it in full. Its tools lists every tool that instruction needs, and only those, by name. " +
                "Tools that lock, sleep, restart or shut down the PC never run in a job. If such a request does not say when, " +
                "or you are not sure, call no tool and ask one short question. When the job works in a web page or in an app, steps " +
                "is the plan the user approves: short concrete steps in order, such as open the app, search for the contact, open the " +
                "chat, type the message, press Enter to send; or open the address, click the tab, read the number. sites lists the " +
                "domains the browser tools may visit; apps lists the apps by program name, such as Telegram or notepad, that list_controls, " +
                "click_control, type_in_control, select_control, press_keys and open_app may use. Nothing outside them is reached.";
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
              "tools":{"type":"array","items":{"type":"string","enum":[{{string.Join(",", JobTools.Select(t => $"\"{t.Name}\""))}}]},"description":"Every tool the job needs; empty when it needs none."},
              "steps":{"type":"array","items":{"type":"string"},"description":"The plan, one short step each, in order. Needed with browser or app tools."},
              "sites":{"type":"array","items":{"type":"string"},"description":"The domains the browser may visit, such as youtube.com. Needed with browser tools."},
              "apps":{"type":"array","items":{"type":"string"},"description":"The apps the job works in, by program name, such as Telegram. Needed with list_controls, click_control, type_in_control, select_control or press_keys."}
            },"required":["name","prompt","time","tools"]}
            """).RootElement);

    /// <summary>
    /// The model's call as a job, or a message saying what is wrong with it. Nothing is stored. A destructive tool or a
    /// one-off whose time has passed (by more than <see cref="JobStore.JustPassed"/>) is refused here, before the user is
    /// asked.
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
            if (!BuiltInTools.Definitions.Any(d => d.Name == tool) && !JobTools.Any(d => d.Name == tool))
            {
                return (null, $"There is no tool named {tool}.");
            }

            if (!IsJobTool(tool))
            {
                return (null, $"{tool} is destructive and never runs in a scheduled job. Tell the user it cannot be scheduled.");
            }
        }

        if (Plan(input, tools) is { Error: { } planError })
        {
            return (null, planError);
        }

        if (!JobSchedule.TryRead(input, out var schedule, out var error))
        {
            return (null, error);
        }

        if (JobStore.FirstRun(schedule!, Now(), Zone) is null)
        {
            return (null, "That time has already passed. Ask the user for a time in the future.");
        }

        var (steps, sites, apps, _) = Plan(input, tools);
        return (new JobDraft(name, prompt, schedule!, tools) { Steps = steps, Sites = sites, Apps = apps }, null);
    }

    /// <summary>
    /// The plan's steps, sites and apps from the call, or what is wrong with them: a browser tool needs sites, an app
    /// tool needs apps, and either needs steps.
    /// </summary>
    private static (IReadOnlyList<string> Steps, IReadOnlyList<string> Sites, IReadOnlyList<string> Apps, string? Error) Plan(JsonElement input, IReadOnlyList<string> tools)
    {
        var steps = JobSchedule.Strings(input, "steps").Select(s => s.ReplaceLineEndings(" ").Trim()).Where(s => s.Length > 0).ToList();
        if (steps.Count > MaxSteps || steps.Any(s => s.Length > MaxStep))
        {
            return ([], [], [], $"steps must be at most {MaxSteps} short steps of up to {MaxStep} characters each.");
        }

        var sites = new List<string>();
        foreach (string text in JobSchedule.Strings(input, "sites"))
        {
            if (BrowserTools.Site(text) is not { } site)
            {
                return ([], [], [], $"\"{text}\" is not a site. Give domain names such as youtube.com.");
            }

            if (!sites.Contains(site))
            {
                sites.Add(site);
            }
        }

        var apps = new List<string>();
        foreach (string text in JobSchedule.Strings(input, "apps").Select(a => a.Trim()))
        {
            if (text.Length is 0 or > MaxName || text.IndexOfAny(['\\', '/', ':', '"', '\r', '\n']) >= 0)
            {
                return ([], [], [], $"\"{text}\" is not an app name. Give program names such as Telegram or notepad.");
            }

            if (!apps.Any(a => AppRules.Normalize(a) == AppRules.Normalize(text)))
            {
                apps.Add(text);
            }
        }

        bool browser = tools.Any(BrowserTools.Owns), automation = tools.Any(AutomationTools.Owns);
        string? error = browser && sites.Count == 0 ? "sites must list the domains the browser may visit, such as youtube.com, since tools has browser tools."
            : automation && apps.Count == 0 ? "apps must list the apps the job works in, such as Telegram, since tools has app tools."
            : (browser || automation) && steps.Count == 0 ? "steps must give the plan, one short step each, since the job works in the browser or in apps."
            : null;
        return (steps, sites, apps, error);
    }

    /// <summary>
    /// What a run sends as its prompt: the stored prompt, then the approved plan with the sites and apps it keeps to.
    /// A job made without a plan sends its prompt alone.
    /// </summary>
    public static string RunPrompt(ScheduledJob job) =>
        job.Steps.Count == 0 && job.Sites.Count == 0 && job.Apps.Count == 0 ? job.Prompt
            : string.Join('\n', [
                job.Prompt,
                "",
                "The plan the user approved. Follow it step by step:",
                .. job.Steps.Select((step, i) => $"{i + 1}. {step}"),
                .. job.Sites.Count == 0 ? Array.Empty<string>() : [$"The browser may only load {JobSchedule.JoinWords(job.Sites)} and their subdomains."],
                .. job.Apps.Count == 0 ? Array.Empty<string>() : [$"Only these apps may be used: {JobSchedule.JoinWords(job.Apps)}."],
            ]);

    /// <summary>
    /// What the user is asked to confirm, the stored prompt whole, since it is what each run follows with the approved
    /// tools: "Every weekday at 09:00: Summarise notes.txt. Uses read_shelf_item. Sends to ollama · llama3.2:3b each time."
    /// A job with a plan gets a line per step, then its sites and apps, then the tools and the provider.
    /// </summary>
    public static string Confirmation(JobDraft draft, ProviderConfig provider)
    {
        string prompt = draft.Prompt.ReplaceLineEndings(" ").Trim();
        string tools = draft.Tools.Count == 0 ? "no tools" : JobSchedule.JoinWords(draft.Tools);
        string head = $"{draft.Schedule.Describe()}: {prompt}{(".!?".Contains(prompt[^1]) ? "" : ".")}";
        string tail = $"Uses {tools}. Sends to {ProviderSetup.NameOf(provider.Id)} · {provider.DefaultModel} each time.";
        if (draft.Steps.Count == 0 && draft.Sites.Count == 0 && draft.Apps.Count == 0)
        {
            return $"{head} {tail}";
        }

        string reach = string.Join(' ', [
            .. draft.Sites.Count == 0 ? Array.Empty<string>() : [$"Sites: {string.Join(", ", draft.Sites)}."],
            .. draft.Apps.Count == 0 ? Array.Empty<string>() : [$"Apps: {string.Join(", ", draft.Apps)}."],
        ]);
        return string.Join('\n', [head, .. draft.Steps.Select((step, i) => $"{i + 1}. {step}"), .. reach.Length == 0 ? Array.Empty<string>() : [reach], tail]);
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
        // The card shows the prompt and the plan whole or not at all: an instruction past what the user can read must not run unread.
        if (!PaletteLayout.FitsJobCard(summary))
        {
            return (Tool, draft.Steps.Count == 0
                ? "prompt is too long to show the user in full. Shorten it and call schedule_job again."
                : "prompt and steps are too long to show the user in full. Shorten them and call schedule_job again.", true);
        }

        if (await confirm(new ToolRequest(Tool, summary, ActionTier.ExternalEffect, OfferAlwaysAllow: false), ct) == PermissionAnswer.Deny)
        {
            return (summary, PaletteSession.NotAllowed, true);
        }

        // A one-off taken within JobStore.JustPassed of its time can be gone by the yes; one with no run left is not
        // stored. The same moment decides it and the stored first run.
        var now = Now();
        if (JobStore.FirstRun(draft.Schedule, now, Zone) is null)
        {
            return (summary, "That time passed before the user said yes, so nothing was scheduled. Ask the user for a new time.", true);
        }

        var (job, taskError, next) = await Task.Run(() =>
        {
            var (made, problem) = Create(draft, provider, now);
            return (made, problem, store.NextDue());
        }, ct);
        Scheduled?.Invoke(next);
        string first = TimeZoneInfo.ConvertTime(job.NextDue!.Value, Zone).ToString("dddd d MMMM 'at' HH:mm", CultureInfo.InvariantCulture);
        // Small models call the tool again after a success; the result tells them the work is done.
        return (summary, $"Scheduled as job {job.Id}; the first run is {first}." + (taskError is null ? ""
            : $" Windows did not take its task ({taskError}), so it runs only while Tomte is running.") +
            " Do not call schedule_job again for this request; tell the user in one short sentence.", false);
    }

    /// <summary>
    /// Stores a confirmed job with its first run from <paramref name="now"/> (the clock when not given), then its Task
    /// Scheduler entry. Blocks: worker thread only. A refused entry keeps the job.
    /// </summary>
    public (ScheduledJob Job, string? TaskError) Create(JobDraft draft, ProviderConfig provider, DateTimeOffset? now = null)
    {
        var job = store.Add(draft, provider, now ?? Now(), Zone);
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
