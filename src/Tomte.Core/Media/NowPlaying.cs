using System.Drawing;
using System.Numerics;
using Tomte.Core.Text;

namespace Tomte.Core.Media;

/// <summary>A transport command. As flags, the set of commands the source app accepts right now.</summary>
[Flags]
public enum MediaCommand
{
    None = 0,
    Play = 1,
    Pause = 2,
    Toggle = 4,
    Next = 8,
    Previous = 16,
}

/// <summary>
/// Album art, decoded once and kept small: 32-bit BGRA, premultiplied, rows top down. The stamp
/// identifies the encoded image it was decoded from, so the same art is never decoded twice.
/// </summary>
public sealed record MediaThumbnail(int Width, int Height, byte[] Pixels, int Stamp);

/// <summary>
/// A playback position as of a moment. An app reports its position only when something happens
/// (play, pause, seek, a new track), so the position in between is the last report plus the time
/// since, times the playback rate. Nothing here runs on a timer: ask when something draws.
/// </summary>
public readonly record struct MediaTimeline(TimeSpan Start, TimeSpan End, TimeSpan Position, DateTimeOffset AsOf, double Rate, bool Playing)
{
    public TimeSpan Duration => End - Start;

    public TimeSpan PositionAt(DateTimeOffset now)
    {
        var elapsed = now - AsOf;
        // A report stamped after "now" (the clock was set back, or the app stamps ahead) never runs backwards.
        var position = Playing && elapsed > TimeSpan.Zero ? Position + elapsed * Rate : Position;
        return position < Start ? Start : position > End ? End : position;
    }

    /// <summary>0 at the start, 1 at the end.</summary>
    public double ProgressAt(DateTimeOffset now) => (PositionAt(now) - Start) / Duration;

    /// <summary>
    /// Wall time until the end is reached, for a progress animation; null when the position is not moving
    /// forward. Simplification: a rewind (negative rate) holds the bar still until the next report.
    /// </summary>
    public TimeSpan? RemainingAt(DateTimeOffset now) => Playing && Rate > 0 ? (End - PositionAt(now)) / Rate : null;

    /// <summary>The same position re-stamped at <paramref name="now"/>: what a pause, a resume or a rate change does to the last report.</summary>
    public MediaTimeline Rebase(DateTimeOffset now, bool playing, double rate) =>
        this with { Position = PositionAt(now), AsOf = now, Playing = playing, Rate = rate };
}

/// <summary>
/// What plays now, as the views read it: every string and value the Compact row and the Media panel
/// show. Immutable; each report from the source app makes the next one through a With method.
/// </summary>
public sealed record NowPlaying
{
    /// <summary>What the title slot reads with no session.</summary>
    public const string NothingPlaying = "Nothing playing";

    public static readonly NowPlaying None = new();

    /// <summary>The source app's AppUserModelId; null when no app reports media.</summary>
    public string? SessionId { get; init; }

    /// <summary>The source app's name for display.</summary>
    public string App { get; init; } = "";

    public string Title { get; init; } = "";

    public string Artist { get; init; } = "";

    public bool IsPlaying { get; init; }

    public double Rate { get; init; } = 1;

    /// <summary>The commands the app accepts right now.</summary>
    public MediaCommand Commands { get; init; }

    /// <summary>Null when the app reports no timeline (a live stream, or an app that never sets one).</summary>
    public MediaTimeline? Timeline { get; init; }

    public MediaThumbnail? Thumbnail { get; init; }

    public bool HasSession => SessionId is not null;

    public TimeSpan? Duration => Timeline?.Duration;

    public TimeSpan? PositionAt(DateTimeOffset now) => Timeline?.PositionAt(now);

    /// <summary>What the play or pause button sends: the explicit command when the app takes it, else the toggle, else nothing.</summary>
    public MediaCommand PlayPause =>
        IsPlaying
            ? (Accepts(MediaCommand.Pause) ? MediaCommand.Pause : Commands & MediaCommand.Toggle)
            : (Accepts(MediaCommand.Play) ? MediaCommand.Play : Commands & MediaCommand.Toggle);

