using System.Drawing;
using Tomte.Core.Interaction;
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

    /// <summary>The app takes a new playback position; sent through <see cref="MediaTracker.SeekAsync"/>, not as a command.</summary>
    Seek = 32,
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

    /// <summary>Whether the bar is a seek bar: the app takes a position and reports a timeline to place it on.</summary>
    public bool CanSeek => Accepts(MediaCommand.Seek) && Timeline is not null;

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
/// A seek sent to <paramref name="To"/> from the timeline <paramref name="From"/> at <paramref name="At"/>. The bar holds
/// there while the app still reports that timeline, for up to <see cref="Wait"/>; then it shows what the app says again.
/// </summary>
public readonly record struct PendingSeek(TimeSpan To, MediaTimeline From, DateTimeOffset At)
{
    public static readonly TimeSpan Wait = TimeSpan.FromSeconds(2);

    /// <summary>Keys held on the seek bar send at most one seek this often; the last position wins.</summary>
    public static readonly TimeSpan KeyGap = TimeSpan.FromMilliseconds(150);

    public bool Holds(MediaTimeline? reported, DateTimeOffset now) => reported == From && now - At < Wait;
}

/// <summary>
/// Where things sit in the Media panel's body, <paramref name="width"/> DIPs wide and 98 tall under the tab strip (560
/// unless the monitor caps the panel): art on the left, then title, artist and the progress row out to the right
/// padding, and the transport row centred under the bar. The bar takes what the clocks leave, 360 at the full width.
/// The text slots are where the text is drawn, each run as wide as its slot; the clocks have room for "99:59:59" either
/// side of the bar. The buttons (32 DIP square) and the seek area (24 DIP tall, as far past each end of the bar as the
/// knob reaches) are what a press is tested against; they touch and do not overlap.
/// a clock of 100 hours or more is cut with an ellipsis; measure it and move the bar if one shows up.
/// </summary>
public sealed class MediaPanelLayout(float width)
{
    public const float ArtCorner = 8;

    /// <summary>The play or pause disc is drawn this wide in the middle of its button.</summary>
    public const float Disc = 28;

    /// <summary>The knob's radius: at the fill's end while the pointer is on the bar, under the pointer while it drags.</summary>
    public const float Knob = 6;

    /// <summary>How far one arrow key moves the position.</summary>
    public static readonly TimeSpan SeekStep = TimeSpan.FromSeconds(5);

    // How each run looks: the title, the artist (or the app, or a command that just failed), the two clocks.
    public static readonly TextStyle TitleStyle = new(14, 600);
    public static readonly TextStyle ArtistStyle = TextStyle.Secondary;
    public static readonly TextStyle FailedStyle = ArtistStyle with { Color = Theme.Warn };
    public static readonly TextStyle ElapsedStyle = TextStyle.Small with { Tabular = true };
    public static readonly TextStyle TotalStyle = ElapsedStyle with { Align = TextAlign.Trailing };

    /// <summary>The time a seek would go to, on its card above the bar.</summary>
    public static readonly TextStyle SeekStyle = ElapsedStyle with { Color = Theme.Ink, Align = TextAlign.Center };

    // The room kept free on the right, and the gap between a clock and the bar.
    private const float RightPad = 14, ClockGap = 2;

    public static readonly RectangleF Art = new(14, 16, 66, 66);
    public static readonly RectangleF Elapsed = new(94, 43, 44, 14);

    public float Width => width;

    public RectangleF Title => new(Elapsed.X, 6, width - RightPad - Elapsed.X, 19);

    public RectangleF Artist => new(Elapsed.X, 25, width - RightPad - Elapsed.X, 16);

    public RectangleF Total => new(width - RightPad - Elapsed.Width, Elapsed.Y, Elapsed.Width, Elapsed.Height);

    public RectangleF Bar => RectangleF.FromLTRB(Elapsed.Right + ClockGap, 48, Total.Left - ClockGap, 52);

