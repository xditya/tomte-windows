using System.Globalization;
using System.Text.RegularExpressions;

namespace Tomte.Core.Jobs;

/// <summary>A reminder read from typed words: what to say, when, and the first time it rings.</summary>
public sealed record ReminderRequest(string Text, JobSchedule Schedule, DateTimeOffset Due);

/// <summary>
/// Reads "remind me ..." without a model: "in 20 minutes", "at 17:00", "at 5pm", "tomorrow at 9", "on friday at 10",
/// "every weekday at 09:30", with what to say before or after them ("to" in front of it is dropped). Anything it cannot
/// read for certain is null, and the AI, when there is one, gets the words instead.
/// </summary>
public static partial class ReminderParser
{
    private const string Day = "monday|tuesday|wednesday|thursday|friday|saturday|sunday|mon|tues|tue|wed|thurs|thur|thu|fri|sat|sun";
    private const string FullDay = "monday|tuesday|wednesday|thursday|friday|saturday|sunday";

    // A time ahead of now is at most this far off: a week, as far as "in N hours" reasonably goes.
    private static readonly TimeSpan Longest = TimeSpan.FromDays(7);

    [GeneratedRegex(@"\bremind me\b", RegexOptions.IgnoreCase)]
    private static partial Regex RemindMe();

    [GeneratedRegex(@"\bin\s+(?<n>\d{1,4}|an?|one|half\s+an)\s*(?<unit>minutes?|mins?|m|hours?|hrs?|h)\b", RegexOptions.IgnoreCase)]
    private static partial Regex After();

    [GeneratedRegex($@"\b(?:daily|every\s+(?<days>day|weekdays?|(?:{Day})(?:\s*(?:,|and|&)\s*(?:{Day}))*))\b", RegexOptions.IgnoreCase)]
    private static partial Regex Every();

    // "on" is needed before a short name, so "sat" or "wed" in the text stays text.
    [GeneratedRegex($@"\b(?:on\s+(?<day>{Day})|(?<day>{FullDay}))\b", RegexOptions.IgnoreCase)]
    private static partial Regex On();

