using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.XamlTypeInfo;
using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Device;
using Tomte.Core.Rambler;
using Windows.ApplicationModel;
using Windows.Graphics;
using Windows.UI.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using VirtualKey = Windows.System.VirtualKey;

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

    /// <summary>The header of the section the shortcuts' add tile opens the window at.</summary>
    public const string ShortcutsHeader = "Shortcuts";

    private static string? settingsFile, startSection;
    private readonly SettingsStore store = new(settingsFile);
    private readonly StackPanel rulesList = new() { Spacing = 4 };
    private readonly StackPanel grantsList = new() { Spacing = 4 };
    private readonly ToggleSwitch startup = new() { OnContent = "On", OffContent = "Off" };
    private readonly TextBlock startupNote = Note();
    private readonly TextBlock clearedNote = Note();
    private readonly TextBlock voiceNote = Note();
    private StartupTask? startupTask;

    public SettingsWindow()
    {
        Title = "Tomte settings";
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "tomte.ico"));
        // Light or dark with Windows' app mode, and again when that changes; the window's content already follows it.
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.UseDefaultAppMode;
        double scale = PInvoke.GetDpiForWindow(new HWND(WinRT.Interop.WindowNative.GetWindowHandle(this))) / 96.0;
        // 960 x 820 DIPs where that fits, never taller or wider than the monitor's work area; each page scrolls inside.
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int width = Math.Min((int)(960 * scale), work.Width), height = Math.Min((int)(820 * scale), work.Height);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));

        var kept = new Tomte.Core.Clipboard.ClipboardRetention();
        // A page per group, listed on the left as in Windows Settings. Every control is made here, once, so a page left
        // and opened again shows what it showed.
        (string Name, string Glyph, UIElement[] Parts)[] pages =
        [
            ("General", "\uE713", [Card(
                Row("Start with Windows", startupNote, startup),
                Row("Desktop peek", Note("Show the tomte at the top of the screen when no window is there. Point at it to open the island."), Toggle(s => s.DesktopPeek, (s, v) => s with { DesktopPeek = v })),
                Row("Track changes", Note("Show the pill for a few seconds when another track starts playing."), Toggle(s => s.MediaTrackChangeEvent, (s, v) => s with { MediaTrackChangeEvent = v })),
                Row("Monitors", Note("Where an edge push or a drag can reveal the island."), Monitors()),
                Row("Island position", Note("Drag the island along the top edge to move it. Double-click an empty part of its tab row to put it back in the middle."), ResetPosition()))]),
            ("Opening and closing", "\uE962", [
                Card(
                    Row("Edge push", Note("Push the cursor up against the top edge to reveal the island."), RevealToggle(r => r.EdgePush, (r, v) => r with { EdgePush = v })),
                    Row("Dwell", Note("Resting in the reveal zone this many milliseconds also reveals it. 0 turns it off."), Number(0, 3000, r => r.DwellMs, (r, v) => r with { DwellMs = (int)v })),
                    Row("Open the panels", Note("Open the panels after resting on the pill, in milliseconds."), Number(RevealSettings.ExpandDwellMinMs, RevealSettings.ExpandDwellMaxMs, r => r.ExpandDwellMs, (r, v) => r with { ExpandDwellMs = (int)v })),
                    Row("Drag reveal", Note("Dragging a file to the top edge shows the shelf."), RevealToggle(r => r.DragReveal, (r, v) => r with { DragReveal = v }))),
                new Expander
                {
                    Header = "Advanced",
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    HorizontalContentAlignment = HorizontalAlignment.Stretch,
                    Content = new StackPanel
                    {
                        Spacing = 12,
                        Children =
                        {
                            Row("Reveal zone", Note("Width of the zone under the island along the top edge, in percent."), Number(10, 100, r => r.ZoneFraction * 100, (r, v) => r with { ZoneFraction = v / 100 })),
                            Row("Push strength, mouse", Note("Upward mouse movement needed, in raw counts. Raise it if the island opens by accident."), Number(10, 1000, r => r.PushCounts, (r, v) => r with { PushCounts = (int)v })),
                            Row("Push strength, touchpad", Note("The same for a precision touchpad."), Number(10, 1000, r => r.TouchpadPushCounts, (r, v) => r with { TouchpadPushCounts = (int)v })),
                            Row("Push time", Note("The push has to happen within this many milliseconds."), Number(100, 2000, r => r.PushWindowMs, (r, v) => r with { PushWindowMs = (int)v })),
                            Row("Drag distance", Note("How close to the top edge a drag has to come, in pixels at 100% scale."), Number(8, 200, r => r.DragApproachDip, (r, v) => r with { DragApproachDip = (int)v })),
                            Row("Drop wait", Note("Milliseconds to wait for a real drop before the shelf goes away."), Number(200, 3000, r => r.DragEnterTimeoutMs, (r, v) => r with { DragEnterTimeoutMs = (int)v })),
                            Row("Retract margin", Note("How far outside the island the cursor can go before it starts to retract, in pixels at 100% scale."), Number(0, 200, r => r.RetractMarginDip, (r, v) => r with { RetractMarginDip = (int)v })),
                            Row("Retract delay", Note("Milliseconds outside that margin before the island retracts."), Number(100, 5000, r => r.RetractDelayMs, (r, v) => r with { RetractDelayMs = (int)v })),
                        },
                    },
                },
                Header("Per-app rules"),
                Card(
                    Note("Allow: edge push and drag reveal work even when the app is fullscreen. Deny: only hotkeys open the island while the app is in front. Deny with hotkeys: the hotkeys go to the app as well."),
                    rulesList,
                    AddRuleRow())]),
            ("Hotkeys", "\uE765", [Card([.. Enum.GetValues<HotkeyAction>().Select(HotkeyRow), Note("The Copilot key cannot open Tomte yet.")])]),
            ("Shelf and shortcuts", "\uE7B8", [
                Header("Shelf"),
                Card(Row(
                    "Remove from the shelf after dragging out",
                    Note("When an app takes the drop, links to your files leave the shelf and the files stay where they are. Copies the shelf keeps (images, notes, attachments) stay, unless notes or images went out on their own."),
                    Toggle(s => s.RemoveAfterDragOut, (s, v) => s with { RemoveAfterDragOut = v }))),
                Header(ShortcutsHeader),
                ShortcutsSection()]),
            ("Clipboard", "\uE77F", [Card(Row(
                "Keep history",
                Note($"Keeps the last {kept.MaxItems} pieces of text and {kept.MaxImages} images you copy, on this PC, for up to {kept.MaxDays} days. Never kept: an image over {kept.MaxImageBytes >> 20} MB as a PNG, items password managers mark, and anything copied while Tomte is paused or an app with a deny rule is in front. Turning it off deletes the history."),
                Toggle(s => s.KeepClipboardHistory, (s, v) => s with { KeepClipboardHistory = v })))]),
            ("Voice", "\uE720", [Card(
                Row("When you finish speaking, Enter does", voiceNote, VoiceEnter()),
                Row("Spoken replies", Note("Read the answer to a voice command aloud with Windows speech."), Toggle(s => s.SpokenReplies, (s, v) => s with { SpokenReplies = v })))]),
            ("AI", "\uF4A5", [
                Header("AI provider"),
                ProviderSection(),
                Header("AI history"),
                Card(
                    Row("Keep history",
                        Note($"Keeps your last {AiHistory.MaxItems} AI conversations and drop zone results, on this PC, for up to {AiHistory.MaxDays} days: what you asked, the names of what you attached, the answers, the model and the cost. Attached content is never kept, and nothing leaves this PC. Turning it off deletes the history."),
                        KeepHistory()),
                    Row("Clear history", clearedNote, ClearHistory()))]),
            ("AI tools", "\uE90F", [
                Card(
                    Note("What the AI can do when you ask, once a provider is set up. A tool that asks each time can be always allowed from its prompt. By voice, every tool asks first, always allowed or not, and MCP server tools are not offered."),
                    Row("Desktop automation", Note("Let the typed palette list, click, type into and select controls in other apps. Every action asks first and none can be always allowed. Never by voice."),
                        Toggle(s => s.UiAutomation.Enabled, (s, v) => s with { UiAutomation = s.UiAutomation with { Enabled = v } }))),
                Header("Always allowed"),
                Card(grantsList),
                Header("Scheduled jobs"),
                JobsSection(),
                Header("All tools"),
                Card([.. BuiltInTools.All.Select(ToolRow)]),
                Header("MCP servers"),
                McpSection(),
                Header("Skills"),
                SkillsSection()]),
        ];

        var nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            OpenPaneLength = 240,
            IsPaneToggleButtonVisible = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
        };
        NavigationViewItem? open = null;
        TextBlock? section = null;
        foreach (var (name, glyph, parts) in pages)
        {
            var page = new StackPanel { Spacing = 8, MaxWidth = 760, Padding = new Thickness(24, 0, 24, 32) };
            page.Children.Add(new TextBlock { Text = name, Style = (Style)Application.Current.Resources["TitleTextBlockStyle"], Margin = new Thickness(4, 28, 0, 8) });
            foreach (var part in parts)
            {
                page.Children.Add(part);
            }

            var item = new NavigationViewItem { Content = name, Icon = new FontIcon { Glyph = glyph }, Tag = new ScrollViewer { Content = page } };
            nav.MenuItems.Add(item);
            if (section is null && page.Children.OfType<TextBlock>().FirstOrDefault(h => h.Text == startSection) is { } found)
            {
                (open, section) = (item, found);
            }
        }

        nav.SelectionChanged += (_, e) => nav.Content = ((NavigationViewItem)e.SelectedItem).Tag;
        open ??= (NavigationViewItem)nav.MenuItems[0];
        nav.SelectedItem = open;
        nav.Content = open.Tag;
        Content = nav;
        if (section is not null)
        {
            // Once laid out, the section's header goes to the top of its page; only the first time the page shows.
            var scroller = (ScrollViewer)open.Tag;
            void Bring(object sender, RoutedEventArgs e)
            {
                scroller.Loaded -= Bring;
                section.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0, VerticalOffset = 28, AnimationDesired = false });
            }

            scroller.Loaded += Bring;
        }

        RenderRules();
        RenderGrants();
        RenderMcpServers();
        _ = RenderSkillsAsync();
        _ = RenderJobsAsync();
        _ = LoadStartupAsync();
        startup.Toggled += async (_, _) => await SetStartupAsync(startup.IsOn);
        Closed += (_, _) => store.Dispose();
        // A settings window that ended while a shortcut box recorded left Tomte's hotkeys paused; no box records yet.
        Hotkey.Pause(DataFolder, false);
    }

    /// <summary>
    /// Starts WinUI on this thread, shows the window and returns when it closes. <paramref name="settingsPath"/> is the
    /// shell's settings file (null for the default); tomte.db is the one next to it. <paramref name="section"/> is the
    /// header of a section to open at, such as <see cref="ShortcutsHeader"/>, on its page; null opens the first page.
    /// </summary>
    public static void Run(string? settingsPath = null, string? section = null)
    {
        (settingsFile, startSection) = (settingsPath, section);
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

    // Insert, Ask Tomte or Add to note, in VoiceChoice order. Ask is off while no provider is set up, and the box shows
    // Insert then without changing what is saved, so Ask comes back with a provider. It follows a provider set up or
    // removed while the window is open.
    private ComboBox VoiceEnter()
    {
        var ask = new ComboBoxItem { Content = VoiceChoices.Label(VoiceChoice.Ask) };
        var box = new ComboBox { MinWidth = 200, Items = { VoiceChoices.Label(VoiceChoice.Insert), ask, VoiceChoices.Label(VoiceChoice.Note) } };
        bool showing = false;
        void Show(TomteSettings s)
        {
            bool canAsk = s.Provider is { DefaultModel.Length: > 0 };
            showing = true;
            ask.IsEnabled = canAsk;
            box.SelectedIndex = (int)VoiceChoices.Default(s.VoiceEnter, canAsk);
            showing = false;
            voiceNote.Text = (canAsk ? "" : VoiceChoices.NoProvider + " Set one up on the AI page. ")
                + "After you speak, Alt+I, Alt+A and Alt+N pick Insert, Ask Tomte and Add to note, and Esc throws the words away.";
            voiceNote.Visibility = Visibility.Visible;
        }

        Show(store.Current);
        box.SelectionChanged += (_, _) =>
        {
            if (!showing && box.SelectedIndex >= 0)
            {
                Save(s => s with { VoiceEnter = (VoiceChoice)box.SelectedIndex });
            }
        };
        store.Changed += s => DispatcherQueue.TryEnqueue(() => Show(s));
        return box;
    }

    // Turning the history off deletes it here too, so it is gone even when the shell is not running.
    private ToggleSwitch KeepHistory()
    {
        var keep = Toggle(s => s.KeepAiHistory, (s, v) => s with { KeepAiHistory = v });
        keep.Toggled += async (_, _) =>
        {
            if (!keep.IsOn)
            {
                await ClearAiHistory();
            }
        };
        return keep;
    }

    private Button ClearHistory()
    {
        var clear = new Button { Content = "Clear history" };
        clear.Click += async (_, _) =>
        {
            clear.IsEnabled = false;
            await ClearAiHistory();
            clear.IsEnabled = true;
        };
        return clear;
    }

    // Deletes every kept conversation and drop zone result in the tomte.db beside the settings file, off the UI thread.
    private async Task ClearAiHistory()
    {
        var db = new Tomte.Core.Storage.TomteDb(Path.Combine(Path.GetDirectoryName(store.FilePath)!, "tomte.db"));
        // The failure line comes from the pool; the note is set back on this thread.
        string? failed = null;
        await new AiHistory(db, () => true, line => failed = line).Clear();
        clearedNote.Text = failed is null ? "Cleared." : "Not cleared: tomte.db could not be opened. Try again in a moment.";
        clearedNote.Visibility = Visibility.Visible;
    }

    private Button ResetPosition()
    {
        var reset = new Button { Content = "Reset position" };
        reset.Click += (_, _) => Save(s => s with { IslandOffset = 0 });
        return reset;
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

    // The box records the keys pressed instead of taking text. While it has focus in the active window, Tomte's own
    // hotkeys are paused so a chord Tomte uses reaches it; they come back, and the box shows the saved chord again, when
    // it loses focus or the window goes to the background or closes.
    private UIElement HotkeyRow(HotkeyAction action)
    {
        const string Hint = "Press the keys. Backspace clears it.";
        var note = Note();
        // Read-only, so typed characters never land in it; the key handlers below take every key but Tab first.
        var box = new TextBox { Text = store.Current.Hotkeys[action], PlaceholderText = "No shortcut", MinWidth = 200, IsReadOnly = true };
        AutomationProperties.SetHelpText(box, Hint);
        bool recording = false, previewing = false;

        void Tell(string? error)
        {
            note.Text = error ?? Hint;
            note.Visibility = Visibility.Visible;
            note.Foreground = (Brush)Application.Current.Resources[error is null ? "TextFillColorSecondaryBrush" : "SystemFillColorCriticalBrush"];
            if (error is not null)
            {
                // Said aloud too: a screen reader stays on the box, not on the note.
                FrameworkElementAutomationPeer.FromElement(box)?.RaiseNotificationEvent(AutomationNotificationKind.Other, AutomationNotificationProcessing.ImportantMostRecent, error, "HotkeyNote");
            }
        }

        void Restore()
        {
            previewing = false;
            box.Text = store.Current.Hotkeys[action];
        }

        void Commit(string chord)
        {
            previewing = false;
            box.Text = chord;
            if (chord == store.Current.Hotkeys[action])
            {
                Tell(null);
                return;
            }

            // Tomte's own process holds the chords it uses now, so only a new chord is probed.
            string? error = Hotkey.Validate(store.Current.Hotkeys, action, chord)
                ?? (Hotkey.TryParse(chord, out var parsed, out _) ? parsed.CheckFree(action) : null);
            Tell(error);
            if (error is null)
            {
                Save(s => s with { Hotkeys = s.Hotkeys.With(action, chord) });
            }
        }

        void Recording(bool on)
        {
            if (on == recording)
            {
                return;
            }

            recording = on;
            Hotkey.Pause(DataFolder, on);
            Restore();
            if (on)
            {
                Tell(null);
            }
            else if (note.Text == Hint)
            {
                Show(note, null);
            }
        }

        box.GotFocus += (_, _) => Recording(true);
        box.LostFocus += (_, _) => Recording(false);
        // A window in the background gets no keys; Win alone opens Start and lands here too.
        Activated += (_, e) => Recording(e.WindowActivationState != WindowActivationState.Deactivated && box.FocusState != FocusState.Unfocused);
        Closed += (_, _) => Recording(false);
        box.PreviewKeyDown += (_, e) =>
        {
            var (press, text) = Hotkey.Record((uint)e.Key, Held());
            e.Handled = press != HotkeyPress.Pass;
            switch (press)
            {
                case HotkeyPress.Preview:
                    previewing = true;
                    box.Text = text;
                    break;
                case HotkeyPress.Chord or HotkeyPress.Clear:
                    Commit(text);
                    break;
                case HotkeyPress.Restore:
                    Restore();
                    Tell(null);
                    break;
                case HotkeyPress.Unusable:
                    Tell("Tomte cannot use that key. Use a letter, a digit, F1 to F24 or Space.");
                    break;
            }
        };
        // Letting go of the last modifier with no key pressed puts the saved chord back.
        box.PreviewKeyUp += (_, e) =>
        {
            e.Handled = e.Key != VirtualKey.Tab;
            if (previewing)
            {
                var held = Held();
                previewing = held != HotkeyModifiers.None;
                box.Text = previewing ? Hotkey.Preview(held) : store.Current.Hotkeys[action];
            }
        };
        return Row(Hotkey.Name(action), note, box);
    }

    // The modifiers down as the key event being handled came in, from this thread's keyboard state, so a key-up that
    // went to another window leaves nothing held here.
    private static HotkeyModifiers Held()
    {
        static bool Down(VirtualKey key) => InputKeyboardSource.GetKeyStateForCurrentThread(key).HasFlag(CoreVirtualKeyStates.Down);
        return (Down(VirtualKey.LeftWindows) || Down(VirtualKey.RightWindows) ? HotkeyModifiers.Win : HotkeyModifiers.None)
            | (Down(VirtualKey.Control) ? HotkeyModifiers.Ctrl : HotkeyModifiers.None)
            | (Down(VirtualKey.Menu) ? HotkeyModifiers.Alt : HotkeyModifiers.None)
            | (Down(VirtualKey.Shift) ? HotkeyModifiers.Shift : HotkeyModifiers.None);
    }

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
