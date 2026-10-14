using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Core.Jobs;

/// <summary>
/// The in-app runner's clock and mailbox: a window that is never shown, on the thread that creates it (the island's).
/// It holds one SetTimer one-shot for the next due job, worked out again from the wall clock after a resume from sleep
/// and after a clock change, so a run that came due meanwhile starts at once and nothing ever ticks. A --run-job start
/// finds it by its title, which names the data folder, and hands the run over instead of starting a second Tomte.
/// A top-level window rather than a message-only one, because only top-level windows hear the clock and power
/// broadcasts.
/// </summary>
public sealed unsafe class JobWindow : IDisposable
{
    public const string ClassName = "TomteJobs";

    private const uint MsgRunJob = PInvoke.WM_APP + 0x4A;
    private const nuint DueTimer = 1;

    // Windows on any thread; tests make several at once.
    private static readonly ConcurrentDictionary<HWND, JobWindow> Open = new();
    private static readonly Lock Registering = new();
    private static bool registered;

    // Kept for the life of the process: Windows calls it for every window of the class.
    private static readonly WNDPROC Proc = HandleMessage;

    private readonly Func<DateTimeOffset> now;
    private DateTimeOffset? next;

    /// <param name="dataFolder">The folder holding tomte.db; a hand-off reaches only the Tomte using the same one.</param>
    /// <param name="now">The wall clock; tests pass their own.</param>
    public JobWindow(string dataFolder, Func<DateTimeOffset>? now = null)
    {
        this.now = now ?? (() => DateTimeOffset.Now);
        // The exe's module, for the class and the window alike: Windows finds an application class by name and module.
        var instance = new HINSTANCE(PInvoke.GetModuleHandle(default(PCWSTR)).Value);
        fixed (char* className = ClassName)
        fixed (char* title = Title(dataFolder))
        {
            lock (Registering)
            {
                if (!registered)
                {
                    var windowClass = new WNDCLASSEXW
                    {
                        cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                        lpfnWndProc = Proc,
                        hInstance = instance,
                        lpszClassName = className,
                    };
                    if (PInvoke.RegisterClassEx(in windowClass) == 0)
                    {
                        throw new Win32Exception();
                    }

                    registered = true;
                }
            }

            // Never shown: no taskbar button, no Alt+Tab entry, nothing drawn.
            Hwnd = PInvoke.CreateWindowEx(WINDOW_EX_STYLE.WS_EX_TOOLWINDOW | WINDOW_EX_STYLE.WS_EX_NOACTIVATE, className, title,
                WINDOW_STYLE.WS_POPUP, 0, 0, 0, 0, HWND.Null, HMENU.Null, instance, null);
        }

        if (Hwnd.IsNull)
        {
            throw new Win32Exception();
        }

        Open[Hwnd] = this;
    }

    internal HWND Hwnd { get; }

    /// <summary>
    /// Something is due: the one-shot reached the next run (null), or a --run-job start handed over its job id.
    /// Raised on the window's thread. The handler reads the database for what is due and calls <see cref="Arm"/> again.
    /// </summary>
    public event Action<long?>? Due;

    /// <summary>The time the one-shot is armed for, or null when nothing is.</summary>
    public DateTimeOffset? Next => next;

    public static string Title(string dataFolder) => "Tomte jobs " + JobTask.Key(dataFolder);

    /// <summary>
    /// Hands a --run-job start to the Tomte already running on <paramref name="dataFolder"/>. False when none is
    /// running there (or none with jobs); the caller then starts Tomte itself.
    /// </summary>
    public static bool HandOff(string dataFolder, long jobId)
    {
        var target = PInvoke.FindWindow(ClassName, Title(dataFolder));
        return !target.IsNull && PInvoke.PostMessage(target, MsgRunJob, (nuint)jobId, 0);
    }

    /// <summary>Arms the one-shot for <paramref name="due"/>, or disarms it. A time already past fires at once.</summary>
    public void Arm(DateTimeOffset? due)
    {
        next = due;
        Rearm();
    }

    public void Dispose()
    {
        Open.TryRemove(Hwnd, out _);
        PInvoke.DestroyWindow(Hwnd);
    }

    /// <summary>
    /// After a clock change or a resume from sleep (the window calls this for both broadcasts): the one-shot is an
    /// interval, which neither follows a clock that was set nor counts time across a sleep, so the wait is worked out
    /// again from the wall clock. A run that came due meanwhile fires at once.
    /// </summary>
    public void ClockChanged() => Rearm();

    // SetTimer waits at most about 24.8 days; an early fire comes back here for what is left.
    private void Rearm()
    {
        PInvoke.KillTimer(Hwnd, DueTimer);
        if (next is { } due)
        {
            double ms = Math.Ceiling((due - now()).TotalMilliseconds);
            PInvoke.SetTimer(Hwnd, DueTimer, (uint)Math.Clamp(ms, 0, int.MaxValue), null);
        }
    }

    private void OnTimer()
    {
        PInvoke.KillTimer(Hwnd, DueTimer);
        if (next is { } due && now() >= due)
        {
            next = null;
            Due?.Invoke(null);
        }
        else
        {
            Rearm();
        }
    }

    private static LRESULT HandleMessage(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (Open.TryGetValue(window, out var self))
            {
                switch (message)
                {
                    case PInvoke.WM_TIMER when wParam.Value == DueTimer:
                        self.OnTimer();
                        return new LRESULT(0);
                    case PInvoke.WM_TIMECHANGE:
                    case PInvoke.WM_POWERBROADCAST when wParam.Value == PInvoke.PBT_APMRESUMEAUTOMATIC:
                        self.ClockChanged();
                        break;
                    case MsgRunJob:
                        self.Due?.Invoke((long)wParam.Value);
                        return new LRESULT(0);
                }
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // An exception must not cross back into Windows. The handlers log their own; the next fire, resume or
            // hand-off tries again.
        }

        return PInvoke.DefWindowProc(window, message, wParam, lParam);
    }
}
