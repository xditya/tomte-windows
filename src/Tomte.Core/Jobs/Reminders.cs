using System.Globalization;
using Microsoft.Data.Sqlite;
using Tomte.Core.Storage;

namespace Tomte.Core.Jobs;

/// <summary>
/// A stored reminder. <see cref="Due"/> is when it next rings, null once a one-off has rung. <see cref="Ringing"/> is the
/// time it last rang while it waits to be dismissed, null when nothing waits.
/// </summary>
public sealed record Reminder(long Id, string Text, JobSchedule Schedule, DateTimeOffset? Due, DateTimeOffset Created, DateTimeOffset? Ringing = null)
{
    /// <summary>A reminder that rang more than this after its time was missed: Tomte was closed or the PC slept.</summary>
    public static readonly TimeSpan Late = TimeSpan.FromMinutes(1);

    /// <summary>
    /// What the island says once it rings: the text, or for a missed one when it was due, "Missed at 09:30: standup", with
    /// the day when it was not today.
    /// </summary>
    public string Line(DateTimeOffset now, TimeZoneInfo zone)
    {
        if (Ringing is not { } at || now - at <= Late)
        {
            return Text;
        }

        var local = TimeZoneInfo.ConvertTime(at, zone);
        bool today = local.Date == TimeZoneInfo.ConvertTime(now, zone).Date;
        return $"Missed {local.ToString(today ? "'at' HH:mm" : "ddd 'at' HH:mm", CultureInfo.InvariantCulture)}: {Text}";
    }

    /// <summary>For the settings list: "Every weekday at 09:30. Next Monday 27 October at 09:30."</summary>
    public string Describe(TimeZoneInfo zone) =>
        $"{Schedule.Describe()}." + (Due is { } next ? $" Next {TimeZoneInfo.ConvertTime(next, zone).ToString("dddd d MMMM 'at' HH:mm", CultureInfo.InvariantCulture)}." : "");
}

/// <summary>
/// The reminders in tomte.db (feature "reminders"). Every method opens, works and closes, and blocks: call it from a
/// worker thread only. Like the jobs, a small file beside the database holds the next time to look, rewritten after every
/// change, so a start can arm its one-shot without opening the database; it holds 0 while a reminder still rings.
/// </summary>
public sealed class ReminderStore(TomteDb db)
{
    public const int MaxText = 200;

    /// <summary>How long Snooze puts a reminder off.</summary>
    public static readonly TimeSpan SnoozeFor = TimeSpan.FromMinutes(10);

    private const string Feature = "reminders";

    // The whole history, oldest first. Never edit or reorder a step that has merged; add at the end.
    private static readonly string[] Schema =
    [
        """
        CREATE TABLE reminders_list(id INTEGER PRIMARY KEY, text TEXT NOT NULL, schedule TEXT NOT NULL, due INTEGER,
            created INTEGER NOT NULL, ringing INTEGER);
        """,
    ];

    private const string Columns = "id, text, schedule, due, created, ringing";

    /// <summary>The next-due file beside tomte.db.</summary>
    public string MarkerPath { get; } = MarkerFor(db.FilePath);

    public static string MarkerFor(string databasePath) => Path.Combine(Path.GetDirectoryName(databasePath)!, "reminders-next.txt");

    /// <summary>Stores a reminder that first rings at <paramref name="due"/>.</summary>
    public Reminder Add(string text, JobSchedule schedule, DateTimeOffset due, DateTimeOffset now)
    {
        using var connection = db.Open(Feature, Schema);
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO reminders_list(text, schedule, due, created) VALUES ($text, $schedule, $due, $created) RETURNING id";
        command.Parameters.AddWithValue("$text", text);
        command.Parameters.AddWithValue("$schedule", schedule.ToJson());
        command.Parameters.AddWithValue("$due", due.ToUnixTimeMilliseconds());
        command.Parameters.AddWithValue("$created", now.ToUnixTimeMilliseconds());
        long id = (long)command.ExecuteScalar()!;
        WriteMarker(connection);
        return new Reminder(id, text, schedule, due, now);
    }

    /// <summary>Takes a reminder out, ringing or not. False when there was no such reminder.</summary>
    public bool Remove(long id)
    {
        using var connection = db.Open(Feature, Schema);
        bool removed = Run(connection, "DELETE FROM reminders_list WHERE id = $id", ("$id", id)) == 1;
        WriteMarker(connection);
        return removed;
    }

