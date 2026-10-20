using System.Drawing;
using System.Globalization;
using System.Numerics;
using Tomte.Core.Interaction;
using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>
/// Where the Home panel's parts sit, in DIPs from the body's top-left corner (under the tab row). Two rows of section
/// cards, the wider column on the left: the Shelf beside Now playing, then the Scratchpad beside Shortcuts. Under them
/// the Ask bar and the line about the provider. The Ask bar stays at the bottom. On a body too short for both rows the
/// second gets shorter first (one row of shortcuts, fewer lines of the note), then goes, then the first goes.
/// </summary>
public sealed class HomeLayout(Vector2 body)
{
    public const float Pad = Section.BodyPad, Top = Section.BodyTop, Bottom = 16, ColumnGap = Section.Gap, RowGap = Section.Gap;

    /// <summary>The left column against the right one.</summary>
    public const float Split = 1.55f;

    public const float FirstRow = 174, SecondRow = 190;
    public const float AskHeight = 48, AskCorner = 16, AskGap = 12, AskPadLeft = 14, AskPadRight = 8, AskInnerGap = 10, Sparkle = 18, Mic = 34, MicIcon = 16;
    public const float FootGap = 8, FootHeight = 14, FootInset = 6, Dot = 6, DotGap = 6;

    /// <summary>The panel's height with both rows: the tab row, the rows, the Ask bar and its line.</summary>
    public const float Height = PanelSpace.Head + Top + FirstRow + RowGap + SecondRow + AskGap + AskHeight + FootGap + FootHeight + Bottom;

    // The shelf's tiles: four across, each a preview over the name and the size.
    public const int ShelfColumns = 4;
    public const float TileGap = 8, TilePad = 8, PreviewHeight = 58, PreviewCorner = 8, NameHeight = 16, MetaHeight = 14;

    // Scratchpad: the note's title and up to four of its lines.
    public const int NoteLines = 4;
    public const float NoteTitleHeight = 22, NoteTitleGap = 8, LineHeight = 18, LineGap = 7, Box = 16, BoxCorner = 5, BoxGap = 10, Tick = 12;

    // Shortcuts: three across, two down.
    public const int ShortcutColumns = 3, ShortcutRows = 2;
    public const float ShortcutHeight = 64, ShortcutIcon = 24, ShortcutIconGap = 6, ShortcutLabelHeight = 14, Add = 24, AddIcon = 16;

    /// <summary>The second row at its shortest: one row of shortcuts under the head.</summary>
    public const float SecondRowMin = Section.Pad + Section.HeadHeight + Section.HeadGap + ShortcutHeight + Section.Pad;

    public const string ShelfTitle = "Shelf", ShelfHint = "Drag out into any app", ShelfEmpty = "Drop files, text or links on the island";
    public const string NoteTitle = "Scratchpad", NoteHint = "Saved on this PC", NoteEmpty = "Nothing in the note yet";
    public const string ShortcutsTitle = "Shortcuts", ShortcutsEmpty = "Add apps, files and pages in Settings";
    public const string Placeholder = "Ask Tomte anything", SendsNothing = "Nothing is sent until you press Enter";
    public const string RegionChip = "Region", WindowChip = "Window", ShelfChip = "Shelf";

    public static TextStyle TileName => new(12, 500);
    public static TextStyle TileMeta => TextStyle.Small;
    public static TextStyle PreviewLabel => new(11, Align: TextAlign.Center, Family: TextStyle.Mono, Tracking: 0.06f);
    public static TextStyle MoreCount => new(20, 600, Align: TextAlign.Center);
    public static TextStyle MoreWords => new(12, Color: Theme.Ink2, Align: TextAlign.Center);
    public static TextStyle EmptyWords => new(12, Color: Theme.Ink3, Align: TextAlign.Center);
    public static TextStyle NoteHeading => new(16, 600);
    public static TextStyle NoteLine => TextStyle.Primary;
    public static TextStyle NoteDone => TextStyle.Primary with { Color = Theme.Ink3 };
    public static TextStyle ShortcutName => new(11, Color: Theme.Ink2, Align: TextAlign.Center);
    public static TextStyle PromptText => new(14);
    public static TextStyle PromptHint => new(14, Color: Theme.Ink3);
    public static TextStyle Foot => TextStyle.Small;
    public static TextStyle FootFigures => TextStyle.Small with { Family = TextStyle.Mono, Tabular = true };