    public bool Accepts(MediaCommand command) => (Commands & command) != 0;

    public bool SameTrackAs(NowPlaying other) => SessionId == other.SessionId && Title == other.Title && Artist == other.Artist;

    /// <summary>
    /// True when this is worth a track-change event after <paramref name="announced"/> was the last track
    /// known: it plays, it has a title and it is another track. Resuming the same track is not a change.
    /// </summary>
    public bool IsNewTrackAfter(NowPlaying? announced) =>
        IsPlaying && Title.Length > 0 && (announced is null || !SameTrackAs(announced));

    /// <summary>A session just picked: nothing is known about it yet but its app.</summary>
    public static NowPlaying For(string sessionId) => new() { SessionId = sessionId, App = MediaSessions.AppName(sessionId) };

    /// <summary>A new title or artist is a new track, and the old track's art goes with it.</summary>
    public NowPlaying WithTrack(string title, string artist) =>
        title == Title && artist == Artist ? this : this with { Title = title, Artist = artist, Thumbnail = null };

    /// <summary>A pause, a resume or a rate change keeps the position where it was at that moment.</summary>
    public NowPlaying WithPlayback(bool playing, double rate, MediaCommand commands, DateTimeOffset now) =>
        playing == IsPlaying && rate == Rate
            ? (commands == Commands ? this : this with { Commands = commands })
            : this with { IsPlaying = playing, Rate = rate, Commands = commands, Timeline = Timeline?.Rebase(now, playing, rate) };

    /// <summary>A position report, which replaces whatever was extrapolated (a seek lands here too).</summary>
    public NowPlaying WithTimeline(TimeSpan start, TimeSpan end, TimeSpan position, DateTimeOffset asOf, DateTimeOffset now)
    {
        if (end <= start)
        {
            return Timeline is null ? this : this with { Timeline = null };
        }

        // An app that never stamps its reports leaves the stamp at the FILETIME epoch; the report is then as of now.
        var timeline = new MediaTimeline(start, end, position, asOf.Year < 2000 ? now : asOf, Rate, IsPlaying);
        return timeline == Timeline ? this : this with { Timeline = timeline };
    }

    /// <summary>"03:15", or "1:03:15" from an hour up.</summary>
    public static string Clock(TimeSpan time)
    {
        if (time < TimeSpan.Zero)
        {
            time = TimeSpan.Zero;
        }

        return time.TotalHours >= 1 ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}" : $"{time.Minutes:00}:{time.Seconds:00}";
    }
}

public static class MediaSessions
{
    /// <summary>
    /// Which of several sessions the island follows, as an index, or -1 with none. Windows names one
    /// session current (the one the media keys go to); it wins while it plays. When it is paused and
    /// another app plays, the playing one wins, because a panel showing a paused tab while music comes
    /// out of another app reads as broken. With nothing playing, the current session stays.
    /// </summary>
    public static int Pick(ReadOnlySpan<bool> playing, int current)
    {
        bool known = current >= 0 && current < playing.Length;
        if (known && playing[current])
        {
            return current;
        }

        int first = playing.IndexOf(true);
        return first >= 0 ? first : known ? current : playing.Length > 0 ? 0 : -1;
    }

    /// <summary>
    /// A readable name cut from an AppUserModelId: "Spotify.exe" and
    /// "SpotifyAB.SpotifyMusic_zpdnekdrzrea0!Spotify" both give "Spotify".
    /// a packaged app whose id does not end in its name reads oddly (Media Player is
    /// "Microsoft.ZuneMusic"). AppInfo.GetFromAppUserModelId has the display name, at the cost of
    /// loading the app model and state repository DLLs; ask it once the name is drawn somewhere.
    /// </summary>
    public static string AppName(string appUserModelId)
    {
        string name = appUserModelId[(appUserModelId.LastIndexOfAny(['\\', '/', '!']) + 1)..];
        return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? name[..^4] : name;
    }
}

