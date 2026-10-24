using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell.PropertiesSystem;

namespace Tomte.Core.Device;

/// <summary>
/// Whether another app is recording from the default communications microphone, the one <see cref="CommsMic"/> mutes,
/// from Core Audio's sessions on that device: an app recording has an active session there. Event driven, with nothing
/// running between changes: the session manager says when a session starts, each session when it becomes active,
/// inactive or goes, and the device enumerator when another microphone becomes the default.
/// <see cref="Changed"/> is raised on an audio thread.
/// </summary>
public sealed class MicUseWatch : IAudioSessionNotification, IMMNotificationClient, IDisposable
{
    private readonly Lock gate = new();
    private readonly IMMDeviceEnumerator enumerator;
    private readonly List<Session> sessions = [];
    private readonly uint ownPid = (uint)Environment.ProcessId;
    private IAudioSessionManager2? manager;
    private bool capturing, disposed;

    private MicUseWatch()
    {
        enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        lock (gate)
        {
            Attach();
            capturing = OthersCapture(sessions.Select(s => (s.Pid, s.Active)), ownPid);
        }

        enumerator.RegisterEndpointNotificationCallback(this);
    }

    /// <summary>Another app was recording at the last change.</summary>
    public bool Capturing
    {
        get
        {
            lock (gate)
            {
                return capturing;
            }
        }
    }

    /// <summary>Another app started or stopped recording from the microphone; true while one does. Raised on an audio thread.</summary>
    public event Action<bool>? Changed;

    /// <summary>
    /// Made on a pool thread, so the audio objects and their callbacks live in the multithreaded apartment rather than
    /// the island's, whose thread they would otherwise have to wait for.
    /// </summary>
    public static MicUseWatch Start() => Task.Run(() => new MicUseWatch()).GetAwaiter().GetResult();

    /// <summary>
    /// Whether an app other than Tomte records now: one of its sessions is active. Tomte's own recording (the rambler)
    /// does not count, nor a session with no single process behind it (0).
    /// </summary>
    public static bool OthersCapture(IEnumerable<(uint Pid, bool Active)> sessions, uint ownPid) =>
        sessions.Any(s => s.Active && s.Pid != 0 && s.Pid != ownPid);

    public void Dispose()
    {
        lock (gate)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            Detach();
        }

        Changed = null;
        enumerator.UnregisterEndpointNotificationCallback(this);
    }

    void IAudioSessionNotification.OnSessionCreated(IAudioSessionControl session)
    {
        lock (gate)
        {
            if (!disposed)
            {
                Track(session);
            }
        }

        Raise();
    }

    void IMMNotificationClient.OnDefaultDeviceChanged(EDataFlow flow, ERole role, PCWSTR id)
    {
        if (flow == EDataFlow.eCapture && role == ERole.eCommunications)
        {
            // Not in the callback itself, where registering for another device's sessions can deadlock.
            Task.Run(() =>
            {
                lock (gate)
                {
                    if (!disposed)
                    {
                        Attach();
                    }
                }

                Raise();
            });
        }
    }

    void IMMNotificationClient.OnDeviceStateChanged(PCWSTR id, DEVICE_STATE state)
    {
    }

    void IMMNotificationClient.OnDeviceAdded(PCWSTR id)
    {
    }

    void IMMNotificationClient.OnDeviceRemoved(PCWSTR id)
    {
    }

    void IMMNotificationClient.OnPropertyValueChanged(PCWSTR id, PROPERTYKEY key)
    {
    }

    /// <summary>The sessions on the default communications microphone, followed from now on; none when there is no microphone.</summary>
    private unsafe void Attach()
    {
        Detach();
        try
        {
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eCapture, ERole.eCommunications, out var device);
            device.Activate(typeof(IAudioSessionManager2).GUID, CLSCTX.CLSCTX_ALL, null, out var activated);
            manager = (IAudioSessionManager2)activated;
            manager.RegisterSessionNotification(this);
            // New sessions are announced only once the ones there are have been listed.
            var list = manager.GetSessionEnumerator();
            list.GetCount(out int count);
            for (int i = 0; i < count; i++)
            {
                list.GetSession(i, out var session);
                Track(session);
            }
        }
        catch (COMException)
        {
            manager = null; // E_NOTFOUND: no capture device until the default changes
        }
    }

    private void Detach()
    {
        foreach (var s in sessions)
        {
            s.Stop();
        }

        sessions.Clear();
        try
        {
            manager?.UnregisterSessionNotification(this);
        }
        catch (COMException)
        {
            // The device went away with its sessions.
        }

        manager = null;
    }

    private void Track(IAudioSessionControl control)
    {
        var session = new Session(this, control);
        if (session.Pid != ownPid)
        {
            sessions.Add(session);
        }
        else
        {
            session.Stop();
        }
    }

    private void Raise()
    {
        bool now;
        lock (gate)
        {
            now = OthersCapture(sessions.Select(s => (s.Pid, s.Active)), ownPid);
            if (disposed || now == capturing)
            {
                return;
            }

            capturing = now;
        }

        Changed?.Invoke(now);
    }

    /// <summary>One app's session on the microphone: its process and whether it records now, followed through its events.</summary>
    private sealed unsafe class Session : IAudioSessionEvents
    {
        private readonly MicUseWatch owner;
        private readonly IAudioSessionControl control;
        private bool listening;

        public Session(MicUseWatch owner, IAudioSessionControl control)
        {
            this.owner = owner;
            this.control = control;
            control.GetState(out var state);
            Active = state == AudioSessionState.AudioSessionStateActive;
            try
            {
                ((IAudioSessionControl2)control).GetProcessId(out uint pid);
                Pid = pid;
            }
            catch (COMException)
            {
                Pid = 0; // a session shared by several processes
            }

            control.RegisterAudioSessionNotification(this);
            listening = true;
        }

        public uint Pid { get; }

        public bool Active { get; private set; }

        public void Stop()
        {
            if (listening)
            {
                listening = false;
                try
                {
                    control.UnregisterAudioSessionNotification(this);
                }
                catch (COMException)
                {
                    // The session is gone.
                }
            }
        }

        void IAudioSessionEvents.OnStateChanged(AudioSessionState state)
        {
            lock (owner.gate)
            {
                Active = state == AudioSessionState.AudioSessionStateActive;
                if (state == AudioSessionState.AudioSessionStateExpired && owner.sessions.Remove(this))
                {
                    // Not from inside its own callback, which can deadlock.
                    Task.Run(Stop);
                }
            }

            owner.Raise();
        }

        void IAudioSessionEvents.OnSessionDisconnected(AudioSessionDisconnectReason reason) => ((IAudioSessionEvents)this).OnStateChanged(AudioSessionState.AudioSessionStateExpired);

        void IAudioSessionEvents.OnDisplayNameChanged(PCWSTR name, Guid* context)
        {
        }

        void IAudioSessionEvents.OnIconPathChanged(PCWSTR path, Guid* context)
        {
        }

        void IAudioSessionEvents.OnSimpleVolumeChanged(float level, BOOL muted, Guid* context)
        {
        }

        void IAudioSessionEvents.OnChannelVolumeChanged(uint count, float[] levels, uint channel, Guid* context)
        {
        }

        void IAudioSessionEvents.OnGroupingParamChanged(Guid* grouping, Guid* context)
        {
        }
    }
}
