using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Media.SpeechSynthesis;

namespace Tomte.Shell;

/// <summary>
/// Windows speech for the reply to a voice command, used only when the user turned spoken replies on. Nothing of
/// it loads before the first reply is read. One player at a time, dropped on the calling thread when it finishes.
/// </summary>
internal static class SpokenReply
{
    private static MediaPlayer? playing;

    public static async Task SpeakAsync(string text, Action<string> log)
    {
        try
        {
            using var synthesizer = new SpeechSynthesizer();
            var stream = await synthesizer.SynthesizeTextToStreamAsync(text);
            playing?.Dispose();
            var player = playing = new MediaPlayer { Source = MediaSource.CreateFromStream(stream, stream.ContentType) };
            var context = SynchronizationContext.Current;
            player.MediaEnded += (_, _) => context?.Post(_ =>
            {
                if (playing == player)
                {
                    player.Dispose();
                    playing = null;
                }
            }, null);
            player.Play();
            log($"{DateTime.Now:HH:mm:ss.fff} voice: reply spoken ({text.Length} chars)");
        }
        catch (Exception e)
        {
            // No voice installed, or no audio device: the reply was the extra, the command already ran.
            log($"{DateTime.Now:HH:mm:ss.fff} voice: reply not spoken: {e.Message}");
        }
    }
}
