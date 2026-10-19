using System.Drawing;
using Tomte.Core.Text;

namespace Tomte.Core.Search;

/// <summary>
/// A line of the result list: a group's heading, a rule before the AI rows, or a row with its index in the rows.
/// <paramref name="Hit"/> marks the top hit, the first row of a search, drawn taller with its full path.
/// </summary>
public readonly record struct SearchLine(float Top, float Height, string? Heading, int Row, bool Rule = false, bool Hit = false);

/// <summary>What Enter does in the search box: open the row, show its file in its folder, or search with AI.</summary>
public enum SearchEnter
{
    Open,
    ShowInFolder,
    SearchWithAi,
}

/// <summary>
/// A card over the list that asks or tells something (getting Everything), with its buttons left to right.
/// <paramref name="Focus"/> is the button Enter presses until Tab moves on: the safe one, never one that installs.
/// </summary>
public sealed record SearchCard(string Title, string Body, IReadOnlyList<(string Id, string Label)> Buttons, int Focus = 0);

/// <summary>A row's parts in band space: its box, the badge, the name, the dim text, and the key hint at its right end if any.</summary>
public readonly record struct SearchRow(RectangleF Row, float Corner, RectangleF Badge, float BadgeCorner, TextSlot Name, TextSlot Detail, string? Key, RectangleF KeyCap);

/// <summary>
/// The search box's content in DIPs from its top-left corner: the query field after the tomte, with the magnifier, the
/// text and where files come from; the result list under it in a band that scrolls, the top hit first and then the
/// groups under their headings; and the key hints along the bottom. The island opens at the palette's smallest and grows
/// to what it shows (<see cref="Fit"/>), up to the palette's tallest capped at half the monitor; it does not shrink while
/// there is text (<see cref="Settles"/>), so the shape settles after the first few letters and typing on does not move it.
/// </summary>
public sealed class SearchLayout(float width, bool mascot, float height)
{
    public const float Pad = 12, Top = 12, Bottom = 14, Gap = 10;

    /// <summary>The query field: a card with the magnifier, the text and, at its right end, a status dot and where files come from.</summary>
    public const float BoxHeight = 56, BoxCorner = 18, BoxPadX = 16, GlassSize = 20, GlassGap = 12, BoxText = 20, EditorHeight = 28, DotSize = 6, DotGap = 6;

    /// <summary>The top hit is taller than the other rows and raised; a heading and a row are 2 DIPs apart.</summary>
    public const float HitHeight = 56, HitCorner = 14, HitBadge = 34, HitBadgeCorner = 9;

    public const float RowHeight = 44, RowCorner = 12, RowBadge = 26, RowBadgeCorner = 7, RowPadX = 12, BadgeGap = 12, LineGap = 2, DetailGap = 12;

    /// <summary>A heading takes 6 DIPs above and below its line, 4 more above after a row; the rule before the AI rows is 8 clear of each side.</summary>
    public const float HeadingHeight = 26, HeadingAbove = 4, RuleHeight = 17, RuleInset = 8;

    /// <summary>The shell icon a badge shows, in DIPs, at both badge sizes.</summary>
    public const float IconSize = 20;

    /// <summary>The key hints along the bottom, inset as the rows' text is, 14 apart.</summary>
    public const float FootHeight = Keycap.Height, FootInset = 12, FootGap = 14, CardPad = 14;

    public static readonly TextStyle Query = new(BoxText, 500);
    public static readonly TextStyle Placeholder = Query with { Color = Theme.Ink3 };
    public static readonly TextStyle Status = TextStyle.Small with { Align = TextAlign.Trailing };
    public static readonly TextStyle HitName = new(14, 500);
    public static readonly TextStyle HitPath = new TextStyle(12, Color: Theme.Ink3) with { Family = TextStyle.Mono };
    public static readonly TextStyle Name = TextStyle.Primary;
    public static readonly TextStyle Detail = TextStyle.Secondary with { Color = Theme.Ink3, Align = TextAlign.Trailing };
    public static readonly TextStyle Heading = TextStyle.Overline;
    public static readonly TextStyle Lead = TextStyle.Small;
    public static readonly TextStyle Foot = TextStyle.Secondary with { Color = Theme.Ink3 };
    public static readonly TextStyle HitBadgeText = new TextStyle(10, 500, Align: TextAlign.Center) with { Family = TextStyle.Mono };
    public static readonly TextStyle RowBadgeText = HitBadgeText with { Size = 9 };

