using System.Drawing;
using System.Globalization;
using System.Numerics;
using Tomte.Core.Interaction;
using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>A result file in the panel: its name, its line under the name, and its icon's colour (RRGGBB).</summary>
public sealed record ResultTile(string Name, string Meta, uint Color)
{
    public const string OnShelf = "On the shelf · drag it out";

    /// <summary>The line once a drag out took the file off the shelf.</summary>
    public const string OffShelf = "Dragged out · no longer on the shelf";

    public static ResultTile Of(ShelfItem item) => new(item.Name, OnShelf, ShelfLayout.KindColor(item));
}

/// <summary>
/// What a drop zone's panel shows: the zone and the file, where the request went, one line of progress or of what
/// went wrong, the answer, the result files, and while the size question is open its choices, the last of which
/// (cancel) is the default.
/// </summary>
public sealed record DropResult(string Title, string Indicator, string? Line, bool Problem, string Body, IReadOnlyList<ResultTile> Tiles, IReadOnlyList<string> Choices)
{
    public static readonly DropResult Empty = new("", "", null, false, "", [], []);

    /// <summary>The one choice of a finished result that offers no other: it takes the island down, as Esc does.</summary>
    public const string Close = "Close";

    /// <summary>The panel's last state, which stays until dismissed: with a Close row unless it already offers its own choices.</summary>
    public DropResult Closable() => Choices.Count == 0 ? this with { Choices = [Close] } : this;
}

/// <summary>
/// The drop zone's panel in the Palette state, in the panel's own DIPs (x = 0 at its left edge, y = 0 at its top):
/// a head row right of the tomte with the zone and file on the left and where the request went on the right, a line
/// under it, then the answer, the choices as full-width buttons with their words centred, the last of them (the
/// default) filled, and the result files as tiles. The panel is the palette's width and grows with what it shows, up to
/// the palette's height or the monitor's cap, whichever is less.
/// The answer is read as Markdown (<see cref="Markdown"/>): paragraphs, bold, italic, headings as bold lines, list items
/// with their marker hanging left of the text, code as it is. It wraps to the panel and scrolls in its band when it is longer.
/// </summary>
public sealed class DropResultLayout
{
    public const float Pad = 16, HeadTop = 12, HeadHeight = 18, LineTop = 34, LineHeight = 14, BodyTop = 58;
    public const float TileWidth = 240, TileHeight = 44, TileIcon = 24, ChoiceHeight = 32, Gap = 8, Corner = 8;

    /// <summary>A choice's words, and the default's on its button filled with <see cref="StateLook.Accent"/>.</summary>
    public static readonly TextStyle ChoiceStyle = TextStyle.Secondary with { Color = TextStyle.WhiteColor, Align = TextAlign.Center };
    public static readonly TextStyle DefaultChoiceStyle = ChoiceStyle with { Color = StateLook.CheckedText, Weight = 600 };

    /// <summary>A list level's indent, which is also how far a bullet's text hangs right of the bullet.</summary>
    public const float Indent = 16;

    public const float BlockGap = 8, ItemGap = 3;

    public static readonly TextStyle IndicatorStyle = TextStyle.Secondary with { Align = TextAlign.Trailing };
    public static readonly TextStyle BodyStyle = TextStyle.Primary with { LineHeight = 19 };
    public static readonly TextStyle CodeStyle = new(12, Color: 0xFFD6D6DE, Family: TextStyle.Mono, LineHeight: 17);
    public static readonly TextStyle TileName = new(12.5f, 600);

    /// <summary>A wheel notch scrolls the answer three of its lines, as Windows scrolls by default.</summary>
    public const float WheelStep = 3 * 19;

    /// <summary>
    /// The most lines of an answer the panel lays out. Each block is one surface, and 250 lines of 19 DIP stay inside
    /// Direct3D's 16384 pixel texture even at 300%; it also bounds what a streaming update lays out again.
    /// </summary>
    public const int MaxLines = 250;

    /// <summary>The most characters of an answer that are read at all, so a huge answer costs no more than this.</summary>
    public const int MaxChars = 30_000;

    /// <summary>The line under an answer that was cut at <see cref="MaxLines"/> or <see cref="MaxChars"/>.</summary>
    public const string Rest = "The answer goes on. All of it is in its file on the shelf.";

    public static readonly TextStyle RestStyle = TextStyle.Secondary with { LineHeight = 19 };

    private readonly List<RectangleF> tiles = [], choices = [];
    private readonly List<TextSlot> slots = [];

    /// <summary>The tomte beside the head row: centred on it, its art as far in from the left edge as the answer below.</summary>
    public static readonly MascotSeat Seat = new(HeadTop + (HeadHeight / 2), Pad);

