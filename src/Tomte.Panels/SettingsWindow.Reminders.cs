using Microsoft.UI.Xaml.Controls;
using Tomte.Core.Jobs;
using Tomte.Core.Storage;

namespace Tomte.Panels;

/// <summary>
/// The reminders section: each reminder, when it rings in words and its next time, with Remove. Read when the window
/// opens and after a removal; nothing watches tomte.db.
/// </summary>
public sealed partial class SettingsWindow
{
    private readonly StackPanel remindersList = new() { Spacing = 12 };
    private readonly TextBlock remindersNote = Note();

    private ReminderStore ReminderStore => new(new TomteDb(Path.Combine(DataFolder, "tomte.db")));

    private Border RemindersSection() => Card(
        Note("Type \"remind me\" in the AI box, with or without a provider: \"remind me in 20 minutes to check the oven\", \"every weekday at 09:30 remind me standup\". When one is due the island shows it until you dismiss it."),
        remindersList,
        remindersNote);

    private async Task RenderRemindersAsync()
    {
        try
        {
            var reminders = await Task.Run(ReminderStore.All);
            remindersList.Children.Clear();
            Show(remindersNote, null);
            if (reminders.Count == 0)
            {
                remindersList.Children.Add(Note("No reminders."));
            }

            foreach (var reminder in reminders)
            {
                var remove = new Button { Content = "Remove" };
                remove.Click += async (_, _) => await RemoveReminderAsync(reminder.Id, remove);
                remindersList.Children.Add(Row(reminder.Text, Note(reminder.Describe(TimeZoneInfo.Local)), remove));
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Called from the constructor and a click: an exception leaving here would end the settings process.
            Show(remindersNote, $"The reminders could not be read: {e.Message}");
        }
    }

    // Then the running Tomte arms its one-shot again, so a removed reminder's time wakes nothing.
    private async Task RemoveReminderAsync(long id, Button remove)
    {
        remove.IsEnabled = false;
        string? problem = null;
        try
        {
            await Task.Run(() => ReminderStore.Remove(id));
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            problem = $"Not removed: {e.Message}";
        }

        JobTask.Changed(DataFolder);
        await RenderRemindersAsync();
        if (problem is not null)
        {
            Show(remindersNote, problem);
        }
    }
}
