using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace Tomte.AI.Palette;

/// <summary>
/// Windows speech for the reply to a voice command, used only when the user turned spoken replies on. Nothing of
/// it loads before the first reply is read. One reply at a time: its player, source and synthesis stream are
/// dropped on the calling thread when playback ends or fails, or when the next reply starts.
/// </summary>
public static class SpokenReply
{
    private static readonly Lock Gate = new();
    private static (MediaPlayer Player, MediaSource Source, IDisposable Stream)? playing;

    /// <summary>A reply is playing or waiting to be dropped.</summary>
    internal static bool Playing
    {
        get
        {
            lock (Gate)
                return playing is not null;
        }
    }

    public static async Task SpeakAsync(string text, Action<string> log)
    {
        try
        {
            using var synthesizer = new SpeechSynthesizer();
            var stream = await synthesizer.SynthesizeTextToStreamAsync(text);
            Play(MediaSource.CreateFromStream(stream, stream.ContentType), stream);
            log($"{DateTime.Now:HH:mm:ss.fff} voice: reply spoken ({text.Length} chars)");
        }
        catch (Exception e)
        {
            // No voice installed, or no audio device: the reply was the extra, the command already ran.
            log($"{DateTime.Now:HH:mm:ss.fff} voice: reply not spoken: {e.Message}");
        }
    }

    internal static void Play(MediaSource source, IDisposable stream)
    {
        var player = new MediaPlayer();
        lock (Gate)
        {
            Drop(playing);
            playing = (player, source, stream);
        }

        // A source that cannot play raises MediaFailed and never MediaEnded; either one lets the pipeline go.
        var context = SynchronizationContext.Current ?? new SynchronizationContext();
        void Release() => context.Post(_ =>
        {
            lock (Gate)
            {
                if (playing?.Player == player)
                {
                    Drop(playing);
                    playing = null;
                }
            }
        }, null);
        player.MediaEnded += (_, _) => Release();
        player.MediaFailed += (_, _) => Release();
        player.Source = source;
        player.Play();
    }

    private static void Drop((MediaPlayer Player, MediaSource Source, IDisposable Stream)? reply)
    {
        if (reply is not { } r)
            return;
        r.Player.Dispose();
        r.Source.Dispose();
        r.Stream.Dispose();
    }
}