    public DropResultLayout(DropResult result, float width, bool mascot, float maxHeight = (float)PanelSize.PaletteMaxHeight)
    {
        float left = mascot ? Seat.After(10) : Pad, inner = width - 2 * Pad;
        float indicator = MathF.Ceiling(TextSlot.Measure(result.Indicator, IndicatorStyle, inner / 2));
        slots.Add(new(result.Title, TextStyle.Title, new(left, HeadTop, Math.Max(0, width - Pad - indicator - 10 - left), HeadHeight)));
        slots.Add(new(result.Indicator, IndicatorStyle, new(width - Pad - indicator, HeadTop, indicator, HeadHeight)));
        // What went wrong is a sentence or three: it takes the answer's place, wrapped, in the warning colour.
        bool problem = result.Problem && result.Body.Length == 0 && result.Line is { Length: > 0 };
        string shown = problem ? result.Line! : result.Body;
        if (!problem && result.Line is { Length: > 0 } line)
        {
            slots.Add(new(line, TextStyle.Small, new(left, LineTop, width - Pad - left, LineHeight)));
        }

        int perRow = Math.Max(1, (int)((inner + Gap) / (TileWidth + Gap)));
        float below = result.Choices.Count * (ChoiceHeight + Gap) + (result.Tiles.Count == 0 ? 0 : (result.Tiles.Count + perRow - 1) / perRow * (TileHeight + Gap));
        float y = problem ? LineTop + 4 : BodyTop;
        if (shown.Length > 0)
        {
            // A cut never splits a character in two.
            int read = shown.Length <= MaxChars ? shown.Length : char.IsHighSurrogate(shown[MaxChars - 1]) ? MaxChars - 1 : MaxChars;
            (Body, BodyHeight) = Lay(Markdown.Parse(shown[..read]), problem ? BodyStyle with { Color = TextStyle.WarnColor } : BodyStyle, inner, read < shown.Length);
            // What fits above the choices and tiles; the rest scrolls.
            float room = Math.Max(BodyStyle.LineHeight, maxHeight - y - below - Pad - 4);
            Band = new RectangleF(0, y, width, Math.Min(MathF.Ceiling(BodyHeight), room));
            y += Band.Height + Gap + 4;
        }

        foreach (string choice in result.Choices)
        {
            var row = new RectangleF(Pad, y, inner, ChoiceHeight);
            choices.Add(row);
            var style = choices.Count == result.Choices.Count ? DefaultChoiceStyle : ChoiceStyle;
            slots.Add(new(choice, style, new(row.X + 12, row.Y, row.Width - 24, row.Height)));
            y += ChoiceHeight + Gap;
        }

        for (int i = 0; i < result.Tiles.Count; i++)
        {
            var tile = new RectangleF(Pad + i % perRow * (TileWidth + Gap), y + i / perRow * (TileHeight + Gap), TileWidth, TileHeight);
            tiles.Add(tile);
            float text = tile.X + 10 + TileIcon + 10;
            slots.Add(new(result.Tiles[i].Name, TileName, new(text, tile.Y + 7, tile.Right - 10 - text, 16)));
            slots.Add(new(result.Tiles[i].Meta, TextStyle.Small, new(text, tile.Y + 24, tile.Right - 10 - text, 13)));
        }

        float bottom = tiles.Count > 0 ? tiles[^1].Bottom + Gap : y;
        Height = Math.Clamp(MathF.Ceiling(bottom + Pad - Gap), Math.Min((float)PanelSize.PaletteMinHeight, maxHeight), maxHeight);
    }

    /// <summary>The head row, the line, each choice's sentence and each tile's name and line, in that order.</summary>
    public IReadOnlyList<TextSlot> Slots => slots;

    /// <summary>
    /// The answer's runs, list markers as runs of their own, each placed from the top-left of <see cref="Band"/>; empty
    /// when there is no answer. Each wraps inside the panel's padding.
    /// </summary>
    public IReadOnlyList<TextAt> Body { get; } = [];

    /// <summary>Where the answer shows, the panel's full width so nothing at its edges is clipped; it scrolls inside.</summary>
    public RectangleF Band { get; }

    /// <summary>All of the answer, top of the first run to the bottom of the last.</summary>
    public float BodyHeight { get; }

    public float MaxScroll => Math.Max(0, BodyHeight - Band.Height);

    /// <summary>A wheel delta (120 a notch, positive away from the user) moves the answer three lines a notch, back toward its start.</summary>
    public float Scrolled(float scroll, int delta) => Math.Clamp(scroll - delta / 120f * WheelStep, 0, MaxScroll);

    public IReadOnlyList<RectangleF> Tiles => tiles;

    public IReadOnlyList<RectangleF> Choices => choices;