    public const string ShelfId = "shelf", MediaId = "media";
    public const string NoteId = "note", ShortcutsId = "shortcuts", AddId = "add", AskId = "ask", RegionId = "region", WindowId = "window", ShelfChipId = "shelfchip", MicId = "mic";

    public static string ShortcutId(long id) => $"shortcut:{id}";

    /// <summary>A shelf tile's id: the Shelf tab's own, so both name an item the same way.</summary>
    public static string ShelfTileId(long id) => ShelfLayout.TileId(id);

    public Vector2 Body { get; } = body;

    private float Inner => Body.X - (2 * Pad);

    private float Left => (Inner - ColumnGap) * Split / (Split + 1);

    private float Right => Inner - ColumnGap - Left;

    private float RowsRoom => Body.Y - Top - (AskGap + AskHeight + FootGap + FootHeight + Bottom);

    /// <summary>How many rows of sections the body holds: 2, 1 or 0.</summary>
    public int Rows => RowsRoom >= FirstRow + RowGap + SecondRowMin - 0.5f ? 2 : RowsRoom >= FirstRow - 0.5f ? 1 : 0;

    /// <summary>The second row's height: all of it when there is room, else what is left, never under <see cref="SecondRowMin"/>.</summary>
    public float SecondHeight => Math.Clamp(RowsRoom - FirstRow - RowGap, SecondRowMin, SecondRow);

    /// <summary>The rows of shortcut tiles the second row holds, 1 or 2.</summary>
    public int ShortcutRowsShown => Math.Clamp((int)((SecondHeight - (2 * Section.Pad) - Section.HeadHeight - Section.HeadGap + TileGap) / (ShortcutHeight + TileGap)), 1, ShortcutRows);

    /// <summary>How many of the note's lines fit under its title, up to <see cref="NoteLines"/>.</summary>
    public int NoteLinesShown => Math.Clamp((int)((SecondHeight - (2 * Section.Pad) - Section.HeadHeight - Section.HeadGap - NoteTitleHeight - NoteTitleGap + LineGap) / (LineHeight + LineGap)), 0, NoteLines);

    public RectangleF Shelf => new(Pad, Top, Left, FirstRow);

    public RectangleF NowPlaying => new(Pad + Left + ColumnGap, Top, Right, FirstRow);

    public RectangleF Scratchpad => new(Pad, Top + FirstRow + RowGap, Left, SecondHeight);

    public RectangleF Shortcuts => new(Pad + Left + ColumnGap, Top + FirstRow + RowGap, Right, SecondHeight);

    public RectangleF Ask => new(Pad, Body.Y - Bottom - FootHeight - FootGap - AskHeight, Inner, AskHeight);

    public RectangleF FootLine => new(Pad + FootInset, Ask.Bottom + FootGap, Inner - (2 * FootInset), FootHeight);

    /// <summary>A section's head row, inside its card's padding.</summary>
    public static RectangleF Head(RectangleF section) => new(section.X + Section.Pad, section.Y + Section.Pad, section.Width - (2 * Section.Pad), Section.HeadHeight);

    /// <summary>The section's title, then a note beside it, then the hint at its right end; in body space.</summary>
    public static IReadOnlyList<TextSlot> HeadText(RectangleF section, string title, string? note, string? hint = null, float rightInset = 0)
    {
        var head = Head(section);
        float after = MathF.Ceiling(TextSlot.Measure(title, Section.Title, head.Width)) + 8;
        float hintWidth = hint is null ? 0 : MathF.Ceiling(TextSlot.Measure(hint, Section.Note, head.Width / 2));
        var lines = new List<TextSlot> { new(title, Section.Title, head) };
        if (note is not null)
        {
            lines.Add(new(note, Section.Note, RectangleF.FromLTRB(head.X + after, head.Y, head.Right - rightInset - (hint is null ? 0 : hintWidth + 8), head.Bottom)));
        }

        if (hint is not null)
        {
            lines.Add(new(hint, Section.Note with { Align = TextAlign.Trailing }, new(head.Right - rightInset - hintWidth, head.Y, hintWidth, head.Height)));
        }

        return lines;
    }

    // ---- Shelf ----

