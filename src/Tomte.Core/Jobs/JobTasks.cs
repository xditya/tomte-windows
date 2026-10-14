using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.TaskScheduler;

namespace Tomte.Core.Jobs;

/// <summary>The Task Scheduler entries that start Tomte for a job while it is closed. Calls block; run them on a worker.</summary>
public interface IJobTasks
{
    /// <summary>Creates or replaces the job's entry.</summary>
    void Register(ScheduledJob job);

    /// <summary>Removes the job's entry; an entry that is not there is not an error.</summary>
    void Remove(long jobId);
}

/// <summary>
/// No entries: a packaged Tomte, until it is known how a task starts the packaged app with its identity and its data
/// folder. Jobs then run only while Tomte runs, and the step that made the job says so.
/// </summary>
public sealed class NoJobTasks : IJobTasks
{
    public void Register(ScheduledJob job) =>
        throw new NotSupportedException("a packaged Tomte makes no Task Scheduler entries yet");

    public void Remove(long jobId)
    {
    }

    /// <summary>The real entries, or none in a packaged process.</summary>
    public static IJobTasks For(string exe, string settingsPath) =>
        Packaged ? new NoJobTasks() : new WindowsTaskScheduler(exe, settingsPath);

    /// <summary>True in a process with package identity: GetCurrentPackageFullName answers APPMODEL_ERROR_NO_PACKAGE (15700) without one.</summary>
    public static unsafe bool Packaged
    {
        get
        {
            uint length = 0;
            return PInvoke.GetCurrentPackageFullName(ref length, null) != WIN32_ERROR.APPMODEL_ERROR_NO_PACKAGE;
        }
    }
}

/// <summary>
/// One job's entry, worked out without touching Task Scheduler: its name, the command line and the task XML. The
/// entry starts Tomte with --run-job (id), and with the settings file when it is not the default one, so the run
/// opens the same folder and the same tomte.db as the app that made the job.
/// </summary>
public static class JobTask
{
    public const string RunJobSwitch = "--run-job";

    /// <summary>What a --run-job start posts to the running Tomte's island window, with the job id in wParam.</summary>
    public const uint MsgRunJob = PInvoke.WM_APP + 0x4A;

    private static readonly XNamespace Ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";

    /// <summary>The job id of a --run-job start, or null.</summary>
    public static long? RunJobArg(IReadOnlyList<string> args)
    {
        int i = args.ToList().IndexOf(RunJobSwitch);
        return i >= 0 && i + 1 < args.Count && long.TryParse(args[i + 1], NumberStyles.None, CultureInfo.InvariantCulture, out long id) ? id : null;
    }

    /// <summary>
    /// Eight hex digits of the data folder, so two Tomte folders on one account (a test run beside the real one) never
    /// share a task name or a hand-off window.
    /// </summary>
    public static string Key(string dataFolder) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(dataFolder).TrimEnd('\\').ToLowerInvariant())))[..8].ToLowerInvariant();

    public static string Name(long jobId, string dataFolder) => $"Tomte job {jobId} {Key(dataFolder)}";

    // A window property, not a title: scripts that look for the island window by class with no title would stop
    // finding it once it had one.
    private const string FolderProperty = "Tomte.DataFolder";

    /// <summary>Marks a window as the Tomte on <paramref name="dataFolder"/>, so a --run-job start there finds it and no other.</summary>
    public static unsafe void Tag(nint window, string dataFolder)
    {
        fixed (char* name = FolderProperty)
        {
            PInvoke.SetProp(new HWND(window), name, new HANDLE(FolderValue(dataFolder)));
        }
    }

    /// <summary>
    /// Hands a --run-job start to the Tomte already running on <paramref name="dataFolder"/>: posts the id to the
    /// top-level window tagged with that folder. False when none is running there; the caller then runs the job itself.
    /// </summary>
    public static unsafe bool HandOff(string dataFolder, long jobId)
    {
        nint tag = FolderValue(dataFolder);
        fixed (char* name = FolderProperty)
        {
            for (var window = PInvoke.FindWindowEx(HWND.Null, HWND.Null, null, null); !window.IsNull; window = PInvoke.FindWindowEx(HWND.Null, window, null, null))
            {
                if ((nint)PInvoke.GetProp(window, name).Value == tag)
                {
                    return PInvoke.PostMessage(window, MsgRunJob, (nuint)jobId, 0);
                }
            }
        }

        return false;
    }

    // The key as a number, with the top bit set so it is never 0, which GetProp answers for a window without the property.
    private static nint FolderValue(string dataFolder) =>
        (nint)(uint.Parse(Key(dataFolder), NumberStyles.HexNumber, CultureInfo.InvariantCulture) | 0x80000000);

    public static string Arguments(long jobId, string settingsPath) =>
        string.Equals(Path.GetFullPath(settingsPath), Path.GetFullPath(SettingsStore.DefaultPath), StringComparison.OrdinalIgnoreCase)
            ? $"{RunJobSwitch} {jobId}"
            : $"--debug-settings \"{Path.GetFullPath(settingsPath)}\" {RunJobSwitch} {jobId}";

    /// <summary>
    /// The task: a calendar trigger in local time from the job's next run (Task Scheduler follows daylight saving the
    /// same way), for the signed-in user only, no elevation, never waking the PC, started late when the time was missed.
    /// <see cref="JobStore.RunLimit"/> at most: the start hands the job to a running Tomte or runs it with no island and
    /// exits, and a run times out after two minutes. Normal priority instead of Task Scheduler's below-normal default.
    /// </summary>
    public static string Xml(ScheduledJob job, string exe, string arguments)
    {
        var first = job.NextDue ?? throw new ArgumentException("A job with no next run has no task.", nameof(job));
        var local = job.Schedule.Date is { } date ? date.ToDateTime(job.Schedule.Time) : DateOnly.FromDateTime(first.LocalDateTime).ToDateTime(job.Schedule.Time);
        var start = new XElement(Ns + "StartBoundary", local.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture));
        XElement trigger = job.Schedule.Date is null
            ? new XElement(Ns + "CalendarTrigger", start,
                new XElement(Ns + "ScheduleByWeek",
                    new XElement(Ns + "DaysOfWeek", job.Schedule.Days.Select(d => new XElement(Ns + d.ToString()))),
                    new XElement(Ns + "WeeksInterval", 1)))
            : new XElement(Ns + "TimeTrigger", start);
        var task = new XElement(Ns + "Task", new XAttribute("version", "1.2"),
            new XElement(Ns + "RegistrationInfo",
                new XElement(Ns + "Author", "Tomte"),
                new XElement(Ns + "Description", $"Starts Tomte for the scheduled job \"{job.Name}\". Remove the job in Tomte to remove this task.")),
            new XElement(Ns + "Triggers", trigger),
            new XElement(Ns + "Principals",
                new XElement(Ns + "Principal", new XAttribute("id", "Author"),
                    new XElement(Ns + "LogonType", "InteractiveToken"),
                    new XElement(Ns + "RunLevel", "LeastPrivilege"))),
            new XElement(Ns + "Settings",
                new XElement(Ns + "MultipleInstancesPolicy", "IgnoreNew"),
                new XElement(Ns + "DisallowStartIfOnBatteries", false),
                new XElement(Ns + "StopIfGoingOnBatteries", false),
                new XElement(Ns + "StartWhenAvailable", true),
                new XElement(Ns + "WakeToRun", false),
                new XElement(Ns + "ExecutionTimeLimit", XmlConvert.ToString(JobStore.RunLimit)),
                new XElement(Ns + "Priority", 4)),
            new XElement(Ns + "Actions", new XAttribute("Context", "Author"),
                new XElement(Ns + "Exec",
                    new XElement(Ns + "Command", exe),
                    new XElement(Ns + "Arguments", arguments))));
        return task.ToString();
    }
}

