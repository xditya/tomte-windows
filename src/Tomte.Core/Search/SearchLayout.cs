using System.Drawing;
using Tomte.Core.Text;

namespace Tomte.Core.Search;

/// <summary>A line of the result list: a group's heading, or a row with its index in the rows.</summary>
public readonly record struct SearchLine(float Top, float Height, string? Heading, int Row);

/// <summary>A card over the list that asks or tells something (getting Everything), with its buttons, the main one last.</summary>
public sealed record SearchCard(string Title, string Body, IReadOnlyList<(string Id, string Label)> Buttons);

/// <summary>
/// The search box's content in DIPs from its top-left corner, in the AI box's look: the box after the tomte, the result
/// list under it in a band that scrolls, and a footer. The island opens at the palette's smallest and grows to what it
/// shows (<see cref="Fit"/>), up to the palette's tallest capped at half the monitor; it does not shrink while open, so
/// the shape settles after the first few letters and typing on does not move it.
/// </summary>
public sealed class SearchLayout(float width, bool mascot, float height)
{
    public const float RowHeight = 36, HeadingHeight = 24, IconSize = 20, RowPadX = 8, RowCorner = 8, IconGap = 10, KindWidth = 72, DetailGap = 8;
    public const float FootHeight = 14, CardPad = 14;

    public static readonly TextStyle Name = new(13, Color: TextStyle.WhiteColor);
    public static readonly TextStyle Detail = new(12, Color: TextStyle.TertiaryColor);
    public static readonly TextStyle Kind = new(11, Color: TextStyle.SecondaryColor, Align: TextAlign.Trailing);
    public static readonly TextStyle Heading = new(11, 600, TextStyle.SecondaryColor);
    public static readonly TextStyle Foot = TextStyle.Small;
    public static readonly TextStyle FootRight = TextStyle.Small with { Align = TextAlign.Trailing };

    /// <summary>The selected row: a lighter fill and the accent's outline, which shows whether the pointer is there or not.</summary>
    public const uint SelectedFill = 0x24FFFFFF, SelectedLine = 0x996FA8FF;

    public const string Placeholder = "Search apps, files and folders";
    public const string Keys = "Enter opens · Ctrl+Enter shows in folder · Tab next group · Esc clears";
    public const string KeysEmpty = "Enter opens · Ctrl+Enter shows in folder · Tab next group · Esc closes";
    public const string NothingYet = "Type to search your apps, files and folders. What you open from here is listed first next time.";
    public const string NoResults = "Nothing found.";

    private readonly PaletteLayout prompt = new(width, mascot);

    /// <summary>Room under the box for the hint when there are no rows: its three lines at most.</summary>
    public const float EmptyRoom = 56;

    /// <summary>The island's tallest for search, for a monitor this tall in DIPs.</summary>
    public static float HeightFor(double monitorHeight) => (float)Math.Min(PanelSize.PaletteMaxHeight, 0.5 * monitorHeight);

    /// <summary>
    /// The height that shows the list (or the hint with no rows, or the card), from the palette's smallest to
    /// <paramref name="tallest"/>; past that the list scrolls.
    /// </summary>
    public static float Fit(float width, bool mascot, IReadOnlyList<SearchItem> rows, SearchCard? card, float tallest)
    {
        var low = new SearchLayout(width, mascot, (float)PanelSize.PaletteMinHeight);
        float top = low.Band.Y, below = PaletteLayout.Gap + FootHeight + PaletteLayout.Bottom;
        var lines = Lines(rows);
        float needed = card is not null ? top + low.Card(card).Card.Height + (2 * PaletteLayout.Gap) + below
            : lines.Count > 0 ? top + lines[^1].Top + lines[^1].Height + below
            : top + EmptyRoom + below;
        return Math.Clamp(needed, (float)PanelSize.PaletteMinHeight, tallest);
    }

    public float Width => width;

    public float Height => height;

    public RectangleF Box => prompt.Prompt;

    public RectangleF Editor => prompt.Editor;

    /// <summary>The band the list scrolls in, from under the box to above the footer.</summary>
    public RectangleF Band
    {
        get
        {
            float top = PaletteLayout.Top + PaletteLayout.PromptHeight + PaletteLayout.Gap;
            return new(PaletteLayout.Pad, top, width - (2 * PaletteLayout.Pad), Math.Max(0, height - top - PaletteLayout.Gap - FootHeight - PaletteLayout.Bottom));
        }
    }

    /// <summary>The footer's keys on the left and where files come from on the right.</summary>
    public (TextSlot Left, TextSlot Right) Footer(string keys, string source)
    {
        float y = height - PaletteLayout.Bottom - FootHeight, w = width - (2 * PaletteLayout.Pad);
        float right = Math.Min(w * 0.4f, TextSlot.Measure(source, FootRight, w * 0.4f));
        return (new(keys, Foot, new RectangleF(PaletteLayout.Pad, y, w - right - DetailGap, FootHeight)),
            new(source, FootRight, new RectangleF(PaletteLayout.Pad + w - right, y, right, FootHeight)));
    }

