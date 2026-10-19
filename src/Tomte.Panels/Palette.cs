using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Tomte.Core;
using Windows.UI;
using Colors = Microsoft.UI.Colors;

namespace Tomte.Panels;

/// <summary>
/// The settings window's colours, all dark, in one place. The accent is the one colour that changes: <see cref="UseAccent"/>
/// recolours WinUI's own accent brushes, so every stock control that uses the accent follows without a style of its own.
/// </summary>
internal static class Palette
{
    public static readonly Color Page = Rgb(0x1A1B1F);
    public static readonly Color Card = Rgb(0x1F2025);
    public static readonly Color Raised = Rgb(0x24262B);
    public static readonly Color RaisedHi = Rgb(0x2A2C31);
    public static readonly Color Line = Rgb(0x2A2C31);
    public static readonly Color Outline = Rgb(0x2E3036);
    public static readonly Color LineStrong = Rgb(0x34363C);
    public static readonly Color Field = Rgb(0x15161A);
    public static readonly Color Ink = Rgb(0xF2F0EB);
    public static readonly Color Ink2 = Rgb(0xB1AFA9);
    public static readonly Color Ink3 = Rgb(0x8E8C86);
    public static readonly Color PaneText = Rgb(0xD9D6CF);
    public static readonly Color InkHover = Rgb(0xDCDAD5);
    public static readonly Color OnInk = Rgb(0x111214);
    public static readonly Color Ok = Rgb(0x6FD39A);
    public static readonly Color Danger = Rgb(0xFF9A85);

    /// <summary>The desktop the peek previews are drawn on: a dusk-blue wallpaper with a lighter glow, and the island's fill.</summary>
    public static readonly Color Wallpaper = Rgb(0x2E3A47), WallpaperGlow = Rgb(0x5B7A86), Island = Rgb(Theme.Island);

    /// <summary>The monospaced face for keys, model ids and money: Cascadia Mono where Windows has it.</summary>
    public const string Mono = "Cascadia Mono, Consolas";

    /// <summary>The hover layer over a dark surface.</summary>
    public static readonly Color Hover = Color.FromArgb(0x15, 0xFF, 0xFF, 0xFF);

    /// <summary>The island's hairline, a faint white edge on its dark fill.</summary>
    public static readonly Color Hairline = Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF);

    // The brushes made here that show the accent, so a new accent reaches them too.
    private static readonly List<SolidColorBrush> AccentBrushes = [];

    public static Color Accent { get; private set; } = Rgb(AccentColors.Default);

    public static SolidColorBrush Brush(Color color) => new(color);

    /// <summary>A brush that shows the accent now and after every <see cref="UseAccent"/>.</summary>
    public static SolidColorBrush AccentBrush()
    {
        var brush = new SolidColorBrush(Accent);
        AccentBrushes.Add(brush);
        return brush;
    }

    /// <summary>
    /// The window's surfaces in place of WinUI's: the page behind the cards, the selected page in the list, and the fields'
    /// fill. Called once, before the window is made; the controls keep their own templates and states.
    /// </summary>
    public static void UseDarkLook(ResourceDictionary resources)
    {
        resources["ControlCornerRadius"] = new CornerRadius(8);
        resources["OverlayCornerRadius"] = new CornerRadius(10);
        resources["NavigationViewContentBackground"] = Brush(Page);
        resources["NavigationViewContentGridBorderBrush"] = Brush(Colors.Transparent);
        resources["NavigationViewContentGridCornerRadius"] = new CornerRadius(14, 0, 0, 0);
        foreach (string state in new[] { "Selected", "SelectedPointerOver", "SelectedPressed" })
        {
            resources["NavigationViewItemBackground" + state] = Brush(Raised);
            resources["NavigationViewItemForeground" + state] = Brush(Ink);
        }

        resources["NavigationViewItemForeground"] = Brush(PaneText);
        foreach (string state in new[] { "", "PointerOver", "Focused" })
        {
            resources["TextControlBackground" + state] = Brush(Field);
        }

        resources["ComboBoxBackground"] = Brush(Field);
        resources["ComboBoxBackgroundPointerOver"] = Brush(Raised);
        resources["ComboBoxBackgroundPressed"] = Brush(Raised);
    }

    /// <summary>
    /// Makes <paramref name="rgb"/> (0xRRGGBB) the accent in this process. WinUI's dark theme brushes are shared objects, and
    /// its control brushes (a toggle that is on, a checked box, the page list's selection mark, a focused text box's
    /// underline) refer to these, so changing their colour recolours what is on screen at once.
    /// </summary>
    public static void UseAccent(ResourceDictionary resources, uint rgb)
    {
        Accent = Rgb(rgb);
        // Text in the accent is lighter, to stay readable on the dark page; text on an accent fill is the accent, nearly black.
        var text = Mix(Accent, Colors.White, 0.25);
        var onAccent = Color.FromArgb(0xFF, (byte)(Accent.R / 10), (byte)(Accent.G / 10), (byte)(Accent.B / 10));
        foreach (var (key, color) in new[]
        {
            ("AccentFillColorDefaultBrush", Accent),
            ("AccentFillColorSecondaryBrush", Accent),
            ("AccentFillColorTertiaryBrush", Accent),
            ("AccentFillColorSelectedTextBackgroundBrush", Accent),
            ("AccentTextFillColorPrimaryBrush", text),
            ("AccentTextFillColorSecondaryBrush", text),
            ("AccentTextFillColorTertiaryBrush", Accent),
            ("TextOnAccentFillColorPrimaryBrush", onAccent),
            ("TextOnAccentFillColorSecondaryBrush", onAccent),
        })
        {
            if (Find(resources, key) is SolidColorBrush brush)
            {
                brush.Color = color;
            }
        }

        // The focused text box's underline: a gradient whose first stop is the accent.
        if (Find(resources, "TextControlElevationBorderFocusedBrush") is GradientBrush { GradientStops.Count: > 0 } gradient)
        {
            gradient.GradientStops[0].Color = Accent;
        }

        foreach (var brush in AccentBrushes)
        {
            brush.Color = Accent;
        }
    }

    // The indexer looks in the merged and theme dictionaries too; a key a newer WinUI drops is left out.
    private static object? Find(ResourceDictionary resources, string key)
    {
        try
        {
            return resources[key];
        }
        catch (Exception e) when (e is KeyNotFoundException or System.Runtime.InteropServices.COMException)
        {
            return null;
        }
    }

    public static Color Rgb(uint rgb) => Color.FromArgb(0xFF, (byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);

    private static Color Mix(Color a, Color b, double t) =>
        Color.FromArgb(0xFF, (byte)(a.R + ((b.R - a.R) * t)), (byte)(a.G + ((b.G - a.G) * t)), (byte)(a.B + ((b.B - a.B) * t)));
}
