using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Tomte.Core;
using Colors = Microsoft.UI.Colors;

namespace Tomte.Panels;

/// <summary>
/// Desktop peek's styles as a row of cards, each a small picture of the top of a desktop with a clock widget under the
/// middle and the style resting on it, the chosen card outlined in the accent like the provider cards. Off is the last
/// card. The corner pill's side shows under the row while Corner is chosen.
/// </summary>
public sealed partial class SettingsWindow
{
    // The pictures are drawn at half the island's size on a 150 x 90 piece of desktop.
    private const double PreviewScale = 0.5, PreviewWidth = 150, PreviewHeight = 90;

    private static readonly (PeekStyle? Style, string Name)[] PeekChoices =
        [(PeekStyle.Perched, "Perched"), (PeekStyle.Lip, "Edge lip"), (PeekStyle.Pill, "Pill"), (PeekStyle.Corner, "Corner"), (null, "Off")];

    private StackPanel PeekStyles()
    {
        var tomte = new BitmapImage(new Uri(Path.Combine(AppContext.BaseDirectory, "Assets", "tomte.png"))) { DecodePixelWidth = 32, DecodePixelType = DecodePixelType.Logical };
        var picked = Palette.AccentBrush();
        var cards = new Grid { ColumnSpacing = 10 };
        AutomationProperties.SetName(cards, "Desktop peek");
        var shown = new List<(PeekStyle? Style, RadioButton Radio, Border Card)>();
        // The corner card's pill sits in the corner the side below picks.
        var corner = PeekPill(tomte, PreviewScale * IslandGeometry.CornerDepth, PreviewScale * CompactLayout.CornerTomte, glance: false);
        PeekCorner[] order = [PeekCorner.Left, PeekCorner.Right];
        var (side, sides) = Segmented(["Left", "Right"], Array.IndexOf(order, store.Current.PeekCorner), i => Save(s => s with { PeekCorner = order[i] }));
        AutomationProperties.SetName(side, "Corner");
        var sideRow = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 12,
            Children = { new TextBlock { Text = "Corner", FontSize = 13, Foreground = Palette.Brush(Palette.Ink2), VerticalAlignment = VerticalAlignment.Center }, side },
        };

        void Show(TomteSettings s)
        {
            var current = Chosen(s);
            foreach (var (style, radio, card) in shown)
            {
                bool on = style == current;
                radio.IsChecked = on;
                card.Background = Palette.Brush(on ? Palette.Raised : Palette.Card);
                card.BorderBrush = on ? picked : Palette.Brush(Palette.Outline);
                card.BorderThickness = new Thickness(on ? 1.5 : 1);
                // The same inner size either way, so picking a card moves nothing.
                card.Padding = new Thickness(on ? 9.5 : 10);
            }

            corner.HorizontalAlignment = s.PeekCorner == PeekCorner.Left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
            sideRow.Visibility = current == PeekStyle.Corner ? Visibility.Visible : Visibility.Collapsed;
            for (int i = 0; i < sides.Count; i++)
            {
                sides[i].IsChecked = order[i] == s.PeekCorner;
            }
        }

        for (int i = 0; i < PeekChoices.Length; i++)
        {
            var (style, name) = PeekChoices[i];
            var radio = new RadioButton
            {
                GroupName = "peek",
                MinWidth = 0,
                Content = new TextBlock { Text = name, FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Palette.Brush(Palette.Ink) },
            };
            AutomationProperties.SetName(radio, name);
            var card = new Border
            {
                CornerRadius = new CornerRadius(14),
                Child = new StackPanel
                {
                    Spacing = 8,
                    Children = { new Viewbox { Child = Desk(style, tomte, corner), StretchDirection = StretchDirection.DownOnly }, radio },
                },
            };
            card.Tapped += (_, _) => radio.IsChecked = true;
            // Off keeps the style it had, so turning peek on again brings it back. Checked also fires when Show checks the
            // card already saved, which needs no save.
            radio.Checked += (_, _) =>
            {
                if (Chosen(store.Current) != style)
                {
                    Save(s => s with { DesktopPeek = style is not null, PeekStyle = style ?? s.PeekStyle });
                }
            };
            cards.ColumnDefinitions.Add(new ColumnDefinition());
            Grid.SetColumn(card, i);
            cards.Children.Add(card);
            shown.Add((style, radio, card));
        }

