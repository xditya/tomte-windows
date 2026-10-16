using System.Globalization;

namespace Tomte.Core.Triggers;

/// <summary>Why the Compact pill is up: an edge push, desktop peek, an event, the first-run hint, the rambler, the launch arrival or a closed AI view's Undo.</summary>
public enum CompactReason
{
    None,
    Summary,
    Peek,
    Event,
    Hint,
    Rambler,

    /// <summary>The pill the tomte lands in at launch: the tomte alone, so a start reads, loads and watches nothing for it.</summary>
    Arrival,

    /// <summary>A closed AI view's pill: what closed and Undo, both its own, so the pill's line shows nothing.</summary>
    Undo,
}

/// <summary>Which feature raised an event, so its pill can show that feature's content.</summary>
public enum IslandEventSource
{
    Other,
    Timer,
    TrackChange,
}

/// <summary>What fills the Compact pill's one line of live content.</summary>
public enum CompactContent
{
    None,
    TimerRing,
    NowPlaying,
}

/// <summary>
/// What the push pill may mention after the time of day, read as it shows: the job due next, the events that wait
/// unseen, the battery's charge while the PC runs on it (null on mains power or with no battery), and how many things
/// are on the shelf (0 until the shelf has loaded).
/// </summary>
public sealed record GlanceFacts
{
    public (string Name, DateTimeOffset At)? Job { get; init; }

    public IReadOnlyCollection<IslandEvent> Waiting { get; init; } = [];

    public int? Battery { get; init; }

    public int ShelfCount { get; init; }

    /// <summary>Focus or Do Not Disturb is on: waiting events are counted, never named.</summary>
    public bool Quiet { get; init; }

    /// <summary>The tray pause is on: the same as <see cref="Quiet"/>.</summary>
    public bool Paused { get; init; }
}

public static class CompactRules
{
    /// <summary>What the first-run hint says.</summary>
    public const string Hint = "Push your cursor up here";

    /// <summary>The timer's done event: "Timer done · 25:00", with the length the timer ran for.</summary>
    public static string TimerDone(TimeSpan length) => $"Timer done · {Media.NowPlaying.Clock(length)}";

    /// <summary>
    /// The words on the Compact pill: the hint, an event's line (beside the ring on the timer's done pill), or
    /// the time a running timer has left beside its ring on the push pill, or the time of day on a push pill with no
    /// timer and nothing playing (<paramref name="clock"/>, null while the now-playing row shows). Null where the pill
    /// shows no words of its own: peek, the rambler, and the track-change event, whose pill shows the now-playing row
    /// with the title. <paramref name="timerLeft"/> is the shown timer's time left, if any.
    /// </summary>
    public static string? Line(CompactReason reason, IReadOnlyList<IslandEvent> shown, TimeSpan? timerLeft, DateTimeOffset? clock = null) => reason switch
    {
        CompactReason.Hint => Hint,
        CompactReason.Event when Owner(shown) == IslandEventSource.TrackChange => null,
        CompactReason.Event => EventLine(shown),
        CompactReason.Summary when timerLeft is { } left => Countdown(left),
        CompactReason.Summary when clock is { } at => TimeOfDay(at),
        _ => null,
    };

    /// <summary>A job is mentioned this long before it is due, and a battery at or below this charge.</summary>
    public static readonly TimeSpan JobSoon = TimeSpan.FromHours(1);
    public const int BatteryLow = 20;

    /// <summary>The time of day as the user's culture writes a short time: "18:41" or "6:41 PM".</summary>
    public static string TimeOfDay(DateTimeOffset at, CultureInfo? culture = null) => at.ToLocalTime().ToString("t", culture ?? CultureInfo.CurrentCulture);

    /// <summary>The weekday and the date in the culture's own order, with short names: "Tue 14 Oct", "Tue Oct 14".</summary>
    public static string ShortDate(DateTimeOffset at, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var local = at.ToLocalTime();
        return $"{local.ToString("ddd", culture)} {local.ToString(culture.DateTimeFormat.MonthDayPattern.Replace("MMMM", "MMM", StringComparison.Ordinal), culture)}";
    }

