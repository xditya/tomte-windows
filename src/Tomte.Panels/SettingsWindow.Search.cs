using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Tomte.Core.Search;

namespace Tomte.Panels;

/// <summary>The Search part of the Hotkeys page: which file search is in use, and getting Everything.</summary>
public sealed partial class SettingsWindow
{
    private readonly TextBlock fileSearchNote = Note();
    private readonly Button getEverything = new() { Content = "Get Everything" };

    // One line on where files come from, read when the window opens, and the button while Everything is not installed.
    private Border FileSearchSection()
    {
        getEverything.Click += async (_, _) => await GetEverythingAsync();
        _ = ShowFileSearchAsync();
        var row = Row("File search", fileSearchNote, getEverything);
        // The row names its control after the row; a screen reader should hear what the button does.
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(getEverything, "Get Everything");
        return Card(row);
    }

    private async Task ShowFileSearchAsync()
    {
        var source = await Task.Run(EverythingSetup.Source);
        fileSearchNote.Text = EverythingSetup.Describe(source);
        fileSearchNote.Visibility = Visibility.Visible;
        getEverything.Visibility = source == FileSource.Index ? Visibility.Visible : Visibility.Collapsed;
    }

    // The same as the search box's offer: it asks first, then winget installs Everything for every user, which Windows
    // asks permission for once. Called from an async void handler, so nothing may escape.
    private async Task GetEverythingAsync()
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Get Everything for whole-disk search",
            Content = new TextBlock
            {
                Text = "Tomte installs Everything (free, from voidtools) with winget. Windows asks for permission once. Everything then runs in the background, indexes every drive and starts when you sign in.",
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 440,
            },
            PrimaryButtonText = "Install",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        getEverything.IsEnabled = false;
        fileSearchNote.Text = "Installing Everything with winget. Windows may ask for permission.";
        string? error;
        try
        {
            error = await Task.Run(() => EverythingSetup.InstallAsync(null, CancellationToken.None));
        }
        catch (Exception e)
        {
            error = $"Everything was not installed: {e.Message}";
        }

        getEverything.IsEnabled = true;
        await ShowFileSearchAsync();
        if (error is not null)
        {
            fileSearchNote.Text = $"{error} You can get it from {EverythingSetup.DownloadPage}";
        }
    }
}
