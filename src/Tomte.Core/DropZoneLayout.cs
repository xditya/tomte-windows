using System.Drawing;
using System.Globalization;
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
/// The drop zones a drag held still on the Shelf-drop pill opens, in island space (x = 0 on the island's centre line,
/// y = 0 at its top): a head row with the tomte, the file's name, its size and token estimate, and what to do; then six
/// tiles, the shelf first and the custom skill slot last, in one row when the panel is at least <see cref="WideFrom"/>
/// wide and in two rows of three below that; then a line on where the results go. Each tile has its glyph at the top
/// and its name and what a drop there sends at the foot. Made only when a provider is set up; without one a drop on the
/// pill goes on the shelf and there is nothing to hold for (<see cref="For"/> gives null).
/// </summary>
public sealed class DropZoneLayout
{
    /// <summary>The panel's width before the monitor's cap, and the width from which the six tiles share one row.</summary>
    public const float Width = 1000, WideFrom = 900;

    public const float PadTop = 14, PadSide = 16, PadBottom = 16, HeadInset = 6, HeadHeight = 24, HeadGap = 12;
    public const float TileHeight = 128, TileGap = 8, TilePad = 14, TileCorner = 18, Glyph = 22, SubGap = 3, FootHeight = 16;

    /// <summary>How far the zone under the cursor rises out of its row.</summary>
    public const float Lift = 4;

    public const string Hint = "Drop it on what you want done";
    public const string Foot = "Results land on the shelf. Each one says which model ran it and what it cost.";
    public const string EmptySlot = "Add a skill", EmptySlotSub = "Pick one in Settings";

    public static TextStyle NameStyle => new(13, 500);
    public static TextStyle FactsStyle => new(12, Color: Theme.Ink3, Tabular: true, Family: TextStyle.Mono);
    public static TextStyle HintStyle => new(12, Color: Theme.Ink3, Align: TextAlign.Trailing);
    public static TextStyle LabelStyle => new(14, 600);
    public static TextStyle SubStyle => new(11, Color: Theme.Ink3);
    public static TextStyle FootStyle => new(12, Color: Theme.Ink3);

    /// <summary>The Shelf-drop pill's words: what a drop does, and, when there are zones to open, how to get them.</summary>
    public const string PillLabel = "Drop on the shelf", PillHint = "or hold for actions";

    /// <summary>The pill's tray glyph, and its dashed ring: this far inside the outline, this thick.</summary>
    public const float PillGlyph = 16, RingInset = 6, RingWidth = 2;

    public static TextStyle PillStyle => new(13, 500);
    public static TextStyle PillHintStyle => new(13, Color: Theme.Ink3);

    /// <summary>The tomte on the head row, its art where the head's words would start.</summary>
    public static readonly MascotSeat Seat = new(PadTop + (HeadHeight / 2), PadSide + HeadInset);

    private static readonly DropZone[] Order = [DropZone.Shelf, DropZone.Summarize, DropZone.Ask, DropZone.Tables, DropZone.Rename, DropZone.Skill];

    private readonly string provider;
    private readonly string? skill;
    private readonly float left;

    private DropZoneLayout(string provider, string? skill, float width)
    {
        this.provider = provider;
        this.skill = skill;
        PanelWidth = width;
        left = -width / 2;
        bool wide = width >= WideFrom;
        int columns = wide ? 6 : 3;
        float inner = width - (2 * PadSide), tile = (inner - ((columns - 1) * TileGap)) / columns, top = PadTop + HeadHeight + HeadGap;
        var zones = new (DropZone, RectangleF)[Order.Length];
        for (int i = 0; i < Order.Length; i++)
        {
            int row = i / columns, column = i % columns;
            zones[i] = (Order[i], new RectangleF(left + PadSide + (column * (tile + TileGap)), top + (row * (TileHeight + TileGap)), tile, TileHeight));
        }

        Zones = zones;
        float foot = zones[^1].Item2.Bottom + HeadGap;
        FootSlot = new TextSlot(Foot, FootStyle, new RectangleF(left + PadSide + HeadInset, foot, inner - (2 * HeadInset), FootHeight));
        Height = foot + FootHeight + PadBottom;
    }

    /// <summary>The panel's width, as the monitor allowed it.</summary>
    public float PanelWidth { get; }

    /// <summary>The panel's height for its width: one row of tiles or two.</summary>
    public float Height { get; }