    /// <summary>The selected row's fill. The pointer's hover is a state layer of its own over it.</summary>
    public const uint SelectedFill = Theme.Raised;

    /// <summary>The rule before the AI rows.</summary>
    public const uint RuleColor = Theme.Raised;

    /// <summary>A badge that shows a glyph or an icon rather than a file's type.</summary>
    public const uint PlainBadge = Theme.RaisedHi;

    public const string PlaceholderText = "Search apps, settings, files and folders";
    public const string NothingYet = "Type to search your apps, settings, files and folders. What you open from here is listed first next time.";
    public const string NoResults = "Nothing found.";
    public const string AiRow = "Search with AI";
    public const string TopHit = "TOP HIT";

    /// <summary>The narrowest the search box gets: 60% of an 800 DIP wide screen.</summary>
    public const float NarrowestWidth = 480;

    /// <summary>The tomte beside the field, centred on it, its art as far in from the left edge as the list below.</summary>
    public static readonly MascotSeat Seat = new(Top + (BoxHeight / 2), Pad);

    /// <summary>
    /// Tab (+1): the first row of the next section, from the last back to the first. Shift+Tab (-1): from inside a
    /// section its first row, from a first row the previous section's, from the first back to the last.
    /// </summary>
    public static int NextSection(IReadOnlyList<SearchItem> rows, int select, int direction)
    {
        var firsts = Enumerable.Range(0, rows.Count).Where(i => i == 0 || rows[i].Group != rows[i - 1].Group).ToList();
        int current = firsts.LastOrDefault(i => i <= select);
        int at = firsts.IndexOf(current);
        return direction > 0 ? firsts[(at + 1) % firsts.Count]
            : select != current ? current
            : firsts[(at - 1 + firsts.Count) % firsts.Count];
    }

    /// <summary>Enter opens; Ctrl+Shift+Enter shows the row in its folder; Ctrl+Enter searches with AI from any row.</summary>
    public static SearchEnter Enter(bool ctrl, bool shift) => !ctrl ? SearchEnter.Open : shift ? SearchEnter.ShowInFolder : SearchEnter.SearchWithAi;

    /// <summary>The keys that work in the box, in the order the footer names them, and Esc's, which stands at its right end.</summary>
    public static (IReadOnlyList<(string Keys, string Word)> Left, (string Keys, string Word) Right) Keys(bool ai, bool typed, bool back)
    {
        List<(string, string)> left = [("↑ ↓", "move"), ("Enter", "open"), (FolderCap, "show in folder")];
        if (!ai)
        {
            left.Add(("Tab", "next section"));
        }

        return (left, ("Esc", ai || (back && !typed) ? "back" : typed ? "clear" : "close"));
    }

    /// <summary>Room under the box for the hint when there are no rows: its three lines at most.</summary>
    public const float EmptyRoom = 56;

    /// <summary>
    /// Whether the box may get shorter than it is: only with the field cleared and the rows for the empty field shown
    /// (<paramref name="shownFor"/> is the text the rows on show were found for). While there is text it only grows, so a
    /// typed letter that shortens the list does not make the shape jump.
    /// </summary>
    public static bool Settles(string typed, string? shownFor) => typed.Length == 0 && shownFor == typed;

    /// <summary>The island's tallest for search, for a monitor this tall in DIPs.</summary>
    public static float HeightFor(double monitorHeight) => (float)Math.Min(PanelSize.PaletteMaxHeight, 0.5 * monitorHeight);