        Show(store.Current);
        store.Changed += s => DispatcherQueue.TryEnqueue(() => Show(s));
        return new StackPanel { Spacing = 12, Children = { cards, sideRow } };
    }

    private static PeekStyle? Chosen(TomteSettings s) => s.DesktopPeek ? s.PeekStyle : null;

    // A piece of the top of a desktop: the wallpaper, a clock widget under the middle, and the style resting on the edge.
    private static Border Desk(PeekStyle? style, BitmapImage tomte, Border corner)
    {
        var desk = new Grid { Width = PreviewWidth, Height = PreviewHeight };
        desk.Children.Add(new StackPanel
        {
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 17, 0, 0),
            Children =
            {
                new TextBlock { Text = "22:02", FontSize = 26, FontWeight = FontWeights.Light, Foreground = Palette.Brush(Colors.White), HorizontalAlignment = HorizontalAlignment.Center, LineHeight = 26 },
                new TextBlock { Text = "Monday, 20 October", FontSize = 7, Foreground = Palette.Brush(Colors.White), Opacity = 0.85, HorizontalAlignment = HorizontalAlignment.Center },
            },
        });
        double s = PreviewScale;
        FrameworkElement? resting = style switch
        {
            PeekStyle.Perched => new Image { Source = tomte, Width = s * MascotArt.SizeDips, Height = s * MascotArt.SizeDips },
            PeekStyle.Lip => new Border
            {
                Width = s * IslandGeometry.Lip.Width,
                Height = s * IslandGeometry.LipDepth,
                CornerRadius = new CornerRadius(0, 0, s * IslandGeometry.LipDepth, s * IslandGeometry.LipDepth),
                Background = Palette.Brush(Palette.Island),
                // The dot it shows while something waits.
                Child = new Border { Width = 2, Height = 1, CornerRadius = new CornerRadius(0.5), Background = Palette.AccentBrush(), VerticalAlignment = VerticalAlignment.Top, Margin = new Thickness(0, 1, 0, 0) },
            },
            PeekStyle.Pill => PeekPill(tomte, s * IslandGeometry.Compact.Depth, 11, glance: true),
            PeekStyle.Corner => corner,
            _ => null,
        };
        if (resting is not null)
        {
            resting.VerticalAlignment = VerticalAlignment.Top;
            if (style != PeekStyle.Corner)
            {
                resting.HorizontalAlignment = HorizontalAlignment.Center;
            }

            // The pills hang the gap below the edge, and the corner pill as far in from the side; the others touch the edge.
            resting.Margin = style is PeekStyle.Pill or PeekStyle.Corner ? new Thickness(s * IslandOutline.Gap) : default;
            desk.Children.Add(resting);
        }

        return new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = Palette.Brush(Palette.Line),
            Background = new LinearGradientBrush
            {
                StartPoint = new Windows.Foundation.Point(0.9, 0),
                EndPoint = new Windows.Foundation.Point(0.2, 1),
                GradientStops = { new GradientStop { Color = Palette.Wallpaper, Offset = 0.3 }, new GradientStop { Color = Palette.WallpaperGlow, Offset = 1 } },
            },
            Child = desk,
        };
    }

    // The peek pill at preview size: the tomte and the time, and on the full pill a rule and one more thing.
    private static Border PeekPill(BitmapImage tomte, double depth, double tomteSize, bool glance)
    {
        double font = glance ? 6.5 : 6;
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = glance ? 5 : 4,
            VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Image { Source = tomte, Width = tomteSize, Height = tomteSize },
                new TextBlock { Text = "22:02", FontFamily = new FontFamily(Palette.Mono), FontSize = font, FontWeight = FontWeights.Medium, Foreground = Palette.Brush(Palette.Ink), VerticalAlignment = VerticalAlignment.Center },
            },
        };
        if (glance)
        {
            row.Children.Add(new Border { Width = 0.5, Height = 7, Background = Palette.Brush(Palette.LineStrong), VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(new TextBlock { Text = "8 on the shelf", FontSize = font, Foreground = Palette.Brush(Palette.Ink2), VerticalAlignment = VerticalAlignment.Center });
        }

        return new Border
        {
            Height = depth,
            CornerRadius = new CornerRadius(depth / 2),
            Background = Palette.Brush(Palette.Island),
            BorderThickness = new Thickness(0.5),
            BorderBrush = Palette.Brush(Palette.Hairline),
            Padding = new Thickness(glance ? 5 : 3.5, 0, glance ? 8 : 6, 0),
            Child = row,
        };
    }
}
