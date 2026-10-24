using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Media.MediaFoundation;

namespace Tomte.Core.Device;

/// <summary>
/// Which processes stream from a camera, from Media Foundation's sensor activity monitor: the source of the camera
/// privacy indicator, which covers desktop apps as well as packaged ones (the camera's consent store entries are only
/// written when an app stops, and late for some desktop apps). Event driven: the monitor reports on its own thread
/// whenever a camera starts or stops streaming to a process, and once when started. <see cref="Changed"/> is raised there.
/// </summary>
public sealed class CameraWatch : IMFSensorActivitiesReportCallback, IDisposable
{
    private readonly IMFSensorActivityMonitor monitor;
    private bool disposed;

    private CameraWatch()
    {
        // The light start: the monitor needs Media Foundation running, but none of its sockets.
        PInvoke.MFStartup(PInvoke.MF_VERSION, 1).ThrowOnFailure();
        try
        {
            PInvoke.MFCreateSensorActivityMonitor(this, out monitor).ThrowOnFailure();
            monitor.Start();
        }
        catch
        {
            PInvoke.MFShutdown();
            throw;
        }
    }

    /// <summary>The processes streaming now, by id and name, at each report. Raised on the monitor's thread.</summary>
    public event Action<IReadOnlyList<(uint Pid, string Process)>>? Changed;

    /// <summary>Started on a pool thread, so the monitor lives in the multithreaded apartment; null where Windows has no monitor.</summary>
    public static CameraWatch? Start() => Task.Run(() =>
    {
        try
        {
            return new CameraWatch();
        }
        catch (Exception e) when (e is COMException or EntryPointNotFoundException or DllNotFoundException)
        {
            return null;
        }
    }).GetAwaiter().GetResult();

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Changed = null;
        try
        {
            monitor.Stop();
        }
        catch (COMException)
        {
            // Already stopped.
        }

        PInvoke.MFShutdown();
    }

    void IMFSensorActivitiesReportCallback.OnActivitiesReport(IMFSensorActivitiesReport report)
    {
        var streaming = new List<(uint, string)>();
        try
        {
            report.GetCount(out uint sensors);
            for (uint i = 0; i < sensors; i++)
            {
                report.GetActivityReport(i, out var sensor);
                sensor.GetProcessCount(out uint processes);
                for (uint j = 0; j < processes; j++)
                {
                    sensor.GetProcessActivity(j, out var activity);
                    activity.GetStreamingState(out BOOL on);
                    activity.GetProcessId(out uint pid);
                    if (on && !streaming.Exists(s => s.Item1 == pid))
                    {
                        streaming.Add((pid, Name(pid)));
                    }
                }
            }
        }
        catch (COMException)
        {
            return; // a report that could not be read; the next one will be
        }

        Changed?.Invoke(streaming);
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
