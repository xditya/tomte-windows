using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tomte.Core.Jobs;

/// <summary>
/// When a job runs: once on a date, or on chosen days of the week, at a time of day. Times are wall-clock times in the
/// user's zone, so a 09:00 job stays at 09:00 across a daylight-saving change. Read from a JSON object with "time"
/// ("HH:MM") and either "date" ("YYYY-MM-DD") or "days" (["mon", ...]); the model's tool call and the stored row use
/// the same shape.
/// </summary>
public sealed record JobSchedule
{
    private static readonly string[] DayKeys = ["sun", "mon", "tue", "wed", "thu", "fri", "sat"];
    private static readonly DayOfWeek[] Weekdays = [DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday];

    public required TimeOnly Time { get; init; }

    /// <summary>A one-off job's day; null for a repeating one.</summary>
    public DateOnly? Date { get; init; }

    /// <summary>A repeating job's days, Sunday first; empty for a one-off.</summary>
    public IReadOnlyList<DayOfWeek> Days { get; init; } = [];

    /// <summary>
    /// Reads "time" and "date" or "days" from a JSON object. False with a message the model can act on when one is
    /// missing, both are given, or a value does not parse. Small models send "Monday" or "weekdays" as well as "mon",
    /// and a list as one comma-separated string; those are taken.
    /// </summary>
    public static bool TryRead(JsonElement input, out JobSchedule? schedule, out string? error)
    {
        schedule = null;
        if (input.ValueKind != JsonValueKind.Object)
        {
            error = "Arguments must be a JSON object.";
            return false;
        }

        if (Text(input, "time") is not { Length: > 0 } timeText)
        {
            error = "time is missing. Ask the user at what time the job should run.";
            return false;
        }

        if (!TimeOnly.TryParse(timeText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var time))
        {
            error = $"time must be a 24-hour time such as 09:00, not \"{timeText}\".";
            return false;
        }

        time = new TimeOnly(time.Hour, time.Minute);
        // Small models fill every field: an empty or "null" date means none.
        bool hasDate = input.TryGetProperty("date", out var dateValue) && dateValue.ValueKind != JsonValueKind.Null
            && !(dateValue.ValueKind == JsonValueKind.String && dateValue.GetString()!.Trim() is "" or "null");
        var days = Strings(input, "days").Where(d => d != "null").ToList();
        if (hasDate == days.Count > 0)
        {
            error = hasDate
                ? "Give days for a repeating job or date for a one-off, not both."
                : "Say when the job runs: days for a repeating job, or date for a one-off. Ask the user when that is unclear.";
            return false;
        }

        if (hasDate)
        {
            // The day only: a model that sends "2026-10-15T08:30:00" gets the time from "time".
            string dateText = dateValue.ValueKind == JsonValueKind.String ? dateValue.GetString()!.Trim() : "";
            if (!DateOnly.TryParseExact(dateText.Length > 10 && dateText[10] == 'T' ? dateText[..10] : dateText, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var date))
            {
                error = "date must be a day such as 2026-10-15.";
                return false;
            }

            schedule = new JobSchedule { Time = time, Date = date };
            error = null;
            return true;
        }

        var set = new SortedSet<DayOfWeek>();
        foreach (var day in days)
        {
            if (ParseDay(day) is not { } parsed)
            {
                error = $"days takes mon, tue, wed, thu, fri, sat or sun, not \"{day}\".";
                return false;
            }

            set.UnionWith(parsed);
        }

        schedule = new JobSchedule { Time = time, Days = [.. set] };
        error = null;
        return true;
    }

    /// <summary>The shape <see cref="TryRead"/> takes, for storing.</summary>
    public string ToJson()
    {
        var node = new JsonObject { ["time"] = Time.ToString("HH:mm", CultureInfo.InvariantCulture) };
        if (Date is { } date)
        {
            node["date"] = date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        else
        {
            node["days"] = new JsonArray([.. Days.Select(d => JsonValue.Create(DayKeys[(int)d]))]);
        }

        return node.ToJsonString();
    }

    public static JobSchedule FromJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return TryRead(doc.RootElement, out var schedule, out var error) ? schedule! : throw new FormatException(error);
    }