    /// <summary>The list's lines in band space: a heading before each group that has one, then its rows.</summary>
    public static IReadOnlyList<SearchLine> Lines(IReadOnlyList<SearchItem> rows)
    {
        var lines = new List<SearchLine>();
        float y = 0;
        int group = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            if (rows[i].Group != group)
            {
                group = rows[i].Group;
                if (HeadingFor(group) is { } heading)
                {
                    lines.Add(new(y, HeadingHeight, heading, -1));
                    y += HeadingHeight;
                }
            }

            lines.Add(new(y, RowHeight, null, i));
            y += RowHeight;
        }

        return lines;
    }

    public static string? HeadingFor(int group) => group switch
    {
        0 => "Apps",
        1 => "Shortcuts and shelf",
        2 => "Files and folders",
        _ => null,
    };

    /// <summary>The word at a row's right end: what it is.</summary>
    public static string KindLabel(SearchKind kind) => kind switch
    {
        SearchKind.App => "App",
        SearchKind.Shortcut => "Shortcut",
        SearchKind.Shelf => "On the shelf",
        SearchKind.Folder => "Folder",
        SearchKind.File => "File",
        SearchKind.Ask => "AI",
        _ => "",
    };

    /// <summary>
    /// A row's parts in band space for a row whose top is <paramref name="top"/>: the icon, the name, the dim detail after
    /// it in what room is left, and the kind at the right end. The name takes what it needs, up to two thirds.
    /// </summary>
    public (RectangleF Row, RectangleF Icon, TextSlot Name, TextSlot Detail, TextSlot Kind) Row(float top, SearchItem item)
    {
        float w = Band.Width;
        var row = new RectangleF(0, top, w, RowHeight);
        var icon = new RectangleF(RowPadX, top + ((RowHeight - IconSize) / 2), IconSize, IconSize);
        float x = icon.Right + IconGap, kindX = w - RowPadX - KindWidth, room = Math.Max(0, kindX - DetailGap - x);
        float nameWidth = Math.Min(TextSlot.Measure(item.Name, Name, room * 2 / 3), room);
        float detailX = x + nameWidth + DetailGap;
        return (row, icon,
            new(item.Name, Name, new RectangleF(x, top, nameWidth, RowHeight)),
            new(item.Detail, Detail, new RectangleF(detailX, top, Math.Max(0, kindX - DetailGap - detailX), RowHeight)),
            new(KindLabel(item.Kind), Kind, new RectangleF(kindX, top, KindWidth, RowHeight)));
    }

    /// <summary>The scroll that shows the line from <paramref name="top"/> to <paramref name="bottom"/> whole, moving as little as it can.</summary>
    public float ScrollTo(float scroll, float top, float bottom, float content) =>
        Math.Clamp(bottom > scroll + Band.Height ? bottom - Band.Height : top < scroll ? top : scroll, 0, Math.Max(0, content - Band.Height));

    /// <summary>A card in the middle of the band: its box, the title, the body on up to four lines, and the buttons along its bottom right.</summary>
    public (RectangleF Card, TextSlot Title, TextRun Body, PointF BodyAt, IReadOnlyList<(string Id, RectangleF Box, TextSlot Label)> Buttons) Card(SearchCard card)
    {
        var band = Band;
        float inner = band.Width - (2 * CardPad);
        using var laid = TextLayout.Of(new TextRun(card.Body, TextStyle.Secondary, inner, 4));
        float h = CardPad + 20 + 4 + laid.Height + PaletteLayout.AskGap + PaletteLayout.ButtonHeight + CardPad;
        var box = new RectangleF(band.X, band.Y + Math.Max(0, (band.Height - h) / 3), band.Width, h);
        float right = box.Right - CardPad, rowY = box.Bottom - CardPad - PaletteLayout.ButtonHeight;
        var buttons = new List<(string, RectangleF, TextSlot)>();
        for (int i = card.Buttons.Count - 1; i >= 0; i--)
        {
            var (id, label) = card.Buttons[i];
            var style = i == card.Buttons.Count - 1 ? PaletteLayout.RunButton : PaletteLayout.Button;
            float bw = Math.Max(PaletteLayout.ButtonMinWidth, TextSlot.Measure(label, style, inner) + (2 * PaletteLayout.ButtonPadX));
            var at = new RectangleF(right - bw, rowY, bw, PaletteLayout.ButtonHeight);
            buttons.Insert(0, (id, at, new TextSlot(label, style, at)));
            right = at.X - PaletteLayout.ButtonGap;
        }

        return (box, new TextSlot(card.Title, TextStyle.Title, new RectangleF(box.X + CardPad, box.Y + CardPad, inner, 20)),
            new TextRun(card.Body, TextStyle.Secondary, inner, 4), new PointF(box.X + CardPad, box.Y + CardPad + 24), buttons);
    }
}