    /// <summary>The tiles that show for this many items: all of them up to four, else three and a fourth that counts the rest.</summary>
    public static (int Tiles, int More) ShelfShows(int items) => items <= ShelfColumns ? (items, 0) : (ShelfColumns - 1, items - (ShelfColumns - 1));

    public RectangleF ShelfTile(int slot)
    {
        var grid = ShelfGrid;
        float width = (grid.Width - ((ShelfColumns - 1) * TileGap)) / ShelfColumns;
        return new(grid.X + (slot * (width + TileGap)), grid.Y, width, grid.Height);
    }

    /// <summary>Under the head, where the tiles go, and the empty shelf's dashed box.</summary>
    public RectangleF ShelfGrid
    {
        get
        {
            var s = Shelf;
            float top = Head(s).Bottom + 12;
            return RectangleF.FromLTRB(s.X + Section.Pad, top, s.Right - Section.Pad, s.Bottom - Section.Pad);
        }
    }

    /// <summary>A tile's preview, in the tile's own coordinates.</summary>
    public static RectangleF Preview(RectangleF tile) => new(TilePad, TilePad, tile.Width - (2 * TilePad), PreviewHeight);

    /// <summary>A tile's name and its size under the preview, in the tile's own coordinates.</summary>
    public static IReadOnlyList<TextSlot> TileText(RectangleF tile, string name, string meta)
    {
        float y = TilePad + PreviewHeight + 8, width = tile.Width - (2 * TilePad);
        return [new(name, TileName, new(TilePad, y, width, NameHeight)), new(meta, TileMeta, new(TilePad, y + NameHeight + 2, width, MetaHeight))];
    }

    /// <summary>The last tile when more is on the shelf than shows: "+5" over "more on the shelf", in the tile's own coordinates.</summary>
    public static IReadOnlyList<TextSlot> MoreText(RectangleF tile, int more)
    {
        float mid = tile.Height / 2;
        return
        [
            new(string.Create(CultureInfo.InvariantCulture, $"+{more}"), MoreCount, new(0, mid - 26, tile.Width, 26)),
            new("more on the shelf", MoreWords, new(4, mid + 2, tile.Width - 8, 16)),
        ];
    }

    /// <summary>
    /// What a tile's preview shows when it has no picture: a folder or a link glyph, as the Shelf tab tells them apart, or
    /// else the file's extension in capitals. A picture's own thumbnail goes over the extension once it is read.
    /// </summary>
    public static (GlyphFigure[]? Glyph, string Word) PreviewOf(ShelfItem item)
    {
        string ext = Path.GetExtension(item.Name).TrimStart('.').ToUpperInvariant();
        return ShelfLayout.PreviewOf(item) switch
        {
            ShelfPreview.Folder => (Glyphs.Folder, ""),
            ShelfPreview.Link => (Glyphs.Link, ""),
            _ => (null, ext is { Length: > 0 and <= 5 } ? ext : "FILE"),
        };
    }

    // ---- Scratchpad ----

    public RectangleF NoteHeadingBox => new(Scratchpad.X + Section.Pad, Head(Scratchpad).Bottom + Section.HeadGap, Scratchpad.Width - (2 * Section.Pad), NoteTitleHeight);

    /// <summary>Line <paramref name="i"/> of the note under its title: the checkbox's square (if it has one) and the words.</summary>
    public (RectangleF Box, RectangleF Text) NoteLineAt(int i, bool box)
    {
        float y = NoteHeadingBox.Bottom + NoteTitleGap + (i * (LineHeight + LineGap)), x = NoteHeadingBox.X;
        var square = new RectangleF(x, y + ((LineHeight - Box) / 2), Box, Box);
        float start = box ? square.Right + BoxGap : x;
        return (square, new(start, y, NoteHeadingBox.Right - start, LineHeight));
    }

    // ---- Shortcuts ----

    public RectangleF AddButton
    {
        get
        {
            var head = Head(Shortcuts);
            return new(head.Right - Add + 4, head.Y + ((head.Height - Add) / 2), Add, Add);
        }
    }

    public RectangleF ShortcutTile(int i)
    {
        var s = Shortcuts;
        float top = Head(s).Bottom + Section.HeadGap, width = (s.Width - (2 * Section.Pad) - ((ShortcutColumns - 1) * TileGap)) / ShortcutColumns;
        return new(s.X + Section.Pad + (i % ShortcutColumns * (width + TileGap)), top + (i / ShortcutColumns * (ShortcutHeight + TileGap)), width, ShortcutHeight);
    }

