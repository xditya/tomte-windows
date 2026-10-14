namespace Tomte.Core.Triggers;

/// <summary>Why the Compact pill is up: an edge push, desktop peek, an event, the first-run hint or the rambler.</summary>
public enum CompactReason
{
    None,
    Summary,
    Peek,
    Event,
    Hint,
    Rambler,
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

    /// <summary>The time of day as the user's culture writes a short time: "18:41" or "6:41 PM".</summary>
    public static string TimeOfDay(DateTimeOffset at) => at.ToLocalTime().ToString("t", System.Globalization.CultureInfo.CurrentCulture);

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
