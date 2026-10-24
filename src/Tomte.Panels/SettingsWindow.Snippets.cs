using System.Data.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Tomte.Core.Storage;
using Snippet = Tomte.Core.Text.Snippet;
using SnippetStore = Tomte.Core.Text.SnippetStore;

namespace Tomte.Panels;

/// <summary>
/// The snippets section: every saved snippet with its text, Edit (its name and text) and Delete, and Add. The note lists
/// them when "/" is typed at the start of a line or after a space. Database work runs on the thread pool.
/// </summary>
public sealed partial class SettingsWindow
{
    private readonly StackPanel snippetsList = new() { Spacing = 12 };
    private readonly TextBlock snippetsNote = Note();
    private SnippetStore? snippets;

    // Made on first use, so the database opens only once this section loads.
    private SnippetStore Snippets => snippets ??= new(new TomteDb(Path.Combine(Path.GetDirectoryName(store.FilePath)!, "tomte.db")));

    private Border SnippetsSection()
    {
        var add = new Button { Content = "Add..." };
        add.Click += async (_, _) => await EditSnippetAsync(null);
        _ = RenderSnippetsAsync();
        return Card(
            Note("In the scratchpad, type / and a snippet's name at the start of a line or after a space, then press Enter or Tab to put its text in. Up and Down choose, Esc leaves what you typed. Ctrl+Z takes the snippet back out."),
            snippetsList,
            Row("Add a snippet", null, add),
            snippetsNote);
    }

    private async Task RenderSnippetsAsync()
    {
        IReadOnlyList<Snippet>? all = null;
        string? error = await SnippetDb(() => all = Snippets.Load());
        Show(snippetsNote, error);
        if (all is null)
        {
            return;
        }

        snippetsList.Children.Clear();
        if (all.Count == 0)
        {
            snippetsList.Children.Add(Note("No snippets yet."));
        }

        foreach (var snippet in all)
        {
            var edit = new Button { Content = "Edit..." };
            edit.Click += async (_, _) => await EditSnippetAsync(snippet);
            var delete = new Button { Content = "Delete" };
            delete.Click += async (_, _) =>
            {
                Show(snippetsNote, await SnippetDb(() => Snippets.Delete(snippet.Id)));
                await RenderSnippetsAsync();
            };
            AutomationProperties.SetName(edit, $"Edit {snippet.Name}");
            AutomationProperties.SetName(delete, $"Delete {snippet.Name}");
            string preview = snippet.Text.Length > 160 ? snippet.Text[..160] + "..." : snippet.Text;
            snippetsList.Children.Add(Row("/" + snippet.Name, Note(preview), new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { edit, delete } }));
        }
    }

    /// <summary>A new snippet, or a new name or text for <paramref name="snippet"/>. Save waits for both.</summary>
    private async Task EditSnippetAsync(Snippet? snippet)
    {
        var name = new TextBox { Header = "Name", PlaceholderText = "sig", Text = snippet?.Name ?? "", MaxLength = SnippetStore.MaxName };
        var text = new TextBox
        {
            Header = "Text",
            Text = snippet?.Text.Replace("\n", "\r", StringComparison.Ordinal) ?? "",
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            Height = 180,
        };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = snippet is null ? "Add a snippet" : "Edit snippet",
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            Content = new StackPanel { Spacing = 12, Width = 440, Children = { name, text, Note("Type / and the name in the scratchpad to put the text in.") } },
        };
        void Check() => dialog.IsPrimaryButtonEnabled = SnippetStore.Name(name.Text).Length > 0 && text.Text.Trim().Length > 0;
        name.TextChanged += (_, _) => Check();
        text.TextChanged += (_, _) => Check();
        Check();
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        string newName = name.Text, newText = text.Text;
        Show(snippetsNote, await SnippetDb(() =>
        {
            if (snippet is null)
            {
                Snippets.Add(newName, newText);
            }
            else
            {
                Snippets.Update(snippet.Id, newName, newText);
            }
        }));
        await RenderSnippetsAsync();
    }

    /// <summary>Runs on the thread pool; a database that cannot be reached comes back as a message instead of ending the window.</summary>
    private static async Task<string?> SnippetDb(Action work)
    {
        try
        {
            await Task.Run(work);
            return null;
        }
        catch (Exception e) when (e is DbException or IOException or UnauthorizedAccessException)
        {
            return $"Tomte could not reach its saved snippets: {e.Message}";
        }
    }
}
