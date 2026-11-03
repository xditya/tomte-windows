using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.Audio.Endpoints;
using Windows.Win32.System.Com;

namespace Tomte.Core.Device;

/// <summary>
/// How loud the call is: the call app's own volume in the mixer (its audio session on the default output), so the rest
/// of the PC keeps its level, or, when the app has no session there, the default communications output's volume. Read
/// when made and after each change made here; Simplification: a change made in the mixer meanwhile shows the next time.
/// </summary>
public sealed unsafe class CallSound
{
    private readonly ISimpleAudioVolume? session;
    private readonly IAudioEndpointVolume? device;

    private CallSound(ISimpleAudioVolume? session, IAudioEndpointVolume? device)
    {
        this.session = session;
        this.device = device;
    }

    /// <summary>True when this is the call app's own volume, false for the device's.</summary>
    public bool OfApp => session is not null;

    /// <summary>0 to 1.</summary>
    public float Level
    {
        get
        {
            float level;
            if (session is not null)
            {
                session.GetMasterVolume(out level);
            }
            else
            {
                device!.GetMasterVolumeLevelScalar(out level);
            }

            return level;
        }
    }

    public bool Muted
    {
        get
        {
            BOOL muted;
            if (session is not null)
            {
                session.GetMute(out muted);
            }
            else
            {
                device!.GetMute(out muted);
            }

            return muted;
        }
    }

    /// <summary>The call app's volume, or the output's; null when there is no output device.</summary>
    public static CallSound? Open(CallApp app)
    {
        var enumerator = (IMMDeviceEnumerator)new MMDeviceEnumerator();
        IMMDevice? comms = null;
        var sessions = new List<(IAudioSessionControl Control, uint Pid, string Process, bool Active)>();
        var seen = new HashSet<string>();
        // Call apps play on the communications device unless set otherwise, and on the default one if they are.
        foreach (var role in new[] { ERole.eCommunications, ERole.eConsole })
        {
            try
            {
                enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, role, out var endpoint);
                comms ??= endpoint;
                endpoint.GetId(out var id);
                string key = id.ToString();
                PInvoke.CoTaskMemFree(id);
                if (seen.Add(key))
                {
                    Sessions(endpoint, app, sessions);
                }
            }
            catch (COMException)
            {
                // E_NOTFOUND: no output device for that role.
            }
        }

        if (comms is null)
        {
            return null;
        }

        int pick = app.PickSession([.. sessions.Select(s => (s.Pid, s.Process, s.Active))]);
        if (pick >= 0)
        {
            return new CallSound((ISimpleAudioVolume)sessions[pick].Control, null);
        }

        comms.Activate(typeof(IAudioEndpointVolume).GUID, CLSCTX.CLSCTX_ALL, null, out var activated);
        return new CallSound(null, (IAudioEndpointVolume)activated);
    }

    public void SetLevel(float level)
    {
        level = Math.Clamp(level, 0, 1);
        if (session is not null)
        {
            session.SetMasterVolume(level, null);
        }
        else
        {
            device!.SetMasterVolumeLevelScalar(level, null);
        }
    }

    public void SetMuted(bool muted)
    {
        if (session is not null)
        {
            session.SetMute(muted, null);
        }
        else
        {
            device!.SetMute(muted, null);
        }
    }

    // The device's sessions with their process and state. A process name is looked up only where it can matter: a
    // session of another process, which may be the call app's other half.
    private static void Sessions(IMMDevice endpoint, CallApp app, List<(IAudioSessionControl, uint, string, bool)> into)
    {
        endpoint.Activate(typeof(IAudioSessionManager2).GUID, CLSCTX.CLSCTX_ALL, null, out var activated);
        var list = ((IAudioSessionManager2)activated).GetSessionEnumerator();
        list.GetCount(out int count);
        for (int i = 0; i < count; i++)
        {
            list.GetSession(i, out var control);
            control.GetState(out var state);
            uint pid = 0;
            try
            {
                ((IAudioSessionControl2)control).GetProcessId(out pid);
            }
            catch (COMException)
            {
                // A session shared by several processes.
            }

            into.Add((control, pid, pid == app.Pid ? app.Process : pid == 0 ? "" : Name(pid), state == AudioSessionState.AudioSessionStateActive));
        }
    }

    private static string Name(uint pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById((int)pid);
            return process.ProcessName;
        }
        catch (ArgumentException)
        {
            return "";
        }
    }
}