    /// <summary>A shortcut's icon square and its name under it, centred in the tile, in body space.</summary>
    public static (RectangleF Icon, TextSlot Name) ShortcutParts(RectangleF tile, string name)
    {
        float top = tile.Y + ((tile.Height - ShortcutIcon - ShortcutIconGap - ShortcutLabelHeight) / 2);
        var icon = new RectangleF(tile.X + ((tile.Width - ShortcutIcon) / 2), top, ShortcutIcon, ShortcutIcon);
        return (icon, new(name, ShortcutName, new(tile.X + 4, icon.Bottom + ShortcutIconGap, tile.Width - 8, ShortcutLabelHeight)));
    }

    // ---- Ask bar ----

    public RectangleF SparkleBox => new(Ask.X + AskPadLeft, Ask.Y + ((AskHeight - Sparkle) / 2), Sparkle, Sparkle);

    public RectangleF MicButton => new(Ask.Right - AskPadRight - Mic, Ask.Y + ((AskHeight - Mic) / 2), Mic, Mic);

    /// <summary>The three chips, Region, Window and Shelf, ending before the mic.</summary>
    public IReadOnlyList<(string Id, string Label, RectangleF Box)> Chips()
    {
        (string Id, string Label)[] all = [(RegionId, RegionChip), (WindowId, WindowChip), (ShelfChipId, ShelfChip)];
        var widths = all.Select(c => Chip.Width(c.Label, Inner)).ToArray();
        float x = MicButton.X - AskInnerGap - widths.Sum() - (AskInnerGap * (all.Length - 1)), y = Ask.Y + ((AskHeight - Chip.Height) / 2);
        var chips = new List<(string, string, RectangleF)>();
        for (int i = 0; i < all.Length; i++)
        {
            chips.Add((all[i].Id, all[i].Label, new RectangleF(x, y, widths[i], Chip.Height)));
            x += widths[i] + AskInnerGap;
        }

        return chips;
    }

    /// <summary>The prompt's text box, from after the sparkle to before the chips.</summary>
    public RectangleF Prompt
    {
        get
        {
            float x = SparkleBox.Right + AskInnerGap, end = Chips()[0].Box.X - AskInnerGap;
            return new(x, Ask.Y + ((AskHeight - 20) / 2), Math.Max(0, end - x), 20);
        }
    }

    /// <summary>
    /// The line under the Ask bar: the status dot's square (none with no provider), the provider and model with what was
    /// spent today, and at the right end that nothing goes before Enter.
    /// </summary>
    public (RectangleF? Dot, IReadOnlyList<TextSlot> Text) FootText(string? provider, string? spend)
    {
        var line = FootLine;
        float end = MathF.Ceiling(TextSlot.Measure(SendsNothing, Foot, line.Width / 2));
        var sends = new TextSlot(SendsNothing, Foot with { Align = TextAlign.Trailing }, new(line.Right - end, line.Y, end, line.Height));
        if (provider is null)
        {
            return (null, [new(PaletteLayout.Off, Foot, new(line.X, line.Y, line.Width - end - 12, line.Height)), sends]);
        }

        var dot = new RectangleF(line.X, line.Y + ((line.Height - Dot) / 2), Dot, Dot);
        float x = dot.Right + DotGap, room = line.Right - end - 12 - x;
        string named = spend is null ? provider : provider + " · ";
        float w = Math.Min(MathF.Ceiling(TextSlot.Measure(named, Foot, room)), room);
        var text = new List<TextSlot> { new(named, Foot, new(x, line.Y, w, line.Height)) };
        if (spend is not null)
        {
            text.Add(new(spend, FootFigures, new(x + w, line.Y, Math.Max(0, room - w), line.Height)));
        }

        text.Add(sends);
        return (dot, text);
    }

    /// <summary>What was spent today against the cap, as the line says it ("$0.12 of $10.00 today").</summary>
    public static string Spend(decimal spent, decimal? cap) =>
        cap is { } c ? string.Create(CultureInfo.InvariantCulture, $"${spent:0.00} of ${c:0.00} today") : string.Create(CultureInfo.InvariantCulture, $"${spent:0.00} today");

    /// <summary>The Now playing card's parts, stacked to fit the section.</summary>
    public Media.MediaPanelLayout NowPlayingCard => new(NowPlaying, stacked: true);

