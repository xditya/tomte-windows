using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using Tomte.Core.Storage;

namespace Tomte.Core.Jobs;

/// <summary>What the user confirmed: a name, the prompt each run sends, when it runs, and the only tools it may use.</summary>
public sealed record JobDraft(string Name, string Prompt, JobSchedule Schedule, IReadOnlyList<string> Tools);

/// <summary>A stored job. <see cref="NextDue"/> is null once a one-off has run.</summary>
public sealed record ScheduledJob(long Id, string Name, string Prompt, JobSchedule Schedule, IReadOnlyList<string> Tools, ProviderConfig Provider, DateTimeOffset? NextDue);

/// <summary>One finished run, kept for the palette to show: the answer or the reason it failed, and the steps and sends.</summary>
public sealed record JobRunRecord(long JobId, string JobName, DateTimeOffset Due, DateTimeOffset Finished, bool Ok, string Text, string Details);

/// <summary>
/// The jobs and their results in tomte.db (feature "jobs"). Every method opens, works and closes, and blocks: call it
/// from a worker thread only. Beside the database a small file holds the next due time over all jobs, rewritten after
/// every change, so a start can arm the runner without opening the database, and with no jobs the file is absent.
/// </summary>
public sealed class JobStore(TomteDb db)
{
    private const string Feature = "jobs";

