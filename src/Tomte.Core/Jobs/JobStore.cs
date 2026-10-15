using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Tomte.Core.Storage;

namespace Tomte.Core.Jobs;

/// <summary>What the user confirmed: a name, the prompt each run sends, when it runs, and the only tools it may use.</summary>
public sealed record JobDraft(string Name, string Prompt, JobSchedule Schedule, IReadOnlyList<string> Tools);

/// <summary>A stored job. <see cref="NextDue"/> is null once a one-off has run.</summary>
public sealed record ScheduledJob(long Id, string Name, string Prompt, JobSchedule Schedule, IReadOnlyList<string> Tools, ProviderConfig Provider, DateTimeOffset? NextDue)
{
    /// <summary>
    /// The job in plain words for the settings window: "Every weekday at 09:00. Next run Thursday 15 October at 09:00.
    /// Uses read_shelf_item."
    /// </summary>
    public string Describe(TimeZoneInfo zone) =>
        $"{Schedule.Describe()}. " +
        (NextDue is { } next ? $"Next run {TimeZoneInfo.ConvertTime(next, zone).ToString("dddd d MMMM 'at' HH:mm", CultureInfo.InvariantCulture)}. " : "No run left. ") +
        $"Uses {(Tools.Count == 0 ? "no tools" : JobSchedule.JoinWords(Tools))}.";
}

/// <summary>
/// One finished run, kept for the palette to show: the answer or the reason it failed, and the steps and sends.
/// Shown is false for a run nobody saw happen (Tomte was closed), whose event the next start raises.
/// </summary>
public sealed record JobRunRecord(long JobId, string JobName, DateTimeOffset Due, DateTimeOffset Finished, bool Ok, string Text, string Details, bool Shown = true);

/// <summary>A run taken from the store: the job as it is now, when it was due, and whether it was too late to run.</summary>
public sealed record JobClaim(ScheduledJob Job, DateTimeOffset Due, bool Missed);

/// <summary>
/// The jobs and their results in tomte.db (feature "jobs"). Every method opens, works and closes, and blocks: call it
/// from a worker thread only. Beside the database a small file holds the next due time over all jobs, rewritten after
/// every change, so a start can arm the runner without opening the database. A claimed run with no result counts in it
/// at <see cref="RunLimit"/> after its claim, when it is to be recorded as not finished. It holds 0 while a result
/// waits to be shown, so the next start looks, and with no jobs it is absent.
/// </summary>
public sealed class JobStore(TomteDb db, Action<string>? log = null)
{
    /// <summary>The text of a one-off's result when it came due more than <see cref="OneOffGrace"/> before Tomte could run it.</summary>
    public const string Missed = "Missed";

    /// <summary>A repeating run later than this is skipped: a 09:00 summary at the end of the day is not wanted.</summary>
    public static readonly TimeSpan RecurringGrace = TimeSpan.FromHours(6);

    /// <summary>A one-off later than this is not run but recorded as missed.</summary>
    public static readonly TimeSpan OneOffGrace = TimeSpan.FromHours(24);

    /// <summary>
    /// A one-off whose time passed less than this ago can still be made, and then runs at once: "in one minute" often
    /// comes due while the user reads the confirmation.
    /// </summary>
    public static readonly TimeSpan JustPassed = TimeSpan.FromMinutes(5);

    /// <summary>The text of a run that was claimed and had no result <see cref="RunLimit"/> later.</summary>
    public const string DidNotFinish = "Did not finish";

    /// <summary>
    /// The longest a run may take: Task Scheduler ends a --run-job start after this, before it can keep a result.
    /// </summary>
    public static readonly TimeSpan RunLimit = TimeSpan.FromMinutes(10);

    private const string Feature = "jobs";

