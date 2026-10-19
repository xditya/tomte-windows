using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Tomte.Core;
using Windows.UI.ViewManagement;
using Colors = Microsoft.UI.Colors;

namespace Tomte.Panels;

/// <summary>
/// The Appearance section: the island's theme and the accent colour. The settings window itself stays
/// dark whatever is chosen here.
/// </summary>
public sealed partial class SettingsWindow
{
    private readonly UISettings systemColours = new();

    private Border Appearance() => Card(
        Row("Theme", Note("Follow the taskbar's light or dark look, or keep the island dark or light."), ThemePicker()),
        Row("Accent colour", Note("The colour of the selection, the AI and the recording dot, on the island and here. On an accent-coloured taskbar the island takes the Windows accent."), AccentPicker()));

    // The accent Windows uses now, as 0xRRGGBB.
    private uint SystemAccent()
    {
        var c = systemColours.GetColorValue(UIColorType.Accent);
        return (uint)(c.R << 16 | c.G << 8 | c.B);
    }

    // The window's own accent, which follows the choice; with Windows accent chosen, the colour Windows uses now.
    private void FollowAccent()
    {
        Palette.UseAccent(Application.Current.Resources, AccentColors.Resolve(store.Current.AccentColor, SystemAccent()));
        store.Changed += s => DispatcherQueue.TryEnqueue(() => Palette.UseAccent(Application.Current.Resources, AccentColors.Resolve(s.AccentColor, SystemAccent())));
    }

    // Three cards, each a small island on a desktop above a taskbar strip in the look it picks; following the taskbar
    // shows both halves. The chosen card has the accent round it.
    private StackPanel ThemePicker()
    {
        (ThemeChoice Choice, string Name)[] choices = [(ThemeChoice.FollowTaskbar, "Follow the taskbar"), (ThemeChoice.Dark, "Dark"), (ThemeChoice.Light, "Light")];
        var cards = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        var frames = new List<(ThemeChoice Choice, Button Card, Border Frame)>();
        void Show(TomteSettings s)
        {
            foreach (var (choice, card, frame) in frames)
            {
                frame.BorderBrush = choice == s.Theme ? Palette.AccentBrush() : Palette.Brush(Palette.LineStrong);
                AutomationProperties.SetItemStatus(card, choice == s.Theme ? "Chosen" : "");
            }
        }

        foreach (var (choice, name) in choices)
        {
            var scene = new Grid { Width = 112, Height = 64 };
            if (choice == ThemeChoice.FollowTaskbar)
            {
                scene.ColumnDefinitions.Add(new ColumnDefinition());
                scene.ColumnDefinitions.Add(new ColumnDefinition());
                var dark = Scene(false, new CornerRadius(6, 0, 0, 6), HorizontalAlignment.Right);
                var light = Scene(true, new CornerRadius(0, 6, 6, 0), HorizontalAlignment.Left);
                Grid.SetColumn(light, 1);
                scene.Children.Add(dark);
                scene.Children.Add(light);
            }
            else
            {
                scene.Children.Add(Scene(choice == ThemeChoice.Light, new CornerRadius(6), HorizontalAlignment.Center));
            }

            var frame = new Border { Child = scene, BorderThickness = new Thickness(2), CornerRadius = new CornerRadius(10), Padding = new Thickness(2) };
            var label = new TextBlock { Text = name, FontSize = 12, Foreground = Palette.Brush(Palette.Ink2), HorizontalAlignment = HorizontalAlignment.Center };
            var card = new Button
            {
                Content = new StackPanel { Spacing = 6, Children = { frame, label } },
                Padding = new Thickness(4),
                MinWidth = 0,
                CornerRadius = new CornerRadius(12),
                BorderThickness = new Thickness(0),
                Background = Palette.Brush(Colors.Transparent),
            };
            AutomationProperties.SetName(card, name);
            card.Click += (_, _) => Save(s => s with { Theme = choice });
            frames.Add((choice, card, frame));
            cards.Children.Add(card);
        }

        Show(store.Current);
        store.Changed += s => DispatcherQueue.TryEnqueue(() => Show(s));
        return cards;
    }

    // A desktop with the island's pill (or the half of it on this side) at the top and the taskbar along the bottom.
    private static Grid Scene(bool light, CornerRadius pill, HorizontalAlignment side)
    {
        var (island, line, desktop, taskbar) = Theme.Sample(light);
        return new Grid
        {
            Background = Palette.Brush(Palette.Rgb(desktop)),
            // The desktop's rounded corners are on the same sides as the pill's.
            CornerRadius = pill,
            Children =
            {
                new Border
                {
                    Width = side == HorizontalAlignment.Center ? 52 : 26,
                    Height = 12,
                    Margin = new Thickness(0, 6, 0, 0),
                    VerticalAlignment = VerticalAlignment.Top,
                    HorizontalAlignment = side,
                    CornerRadius = pill,
                    Background = Palette.Brush(Palette.Rgb(island)),
                    BorderBrush = Palette.Brush(Palette.Rgb(line)),
                    BorderThickness = new Thickness(pill.TopLeft > 0 ? 1 : 0, 1, pill.TopRight > 0 ? 1 : 0, 1),
                },
                new Border { Height = 10, VerticalAlignment = VerticalAlignment.Bottom, Background = Palette.Brush(Palette.Rgb(taskbar)) },
            },
        };
    }

    // A swatch per accent colour and one for the Windows accent; the chosen one has a ring. The window and the island take
    // a new one at once.
    private StackPanel AccentPicker()
    {
        string[] names = ["Coral", "Amber", "Sky", "Green", "Windows accent"];
        uint[] picks = [.. AccentColors.Choices, AccentColors.Windows];
        var swatches = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        var rings = new List<(uint Pick, Button Swatch, Border Ring)>();
        void Show(TomteSettings s)
        {
            uint chosen = AccentColors.Held(s.AccentColor);
            foreach (var (pick, swatch, ring) in rings)
            {
                ring.BorderBrush = Palette.Brush(pick == chosen ? Palette.Ink : Colors.Transparent);
                AutomationProperties.SetItemStatus(swatch, pick == chosen ? "Chosen" : "");
            }
        }

        for (int i = 0; i < picks.Length; i++)
        {
            uint pick = picks[i];
            var ring = new Border
            {
                Width = 28,
                Height = 28,
                CornerRadius = new CornerRadius(14),
                BorderThickness = new Thickness(2),
                Padding = new Thickness(3),
                Child = new Microsoft.UI.Xaml.Shapes.Ellipse { Fill = Palette.Brush(Palette.Rgb(AccentColors.Resolve(pick, SystemAccent()))) },
            };
            var swatch = new Button { Content = ring, Width = 34, Height = 34, MinWidth = 0, Padding = new Thickness(0), CornerRadius = new CornerRadius(17), BorderThickness = new Thickness(0), Background = Palette.Brush(Colors.Transparent) };
            AutomationProperties.SetName(swatch, names[i]);
            ToolTipService.SetToolTip(swatch, names[i]);
            swatch.Click += (_, _) => Save(s => s with { AccentColor = pick });
            rings.Add((pick, swatch, ring));
            swatches.Children.Add(swatch);
        }

        Show(store.Current);
        store.Changed += s => DispatcherQueue.TryEnqueue(() => Show(s));
        return swatches;
    }
}