/// <summary>
/// The real entries, through the Task Scheduler COM API, in the root folder of the signed-in user's tasks. Creating a
/// task for oneself with an interactive token needs no administrator and shows no UAC prompt.
/// </summary>
public sealed class WindowsTaskScheduler(string exe, string settingsPath) : IJobTasks
{
    private string DataFolder => Path.GetDirectoryName(Path.GetFullPath(settingsPath))!;

    public void Register(ScheduledJob job) => WithRoot(root =>
    {
        using var name = new Bstr(JobTask.Name(job.Id, DataFolder));
        using var xml = new Bstr(JobTask.Xml(job, exe, JobTask.Arguments(job.Id, settingsPath)));
        root.RegisterTask(name, xml, (int)TASK_CREATION.TASK_CREATE_OR_UPDATE, null!, null!, TASK_LOGON_TYPE.TASK_LOGON_INTERACTIVE_TOKEN, null!, out var task);
        Marshal.FinalReleaseComObject(task);
    });

    public void Remove(long jobId) => WithRoot(root =>
    {
        using var name = new Bstr(JobTask.Name(jobId, DataFolder));
        try
        {
            root.DeleteTask(name, 0);
        }
        catch (FileNotFoundException)
        {
            // Already gone: removed by hand in Task Scheduler, or never registered.
        }
    });

    /// <summary>
    /// Null when Task Scheduler takes the XML as a task definition, otherwise its message. Connects and builds the
    /// definition in memory; opens no folder and registers nothing.
    /// </summary>
    public static string? Check(string xml)
    {
        string? error = null;
        WithService(service =>
        {
            service.NewTask(0, out var definition);
            using var text = new Bstr(xml);
            try
            {
                definition.XmlText = text;
            }
            catch (COMException e)
            {
                error = e.Message;
            }
            finally
            {
                Marshal.FinalReleaseComObject(definition);
            }
        });
        return error;
    }

    private static void WithRoot(Action<ITaskFolder> action) => WithService(service =>
    {
        using var path = new Bstr("\\");
        service.GetFolder(path, out var root);
        try
        {
            action(root);
        }
        finally
        {
            Marshal.FinalReleaseComObject(root);
        }
    });

    private static void WithService(Action<ITaskService> action)
    {
        var service = (ITaskService)new Windows.Win32.System.TaskScheduler.TaskScheduler();
        try
        {
            service.Connect(null!, null!, null!, null!);
            action(service);
        }
        finally
        {
            Marshal.FinalReleaseComObject(service);
        }
    }

    private readonly struct Bstr(string value) : IDisposable
    {
        private readonly nint _value = Marshal.StringToBSTR(value);

        public static implicit operator BSTR(Bstr b) => new(b._value);

        public void Dispose() => Marshal.FreeBSTR(_value);
    }
}