    /// <summary>
    /// The height that shows the list (or the hint with no rows, or the card), from the palette's smallest to
    /// <paramref name="tallest"/>; past that the list scrolls.
    /// </summary>
    public static float Fit(float width, bool mascot, IReadOnlyList<SearchItem> rows, SearchCard? card, float tallest, string? lead = null, bool hit = false)
    {
        var low = new SearchLayout(width, mascot, (float)PanelSize.PaletteMinHeight);
        float top = low.Band.Y, below = Gap + FootHeight + Bottom;
        var lines = Lines(rows, lead, hit);
        float needed = card is not null ? top + low.Card(card).Card.Height + (2 * Gap) + below
            : lines.Count > 0 ? top + lines[^1].Top + lines[^1].Height + below
            : top + EmptyRoom + below;
        return Math.Clamp(needed, (float)PanelSize.PaletteMinHeight, tallest);
    }

    public float Width => width;

    public float Height => height;

    /// <summary>The query field's card.</summary>
    public RectangleF Box
    {
        get
        {
            float left = mascot ? Seat.After(10) : Pad;
            return new(left, Top, width - Pad - left, BoxHeight);
        }
    }

    /// <summary>The magnifier at the field's left.</summary>
    public RectangleF Glass => new(Box.X + BoxPadX, Top + ((BoxHeight - GlassSize) / 2), GlassSize, GlassSize);

    /// <summary>Where files come from (or where AI search sent its request), right-aligned at the field's right end, with its dot before it.</summary>
    public (RectangleF Dot, TextSlot Line) Source(string source)
    {
        var box = Box;
        float room = (box.Width - (2 * BoxPadX)) * 0.4f;
        float w = Math.Min(room, TextSlot.Measure(source, Status, room));
        float right = box.Right - BoxPadX, middle = Top + (BoxHeight / 2);
        var line = new TextSlot(source, Status, new RectangleF(right - w, Top, w, BoxHeight));
        return (source.Length == 0 ? RectangleF.Empty : new RectangleF(right - w - DotGap - DotSize, middle - (DotSize / 2), DotSize, DotSize), line);
    }

    /// <summary>The status dot: a working source, one that is off, or one on its way.</summary>
    public static uint SourceDot(string source) =>
        source is "Everything" or "Windows index" || source.StartsWith("Sent to", StringComparison.Ordinal) ? Theme.Ok
        : source == IndexOff ? Theme.Warn
        : Theme.Ink3;

    public const string IndexOff = "Index is off";

    /// <summary>The text box, between the magnifier and where files come from, centred down the field.</summary>
    public RectangleF EditorFor(string source)
    {
        var (dot, _) = Source(source);
        float left = Glass.Right + GlassGap, right = dot.IsEmpty ? Box.Right - BoxPadX : dot.X - GlassGap;
        return new(left, Top + ((BoxHeight - EditorHeight) / 2), Math.Max(0, right - left), EditorHeight);
    }

    /// <summary>The band the list scrolls in, from under the box to above the footer.</summary>
    public RectangleF Band
    {
        get
        {
            float top = Top + BoxHeight + Gap;
            return new(Pad, top, width - (2 * Pad), Math.Max(0, height - top - Gap - FootHeight - Bottom));
        }
    }

    /// <summary>
    /// The footer: each key and what it does from the left, as many as fit before Esc's at the right end; the ones that
    /// do not fit are left out from the last.
    /// </summary>
    public List<(RectangleF Cap, string Keys, TextSlot Word)> Footer(IReadOnlyList<(string Keys, string Word)> left, (string Keys, string Word) right)
    {
        float y = height - Bottom - FootHeight, from = Pad + FootInset, to = width - Pad - FootInset;
        var end = Keycap.Row([right], 0, float.MaxValue, y, FootHeight, Foot, 0);
        float endWidth = end.Count > 0 ? end[0].Word.Slot.Right - end[0].Cap.X : 0;
        var laid = Keycap.Row(left, from, to - endWidth - FootGap, y, FootHeight, Foot, FootGap);
        foreach (var (cap, keys, word) in end)
        {
            float shift = to - endWidth;
            laid.Add((cap with { X = cap.X + shift }, keys, word with { Slot = word.Slot with { X = word.Slot.X + shift } }));
        }

        return laid;
    }

