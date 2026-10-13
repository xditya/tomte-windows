namespace Tomte.Core;

/// <summary>
/// The five values the island outline is built from, in DIPs at 100% scale. Each one is driven by
/// its own spring, so any change of state is one continuous morph of the same shape.
/// </summary>
public readonly record struct IslandGeometry(double Width, double Depth, double Corner, double RampW, double RampH)
{
    public static readonly IslandGeometry Hidden = new(120, 0, 0, 0, 0);
    public static readonly IslandGeometry Compact = new(220, 38, 16, 10, 12);
    public static readonly IslandGeometry ShelfDrop = new(420, 96, 20, 16, 18);

    /// <summary>Panels and the palette share one corner and ramp; only the size differs.</summary>
    public static IslandGeometry Panel(double width, double height) => new(width, height, 22, 24, 24);

    /// <summary>Width of the outline including both ramps.</summary>
    public double OuterWidth => Width + 2 * RampW;

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
    public static readonly (double W, double H) Media = (380, 140);
    public static readonly (double W, double H) Shortcuts = (520, 300);
    public static readonly (double W, double H) Scratchpad = (520, 320);
    public static readonly (double W, double H) Shelf = (560, 260);
    public static readonly (double W, double H) Clipboard = (560, 360);
    public const double PaletteWidth = 640;
    public const double PaletteMinHeight = 150;
    public const double PaletteMaxHeight = 480;
}
