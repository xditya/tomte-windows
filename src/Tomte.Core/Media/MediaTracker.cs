using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Control;
using Windows.Storage.Streams;
using Manager = Windows.Media.Control.GlobalSystemMediaTransportControlsSessionManager;
using Session = Windows.Media.Control.GlobalSystemMediaTransportControlsSession;

namespace Tomte.Core.Media;

/// <summary>
/// Follows the system's media sessions (GlobalSystemMediaTransportControlsSessionManager) and keeps one
/// <see cref="NowPlaying"/>. Everything is event driven; nothing polls. It listens only while something
/// watches: a view that shows, or the track-change event when the user turned that on. Unwatched, every
/// handler is removed, and a new watch re-reads the state.
/// The calls into the system (cross-process, and slow the first time) run one at a time on pool threads,
/// never on the caller's thread. <see cref="Changed"/> and <see cref="TrackChanged"/> are raised through
/// <c>notify</c>, which the owner points at its own thread; every public member is safe to use from there.
/// </summary>
public sealed class MediaTracker
{
    // Short side of the cached art in pixels: the panel draws it at 66 DIPs, so this is sharp up to 200%.
    private const int ThumbnailSide = 132;

    // Art comes from another app; anything this large is not a thumbnail.
    private const ulong MaxEncodedBytes = 16 << 20;

    private readonly Action<Action> notify;
    private readonly Func<DateTimeOffset> now;
    private readonly Action<string>? log;
    private readonly TaskFactory work = new(new ConcurrentExclusiveSchedulerPair().ExclusiveScheduler);
    private readonly Action hook, pick, readTimeline, readTrack, raiseChanged;
    private readonly TypedEventHandler<Manager, SessionsChangedEventArgs> onSessions;
    private readonly TypedEventHandler<Manager, CurrentSessionChangedEventArgs> onCurrent;
    private readonly TypedEventHandler<Session, PlaybackInfoChangedEventArgs> onPlayback;
    private readonly TypedEventHandler<Session, MediaPropertiesChangedEventArgs> onProperties;
    private readonly TypedEventHandler<Session, TimelinePropertiesChangedEventArgs> onTimeline;
    private readonly List<Session> sessions = [];
    private volatile NowPlaying current = NowPlaying.None;
    private volatile string? failure;
    private Manager? manager;
    private Session? picked;
    private NowPlaying? announced;
    private int watchers, viewers, changePending;
    private bool listening, requesting, trackKnown, trackReading, trackStale;

    public MediaTracker(Action<Action> notify, Func<DateTimeOffset> now, Action<string>? log = null)
    {
        this.notify = notify;
        this.now = now;
        this.log = log;
        (hook, pick, readTimeline, readTrack) = (Hook, Pick, ReadTimeline, ReadTrack);
        raiseChanged = () =>
        {
            Volatile.Write(ref changePending, 0);
            Changed?.Invoke();
        };
        // The handlers run on the system's threads. Only the followed session has the last two attached.
        onSessions = (_, _) => Post(hook);
        onCurrent = (_, _) => Post(pick);
        onPlayback = (_, _) => Post(pick);
        onProperties = (_, _) => Post(readTrack);
        onTimeline = (_, _) => Post(readTimeline);
    }

    /// <summary>The last state read. Only current while something watches.</summary>
    public NowPlaying Current => current;

    /// <summary>Why the last transport command did nothing, for the text renderer; null after one that worked.</summary>
    public string? Failure => failure;

    /// <summary>Raised once for a burst of changes; read <see cref="Current"/> for where things stand.</summary>
    public event Action? Changed;

    /// <summary>Another track started playing while watched. The track found when a watch begins is not announced.</summary>
    public event Action<NowPlaying>? TrackChanged;

    /// <summary>
    /// Start listening. A view watches with the timeline. The track-change event watches without it: some
    /// apps report their position every second, and every report would run code here while the island is
    /// hidden.
    /// </summary>
    public void Watch(bool timeline = true) => Post(() =>
    {
        if (timeline && viewers++ == 0 && listening)
        {
            HookTimeline(true);
        }

        if (watchers++ > 0)
        {
            return;
        }

        if (manager is not null)
        {
            Listen();
        }
        else if (!requesting)
        {
            requesting = true;
            _ = RequestAsync();
        }
    });

