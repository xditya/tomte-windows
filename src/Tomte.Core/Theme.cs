namespace Tomte.Core;

/// <summary>The three looks the island can take, after the taskbar's: dark, light, or the taskbar's accent colour.</summary>
public enum ThemeScheme
{
    Dark,
    Light,
    Accent,
}

/// <summary>
/// Every colour the island draws, as 0xAARRGGBB. Two levers change them: <see cref="UseScheme"/> picks the surfaces and
/// inks (dark, light or accent-tinted) and <see cref="UseAccent"/> the accent. Both raise
/// <see cref="Changed"/>, and brushes made before a change keep the old colour until rebuilt, so nothing may cache
/// these in a const or a static readonly field. The overlays are the surfaces' steps as white over a dark island (black
/// over a light one), for anything drawn over something else.
/// </summary>
public static class Theme
{
    /// <summary>The accent the island starts with.</summary>
    public const uint DefaultAccent = 0xFFFF6B4A;

    /// <summary>The ink on an accent fill when the accent is light enough to carry dark text.</summary>
    public const uint DarkOnAccent = 0xFF1A0A05;

    /// <summary>The coral as it is on a light island: darker, so a selection ring and matched letters still read.</summary>
    public const uint DeepCoral = 0xFFC23E23;

    /// <summary>A settings card, a step above the settings window's own fill. The settings window stays dark.</summary>
    public const uint SettingsCard = 0xFF1F2025;

    /// <summary>The settings window's fill and its segmented controls' track.</summary>
    public const uint Segmented = 0xFF15161A;

    /// <summary>A shelf tile's sketch of a document: the page, its heading bar and its lines. The same in every scheme.</summary>
    public const uint Paper = 0xFFF4F1EA, PaperInk = 0xFF2A2825, PaperLine = 0xFFC9C4BA;

    /// <summary>A shelf tile's icon square by kind of file.</summary>
    public const uint KindPdf = 0xFFC0392B, KindPicture = 0xFF3E8E6A, KindDocument = 0xFF3A6EA5, KindLink = 0xFF8C5BD6, KindOther = 0xFF5D6D7E;

    /// <summary>The dimming over the screen while picking a region.</summary>
    public const uint Scrim = 0x59000000;

    private const uint White = 0xFFFFFFFF, Black = 0xFF000000;

    private static readonly Set Dark = new(
        Island: 0xFF111214, Card: 0xFF1A1B1F, Raised: 0xFF24262B, RaisedHi: 0xFF2A2C31, Line: 0xFF2A2C31, LineStrong: 0xFF34363C, Dash: 0xFF3A3C42,
        Track: 0xFF2E3036, Silence: 0xFF4A4C52, Ink: 0xFFF2F0EB, Ink2: 0xFFB1AFA9, Ink3: 0xFF8E8C86,
        Ok: 0xFF6FD39A, Warn: 0xFFF0B35A, Danger: 0xFFFF9A85, Link: 0xFF7CC4FF, Folder: 0xFFE9C46A,
        Shadow: Black, ShadowOpacity: 0.35f, Over: White);

    private static readonly Set LightSet = new(
        Island: 0xFFF3F2EF, Card: 0xFFFFFFFF, Raised: 0xFFE9E7E2, RaisedHi: 0xFFFFFFFF, Line: 0xFFE0DED8, LineStrong: 0xFFCFCCC5, Dash: 0xFFB9B5AD,
        Track: 0xFFDDDAD3, Silence: 0xFFA8A49C, Ink: 0xFF1C1B19, Ink2: 0xFF4F4D48, Ink3: 0xFF67645D,
        Ok: Deepened(0xFF6FD39A, 0xFFF3F2EF), Warn: Deepened(0xFFF0B35A, 0xFFF3F2EF), Danger: Deepened(0xFFFF9A85, 0xFFF3F2EF), Link: 0xFF1F6FB8, Folder: 0xFFB8860B,
        Shadow: 0xFF1E1E28, ShadowOpacity: 0.18f, Over: Black);

    private static Set set = Dark;
    private static uint chosenAccent = DefaultAccent, systemLight;

    /// <summary>Something here changed: views that built brushes from these colours rebuild them.</summary>
    public static event Action? Changed;

    /// <summary>Which of the three looks is in use.</summary>
    public static ThemeScheme Scheme { get; private set; }

    /// <summary>The island, pill and panel fill.</summary>
    public static uint Island => set.Island;

    /// <summary>Sections inside a panel, input fields, the tab rail's track.</summary>
    public static uint Card => set.Card;

    /// <summary>Tiles, selected rows, secondary buttons, keycaps.</summary>
    public static uint Raised => set.Raised;

    /// <summary>The current tab's pill, a pressed secondary button.</summary>
    public static uint RaisedHi => set.RaisedHi;