    // The whole history, oldest first. Never edit or reorder a step that has merged; add at the end.
    private static readonly string[] Schema =
    [
        """
        CREATE TABLE jobs_list(id INTEGER PRIMARY KEY, name TEXT NOT NULL, prompt TEXT NOT NULL, schedule TEXT NOT NULL,
            tools TEXT NOT NULL, provider TEXT NOT NULL, created INTEGER NOT NULL, next_due INTEGER);
        CREATE TABLE jobs_runs(id INTEGER PRIMARY KEY, job INTEGER NOT NULL REFERENCES jobs_list(id) ON DELETE CASCADE,
            name TEXT NOT NULL, due INTEGER NOT NULL, finished INTEGER NOT NULL, ok INTEGER NOT NULL, text TEXT NOT NULL,
            details TEXT NOT NULL, shown INTEGER NOT NULL);
        """,
        """
        CREATE TABLE jobs_claims(job INTEGER NOT NULL REFERENCES jobs_list(id) ON DELETE CASCADE, name TEXT NOT NULL,
            due INTEGER NOT NULL, claimed INTEGER NOT NULL);
        """,
    ];

    private const string JobColumns = "id, name, prompt, schedule, tools, provider, next_due";
    private const string RunColumns = "job, name, due, finished, ok, text, details, shown";

    // The last instant DateTimeOffset can hold, in Unix milliseconds.
    private const long MaxMilliseconds = 253402300799999;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>The next-due file beside tomte.db.</summary>
    public string MarkerPath { get; } = MarkerFor(db.FilePath);

    public static string MarkerFor(string databasePath) => Path.Combine(Path.GetDirectoryName(databasePath)!, "jobs-next.txt");