/// <summary>
/// Who watches the media tracker and what for. Listening, position reports and art each start with the
/// first watcher that wants them and stop with the last. Add and Remove say which of the three flipped.
/// </summary>
public sealed class MediaWatchers
{
    private int all, timeline, art;

    [Flags]
    public enum Flip
    {
        None = 0,
        Listening = 1,
        Timeline = 2,
        Art = 4,
    }

    public bool Listening => all > 0;

    public bool Timeline => timeline > 0;

    public bool Art => art > 0;

    public Flip Add(bool wantsTimeline, bool wantsArt) => Count(1, wantsTimeline, wantsArt);

    /// <summary>Takes back one <see cref="Add"/> with the same arguments.</summary>
    public Flip Remove(bool wantsTimeline, bool wantsArt)
    {
        if (all == 0 || (wantsTimeline && timeline == 0) || (wantsArt && art == 0))
        {
            throw new InvalidOperationException("A watch was taken back that was never made.");
        }

        return Count(-1, wantsTimeline, wantsArt);
    }

    private Flip Count(int by, bool wantsTimeline, bool wantsArt)
    {
        var (wasListening, hadTimeline, hadArt) = (Listening, Timeline, Art);
        all += by;
        timeline += wantsTimeline ? by : 0;
        art += wantsArt ? by : 0;
        return (wasListening != Listening ? Flip.Listening : 0)
            | (hadTimeline != Timeline ? Flip.Timeline : 0)
            | (hadArt != Art ? Flip.Art : 0);
    }
}

/// <summary>
/// Where things sit in the Media panel's body (380 x 98 DIPs under the tab strip): art on the left,
/// then title, artist, the progress row and the transport row. The text slots are where the text is
/// drawn, each run as wide as its slot; the clocks have room for "99:59:59" either side of the
/// bar. The buttons are what a press is tested against.
/// a clock of 100 hours or more is cut with an ellipsis; measure it and move the bar if one shows up.
/// </summary>
public static class MediaPanelLayout
{
    public const float ArtCorner = 8;

    // How each run looks: the title, the artist (or the app, or a command that just failed), the two clocks.
    public static readonly TextStyle TitleStyle = new(14, 600);
    public static readonly TextStyle ArtistStyle = TextStyle.Secondary;
    public static readonly TextStyle FailedStyle = ArtistStyle with { Color = TextStyle.WarnColor };
    public static readonly TextStyle ElapsedStyle = TextStyle.Small with { Tabular = true };
    public static readonly TextStyle TotalStyle = ElapsedStyle with { Align = TextAlign.Trailing };

    public static readonly RectangleF Art = new(14, 16, 66, 66);
    public static readonly RectangleF Title = new(94, 9, 272, 19);
    public static readonly RectangleF Artist = new(94, 29, 272, 16);
    public static readonly RectangleF Elapsed = new(94, 49, 44, 14);
    public static readonly RectangleF Bar = new(140, 54, 180, 4);
    public static readonly RectangleF Total = new(322, 49, 44, 14);
    public static readonly RectangleF Previous = new(94, 66, 28, 28);
    public static readonly RectangleF PlayPause = new(128, 66, 28, 28);
    public static readonly RectangleF Next = new(162, 66, 28, 28);

    /// <summary>
    /// The command a press at this body point sends for what plays now. None outside the buttons, and
    /// on a button whose command the app does not accept.
    /// </summary>
    public static MediaCommand Command(Vector2 point, NowPlaying now) =>
        Previous.Contains(point.X, point.Y) ? now.Commands & MediaCommand.Previous
        : PlayPause.Contains(point.X, point.Y) ? now.PlayPause
        : Next.Contains(point.X, point.Y) ? now.Commands & MediaCommand.Next
        : MediaCommand.None;
}