    /// <summary>Every reminder, soonest first; those with nothing left to ring last.</summary>
    public IReadOnlyList<Reminder> All()
    {
        using var connection = db.Open(Feature, Schema);
        return Read(connection, $"SELECT {Columns} FROM reminders_list ORDER BY due IS NULL, due, id");
    }

    /// <summary>
    /// Rings every reminder due at <paramref name="now"/>, in one write transaction: each is marked ringing at the latest
    /// time it should have rung (one missed for days rings once), a repeating one moves to its next time after now and a
    /// one-off has none left. Returns everything that rings, those not yet dismissed from before included, oldest first.
    /// </summary>
    public IReadOnlyList<Reminder> Ring(DateTimeOffset now, TimeZoneInfo zone)
    {
        using var connection = db.Open(Feature, Schema);
        using var transaction = connection.BeginTransaction();
        foreach (var due in Read(connection, $"SELECT {Columns} FROM reminders_list WHERE due <= $now", ("$now", now.ToUnixTimeMilliseconds())))
        {
            var latest = due.Due!.Value;
            // one step per missed time; a year away is a few hundred steps.
            while (due.Schedule.Date is null && due.Schedule.Next(latest, zone) is { } after && after <= now)
            {
                latest = after;
            }

            Run(connection, "UPDATE reminders_list SET ringing = $ringing, due = $next WHERE id = $id",
                ("$id", due.Id), ("$ringing", latest.ToUnixTimeMilliseconds()), ("$next", Ms(due.Schedule.Next(now, zone))));
        }

        var ringing = Read(connection, $"SELECT {Columns} FROM reminders_list WHERE ringing IS NOT NULL ORDER BY ringing, id");
        transaction.Commit();
        WriteMarker(connection);
        return ringing;
    }

    /// <summary>Stops a reminder ringing; a one-off with nothing left to ring is then gone.</summary>
    public void Dismiss(long id)
    {
        using var connection = db.Open(Feature, Schema);
        Run(connection, "UPDATE reminders_list SET ringing = NULL WHERE id = $id; DELETE FROM reminders_list WHERE id = $id AND due IS NULL", ("$id", id));
        WriteMarker(connection);
    }

    /// <summary>
    /// Dismisses a ringing reminder and rings its text again <see cref="SnoozeFor"/> from <paramref name="now"/>, as a
    /// one-off of its own: a repeating one keeps its next time. Null when the reminder was removed meanwhile.
    /// </summary>
    public Reminder? Snooze(long id, DateTimeOffset now, TimeZoneInfo zone)
    {
        Reminder? was;
        using (var connection = db.Open(Feature, Schema))
        {
            was = Read(connection, $"SELECT {Columns} FROM reminders_list WHERE id = $id", ("$id", id)).FirstOrDefault();
        }

        Dismiss(id);
        if (was is null)
        {
            return null;
        }

        var at = now + SnoozeFor;
        var local = TimeZoneInfo.ConvertTime(at, zone);
        return Add(was.Text, new JobSchedule { Date = DateOnly.FromDateTime(local.DateTime), Time = new TimeOnly(local.Hour, local.Minute) }, at, now);
    }

    /// <summary>When the next reminder is due, ringing ones aside; null with none ahead.</summary>
    public DateTimeOffset? NextDue()
    {
        using var connection = db.Open(Feature, Schema);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT MIN(due) FROM reminders_list";
        return command.ExecuteScalar() is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null;
    }

    private static object Ms(DateTimeOffset? at) => (object?)at?.ToUnixTimeMilliseconds() ?? DBNull.Value;

    // After the change is committed, and only a hint, as the jobs' file is.
    private void WriteMarker(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT CASE WHEN EXISTS (SELECT 1 FROM reminders_list WHERE ringing IS NOT NULL) THEN 0 ELSE (SELECT MIN(due) FROM reminders_list) END";
        JobStore.WriteMarker(MarkerPath, command.ExecuteScalar() is long ms ? DateTimeOffset.FromUnixTimeMilliseconds(ms) : null);
    }

    private static List<Reminder> Read(SqliteConnection connection, string sql, params ReadOnlySpan<(string Name, object Value)> parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        using var reader = command.ExecuteReader();
        var list = new List<Reminder>();
        while (reader.Read())
        {
            list.Add(new Reminder(reader.GetInt64(0), reader.GetString(1), JobSchedule.FromJson(reader.GetString(2)), At(reader, 3),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(4)), At(reader, 5)));
        }

        return list;

        static DateTimeOffset? At(SqliteDataReader reader, int i) => reader.IsDBNull(i) ? null : DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(i));
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
}
