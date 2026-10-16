using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Tomte.Core.Search;
using Windows.System;

namespace Tomte.Panels;

/// <summary>
/// Find a setting, the box above the page list. Each time its text changes it reads the rows the pages show, so a row
/// added to a page is found with no list to keep, and lists the best matches with their page. Choosing one opens its
/// page, brings the row into view, tints it for a moment and puts the keyboard focus on its control. Ctrl+F comes here
/// from anywhere in the window; Down and Enter choose; Esc clears.
/// </summary>
public sealed partial class SettingsWindow
{
    // What a row is made of, kept on the row by Row.
    private sealed record RowParts(TextBlock Title, TextBlock? Note, FrameworkElement Control);

    // A row as read from its page: what is matched, and where to take the user. Inside is the Expander it is in, if any.
    private sealed record FoundRow(SettingRow Row, NavigationViewItem Page, Grid Element, FrameworkElement Control, Expander? Inside);

    private AutoSuggestBox Finder()
    {
        var box = new AutoSuggestBox { PlaceholderText = "Find a setting", QueryIcon = new SymbolIcon(Symbol.Find), UpdateTextOnSelect = false };
        AutomationProperties.SetName(box, "Find a setting");
        // Without a tooltip of its own: WinUI's would cover the title bar each time the box takes the focus.
        AutomationProperties.SetAcceleratorKey(box, "Ctrl+F");
        box.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        var find = new KeyboardAccelerator { Key = VirtualKey.F, Modifiers = VirtualKeyModifiers.Control };
        find.Invoked += (_, e) =>
        {
            e.Handled = true;
            box.Focus(FocusState.Keyboard);
        };
        box.KeyboardAccelerators.Add(find);
        List<FoundRow> listed = [];

        void Clear()
        {
            box.Text = "";
            box.ItemsSource = null;
            listed = [];
        }

        box.TextChanged += (_, e) =>
        {
            if (e.Reason != AutoSuggestionBoxTextChangeReason.UserInput)
            {
                return;
            }

            if (box.Text.Trim().Length == 0)
            {
                Clear();
                return;
            }

            var rows = ReadRows();
            listed = [.. SettingsFind.Rank([.. rows.Select(r => r.Row)], box.Text).Select(i => rows[i])];
            box.ItemsSource = listed.Count > 0 ? listed.Select(Suggestion).ToList<UIElement>() : new List<UIElement> { Note("No setting found.") };
            // The list is not read out as it changes; a screen reader hears the count instead.
            string count = listed.Count switch { 0 => "No setting found", 1 => "1 setting found", int n => $"{n} settings found" };
            (FrameworkElementAutomationPeer.FromElement(box) ?? FrameworkElementAutomationPeer.CreatePeerForElement(box))
                .RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.MostRecent, count, "FindSettingCount");
        };
        // Enter with no row picked takes the best one; a click or Enter on a row takes that row.
        box.QuerySubmitted += (sender, e) =>
        {
            if ((e.ChosenSuggestion is FrameworkElement { Tag: FoundRow row } ? row : listed.FirstOrDefault()) is { } chosen)
            {
                Clear();
                _ = ShowAsync(chosen);
            }
        };
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape && box.Text.Length > 0)
            {
                e.Handled = true;
                Clear();
            }
        };
        return box;
    }

    // The title, and the page under it in the notes' colour.
    private static StackPanel Suggestion(FoundRow found)
    {
        var item = new StackPanel { Tag = found, Padding = new Thickness(0, 4, 0, 4), Children = { new TextBlock { Text = found.Row.Title, TextTrimming = TextTrimming.CharacterEllipsis }, Note(found.Row.Page) } };
        AutomationProperties.SetName(item, $"{found.Row.Title}, {found.Row.Page}");
        return item;
    }

    // The rows each page shows now, in the window's order. Hidden rows are left out; a row in a closed Expander is kept,
    // since choosing it opens the Expander. A header on the page names the section of the rows after it, an Expander's
    // header the rows in it.
    private List<FoundRow> ReadRows()
    {
        var rows = new List<FoundRow>();
        foreach (var page in nav.MenuItems.OfType<NavigationViewItem>())
        {
            string section = "";
            void Walk(UIElement element, Expander? inside)
            {
                switch (element)
                {
                    case { Visibility: Visibility.Collapsed }:
                        return;
                    case Grid { Tag: RowParts parts } row:
                        string note = parts.Note is { Visibility: Visibility.Visible } shown ? shown.Text : "";
                        rows.Add(new FoundRow(new SettingRow((string)page.Content, inside?.Header as string ?? section, parts.Title.Text, note), page, row, parts.Control, inside));
                        return;
                    case Panel panel:
                        foreach (var child in panel.Children)
                        {
                            Walk(child, inside);
                        }

                        return;
                    case Border { Child: { } child }:
                        Walk(child, inside);
                        return;
                    case Expander { Content: UIElement content } expander:
                        Walk(content, expander);
                        return;
                }
            }

            // The first child is the page's title.
            foreach (var part in ((StackPanel)((ScrollViewer)page.Tag).Content).Children.Skip(1))
            {
                if (part is TextBlock header)
                {
                    section = header.Text;
                }
                else
                {
                    Walk(part, null);
                }
            }
        }

        return rows;
    }

    private async Task ShowAsync(FoundRow found)
    {
        nav.SelectedItem = found.Page;
        nav.Content = found.Page.Tag;
        // A page not shown before has no layout yet; the row needs one to be brought into view.
        nav.UpdateLayout();
        if (found.Inside is { IsExpanded: false } expander)
        {
            // An opening Expander slides its content down from above, and a row brought into view while it moves lands in
            // the wrong place. Simplification: waits out the slide (333 ms in WinUI's template); retune if the template changes.
            expander.IsExpanded = true;
            await Task.Delay(350);
        }

        // The control, or the first control in it (a box with its Add button). A row whose control is text takes none.
        (found.Control as Control ?? FocusManager.FindFirstFocusableElement(found.Control) as Control)?.Focus(FocusState.Keyboard);
        found.Element.StartBringIntoView(new BringIntoViewOptions { VerticalAlignmentRatio = 0.5, AnimationDesired = false });
        await TintAsync(found.Element);
    }

    // A tint of the accent colour behind the row that fades after a moment. The row's margin and padding cancel out, so
    // the tint reaches past its text without moving anything.
    private static async Task TintAsync(Grid row)
    {
        var accent = ((SolidColorBrush)Application.Current.Resources["AccentFillColorDefaultBrush"]).Color;
        row.Margin = new Thickness(-8, -6, -8, -6);
        row.Padding = new Thickness(8, 6, 8, 6);
        row.CornerRadius = new CornerRadius(4);
        row.BackgroundTransition ??= new BrushTransition { Duration = TimeSpan.FromMilliseconds(400) };
        row.Background = new SolidColorBrush(accent with { A = 0x40 });
        await Task.Delay(1500);
        row.Background = new SolidColorBrush(accent with { A = 0 });
    }
}
