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

    // Marks a part of a page as a section of its own, for a card with no header above it: Find a setting names its rows'
    // section after it, and the shell can open the window at it.
    private sealed record SectionName(string Name);

    // The box with a magnifier at its start and its key at its end; the key steps aside while there is text, for the
    // box's clear button.
    private Grid Finder()
    {
        var box = new AutoSuggestBox { PlaceholderText = "Find a setting", UpdateTextOnSelect = false, FontSize = 13 };
        AutomationProperties.SetName(box, "Find a setting");
        // Without a tooltip of its own: WinUI's would cover the title bar each time the box takes the focus.
        AutomationProperties.SetAcceleratorKey(box, "Ctrl+F");
        box.KeyboardAcceleratorPlacementMode = KeyboardAcceleratorPlacementMode.Hidden;
        // The box's own text field is what takes the focus, so a screen reader hears the key from there.
        box.GotFocus += (_, e) =>
        {
            if (e.OriginalSource is TextBox text)
            {
                AutomationProperties.SetAcceleratorKey(text, "Ctrl+F");
            }
        };
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
            box.ItemsSource = listed.Count > 0 ? listed.Select(Suggestion).ToList<UIElement>() : new List<UIElement> { Caption("No setting found.") };
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
        // Every Esc: the box's own would bring back the last text typed once the box is empty.
        box.PreviewKeyDown += (_, e) =>
        {
            if (e.Key == VirtualKey.Escape)
            {
                e.Handled = true;
                Clear();
            }
        };
        var glass = new FontIcon { Glyph = "\uE721", FontSize = 14, Foreground = Palette.Brush(Palette.Ink3), HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0), IsHitTestVisible = false };
        var key = Keycap("Ctrl F");
        key.HorizontalAlignment = HorizontalAlignment.Right;
        key.Margin = new Thickness(0, 0, 8, 0);
        box.TextChanged += (_, _) => key.Visibility = box.Text.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        // Room in the box's own text field for the magnifier and the key.
        box.Loaded += (_, _) =>
        {
            if (Descendant<TextBox>(box) is { } text)
            {
                text.Padding = new Thickness(36, 9, 64, 0);
                text.MinHeight = 38;
            }
        };
        var field = new Grid { Margin = new Thickness(12, 4, 12, 14), Children = { box, glass, key } };
        foreach (string state in new[] { "", "PointerOver" })
        {
            field.Resources["TextControlBackground" + state] = Palette.Brush(Palette.Card);
        }

        return field;
    }

    // A key's name in a small raised box, as on the island.
    private static Border Keycap(string text) => new()
    {
        Child = new TextBlock { Text = text, FontFamily = new FontFamily(Palette.Mono), FontSize = 11, Foreground = Palette.Brush(Palette.Ink2) },
        Padding = new Thickness(6, 3, 6, 4),
        CornerRadius = new CornerRadius(6),
        BorderThickness = new Thickness(1),
        Background = Palette.Brush(Palette.Raised),
        BorderBrush = Palette.Brush(Palette.LineStrong),
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
    };

    private static T? Descendant<T>(DependencyObject parent)
        where T : DependencyObject
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(parent); i++)
        {
            var child = VisualTreeHelper.GetChild(parent, i);
            if ((child as T ?? Descendant<T>(child)) is { } found)
            {
                return found;
            }
        }

        return null;
    }

    // The title, and the page under it in small type. Neither sets a colour, so a highlighted item's text takes the
    // highlight's colour, which high contrast themes rely on.
    private static StackPanel Suggestion(FoundRow found)
    {
        var item = new StackPanel { Tag = found, Padding = new Thickness(0, 4, 0, 4), Children = { new TextBlock { Text = found.Row.Title, TextTrimming = TextTrimming.CharacterEllipsis }, Caption(found.Row.Page) } };
        AutomationProperties.SetName(item, $"{found.Row.Title}, {found.Row.Page}");
        return item;
    }

    private static TextBlock Caption(string text) => new() { Text = text, Style = (Style)Application.Current.Resources["CaptionTextBlockStyle"] };

    // The rows each page shows now, in the window's order. Hidden rows are left out; a row in a closed Expander is kept,
    // since choosing it opens the Expander. A header on the page names the section of the rows after it, an Expander's
    // header the rows in it, and a part marked with a SectionName the rows inside it.
    private List<FoundRow> ReadRows()
    {
        var rows = new List<FoundRow>();
        foreach (var page in PageItems)
        {
            void Walk(UIElement element, string section, Expander? inside)
            {
                if (element is FrameworkElement { Tag: SectionName marked })
                {
                    section = marked.Name;
                }

                switch (element)
                {
                    case { Visibility: Visibility.Collapsed }:
                        return;
                    case Grid { Tag: RowParts parts } row:
                        string note = parts.Note is { Visibility: Visibility.Visible } shown ? shown.Text : "";
                        rows.Add(new FoundRow(new SettingRow((string)page.Content, inside?.Header as string ?? section, parts.Title.Text, note, parts.Control is not TextBlock), page, row, parts.Control, inside));
                        return;
                    case Panel panel:
                        foreach (var child in panel.Children)
                        {
                            Walk(child, section, inside);
                        }

                        return;
                    case Border { Child: { } child }:
                        Walk(child, section, inside);
                        return;
                    case Expander { Content: UIElement content } expander:
                        Walk(content, section, expander);
                        return;
                }
            }

            // The first part is the page's title and lede.
            string header = "";
            foreach (var part in PageOf(page).Children.Skip(1))
            {
                if (part is TextBlock text)
                {
                    header = text.Text;
                }
                else
                {
                    Walk(part, header, null);
                }
            }
        }

        return rows;
    }

    private async Task ShowAsync(FoundRow found)
    {
        Open(found.Page);
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