    public void Unwatch(bool timeline = true) => Post(() =>
    {
        if (timeline && --viewers == 0 && listening)
        {
            HookTimeline(false);
        }

        if (--watchers > 0 || !listening)
        {
            return;
        }

        listening = false;
        manager!.SessionsChanged -= onSessions;
        manager.CurrentSessionChanged -= onCurrent;
        // The last state stays, so a new watch of the same track still has its art.
        Unhook();
    });

    /// <summary>
    /// Sends a transport command to the session being followed. It acts on another app, on the user's own
    /// click. False when the app refused, went away, or nothing plays; <see cref="Failure"/> then says which.
    /// </summary>
    public Task<bool> SendAsync(MediaCommand command) => work.StartNew(() => SendOnWorkAsync(command)).Unwrap();

    private async Task<bool> SendOnWorkAsync(MediaCommand command)
    {
        var session = picked;
        string app = current.App, name = command.ToString().ToLowerInvariant();
        string? failed = null;
        if (session is null)
        {
            failed = "Nothing is playing.";
        }
        else
        {
            try
            {
                var sent = command switch
                {
                    MediaCommand.Play => session.TryPlayAsync(),
                    MediaCommand.Pause => session.TryPauseAsync(),
                    MediaCommand.Toggle => session.TryTogglePlayPauseAsync(),
                    MediaCommand.Next => session.TrySkipNextAsync(),
                    MediaCommand.Previous => session.TrySkipPreviousAsync(),
                    _ => null,
                };
                if (sent is null || !await sent.AsTask().ConfigureAwait(false))
                {
                    failed = $"{app} did not accept {name}.";
                }
            }
            catch (Exception e)
            {
                // The app closed between the click and the call.
                failed = $"{app} is gone.";
                log?.Invoke($"media: {name} threw {e.GetType().Name} 0x{e.HResult:X8}");
            }
        }

        log?.Invoke(failed is null ? $"media: {name} accepted by {app}" : $"media: {name} failed: {failed}");
        if (failed != failure)
        {
            failure = failed;
            NotifyChanged();
        }

        return failed is null;
    }

    /// <summary>Run on the tracker's own queue: one piece of work at a time, in order, on a pool thread.</summary>
    private void Post(Action action) => work.StartNew(() =>
    {
        try
        {
            action();
        }
        catch (Exception e)
        {
            // A session can die between any two calls; the next event puts things right.
            log?.Invoke($"media: {e.GetType().Name}: {e.Message}");
        }
    });

    private async Task RequestAsync()
    {
        Manager? requested = null;
        try
        {
            requested = await Manager.RequestAsync().AsTask().ConfigureAwait(false);
        }
        catch (Exception e)
        {
            log?.Invoke($"media: no session manager: {e.Message}");
        }

        Post(() =>
        {
            requesting = false;
            manager ??= requested;
            if (manager is not null && watchers > 0 && !listening)
            {
                Listen();
            }
        });
    }

    private void Listen()
    {
        listening = true;
        trackKnown = false;
        manager!.SessionsChanged += onSessions;
        manager.CurrentSessionChanged += onCurrent;
        Hook();
        // Whatever plays when a watch begins is where things stand, not a change. With a session, its
        // track is known once the first read of it lands.
        trackKnown = picked is null;
        announced = current;
    }

    /// <summary>The set of sessions changed: listen to the playback state of each, then pick.</summary>
    private void Hook()
    {
        if (!listening)
        {
            return;
        }

        Unhook();
        try
        {
            sessions.AddRange(manager!.GetSessions());
            foreach (var session in sessions)
            {
                session.PlaybackInfoChanged += onPlayback;
            }
        }
        catch (Exception e)
        {
            log?.Invoke($"media: sessions unreadable: {e.Message}");
        }

        Pick();
    }

    private void Unhook()
    {
        Follow(null);
        foreach (var session in sessions)
        {
            Quietly(() => session.PlaybackInfoChanged -= onPlayback);
        }

        sessions.Clear();
    }