    // The whole history, oldest first. Never edit or reorder a step that has merged; add at the end.
    private static readonly string[] Schema =
    [
        """
        CREATE TABLE jobs_list(id INTEGER PRIMARY KEY, name TEXT NOT NULL, prompt TEXT NOT NULL, schedule TEXT NOT NULL,
            tools TEXT NOT NULL, provider TEXT NOT NULL, created INTEGER NOT NULL, next_due INTEGER);
        CREATE TABLE jobs_runs(id INTEGER PRIMARY KEY, job INTEGER NOT NULL REFERENCES jobs_list(id) ON DELETE CASCADE,
            name TEXT NOT NULL, due INTEGER NOT NULL, finished INTEGER NOT NULL, ok INTEGER NOT NULL, text TEXT NOT NULL, details TEXT NOT NULL);
        """,
    ];

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } };

    /// <summary>The next-due file beside tomte.db.</summary>
    public string MarkerPath { get; } = MarkerFor(db.FilePath);

    public static string MarkerFor(string databasePath) => Path.Combine(Path.GetDirectoryName(databasePath)!, "jobs-next.txt");

    /// <summary>
    /// The next due time the file holds; null when there is no file (no jobs). A file that does not parse reads as due
    /// now, so the runner looks in the database instead of trusting it.
    /// </summary>
    public static DateTimeOffset? ReadMarker(string path)
    {
        try
        {
            string text = File.ReadAllText(path).Trim();
            return long.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out long ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : DateTimeOffset.MinValue;
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

    /// <summary>Stores a confirmed job with its first run worked out from <paramref name="now"/>.</summary>
    public ScheduledJob Add(JobDraft draft, ProviderConfig provider, DateTimeOffset now, TimeZoneInfo zone)
    {
        var next = draft.Schedule.Next(now, zone);
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
        command.Parameters.AddWithValue("$next", (object?)next?.ToUnixTimeMilliseconds() ?? DBNull.Value);
        long id = (long)command.ExecuteScalar()!;
        WriteMarker(connection);
        return new ScheduledJob(id, draft.Name, draft.Prompt, draft.Schedule, draft.Tools, provider, next);
    }

    /// <summary>Removes a job and its results. False when there was no such job.</summary>
    public bool Remove(long id)
    {
        using var connection = db.Open(Feature, Schema);
        using var command = connection.CreateCommand();
        command.CommandText = "DELETE FROM jobs_list WHERE id = $id";
        command.Parameters.AddWithValue("$id", id);
        bool removed = command.ExecuteNonQuery() == 1;
        WriteMarker(connection);
        return removed;
    }

    public IReadOnlyList<ScheduledJob> All()
    {
        using var connection = db.Open(Feature, Schema);
        return Jobs(connection, "SELECT id, name, prompt, schedule, tools, provider, next_due FROM jobs_list ORDER BY id", null);
    }

    /// <summary>
    /// Takes every job due at <paramref name="now"/> and moves each to its next run after now, in one write
    /// transaction, so another connection or process that looks at the same moment finds nothing due. A run missed
    /// while the PC slept or Tomte was closed is taken once, however many times it was missed; a one-off is then done.
    /// Returns each job with the time it was due.
    /// </summary>
    public IReadOnlyList<(ScheduledJob Job, DateTimeOffset Due)> ClaimDue(DateTimeOffset now, TimeZoneInfo zone)
    {
        using var connection = db.Open(Feature, Schema);
        using var transaction = connection.BeginTransaction();
        var due = Jobs(connection, "SELECT id, name, prompt, schedule, tools, provider, next_due FROM jobs_list WHERE next_due <= $now ORDER BY next_due, id", now);
        var claimed = new List<(ScheduledJob, DateTimeOffset)>();
        foreach (var job in due)
        {
            var next = job.Schedule.Next(now, zone);
            using var command = connection.CreateCommand();
            command.CommandText = "UPDATE jobs_list SET next_due = $next WHERE id = $id";
            command.Parameters.AddWithValue("$id", job.Id);
            command.Parameters.AddWithValue("$next", (object?)next?.ToUnixTimeMilliseconds() ?? DBNull.Value);
            command.ExecuteNonQuery();
            claimed.Add((job with { NextDue = next }, job.NextDue!.Value));
        }

        transaction.Commit();
        WriteMarker(connection);
        return claimed;
    }

    /// <summary>Keeps a finished run. A run of a job removed meanwhile is dropped with it.</summary>
    public void Record(JobRunRecord run)
    {
        using var connection = db.Open(Feature, Schema);
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO jobs_runs(job, name, due, finished, ok, text, details)
            SELECT $job, $name, $due, $finished, $ok, $text, $details WHERE EXISTS (SELECT 1 FROM jobs_list WHERE id = $job)
            """;
        command.Parameters.AddWithValue("$job", run.JobId);
        command.Parameters.AddWithValue("$name", run.JobName);
        command.Parameters.AddWithValue("$due", run.Due.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$finished", run.Finished.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$ok", run.Ok);
        command.Parameters.AddWithValue("$text", run.Text);
        command.Parameters.AddWithValue("$details", run.Details);
        command.ExecuteNonQuery();
    }

    /// <summary>The latest results, newest first, for the palette.</summary>
    public IReadOnlyList<JobRunRecord> Results(int limit = 20)
    {
        using var connection = db.Open(Feature, Schema);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT job, name, due, finished, ok, text, details FROM jobs_runs ORDER BY id DESC LIMIT $limit";
        command.Parameters.AddWithValue("$limit", limit);
        using var reader = command.ExecuteReader();
        var runs = new List<JobRunRecord>();
        while (reader.Read())
        {
            runs.Add(new JobRunRecord(reader.GetInt64(0), reader.GetString(1), DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(2)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(3)), reader.GetBoolean(4), reader.GetString(5), reader.GetString(6)));
        }

        return runs;
    }

    /// <summary>The earliest next run over all jobs, or null when none will run again.</summary>
    public DateTimeOffset? NextDue()
    {
        using var connection = db.Open(Feature, Schema);
        return Earliest(connection);
    }

    private static DateTimeOffset? Earliest(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(next_due) FROM jobs_list";
        return command.ExecuteScalar() is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
    }

    // After the change is committed, and only a hint: a crash in between, or a disk that says no, leaves the file early
    // (the runner looks and finds nothing due) or late, when the Task Scheduler entry still starts the run.
    private void WriteMarker(SqliteConnection connection)
    {
        try
        {
            if (Earliest(connection) is { } next)
            {
                string temp = MarkerPath + ".tmp";
                File.WriteAllText(temp, next.ToUnixTimeMilliseconds().ToString(CultureInfo.InvariantCulture));
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

    private static List<ScheduledJob> Jobs(SqliteConnection connection, string sql, DateTimeOffset? now)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        if (now is { } at)
        {
            command.Parameters.AddWithValue("$now", at.ToUnixTimeMilliseconds());
        }

        using var reader = command.ExecuteReader();
        var jobs = new List<ScheduledJob>();
        while (reader.Read())
        {
            jobs.Add(new ScheduledJob(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.GetString(2),
                JobSchedule.FromJson(reader.GetString(3)),
                JsonSerializer.Deserialize<string[]>(reader.GetString(4), Json)!,
                JsonSerializer.Deserialize<ProviderConfig>(reader.GetString(5), Json)!,
                reader.IsDBNull(6) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(6))));
        }

        return jobs;
    }
}
