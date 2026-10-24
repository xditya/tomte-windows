using Windows.Win32;
using Windows.Win32.Media.Audio;

namespace Tomte.Shell;

/// <summary>
/// The sound of a timer ending or a reminder ringing: Windows' own Alarm01, played once (about 6 s) without waiting for
/// it. Where the file is gone, Windows plays its default sound instead.
/// </summary>
internal static class Alarm
{
    private static readonly string Sound = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Media", "Alarm01.wav");

    public static void Play() => PInvoke.PlaySound(Sound, default, SND_FLAGS.SND_FILENAME | SND_FLAGS.SND_ASYNC);
}
