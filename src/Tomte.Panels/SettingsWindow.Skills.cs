using System.Diagnostics;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tomte.AI.Mcp;
using Tomte.AI.Skills;

namespace Tomte.Panels;

/// <summary>
/// The skills section: every folder in the skills folder with its description, whether it is on, what
/// it registers, any problem, and a note when it ships scripts. The folder is read when the window
/// opens and when "Look again" is clicked; nothing watches it.
/// </summary>
public sealed partial class SettingsWindow
{
    private readonly StackPanel skillsList = new() { Spacing = 12 };
    private readonly TextBlock skillsNote = Note();

    private string SkillsFolder => SkillSet.FolderFor(store.FilePath);

    private Border SkillsSection()
    {
        var open = new Button { Content = "Open folder" };
        open.Click += (_, _) => OpenSkillsFolder();
        var refresh = new Button { Content = "Look again" };
        refresh.Click += async (_, _) => await RenderSkillsAsync();
        return Card(
            Note($"A skill is a folder with a SKILL.md file in {SkillsFolder}. A new skill starts off. When it is on, every AI palette request carries its name and description, and the model reads the rest of its folder with read_skill_file, which runs without asking, only when the skill fits what you asked. A skill's text goes to your provider only with a request you send. Skills cannot allow or approve tools: every tool still asks as set above."),
            skillsList,
            new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { open, refresh } },
            skillsNote);
    }

    private async Task RenderSkillsAsync()
    {
        try
        {
            string folder = SkillsFolder;
            var skills = await Task.Run(() => SkillSet.List(folder));
            skillsList.Children.Clear();
            Show(skillsNote, null);
            if (skills.Count == 0)
            {
                skillsList.Children.Add(Note("No skills yet. Put a skill's folder in the skills folder, then click Look again."));
            }

            foreach (var skill in skills)
            {
                // "On" holds for the skill as it was when the user turned it on; an edit since turns it off until confirmed.
                var enabled = new ToggleSwitch { IsOn = SkillSet.IsOn(store.Current.EnabledSkills, skill), OnContent = "On", OffContent = "Off" };
                enabled.Toggled += (_, _) => Save(s => s with { EnabledSkills = SkillSet.Turn(s.EnabledSkills, skill, enabled.IsOn) });
                var details = new StackPanel { Spacing = 4, Margin = new Thickness(16, 0, 0, 0) };
                if (SkillSet.ChangedSinceOn(store.Current.EnabledSkills, skill))
                {
                    var changed = Note();
                    Show(changed, "Changed since you turned it on. Read it again, then turn it on to use it.");
                    details.Children.Add(changed);
                }

                foreach (string line in Details(skill))
                {
                    details.Children.Add(Note(line));
                }

                if (skill.Problem is not null)
                {
                    var problem = Note();
                    Show(problem, "Not loaded. " + skill.Problem);
                    details.Children.Add(problem);
                }

                string name = McpTools.Clean(skill.Name, McpTools.MaxName);
                // The whole description: it is exactly what every request carries while the skill is on.
                var row = Row(name, skill.Description.Length == 0 ? null : Note(skill.Description), enabled);
                skillsList.Children.Add(new StackPanel { Spacing = 8, Children = { row, details } });
            }

            skillsList.Children.Add(SlotRow(skills));
        }
        catch (Exception e)
        {
            // Called from an async void handler: an exception leaving here would end the settings process.
            Show(skillsNote, $"Could not read the skills folder: {e.Message}");
        }
    }

    /// <summary>The skill behind the custom drop zone: none, or one of the skills that are on and register a drop zone.</summary>
    private Grid SlotRow(IReadOnlyList<Skill> skills)
    {
        var choices = SkillSet.DropZoneChoices(skills, store.Current.EnabledSkills);
        var names = choices.Select(s => s.Name).ToList();
        var box = new ComboBox
        {
            ItemsSource = new[] { "None" }.Concat(choices.Select(s => $"{McpTools.Clean(s.Name, McpTools.MaxName)}: {McpTools.Clean(s.DropZone!.Label, 40)}")).ToArray(),
            SelectedIndex = names.IndexOf(store.Current.DropZoneSkill ?? "") + 1,
            MinWidth = 200,
        };
        box.SelectionChanged += (_, _) => Save(s => s with { DropZoneSkill = box.SelectedIndex <= 0 ? null : names[box.SelectedIndex - 1] });
        return Row("Custom drop zone", Note("The fifth zone while you drag a file toward the top of the screen, when an AI provider is set up. A drop on it sends the file to your provider with the skill you pick here. Only skills that are on and add a drop zone are listed."), box);
    }

    private static IEnumerable<string> Details(Skill skill)
    {
        if (skill.Command is { } command)
            yield return $"Adds the palette command \"{command.Name}\", which asks: {command.Text}";
        if (skill.DropZone is { } zone)
            yield return $"Adds the drop zone \"{zone.Label}\" for {string.Join(", ", zone.Accepts)}. It shows while you drag once you pick it as the custom drop zone below.";
        if (skill.Scripts.Count > 0)
            yield return $"Ships scripts ({string.Join(", ", skill.Scripts.Take(3).Select(s => McpTools.Clean(s, 80)))}{(skill.Scripts.Count > 3 ? $" and {skill.Scripts.Count - 3} more" : "")}). Tomte never runs them: it refuses to open a file from the skills folder.";
        if (skill.IgnoredKeys.Count > 0)
            yield return $"Tomte does not use these SKILL.md keys: {string.Join(", ", skill.IgnoredKeys)}.{(skill.IgnoredKeys.Contains("allowed-tools") ? " A skill cannot approve tools in advance." : "")}";
        if (skill.License is { Length: > 0 } license)
            yield return $"License: {license}";
    }

    private void OpenSkillsFolder()
    {
        try
        {
            Directory.CreateDirectory(SkillsFolder);
            Process.Start(new ProcessStartInfo(SkillsFolder) { UseShellExecute = true })?.Dispose();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            Show(skillsNote, $"Could not open the skills folder: {e.Message}");
        }
    }
}
