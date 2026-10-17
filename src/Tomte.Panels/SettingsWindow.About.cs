using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Tomte.Core;
using Tomte.Core.Jobs;
using Tomte.Core.Search;
using Windows.ApplicationModel;
using Windows.ApplicationModel.DataTransfer;

namespace Tomte.Panels;

/// <summary>
/// The About page: the tomte, who made Tomte with their links, the version, the license and the third-party notices,
/// then a Support section with the donation pages and payment details when there are any. The page fetches nothing; a
/// page opens in the browser only when its button is clicked.
/// </summary>
public sealed partial class SettingsWindow
{
    // Beside Tomte.Shell.exe in the build and in the package.
    private const string NoticesFile = "THIRD-PARTY-NOTICES.txt";

    private UIElement[] AboutPage()
    {
        var shown = About.Shown(About.Links);
        var noticesNote = Note("Copyright and license text for the libraries that come with Tomte.");
        var notices = new Button { Content = "Open" };
        notices.Click += (_, _) => OpenNotices(noticesNote);
        List<UIElement> parts =
        [
            Card(Maker(shown)),
            Card(
                ValueRow("Version", About.Version(PackageVersion(), Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion), null),
                ValueRow("License", "MIT License", Note("Tomte is free and open source.")),
                Row("Third-party notices", noticesNote, notices)),
        ];
        AutomationProperties.SetName(notices, "Open the third-party notices");

        var support = About.Support(shown);
        if (support.Count > 0)
        {
            var line = Note("Tomte is free, and support keeps it going.");
            var pages = support.Where(l => l.Kind == AboutLinkKind.Donation).ToList();
            List<UIElement> rows = [pages.Count > 0 ? DonateRow(line, pages) : line, .. support.Where(l => l.Kind == AboutLinkKind.Payment).Select(PaymentRow)];
            parts.Add(Header("Support"));
            parts.Add(Card([.. rows]));
        }

        return [.. parts];
    }

    // The tomte beside "Tomte" and who made it, with the profile and source links under them.
    private static Grid Maker(IReadOnlyList<AboutLink> shown)
    {
        // The icon's 256-pixel image, decoded at the size it shows at this scale so it stays sharp.
        var icon = new Image { Width = 64, Height = 64, VerticalAlignment = VerticalAlignment.Top, Source = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "tomte.png"))) { DecodePixelWidth = 64, DecodePixelType = DecodePixelType.Logical } };
        AutomationProperties.SetAccessibilityView(icon, AccessibilityView.Raw);
        var text = new StackPanel
        {
            Spacing = 2,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new TextBlock { Text = "Tomte", Style = (Style)Application.Current.Resources["SubtitleTextBlockStyle"] },
                new TextBlock { Text = $"Made by {About.Developer}" },
            },
        };
        var links = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Margin = new Thickness(0, 4, 0, 0) };
        foreach (var link in shown.Where(l => l.Kind is AboutLinkKind.Profile or AboutLinkKind.Source))
        {
            links.Children.Add(WebButton(new HyperlinkButton { Padding = new Thickness(0) }, link));
        }

        if (links.Children.Count > 0)
        {
            text.Children.Add(links);
        }

        var grid = new Grid { ColumnSpacing = 16 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetColumn(text, 1);
        grid.Children.Add(icon);
        grid.Children.Add(text);
        return grid;
    }

    // A button per donation page, on a line of their own under the row's text: beside it they would squeeze the text to
    // nothing in a narrow window.
    private static Grid DonateRow(TextBlock line, List<AboutLink> pages)
    {
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (var page in pages)
        {
            buttons.Children.Add(WebButton(new Button(), page));
        }

        var row = Row("Donate", line, buttons);
        row.RowSpacing = 12;
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        row.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        Grid.SetRow(buttons, 1);
        Grid.SetColumn(buttons, 0);
        Grid.SetColumnSpan(buttons, 2);
        return row;
    }

    // A button that opens the link's page in the default browser; its tooltip says where that is.
    private static T WebButton<T>(T button, AboutLink link)
        where T : ButtonBase
    {
        string address = WebSearch.Address(link.Value)!.AbsoluteUri;
        button.Content = link.Name;
        ToolTipService.SetToolTip(button, address);
        button.Click += (_, _) => _ = Task.Run(() => WebSearch.Open(address));
        return button;
    }

    // The ID or address in full, selectable, in a monospaced font so 0 and O or l and 1 cannot be mixed up, and a button
    // that copies it.
    private static Grid PaymentRow(AboutLink payment)
    {
        var value = new TextBlock { Text = payment.Value, IsTextSelectionEnabled = true, TextWrapping = TextWrapping.Wrap, FontFamily = new FontFamily("Cascadia Mono, Consolas") };
        var copy = new Button { Content = "Copy" };
        copy.Click += async (_, _) =>
        {
            string said;
            try
            {
                var data = new DataPackage();
                data.SetText(payment.Value);
                Clipboard.SetContent(data);
                // The settings process ends with its window; flushed, the text stays on the clipboard after it.
                Clipboard.Flush();
                said = "Copied";
            }
            catch (Exception e) when (e is COMException or UnauthorizedAccessException)
            {
                // Another app has the clipboard open; it is not waited for here, on the window's thread.
                said = "Not copied";
            }

            copy.Content = said;
            FrameworkElementAutomationPeer.FromElement(copy)?.RaiseNotificationEvent(AutomationNotificationKind.ActionCompleted, AutomationNotificationProcessing.MostRecent, said, "AboutCopy");
            await Task.Delay(1500);
            copy.Content = "Copy";
        };
        var row = Row(payment.Name, value, copy);
        AutomationProperties.SetName(copy, $"Copy {payment.Name}");
        return row;
    }

    // A row whose right side is text to read, not a control. Row names its control after the row; this one keeps its text.
    private static Grid ValueRow(string label, string value, TextBlock? note)
    {
        var text = new TextBlock { Text = value, IsTextSelectionEnabled = true };
        var row = Row(label, note, text);
        text.ClearValue(AutomationProperties.NameProperty);
        return row;
    }

    // The package's version, or null for a build that runs without one.
    private static string? PackageVersion()
    {
        if (!NoJobTasks.Packaged)
        {
            return null;
        }

        var version = Package.Current.Id.Version;
        return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
    }

    // In the default text editor, through the shell.
    private static void OpenNotices(TextBlock note)
    {
        try
        {
            Process.Start(new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, NoticesFile)) { UseShellExecute = true })?.Dispose();
        }
        catch (Win32Exception e)
        {
            Show(note, $"Could not open {NoticesFile}: {e.Message}");
        }
    }
}
