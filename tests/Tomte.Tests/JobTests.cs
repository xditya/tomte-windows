extern alias ai;

using System.Text.Json;
using System.Text.Json.Nodes;
using System.Xml.Linq;
using Tomte.AI;
using Tomte.AI.Jobs;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Device;
using Tomte.Core.Jobs;
using Tomte.Core.Storage;
using Tomte.Core.Triggers;
using Windows.System;
using Xunit;

namespace Tomte.Tests;

/// <summary>
/// Scheduled jobs: making one from a request, storing it, its Task Scheduler entry, running it unattended, the clock
/// that arms the next run, and the hand-off of a --run-job start. The model answers were recorded from llama3.2:3b on
/// local Ollama, answering the requests these sessions send, bad and unclear answers included.
/// </summary>
public sealed class JobTests : IDisposable
{
    private static readonly TimeZoneInfo India = TimeZoneInfo.FindSystemTimeZoneById("India Standard Time");
    private static readonly TimeSpan Ist = TimeSpan.FromHours(5.5);

    // When the requests were recorded: "now" in the scheduling prompt, so the past one-off reads as it did then.
    private static readonly DateTimeOffset Recorded = new(2026, 10, 14, 13, 45, 0, Ist);
    private static readonly DateTimeOffset Monday0800 = new(2026, 10, 19, 8, 0, 0, Ist);
    private static readonly DateTimeOffset Monday0900 = new(2026, 10, 19, 9, 0, 0, Ist);
    private static readonly JobSchedule WeekdaysAtNine = new()
    {
        Time = new TimeOnly(9, 0),
        Days = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday],
    };

    private static readonly ProviderConfig Ollama = new("ollama", ProviderKind.OpenAICompatible, "http://127.0.0.1:11434/v1", OllamaFactAttribute.Model);
    private const string Exe = @"C:\Tomte\Tomte.Shell.exe";

    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tomte-jobs-{Guid.NewGuid():N}");
    private readonly JobStore _store;
    private readonly FakeTasks _tasks = new();
    private readonly InMemoryShelf _shelf = new();
    private readonly List<string> _ran = [];
    private readonly List<ToolRequest> _asked = [];
    private readonly List<string> _logged = [];
    private PermissionAnswer _answer = PermissionAnswer.AllowOnce;

    public JobTests()
    {
        _store = new JobStore(new TomteDb(Path.Combine(_dir, "tomte.db")), _logged.Add);
        _shelf.CreateFile("notes.txt", "Milk, eggs, bread.\nCall the dentist on Monday at 10.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
            Directory.Delete(_dir, recursive: true);
    }

    private string LogPath => Path.Combine(_dir, "send-log.jsonl");

    // Every effect outside the app is a fake that only notes it was asked for.
    private BuiltInTools Tools() =>
        new(_shelf, new InMemoryScratchpad(), t => _ran.Add("clipboard " + t), u => _ran.Add("open " + u), (name, _) =>
        {
            _ran.Add(name);
            return ActionResult.Done(name + " done.");
        });

    private static RecordedHandler Handler(string name, int turns) =>
        new($"openai-ollama-job-{name}-1.sse", then: [.. Enumerable.Range(2, turns - 1).Select(i => $"openai-ollama-job-{name}-{i}.sse")]);

    private (PaletteSession Session, RecordedHandler Handler, JobPlanner Planner) Planning(string name, int turns)
    {
        var handler = Handler(name, turns);
        var planner = new JobPlanner(_store, _tasks) { Now = () => Recorded, Zone = India };
        var session = new PaletteSession(new OpenAICompatibleProvider(new HttpClient(handler), Ollama.BaseUrl!, null), Ollama, new PaletteOptions(),
            new SendLog(LogPath), Tools(), new PermissionGrants(), [])
        {
            Scheduling = planner,
            Prompt = "the user's request",
            ConfirmTool = (request, _) =>
            {
                _asked.Add(request);
                return Task.FromResult(_answer);
            },
        };
        return (session, handler, planner);
    }

    private JobRunner Runner(string name, int turns, out RecordedHandler handler, PaletteOptions? options = null, bool resident = true, DateTimeOffset? now = null)
    {
        var recorded = handler = Handler(name, turns);
        return new JobRunner(_store, _tasks, _ => new OpenAICompatibleProvider(new HttpClient(recorded), Ollama.BaseUrl!, null), Tools(), new SendLog(LogPath), options ?? new PaletteOptions())
        {
            Now = () => now ?? Monday0900.AddSeconds(30),
            Zone = India,
            Resident = resident,
            Log = _logged.Add,
        };
    }

    /// <summary>A connection to the test's tomte.db for changing rows by hand, as a damaged file or a hand edit would.</summary>
    private void Sql(string sql)
    {
        using var connection = new TomteDb(Path.Combine(_dir, "tomte.db")).Open("jobs", []);
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    /// <summary>A weekday job made on Monday at 08:00, so its first run is Monday at 09:00.</summary>
    private ScheduledJob AddJob(string prompt, params string[] tools) =>
        _store.Add(new JobDraft("Morning notes", prompt, WeekdaysAtNine, tools), Ollama, Monday0800, India);

    private static List<string> ToolNames(string body) =>
        [.. JsonNode.Parse(body)!["tools"]!.AsArray().Select(t => t!["function"]!["name"]!.GetValue<string>())];

    // ---- Making a job ----

    [Fact]
    public async Task AWeekdayJobIsShownInPlainWordsAndStoredOnlyAfterTheYes()
    {
        var (session, handler, planner) = Planning("weekday", 2);
        DateTimeOffset? armed = null;
        planner.Scheduled += next => armed = next;

        await session.SendAsync();

        // The model's own words, cut, as the user would read them before saying yes.
        var asked = Assert.Single(_asked);
        Assert.Equal("Every weekday at 09:00: Read the text of an item on the user\\u2019s shelf called notes.txt. Write the co... "
            + "using read_shelf_item and write_scratchpad. Sends to ollama · llama3.2:3b each time.", asked.Summary);
        Assert.False(asked.OfferAlwaysAllow);
        var job = Assert.Single(_store.All());
        Assert.Equal("daily notes", job.Name);
        Assert.Equal(["read_shelf_item", "write_scratchpad"], job.Tools);
        Assert.Equal(WeekdaysAtNine.Days, job.Schedule.Days);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 9, 0, 0, Ist), job.NextDue);
        Assert.Equal(Ollama, job.Provider);
        Assert.Equal(job.NextDue, armed);
        Assert.Equal(job.NextDue, JobStore.ReadMarker(_store.MarkerPath));
        Assert.Equal([job.Id], _tasks.Registered.Select(j => j.Id));
        Assert.StartsWith($"Scheduled as job {job.Id}; the first run is Thursday 15 October at 09:00.", session.Answer.OfType<ToolStepSegment>().First().Result);

        // What left the PC: the scheduling prompt with the date, the request, and schedule_job as the only tool.
        var first = JsonNode.Parse(handler.Bodies[0])!;
        Assert.Contains("It is now Wednesday 14 October 2026, 13:45 local time.", planner.Prompt);
        Assert.Equal(planner.Prompt, first["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal("the user's request", first["messages"]![1]!["content"]!.GetValue<string>());
        Assert.Equal([JobPlanner.Tool], ToolNames(handler.Bodies[0]));
        Assert.Single(new SendLog(LogPath).ReadAll());
    }

    [Fact]
    public async Task NoStoresNothing()
    {
        _answer = PermissionAnswer.Deny;
        var (session, _, _) = Planning("weekday", 2);

        await session.SendAsync();

        Assert.Single(_asked);
        Assert.Empty(_store.All());
        Assert.Empty(_tasks.Registered);
        Assert.False(File.Exists(_store.MarkerPath));
        Assert.Equal(PaletteSession.NotAllowed, session.Answer.OfType<ToolStepSegment>().First().Result);
    }

    [Fact]
    public async Task AOneOffJobIsStoredForItsDay()
    {
        var (session, _, _) = Planning("once", 2);

        await session.SendAsync();

        Assert.Equal("On Thursday 15 October 2026 at 08:30: open https://example.com/news in your browser using open_url. Sends to ollama · llama3.2:3b each time.",
            Assert.Single(_asked).Summary);
        var job = Assert.Single(_store.All());
        Assert.Equal(new DateOnly(2026, 10, 15), job.Schedule.Date);
        Assert.Equal(new DateTimeOffset(2026, 10, 15, 8, 30, 0, Ist), job.NextDue);
        Assert.Equal(["open_url"], job.Tools);
        Assert.Single(XDocument.Parse(_tasks.Xml(job)).Descendants(TaskNs + "TimeTrigger"));
    }

    [Fact]
    public async Task AnUnclearRequestStoresNothingAndTheModelAsks()
    {
        var (session, _, _) = Planning("unclear", 2);

        await session.SendAsync();

        Assert.Empty(_asked);
        Assert.Empty(_store.All());
        Assert.Equal("time is missing. Ask the user at what time the job should run.", Assert.Single(session.Answer.OfType<ToolStepSegment>()).Result);
        Assert.Contains("What time would you like me to remind you to stretch?", session.AnswerText);
    }

    [Fact]
    public async Task ARequestWithNoDaysAndNoDateIsSentBack()
    {
        var (session, _, _) = Planning("nodays", 2);

        await session.SendAsync();

        Assert.Empty(_asked);
        Assert.Empty(_store.All());
        Assert.StartsWith("Say when the job runs", Assert.Single(session.Answer.OfType<ToolStepSegment>()).Result);
    }

    [Fact]
    public async Task AOneOffWhoseTimeHasPassedIsRefused()
    {
        var (session, _, _) = Planning("past", 3);

        await session.SendAsync();

        Assert.Empty(_asked);
        Assert.Empty(_store.All());
        Assert.Equal("That time has already passed. Ask the user for a time in the future.", session.Answer.OfType<ToolStepSegment>().First().Result);
    }

    [Fact]
    public async Task ADestructiveToolIsRefusedAtCreation()
    {
        var (session, _, planner) = Planning("shutdown", 2);

        await session.SendAsync();

        Assert.Empty(_asked);
        Assert.Empty(_store.All());
        Assert.Equal("shut_down_pc is destructive and never runs in a scheduled job. Tell the user it cannot be scheduled.",
            Assert.Single(session.Answer.OfType<ToolStepSegment>()).Result);
        // Every power tool, however right the rest of the call is.
        foreach (var tool in new[] { BuiltInTools.LockPc, BuiltInTools.SleepPc, BuiltInTools.RestartPc, BuiltInTools.ShutDownPc })
        {
            using var call = JsonDocument.Parse($$"""{"name":"n","prompt":"p","time":"09:00","days":["mon"],"tools":["read_shelf_item","{{tool}}"]}""");
            Assert.Equal($"{tool} is destructive and never runs in a scheduled job. Tell the user it cannot be scheduled.", planner.Read(call.RootElement).Error);
        }

        Assert.DoesNotContain(JobPlanner.JobTools, t => BuiltInTools.Tiers[t.Name] == ActionTier.Destructive);
    }

    [Fact]
    public void AJobIsCheckedBeforeTheUserIsAsked()
    {
        var planner = new JobPlanner(_store, _tasks) { Now = () => Recorded, Zone = India };
        string? Error(string json)
        {
            using var call = JsonDocument.Parse(json);
            return planner.Read(call.RootElement).Error;
        }

        Assert.Equal("There is no tool named awk.", Error("""{"name":"n","prompt":"p","time":"09:00","days":["mon"],"tools":"[\"read_shelf_item\", \"awk\"]"}"""));
        // Voice-only tools and MCP tools are not offered to jobs.
        Assert.Equal("There is no tool named save_note.", Error("""{"name":"n","prompt":"p","time":"09:00","days":["mon"],"tools":["save_note"]}"""));
        Assert.Equal("name must be a short name of 1 to 60 characters.", Error("""{"name":"","prompt":"p","time":"09:00","days":["mon"],"tools":[]}"""));
        Assert.Equal("prompt must say what the job does, in 1 to 2000 characters.", Error("""{"name":"n","time":"09:00","days":["mon"],"tools":[]}"""));
        Assert.Null(Error("""{"name":"n","prompt":"p","time":"09:00","days":["mon"],"tools":[]}"""));
    }

    [Fact]
    public async Task ATaskWindowsRefusesKeepsTheJobAndSaysSo()
    {
        _tasks.Fail = true;
        var (session, _, _) = Planning("weekday", 2);

        await session.SendAsync();

        Assert.Single(_store.All());
        Assert.Contains("Windows did not take its task (Access is denied.), so it runs only while Tomte is running.",
            session.Answer.OfType<ToolStepSegment>().First().Result);
    }

    [Fact]
    public void RemovingAJobRemovesItsTaskAndItsResults()
    {
        var planner = new JobPlanner(_store, _tasks) { Now = () => Monday0800, Zone = India };
        var (job, problem) = planner.Create(new JobDraft("Morning notes", "p", WeekdaysAtNine, []), Ollama);
        Assert.Null(problem);
        _store.Record(new JobRunRecord(job.Id, job.Name, Monday0900, Monday0900, true, "ok", ""));

        Assert.Null(planner.Remove(job.Id));

        Assert.Equal([job.Id], _tasks.Removed);
        Assert.Empty(_store.All());
        Assert.Empty(_store.Results());
        Assert.False(File.Exists(_store.MarkerPath));
        Assert.Equal($"There is no job {job.Id}.", planner.Remove(job.Id));
    }

    // ---- The Task Scheduler entry ----

    private static readonly XNamespace TaskNs = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    [Fact]
    public void TheTaskStartsTomteWithRunJobAndTheSameSettings()
    {
        Assert.Equal("--run-job 7", JobTask.Arguments(7, SettingsStore.DefaultPath));
        string mine = Path.Combine(_dir, "settings.json");
        Assert.Equal($"--debug-settings \"{mine}\" --run-job 7", JobTask.Arguments(7, mine));
        Assert.Equal(7, JobTask.RunJobArg(["--debug-settings", mine, "--run-job", "7"]));
        Assert.Null(JobTask.RunJobArg(["--run-job"]));
        Assert.Null(JobTask.RunJobArg(["--run-job", "-1"]));
        Assert.Null(JobTask.RunJobArg([]));
        // Each data folder has its own task names, so a test folder never touches the real folder's entries.
        Assert.StartsWith("Tomte job 7 ", JobTask.Name(7, _dir));
        Assert.NotEqual(JobTask.Name(7, _dir), JobTask.Name(7, Path.GetDirectoryName(SettingsStore.DefaultPath)!));
    }

    [Fact]
    public void TheTaskHasTheTriggerTheUserAndTheCommand()
    {
        var job = AddJob("p", "read_shelf_item");

        var xml = XDocument.Parse(_tasks.Xml(job));

        var trigger = xml.Descendants(TaskNs + "CalendarTrigger").Single();
        Assert.Equal($"{DateOnly.FromDateTime(job.NextDue!.Value.LocalDateTime):yyyy-MM-dd}T09:00:00", trigger.Element(TaskNs + "StartBoundary")!.Value);
        Assert.Equal(["Monday", "Tuesday", "Wednesday", "Thursday", "Friday"], trigger.Descendants(TaskNs + "DaysOfWeek").Elements().Select(e => e.Name.LocalName));
        string Value(string name) => xml.Descendants(TaskNs + name).Single().Value;
        Assert.Equal("InteractiveToken", Value("LogonType"));
        Assert.Equal("LeastPrivilege", Value("RunLevel"));
        Assert.Equal("true", Value("StartWhenAvailable"));
        Assert.Equal("false", Value("WakeToRun"));
        Assert.Equal("false", Value("DisallowStartIfOnBatteries"));
        Assert.Equal("PT10M", Value("ExecutionTimeLimit"));
        Assert.Equal("4", Value("Priority"));
        Assert.Equal("IgnoreNew", Value("MultipleInstancesPolicy"));
        Assert.Equal(Exe, Value("Command"));
        Assert.Equal($"--debug-settings \"{FakeTasks.Settings}\" --run-job {job.Id}", Value("Arguments"));
    }

    [Fact]
    public void TaskSchedulerTakesTheXmlWithoutAnythingBeingRegistered()
    {
        var weekly = AddJob("p", "read_shelf_item");
        var once = _store.Add(new JobDraft("Once", "p", new JobSchedule { Time = new TimeOnly(8, 30), Date = new DateOnly(2030, 1, 1) }, []), Ollama, Monday0800, India);

        // A definition built in memory from the XML; nothing is registered, changed or deleted.
        Assert.Null(WindowsTaskScheduler.Check(_tasks.Xml(weekly)));
        Assert.Null(WindowsTaskScheduler.Check(_tasks.Xml(once)));
        // The check is real: an element Task Scheduler does not know is refused.
        Assert.NotNull(WindowsTaskScheduler.Check(_tasks.Xml(weekly).Replace("<Actions ", "<Nope ").Replace("</Actions>", "</Nope>")));
    }

    // ---- Storing ----

    [Fact]
    public void AJobAndItsResultsSurviveARestart()
    {
        var job = AddJob("Read the shelf item notes.txt and summarise it in one sentence.", "read_shelf_item");
        _store.Record(new JobRunRecord(job.Id, job.Name, Monday0900, Monday0900.AddMinutes(1), false, "It failed.", "a step"));

        var reopened = new JobStore(new TomteDb(Path.Combine(_dir, "tomte.db")));

        var back = Assert.Single(reopened.All());
        Assert.Equal((job.Id, job.Name, job.Prompt, job.NextDue, job.Provider), (back.Id, back.Name, back.Prompt, back.NextDue, back.Provider));
        Assert.Equal(job.Tools, back.Tools);
        Assert.Equal(job.Schedule.ToJson(), back.Schedule.ToJson());
        Assert.Equal(Monday0900, JobStore.ReadMarker(reopened.MarkerPath));
        var run = Assert.Single(reopened.Results());
        Assert.Equal((job.Id, "It failed.", false, Monday0900, "a step"), (run.JobId, run.Text, run.Ok, run.Due, run.Details));
    }

    [Fact]
    public void TheNextDueFileIsAbsentWithNoJobsAndADamagedOneReadsAsDueNow()
    {
        Assert.Null(JobStore.ReadMarker(_store.MarkerPath));
        Directory.CreateDirectory(_dir);
        File.WriteAllText(_store.MarkerPath, "not a time");
        Assert.Equal(DateTimeOffset.MinValue, JobStore.ReadMarker(_store.MarkerPath));
        // A number past what a DateTimeOffset holds is a damaged file too, not a crash at start.
        File.WriteAllText(_store.MarkerPath, "253402300800000");
        Assert.Equal(DateTimeOffset.MinValue, JobStore.ReadMarker(_store.MarkerPath));
        File.WriteAllText(_store.MarkerPath, "253402300799999");
        Assert.Equal(DateTimeOffset.MaxValue.ToUnixTimeMilliseconds(), JobStore.ReadMarker(_store.MarkerPath)!.Value.ToUnixTimeMilliseconds());
        File.WriteAllText(_store.MarkerPath, "0");
        Assert.Equal(DateTimeOffset.UnixEpoch, JobStore.ReadMarker(_store.MarkerPath));
    }

    [Fact]
    public void ARunMissedWhileAsleepIsTakenOnceAndTheNextIsInTheFuture()
    {
        AddJob("p", "read_shelf_item");
        // Asleep from Monday 08:30 to Thursday 11:00: the runs at nine on Monday to Thursday all passed. Today's, two
        // hours late, is taken once.
        var thursday = new DateTimeOffset(2026, 10, 22, 11, 0, 0, Ist);

        var (claimed, due, missed) = Assert.Single(_store.ClaimDue(thursday, India));

        Assert.False(missed);
        Assert.Equal(new DateTimeOffset(2026, 10, 22, 9, 0, 0, Ist), due);
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 9, 0, 0, Ist), claimed.NextDue);
        Assert.Empty(_store.ClaimDue(thursday.AddMinutes(1), India));
        Assert.Equal(claimed.NextDue, JobStore.ReadMarker(_store.MarkerPath));
    }

    [Fact]
    public void ARepeatingRunMoreThanSixHoursLateIsSkipped()
    {
        _store.Add(new JobDraft("Morning notes", "p", WeekdaysAtNine, []), Ollama, Monday0800, India);
        // Asleep from Monday 08:30 to Thursday 16:00: the latest run, at nine today, is seven hours late.
        var thursday = new DateTimeOffset(2026, 10, 22, 16, 0, 0, Ist);

        Assert.Empty(_store.ClaimDue(thursday, India));
        Assert.Equal(new DateTimeOffset(2026, 10, 23, 9, 0, 0, Ist), Assert.Single(_store.All()).NextDue);
        Assert.Contains(_logged, line => line.Contains("skipped"));
    }

    [Fact]
    public void AOneOffMissedWhileTomteWasClosedRunsOnceAndIsThenDone()
    {
        _store.Add(new JobDraft("Once", "p", new JobSchedule { Time = new TimeOnly(9, 0), Date = new DateOnly(2026, 10, 19) }, []), Ollama, Monday0800, India);

        var (claimed, due, missed) = Assert.Single(_store.ClaimDue(Monday0900.AddHours(20), India));

        Assert.False(missed);
        Assert.Equal(Monday0900, due);
        Assert.Null(claimed.NextDue);
        Assert.Null(_store.NextDue());
        Assert.False(File.Exists(_store.MarkerPath));
        Assert.Single(_store.All());
    }

    [Fact]
    public async Task AOneOffMoreThanADayLateIsKeptAsMissedAndNotRun()
    {
        var once = _store.Add(new JobDraft("Once", "p", new JobSchedule { Time = new TimeOnly(9, 0), Date = new DateOnly(2026, 10, 19) }, []), Ollama, Monday0800, India);
        var runner = Runner("runshelf", 2, out var handler, now: Monday0900.AddHours(25));
        var events = new List<IslandEvent>();
        runner.Raised += events.Add;

        Assert.Null(await runner.RunDueAsync());

        Assert.Empty(handler.Bodies);
        var run = Assert.Single(_store.Results());
        Assert.Equal((false, JobStore.Missed, Monday0900), (run.Ok, run.Text, run.Due));
        Assert.Equal(["Job missed: Once"], events.Select(e => e.Text));
        Assert.Null(Assert.Single(_store.All()).NextDue);
        // Its task would only start Tomte for nothing now.
        Assert.Equal([once.Id], _tasks.Removed);
    }

    [Fact]
    public void ARowThatCannotBeReadIsSwitchedOffAndTheOthersStillRun()
    {
        AddJob("p", "read_shelf_item");
        var bad = AddJob("q", "read_shelf_item");
        Sql($"UPDATE jobs_list SET schedule = 'not json' WHERE id = {bad.Id}");

        var claim = Assert.Single(_store.ClaimDue(Monday0900, India));

        Assert.NotEqual(bad.Id, claim.Job.Id);
        Assert.Contains(_logged, line => line.Contains($"job {bad.Id} cannot be read"));
        // Switched off, so it never comes due again and the runner never spins on it.
        Assert.Equal(new DateTimeOffset(2026, 10, 20, 9, 0, 0, Ist), _store.NextDue());
    }

    [Fact]
    public async Task TwoProcessesNeverClaimTheSameRun()
    {
        AddJob("p", "read_shelf_item");
        var other = new JobStore(new TomteDb(Path.Combine(_dir, "tomte.db")), _logged.Add);

        var claims = await Task.WhenAll(
            Task.Run(() => _store.ClaimDue(Monday0900, India)),
            Task.Run(() => other.ClaimDue(Monday0900, India)));

        Assert.Equal(1, claims.Sum(c => c.Count));
    }

    // ---- Running ----

    [Fact]
    public async Task ADueJobRunsWithOnlyItsToolsAndRaisesItsEvent()
    {
        var job = AddJob("Read the shelf item notes.txt and summarise it in one sentence.", "read_shelf_item");
        var runner = Runner("runshelf", 2, out var handler);
        var events = new List<IslandEvent>();
        runner.Raised += events.Add;

        var next = await runner.RunDueAsync();

        Assert.Equal(new DateTimeOffset(2026, 10, 20, 9, 0, 0, Ist), next);
        Assert.Equal(["Running job: Morning notes", "Job done: Morning notes"], events.Select(e => e.Text));
        // A repeating job keeps its Task Scheduler entry.
        Assert.Empty(_tasks.Removed);
        Assert.Equal(["read_shelf_item"], ToolNames(handler.Bodies[0]));
        var request = JsonNode.Parse(handler.Bodies[0])!;
        Assert.EndsWith("Nobody is at the keyboard, so nothing can be asked: do the task with the tools you are given and answer with the result.",
            request["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal(job.Prompt, request["messages"]![1]!["content"]!.GetValue<string>());
        // The file paragraph comes with the read and not before: a job reads files only through a tool it was approved.
        Assert.EndsWith(Tomte.AI.Files.FileAttachments.Prompt, JsonNode.Parse(handler.Bodies[1])!["messages"]![0]!["content"]!.GetValue<string>());
        // The result is kept for the palette.
        var run = Assert.Single(_store.Results());
        Assert.True(run.Ok);
        Assert.Equal("Your dentist appointment is scheduled for Monday at 10:00 AM.", run.Text);
        Assert.Equal(Monday0900, run.Due);
        Assert.Contains("Read shelf item \"notes.txt\": <file name=\"notes.txt\"", run.Details);
        Assert.Contains("Milk, eggs, bread.", run.Details);
        Assert.EndsWith("Sent to ollama · llama3.2:3b", run.Details);
        // One send-log line for the run, marked with the job.
        var line = Assert.Single(new SendLog(LogPath).ReadAll());
        Assert.Equal("Morning notes", line.Job);
        Assert.Equal(263 + 206, line.InputTokens);
        // Nothing more is due: a second look sends nothing.
        Assert.Equal(next, await runner.RunDueAsync());
        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(next, JobStore.ReadMarker(_store.MarkerPath));
    }

    [Fact]
    public async Task ACallOutsideTheApprovedToolsEndsTheRun()
    {
        AddJob("Use the copy_to_clipboard tool to put the words standup at 10 on the clipboard.", "read_shelf_item");
        var runner = Runner("runoutside", 1, out var handler);
        var events = new List<IslandEvent>();
        runner.Raised += events.Add;

        await runner.RunDueAsync();

        Assert.Empty(_ran);
        Assert.Single(handler.Bodies);
        var run = Assert.Single(_store.Results());
        Assert.False(run.Ok);
        Assert.Equal("The job called copy_to_clipboard, which it was not approved to use, so the run stopped.", run.Text);
        Assert.Equal(["Running job: Morning notes", "Job failed: Morning notes"], events.Select(e => e.Text));
        Assert.Equal("Morning notes", Assert.Single(new SendLog(LogPath).ReadAll()).Job);
    }

    [Fact]
    public async Task ADestructiveCallDuringARunEndsItAndNothingRuns()
    {
        AddJob("Use the lock_pc tool to lock the PC now.", "read_shelf_item");
        var runner = Runner("runlock", 1, out _);

        await runner.RunDueAsync();

        Assert.Empty(_ran);
        Assert.Equal("The job called lock_pc, which it was not approved to use, so the run stopped.", Assert.Single(_store.Results()).Text);
    }

    [Fact]
    public async Task ADestructiveToolStoredWithAJobIsRefusedAtEveryRunBeforeAnythingIsSent()
    {
        // The store keeps what it is given: a row edited by hand can hold a power tool.
        AddJob("Lock the PC.", BuiltInTools.LockPc);
        var runner = Runner("runlock", 1, out var handler);

        await runner.RunDueAsync();

        Assert.Empty(handler.Bodies);
        Assert.Empty(_ran);
        Assert.Equal("lock_pc cannot run in a scheduled job, so this run was refused before anything was sent.", Assert.Single(_store.Results()).Text);
        Assert.Empty(new SendLog(LogPath).ReadAll());
    }

    [Fact]
    public async Task AJobSendsNothingOnceTheDailyCapIsReached()
    {
        new SendLog(LogPath).Append(new SendLogEntry(DateTimeOffset.Now, "ollama", OllamaFactAttribute.Model, 0, 0, 2m, [], null));
        AddJob("Read the shelf item notes.txt and summarise it in one sentence.", "read_shelf_item");
        var runner = Runner("runshelf", 2, out var handler);

        await runner.RunDueAsync();

        Assert.Empty(handler.Bodies);
        var run = Assert.Single(_store.Results());
        Assert.False(run.Ok);
        Assert.StartsWith("Daily cap of $2.00 reached for ollama.", run.Text);
    }

    [Fact]
    public async Task AJobStopsHardAtTheCapBetweenTurns()
    {
        // The recording's first turn used 263 input tokens: at $100 a million that is $0.0263, past a $0.02 cap, so the
        // turn that would carry the tool's result back is never sent.
        var options = new PaletteOptions { DailyCapUsd = 0.02m, Prices = new Dictionary<string, ModelPrice> { [OllamaFactAttribute.Model] = new(100m, 0m) } };
        AddJob("Read the shelf item notes.txt and summarise it in one sentence.", "read_shelf_item");
        var runner = Runner("runshelf", 2, out var handler, options);

        await runner.RunDueAsync();

        Assert.Single(handler.Bodies);
        var run = Assert.Single(_store.Results());
        Assert.False(run.Ok);
        Assert.Equal("Daily cap of $0.02 reached for ollama, so the job stopped.", run.Text);
        Assert.Equal(0.0263m, Assert.Single(new SendLog(LogPath).ReadAll()).CostUsd);
    }

    [Fact]
    public async Task AJobThatIsNotDueDoesNotRun()
    {
        _store.Add(new JobDraft("Later", "p", WeekdaysAtNine, []), Ollama, Monday0900.AddHours(1), India);
        var runner = Runner("runshelf", 2, out var handler);

        Assert.Equal(new DateTimeOffset(2026, 10, 20, 9, 0, 0, Ist), await runner.RunDueAsync());
        Assert.Empty(handler.Bodies);
    }

    // ---- The clock that arms the next run, and the hand-off ----

    [Fact]
    public void ATimeThatHasPassedFiresAtOnceAndOnlyOnce()
    {
        var clock = new JobClock(() => Monday0900);

        Assert.Equal(0u, clock.Arm(Monday0800));

        Assert.True(clock.Fired());
        Assert.Null(clock.Next);
        Assert.Null(clock.Wait());
        Assert.False(clock.Fired());
    }

    [Fact]
    public void AnEarlyFireWaitsForWhatIsLeft()
    {
        var now = Monday0800;
        var clock = new JobClock(() => now);
        Assert.Equal(3_600_000u, clock.Arm(Monday0900));

        // The clock was set back ten minutes before the one-shot fired: the fire is early and waits again.
        now = Monday0900.AddMinutes(-10);
        Assert.False(clock.Fired());
        Assert.Equal(600_000u, clock.Wait());
        Assert.Equal(Monday0900, clock.Next);
    }

    [Fact]
    public void AResumeOrAClockChangeWorksTheWaitOutFromTheWallClock()
    {
        var now = Monday0800;
        var clock = new JobClock(() => now);
        clock.Arm(Monday0900);

        // Two hours asleep: the one-shot did not count them, the wall clock did, so the run is due at once.
        now = Monday0800.AddHours(2);
        Assert.Equal(0u, clock.Wait());
        Assert.True(clock.Fired());
        // A run a month away waits as long as SetTimer can, then asks again.
        Assert.Equal((uint)int.MaxValue, clock.Arm(now.AddDays(30)));
    }

    [Fact]
    public async Task ARunJobStartHandsOverToTheTomteRunningOnTheSameFolder()
    {
        // A Tomte on this folder tags its island window so; a plain window stands in for it.
        await using var tomte = await TaggedWindow.OpenAsync(_dir);

        Assert.True(JobTask.HandOff(_dir, 42));
        // No Tomte runs on another folder, so a start there is not handed over: it runs the job itself.
        Assert.False(JobTask.HandOff(Path.Combine(_dir, "other"), 42));
    }

    [Fact]
    public void WithNoTomteRunningARunJobStartIsNotHandedOver() => Assert.False(JobTask.HandOff(_dir, 1));

    [Fact]
    public async Task AHeadlessRunKeepsItsResultForTheNextStart()
    {
        AddJob("Read the shelf item notes.txt and summarise it in one sentence.", "read_shelf_item");
        var headless = Runner("runshelf", 2, out var handler, resident: false);
        var unseen = new List<IslandEvent>();
        headless.Raised += e => unseen.Add(e);

        await headless.RunDueAsync();

        // It ran, but nobody saw it: the result is kept unshown and the next start looks at once.
        Assert.Equal(2, handler.Bodies.Count);
        Assert.False(Assert.Single(_store.Results()).Shown);
        Assert.Equal(DateTimeOffset.UnixEpoch, JobStore.ReadMarker(_store.MarkerPath));
        Assert.Equal(["Running job: Morning notes"], unseen.Select(e => e.Text));

        // The next start raises it once and puts the real next run back in the file.
        var resident = Runner("runshelf", 2, out var nothing, now: Monday0900.AddMinutes(5));
        var events = new List<IslandEvent>();
        resident.Raised += events.Add;
        var next = await resident.RunDueAsync();
        Assert.Empty(nothing.Bodies);
        Assert.Equal(["Job done: Morning notes"], events.Select(e => e.Text));
        Assert.True(Assert.Single(_store.Results()).Shown);
        Assert.Equal(next, JobStore.ReadMarker(_store.MarkerPath));
        Assert.Empty(_store.TakeUnshown());
    }

    [Fact]
    public async Task AResultThatCannotBeKeptDoesNotStopTheOtherRuns()
    {
        AddJob("Use the copy_to_clipboard tool to put the words standup at 10 on the clipboard.", "read_shelf_item");
        AddJob("Use the copy_to_clipboard tool to put the words standup at 10 on the clipboard.", "read_shelf_item");
        // The disk says no to every result.
        Sql("CREATE TRIGGER jobs_refuse BEFORE INSERT ON jobs_runs BEGIN SELECT RAISE(FAIL, 'disk full'); END");
        var runner = Runner("runoutside", 1, out var handler);
        var events = new List<IslandEvent>();
        runner.Raised += events.Add;

        await runner.RunDueAsync();

        Assert.Equal(2, handler.Bodies.Count);
        Assert.Equal(4, events.Count);
        Assert.Equal(2, _logged.Count(line => line.StartsWith("jobs: could not keep the result of job", StringComparison.Ordinal)));
    }

    [Fact]
    public void APackagedTomteMakesNoTaskAndSaysTheJobRunsOnlyWhileItRuns()
    {
        Assert.False(NoJobTasks.Packaged);
        Assert.IsType<WindowsTaskScheduler>(NoJobTasks.For(Exe, FakeTasks.Settings));
        var planner = new JobPlanner(_store, new NoJobTasks()) { Now = () => Monday0800, Zone = India };

        var (job, problem) = planner.Create(new JobDraft("Morning notes", "p", WeekdaysAtNine, []), Ollama);

        Assert.Equal("a packaged Tomte makes no Task Scheduler entries yet", problem);
        Assert.Single(_store.All());
        Assert.Null(planner.Remove(job.Id));
    }

    /// <summary>Records what would be registered; the XML is what the real entry would get.</summary>
    private sealed class FakeTasks : IJobTasks
    {
        public static readonly string Settings = Path.Combine(Path.GetTempPath(), "tomte-jobs-fake", "settings.json");

        public List<ScheduledJob> Registered { get; } = [];

        public List<long> Removed { get; } = [];

        public bool Fail { get; set; }

        public string Xml(ScheduledJob job) => JobTask.Xml(job, Exe, JobTask.Arguments(job.Id, Settings));

        public void Register(ScheduledJob job)
        {
            if (Fail)
                throw new UnauthorizedAccessException("Access is denied.");
            Registered.Add(job);
        }

        public void Remove(long jobId) => Removed.Add(jobId);
    }

    /// <summary>A hidden top-level window tagged with a data folder, on a thread of its own, standing in for the island window.</summary>
    private sealed class TaggedWindow : IAsyncDisposable
    {
        private readonly DispatcherQueueController _thread = DispatcherQueueController.CreateOnDedicatedThread();

        public static async Task<TaggedWindow> OpenAsync(string dataFolder)
        {
            var window = new TaggedWindow();
            var made = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            window._thread.DispatcherQueue.TryEnqueue(() =>
            {
                nint hwnd = Create();
                if (hwnd != 0)
                    JobTask.Tag(hwnd, dataFolder);
                made.SetResult(hwnd != 0);
            });
            Assert.True(await made.Task);
            return window;
        }

        // The window goes with its thread.
        public async ValueTask DisposeAsync() => await _thread.ShutdownQueueAsync();

        private static unsafe nint Create() =>
            (nint)ai::Windows.Win32.PInvoke.CreateWindowEx(0, "STATIC", "", ai::Windows.Win32.UI.WindowsAndMessaging.WINDOW_STYLE.WS_POPUP, 0, 0, 0, 0, default, null, null, null).Value;
    }
}
