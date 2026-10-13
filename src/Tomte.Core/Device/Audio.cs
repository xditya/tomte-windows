using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Media.Audio;
using Windows.Win32.Media.Audio.Endpoints;
using Windows.Win32.System.Com;

namespace Tomte.Core.Device;

public sealed record OutputDevice(string Id, string Name, bool IsDefault);

// Core Audio on the default endpoints. Each call creates its own enumerator, so nothing is
// held between calls.
// the COM wrappers are released by the GC, not Marshal.FinalReleaseComObject; a
// handful per user action. Release them explicitly if an action ever runs in a loop.
public static class Audio
{
    public static ActionResult SetVolume(int percent, bool dryRun = false)
    {
        if (percent is < 0 or > 100) return ActionResult.Fail("Volume must be between 0 and 100.");
        if (dryRun) return ActionResult.DryRun($"set volume to {percent}%.");

        var volume = EndpointVolume(EDataFlow.eRender);
        volume.GetMasterVolumeLevelScalar(out var previous);
        SetLevel(volume, percent / 100f);
        return ActionResult.Done($"Volume {percent}%.", () =>
        {
            SetLevel(EndpointVolume(EDataFlow.eRender), previous);
            return ActionResult.Done($"Volume back to {Math.Round(previous * 100)}%.");
        });
    }

    public static ActionResult SetMute(bool muted, bool dryRun = false) =>
        Mute(EDataFlow.eRender, "Sound", muted, dryRun);

    public static ActionResult SetMicMute(bool muted, bool dryRun = false) =>
        Mute(EDataFlow.eCapture, "Microphone", muted, dryRun);

    public static ActionResult ListOutputDevices()
    {
        var devices = OutputDevices();
        return devices.Count == 0
            ? ActionResult.Fail("No active output devices.")
            : ActionResult.Done(string.Join("\n", devices.Select(d => d.IsDefault ? d.Name + " (default)" : d.Name)));
    }

    // Name may be the full device name or a unique part of it ("headphones").
    public static ActionResult SetOutputDevice(string name, bool dryRun = false)
    {
        if (string.IsNullOrWhiteSpace(name)) return ActionResult.Fail("Name an output device.");
        if (dryRun) return ActionResult.DryRun($"switch output to the device matching \"{name}\".");
        var devices = OutputDevices();
        var (device, error) = PickDevice(devices, name);
        if (device is null) return ActionResult.Fail(error!);
        if (device.IsDefault) return ActionResult.Done($"{device.Name} is already the output device.");

        var previous = devices.FirstOrDefault(d => d.IsDefault);
        SetDefault(device.Id);
        return ActionResult.Done($"Output: {device.Name}.", previous is null ? null : () =>
        {
            SetDefault(previous.Id);
            return ActionResult.Done($"Output back to {previous.Name}.");
        });
    }

    // Exact name (ignoring case) wins; otherwise the query must match exactly one name.
    public static (OutputDevice? Device, string? Error) PickDevice(IReadOnlyList<OutputDevice> devices, string query)
    {
        var exact = devices.FirstOrDefault(d => string.Equals(d.Name, query, StringComparison.OrdinalIgnoreCase));
        if (exact is not null) return (exact, null);
        var partial = devices.Where(d => d.Name.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
        return partial.Count switch
        {
            1 => (partial[0], null),
            0 => (null, $"No output device matches \"{query}\"."),
            _ => (null, $"\"{query}\" matches {string.Join(", ", partial.Select(d => d.Name))}. Which one?"),
        };
    }

    private static ActionResult Mute(EDataFlow flow, string name, bool muted, bool dryRun)
    {
        var verb = muted ? "mute" : "unmute";
        if (dryRun) return ActionResult.DryRun($"{verb} {name.ToLowerInvariant()}.");

        var volume = EndpointVolume(flow);
        volume.GetMute(out var previous);
        SetMuted(volume, muted);
        return ActionResult.Done($"{name} {verb}d.", () =>
        {
            SetMuted(EndpointVolume(flow), previous);
            return ActionResult.Done($"{name} {(previous ? "muted" : "unmuted")} again.");
        });
    }

    private static IMMDeviceEnumerator Enumerator() => (IMMDeviceEnumerator)new MMDeviceEnumerator();

    private static unsafe IAudioEndpointVolume EndpointVolume(EDataFlow flow)
    {
        Enumerator().GetDefaultAudioEndpoint(flow, ERole.eMultimedia, out var device);
        device.Activate(typeof(IAudioEndpointVolume).GUID, CLSCTX.CLSCTX_ALL, null, out var volume);
        return (IAudioEndpointVolume)volume;
    }

    private static unsafe void SetLevel(IAudioEndpointVolume volume, float level) =>
        volume.SetMasterVolumeLevelScalar(level, null);

    private static unsafe void SetMuted(IAudioEndpointVolume volume, bool muted) =>
        volume.SetMute(muted, null);

    private static unsafe List<OutputDevice> OutputDevices()
    {
        var enumerator = Enumerator();
        string? defaultId = null;
        try
        {
            enumerator.GetDefaultAudioEndpoint(EDataFlow.eRender, ERole.eMultimedia, out var current);
            current.GetId(out var id);
            defaultId = Take(id);
        }
        catch (COMException)
        {
            // No default device: every device is listed as not default.
        }

        enumerator.EnumAudioEndpoints(EDataFlow.eRender, DEVICE_STATE.DEVICE_STATE_ACTIVE, out var collection);
        collection.GetCount(out var count);
        var devices = new List<OutputDevice>((int)count);
        for (uint i = 0; i < count; i++)
        {
            collection.Item(i, out var device);
            device.GetId(out var id);
            var deviceId = Take(id);
            device.OpenPropertyStore(STGM.STGM_READ, out var store);
            store.GetValue(PInvoke.PKEY_Device_FriendlyName, out var value);
            PInvoke.PropVariantToStringAlloc(value, out var name);
            PInvoke.PropVariantClear(ref value);
            devices.Add(new OutputDevice(deviceId, Take(name), deviceId == defaultId));
        }
        return devices;
    }

    private static unsafe string Take(Windows.Win32.Foundation.PWSTR text)
    {
        var s = text.ToString();
        PInvoke.CoTaskMemFree(text);
        return s;
    }

    private static void SetDefault(string deviceId)
    {
        var config = (IPolicyConfig)new PolicyConfigClient();
        foreach (var role in new[] { ERole.eConsole, ERole.eMultimedia, ERole.eCommunications })
            Marshal.ThrowExceptionForHR(config.SetDefaultEndpoint(deviceId, role));
    }

    // IPolicyConfig is undocumented, so it is not in the Win32 metadata CsWin32 generates from.
    // It is the interface the Sound control panel uses to change the default device (also used by
    // EarTrumpet and SoundSwitch). Only SetDefaultEndpoint is called; the earlier methods are
    // declared only to keep the vtable slots in order.
    [ComImport, Guid("870AF99C-171D-4F9E-AF0D-E63DF40C2BC9")]
    private class PolicyConfigClient;

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