    /// <summary>The six tiles in reading order: the shelf, the four AI zones, the custom skill slot.</summary>
    public IReadOnlyList<(DropZone Zone, RectangleF Rect)> Zones { get; }

    /// <summary>The line under the tiles.</summary>
    public TextSlot FootSlot { get; }

    /// <summary>The custom slot has no skill: it is drawn empty, and a drop on it is a drop on the shelf.</summary>
    public bool SlotEmpty => skill is null;

    /// <summary>A provider is set up in settings with a model: the zones show, and a drop on one may send.</summary>
    public static bool HasProvider(TomteSettings settings) => settings.Provider is { DefaultModel.Length: > 0 };

    /// <summary>
    /// The skill the custom slot carries: the one picked in Settings, while it is on. An "on" key is the skill's name,
    /// then # and its hash. Null for none, and the slot is drawn empty.
    /// </summary>
    public static string? SlotSkill(TomteSettings settings) =>
        settings.DropZoneSkill is { } name && settings.EnabledSkills.Any(key => key.Split('#')[0] == name) ? name : null;

    /// <summary>The name a provider goes by on the tiles: the company for the two cloud APIs, the host for any other server.</summary>
    public static string ProviderName(ProviderConfig config) => config.Kind switch
    {
        ProviderKind.Anthropic => "Anthropic",
        ProviderKind.Gemini => "Google Gemini",
        _ => config.Id,
    };

    /// <summary>The zones the settings ask for, in a panel this wide: null without a provider that has a model.</summary>
    public static DropZoneLayout? For(TomteSettings settings, double width) =>
        HasProvider(settings) ? For(ProviderName(settings.Provider!), SlotSkill(settings), width) : null;

    /// <summary>
    /// The zones in a panel this wide, or null without a provider: then nothing of the AI layer is drawn.
    /// <paramref name="provider"/> is the name the tiles give it; <paramref name="skill"/> is the skill the user picked for
    /// the custom slot, null for none.
    /// </summary>
    public static DropZoneLayout? For(string? provider, string? skill, double width) =>
        string.IsNullOrEmpty(provider) ? null : new DropZoneLayout(provider, skill, (float)width);

    /// <summary>A zone's name on its tile and in its result, and the picked skill's name in its slot.</summary>
    public static string Label(DropZone zone, string? skill) => zone switch
    {
        DropZone.Summarize => "Summarize",
        DropZone.Ask => "Ask about it",
        DropZone.Tables => "Tables to CSV",
        DropZone.Rename => "Rename by content",
        DropZone.Skill => skill ?? EmptySlot,
        _ => "Keep on shelf",
    };

    /// <summary>What a drop on the zone does with the file, under its name: whether anything is sent, and where.</summary>
    public string Sub(DropZone zone) => zone switch
    {
        DropZone.Shelf => "Nothing is sent",
        DropZone.Ask => "Opens Ask · nothing sent yet",
        DropZone.Skill when skill is null => EmptySlotSub,
        _ => $"Sends to {provider}",
    };

    /// <summary>The zone under an island point; anywhere else on the panel, the empty slot too, is the shelf.</summary>
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

    /// <summary>A tile's glyph box, its name and its line, in the tile's own DIPs.</summary>
    public (RectangleF Glyph, TextSlot Label, TextSlot Sub) TileText(DropZone zone, SizeF tile)
    {
        float w = tile.Width - (2 * TilePad), sub = TilePad + 14, label = sub + SubGap + 18;
        return (new RectangleF(TilePad, TilePad, Glyph, Glyph),
            new TextSlot(Label(zone, skill), LabelStyle, new RectangleF(TilePad, tile.Height - label, w, 18)),
            new TextSlot(Sub(zone), SubStyle, new RectangleF(TilePad, tile.Height - sub, w, 14)));
    }