    public RectangleF Seek => new(Bar.X - Knob, Bar.Y + (Bar.Height / 2) - 12, Bar.Width + (2 * Knob), 24);

    public RectangleF PlayPause => new(Bar.X + (Bar.Width / 2) - 16, 62, 32, 32);

    public RectangleF Previous => PlayPause with { X = PlayPause.X - 36 };

    public RectangleF Next => PlayPause with { X = PlayPause.X + 36 };

    public const string SeekId = "seek", PreviousId = "previous", PlayPauseId = "playpause", NextId = "next";

    /// <summary>
    /// The clickable parts for what plays now, in Tab order: the seek bar while the app takes a position (a plain
    /// progress bar otherwise, and no target), then previous, play or pause and next, each a Tab stop of its own, since
    /// on them Left and Right skip tracks and Space plays or pauses. A button whose command the app does not accept is
    /// disabled.
    /// </summary>
    public IReadOnlyList<Target> Targets(NowPlaying now)
    {
        var buttons = new List<Target>();
        if (now.CanSeek)
        {
            buttons.Add(new(SeekId, Seek, Seek.Height / 2, TargetKind.Slider, Draggable: true));
        }

        buttons.Add(new(PreviousId, Previous, Previous.Width / 2, TargetKind.Button, now.Accepts(MediaCommand.Previous), "Previous", Key: "Left"));
        buttons.Add(new(PlayPauseId, PlayPause, PlayPause.Width / 2, TargetKind.Button, now.PlayPause != MediaCommand.None, now.IsPlaying ? "Pause" : "Play", Key: "Space"));
        buttons.Add(new(NextId, Next, Next.Width / 2, TargetKind.Button, now.Accepts(MediaCommand.Next), "Next", Key: "Right"));
        return buttons;
    }

    /// <summary>
    /// A key on one of the transport buttons: Space plays or pauses, Left and Right skip to the previous and next track,
    /// as the button with that id would. Null for any other key or target (the seek bar takes Left and Right itself).
    /// </summary>
    public static string? KeyFor(string id, NavKey key) => (id, key) switch
    {
        (PreviousId or PlayPauseId or NextId or SeekId, NavKey.Space) => PlayPauseId,
        (PreviousId or PlayPauseId or NextId, NavKey.Left) => PreviousId,
        (PreviousId or PlayPauseId or NextId, NavKey.Right) => NextId,
        _ => null,
    };

    /// <summary>What a click on a transport button sends now; None for anything else or a command the app does not accept.</summary>
    public static MediaCommand Command(string id, NowPlaying now) => id switch
    {
        PreviousId => now.Commands & MediaCommand.Previous,
        PlayPauseId => now.PlayPause,
        NextId => now.Commands & MediaCommand.Next,
        _ => MediaCommand.None,
    };

    /// <summary>The position a point on the bar stands for, held to the track's ends.</summary>
    public TimeSpan SeekAt(float x, MediaTimeline timeline) =>
        timeline.Start + timeline.Duration * Math.Clamp((x - Bar.X) / Bar.Width, 0, 1);

    /// <summary>Where on the bar a position is, from its left end to its right one.</summary>
    public float BarX(TimeSpan position, MediaTimeline timeline) =>
        Bar.X + Bar.Width * (float)Math.Clamp((position - timeline.Start) / timeline.Duration, 0, 1);

    /// <summary>An arrow key's seek: <paramref name="steps"/> times <see cref="SeekStep"/> on from here, held to the track.</summary>
    public static TimeSpan Stepped(TimeSpan from, int steps, MediaTimeline timeline)
    {
        var to = from + SeekStep * steps;
        return to < timeline.Start ? timeline.Start : to > timeline.End ? timeline.End : to;
    }

    /// <summary>The seek time's card: centred on <paramref name="x"/> held to the bar, just above it, over the artist's line.</summary>
    public RectangleF SeekLabel(float x) => new(Math.Clamp(x, Bar.Left, Bar.Right) - 22, Bar.Y - 20, 44, 16);
}
