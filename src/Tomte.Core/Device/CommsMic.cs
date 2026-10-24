using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.Audio.Endpoints;
using Windows.Win32.System.Com;

namespace Tomte.Core.Device;

/// <summary>
/// The mute of the default communications microphone, the one call apps record from. Muting it mutes the device itself,
/// for every app that records from it, which is what a hardware mute key does; the call app may not show it as muted.
/// <see cref="MuteChanged"/> follows the mute whoever changes it: this, the call app, Windows' settings or a key.
/// </summary>
public sealed unsafe class CommsMic : IAudioEndpointVolumeCallback, IDisposable
{
    private readonly IAudioEndpointVolume volume;
    private bool disposed;

    private CommsMic(IAudioEndpointVolume volume)
    {
        this.volume = volume;
        volume.RegisterControlChangeNotify(this);
    }

    /// <summary>The mute changed, to the value given. Raised on an audio thread.</summary>
    public event Action<bool>? MuteChanged;

    public bool Muted
    {
        get
        {
            volume.GetMute(out BOOL muted);
            return muted;
        }
    }

    /// <summary>The default communications microphone, or null when there is none.</summary>
    public static CommsMic? Open()
    {
        try
        {
            var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eCommunications, out var device);
            device.Activate(typeof(IAudioEndpointVolume).GUID, CLSCTX.CLSCTX_ALL, null, out var activated);
            return new CommsMic((IAudioEndpointVolume)activated);
        }
        catch (COMException)
        {
            return null; // E_NOTFOUND: no capture device
        }
    }

    public void SetMuted(bool muted) => volume.SetMute(muted, null);

    void IAudioEndpointVolumeCallback.OnNotify(AUDIO_VOLUME_NOTIFICATION_DATA* data) => MuteChanged?.Invoke(data->bMuted);

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        MuteChanged = null;
        try
        {
            volume.UnregisterControlChangeNotify(this);
        }
        catch (COMException)
        {
            // The device went away; there is nothing left to unregister from.
        }
    }
}