    /// <summary>A tile's icon square.</summary>
    public static RectangleF Icon(RectangleF tile) => new(tile.X + 10, tile.Y + (tile.Height - TileIcon) / 2, TileIcon, TileIcon);

    /// <summary>The panel's height for what it shows, between the palette's smallest and largest.</summary>
    public float Height { get; }

    /// <summary>
    /// Markdown blocks one under the other inside <paramref name="width"/>, from x = <see cref="Pad"/>: a list item's
    /// marker at its level's indent and its text hanging right of it, so wrapped lines start under the text, not under
    /// the marker. Items of one list sit closer together than other blocks. At most <paramref name="maxLines"/> lines; an
    /// answer that goes on, or that was <paramref name="cut"/> before, ends with <paramref name="rest"/>. Each run is
    /// measured by <paramref name="measure"/>, <see cref="Measure"/> unless the caller keeps what it measured before.
    /// </summary>
    internal static (IReadOnlyList<TextAt> Runs, float Height) Lay(IReadOnlyList<MarkdownBlock> blocks, TextStyle style, float width, bool cut, int maxLines = MaxLines, string rest = Rest,
        Func<TextRun, (float Width, float Height, int Lines, bool Trimmed)>? measure = null)
    {
        measure ??= Measure;
        var runs = new List<TextAt>();
        float y = 0, numbers = measure(new TextRun("00.", style, width)).Width + 6;
        int lines = 0;
        MarkdownBlock? last = null;
        foreach (var block in blocks)
        {
            if (lines >= maxLines)
            {
                cut = true;
                break;
            }

            bool item = block.Kind is MarkdownKind.Bullet or MarkdownKind.Numbered;
            y += last is null ? 0 : item && last.Kind is MarkdownKind.Bullet or MarkdownKind.Numbered ? ItemGap : BlockGap;
            var look = block.Kind switch
            {
                MarkdownKind.Heading => style with { Weight = 600 },
                MarkdownKind.Code => CodeStyle,
                _ => style,
            };
            float x = Pad + (item ? Math.Min(block.Depth, 4) * Indent : 0);
            if (item)
            {
                string marker = block.Kind == MarkdownKind.Bullet ? "•" : block.Marker;
                // Two digits and their dot fit the column the numbers share.
                float hang = block.Kind == MarkdownKind.Bullet ? Indent : marker.Length <= 3 ? numbers : Math.Max(numbers, MathF.Ceiling(measure(new TextRun(marker, look, width)).Width) + 6);
                runs.Add(new TextAt(new TextRun(marker, look, hang), new Vector2(x, y)));
                x += hang;
            }

            var run = new TextRun(block.Text, look, Math.Max(Indent, Pad + width - x), maxLines - lines) { Bold = block.Bold, Italic = block.Italic, Code = block.Code };
            var laid = measure(run);
            runs.Add(new TextAt(run, new Vector2(x, y)));
            y += laid.Height;
            lines += laid.Lines;
            cut |= laid.Trimmed;
            last = block;
        }

        if (cut)
        {
            y += BlockGap;
            var line = new TextRun(rest, RestStyle, width);
            runs.Add(new TextAt(line, new Vector2(Pad, y)));
            y += measure(line).Height;
        }

        return (runs, y);
    }

    /// <summary>A run laid out: its widest line, its height, its lines and whether it was cut.</summary>
    internal static (float Width, float Height, int Lines, bool Trimmed) Measure(TextRun run)
    {
        using var laid = TextLayout.Of(run);
        return (laid.Width, laid.Height, laid.LineCount, laid.Trimmed);
    }

    /// <summary>
    /// What takes clicks and keys, in Tab order and the panel's own DIPs: each choice as a button ("choice:0" on), then
    /// each tile ("tile:0" on), which a drag takes out of the island. A tile with no file behind it is disabled.
    /// </summary>
    public IReadOnlyList<Target> Targets(int files) =>
    [
        .. choices.Select((r, i) => new Target($"{ChoicePrefix}{i}", r, Corner, TargetKind.Button)),
        .. tiles.Select((r, i) => new Target($"{TilePrefix}{i}", r, Corner, TargetKind.Tile, Enabled: i < files, Draggable: true)),
    ];

    public const string ChoicePrefix = "choice:", TilePrefix = "tile:";

    /// <summary>The choice's or the tile's index in a target's id, -1 for each when it is the other.</summary>
    public static (int Tile, int Choice) Of(string id) =>
        id.StartsWith(TilePrefix, StringComparison.Ordinal) ? (int.Parse(id.AsSpan(TilePrefix.Length), CultureInfo.InvariantCulture), -1)
        : id.StartsWith(ChoicePrefix, StringComparison.Ordinal) ? (-1, int.Parse(id.AsSpan(ChoicePrefix.Length), CultureInfo.InvariantCulture)) : (-1, -1);
}
