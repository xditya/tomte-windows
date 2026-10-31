using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Input;
using Microsoft.UI.Text;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.XamlTypeInfo;
using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Device;
using Tomte.Core.Rambler;
using Tomte.Core.Triggers;
using Windows.ApplicationModel;
using Windows.Graphics;
using Windows.UI.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Color = Windows.UI.Color;
using Colors = Microsoft.UI.Colors;
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

    // The process's one settings window, for the message procedure, which Windows calls with no object.
    private static SettingsWindow? shown;
    private static Microsoft.UI.Dispatching.DispatcherQueue? shownQueue;
    private readonly SettingsStore store = new(settingsFile);
    private readonly NavigationView nav;
    private readonly StackPanel rulesList = new() { Spacing = 4 };
    private readonly StackPanel grantsList = new() { Spacing = 4 };
    private readonly ToggleSwitch startup = new() { OnContent = "On", OffContent = "Off", MinWidth = 0 };
    private readonly TextBlock startupNote = Note();
    private readonly TextBlock clearedNote = Note();
    private readonly TextBlock voiceNote = Note();
    private StartupTask? startupTask;

    public SettingsWindow()
    {
        Title = "Tomte settings";
        SystemBackdrop = new MicaBackdrop { Kind = MicaKind.Base };
        AppWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "tomte.ico"));
        // Always dark, like the island, whatever Windows' app mode is.
        AppWindow.TitleBar.PreferredTheme = TitleBarTheme.Dark;
        double scale = PInvoke.GetDpiForWindow(new HWND(WinRT.Interop.WindowNative.GetWindowHandle(this))) / 96.0;
        // 1200 x 800 DIPs where that fits, never taller or wider than the monitor's work area; each page scrolls inside.
        var work = DisplayArea.GetFromWindowId(AppWindow.Id, DisplayAreaFallback.Nearest).WorkArea;
        int width = Math.Min((int)(1200 * scale), work.Width), height = Math.Min((int)(800 * scale), work.Height);
        AppWindow.MoveAndResize(new RectInt32(work.X + (work.Width - width) / 2, work.Y + (work.Height - height) / 2, width, height));
        FollowAccent();

        var kept = new Tomte.Core.Clipboard.ClipboardRetention();
        // A page per group, listed on the left as in Windows Settings, under a header for each group of pages. Every control
        // is made here, once, so a page left and opened again shows what it showed.
        (string Group, (string Name, string Glyph, string Lede, UIElement[] Parts)[] Pages)[] groups =
        [
            ("Everyday", [
                ("Opening and closing", "\uE962", "How the island shows itself, when it goes away, and how it looks.", [
                    Card(
                        Row("Edge push", Note("Push the cursor up against the top edge to reveal the island."), RevealToggle(r => r.EdgePush, (r, v) => r with { EdgePush = v })),
                        Row("Dwell", Note("Resting in the reveal zone this many milliseconds also reveals it. 0 turns it off."), Number(0, 3000, r => r.DwellMs, (r, v) => r with { DwellMs = (int)v })),
                        Row("Open the panels", Note("Open the panels after resting on the pill, in milliseconds."), Number(RevealSettings.ExpandDwellMinMs, RevealSettings.ExpandDwellMaxMs, r => r.ExpandDwellMs, (r, v) => r with { ExpandDwellMs = (int)v })),
                        Row("Drag reveal", Note("Dragging a file to the top edge shows the shelf."), RevealToggle(r => r.DragReveal, (r, v) => r with { DragReveal = v }))),
                    Advanced(
                        Row("Reveal zone", Note("Width of the zone under the island along the top edge, in percent."), Number(10, 100, r => r.ZoneFraction * 100, (r, v) => r with { ZoneFraction = v / 100 })),
                        Row("Push strength, mouse", Note("Upward mouse movement needed, in raw counts. Raise it if the island opens by accident."), Number(10, 1000, r => r.PushCounts, (r, v) => r with { PushCounts = (int)v })),
                        Row("Push strength, touchpad", Note("The same for a precision touchpad."), Number(10, 1000, r => r.TouchpadPushCounts, (r, v) => r with { TouchpadPushCounts = (int)v })),
                        Row("Push time", Note("The push has to happen within this many milliseconds."), Number(100, 2000, r => r.PushWindowMs, (r, v) => r with { PushWindowMs = (int)v })),
                        Row("Drag distance", Note("How close to the top edge a drag has to come, in pixels at 100% scale."), Number(8, 200, r => r.DragApproachDip, (r, v) => r with { DragApproachDip = (int)v })),
                        Row("Drop wait", Note("Milliseconds to wait for a real drop before the shelf goes away."), Number(200, 3000, r => r.DragEnterTimeoutMs, (r, v) => r with { DragEnterTimeoutMs = (int)v })),
                        Row("Retract margin", Note("How far outside the island the cursor can go before it starts to retract, in pixels at 100% scale."), Number(0, 200, r => r.RetractMarginDip, (r, v) => r with { RetractMarginDip = (int)v })),
                        Row("Retract delay", Note("Milliseconds outside that margin before the island retracts."), Number(100, 5000, r => r.RetractDelayMs, (r, v) => r with { RetractDelayMs = (int)v }))),
                    Header("General"),
                    Card(
                        Row("Start with Windows", startupNote, startup),
                        StackedRow("Desktop peek", Note("How Tomte rests at the top of the screen when no window is there: perched on the edge, an edge lip, the pill, or a small pill in a corner, clear of a clock or widget in the middle. Point at it to open the island."), PeekStyles(), 14),
                        Row("Track changes", Note("Show the pill for a few seconds when another track starts playing."), Toggle(s => s.MediaTrackChangeEvent, (s, v) => s with { MediaTrackChangeEvent = v })),
                        Row("Keep showing while music plays", Note("Keep the pill at the top of the screen with what is playing. It goes 5 seconds after the music pauses or stops, and still hides over fullscreen apps."), Toggle(s => s.KeepWhilePlaying, (s, v) => s with { KeepWhilePlaying = v })),
                        Row("Monitors", Note("Where an edge push or a drag can reveal the island."), Monitors()),
                        Row("Island position", Note("Drag the island along the top edge to move it. Double-click an empty part of its tab row to put it back in the middle."), ResetPosition())),
                    Header("Appearance"),
                    Appearance(),
                    Header("Per-app rules"),
                    Card(
                        Note("Allow: edge push and drag reveal work even when the app is fullscreen. Deny: only hotkeys open the island while the app is in front. Deny with hotkeys: the hotkeys go to the app as well."),
                        rulesList,
                        AddRuleRow())]),
                ("Hotkeys", "\uE765", "Keys that open Tomte from any app. Click a box, then press the keys.", [Card([.. Enum.GetValues<HotkeyAction>().Select(HotkeyRow), Note("The Copilot key cannot open Tomte yet.")])]),
                ("Search", "\uE721", "Where the search box looks for files, and what it searches the web with.", [SearchSection()]),
                ("Shelf and shortcuts", "\uE7B8", "The files you keep on the shelf and the tiles in the Shortcuts tab.", [
                    Header("Shelf"),
                    Card(Row(
                        "Remove from the shelf after dragging out",
                        Note("When an app takes the drop, links to your files leave the shelf and the files stay where they are. Copies the shelf keeps (images, notes, attachments) stay, unless notes or images went out on their own. Pinned items always stay."),
                        Toggle(s => s.RemoveAfterDragOut, (s, v) => s with { RemoveAfterDragOut = v }))),
                    Header(ShortcutsHeader),
                    ShortcutsSection()]),
                ("Clipboard", "\uE77F", "What Tomte keeps of what you copy. It stays on this PC.", [Card(Row(
                    "Keep history",
                    Note($"Keeps the last {kept.MaxItems} pieces of text and {kept.MaxImages} images you copy, on this PC, for up to {kept.MaxDays} days. Never kept: an image over {kept.MaxImageBytes >> 20} MB as a PNG, items password managers mark, and anything copied while Tomte is paused or an app with a deny rule is in front. Turning it off deletes the history."),
                    Toggle(s => s.KeepClipboardHistory, (s, v) => s with { KeepClipboardHistory = v })))]),
                ("Voice", "\uE720", "What happens to the words you say after a hold.", [Card(
                    Row("When you finish speaking, Enter does", voiceNote, VoiceEnter()),
                    Row("Spoken replies", Note("Read the answer to a voice command aloud with Windows speech."), Toggle(s => s.SpokenReplies, (s, v) => s with { SpokenReplies = v })))]),
            ]),
            ("AI", [
                (ProviderPage, "\uF4A5", "Tomte sends a request only when you ask, or when a job you made runs. With no provider set up, the AI layer stays off.", ProviderSection()),
                ("Tools and permissions", "\uE90F", "What the AI may do on this PC, and what it asks you first.", [
                    Card(
                        Note("What the AI can do when you ask, once a provider is set up. A tool that asks each time can be always allowed from its prompt. By voice, every tool asks first, always allowed or not, and MCP server tools are not offered."),
                        Row("Desktop automation", Note("Let the typed palette list, click, type into and select controls in other apps. Every action asks first and none can be always allowed. Never by voice."),
                            Toggle(s => s.UiAutomation.Enabled, (s, v) => s with { UiAutomation = s.UiAutomation with { Enabled = v } }))),
                    Header("Always allowed"),
                    Card(grantsList),
                    Header("All tools"),
                    Card([.. BuiltInTools.All.Select(ToolRow)])]),
                ("Jobs", "\uE823", "Requests you scheduled in the AI box or by voice.", [JobsSection()]),
                ("MCP servers", "\uE839", "Other programs that give the AI more tools.", [McpSection()]),
                ("Skills", "\uE8A5", "Folders of instructions the AI reads when a request fits them.", [SkillsSection()]),
            ]),
        ];

        nav = new NavigationView
        {
            PaneDisplayMode = NavigationViewPaneDisplayMode.Left,
            OpenPaneLength = 268,
            IsPaneToggleButtonVisible = false,
            IsBackButtonVisible = NavigationViewBackButtonVisible.Collapsed,
            IsSettingsVisible = false,
            RequestedTheme = ElementTheme.Dark,
            PaneCustomContent = Finder(),
        };
        // Up and Down in the page list open each page as they reach it, so Tab goes on into the page that shows. Tab and
        // Shift+Tab come back into the list at the open page, not at the first one, and open nothing; Windows does not
        // let every such move be redirected (Tab past the last control comes back in from outside), so that one hops on.
        nav.GettingFocus += (_, e) =>
        {
            if (e.FocusState != FocusState.Keyboard || e.NewFocusedElement is not NavigationViewItem to || nav.SelectedItem is not NavigationViewItem page)
            {
                return;
            }

            if (e.OldFocusedElement is NavigationViewItem)
            {
                nav.SelectedItem = to;
            }
            else if (!ReferenceEquals(to, page) && !e.TrySetNewFocusedElement(page))
            {
                DispatcherQueue.TryEnqueue(() => page.Focus(FocusState.Keyboard));
            }
        };
        foreach (var (group, members) in groups)
        {
            nav.MenuItems.Add(PaneText(group.ToUpperInvariant(), 30, 80));
            foreach (var (name, glyph, lede, parts) in members)
            {
                nav.MenuItems.Add(PageItem(name, glyph, lede, parts));
            }
        }

        // About sits at the foot of the list, apart from the groups, and the promise under it.
        nav.FooterMenuItems.Add(PageItem("About", "\uE946", "Who makes Tomte, and what it is made with.", AboutPage()));
        nav.FooterMenuItems.Add(PaneText("No account, no telemetry.\nEverything stays in one file on this PC.", 44, 0));
        nav.SelectionChanged += (_, e) => Open((NavigationViewItem)e.SelectedItem);
        Open(PageItems.First());
        if (startSection is not null)
        {
            GoTo(startSection);
        }

        // The open page's item has focus from the start, so the arrow keys move through the pages at once.
        nav.Loaded += (_, _) => ((NavigationViewItem)nav.SelectedItem).Focus(FocusState.Programmatic);
        Content = nav;
        // The shell asks this window, once open, for another page: a search row for one of Tomte's settings pages.
        shown = this;
        shownQueue = DispatcherQueue;
        Subclass(WinRT.Interop.WindowNative.GetWindowHandle(this));

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

    // Every page in the list, the groups' then About.
    private IEnumerable<NavigationViewItem> PageItems => nav.MenuItems.Concat(nav.FooterMenuItems).OfType<NavigationViewItem>();

    private static StackPanel PageOf(NavigationViewItem item) => (StackPanel)((ScrollViewer)item.Tag).Content;

    // The page's title and lede, then its parts, in a column that scrolls.
    private static NavigationViewItem PageItem(string name, string glyph, string lede, UIElement[] parts)
    {
        var page = new StackPanel { Spacing = 12, MaxWidth = 932, Padding = new Thickness(40, 32, 40, 40) };
        page.Children.Add(new StackPanel
        {
            Spacing = 6,
            Margin = new Thickness(0, 0, 0, 8),
            Children =
            {
                new TextBlock { Text = name, FontFamily = new FontFamily("Segoe UI Variable Display"), FontSize = 26, FontWeight = FontWeights.SemiBold, CharacterSpacing = -20, Foreground = Palette.Brush(Palette.Ink) },
                new TextBlock { Text = lede, FontSize = 13, TextWrapping = TextWrapping.Wrap, Foreground = Palette.Brush(Palette.Ink2) },
            },
        });
        foreach (var part in parts)
        {
            page.Children.Add(part);
        }

        return new NavigationViewItem { Content = name, FontSize = 13, Icon = new FontIcon { Glyph = glyph, FontSize = 16 }, Tag = new ScrollViewer { Content = page } };
    }

    // Small text in the page list that is not a page: a group's name above its pages, or the line at the foot. A header
    // item, so the list skips it with the keyboard and never selects it; its template sets the header type, so the type
    // is set again on its text once it is made.
    private static NavigationViewItemHeader PaneText(string text, double height, int spacing)
    {
        var item = new NavigationViewItemHeader { Content = text, Foreground = Palette.Brush(Palette.Ink3) };
        AutomationProperties.SetAccessibilityView(item, AccessibilityView.Content);
        item.Loaded += (_, _) =>
        {
            if (Descendant<TextBlock>(item) is { } shown)
            {
                shown.FontSize = 11;
                shown.FontWeight = FontWeights.Normal;
                shown.CharacterSpacing = spacing;
                shown.LineHeight = 16;
                ((FrameworkElement)shown.Parent).Height = height;
            }
        };
        return item;
    }

    // Shows the item's page and marks it, the open page's name a little heavier than the rest.
    private void Open(NavigationViewItem item)
    {
        nav.SelectedItem = item;
        nav.Content = item.Tag;
        foreach (var other in PageItems)
        {
            other.FontWeight = ReferenceEquals(other, item) ? FontWeights.Medium : FontWeights.Normal;
        }
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

    // The page whose title is this text, or that has a section of this name, opens, and once laid out the title or the
    // section goes to the top of the page. Text that no page has changes nothing.
    private void GoTo(string section)
    {
        foreach (var item in PageItems)
        {
            var scroller = (ScrollViewer)item.Tag;
            var page = PageOf(item);
            if (((string)item.Content == section ? page.Children[0] as FrameworkElement : SectionIn(page, section)) is not { } header)
            {
                continue;
            }

            // Before the window is laid out the list may not raise its change yet, so the page is set here too.
            Open(item);
            var options = new BringIntoViewOptions { VerticalAlignmentRatio = 0, VerticalOffset = 28, AnimationDesired = false };
            if (scroller.IsLoaded)
            {
                header.StartBringIntoView(options);
                return;
            }

            void Bring(object sender, RoutedEventArgs e)
            {
                scroller.Loaded -= Bring;
                header.StartBringIntoView(options);
            }

            scroller.Loaded += Bring;
            return;
        }
    }

    // A header on the page, or a part marked as a section of its own (a card with no header, alone or in a row of cards).
    private static FrameworkElement? SectionIn(StackPanel page, string section)
    {
        static bool Marked(UIElement part, string name) => part is FrameworkElement { Tag: SectionName marked } && marked.Name == name;
        foreach (var part in page.Children.Skip(1))
        {
            if ((part is TextBlock header && header.Text == section) || Marked(part, section))
            {
                return (FrameworkElement)part;
            }

            if (part is Grid { Tag: null } cards && cards.Children.FirstOrDefault(c => Marked(c, section)) is FrameworkElement found)
            {
                return found;
            }
        }

        return null;
    }

    // A static procedure, so there is no delegate for the collector to take while Windows still calls it.
    private static unsafe void Subclass(nint window) => PInvoke.SetWindowSubclass(new HWND(window), &OnMessage, 1, 0);

    // The window's messages before WinUI's: the shell's request for a page, read and checked by SettingsSection, is
    // handled on the next turn; everything else goes on.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT OnMessage(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam, nuint id, nuint data)
    {
        // An exception here would end the process, so nothing is read off the window object.
        if (message == PInvoke.WM_COPYDATA && shownQueue is { } queue && SettingsSection.TryRead(lParam, out string section))
        {
            queue.TryEnqueue(() => shown?.GoTo(section));
            return new LRESULT(1);
        }

        return PInvoke.DefSubclassProc(hwnd, message, wParam, lParam);
    }

    private void Save(Func<TomteSettings, TomteSettings> change) => store.Save(change(store.Current));

    private ToggleSwitch Toggle(Func<TomteSettings, bool> get, Func<TomteSettings, bool, TomteSettings> set)
    {
        var toggle = new ToggleSwitch { IsOn = get(store.Current), OnContent = "On", OffContent = "Off", MinWidth = 0 };
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
            voiceNote.Text = (canAsk ? "" : VoiceChoices.NoProvider + $" Set one up on the {ProviderPage} page. ")
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

    // Off, 5 seconds or 10 seconds; a hand-edited number shows as the choice it acts as.
    private Border UndoAfterClosing()
    {
        int[] seconds = [0, 5, 10];
        int current = (int)(UndoClose.Time(store.Current.UndoSeconds)?.TotalSeconds ?? 0);
        var (track, choices) = Segmented([.. seconds.Select(v => v == 0 ? "Off" : $"{v} s")], Array.IndexOf(seconds, current), i => Save(s => s with { UndoSeconds = seconds[i] }));
        for (int i = 1; i < seconds.Length; i++)
        {
            AutomationProperties.SetName(choices[i], $"{seconds[i]} seconds");
        }

        return track;
    }

    // Choices as buttons side by side on a track, the chosen one raised. A click on the chosen one leaves it chosen.
    private (Border Track, List<ToggleButton> Choices) Segmented(string[] labels, int chosen, Action<int> pick)
    {
        var bar = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 2 };
        var choices = new List<ToggleButton>();
        for (int i = 0; i < labels.Length; i++)
        {
            int index = i;
            var choice = new ToggleButton { Content = labels[i], IsChecked = i == chosen, Height = 30, MinWidth = 0, Padding = new Thickness(14, 0, 14, 0), CornerRadius = new CornerRadius(8), FontSize = 12 };
            AutomationProperties.SetName(choice, labels[i]);
            choice.Click += (_, _) =>
            {
                choices.ForEach(c => c.IsChecked = ReferenceEquals(c, choice));
                pick(index);
            };
            choices.Add(choice);
            bar.Children.Add(choice);
        }

        var track = new Border { Child = bar, Padding = new Thickness(3), CornerRadius = new CornerRadius(10), Background = Palette.Brush(Palette.Field), HorizontalAlignment = HorizontalAlignment.Left };
        Look(track, "ToggleButton", "", Colors.Transparent, Palette.Hover, Palette.Ink2);
        Look(track, "ToggleButton", "Checked", Palette.RaisedHi, Palette.RaisedHi, Palette.Ink);
        return (track, choices);
    }

    // Turning the history off deletes it here too, so it is gone even when the shell is not running.
    private CheckBox KeepHistory()
    {
        var keep = new CheckBox { IsChecked = store.Current.KeepAiHistory, MinWidth = 0 };
        keep.Click += async (_, _) =>
        {
            bool on = keep.IsChecked == true;
            Save(s => s with { KeepAiHistory = on });
            if (!on)
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
            note.Foreground = error is null ? Palette.Brush(Palette.Ink3) : (Brush)Application.Current.Resources["SystemFillColorCriticalBrush"];
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
                    Tell("Tomte cannot use that key. Use a letter, a digit on the top row, F1 to F24 or Space.");
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
        // The box takes what the mode and Add leave, so a long name scrolls inside it instead of pushing Add out of
        // the card.
        var controls = new Grid { ColumnSpacing = 8 };
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        controls.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(mode, 1);
        Grid.SetColumn(add, 2);
        controls.Children.Add(exe);
        controls.Children.Add(mode);
        controls.Children.Add(add);
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
        new() { Text = text, FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = Palette.Brush(Palette.Ink), Margin = new Thickness(2, 14, 0, 0) };

    private static TextBlock Note(string? text = null) => new()
    {
        Text = text ?? "",
        TextWrapping = TextWrapping.Wrap,
        FontSize = 12,
        LineHeight = 17,
        Foreground = Palette.Brush(Palette.Ink3),
        Visibility = text is null ? Visibility.Collapsed : Visibility.Visible,
    };

    // A rounded card with a line between its rows.
    private static Border Card(params UIElement[] rows) => new()
    {
        Child = Cells(rows),
        CornerRadius = new CornerRadius(14),
        BorderThickness = new Thickness(1),
        Background = Palette.Brush(Palette.Card),
        BorderBrush = Palette.Brush(Palette.Line),
    };

    // Each row padded, with a line above every row but the first. A row's place hides with the row, so a hidden row
    // leaves no gap or extra line.
    private static StackPanel Cells(UIElement[] rows)
    {
        var panel = new StackPanel();
        foreach (var row in rows)
        {
            var cell = new Border
            {
                Child = row,
                Padding = new Thickness(18, 14, 18, 14),
                BorderThickness = new Thickness(0, panel.Children.Count == 0 ? 0 : 1, 0, 0),
                BorderBrush = Palette.Brush(Palette.Line),
                Visibility = row.Visibility,
            };
            row.RegisterPropertyChangedCallback(UIElement.VisibilityProperty, (_, _) => cell.Visibility = row.Visibility);
            panel.Children.Add(cell);
        }

        return panel;
    }

    // The fine-tuning, folded away under the card it tunes, in the same card look.
    private static Expander Advanced(params UIElement[] rows)
    {
        var expander = new Expander
        {
            Header = "Advanced",
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            CornerRadius = new CornerRadius(14),
            Content = Cells(rows),
        };
        expander.Resources["ExpanderHeaderBackground"] = expander.Resources["ExpanderContentBackground"] = Palette.Brush(Palette.Card);
        expander.Resources["ExpanderHeaderBorderBrush"] = expander.Resources["ExpanderContentBorderBrush"] = Palette.Brush(Palette.Line);
        expander.Resources["ExpanderContentPadding"] = new Thickness(0);
        return expander;
    }

    // Label and note on the left, the control on the right, as in Windows Settings; a box or a list is 300 wide. The row
    // keeps its parts, so Find a setting reads the rows the pages show.
    private static Grid Row(string label, TextBlock? note, FrameworkElement control)
    {
        var title = new TextBlock { Text = label, TextWrapping = TextWrapping.Wrap, FontSize = 14, FontWeight = FontWeights.Medium, Foreground = Palette.Brush(Palette.Ink) };
        var grid = new Grid { ColumnSpacing = 24, Tag = new RowParts(title, note, control) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var text = new StackPanel { Spacing = 3, VerticalAlignment = VerticalAlignment.Center, Children = { title } };
        if (note is not null)
        {
            text.Children.Add(note);
        }

        if (control is TextBox or PasswordBox or ComboBox or NumberBox)
        {
            control.Width = 300;
        }

        control.VerticalAlignment = VerticalAlignment.Center;
        AutomationProperties.SetName(control, label);
        Grid.SetColumn(control, 1);
        grid.Children.Add(text);
        grid.Children.Add(control);
        return grid;
    }

    // A row whose control goes under its text, at the left, for a control too wide to sit beside it.
    private static Grid StackedRow(string label, TextBlock? note, FrameworkElement control, double spacing = 12)
    {
        var row = Row(label, note, control);
        row.RowSpacing = spacing;
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        control.HorizontalAlignment = HorizontalAlignment.Left;
        Grid.SetRow(control, 1);
        Grid.SetColumn(control, 0);
        Grid.SetColumnSpan(control, 2);
        return row;
    }

    // Colours for a control's look at rest, under the pointer and pressed, set as the resources its template reads, so the
    // template's states keep working. State is "" or "Checked", for a toggle button that is on.
    private static void Look(FrameworkElement scope, string control, string state, Color fill, Color hover, Color text)
    {
        foreach (var (pointer, color) in new[] { ("", fill), ("PointerOver", hover), ("Pressed", hover) })
        {
            scope.Resources[$"{control}Background{state}{pointer}"] = Palette.Brush(color);
            scope.Resources[$"{control}Foreground{state}{pointer}"] = Palette.Brush(text);
            scope.Resources[$"{control}BorderBrush{state}{pointer}"] = Palette.Brush(Colors.Transparent);
        }
    }

    /// <summary>A code-only app: the metadata provider lets XamlControlsResources resolve the built-in control types.</summary>
    private sealed class SettingsApp : Application, IXamlMetadataProvider
    {
        private readonly XamlControlsXamlMetaDataProvider provider = new();

        // Dark whatever Windows' app mode is, so every brush looked up here is the dark one.
        public SettingsApp() => RequestedTheme = ApplicationTheme.Dark;

        public IXamlType GetXamlType(Type type) => provider.GetXamlType(type);

        public IXamlType GetXamlType(string fullName) => provider.GetXamlType(fullName);

        public XmlnsDefinition[] GetXmlnsDefinitions() => provider.GetXmlnsDefinitions();

        protected override void OnLaunched(LaunchActivatedEventArgs args)
        {
            Resources.MergedDictionaries.Add(new XamlControlsResources());
            Palette.UseDarkLook(Resources);
            var window = new SettingsWindow();
            window.Closed += (_, _) => Exit();
            window.Activate();
        }
    }
}
