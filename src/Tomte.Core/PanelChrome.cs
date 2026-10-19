using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>
/// A shortcut drawn as one key: the whole chord in one cap with spaces ("Win Alt Space"), mono, on a raised fill with a
/// stronger hairline round it.
/// </summary>
public static class Keycap
{
    public const float PadX = 6, Height = 21, Corner = 6, Border = 1;

    public static readonly TextStyle Style = new(11, Color: Theme.Ink2, Align: TextAlign.Center, Family: TextStyle.Mono);

    /// <summary>The chord as the cap shows it, or null when <paramref name="chord"/> is not a shortcut (none set).</summary>
    public static string? Words(string chord) => Hotkey.TryParse(chord, out var key, out _) ? key.ToString().Replace('+', ' ') : null;

    public static float Width(string words, float max) => MathF.Ceiling(TextSlot.Measure(words, Style, max)) + (2 * (PadX + Border));
}

/// <summary>
/// A round chip: an outlined capsule with an icon and a short word (an attachment, a filter). One that holds something is
/// filled instead.
/// </summary>
public static class Chip
{
    public const float Height = 30, PadX = 10, Icon = 14, IconGap = 5, Border = 1;

    public static readonly TextStyle Style = TextStyle.Secondary;

    public static float Width(string label, float max) => PadX + Icon + IconGap + MathF.Ceiling(TextSlot.Measure(label, Style, max)) + PadX;
}

/// <summary>A section inside a panel: a card with this corner and padding, this far from the next.</summary>
public static class Section
{
    public const float Corner = 18, Pad = 14, Gap = 10, HeadHeight = 16, HeadGap = 10;

    /// <summary>A panel body's padding at its sides and bottom.</summary>
    public const float BodyPad = 16, BodyTop = 12;

    /// <summary>A tile or row inside a section, and a rectangular button.</summary>
    public const float TileCorner = 12, ButtonCorner = 10;

    /// <summary>A section's name, and the count or note beside it.</summary>
    public static readonly TextStyle Title = TextStyle.Title;
    public static readonly TextStyle Note = new(12, Color: Theme.Ink3);
}
