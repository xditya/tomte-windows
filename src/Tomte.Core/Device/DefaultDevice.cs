using System.Runtime.InteropServices;
using Windows.Win32.Media.Audio;

namespace Tomte.Core.Device;

/// <summary>
/// Makes an audio endpoint the Windows default. Windows has no public API for this; IPolicyConfig is the undocumented
/// interface the Sound control panel itself uses, and what EarTrumpet and SoundSwitch use too. Being undocumented it is
/// not in the Win32 metadata CsWin32 generates from, and a Windows update could change or drop it, so every failure comes
/// back as an HRESULT for the caller to explain, never as an exception.
/// </summary>
public static class DefaultDevice
{
    /// <summary>
    /// Makes the endpoint the default for every role: the default device (console and multimedia) and the default
    /// communications device, as picking a device in Settings does. 0 when it worked, else the HRESULT that failed.
    /// </summary>
    public static int Set(string deviceId)
    {
        try
        {
            var config = (IPolicyConfig)new PolicyConfigClient();
            foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications })
            {
                int hr = config.SetDefaultEndpoint(deviceId, role);
                if (hr < 0)
                {
                    return hr;
                }
            }

            return 0;
        }
        catch (Exception e) when (e is COMException or InvalidCastException)
        {
            // No such class, or it no longer has this interface.
            return e.HResult;
        }
    }

    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient;

    // Only SetDefaultEndpoint is called; the methods before it are declared to keep the vtable slots in order.
    [ComImport, Guid("F8679F50-850A-41CF-9C72-430F290290C8"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPolicyConfig
    {
        [PreserveSig] int GetMixFormat(string deviceId, nint format);
        [PreserveSig] int GetDeviceFormat(string deviceId, int useDefault, nint format);
        [PreserveSig] int ResetDeviceFormat(string deviceId);
        [PreserveSig] int SetDeviceFormat(string deviceId, nint endpointFormat, nint mixFormat);
        [PreserveSig] int GetProcessingPeriod(string deviceId, int useDefault, nint defaultPeriod, nint minimumPeriod);
        [PreserveSig] int SetProcessingPeriod(string deviceId, nint period);
        [PreserveSig] int GetShareMode(string deviceId, nint mode);
        [PreserveSig] int SetShareMode(string deviceId, nint mode);
        [PreserveSig] int GetPropertyValue(string deviceId, nint key, nint value);
        [PreserveSig] int SetPropertyValue(string deviceId, nint key, nint value);
        [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string deviceId, ERole role);
    }
}
