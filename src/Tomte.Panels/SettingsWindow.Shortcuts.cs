using System.Data.Common;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Tomte.Core.Shortcuts;
using Tomte.Core.Storage;
using Windows.Storage.Pickers;

namespace Tomte.Panels;

/// <summary>
/// The Shortcuts section: the tiles with their state, add (an app from Start's list, a file or a folder by
/// browsing, a web address), remove, the script switch, and the confirmation that shows a script's full command.
/// This window is the only place a tile is added or a script confirmed. Database work runs on the thread pool.
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
        var app = new Button { Content = "Choose..." };
        app.Click += async (_, _) => await PickAppAsync();
        var web = new Button { Content = "Add" };
        web.Click += async (_, _) => await AddShortcutAsync(webAddress.Text);
        var scripts = Toggle(s => s.ScriptShortcuts, (s, v) => s with { ScriptShortcuts = v });
        scripts.Toggled += async (_, _) => await RenderShortcutsAsync();
        _ = RenderShortcutsAsync();
        return Card(
            Note("Tiles in the island's Shortcuts tab. A click opens the app, file, folder or web page. Files on a network path cannot be added."),
            shortcutsList,
            Row("Add an app, a file or a folder", Note("Apps as Start lists them. Browse in the list for any file, folder or script."), app),
            Row("Add a web address", null, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { webAddress, web } }),
            shortcutsNote,
            Row("Script shortcuts", Note("Off: script tiles do nothing. On: a script shows its full command and asks before its first run, and again when it changes. Scripts are batch, PowerShell and Windows Script Host files, and shortcuts (.lnk) that pass arguments to a program, browser web apps included, except apps added from Start's list."), scripts));
    }

    /// <summary>
    /// The apps Start lists, with their icons and a search box, and Browse for anything else. The list is read
    /// when the picker opens, on the thread pool; icons fill in as the shell draws them.
    /// </summary>
    private async Task PickAppAsync()
    {
        var search = new TextBox { PlaceholderText = "Search apps" };
        AutomationProperties.SetName(search, "Search apps");
        var status = Note("Loading apps...");
        var list = new ListView { Height = 360, SelectionMode = ListViewSelectionMode.Single };
        AutomationProperties.SetName(list, "Apps");
        var file = new MenuFlyoutItem { Text = "A file or script..." };
        var folder = new MenuFlyoutItem { Text = "A folder..." };
        var browse = new DropDownButton { Content = "Browse...", Flyout = new MenuFlyout { Items = { file, folder } } };
        var dialog = new ContentDialog
        {
            XamlRoot = Content.XamlRoot,
            Title = "Add a shortcut",
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            IsPrimaryButtonEnabled = false,
            Content = new StackPanel { Spacing = 8, Width = 440, Children = { search, status, list, browse } },
        };

        InstalledApp? selected = null, chosen = null;
        bool? pickFolder = null;
        file.Click += (_, _) =>
        {
            pickFolder = false;
            dialog.Hide();
        };
        folder.Click += (_, _) =>
        {
            pickFolder = true;
            dialog.Hide();
        };
        dialog.PrimaryButtonClick += (_, _) => chosen = selected;
        var closed = new CancellationTokenSource();
        void Select(InstalledApp? app)
        {
            selected = app;
            dialog.IsPrimaryButtonEnabled = app is not null;
        }

        void Add(InstalledApp app)
        {
            chosen = app;
            dialog.Hide();
        }

        _ = ListAppsAsync(search, status, list, Content.XamlRoot.RasterizationScale, Select, Add, closed.Token);
        await dialog.ShowAsync();
        closed.Cancel();

        if (chosen is not null)
        {
            await AddShortcutAsync(chosen.Target, chosen.Name);
        }
        else if (pickFolder is { } isFolder)
        {
            await PickAsync(isFolder);
        }
    }

    /// <summary>
    /// Fills the picker's list. Its items are the apps' targets (strings), drawn from one row template, so the list
    /// recycles rows as it scrolls and a search only swaps the items; each row's name and icon are set as it shows.
    /// </summary>
    private async Task ListAppsAsync(TextBox search, TextBlock status, ListView list, double scale, Action<InstalledApp?> select, Action<InstalledApp> add, CancellationToken closed)
    {
        List<InstalledApp> apps;
        try
        {
            apps = [.. (await Task.Run(AppList.Load, closed)).DistinctBy(app => app.Target)];
        }
        catch (Exception e) when (e is COMException or ArgumentException or IOException or UnauthorizedAccessException or OperationCanceledException)
        {
            status.Text = closed.IsCancellationRequested ? "" : $"Windows did not list the apps: {e.Message}";
            return;
        }

        if (closed.IsCancellationRequested)
        {
            return;
        }

        var byTarget = apps.ToDictionary(app => app.Target);
        var icons = new Dictionary<string, WriteableBitmap>();
        // Start lists some names more than once (an "Uninstall" per app); a second line with the ID tells them apart.
        var repeated = apps.GroupBy(app => app.Name, StringComparer.CurrentCultureIgnoreCase).Where(g => g.Skip(1).Any())
            .Select(g => g.Key).ToHashSet(StringComparer.CurrentCultureIgnoreCase);
        var secondary = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"];
        void Show(SelectorItem row, string target)
        {
            var app = byTarget[target];
            string? id = repeated.Contains(app.Name) ? app.Id : null;
            var parts = ((StackPanel)row.ContentTemplateRoot).Children;
            ((Image)parts[0]).Source = icons.GetValueOrDefault(target);
            var lines = ((StackPanel)parts[1]).Children;
            ((TextBlock)lines[0]).Text = app.Name;
            var second = (TextBlock)lines[1];
            second.Text = id ?? "";
            second.Foreground = secondary;
            second.Visibility = id is null ? Visibility.Collapsed : Visibility.Visible;
            AutomationProperties.SetName(row, id is null ? app.Name : $"{app.Name}, {id}");
        }

        list.ItemTemplate = (DataTemplate)XamlReader.Load(
            "<DataTemplate xmlns=\"http://schemas.microsoft.com/winfx/2006/xaml/presentation\"><StackPanel Orientation=\"Horizontal\" Spacing=\"12\">"
            + "<Image Width=\"24\" Height=\"24\"/><StackPanel VerticalAlignment=\"Center\"><TextBlock/><TextBlock FontSize=\"12\"/></StackPanel>"
            + "</StackPanel></DataTemplate>");
        list.ContainerContentChanging += (_, e) =>
        {
            if (!e.InRecycleQueue && e.Item is string target)
            {
                Show(e.ItemContainer, target);
            }
        };
        list.SelectionChanged += (_, _) => select(list.SelectedItem is string target ? byTarget[target] : null);
        list.DoubleTapped += (_, e) =>
        {
            if ((e.OriginalSource as FrameworkElement)?.DataContext is string target)
            {
                add(byTarget[target]);
            }
        };

        void Filter()
        {
            var shown = AppList.Filter(apps, search.Text).Select(app => app.Target).ToList();
            list.ItemsSource = shown;
            // With text typed, the first match is chosen, so Enter adds it.
            list.SelectedIndex = shown.Count > 0 && search.Text.Trim().Length > 0 ? 0 : -1;
            status.Text = shown.Count == 0 ? (apps.Count == 0 ? "Windows lists no apps." : "No app matches.") : "";
            status.Visibility = status.Text.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        search.TextChanged += (_, _) => Filter();
        Filter();

        // every icon is drawn, in list order, four at a time (about 2 s for 300 apps); drawing the rows in
        // view first would need the list's scroll position.
        int pixels = (int)Math.Round(24 * scale);
        var queue = DispatcherQueue;
        try
        {
            await Task.Run(() => Parallel.ForEach(apps, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = closed }, app =>
            {
                if (ShellIcon.For(app.Target, pixels) is { } icon)
                {
                    queue.TryEnqueue(() =>
                    {
                        icons[app.Target] = Bitmap(icon);
                        if (list.ContainerFromItem(app.Target) is SelectorItem row)
                        {
                            Show(row, app.Target);
                        }
                    });
                }
            }));
        }
        catch (OperationCanceledException)
        {
            // The picker closed; the rest of the icons are not needed.
        }
    }

    private static WriteableBitmap Bitmap(ShellImage icon)
    {
        var bitmap = new WriteableBitmap(icon.Width, icon.Height);
        using (var pixels = bitmap.PixelBuffer.AsStream())
        {
            pixels.Write(icon.Pixels);
        }

        bitmap.Invalidate();
        return bitmap;
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

    private async Task AddShortcutAsync(string target, string? name = null)
    {
        string? error = await Db(() => Shortcuts.Add(target, name));
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
