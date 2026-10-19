namespace Tomte.Core;

/// <summary>
/// The values the island outline is built from, in DIPs at 100% scale. Each one is driven by its own
/// spring, so any change of state is one continuous morph of the same shape. The floating pill is
/// width, depth and corner. The two ramps are the fillets of the attached silhouette and stay 0 otherwise.
/// </summary>
public readonly record struct IslandGeometry(double Width, double Depth, double Corner, double RampW = 0, double RampH = 0)
{
    public static readonly IslandGeometry Hidden = new(120, 0, 0);

    // Corner 19, half the depth, so Compact is a capsule. The notch's Compact corner is 16.
    public static readonly IslandGeometry Compact = new(220, 38, 19);

    /// <summary>Desktop peek: Compact's depth and corner around the tomte alone, its inset on both sides (37 wide).</summary>
    public static readonly IslandGeometry Peek = new(2 * MascotArt.InsetX + MascotArt.SizeDips, 38, 19);

    public static readonly IslandGeometry ShelfDrop = new(420, 96, 20);

    /// <summary>Panels and the palette share one corner; only the size differs.</summary>
    public static IslandGeometry Panel(double width, double height) => new(width, height, 22);

    /// <summary>The resting pill grown a little while the pointer rests on it: 6 wider, 2 deeper, and a corner that keeps a capsule a capsule.</summary>
    public IslandGeometry Swollen() => this with { Width = Width + 6, Depth = Depth + 2, Corner = Corner + 1 };

    /// <summary>Width of the outline including both ramps.</summary>
    public double OuterWidth => Width + 2 * RampW;

    /// <summary>The same state drawn as the notch: the notch's own corner and fillet ramps for it.</summary>
    // keyed by the corner, which no two states share; give the table a state column if two ever do.
    public IslandGeometry Attached() => Corner switch
    {
        19 => this with { Corner = 16, RampW = 10, RampH = 12 },
        20 => this with { RampW = 16, RampH = 18 },
        22 => this with { RampW = 24, RampH = 24 },
        _ => this,
    };

    /// <summary>Capture keeps the island hidden under the overlay, so it shares Hidden's geometry.</summary>
    public static IslandGeometry For(IslandState state, double panelWidth, double panelHeight) => state switch
    {
        IslandState.Compact => Compact,
        IslandState.ShelfDrop => ShelfDrop,
        IslandState.Panels or IslandState.Palette => Panel(panelWidth, panelHeight),
        _ => Hidden,
    };
}

/// <summary>Panel sizes in DIPs (width, height). The caller caps them at 60% x 50% of the monitor.</summary>
public static class PanelSize
{
    /// <summary>Every panel is this wide, so the tab strip and its labels stay where they are from tab to tab; only the height differs.</summary>
    public const double Width = 560;

    public static readonly (double W, double H) Media = (Width, 140);
    public static readonly (double W, double H) Shortcuts = (Width, 300);
    public static readonly (double W, double H) Scratchpad = (Width, 320);
    public static readonly (double W, double H) Shelf = (Width, 260);
    public static readonly (double W, double H) Clipboard = (Width, 360);
    public static readonly (double W, double H) History = (Width, 400);
    public const double PaletteWidth = 640;

    /// <summary>The search box and the voice card are narrower than the AI box.</summary>
    public const double SearchWidth = 680, VoiceWidth = 620;
    public const double PaletteMinHeight = 150;
    public const double PaletteMaxHeight = 480;

    /// <summary>A size capped at 60% of the monitor's width and 50% of its height, both in DIPs.</summary>
    public static (double W, double H) Fit((double W, double H) size, (double W, double H) monitor) =>
        (Math.Min(size.W, 0.6 * monitor.W), Math.Min(size.H, 0.5 * monitor.H));

    public static (double W, double H) Of(PanelId id) => id switch
    {
        PanelId.Shelf => Shelf,
        PanelId.Shortcuts => Shortcuts,
        PanelId.Media => Media,
        PanelId.Scratchpad => Scratchpad,
        PanelId.Clipboard => Clipboard,
        PanelId.History => History,
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    /// <summary>
    /// Palette height for an answer given as lines of text, until a text renderer measures the real
    /// height: about 88 characters per line at the palette's width, 21 DIP per line, rounded up to 24 DIP
    /// steps so a streaming answer retargets the spring every few lines, not every token.
    /// </summary>
    public static double PaletteHeightFor(IEnumerable<string> lines)
    {
        double count = lines.Sum(line => Math.Max(1, Math.Ceiling(line.Length / 88.0)));
        double needed = PaletteMinHeight + Math.Ceiling(count * 21 / 24) * 24;
        return Math.Clamp(needed, PaletteMinHeight, PaletteMaxHeight);
    }
}
