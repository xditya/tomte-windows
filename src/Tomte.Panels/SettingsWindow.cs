using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.XamlTypeInfo;
using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Device;
using Windows.ApplicationModel;
using Windows.Graphics;
using Windows.Win32;
using Windows.Win32.Foundation;

namespace Tomte.Panels;

/// <summary>
/// The settings window: an ordinary WinUI 3 window that may take focus. It runs in its own short-lived
/// process (Tomte.Shell.exe --settings), because XAML memory does not come back once it has been
/// initialised; closing the window ends the process. Every change is saved at once, and the shell
/// picks it up from the file.
/// </summary>
public sealed partial class SettingsWindow : Window
{
    private const string StartupTaskId = "TomteStartup";

    private static string? settingsFile;
    private readonly SettingsStore store = new(settingsFile);
    private readonly StackPanel rulesList = new() { Spacing = 4 };
    private readonly StackPanel grantsList = new() { Spacing = 4 };
    private readonly ToggleSwitch startup = new() { OnContent = "On", OffContent = "Off" };
    private readonly TextBlock startupNote = Note();
    private StartupTask? startupTask;

    public SettingsWindow()
    {
        Title = "Tomte settings";
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "tomte.ico"));
        double scale = PInvoke.GetDpiForWindow(new HWND(WinRT.Interop.WindowNative.GetWindowHandle(this))) / 96.0;
        AppWindow.Resize(new SizeInt32((int)(720 * scale), (int)(820 * scale)));

        var page = new StackPanel { Spacing = 8, MaxWidth = 760, Padding = new Thickness(24, 16, 24, 32) };
        page.Children.Add(Header("General"));
        page.Children.Add(Card(
            Row("Start with Windows", startupNote, startup),
            Row("Desktop peek", Note("Show the clock and what is playing at the top of the screen when no window is there."), Toggle(s => s.DesktopPeek, (s, v) => s with { DesktopPeek = v })),
            Row("Track changes", Note("Show the pill for a few seconds when another track starts playing."), Toggle(s => s.MediaTrackChangeEvent, (s, v) => s with { MediaTrackChangeEvent = v })),
            Row("Monitors", Note("Where an edge push or a drag can reveal the island."), Monitors())));

        page.Children.Add(Header("Hotkeys"));
        page.Children.Add(Card([.. Enum.GetValues<HotkeyAction>().Select(HotkeyRow), CopilotNote()]));

        page.Children.Add(Header("Shortcuts"));
        page.Children.Add(ShortcutsSection());

        page.Children.Add(Header("Reveal zone and thresholds"));
        page.Children.Add(Card(
            Row("Edge push", Note("Push the cursor up against the top edge to reveal."), RevealToggle(r => r.EdgePush, (r, v) => r with { EdgePush = v })),
            Row("Reveal zone", Note("Width of the zone in the middle of the top edge, in percent."), Number(10, 100, r => r.ZoneFraction * 100, (r, v) => r with { ZoneFraction = v / 100 })),
            Row("Push strength, mouse", Note("Upward mouse movement needed, in raw counts. Raise it if the island opens by accident."), Number(10, 1000, r => r.PushCounts, (r, v) => r with { PushCounts = (int)v })),
            Row("Push strength, touchpad", Note("The same for a precision touchpad."), Number(10, 1000, r => r.TouchpadPushCounts, (r, v) => r with { TouchpadPushCounts = (int)v })),
            Row("Push time", Note("The push has to happen within this many milliseconds."), Number(100, 2000, r => r.PushWindowMs, (r, v) => r with { PushWindowMs = (int)v })),
            Row("Dwell", Note("Resting in the zone this many milliseconds also reveals. 0 turns it off."), Number(0, 3000, r => r.DwellMs, (r, v) => r with { DwellMs = (int)v })),
            Row("Drag reveal", Note("Dragging a file to the top edge shows the shelf."), RevealToggle(r => r.DragReveal, (r, v) => r with { DragReveal = v })),
            Row("Drag distance", Note("How close to the top edge a drag has to come, in pixels at 100% scale."), Number(8, 200, r => r.DragApproachDip, (r, v) => r with { DragApproachDip = (int)v })),
            Row("Drop wait", Note("Milliseconds to wait for a real drop before the shelf goes away."), Number(200, 3000, r => r.DragEnterTimeoutMs, (r, v) => r with { DragEnterTimeoutMs = (int)v })),
            Row("Retract margin", Note("How far outside the island the cursor can go before it starts to retract, in pixels at 100% scale."), Number(0, 200, r => r.RetractMarginDip, (r, v) => r with { RetractMarginDip = (int)v })),
            Row("Retract delay", Note("Milliseconds outside that margin before the island retracts."), Number(100, 5000, r => r.RetractDelayMs, (r, v) => r with { RetractDelayMs = (int)v }))));

        page.Children.Add(Header("Per-app rules"));
        page.Children.Add(Card(
            Note("Allow: edge push and drag reveal work even when the app is fullscreen. Deny: only hotkeys open the island while the app is in front. Deny with hotkeys: the hotkeys go to the app as well."),
            rulesList,
            AddRuleRow()));

        var kept = new Tomte.Core.Clipboard.ClipboardRetention();
        page.Children.Add(Header("Clipboard history"));
        page.Children.Add(Card(Row(
            "Keep history",
            Note($"Keep the last {kept.MaxItems} pieces of text you copy on this PC, for up to {kept.MaxDays} days. Items password managers mark are never kept, and nothing is kept while Tomte is paused or an app with a deny rule is in front. Turning it off deletes the history."),
            Toggle(s => s.KeepClipboardHistory, (s, v) => s with { KeepClipboardHistory = v }))));

        page.Children.Add(Header("AI tools"));
        page.Children.Add(Card(
            Note("Tools the AI layer can use when you ask for their effect. Nothing runs without a provider configured. Tools marked \"asks each time\" can be always allowed from the prompt; remove a grant here to be asked again."),
            Note("By voice (hold the rambler hotkey and speak), every tool asks before it runs, always allowed or not, and MCP server tools are not offered."),
            Row("Spoken replies", Note("Read the answer to a voice command aloud with Windows speech."), Toggle(s => s.SpokenReplies, (s, v) => s with { SpokenReplies = v })),
            Note("Always allowed"),
            grantsList,
            Note("All tools"),
            Card([.. BuiltInTools.All.Select(ToolRow)])));

        page.Children.Add(Header("MCP servers"));
        page.Children.Add(McpSection());

        page.Children.Add(Header("Skills"));
        page.Children.Add(SkillsSection());

        Content = new ScrollViewer { Content = page };
        RenderRules();
        RenderGrants();
        RenderMcpServers();
        _ = RenderSkillsAsync();
        _ = LoadStartupAsync();
        startup.Toggled += async (_, _) => await SetStartupAsync(startup.IsOn);
        Closed += (_, _) => store.Dispose();
    }

    /// <summary>
    /// Starts WinUI on this thread, shows the window and returns when it closes. <paramref name="settingsPath"/> is the
    /// shell's settings file (null for the default); tomte.db is the one next to it.
    /// </summary>
    public static void Run(string? settingsPath = null)
    {
        settingsFile = settingsPath;
        WinRT.ComWrappersSupport.InitializeComWrappers();
        Application.Start(p =>
        {
            SynchronizationContext.SetSynchronizationContext(new Microsoft.UI.Dispatching.DispatcherQueueSynchronizationContext(Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread()));
            _ = new SettingsApp();
        });
    }

    private void Save(Func<TomteSettings, TomteSettings> change) => store.Save(change(store.Current));

    private ToggleSwitch Toggle(Func<TomteSettings, bool> get, Func<TomteSettings, bool, TomteSettings> set)
    {
        var toggle = new ToggleSwitch { IsOn = get(store.Current), OnContent = "On", OffContent = "Off" };
        toggle.Toggled += (_, _) => Save(s => set(s, toggle.IsOn));
        return toggle;
    }

    private ToggleSwitch RevealToggle(Func<RevealSettings, bool> get, Func<RevealSettings, bool, RevealSettings> set) =>
        Toggle(s => get(s.Reveal), (s, v) => s with { Reveal = set(s.Reveal, v) });

    private ComboBox Monitors()
    {
        var box = new ComboBox { ItemsSource = new[] { "Every monitor", "Primary monitor only" }, SelectedIndex = store.Current.AllMonitors ? 0 : 1, MinWidth = 200 };
        box.SelectionChanged += (_, _) => Save(s => s with { AllMonitors = box.SelectedIndex == 0 });
        return box;
    }

    private NumberBox Number(double min, double max, Func<RevealSettings, double> get, Func<RevealSettings, double, RevealSettings> set)
    {
        var box = new NumberBox
        {
            Minimum = min,
            Maximum = max,
            Value = Math.Round(get(store.Current.Reveal)),
            SpinButtonPlacementMode = NumberBoxSpinButtonPlacementMode.Compact,
            SmallChange = max >= 1000 ? 10 : 1,
            ValidationMode = NumberBoxValidationMode.InvalidInputOverwritten,
            MinWidth = 140,
        };
        box.ValueChanged += (_, e) =>
        {
            if (double.IsNaN(e.NewValue))
            {
                box.Value = e.OldValue;
                return;
            }

            Save(s => s with { Reveal = set(s.Reveal, Math.Round(e.NewValue)) });
        };
        return box;
    }

    private UIElement HotkeyRow(HotkeyAction action)
    {
        var note = Note();
        var box = new TextBox { Text = store.Current.Hotkeys[action], PlaceholderText = "No shortcut", MinWidth = 200 };
        void Commit()
        {
            string current = store.Current.Hotkeys[action];
            string chord = Hotkey.TryParse(box.Text, out var parsed, out _) ? parsed.ToString() : box.Text.Trim();
            if (chord == current)
            {
                box.Text = chord;
                Show(note, null);
                return;
            }

            // Tomte's own process holds the chords it uses now, so only a new chord is probed.
            string? error = Hotkey.Validate(store.Current.Hotkeys, action, chord)
                ?? (chord.Length > 0 ? parsed.CheckFree(action) : null);
            Show(note, error);
            if (error is null)
            {
                box.Text = chord;
                Save(s => s with { Hotkeys = s.Hotkeys.With(action, chord) });
            }
        }

        box.LostFocus += (_, _) => Commit();
        box.KeyDown += (_, e) =>
        {
            if (e.Key == Windows.System.VirtualKey.Enter)
            {
                Commit();
                e.Handled = true;
            }
        };
        return Row(Hotkey.Name(action), note, box);
    }

    private static TextBlock CopilotNote() =>
        Note("Copilot key: Tomte does not offer itself for it yet. When it does, you will pick Tomte in Windows Settings > Bluetooth & devices > Keyboard, and Win+C will open Tomte as well.");

    private UIElement AddRuleRow()
    {
        var exe = new TextBox { PlaceholderText = "app.exe", MinWidth = 200 };
        AutomationProperties.SetName(exe, "App file name");
        var mode = ModeBox(AppRuleMode.Deny);
        AutomationProperties.SetName(mode, "Rule for the new app");
        var add = new Button { Content = "Add" };
        var note = Note();
        add.Click += (_, _) =>
        {
            string? error = AppRules.Validate(store.Current.AppRules, exe.Text);
            Show(note, error);
            if (error is null)
            {
                Save(s => s with { AppRules = [.. s.AppRules, new AppRule(AppRules.Normalize(exe.Text) + ".exe", (AppRuleMode)mode.SelectedIndex)] });
                exe.Text = "";
                RenderRules();
            }
        };
        var controls = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { exe, mode, add } };
        return new StackPanel { Spacing = 4, Margin = new Thickness(0, 8, 0, 0), Children = { controls, note } };
    }

    private void RenderRules()
    {
        rulesList.Children.Clear();
        if (store.Current.AppRules.Count == 0)
        {
            rulesList.Children.Add(Note("No rules yet."));
        }

        foreach (var rule in store.Current.AppRules)
        {
            var mode = ModeBox(rule.Mode);
            mode.SelectionChanged += (_, _) => Save(s => s with { AppRules = [.. s.AppRules.Select(r => SameApp(r, rule) ? r with { Mode = (AppRuleMode)mode.SelectedIndex } : r)] });
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) =>
            {
                Save(s => s with { AppRules = [.. s.AppRules.Where(r => !SameApp(r, rule))] });
                RenderRules();
            };
            rulesList.Children.Add(Row(rule.Exe, null, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { mode, remove } }));
        }
    }

    private static bool SameApp(AppRule a, AppRule b) => AppRules.Normalize(a.Exe) == AppRules.Normalize(b.Exe);

    private void RenderGrants()
    {
        grantsList.Children.Clear();
        if (store.Current.AlwaysAllowedTools.Count == 0)
        {
            grantsList.Children.Add(Note("No tool is always allowed. Every external effect asks first."));
        }

        foreach (string tool in store.Current.AlwaysAllowedTools)
        {
            var remove = new Button { Content = "Remove" };
            remove.Click += (_, _) =>
            {
                Save(s => s with { AlwaysAllowedTools = [.. s.AlwaysAllowedTools.Where(t => t != tool)] });
                RenderGrants();
            };
            // An MCP tool's grant is stored with a hash of what the tool was; the row shows the name only.
            grantsList.Children.Add(Row(McpServerSettings.NameOf(tool), Note("Runs without asking."), remove));
        }
    }

    private static UIElement ToolRow(ToolDefinition tool) =>
        Row(tool.Name, Note(tool.Description), Note(BuiltInTools.Tiers[tool.Name] switch
        {
            ActionTier.Read => "Read: runs on its own.",
            ActionTier.AppLocalWrite => "App-local write: runs on its own, can be undone.",
            ActionTier.ExternalEffect => "External effect: asks each time unless always allowed.",
            _ => "Destructive: always asks, never in scheduled jobs.",
        }));

    private static ComboBox ModeBox(AppRuleMode selected) =>
        new() { ItemsSource = new[] { "Allow", "Deny", "Deny with hotkeys" }, SelectedIndex = (int)selected, MinWidth = 180 };

    private async Task LoadStartupAsync()
    {
        try
        {
            startupTask = await StartupTask.GetAsync(StartupTaskId);
        }
        catch (Exception)
        {
            // Throws when the process has no package identity: an unpackaged development build.
            startupTask = null;
        }

        ShowStartup();
    }

    private async Task SetStartupAsync(bool on)
    {
        if (startupTask is null || on == (startupTask.State is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy))
        {
            return;
        }

        try
        {
            if (on)
            {
                await startupTask.RequestEnableAsync();
            }
            else
            {
                startupTask.Disable();
            }

            ShowStartup();
        }
        catch (Exception e)
        {
            // Called from an async void handler: an exception leaving here would end the settings process.
            ShowStartup();
            startupNote.Text = $"Windows did not change the startup setting: {e.Message}";
        }
    }

    private void ShowStartup()
    {
        var state = startupTask?.State;
        startup.IsOn = state is StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy;
        startup.IsEnabled = state is StartupTaskState.Enabled or StartupTaskState.Disabled;
        startupNote.Visibility = Visibility.Visible;
        startupNote.Text = state switch
        {
            null => "Only the installed version of Tomte can start with Windows. This copy runs without a package.",
            StartupTaskState.DisabledByUser => "Turned off in Task Manager. Turn it on again there, under Startup apps.",
            StartupTaskState.DisabledByPolicy => "Turned off by your organisation.",
            StartupTaskState.EnabledByPolicy => "Turned on by your organisation.",
            _ => "Open Tomte when you sign in. It stays hidden until you call it.",
        };
    }

    private static void Show(TextBlock note, string? error)
    {
        note.Text = error ?? "";
        note.Visibility = error is null ? Visibility.Collapsed : Visibility.Visible;
        note.Foreground = (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
    }

    private static TextBlock Header(string text) =>
        new() { Text = text, Style = (Style)Application.Current.Resources["BodyStrongTextBlockStyle"], Margin = new Thickness(4, 16, 0, 4) };

    private static TextBlock Note(string? text = null) => new()
    {
        Text = text ?? "",
        TextWrapping = TextWrapping.Wrap,
        Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"],
        Foreground = (Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
        Visibility = text is null ? Visibility.Collapsed : Visibility.Visible,
    };

    private static Border Card(params UIElement[] rows)
    {
        var panel = new StackPanel { Spacing = 12 };
        foreach (var row in rows)
        {
            panel.Children.Add(row);
        }

        return new Border
        {
            Child = panel,
            Padding = new Thickness(16),
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Background = (Brush)Application.Current.Resources["CardBackgroundFillColorDefaultBrush"],
            BorderBrush = (Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"],
        };
    }

    // Label and note on the left, the control on the right, as in Windows Settings.
    private static Grid Row(string label, TextBlock? note, FrameworkElement control)
    {
        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap } } };
        if (note is not null)
        {
            text.Children.Add(note);
        }

        control.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(control, label);
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    /// <summary>A code-only app: the metadata provider lets XamlControlsResources resolve the built-in control types.</summary>
    private sealed class SettingsApp : Application, IXamlMetadataProvider
    {
        private readonly XamlControlsXamlMetaDataProvider provider = new();

        public IXamlType GetXamlType(Type type) => provider.GetXamlType(type);

        public IXamlType GetXamlType(string fullName) => provider.GetXamlType(fullName);

        public XmlnsDefinition[] GetXmlnsDefinitions() => provider.GetXmlnsDefinitions();

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            var window = new SettingsWindow();
            window.Closed += (_, _) => Exit();
            window.Activate();
        }
    }
}
