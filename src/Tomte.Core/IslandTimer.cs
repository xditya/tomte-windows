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
            return JsonSerializer.Deserialize<TimerRun>(File.ReadAllText(path));
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
        {
            // Missing or damaged: no timer. The next start overwrites a damaged file.
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
