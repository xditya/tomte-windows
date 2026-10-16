using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Tomte.Core;
using Tomte.Core.Search;

namespace Tomte.Panels;

/// <summary>
/// The Search page: the search box's shortcut (set on the Hotkeys page with the others), which file search is in use,
/// getting Everything, and the web search engine.
/// </summary>
public sealed partial class SettingsWindow
{
    // Under the file search: small local models rarely call AI search's tools.
    private const string AiSearchNote = "Search with AI works best with the cloud model or a local model of 7B or larger.";

    private readonly TextBlock fileSearchNote = Note();
    private readonly Button getEverything = new() { Content = "Get Everything" };

    // The shortcut, with a link to its box on the Hotkeys page; one line on where files come from, read when the window
    // opens, and the button while Everything is not installed; the web search engine.
    private Border SearchSection()
    {
        var shortcut = Note();
        void ShowShortcut(TomteSettings s)
        {
            shortcut.Text = s.Hotkeys.Search.Length > 0 ? $"{s.Hotkeys.Search} opens the search box." : "No shortcut opens the search box.";
            shortcut.Visibility = Visibility.Visible;
        }

        ShowShortcut(store.Current);
        store.Changed += s => DispatcherQueue.TryEnqueue(() => ShowShortcut(s));
        var change = new HyperlinkButton { Content = "Change in Hotkeys" };
        change.Click += (_, _) =>
        {
            if (ReadRows().FirstOrDefault(r => r.Row is { Page: "Hotkeys" } && r.Row.Title == Hotkey.Name(HotkeyAction.Search)) is { } box)
            {
                _ = ShowAsync(box);
            }
        };
        getEverything.Click += async (_, _) => await GetEverythingAsync();
        _ = ShowFileSearchAsync();
        var card = Card(
            Row("Search shortcut", shortcut, change),
            Row("File search", fileSearchNote, getEverything),
            Row("Web search", Note("What Open in browser in the search box searches with."), WebSearchEngines()),
            Note(AiSearchNote));
        // A row names its control after the row; a screen reader should hear what the link and the button do.
        AutomationProperties.SetName(change, "Change the search shortcut in Hotkeys");
        AutomationProperties.SetName(getEverything, "Get Everything");
        return card;
    }

    // Google, Bing or DuckDuckGo, in the enum's order.
    private ComboBox WebSearchEngines()
    {
        var box = new ComboBox { MinWidth = 200 };
        foreach (var engine in Enum.GetValues<WebSearchEngine>())
        {
            box.Items.Add(WebSearch.Name(engine));
        }

        // A number written into settings.json by hand may name no engine; the list then shows Google, as the search does.
        box.SelectedIndex = Enum.IsDefined(store.Current.WebSearch) ? (int)store.Current.WebSearch : 0;
        box.SelectionChanged += (_, _) =>
        {
            if (box.SelectedIndex >= 0)
            {
                Save(s => s with { WebSearch = (WebSearchEngine)box.SelectedIndex });
            }
        };
        return box;
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