    /// <summary>
    /// The clickable parts in Tab order: each section (a click opens its tab) with its own controls after it, so they sit
    /// on top of it (<paramref name="media"/> are the Now playing card's, <paramref name="shelfItems"/> the shelf's
    /// items, of which the tiles that show are each a target that opens and drags out its item; the "+N more" tile is
    /// the section's), then the Ask bar's prompt, its chips and the mic. Sections in a row that does not fit are left
    /// out. The shelf tiles are one arrow-key group, the shortcut tiles another.
    /// </summary>
    public IReadOnlyList<Target> Targets(IReadOnlyList<Target> media, IReadOnlyList<(long Id, string Name)> shortcuts, bool window, bool shelf, IReadOnlyList<ShelfItem>? shelfItems = null)
    {
        var all = new List<Target>();
        if (Rows >= 1)
        {
            all.Add(new(ShelfId, Shelf, Section.Corner, TargetKind.Tile, Tip: "Open the Shelf"));
            var items = shelfItems ?? [];
            for (int i = 0; i < ShelfShows(items.Count).Tiles; i++)
            {
                all.Add(new(ShelfTileId(items[i].Id), ShelfTile(i), Section.TileCorner, TargetKind.Tile, Tip: ShelfLayout.Tip(items[i]), Group: 3, Draggable: true));
            }

            all.Add(new(MediaId, NowPlaying, Section.Corner, TargetKind.Tile, Tip: "Open Media"));
            all.AddRange(media);
        }

        if (Rows >= 2)
        {
            all.Add(new(NoteId, Scratchpad, Section.Corner, TargetKind.Tile, Tip: "Open the Scratchpad"));
            all.Add(new(ShortcutsId, Shortcuts, Section.Corner, TargetKind.Tile, Tip: "Open Shortcuts"));
            all.Add(new(AddId, AddButton, Add / 2, TargetKind.Button, Tip: "Add a shortcut in Settings"));
            for (int i = 0; i < shortcuts.Count && i < ShortcutColumns * ShortcutRowsShown; i++)
            {
                all.Add(new(ShortcutId(shortcuts[i].Id), ShortcutTile(i), Section.TileCorner, TargetKind.Tile, Tip: shortcuts[i].Name, Group: 2));
            }
        }

        all.Add(new(AskId, Ask, AskCorner, TargetKind.Box));
        foreach (var (id, label, box) in Chips())
        {
            all.Add(id == RegionId
                ? new Target(id, box, Chip.Height / 2, TargetKind.Button, Tip: "Circle part of the screen to ask about")
                : new Target(id, box, Chip.Height / 2, TargetKind.Toggle, Tip: id == WindowId ? "Send the window in front with the question" : "Send what is on the shelf with the question", Checked: id == WindowId ? window : shelf));
        }

        all.Add(new(MicId, MicButton, Mic / 2, TargetKind.Button, Tip: "Hold to talk", Draggable: true));
        return all;
    }

    /// <summary>
    /// A note's preview: its first line as the title (a heading's marks dropped), then up to four lines after it, each
    /// with whether it is a checkbox and whether that is ticked. Blank lines are skipped; list and emphasis marks are not
    /// shown.
    /// </summary>
    public static (string Title, IReadOnlyList<(string Text, bool? Done)> Lines) NotePreview(string note)
    {
        var lines = note.Split('\n').Select(l => l.TrimEnd('\r').Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0)
        {
            return ("", []);
        }

        string title = Plain(lines[0].TrimStart('#').Trim());
        var rest = new List<(string, bool?)>();
        foreach (string line in lines.Skip(1).Take(NoteLines))
        {
            string text = line.StartsWith("- ", StringComparison.Ordinal) || line.StartsWith("* ", StringComparison.Ordinal) ? line[2..] : line;
            bool? done = null;
            if (text.StartsWith("[ ]", StringComparison.Ordinal))
            {
                done = false;
            }
            else if (text.StartsWith("[x]", StringComparison.OrdinalIgnoreCase))
            {
                done = true;
            }

            rest.Add((Plain(done is null ? text.TrimStart('#').Trim() : text[3..].Trim()), done));
        }

        return (title, rest);
    }

    private static string Plain(string text) => text.Replace("**", "").Replace("__", "").Replace("`", "");
}