    /// <summary>
    /// The list's lines in band space. With <paramref name="hit"/> (a search with text) the first row is the top hit under
    /// its own heading; then a heading before each group that has one, and a rule before the AI rows. With a
    /// <paramref name="lead"/> (AI search's line on why it picked these) that line comes first, in the headings' place.
    /// </summary>
    public static IReadOnlyList<SearchLine> Lines(IReadOnlyList<SearchItem> rows, string? lead = null, bool hit = false)
    {
        var lines = new List<SearchLine>();
        float y = 0;
        void Add(SearchLine line)
        {
            lines.Add(line with { Top = y + (lines.Count > 0 && line.Heading is not null && line.Row < 0 && !line.Rule && lead is null ? HeadingAbove : 0) });
            y = lines[^1].Top + line.Height + LineGap;
        }

        if (lead is not null && rows.Count > 0)
        {
            Add(new(0, HeadingHeight, lead, -1));
        }

        int group = -1;
        for (int i = 0; i < rows.Count; i++)
        {
            if (i == 0 && hit && lead is null && rows[0].Kind is not (SearchKind.Ask or SearchKind.AiSearch or SearchKind.GetEverything))
            {
                Add(new(0, HeadingHeight, TopHit, -1));
                Add(new(0, HitHeight, null, 0, Hit: true));
                continue;
            }

            if (rows[i].Group != group && lead is null)
            {
                group = rows[i].Group;
                if (group == AiGroup)
                {
                    Add(new(0, RuleHeight, null, -1, Rule: true));
                }
                else if (HeadingFor(group) is { } heading)
                {
                    Add(new(0, HeadingHeight, heading, -1));
                }
            }

            Add(new(0, RowHeight, null, i));
        }

        return lines;
    }

    private const int AiGroup = 5;

    public static string? HeadingFor(int group) => group switch
    {
        0 => "APPS",
        1 => "SETTINGS",
        2 => "SHORTCUTS AND SHELF",
        3 => "FILES",
        4 => "WEB",
        _ => null,
    };

    /// <summary>The word for what a row is, at its right end when it has no detail of its own.</summary>
    public static string KindLabel(SearchItem item) => item.Kind switch
    {
        SearchKind.Place => Places.Label(item.Target),
        SearchKind.App => "App",
        SearchKind.Shortcut => "Shortcut",
        SearchKind.Shelf => "On the shelf",
        SearchKind.Folder => "Folder",
        SearchKind.File => "File",
        SearchKind.Web => "Web",
        SearchKind.Ask => "AI",
        SearchKind.AiSearch => "AI",
        _ => "",
    };

    /// <summary>
    /// The type a file's badge shows: its extension in capitals, up to four letters; null for a row that is not a file
    /// with one, whose badge shows its icon or a glyph instead.
    /// </summary>
    public static string? BadgeText(SearchItem item)
    {
        if (item.Kind is not (SearchKind.File or SearchKind.Shelf) || item.Location is null)
        {
            return null;
        }

        string ext = Path.GetExtension(item.Name).TrimStart('.');
        return ext.Length is > 0 and <= 4 ? ext.ToUpperInvariant() : null;
    }

    /// <summary>A badge's fill: a colour by kind of file for a file's type, none for the AI rows' glyph, else the plain raised one.</summary>
    public static uint BadgeFill(SearchItem item) => item.Kind is SearchKind.Ask or SearchKind.AiSearch ? 0
        : BadgeText(item) is null ? PlainBadge
        : Path.GetExtension(item.Name).ToLowerInvariant() switch
        {
            ".pdf" => Theme.KindPdf,
            ".doc" or ".docx" or ".txt" or ".md" or ".rtf" or ".odt" => Theme.KindDocument,
            ".xls" or ".xlsx" or ".csv" or ".ods" or ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" => Theme.KindPicture,
            ".url" or ".ppt" or ".pptx" or ".odp" => Theme.KindLink,
            _ => Theme.KindOther,
        };

