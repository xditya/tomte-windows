using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
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

    /// <summary>The island window's title, so a --run-job start finds the Tomte on its own data folder and no other.</summary>
    public static string WindowTitle(string dataFolder) => "Tomte " + Key(dataFolder);

    /// <summary>
    /// Hands a --run-job start to the Tomte already running on <paramref name="dataFolder"/>: posts the id to the window
    /// with that folder's title. False when none is running there; the caller then runs the job itself.
    /// </summary>
    public static bool HandOff(string dataFolder, long jobId)
    {
        var target = PInvoke.FindWindow(null, WindowTitle(dataFolder));
        return !target.IsNull && PInvoke.PostMessage(target, MsgRunJob, (nuint)jobId, 0);
    }

    public static string Arguments(long jobId, string settingsPath) =>
        string.Equals(Path.GetFullPath(settingsPath), Path.GetFullPath(SettingsStore.DefaultPath), StringComparison.OrdinalIgnoreCase)
            ? $"{RunJobSwitch} {jobId}"
            : $"--debug-settings \"{Path.GetFullPath(settingsPath)}\" {RunJobSwitch} {jobId}";

    /// <summary>
    /// The task: a calendar trigger in local time from the job's next run (Task Scheduler follows daylight saving the
    /// same way), for the signed-in user only, no elevation, never waking the PC, started late when the time was missed.
    /// Ten minutes at most: the start hands the job to a running Tomte or runs it with no island and exits, and a run
    /// times out after two. Normal priority instead of Task Scheduler's below-normal default.
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
                new XElement(Ns + "ExecutionTimeLimit", "PT10M"),
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