    private void Pick()
    {
        if (!listening)
        {
            return;
        }

        Span<bool> playing = stackalloc bool[sessions.Count];
        int currentIndex = -1;
        try
        {
            string? currentId = manager!.GetCurrentSession()?.SourceAppUserModelId;
            for (int i = 0; i < sessions.Count; i++)
            {
                playing[i] = sessions[i].GetPlaybackInfo().PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                if (currentIndex < 0 && sessions[i].SourceAppUserModelId == currentId)
                {
                    currentIndex = i;
                }
            }
        }
        catch (Exception e)
        {
            // A session that closed stays in the list until SessionsChanged arrives; that event hooks again.
            log?.Invoke($"media: a session went away: {e.Message}");
        }

        int index = MediaSessions.Pick(playing, currentIndex);
        if (index < 0)
        {
            Follow(null);
            Set(NowPlaying.None);
        }
        else if (ReferenceEquals(sessions[index], picked))
        {
            ReadPlayback();
        }
        else
        {
            Follow(sessions[index]);
        }
    }

    /// <summary>Listen to one session's track and timeline and read where it stands. Null follows nothing.</summary>
    private void Follow(Session? session)
    {
        if (picked is { } old)
        {
            Quietly(() =>
            {
                old.MediaPropertiesChanged -= onProperties;
                old.TimelinePropertiesChanged -= onTimeline;
            });
        }

        picked = session;
        if (session is null)
        {
            return;
        }

        try
        {
            string id = session.SourceAppUserModelId;
            if (current.SessionId != id)
            {
                Set(NowPlaying.For(id));
            }

            session.MediaPropertiesChanged += onProperties;
            if (viewers > 0)
            {
                session.TimelinePropertiesChanged += onTimeline;
            }
        }
        catch (Exception e)
        {
            log?.Invoke($"media: session unreadable: {e.Message}");
        }

        // Playback before the timeline: the position is extrapolated with the state it was reported in.
        ReadPlayback();
        if (viewers > 0)
        {
            ReadTimeline();
        }

        ReadTrack();
    }

    /// <summary>The first view came or the last one went: the followed session's position reports are wanted, or not.</summary>
    private void HookTimeline(bool on)
    {
        if (picked is not { } session)
        {
            return;
        }

        Quietly(() =>
        {
            if (on)
            {
                session.TimelinePropertiesChanged += onTimeline;
            }
            else
            {
                session.TimelinePropertiesChanged -= onTimeline;
            }
        });
        if (on)
        {
            ReadTimeline();
        }
    }

    private void ReadPlayback()
    {
        if (picked is not { } session)
        {
            return;
        }

        try
        {
            var info = session.GetPlaybackInfo();
            var controls = info.Controls;
            var commands = (controls.IsPlayEnabled ? MediaCommand.Play : 0)
                | (controls.IsPauseEnabled ? MediaCommand.Pause : 0)
                | (controls.IsPlayPauseToggleEnabled ? MediaCommand.Toggle : 0)
                | (controls.IsNextEnabled ? MediaCommand.Next : 0)
                | (controls.IsPreviousEnabled ? MediaCommand.Previous : 0);
            bool playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
            Set(current.WithPlayback(playing, info.PlaybackRate ?? 1, commands, now()));
        }
        catch (Exception e)
        {
            log?.Invoke($"media: playback unreadable: {e.Message}");
        }
    }

    private void ReadTimeline()
    {
        if (picked is not { } session)
        {
            return;
        }

        try
        {
            var t = session.GetTimelineProperties();
            Set(current.WithTimeline(t.StartTime, t.EndTime, t.Position, t.LastUpdatedTime, now()));
        }
        catch (Exception e)
        {
            log?.Invoke($"media: timeline unreadable: {e.Message}");
        }
    }