    /// <summary>Hairlines and dividers.</summary>
    public static uint Line => set.Line;

    /// <summary>Outlined chips, a keycap's border, a field's border.</summary>
    public static uint LineStrong => set.LineStrong;

    /// <summary>Dashed "drop here" outlines and empty slots.</summary>
    public static uint Dash => set.Dash;

    /// <summary>The surfaces above as white over a dark island (black over a light one), for drawing over the screen or a picture.</summary>
    public static uint CardOver => Over(0x0A);

    /// <inheritdoc cref="CardOver"/>
    public static uint RaisedOver => Over(0x14);

    /// <inheritdoc cref="CardOver"/>
    public static uint LineOver => Over(0x1A);

    /// <inheritdoc cref="CardOver"/>
    public static uint LineStrongOver => Over(0x24);

    /// <inheritdoc cref="CardOver"/>
    public static uint DashOver => Over(0x29);

    /// <summary>The empty part of a meter or progress track.</summary>
    public static uint Track => set.Track;

    /// <summary>A waveform bar in silence, an unchecked checkbox's border.</summary>
    public static uint Silence => set.Silence;

    /// <summary>A folder's glyph.</summary>
    public static uint Folder => set.Folder;

    /// <summary>Text: primary, secondary and tertiary. All three read at 4.5:1 on <see cref="Island"/>, <see cref="Card"/> and <see cref="Raised"/>.</summary>
    public static uint Ink => set.Ink;

    /// <inheritdoc cref="Ink"/>
    public static uint Ink2 => set.Ink2;

    /// <inheritdoc cref="Ink"/>
    public static uint Ink3 => set.Ink3;

    /// <summary>Connected, saved, a provider that answers.</summary>
    public static uint Ok => set.Ok;

    /// <summary>Something did not work, or wants a second press; its faint fill.</summary>
    public static uint Warn => set.Warn;

    /// <inheritdoc cref="Warn"/>
    public static uint WarnSoft => Soft(set.Warn);

    /// <summary>Destructive text buttons.</summary>
    public static uint Danger => set.Danger;

    /// <summary>A link's glyph. Never a fill.</summary>
    public static uint Link => set.Link;

    /// <summary>The state layer over a hovered target, about 8% white (black on a light island); over a pressed one, about 3%.</summary>
    public static uint HoverLayer => Over(set.Over == White ? 0x15u : 0x0Fu);

    /// <inheritdoc cref="HoverLayer"/>
    public static uint PressedLayer => Over(0x08);

    /// <summary>The island's shadow and how strong it is.</summary>
    public static uint Shadow => set.Shadow;

    /// <inheritdoc cref="Shadow"/>
    public static float ShadowOpacity => set.ShadowOpacity;

    /// <summary>The one accent: AI, the selection and focus rings, checked controls, the timer, the recording dot.</summary>
    public static uint Accent { get; private set; } = DefaultAccent;

    /// <summary>The accent at 20%: the halo round the recording dot, the glow under a hot drop zone.</summary>
    public static uint AccentSoft { get; private set; } = Soft(DefaultAccent);

    /// <summary>Text and glyphs on an <see cref="Accent"/> fill.</summary>
    public static uint OnAccent { get; private set; } = DarkOnAccent;

    /// <summary>
    /// Makes <paramref name="rgb"/> (its alpha ignored) the chosen accent. What is drawn follows the scheme: as chosen on
    /// a dark island, darkened until it reads at 4.5:1 on a light one, and on an accent island the system's light tint
    /// instead. The soft accent is its 20%, and the ink on it whichever of light and dark stands out more.
    /// </summary>
    public static void UseAccent(uint rgb)
    {
        chosenAccent = 0xFF000000 | (rgb & 0xFFFFFF);
        Apply();
    }

    /// <summary>
    /// Switches the surfaces and inks. <paramref name="accentDark"/> and <paramref name="accentLight"/> are the system
    /// accent's deep and light shades (UISettings' AccentDark2 and AccentLight2), which the accent scheme is made from.
    /// </summary>
    public static void UseScheme(ThemeScheme scheme, uint accentDark = 0, uint accentLight = 0)
    {
        Scheme = scheme;
        systemLight = 0xFF000000 | accentLight;
        set = scheme switch
        {
            ThemeScheme.Light => LightSet,
            ThemeScheme.Accent => FromAccent(0xFF000000 | accentDark, systemLight),
            _ => Dark,
        };
        Apply();
    }

    /// <summary>
    /// The scheme for a choice in Settings: following the taskbar is dark, light, or the accent scheme when Windows
    /// shows its accent on the taskbar (which it allows only in dark mode).
    /// </summary>
    public static ThemeScheme SchemeFor(ThemeChoice choice, bool systemLight, bool accentOnTaskbar) => choice switch
    {
        ThemeChoice.Dark => ThemeScheme.Dark,
        ThemeChoice.Light => ThemeScheme.Light,
        _ when systemLight => ThemeScheme.Light,
        _ => accentOnTaskbar ? ThemeScheme.Accent : ThemeScheme.Dark,
    };

