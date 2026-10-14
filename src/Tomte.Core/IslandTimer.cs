using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tomte.Core;

/// <summary>One countdown, stored as wall-clock instants so a restart cannot move its end.</summary>
public sealed record TimerRun(DateTimeOffset StartedAt, DateTimeOffset EndsAt)
{
    /// <summary>Longest timer: one day. SetTimer itself tops out near 24.8 days.</summary>
    public static readonly TimeSpan MaxDuration = TimeSpan.FromDays(1);

    public static readonly TimeSpan MinDuration = TimeSpan.FromSeconds(1);

    public static TimerRun Starting(DateTimeOffset now, TimeSpan duration) => new(now, now + duration);

    /// <summary>
    /// Minutes as typed ("25", "0.5"), read with the invariant culture. False when the text is missing, is not
    /// a number, or is outside the shortest and longest timer.
    /// </summary>
    public static bool TryParseMinutes(string? text, out TimeSpan duration)
    {
        duration = default;
        // Written so NaN fails both comparisons.
        if (!double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double minutes)
            || !(minutes * 60 >= MinDuration.TotalSeconds && minutes <= MaxDuration.TotalMinutes))
        {
            return false;
        }

        duration = TimeSpan.FromMinutes(minutes);
        return true;
    }

    [JsonIgnore]
    public TimeSpan Duration => EndsAt - StartedAt;

    public TimeSpan Remaining(DateTimeOffset now) => now >= EndsAt ? TimeSpan.Zero : EndsAt - now;

    /// <summary>How full the ring is: 0 at the start, 1 at the end, clamped both ways (the clock can be set back).</summary>
    public double Progress(DateTimeOffset now) =>
        Duration <= TimeSpan.Zero ? 1 : Math.Clamp((now - StartedAt) / Duration, 0, 1);
}

/// <summary>
/// The single running timer, persisted to timer.json beside settings.json. Changed fires on the caller's
/// thread after every start, cancel or completion. Completion is taken once: the first TryComplete at or
/// after the end time clears the run and returns true, every later call returns false.
/// </summary>
public sealed class IslandTimer(string path)
{
    private TimerRun? run = Load(path);

    public static string DefaultPath { get; } = Path.Combine(Path.GetDirectoryName(SettingsStore.DefaultPath)!, "timer.json");

    public event Action? Changed;

    public TimerRun? Run
    {
        get => run;
        set
        {
            run = value;
            Save(path, value);
            Changed?.Invoke();
        }
    }

    /// <summary>Time to the end, zero once it has passed (a restart after the end fires at once), null with no timer.</summary>
    public TimeSpan? DueIn(DateTimeOffset now) => run?.Remaining(now);

    public bool TryComplete(DateTimeOffset now)
    {
        if (run is null || now < run.EndsAt)
        {
            return false;
        }

        Run = null;
        return true;
    }

    private static TimerRun? Load(string path)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            // "{}" parses to a run that ended in year 1 and a hand edit can put the end years away; neither is a
            // timer this app starts, so neither is resumed.
            var run = JsonSerializer.Deserialize<TimerRun>(File.ReadAllText(path));
            return run is not null && run.Duration > TimeSpan.Zero && run.Duration <= TimerRun.MaxDuration ? run : null;
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Unreadable or damaged: no timer. The next start overwrites a damaged file.
            return null;
        }
    }

    private static void Save(string path, TimerRun? run)
    {
        if (run is null)
        {
            File.Delete(path);
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        string temp = path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(run));
        File.Move(temp, path, overwrite: true);
    }
}