    /// <summary>
    /// Read the followed session's track. One read runs at a time; reports that arrive meanwhile (apps send
    /// two or three per track) fold into one more read after it, so the art is decoded once.
    /// </summary>
    private void ReadTrack()
    {
        if (picked is not { } session)
        {
            return;
        }

        if (trackReading)
        {
            trackStale = true;
            return;
        }

        trackReading = true;
        _ = ReadTrackAsync(session, current.SessionId, current.Thumbnail);
    }

    private async Task ReadTrackAsync(Session session, string? id, MediaThumbnail? cached)
    {
        (string Title, string Artist, MediaThumbnail? Art)? track = null;
        try
        {
            var properties = await session.TryGetMediaPropertiesAsync().AsTask().ConfigureAwait(false);
            if (properties is not null)
            {
                track = (properties.Title ?? "", properties.Artist ?? "", await DecodeAsync(properties.Thumbnail, cached).ConfigureAwait(false));
            }
        }
        catch (Exception e)
        {
            log?.Invoke($"media: track unreadable: {e.Message}");
        }

        Post(() =>
        {
            trackReading = false;
            // The same app counts, not the same object: a change in the session list hands out new objects.
            bool followed = picked is not null && current.SessionId == id;
            if (followed && track is { } read)
            {
                Set(current.WithTrack(read.Title, read.Artist) with { Thumbnail = read.Art });
                if (!trackKnown)
                {
                    trackKnown = true;
                    announced = current;
                }
            }

            if (trackStale || !followed)
            {
                trackStale = false;
                ReadTrack();
            }
        });
    }

    /// <summary>
    /// The art for a track report. Reports for one track repeat, and the first one after a track change
    /// can still carry the previous art, so the encoded bytes decide: the same bytes as the cached art
    /// give the cached art back, anything else is decoded once, scaled down. Null when there is none.
    /// </summary>
    private async Task<MediaThumbnail?> DecodeAsync(IRandomAccessStreamReference? reference, MediaThumbnail? cached)
    {
        if (reference is null)
        {
            return null;
        }

        try
        {
            using var stream = await reference.OpenReadAsync();
            if (stream.Size is 0 or > MaxEncodedBytes)
            {
                return null;
            }

            var encoded = new byte[stream.Size];
            await stream.AsStreamForRead().ReadExactlyAsync(encoded).ConfigureAwait(false);
            var hash = new HashCode();
            hash.AddBytes(encoded);
            int stamp = hash.ToHashCode();
            if (cached?.Stamp == stamp)
            {
                return cached;
            }

            stream.Seek(0);
            var decoder = await BitmapDecoder.CreateAsync(stream);
            double shrink = Math.Min(1, (double)ThumbnailSide / Math.Min(decoder.PixelWidth, decoder.PixelHeight));
            uint width = Math.Max(1, (uint)Math.Round(decoder.PixelWidth * shrink));
            uint height = Math.Max(1, (uint)Math.Round(decoder.PixelHeight * shrink));
            var transform = new BitmapTransform { ScaledWidth = width, ScaledHeight = height, InterpolationMode = BitmapInterpolationMode.Fant };
            var pixels = await decoder.GetPixelDataAsync(
                BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage);
            log?.Invoke($"media: art decoded, {decoder.PixelWidth}x{decoder.PixelHeight} kept as {width}x{height}");
            return new MediaThumbnail((int)width, (int)height, pixels.DetachPixelData(), stamp);
        }
        catch (Exception e)
        {
            log?.Invoke($"media: art unreadable: {e.Message}");
            return null;
        }
    }

    private void Set(NowPlaying next)
    {
        if (next == current)
        {
            return;
        }

        current = next;
        NotifyChanged();
        if (trackKnown && next.IsNewTrackAfter(announced))
        {
            announced = next;
            notify(() => TrackChanged?.Invoke(next));
        }
    }

    /// <summary>One notification for however many changes land before the owner's thread gets to it.</summary>
    private void NotifyChanged()
    {
        if (Interlocked.Exchange(ref changePending, 1) == 0)
        {
            notify(raiseChanged);
        }
    }

    /// <summary>Removing a handler from a session whose app has exited throws; there is nothing left to remove.</summary>
    private static void Quietly(Action action)
    {
        try
        {
            action();
        }
        catch (Exception)
        {
        }
    }
}