    /// <summary>A light or dark island's fill and hairline, a desktop and the taskbar under it, for the settings window's previews.</summary>
    public static (uint Island, uint Line, uint Desktop, uint Taskbar) Sample(bool light) =>
        light ? (LightSet.Island, LightSet.LineStrong, 0xFFC9D3DC, 0xFFEEEEEE) : (Dark.Island, Dark.LineStrong, 0xFF2B3440, 0xFF1C1C1C);

    /// <summary><paramref name="argb"/> with its alpha replaced by <paramref name="opacity"/> (0 to 1).</summary>
    public static uint WithOpacity(uint argb, float opacity) => (uint)Math.Round(Math.Clamp(opacity, 0, 1) * 255) << 24 | (argb & 0xFFFFFF);

    /// <summary>WCAG contrast ratio of two opaque colours, 1 to 21.</summary>
    public static double Contrast(uint a, uint b)
    {
        double la = Luminance(a), lb = Luminance(b);
        return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
    }

    private static uint Over(uint alpha) => alpha << 24 | (set.Over & 0xFFFFFF);

    private static void Apply()
    {
        Accent = Scheme switch
        {
            ThemeScheme.Light when chosenAccent == DefaultAccent => DeepCoral,
            ThemeScheme.Light => Deepened(chosenAccent, Island),
            // The light tint reads on the deep shade; pushed lighter only if a system colour is too dark for a ring.
            ThemeScheme.Accent => Toward(systemLight, White, Island, 3),
            _ => Toward(chosenAccent, White, Island, 3),
        };
        AccentSoft = Soft(Accent);
        uint light = Scheme == ThemeScheme.Light ? White : set.Ink;
        OnAccent = Contrast(Accent, DarkOnAccent) >= Contrast(Accent, light) ? DarkOnAccent : light;
        Changed?.Invoke();
    }

    // The accent scheme: the island takes the taskbar's deep shade, kept dark enough for light text, and every step above
    // it leans toward the light tint, so the panel reads as one colour family.
    private static Set FromAccent(uint deep, uint light)
    {
        const uint ink = 0xFFF2F5F8;
        uint island = Toward(Mix(deep, Dark.Island, 0.35), Black, ink, 10);
        uint Step(double t) => Mix(island, light, t);
        uint raised = Step(0.14);
        uint Text(double t) => Toward(Mix(ink, island, t), ink, raised, 4.5);
        return new Set(
            Island: island, Card: Step(0.07), Raised: raised, RaisedHi: Step(0.19), Line: Step(0.11), LineStrong: Step(0.24), Dash: Step(0.32),
            Track: Step(0.2), Silence: Step(0.45), Ink: ink, Ink2: Text(0.22), Ink3: Text(0.4),
            Ok: Dark.Ok, Warn: Dark.Warn, Danger: Dark.Danger, Link: Mix(light, White, 0.6), Folder: Dark.Folder,
            Shadow: 0xFF000A19, ShadowOpacity: 0.4f, Over: White);
    }

    // A colour for text or a ring on a light island: darkened until it reads at 4.5:1 there.
    private static uint Deepened(uint argb, uint island) => Toward(argb, Black, island, 4.5);

    // Moves `argb` toward `target` in small steps until it stands at `ratio` against `against` (or reaches the target).
    private static uint Toward(uint argb, uint target, uint against, double ratio)
    {
        uint c = argb;
        for (int i = 1; i <= 50 && Contrast(c, against) < ratio; i++)
        {
            c = Mix(argb, target, i / 50.0);
        }

        return c;
    }

    // `a` moved `t` (0 to 1) of the way to `b`, channel by channel; the result is opaque.
    private static uint Mix(uint a, uint b, double t)
    {
        uint Channel(int shift) => (uint)Math.Round((a >> shift & 0xFF) + (((b >> shift & 0xFF) - (double)(a >> shift & 0xFF)) * t));
        return 0xFF000000 | Channel(16) << 16 | Channel(8) << 8 | Channel(0);
    }

    private static uint Soft(uint argb) => 0x33000000 | (argb & 0xFFFFFF);

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

    // One scheme's colours. Over is the colour of the overlays and state layers (white on a dark island, black on a light one).
    private sealed record Set(
        uint Island, uint Card, uint Raised, uint RaisedHi, uint Line, uint LineStrong, uint Dash,
        uint Track, uint Silence, uint Ink, uint Ink2, uint Ink3,
        uint Ok, uint Warn, uint Danger, uint Link, uint Folder,
        uint Shadow, float ShadowOpacity, uint Over);
}