    /// <summary>The glyph a badge shows for a row with no file type and no icon of its own, and its ink.</summary>
    public static (GlyphFigure[] Glyph, uint Ink) BadgeGlyph(SearchItem item) => item.Kind switch
    {
        SearchKind.Ask => (Glyphs.Sparkle, Theme.Accent),
        SearchKind.AiSearch => (Glyphs.Search, Theme.Accent),
        SearchKind.Folder => (Glyphs.Folder, Theme.Folder),
        SearchKind.Web => (Glyphs.Globe, Theme.Ink2),
        SearchKind.Place => (Glyphs.Settings, Theme.Ink2),
        SearchKind.GetEverything => (Glyphs.Search, Theme.Ink2),
        SearchKind.Shortcut => (Glyphs.Shortcuts, Theme.Ink2),
        _ => (Glyphs.File, Theme.Ink2),
    };

    /// <summary>
    /// The letters of <paramref name="name"/> the typed words matched, for drawing in the accent: each word where it
    /// first starts a word of the name, else where it first appears. A word found nowhere (a loose match) marks nothing.
    /// </summary>
    public static IReadOnlyList<(int Start, int Length)> Matched(string name, string typed)
    {
        var spans = new List<(int Start, int Length)>();
        foreach (string word in FileQuery.Words(SearchRank.Query(typed)))
        {
            int at = -1;
            for (int i = name.IndexOf(word, StringComparison.CurrentCultureIgnoreCase); i >= 0; i = name.IndexOf(word, i + 1, StringComparison.CurrentCultureIgnoreCase))
            {
                if (at < 0)
                {
                    at = i;
                }

                if (i == 0 || !char.IsLetterOrDigit(name[i - 1]))
                {
                    at = i;
                    break;
                }
            }

            if (at >= 0)
            {
                spans.Add((at, word.Length));
            }
        }

        // Overlapping words ("pri", "pricing") become one span, in order, as DirectWrite's ranges want.
        var merged = new List<(int Start, int Length)>();
        foreach (var (start, length) in spans.OrderBy(s => s.Start))
        {
            if (merged.Count > 0 && start <= merged[^1].Start + merged[^1].Length)
            {
                var last = merged[^1];
                merged[^1] = (last.Start, Math.Max(last.Start + last.Length, start + length) - last.Start);
            }
            else
            {
                merged.Add((start, length));
            }
        }

        return merged;
    }

    /// <summary>The key hint at a row's right end: Enter on the selected top hit and Ask row, Ctrl Enter always on AI search.</summary>
    public static string? KeyFor(SearchItem item, bool hit, bool selected) =>
        item.Kind == SearchKind.AiSearch ? "Ctrl Enter"
        : selected && (hit || item.Kind == SearchKind.Ask) ? "Enter"
        : null;

    /// <summary>
    /// A row's parts in band space: the badge, the name, and the dim text: for the top hit its full path on a second
    /// line, for the others its detail (or what it is) right-aligned, in what room the name leaves, up to 45%.
    /// </summary>
    public SearchRow Row(SearchLine line, SearchItem item, bool selected)
    {
        float w = Band.Width, top = line.Top, h = line.Height;
        float size = line.Hit ? HitBadge : RowBadge;
        var row = new RectangleF(0, top, w, h);
        var badge = new RectangleF(RowPadX, top + ((h - size) / 2), size, size);
        string? key = KeyFor(item, line.Hit, selected);
        var cap = key is null ? RectangleF.Empty : Keycap.EndingAt(key, w - RowPadX, row);
        float x = badge.Right + BadgeGap, right = key is null ? w - RowPadX : cap.X - DetailGap;
        if (line.Hit)
        {
            const float NameLine = 19, PathLine = 16, Between = 2;
            string path = item.Location ?? item.Detail;
            // With nothing to say under it (an app), the name is centred down the row.
            float y = path.Length == 0 ? top + ((h - NameLine) / 2) : top + ((h - NameLine - Between - PathLine) / 2);
            return new(row, HitCorner, badge, HitBadgeCorner,
                new TextSlot(item.Name, HitName, new RectangleF(x, y, Math.Max(0, right - x), NameLine)),
                new TextSlot(path, item.Location is null ? Detail with { Family = TextStyle.Ui, Align = TextAlign.Leading } : HitPath, new RectangleF(x, y + NameLine + Between, Math.Max(0, right - x), PathLine)),
                key, cap);
        }

        string detail = item.Detail.Length > 0 ? item.Detail : KindLabel(item);
        if (item.Kind is SearchKind.Ask)
        {
            detail = "";
        }

        float room = Math.Max(0, right - x);
        float dw = Math.Min(TextSlot.Measure(detail, Detail, room * 0.45f), room * 0.45f);
        return new(row, RowCorner, badge, RowBadgeCorner,
            new TextSlot(item.Name, Name, new RectangleF(x, top, Math.Max(0, room - dw - (dw > 0 ? DetailGap : 0)), h)),
            new TextSlot(detail, Detail, new RectangleF(right - dw, top, dw, h)),
            key, cap);
    }

