using System.Data.Common;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Tomte.Core.Shortcuts;
using Tomte.Core.Storage;
using Windows.Storage.Pickers;

namespace Tomte.Panels;

/// <summary>
/// The Shortcuts section: the tiles with their state, add (a file or app, a folder, a web address), remove, the
/// script switch, and the confirmation that shows a script's full command. This window is the only place a
/// tile is added or a script confirmed. Database work runs on the thread pool.
/// </summary>
public sealed partial class SettingsWindow
{
    private readonly StackPanel shortcutsList = new() { Spacing = 12 };
    private readonly TextBlock shortcutsNote = Note();
    private readonly TextBox webAddress = new() { PlaceholderText = "https://", MinWidth = 280 };
    private ShortcutStore? shortcuts;

    // Made on first use, so the database opens only once this section loads; tomte.db is the one next to the
    // settings file, as in the shell that opened this window.
    private ShortcutStore Shortcuts => shortcuts ??= new(new TomteDb(Path.Combine(Path.GetDirectoryName(store.FilePath)!, "tomte.db")), () => store.Current.ScriptShortcuts);

    private Border ShortcutsSection()
    {
        var file = new Button { Content = "Choose a file or app..." };
        file.Click += async (_, _) => await PickAsync(folder: false);
        var folder = new Button { Content = "Choose a folder..." };
        folder.Click += async (_, _) => await PickAsync(folder: true);
        var web = new Button { Content = "Add" };
        web.Click += async (_, _) => await AddShortcutAsync(webAddress.Text);
        var scripts = Toggle(s => s.ScriptShortcuts, (s, v) => s with { ScriptShortcuts = v });
        scripts.Toggled += async (_, _) => await RenderShortcutsAsync();
        _ = RenderShortcutsAsync();
        return Card(
            Note("Tiles in the island's Shortcuts tab. A click opens the app, file, folder or web page. Web addresses start with http or https; files on a network path are not offered."),
            shortcutsList,
            Row("Add an app or a file", null, file),
            Row("Add a folder", null, folder),
            Row("Add a web address", null, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { webAddress, web } }),
            shortcutsNote,
            Row("Script shortcuts", Note("Off: script tiles do nothing. On: each script asks once before it runs, showing its full command, and asks again when it changes. Scripts are batch, PowerShell and Windows Script Host files, and every shortcut (.lnk) that passes arguments to a program, browser web apps and profiles included."), scripts));
    }

    private async Task PickAsync(bool folder)
    {
        nint window = WinRT.Interop.WindowNative.GetWindowHandle(this);
        string? path;
        if (folder)
        {
            var picker = new FolderPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, window);
            path = (await picker.PickSingleFolderAsync())?.Path;
        }
        else
        {
            var picker = new FileOpenPicker();
            picker.FileTypeFilter.Add("*");
            WinRT.Interop.InitializeWithWindow.Initialize(picker, window);
            path = (await picker.PickSingleFileAsync())?.Path;
        }

        // An item with no path (a phone, a library view) cannot be a tile.
        if (!string.IsNullOrEmpty(path))
        {
            await AddShortcutAsync(path);
        }
    }

    private async Task AddShortcutAsync(string target)
    {
        string? error = await Db(() => Shortcuts.Add(target));
        Show(shortcutsNote, error);
        if (error is null)
        {
            webAddress.Text = "";
            await RenderShortcutsAsync();
        }
    }

    private async Task RenderShortcutsAsync()
    {
        List<(Shortcut Tile, ShortcutTarget Target)>? rows = null;
        string? error = await Db(() =>
        {
            rows = [.. Shortcuts.Load().Select(tile => (tile, ShortcutTarget.Read(tile.Target)))];
            return null;
        });
        if (rows is null)
        {
            Show(shortcutsNote, error);
            return;
        }

        bool scriptsOn = store.Current.ScriptShortcuts;
        shortcutsList.Children.Clear();
        if (rows.Count == 0)
        {
            shortcutsList.Children.Add(Note("No shortcuts yet."));
        }

        foreach (var (tile, target) in rows)
        {
            var gate = ShortcutStore.Gate(tile, target, scriptsOn);
            var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            if (target.Script is { } command && gate is ShortcutOutcome.NeedsConfirm or ShortcutOutcome.Changed)
            {
                var confirm = new Button { Content = "Confirm..." };
                confirm.Click += async (_, _) => await ConfirmAsync(tile, command);
                buttons.Children.Add(confirm);
            }

            var remove = new Button { Content = "Remove" };
            remove.Click += async (_, _) =>
            {
                Show(shortcutsNote, await Db(() =>
                {
                    Shortcuts.Remove(tile.Id);
                    return null;
                }));
                await RenderShortcutsAsync();
            };
            buttons.Children.Add(remove);
            shortcutsList.Children.Add(Row(tile.Name, Note($"{State(target, gate)} {tile.Target}"), buttons));
        }
    }

    private static string State(ShortcutTarget target, ShortcutOutcome? gate) => gate switch
    {
        ShortcutOutcome.Missing => "Moved or deleted:",
        ShortcutOutcome.Blocked => target.Problem + "",
        ShortcutOutcome.ScriptsOff => "Script, off while script shortcuts are off:",
        ShortcutOutcome.NeedsConfirm => "Script, not confirmed yet:",
        ShortcutOutcome.Changed => "Script, changed since you confirmed it:",
        _ => target.Kind switch
        {
            ShortcutKind.App => "App:",
            ShortcutKind.Folder => "Folder:",
            ShortcutKind.Url => "Web page:",
            ShortcutKind.Script => "Script, confirmed:",
            _ => "File:",
        },
    };

    /// <summary>The full command, exactly as it will run; Confirm stores the fingerprint of what was shown here.</summary>
    private async Task ConfirmAsync(Shortcut tile, ScriptCommand command)
    {
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = $"Run {tile.Name} from its tile?",
            PrimaryButtonText = "Confirm",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
            Content = new StackPanel
            {
                Spacing = 8,
                MaxWidth = 520,
                Children =
                {
                    new TextBlock { Text = "When you click the tile, Tomte runs this command:", TextWrapping = TextWrapping.Wrap },
                    new TextBox { Text = command.Text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas") },
                    new TextBlock { Text = $"Working folder: {command.Folder}", TextWrapping = TextWrapping.Wrap },
                    Note(command.HashedFile is null
                        ? "It runs with your permissions and can change anything you can. Only this command is checked for changes, not any file it names: if such a file changes, the tile does not ask again."
                        : "It runs with your permissions and can change anything you can. If the script or this command changes, the tile asks again."),
                },
            },
        };
        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
        {
            Show(shortcutsNote, await Db(() =>
            {
                Shortcuts.Confirm(tile.Id, command);
                return null;
            }));
            await RenderShortcutsAsync();
        }
    }

    /// <summary>Runs on the thread pool; a database that cannot be reached comes back as a message instead of ending the window.</summary>
    private static async Task<string?> Db(Func<string?> work)
    {
        try
        {
            return await Task.Run(work);
        }
        catch (Exception e) when (e is DbException or IOException or UnauthorizedAccessException)
        {
            return $"Tomte could not reach its saved shortcuts: {e.Message}";
        }
    }
}
