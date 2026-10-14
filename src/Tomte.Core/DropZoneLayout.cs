using System.Drawing;
using System.Numerics;
using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>Where a drop on the Shelf-drop surface goes: onto the shelf, or into one of the AI zones.</summary>
public enum DropZone
{
    Shelf,
    Summarize,
    Ask,
    Tables,
    Rename,
    Skill,
}

/// <summary>A run of one or more lines with its box's top-left corner, in the coordinates of the layout that made it.</summary>
public readonly record struct TextAt(TextRun Run, Vector2 At);

/// <summary>
/// The Shelf-drop surface with the AI zones, in island space (x = 0 on the island's centre line, y = 0 at its top):
/// the shelf target right of the tomte, the zones in a row to its right, each as wide as its longest word, and under
/// them one line that says what the zone under the cursor does with the file. Made only when a provider is set up;
/// without one the surface is the shelf's alone (<see cref="For"/> gives null).
/// </summary>
public sealed class DropZoneLayout
{
    public const float RowHeight = 52, Gap = 6, LineTop = 68, LineHeight = 14, ZonePad = 5, MinZone = 40, MaxZone = 72;

    public static readonly TextStyle ZoneStyle = new(11, Color: TextStyle.SecondaryColor, Align: TextAlign.Center, LineHeight: 13);
    public static readonly TextStyle TargetStyle = new(12, 600, Color: TextStyle.SecondaryColor, Align: TextAlign.Center, LineHeight: 15);
    public static readonly TextStyle LineStyle = new(11, Color: TextStyle.SecondaryColor);

    private const float Glyph = 16, GlyphGap = 4;

    private readonly string provider;
    private readonly string? skill;

    private DropZoneLayout(string provider, string? skill, RectangleF target, IReadOnlyList<(DropZone Zone, RectangleF Rect)> zones)
    {
        this.provider = provider;
        this.skill = skill;
        Target = target;
        Zones = zones;
    }

    /// <summary>The shelf's own target, narrower than without zones.</summary>
    public RectangleF Target { get; }

    /// <summary>The AI zones left to right, the custom skill slot last.</summary>
    public IReadOnlyList<(DropZone Zone, RectangleF Rect)> Zones { get; }

    /// <summary>The custom slot has no skill: it is drawn empty, and a drop on it is a drop on the shelf.</summary>
    public bool SlotEmpty => skill is null;

    /// <summary>The status line's slot, under the row, as wide as the target and the zones together.</summary>
    public RectangleF Line => new(Target.X, LineTop, Zones[^1].Rect.Right - Target.X, LineHeight);

    /// <summary>A provider is set up in settings with a model: the zones show, and a drop on one may send.</summary>
    public static bool HasProvider(TomteSettings settings) => settings.Provider is { DefaultModel.Length: > 0 };

    /// <summary>
    /// The skill the custom slot carries: the one picked in Settings, while it is on. An "on" key is the skill's name,
    /// then # and its hash. Null for none, and the slot is drawn empty.
    /// </summary>
    public static string? SlotSkill(TomteSettings settings) =>
        settings.DropZoneSkill is { } name && settings.EnabledSkills.Any(key => key.Split('#')[0] == name) ? name : null;

    /// <summary>The zones the settings ask for, for a surface this wide and deep: null without a provider that has a model.</summary>
    public static DropZoneLayout? For(TomteSettings settings, double width, double depth) =>
        HasProvider(settings) ? For(settings.Provider!.Id, SlotSkill(settings), width, depth) : null;

    /// <summary>
    /// The zones for a surface this wide and deep, or null without a provider: then nothing of the AI layer is drawn.
    /// <paramref name="skill"/> is the skill the user picked for the custom slot, null for none.
    /// </summary>
    public static DropZoneLayout? For(string? provider, string? skill, double width, double depth)
    {
        if (string.IsNullOrEmpty(provider))
        {
            return null;
        }

        var all = ShelfLayout.DropTarget(width, depth);
        DropZone[] kinds = [DropZone.Summarize, DropZone.Ask, DropZone.Tables, DropZone.Rename, DropZone.Skill];
        var widths = kinds.Select(k => ZoneWidth(Label(k, skill))).ToArray();
        float x = all.Right, top = all.Y;
        var zones = new (DropZone, RectangleF)[kinds.Length];
        for (int i = kinds.Length - 1; i >= 0; i--)
        {
            x -= widths[i];
            zones[i] = (kinds[i], new RectangleF(x, top, widths[i], RowHeight));
            x -= Gap;
        }

        return new DropZoneLayout(provider, skill, new RectangleF(all.X, top, x - all.X, RowHeight), zones);
    }

