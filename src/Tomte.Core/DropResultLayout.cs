using System.Drawing;
using System.Numerics;
using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>A result file in the panel: its name, its line under the name, and its icon's colour (RRGGBB).</summary>
public sealed record ResultTile(string Name, string Meta, uint Color)
{
    public const string OnShelf = "On the shelf · drag it out";

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
}

/// <summary>
/// The drop zone's panel in the Palette state, in the panel's own DIPs (x = 0 at its left edge, y = 0 at its top):
/// a head row right of the tomte with the zone and file on the left and where the request went on the right, a line
/// under it, then the answer, the size question's choices as full-width rows and the result files as tiles. The panel
/// is the palette's width and grows with what it shows, up to the palette's height or the monitor's cap, whichever is less.
/// </summary>
public sealed class DropResultLayout
{
    public const float Pad = 16, HeadTop = 12, HeadHeight = 18, LineTop = 34, LineHeight = 14, BodyTop = 58;
    public const float TileWidth = 240, TileHeight = 44, TileIcon = 24, ChoiceHeight = 30, Gap = 8, Corner = 8;

    public static readonly TextStyle IndicatorStyle = TextStyle.Secondary with { Align = TextAlign.Trailing };
    public static readonly TextStyle BodyStyle = TextStyle.Primary with { LineHeight = 19 };
    public static readonly TextStyle TileName = new(12.5f, 600);

    private readonly List<RectangleF> tiles = [], choices = [];
    private readonly List<TextSlot> slots = [];

    public DropResultLayout(DropResult result, float width, bool mascot, float maxHeight = (float)PanelSize.PaletteMaxHeight)
    {
        float left = mascot ? MascotArt.InsetX + MascotArt.SizeDips + 10 : Pad, inner = width - 2 * Pad;
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
            int lines = Math.Max(1, (int)((maxHeight - y - below - Pad) / BodyStyle.LineHeight));
            var run = new TextRun(shown, problem ? BodyStyle with { Color = TextStyle.WarnColor } : BodyStyle, inner, lines);
            using var laid = TextLayout.Of(run);
            Body = new TextAt(run, new Vector2(Pad, y));
            y += MathF.Ceiling(laid.Height) + Gap + 4;
        }

        foreach (string choice in result.Choices)
        {
            var row = new RectangleF(Pad, y, inner, ChoiceHeight);
            choices.Add(row);
            slots.Add(new(choice, TextStyle.Secondary with { Color = TextStyle.WhiteColor }, new(row.X + 12, row.Y, row.Width - 24, row.Height)));
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

    /// <summary>The answer, wrapped to the panel and cut with an ellipsis where the panel ends; null when there is none.</summary>
    public TextAt? Body { get; }

    public IReadOnlyList<RectangleF> Tiles => tiles;

    public IReadOnlyList<RectangleF> Choices => choices;

    /// <summary>A tile's icon square.</summary>
    public static RectangleF Icon(RectangleF tile) => new(tile.X + 10, tile.Y + (tile.Height - TileIcon) / 2, TileIcon, TileIcon);

    /// <summary>The panel's height for what it shows, between the palette's smallest and largest.</summary>
    public float Height { get; }

    /// <summary>What a press at a point of the panel lands on: a tile's index, a choice's index, or -1 for each.</summary>
    public (int Tile, int Choice) At(Vector2 point) =>
        (tiles.FindIndex(r => r.Contains(point.X, point.Y)), choices.FindIndex(r => r.Contains(point.X, point.Y)));
}
