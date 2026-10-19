namespace Tomte.Core;

/// <summary>
/// Every colour the island draws, as 0xAARRGGBB. The island is always dark and its fill is flat, so the surfaces are
/// opaque and exact; the overlays are the same steps as white over <see cref="Island"/>, for anything drawn over
/// something else. The accent is the one colour that may change: <see cref="UseAccent"/> sets it and the two values
/// that follow from it.
/// </summary>
public static class Theme
{
    /// <summary>The island, pill and panel fill.</summary>
    public const uint Island = 0xFF111214;

    /// <summary>Sections inside a panel, input fields, the tab rail's track.</summary>
    public const uint Card = 0xFF1A1B1F;

    /// <summary>Tiles, selected rows, secondary buttons, keycaps.</summary>
    public const uint Raised = 0xFF24262B;

    /// <summary>The current tab's pill, a pressed secondary button.</summary>
    public const uint RaisedHi = 0xFF2A2C31;

    /// <summary>Hairlines and dividers.</summary>
    public const uint Line = 0xFF2A2C31;

    /// <summary>Outlined chips, a keycap's border, a field's border.</summary>
    public const uint LineStrong = 0xFF34363C;

    /// <summary>Dashed "drop here" outlines and empty slots.</summary>
    public const uint Dash = 0xFF3A3C42;

    /// <summary>The surfaces above as white over the island, for drawing over the screen or a picture.</summary>
    public const uint CardOver = 0x0AFFFFFF, RaisedOver = 0x14FFFFFF, LineOver = 0x1AFFFFFF, LineStrongOver = 0x24FFFFFF, DashOver = 0x29FFFFFF;

    /// <summary>The empty part of a meter or progress track.</summary>
    public const uint Track = 0xFF2E3036;

    /// <summary>A waveform bar in silence, an unchecked checkbox's border.</summary>
    public const uint Silence = 0xFF4A4C52;

    /// <summary>A folder's glyph.</summary>
    public const uint Folder = 0xFFE9C46A;

    /// <summary>A settings card, a step above the settings window's own fill.</summary>
    public const uint SettingsCard = 0xFF1F2025;

    /// <summary>The settings window's fill and its segmented controls' track.</summary>
    public const uint Segmented = 0xFF15161A;

    /// <summary>Text: primary, secondary and tertiary. Tertiary is legible on <see cref="Raised"/> and darker only.</summary>
    public const uint Ink = 0xFFF2F0EB, Ink2 = 0xFFB1AFA9, Ink3 = 0xFF8E8C86;

    /// <summary>A shelf tile's icon square by kind of file.</summary>
    public const uint KindPdf = 0xFFC0392B, KindPicture = 0xFF3E8E6A, KindDocument = 0xFF3A6EA5, KindLink = 0xFF8C5BD6, KindOther = 0xFF5D6D7E;

    /// <summary>Connected, saved, a provider that answers.</summary>
    public const uint Ok = 0xFF6FD39A;

    /// <summary>Something did not work, or wants a second press; its faint fill.</summary>
    public const uint Warn = 0xFFF0B35A, WarnSoft = 0x33F0B35A;

    /// <summary>Destructive text buttons.</summary>
    public const uint Danger = 0xFFFF9A85;

    /// <summary>A link's glyph. Never a fill.</summary>
    public const uint Link = 0xFF7CC4FF;

    /// <summary>The state layer over a hovered target, about 8% white; over a pressed one, about 3%.</summary>
    public const uint HoverLayer = 0x15FFFFFF, PressedLayer = 0x08FFFFFF;

    /// <summary>The island's shadow, and the dimming over the screen while picking a region.</summary>
    public const uint Shadow = 0xFF000000, Scrim = 0x59000000;

    /// <summary>The accent the island starts with.</summary>
    public const uint DefaultAccent = 0xFFFF6B4A;

    /// <summary>The ink on an accent fill when the accent is light enough to carry dark text.</summary>
    public const uint DarkOnAccent = 0xFF1A0A05;

    /// <summary>The one accent: AI, the selection and focus rings, checked controls, the timer, the recording dot.</summary>
    public static uint Accent { get; private set; } = DefaultAccent;

    /// <summary>The accent at 20%: the halo round the recording dot, the glow under a hot drop zone.</summary>
    public static uint AccentSoft { get; private set; } = Soft(DefaultAccent);

    /// <summary>Text and glyphs on an <see cref="Accent"/> fill.</summary>
    public static uint OnAccent { get; private set; } = DarkOnAccent;

    /// <summary>
    /// Makes <paramref name="rgb"/> (its alpha ignored) the accent, its 20% the soft accent, and the ink on it whichever
    /// of <see cref="DarkOnAccent"/> and <see cref="Ink"/> stands out more. Brushes made before the call keep the old colour.
    /// </summary>
    public static void UseAccent(uint rgb)
    {
        Accent = 0xFF000000 | (rgb & 0xFFFFFF);
        AccentSoft = Soft(Accent);
        OnAccent = Contrast(Accent, DarkOnAccent) >= Contrast(Accent, Ink) ? DarkOnAccent : Ink;
    }

    /// <summary><paramref name="argb"/> with its alpha replaced by <paramref name="opacity"/> (0 to 1).</summary>
    public static uint WithOpacity(uint argb, float opacity) => (uint)Math.Round(Math.Clamp(opacity, 0, 1) * 255) << 24 | (argb & 0xFFFFFF);

    private static uint Soft(uint argb) => 0x33000000 | (argb & 0xFFFFFF);

    private static double Contrast(uint a, uint b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    // WCAG relative luminance: 0 for black, 1 for white.
    private static double Luminance(uint argb)
    {
        static double Channel(uint c)
        {
            double s = c / 255.0;
            return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4);
        }

        return 0.2126 * Channel(argb >> 16 & 0xFF) + 0.7152 * Channel(argb >> 8 & 0xFF) + 0.0722 * Channel(argb & 0xFF);
    }
}