    /// <summary>
    /// The head row in island space: the file's name after the tomte (or from the inset with it off), its size and
    /// estimate after it, and the hint at the right end. The name gives way first when the row is short.
    /// </summary>
    public IReadOnlyList<TextSlot> Head(string name, string facts, bool mascot)
    {
        float x = left + (mascot ? Seat.After(CompactLayout.Gap) : PadSide + HeadInset), right = -left - PadSide - HeadInset;
        float hint = MathF.Ceiling(TextSlot.Measure(Hint, HintStyle, PanelWidth)), gap = CompactLayout.Gap;
        float factsWidth = facts.Length == 0 ? 0 : MathF.Ceiling(TextSlot.Measure(facts, FactsStyle, PanelWidth)) + 1;
        float room = right - hint - gap - x;
        if (room < factsWidth + gap + 60)
        {
            // Too short for all three: the hint goes, the name and the facts stay.
            (hint, room) = (0, right - x);
        }

        float nameWidth = Math.Clamp(MathF.Ceiling(TextSlot.Measure(name, NameStyle, PanelWidth)) + 1, 0, Math.Max(0, room - (factsWidth > 0 ? factsWidth + gap : 0)));
        return
        [
            new(name, NameStyle, new RectangleF(x, PadTop, nameWidth, HeadHeight)),
            new(facts, FactsStyle, new RectangleF(x + nameWidth + gap, PadTop, factsWidth, HeadHeight)),
            new(hint > 0 ? Hint : "", HintStyle, new RectangleF(right - hint, PadTop, hint, HeadHeight)),
        ];
    }

    /// <summary>
    /// The Shelf-drop pill's content in island space, one group centred across it: the tray glyph's box, the label and,
    /// with <paramref name="hint"/>, the hint after it, each a gap apart and centred down the pill.
    /// </summary>
    public static (RectangleF Glyph, TextSlot Label, TextSlot Hint) Pill(bool hint)
    {
        float depth = (float)IslandGeometry.ShelfDrop.Depth, gap = CompactLayout.Gap;
        float label = MathF.Ceiling(TextSlot.Measure(PillLabel, PillStyle, 400)) + 1;
        float more = hint ? MathF.Ceiling(TextSlot.Measure(PillHint, PillHintStyle, 400)) + 1 : 0;
        float x = -(PillGlyph + gap + label + (hint ? gap + more : 0)) / 2, words = x + PillGlyph + gap;
        return (new RectangleF(x, (depth - PillGlyph) / 2, PillGlyph, PillGlyph),
            new TextSlot(PillLabel, PillStyle, new RectangleF(words, 0, label, depth)),
            new TextSlot(hint ? PillHint : "", PillHintStyle, new RectangleF(words + label + gap, 0, more, depth)));
    }

    /// <summary>The file's size and the estimate of what it costs to send, "1.2 MB · about 1,090 tokens"; either can be unknown.</summary>
    public static string Facts(long? bytes, int? tokens) => (bytes, tokens) switch
    {
        ({ } b, { } t) => $"{ShelfLayout.Size(b)} · about {t.ToString("N0", CultureInfo.InvariantCulture)} tokens",
        ({ } b, null) => ShelfLayout.Size(b),
        (null, { } t) => $"about {t.ToString("N0", CultureInfo.InvariantCulture)} tokens",
        _ => "",
    };
}

/// <summary>
/// Whether a drag over the Shelf-drop pill has been held still long enough to open the drop zones: it is due once the
/// cursor has stayed within <see cref="StillDips"/> of where it settled for <see cref="HoldMs"/>. Any larger move starts
/// the wait again from the new place; leaving the pill ends it. Times are milliseconds on one clock, points in DIPs.
/// </summary>
public sealed class DropHold
{
    /// <summary>How long a drag rests on the pill before the zones open: long enough that a drop on the way through is not slowed.</summary>
    public const int HoldMs = 600;

    /// <summary>How far the cursor may drift and still count as held, about a hand's tremor on a mouse.</summary>
    public const float StillDips = 6;

    private Vector2 anchor;
    private long since;
    private bool on;

    /// <summary>The cursor is over the pill at this point. True when the wait (re)started here, and a timer should be armed for <see cref="HoldMs"/>.</summary>
    public bool Over(Vector2 at, long nowMs)
    {
        if (on && Vector2.Distance(at, anchor) <= StillDips)
        {
            return false;
        }

        (anchor, since, on) = (at, nowMs, true);
        return true;
    }

    /// <summary>The drag left the pill, ended, or the zones opened: nothing is waited for until it is over the pill again.</summary>
    public void Reset() => on = false;

    /// <summary>Held still for long enough by <paramref name="nowMs"/>.</summary>
    public bool Due(long nowMs) => on && nowMs - since >= HoldMs;

    /// <summary>How long is left until <see cref="Due"/>, 0 once it is or when nothing is waited for.</summary>
    public int Left(long nowMs) => on ? (int)Math.Max(0, HoldMs - (nowMs - since)) : 0;
}