    /// <summary>The show in folder button: a card this big, 8 DIPs in from the row's right end and centred down it, over what the row shows there.</summary>
    public const float FolderSize = 28, FolderGlyphSize = 16, FolderInset = 8, FolderCorner = 8;

    /// <summary>The button's card is raised a step above the row; its folder is drawn in the name's ink.</summary>
    public const uint FolderFill = Theme.RaisedHi, FolderInk = Theme.Ink;

    public const string FolderTip = "Show in folder", FolderKey = "Ctrl+Shift+Enter", FolderCap = "Ctrl Shift Enter";

    /// <summary>
    /// Whether a row gets the show in folder button: one that is a file or a folder (a file, a folder, a shelf item, a
    /// tile for a file or folder, AI search's picks). Apps, places and the AI rows have nothing to show in a folder.
    /// </summary>
    public static bool HasFolder(SearchItem item) => item.Location is not null;

    /// <summary>The show in folder button's card and its folder's box, in band space, for a line.</summary>
    public (RectangleF Card, RectangleF Glyph) Folder(SearchLine line)
    {
        float x = Band.Width - FolderInset - FolderSize, y = line.Top + ((line.Height - FolderSize) / 2), inset = (FolderSize - FolderGlyphSize) / 2;
        return (new(x, y, FolderSize, FolderSize), new(x + inset, y + inset, FolderGlyphSize, FolderGlyphSize));
    }

    /// <summary>The scroll that shows the line from <paramref name="top"/> to <paramref name="bottom"/> whole, moving as little as it can.</summary>
    public float ScrollTo(float scroll, float top, float bottom, float content) =>
        Math.Clamp(bottom > scroll + Band.Height ? bottom - Band.Height : top < scroll ? top : scroll, 0, Math.Max(0, content - Band.Height));

    /// <summary>
    /// A card in the middle of the band: its box, the title, the body on up to four lines, and the buttons along its
    /// bottom right. The button <paramref name="focus"/> names (the card's own <see cref="SearchCard.Focus"/> when -1) is
    /// the one Enter presses: it takes the accent fill and the accent's ink, the others the plain look. Widths are measured in
    /// the bold style, so moving the focus never moves a button.
    /// </summary>
    public (RectangleF Card, TextSlot Title, TextRun Body, PointF BodyAt, IReadOnlyList<(string Id, RectangleF Box, TextSlot Label)> Buttons) Card(SearchCard card, int focus = -1)
    {
        focus = focus < 0 ? card.Focus : focus;
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
            var style = i == focus ? PaletteLayout.RunButton : PaletteLayout.Button;
            float bw = Math.Max(PaletteLayout.ButtonMinWidth, TextSlot.Measure(label, PaletteLayout.RunButton, inner) + (2 * PaletteLayout.ButtonPadX));
            var at = new RectangleF(right - bw, rowY, bw, PaletteLayout.ButtonHeight);
            buttons.Insert(0, (id, at, new TextSlot(label, style, at)));
            right = at.X - PaletteLayout.ButtonGap;
        }

        return (box, new TextSlot(card.Title, TextStyle.Title, new RectangleF(box.X + CardPad, box.Y + CardPad, inner, 20)),
            new TextRun(card.Body, TextStyle.Secondary, inner, 4), new PointF(box.X + CardPad, box.Y + CardPad + 24), buttons);
    }
}