    [GeneratedRegex(@"\b(?<when>tomorrow|today|tonight)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Relative();

    [GeneratedRegex(@"(?<at>\bat\s+)?(?:(?<word>noon|midday|midnight)|\b(?<h>\d{1,2})(?:[:.](?<m>\d{2}))?\s*(?<ap>[ap]\.?m\.?)?)(?![\w:])", RegexOptions.IgnoreCase)]
    private static partial Regex Clock();

    /// <summary>The words hold "remind me", so they are meant as a reminder even when <see cref="Parse"/> cannot read them.</summary>
    public static bool IsReminder(string typed) => RemindMe().IsMatch(typed);

    /// <summary>The reminder in <paramref name="typed"/>, its times in <paramref name="zone"/>; null when it is not one, or not certain.</summary>
    public static ReminderRequest? Parse(string typed, DateTimeOffset now, TimeZoneInfo zone)
    {
        if (RemindMe().Match(typed) is not { Success: true } remind)
        {
            return null;
        }

        string rest = Cut(typed, remind);
        var after = After().Match(rest);
        if (after.Success)
        {
            rest = Cut(rest, after);
        }

        var every = Every().Match(rest);
        if (every.Success)
        {
            rest = Cut(rest, every);
        }

        var on = On().Match(rest);
        if (on.Success)
        {
            rest = Cut(rest, on);
        }

        var relative = Relative().Match(rest);
        if (relative.Success)
        {
            rest = Cut(rest, relative);
        }

        // A number in the text is a time only with "at", minutes or am/pm: "call 3 people at 5" is at 5.
        var clock = Clock().Matches(rest).FirstOrDefault(m => m.Groups["at"].Success || m.Groups["word"].Success || m.Groups["m"].Success || m.Groups["ap"].Success);
        if (clock is not null)
        {
            rest = Cut(rest, clock);
        }

        string text = Text(rest);
        int days = (every.Success ? 1 : 0) + (on.Success ? 1 : 0) + (relative.Success ? 1 : 0);
        if (text.Length is 0 or > ReminderStore.MaxText || days > 1)
        {
            return null;
        }

        if (after.Success)
        {
            return days > 0 || clock is not null || Span(after) is not { } span ? null : In(text, now + span, zone);
        }

        if (clock is null || Time(clock) is not { } parsed)
        {
            return null;
        }

        var (time, open) = parsed;

        var today = DateOnly.FromDateTime(TimeZoneInfo.ConvertTime(now, zone).DateTime);
        if (every.Success)
        {
            var set = new SortedSet<DayOfWeek>();
            foreach (var word in (every.Groups["days"].Success ? every.Groups["days"].Value : "day").Split([',', '&', ' '], StringSplitOptions.RemoveEmptyEntries))
            {
                if (!word.Equals("and", StringComparison.OrdinalIgnoreCase))
                {
                    set.UnionWith(word.Equals("day", StringComparison.OrdinalIgnoreCase) ? Enum.GetValues<DayOfWeek>() : JobSchedule.ParseDay(word)!);
                }
            }

            var schedule = new JobSchedule { Time = time, Days = [.. set] };
            return schedule.Next(now, zone) is { } first ? new ReminderRequest(text, schedule, first) : null;
        }

        // The days it may fall on, nearest first; an hour with no am or pm ("at 5") is its morning or its evening,
        // whichever comes first today, and the evening tonight.
        string when = relative.Groups["when"].Value.ToLowerInvariant();
        IEnumerable<DateOnly> dates = on.Success
            ? Enumerable.Range(0, 8).Select(today.AddDays).Where(d => JobSchedule.ParseDay(on.Groups["day"].Value)!.Contains(d.DayOfWeek))
            : when == "tomorrow" ? [today.AddDays(1)]
            : relative.Success ? [today]
            : [today, today.AddDays(1)];
        IEnumerable<TimeOnly> times = !open || on.Success || when == "tomorrow" ? [time]
            : when == "tonight" ? [time.AddHours(12)]
            : [time, time.AddHours(12)];
        return dates.SelectMany(d => times.Select(t => new JobSchedule { Date = d, Time = t }))
            .Select(s => (Schedule: s, First: s.Next(now, zone)))
            .FirstOrDefault(c => c.First is not null) is { First: { } at } chosen
            ? new ReminderRequest(text, chosen.Schedule, at)
            : null;
    }

    // "in 20 minutes" rings at that instant; the schedule keeps its minute for the list.
    private static ReminderRequest In(string text, DateTimeOffset at, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        return new ReminderRequest(text, new JobSchedule { Date = DateOnly.FromDateTime(local.DateTime), Time = new TimeOnly(local.Hour, local.Minute) }, at);
    }

    private static TimeSpan? Span(Match after)
    {
        string n = after.Groups["n"].Value.ToLowerInvariant();
        bool hours = after.Groups["unit"].Value.StartsWith('h');
        if (n.StartsWith("half", StringComparison.Ordinal))
        {
            // "in half an hour"; half a minute is not a reminder.
            return hours ? TimeSpan.FromMinutes(30) : null;
        }

        int count = n is "a" or "an" or "one" ? 1 : int.Parse(n, CultureInfo.InvariantCulture);
        var span = hours ? TimeSpan.FromHours(count) : TimeSpan.FromMinutes(count);
        return span >= TimeSpan.FromMinutes(1) && span <= Longest ? span : null;
    }

    /// <summary>The time of day, and whether it is an hour with no am or pm that could be either ("at 5", "at 9:30" but not "at 09:30").</summary>
    private static (TimeOnly Time, bool Open)? Time(Match clock)
    {
        switch (clock.Groups["word"].Value.ToLowerInvariant())
        {
            case "noon" or "midday":
                return (new TimeOnly(12, 0), false);
            case "midnight":
                return (new TimeOnly(0, 0), false);
        }

        int hour = int.Parse(clock.Groups["h"].Value, CultureInfo.InvariantCulture);
        int minute = clock.Groups["m"].Success ? int.Parse(clock.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        if (minute > 59)
        {
            return null;
        }

        if (clock.Groups["ap"].Success)
        {
            bool pm = char.ToLowerInvariant(clock.Groups["ap"].Value[0]) == 'p';
            return hour is >= 1 and <= 12 ? (new TimeOnly((hour % 12) + (pm ? 12 : 0), minute), false) : null;
        }

        return hour <= 23 ? (new TimeOnly(hour, minute), hour is >= 1 and <= 11 && clock.Groups["h"].Length == 1) : null;
    }

    private static string Cut(string text, Match match) => text[..match.Index] + " " + text[(match.Index + match.Length)..];

    // What is left once the times are cut out, without "to" or "that" before it and the punctuation around it.
    private static string Text(string rest)
    {
        string text = string.Join(' ', rest.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)).Trim(' ', ',', '.', ';', ':', '-');
        foreach (var lead in new[] { "to ", "that ", "about " })
        {
            if (text.StartsWith(lead, StringComparison.OrdinalIgnoreCase))
            {
                return text[lead.Length..].Trim(' ', ',', '.', ';', ':', '-');
            }
        }

        return text is "to" or "that" ? "" : text;
    }
}