    /// <summary>
    /// The first run strictly after <paramref name="after"/>, or null for a one-off whose time has passed. A time the
    /// clock skips when it springs forward runs when the clock gets past the gap; a time the clock passes twice when it
    /// falls back runs the first time only.
    /// </summary>
    public DateTimeOffset? Next(DateTimeOffset after, TimeZoneInfo zone)
    {
        if (Date is { } once)
        {
            var at = Instant(once, Time, zone);
            return at > after ? at : null;
        }

        var start = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(after, zone).DateTime);
        // Eight days reach the same weekday a week on, which covers a run earlier today on a one-day schedule.
        for (int i = 0; i <= 7; i++)
        {
            var day = start.AddDays(i);
            if (Days.Contains(day.DayOfWeek) && Instant(day, Time, zone) is var at && at > after)
            {
                return at;
            }
        }

        return null;
    }

    /// <summary>The schedule in plain words: "Every weekday at 09:00", "On Thursday 15 October 2026 at 09:00".</summary>
    public string Describe()
    {
        var names = CultureInfo.InvariantCulture.DateTimeFormat;
        string at = Time.ToString("HH:mm", CultureInfo.InvariantCulture);
        if (Date is { } date)
        {
            return $"On {names.GetDayName(date.DayOfWeek)} {date.Day} {names.GetMonthName(date.Month)} {date.Year} at {at}";
        }

        string days = Days.Count == 7 ? "day"
            : Days.SequenceEqual(Weekdays) ? "weekday"
            : JoinWords([.. Days.Select(names.GetDayName)]);
        return $"Every {days} at {at}";
    }

    /// <summary>"a", "a and b", "a, b and c".</summary>
    public static string JoinWords(IReadOnlyList<string> words) =>
        words.Count <= 1 ? string.Concat(words) : $"{string.Join(", ", words.Take(words.Count - 1))} and {words[^1]}";

    private static DateTimeOffset Instant(DateOnly day, TimeOnly time, TimeZoneInfo zone)
    {
        var local = day.ToDateTime(time);
        // minute steps to the end of a skipped hour; every zone's gap is an hour or less.
        while (zone.IsInvalidTime(local))
        {
            local = local.AddMinutes(1);
        }

        var offset = zone.IsAmbiguousTime(local) ? zone.GetAmbiguousTimeOffsets(local).Max() : zone.GetUtcOffset(local);
        return new DateTimeOffset(local, offset);
    }

    private static IEnumerable<DayOfWeek>? ParseDay(string day)
    {
        string key = day.Trim().ToLowerInvariant();
        if (key is "weekday" or "weekdays")
        {
            return Weekdays;
        }

        if (key is "daily" or "everyday" or "every day")
        {
            return Enum.GetValues<DayOfWeek>();
        }

        int i = key.Length >= 3 ? Array.IndexOf(DayKeys, key[..3]) : -1;
        // "mon", "monday", "Mon." but never "month".
        return i >= 0 && (key.Length == 3 || CultureInfo.InvariantCulture.DateTimeFormat.GetDayName((DayOfWeek)i).StartsWith(key.TrimEnd('.'), StringComparison.OrdinalIgnoreCase))
            ? [(DayOfWeek)i]
            : null;
    }

    private static string? Text(JsonElement input, string name) =>
        input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()!.Trim() : null;

    /// <summary>
    /// A JSON array of strings, or the same array sent as a string, or one comma-separated string; missing or null is
    /// empty. llama3.2:3b sends "[\"mon\", \"tue\"]" as often as the array itself.
    /// </summary>
    public static IReadOnlyList<string> Strings(JsonElement input, string name)
    {
        if (!input.TryGetProperty(name, out var v))
        {
            return [];
        }

        if (v.ValueKind == JsonValueKind.String && v.GetString()!.TrimStart().StartsWith('['))
        {
            try
            {
                using var inner = JsonDocument.Parse(v.GetString()!);
                return Items(inner.RootElement);
            }
            catch (JsonException)
            {
                // Not an array after all: read as a comma-separated string below.
            }
        }

        return v.ValueKind switch
        {
            JsonValueKind.Array => Items(v),
            JsonValueKind.String => [.. v.GetString()!.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)],
            _ => [],
        };

        static string[] Items(JsonElement array) => array.ValueKind != JsonValueKind.Array ? []
            : [.. array.EnumerateArray().Select(e => e.ValueKind == JsonValueKind.String ? e.GetString()!.Trim() : e.GetRawText()).Where(s => s.Length > 0)];
    }
}
