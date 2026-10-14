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
    /// <summary>
    /// One rule for the pill's one line. The push pill shows the timer ring while a timer runs (or has ended
    /// and waits for its done pill), else what plays. The peek pill shows what plays and never the ring. An
    /// event pill shows only its own event's content. The first-run hint and the rambler show neither.
    /// </summary>
    public static CompactContent Content(CompactReason reason, IslandEventSource shown, bool timerRuns, bool playing) => reason switch
    {
        CompactReason.Summary => timerRuns ? CompactContent.TimerRing : playing ? CompactContent.NowPlaying : CompactContent.None,
        CompactReason.Peek => playing ? CompactContent.NowPlaying : CompactContent.None,
        CompactReason.Event when shown == IslandEventSource.Timer => CompactContent.TimerRing,
        CompactReason.Event when shown == IslandEventSource.TrackChange && playing => CompactContent.NowPlaying,
        _ => CompactContent.None,
    };

    /// <summary>
    /// Whose content an event pill shows: the timer's when one of its events is the timer's done event, else
    /// the media's when one is a track change, else nobody's. A digest of several follows the same order.
    /// </summary>
    public static IslandEventSource Owner(IReadOnlyList<IslandEvent> shown) =>
        shown.Any(e => e.Source == IslandEventSource.Timer) ? IslandEventSource.Timer
        : shown.Any(e => e.Source == IslandEventSource.TrackChange) ? IslandEventSource.TrackChange
        : IslandEventSource.Other;
}
