using Microsoft.UI.Xaml.Controls;
using Tomte.AI.Browser;
using Tomte.AI.Jobs;
using Tomte.Core;
using Tomte.Core.Jobs;
using Tomte.Core.Storage;

namespace Tomte.Panels;

/// <summary>
/// The scheduled jobs section: each job made in the AI box or by voice, when it runs, its next run and the tools it may
/// use, with Remove; how long before a run the island shows its Jobs tab; and a button that opens Tomte's browser for
/// signing in to sites. Read when the window opens and after a removal; nothing watches tomte.db.
/// </summary>
public sealed partial class SettingsWindow
{
    private readonly StackPanel jobsList = new() { Spacing = 12 };
    private readonly TextBlock jobsNote = Note();

    private string DataFolder => Path.GetDirectoryName(Path.GetFullPath(store.FilePath))!;

    private JobStore Jobs => new(new TomteDb(Path.Combine(DataFolder, "tomte.db")));

    private Border JobsSection() => Card(
        Note("Jobs you made in the AI box or by voice. Each runs at its time with only the tools listed, without asking, and sends to the provider it was made with."),
        jobsList,
        jobsNote,
        Row("Jobs tab", Note("Minutes before a job runs that the island shows the Jobs tab and names the job on the pill. The tab also shows while a job runs."),
            Number(TomteSettings.JobsAheadMinMinutes, TomteSettings.JobsAheadMaxMinutes, store.Current.JobsAhead.TotalMinutes, value => Save(s => s with { JobsAheadMinutes = (int)value }))),
        Row("Tomte's browser", Note("Jobs and the AI box use their own Edge profile, not yours. Sign in to sites here once; Tomte never types passwords."), BrowserButton()));

    // Edge on Tomte's own profile, in a window, for signing in; what it signs in to stays in that profile.
    private Button BrowserButton()
    {
        var open = new Button { Content = "Open Tomte's browser" };
        open.Click += (_, _) => Show(jobsNote, Edge.OpenForSignIn());
        return open;
    }

    private async Task RenderJobsAsync()
    {
        try
        {
            var jobs = await Task.Run(Jobs.All);
            jobsList.Children.Clear();
            Show(jobsNote, null);
            if (jobs.Count == 0)
            {
                jobsList.Children.Add(Note("No scheduled jobs."));
            }

            foreach (var job in jobs)
            {
                var remove = new Button { Content = "Remove" };
                remove.Click += async (_, _) => await RemoveJobAsync(job.Id, remove);
                jobsList.Children.Add(Row(job.Name, Note(job.Describe(TimeZoneInfo.Local)), remove));
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Called from the constructor and a click: an exception leaving here would end the settings process.
            Show(jobsNote, $"The scheduled jobs could not be read: {e.Message}");
        }
    }

    // The job, its results and its Task Scheduler entry go, as --debug-remove-job does it; then the running Tomte reads
    // the next-due file again, so a removed job's time wakes nothing.
    private async Task RemoveJobAsync(long id, Button remove)
    {
        remove.IsEnabled = false;
        var planner = new JobPlanner(Jobs, NoJobTasks.For(Environment.ProcessPath!, store.FilePath));
        string? problem;
        try
        {
            problem = await Task.Run(() => planner.Remove(id));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            problem = $"Not removed: {e.Message}";
        }

        JobTask.Changed(DataFolder);
        await RenderJobsAsync();
        if (problem is not null)
        {
            Show(jobsNote, problem);
        }
    }
}