    /// <summary>A zone's word or words on the surface, and the picked skill's name in its slot, "+ skill" without one.</summary>
    public static string Label(DropZone zone, string? skill) => zone switch
    {
        DropZone.Summarize => "Summarize",
        DropZone.Ask => "Ask about it",
        DropZone.Tables => "Tables to CSV",
        DropZone.Rename => "Rename by content",
        DropZone.Skill => skill ?? EmptySlot,
        _ => ShelfLayout.DropHere,
    };

    public const string EmptySlot = "+ skill";

    /// <summary>What a drop does, for the line under the row: the zone and where the file goes.</summary>
    public string Says(DropZone zone) => zone switch
    {
        DropZone.Summarize => $"Summarize: sends the file to {provider}",
        DropZone.Ask => "Ask about it: opens the palette with the file, nothing is sent yet",
        DropZone.Tables => $"Tables to CSV: sends the file to {provider}",
        DropZone.Rename => $"Rename by content: sends the file to {provider}",
        DropZone.Skill when skill is not null => $"{skill}: sends the file to {provider} with this skill",
        _ => "Drop to shelf: keeps it on the shelf, nothing is sent",
    };

    /// <summary>The zone under an island point; anywhere else on the surface, the empty slot too, is the shelf, as without zones.</summary>
    public DropZone At(Vector2 point)
    {
        foreach (var (zone, rect) in Zones)
        {
            if (rect.Contains(point.X, point.Y) && !(zone == DropZone.Skill && SlotEmpty))
            {
                return zone;
            }
        }

        return DropZone.Shelf;
    }

    /// <summary>A zone's label, wrapped at its words into up to three lines and centred in the zone.</summary>
    public TextAt ZoneText(DropZone zone, RectangleF rect) =>
        Centred(Label(zone, skill), zone == DropZone.Skill && SlotEmpty ? ZoneStyle with { Color = TextStyle.TertiaryColor } : ZoneStyle, rect, 3, 0);

    /// <summary>
    /// The target's words, centred: under the folder glyph, whose top-left corner is the second value, when they fit
    /// on one line; on two lines without the glyph when the zones leave the target narrow.
    /// </summary>
    public (TextAt Label, Vector2? Glyph) TargetText()
    {
        bool oneLine = TextSlot.Measure(ShelfLayout.DropHere, TargetStyle, 1000) <= Target.Width - 2 * ZonePad;
        if (!oneLine)
        {
            return (Centred(ShelfLayout.DropHere, TargetStyle, Target, 2, 0), null);
        }

        var label = Centred(ShelfLayout.DropHere, TargetStyle, Target, 1, Glyph + GlyphGap);
        return (label, new Vector2(Target.X + (Target.Width - Glyph) / 2, label.At.Y - Glyph - GlyphGap));
    }

    public TextSlot LineText(DropZone hot) => new(Says(hot), LineStyle, Line);

    private static float ZoneWidth(string label)
    {
        float longest = label.Split(' ').Max(word => TextSlot.Measure(word, ZoneStyle, MaxZone));
        return Math.Clamp(MathF.Ceiling(longest) + 2 * ZonePad, MinZone, MaxZone);
    }

    /// <summary>A run in a box, centred across and, with <paramref name="above"/> room for something over it, down.</summary>
    private static TextAt Centred(string text, TextStyle style, RectangleF box, int lines, float above)
    {
        var run = new TextRun(text, style, box.Width - 2 * ZonePad, lines);
        using var layout = TextLayout.Of(run);
        float top = box.Y + (box.Height - layout.Height - above) / 2 + above;
        return new TextAt(run, new Vector2(box.X + ZonePad, top));
    }
}