    /// <summary>
    /// The push pill's line with no timer and nothing playing: the time of day, then the first that applies of a job due
    /// within the hour ("Report at 18:00"), what waits unseen (only counted during Focus, Do Not Disturb or the tray pause), a low battery while the PC runs on it, what is on the
    /// shelf, and the short date. Cut to <paramref name="width"/> as <paramref name="measure"/> lays a line out: the
    /// job's name or the mention ends in an ellipsis, a job's time stays, and with no room even for that the time of
    /// day stands alone.
    /// </summary>
    public static string Glance(DateTimeOffset now, GlanceFacts facts, float width, Func<string, float> measure, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        string time = TimeOfDay(now, culture);
        var (cut, kept) = facts switch
        {
            { Job: (var name, var at) } when at > now && at - now <= JobSoon => (name, $" at {TimeOfDay(at, culture)}"),
            { Waiting.Count: 1, Quiet: false, Paused: false } => (facts.Waiting.First().Text, ""),
            { Waiting.Count: > 0 and var n } => (n == 1 ? "1 event waiting" : $"{n} events waiting", ""),
            { Battery: <= BatteryLow and var charge } => ($"Battery {charge}%", ""),
            { ShelfCount: > 0 and var count } => ($"{count} on the shelf", ""),
            _ => (ShortDate(now, culture), ""),
        };

        // Cut between text elements, so a surrogate pair, a combining mark or an emoji sequence is kept whole or left out.
        int[] starts = StringInfo.ParseCombiningCharacters(cut);
        string Line(int elements) => elements == starts.Length ? $"{time} · {cut}{kept}" : $"{time} · {cut[..starts[elements]].TrimEnd()}…{kept}";
        bool Fits(int elements) => measure(Line(elements)) <= width;
        if (Fits(starts.Length))
        {
            return Line(starts.Length);
        }

        if (starts.Length < 2 || !Fits(1))
        {
            return time;
        }

        // The longest cut that fits, between one element, which does, and the whole, which does not.
        int fits = 1, over = starts.Length;
        while (over - fits > 1)
        {
            int mid = (fits + over) / 2;
            (fits, over) = Fits(mid) ? (mid, over) : (fits, mid);
        }

        return Line(fits);
    }

    /// <summary>One event says its own text; a digest says how many waited and what they were.</summary>
    public static string EventLine(IReadOnlyList<IslandEvent> shown) => shown switch
    {
        [] => "",
        [var only] => only.Text,
        _ => $"{shown.Count} events: {string.Join(", ", shown.Select(e => e.Text))}",
    };

    /// <summary>Time left, counted up to the whole second, so it reads 00:01 until the end and 00:00 at it.</summary>
    public static string Countdown(TimeSpan left) => Media.NowPlaying.Clock(TimeSpan.FromSeconds(Math.Ceiling(left.TotalSeconds)));

    /// <summary>
    /// One rule for the pill's one line. The push pill shows the timer ring while a timer runs (or has ended
    /// and waits for its done pill), else what plays. An event pill shows only its own event's content. The peek
    /// pill holds the tomte alone; with the tomte off it is the full pill with what plays, never the ring. A digest of
    /// several events, the first-run hint and the rambler show neither.
    /// </summary>
    public static CompactContent Content(CompactReason reason, IslandEventSource shown, bool timerRuns, bool playing, bool tomte = true) => reason switch
    {
        CompactReason.Summary => timerRuns ? CompactContent.TimerRing : playing ? CompactContent.NowPlaying : CompactContent.None,
        CompactReason.Peek when !tomte && playing => CompactContent.NowPlaying,
        CompactReason.Event when shown == IslandEventSource.Timer => CompactContent.TimerRing,
        CompactReason.Event when shown == IslandEventSource.TrackChange && playing => CompactContent.NowPlaying,
        _ => CompactContent.None,
    };

    /// <summary>
    /// Whose content an event pill shows: the feature that raised its one event. A digest of several shows
    /// only its own content, neither the timer ring nor what plays, whichever events it holds.
    /// </summary>
    public static IslandEventSource Owner(IReadOnlyList<IslandEvent> shown) =>
        shown is [var only] ? only.Source : IslandEventSource.Other;
}