    /// <summary>
    /// The next due time the file holds; null when there is no file (no jobs). A file that does not hold a time reads
    /// as due now, so the runner looks in the database instead of trusting it.
    /// </summary>
    public static DateTimeOffset? ReadMarker(string path)
    {
        try
        {
            string text = File.ReadAllText(path).Trim();
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long ms) && ms <= MaxMilliseconds
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : DateTimeOffset.MinValue;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return DateTimeOffset.MinValue;
        }
    }

    /// <summary>A new job's first run: the next one after <paramref name="now"/>, else a one-off's time within <see cref="JustPassed"/>.</summary>
    public static DateTimeOffset? FirstRun(JobSchedule schedule, DateTimeOffset now, TimeZoneInfo zone) =>
        schedule.Next(now, zone) ?? schedule.Next(now - JustPassed, zone);

    /// <summary>Stores a confirmed job with its first run worked out from <paramref name="now"/> (see <see cref="FirstRun"/>).</summary>
    public ScheduledJob Add(JobDraft draft, ProviderConfig provider, DateTimeOffset now, TimeZoneInfo zone)
    {
        var next = FirstRun(draft.Schedule, now, zone);
        using var connection = db.Open(Feature, Schema);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs_list(name, prompt, schedule, tools, provider, created, next_due)
            VALUES ($name, $prompt, $schedule, $tools, $provider, $created, $next) RETURNING id
            """;
        command.Parameters.AddWithValue("$name", draft.Name);
        command.Parameters.AddWithValue("$prompt", draft.Prompt);
        command.Parameters.AddWithValue("$schedule", draft.Schedule.ToJson());
        command.Parameters.AddWithValue("$tools", JsonSerializer.Serialize(draft.Tools, Json));
        command.Parameters.AddWithValue("$provider", JsonSerializer.Serialize(provider, Json));
        command.Parameters.AddWithValue("$created", now.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$next", Ms(next));
        long id = (long)command.ExecuteScalar()!;
        WriteMarker(connection);
        return new ScheduledJob(id, draft.Name, draft.Prompt, draft.Schedule, draft.Tools, provider, next);
    }

    /// <summary>Removes a job and its results. False when there was no such job.</summary>
    public bool Remove(long id)
    {
        using var connection = db.Open(Feature, Schema);
        bool removed = Run(connection, "DELETE FROM jobs_list WHERE id = $id", ("$id", id)) == 1;
        WriteMarker(connection);
        return removed;
    }

    public IReadOnlyList<ScheduledJob> All()
    {
        using var connection = db.Open(Feature, Schema);
        return Jobs(connection, $"SELECT {JobColumns} FROM jobs_list ORDER BY id", null).Jobs;
    }

    /// <summary>
    /// Takes every job due at <paramref name="now"/> and moves each to its next run after now, in one write
    /// transaction, so another connection or process that looks at the same moment finds nothing due. A run missed
    /// while the PC slept or Tomte was closed is taken once, at the latest time it should have run, unless that is
    /// more than <see cref="RecurringGrace"/> ago, when it is skipped. A one-off is then done; one more than
    /// <see cref="OneOffGrace"/> late comes back marked missed, to be recorded and not run. A row that cannot be read is
    /// switched off and logged, so it never comes due again. Each claim is noted until its result is recorded; one
    /// still without a result <see cref="RunLimit"/> later is recorded here as not finished, unshown, so a start raises
    /// it. That moment is in the next-due file, so a start looks then.
    /// </summary>
    public IReadOnlyList<JobClaim> ClaimDue(DateTimeOffset now, TimeZoneInfo zone)
    {
        using var connection = db.Open(Feature, Schema);
        using var transaction = connection.BeginTransaction();
        Run(connection, $"""
            INSERT INTO jobs_runs({RunColumns})
            SELECT job, name, due, claimed + $limit, 0, $text, '', 0 FROM jobs_claims WHERE claimed + $limit <= $now;
            DELETE FROM jobs_claims WHERE claimed + $limit <= $now;
            """,
            ("$limit", (long)RunLimit.TotalMilliseconds), ("$now", now.ToUnixTimeMilliseconds()), ("$text", DidNotFinish));
        var (due, unreadable) = Jobs(connection, $"SELECT {JobColumns} FROM jobs_list WHERE next_due <= $now ORDER BY next_due, id", now);
        foreach (long id in unreadable)
        {
            Run(connection, "UPDATE jobs_list SET next_due = NULL WHERE id = $id", ("$id", id));
        }

        var claimed = new List<JobClaim>();
        foreach (var job in due)
        {
            var next = job.Schedule.Next(now, zone);
            Run(connection, "UPDATE jobs_list SET next_due = $next WHERE id = $id", ("$id", job.Id), ("$next", Ms(next)));
            // The latest time it should have run: after three days asleep, this morning's run, not Monday's.
            // one step per missed run; a year away is a few hundred steps.
            var latest = job.NextDue!.Value;
            while (job.Schedule.Next(latest, zone) is { } after && after <= now)
            {
                latest = after;
            }

            var late = now - latest;
            if (job.Schedule.Date is null && late > RecurringGrace)
            {
                log?.Invoke($"jobs: job {job.Id} was due {late.TotalHours:0.#} hours ago; skipped to {next:O}");
                continue;
            }

            claimed.Add(new JobClaim(job with { NextDue = next }, latest, Missed: job.Schedule.Date is not null && late > OneOffGrace));
            Run(connection, "INSERT INTO jobs_claims(job, name, due, claimed) VALUES ($id, $name, $due, $now)",
                ("$id", job.Id), ("$name", job.Name), ("$due", latest.ToUnixTimeMilliseconds()), ("$now", now.ToUnixTimeMilliseconds()));
        }

        transaction.Commit();
        WriteMarker(connection);
        return claimed;
    }

    /// <summary>Keeps a finished run and lets go of its claim. A run of a job removed meanwhile is dropped with it.</summary>
    public void Record(JobRunRecord run)
    {
        using var connection = db.Open(Feature, Schema);
        using var transaction = connection.BeginTransaction();
        Run(connection, $"""
            INSERT INTO jobs_runs({RunColumns})
            SELECT $job, $name, $due, $finished, $ok, $text, $details, $shown WHERE EXISTS (SELECT 1 FROM jobs_list WHERE id = $job);
            DELETE FROM jobs_claims WHERE job = $job AND due = $due;
            """,
            ("$job", run.JobId), ("$name", run.JobName), ("$due", run.Due.ToUnixTimeMilliseconds()), ("$finished", run.Finished.ToUnixTimeMilliseconds()),
            ("$ok", run.Ok), ("$text", run.Text), ("$details", run.Details), ("$shown", run.Shown));
        transaction.Commit();
        WriteMarker(connection);
    }

    /// <summary>The results nobody has seen yet, oldest first, now marked seen: the next start raises their events.</summary>
    public IReadOnlyList<JobRunRecord> TakeUnshown()
    {
        using var connection = db.Open(Feature, Schema);
        using var transaction = connection.BeginTransaction();
        var runs = Runs(connection, $"SELECT {RunColumns} FROM jobs_runs WHERE shown = 0 ORDER BY id", null);
        Run(connection, "UPDATE jobs_runs SET shown = 1 WHERE shown = 0");
        transaction.Commit();
        WriteMarker(connection);
        return runs;
    }

    /// <summary>The latest results, newest first, for the palette.</summary>
    public IReadOnlyList<JobRunRecord> Results(int limit = 20)
    {
        using var connection = db.Open(Feature, Schema);
        return Runs(connection, $"SELECT {RunColumns} FROM jobs_runs ORDER BY id DESC LIMIT $limit", limit);
    }

    /// <summary>
    /// The earliest next run over all jobs, or the moment a claimed run with no result is to be recorded as not finished
    /// if that comes first; null when neither is ahead.
    /// </summary>
    public DateTimeOffset? NextDue()
    {
        using var connection = db.Open(Feature, Schema);
        return Earliest(connection);
    }

    private static object Ms(DateTimeOffset? at) => (object?)at?.ToUnixTimeMilliseconds() ?? DBNull.Value;

    private static DateTimeOffset? Earliest(SqliteConnection connection) =>
        Scalar(connection, "SELECT MIN(at) FROM (SELECT next_due AS at FROM jobs_list UNION ALL SELECT claimed + $limit FROM jobs_claims)",
            ("$limit", (long)RunLimit.TotalMilliseconds)) is long ms
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;

    // After the change is committed, and only a hint: a crash in between, or a disk that says no, leaves the file early
    // (the runner looks and finds nothing due) or late, when the Task Scheduler entry still starts the run.
    private void WriteMarker(SqliteConnection connection)
    {
        try
        {
            // A result that waits to be shown makes the next start look at once.
            DateTimeOffset? next = Scalar(connection, "SELECT EXISTS (SELECT 1 FROM jobs_runs WHERE shown = 0)") is 1L
                ? DateTimeOffset.UnixEpoch
                : Earliest(connection);
            if (next is { } at)
            {
                string temp = MarkerPath + ".tmp";
                File.WriteAllText(temp, at.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
                File.Move(temp, MarkerPath, overwrite: true);
            }
            else
            {
                File.Delete(MarkerPath);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private (List<ScheduledJob> Jobs, List<long> Unreadable) Jobs(SqliteConnection connection, string sql, DateTimeOffset? now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (now is { } at)
        {
            command.Parameters.AddWithValue("$now", at.ToUnixTimeMilliseconds());
        }

        using var reader = command.ExecuteReader();
        var jobs = new List<ScheduledJob>();
        var unreadable = new List<long>();
        while (reader.Read())
        {
            long id = reader.GetInt64(0);
            try
            {
                jobs.Add(new ScheduledJob(
                    id,
                    reader.GetString(1),
                    reader.GetString(2),
                    JobSchedule.FromJson(reader.GetString(3)),
                    JsonSerializer.Deserialize<string[]>(reader.GetString(4), Json) ?? throw new JsonException("no tools"),
                    JsonSerializer.Deserialize<ProviderConfig>(reader.GetString(5), Json) ?? throw new JsonException("no provider"),
                    reader.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6))));
            }
            // One damaged or hand-edited row must not stop every other job.
            catch (Exception e) when (e is FormatException or JsonException or InvalidCastException or ArgumentException)
            {
                log?.Invoke($"jobs: job {id} cannot be read and is skipped ({e.Message})");
                unreadable.Add(id);
            }
        }

        return (jobs, unreadable);
    }

    private static List<JobRunRecord> Runs(SqliteConnection connection, string sql, int? limit)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (limit is { } n)
        {
            command.Parameters.AddWithValue("$limit", n);
        }

        using var reader = command.ExecuteReader();
        var runs = new List<JobRunRecord>();
        while (reader.Read())
        {
            runs.Add(new JobRunRecord(reader.GetInt64(0), reader.GetString(1), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)), reader.GetBoolean(4), reader.GetString(5), reader.GetString(6), reader.GetBoolean(7)));
        }

        return runs;
    }

    private static int Run(SqliteConnection connection, string sql, params ReadOnlySpan<(string Name, object Value)> parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command.ExecuteNonQuery();
    }

    private static object? Scalar(SqliteConnection connection, string sql, params ReadOnlySpan<(string Name, object Value)> parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command.ExecuteScalar();
    }
}
