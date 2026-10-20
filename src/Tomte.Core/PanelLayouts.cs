using System.Drawing;
using System.Globalization;
using System.Numerics;
using Tomte.Core.Clipboard;
using Tomte.Core.Interaction;
using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>One line of the Clipboard panel's list: a group's heading, or a row (its index among the rows shown), at its place in list coordinates.</summary>
public readonly record struct ClipboardLine(int Row, string? Heading, float Y, float Height)
{
    public bool IsHeading => Heading is not null;
}

/// <summary>
/// Where the Clipboard panel's parts sit, in DIPs from the body's top-left corner (the body starts
/// under the tab strip). The same numbers draw the panel and place its targets. The head row holds the Keep
/// history switch and the filter field; under it the list, rows grouped under headings by when they were copied,
/// and beside it, wide enough, the preview of the selected row. Lines scroll inside the list; the scroll is how far
/// the list has moved up.
/// </summary>
public sealed class ClipboardPanelLayout(Vector2 body)
{
    public const float Pad = 16, Top = 14, HeadHeight = 36, Gap = 14, FootHeight = 24, Bottom = 16, ColumnGap = 10;
    public const float RowHeight = 44, LineGap = 2, Corner = 12, RowPad = 10, Icon = 18, IconGap = 10;
    public const float HeadingHeight = 24, LaterHeadingHeight = 30, HeadingText = 14;
    public const float FilterWidth = 180, FilterGlyph = 16, FilterPad = 12, FilterGlyphGap = 8;
    public const float SwitchWidth = 34, SwitchHeight = 20, SwitchGap = 8;
    public const float PreviewPad = 14, PreviewCorner = 16, MetaHeight = 14, PreviewGap = 12, ButtonHeight = 36, ButtonGap = 6, ButtonPad = 12, ButtonCorner = 10;
    public const float LockGlyph = 14, LockGap = 12, ClearPad = 8;
    public const float ThumbWidth = 32, ThumbHeight = 20, ThumbCorner = 4, SwatchCorner = 5, PinGlyph = 14;

    /// <summary>One row's step down the list, with the gap after it: what a notch of the wheel scrolls.</summary>
    public const float RowPitch = RowHeight + LineGap;

    /// <summary>Below this body width the preview gives its room to the list.</summary>
    public const float PreviewFrom = 520;

    /// <summary>Clear all asks once: a second click within this many seconds clears.</summary>
    public const int AskSeconds = 3;

    public const string ClearAllText = "Clear all", ClearAllAsk = "Clear all? Click again";
    public const string FilterHint = "Filter", KeepHistory = "Keep history", Enter = "Enter";
    public const string NeverKept = "Items a password manager marks private are never kept";

    /// <summary>Clear all's word at rest, and in the warn colour while it asks.</summary>
    public static TextStyle ButtonStyle => TextStyle.Secondary with { Align = TextAlign.Center };
    public static TextStyle AskStyle => ButtonStyle with { Color = Theme.Warn };

    /// <summary>The preview's meta line, its text, and its buttons' words: Paste on the ink fill, the others on raised.</summary>
    public static TextStyle MetaStyle => new(11, Color: Theme.Ink3, Family: TextStyle.Mono);
    public static TextStyle PreviewStyle => new(14, Color: Theme.Ink, LineHeight: 22.4f);
    public static TextStyle PasteStyle => new(13, 600, Theme.Island, TextAlign.Center);
    public static TextStyle SecondaryButtonStyle => TextStyle.Primary with { Align = TextAlign.Center };

    /// <summary>The filter's hint while it is empty, the footer's words, and a colour row's value in mono.</summary>
    public static TextStyle HintStyle => TextStyle.Primary with { Color = Theme.Ink3 };
    public static TextStyle FootStyle => TextStyle.Secondary with { Color = Theme.Ink3 };
    public static TextStyle ColourStyle => TextStyle.Primary with { Family = TextStyle.Mono };

    public Vector2 Body { get; } = body;

    public RectangleF Head => new(Pad, Top, Body.X - 2 * Pad, HeadHeight);

    /// <summary>The filter field at the head row's right end: a capsule with the magnifier and the text box.</summary>
    public RectangleF Filter => new(Body.X - Pad - Math.Min(FilterWidth, Head.Width / 2), Top, Math.Min(FilterWidth, Head.Width / 2), HeadHeight);

    public RectangleF FilterGlyphBox => new(Filter.X + FilterPad, Top + (HeadHeight - FilterGlyph) / 2, FilterGlyph, FilterGlyph);

    /// <summary>The text box the filter is typed in, one line tall, centred in the field.</summary>
    public RectangleF FilterBox => RectangleF.FromLTRB(FilterGlyphBox.Right + FilterGlyphGap, Top + (HeadHeight - 20) / 2, Filter.Right - FilterPad, Top + (HeadHeight + 20) / 2);

    /// <summary>The switch as drawn, after its label.</summary>
    public RectangleF Switch => new(Pad + MathF.Ceiling(TextSlot.Measure(KeepHistory, TextStyle.Secondary, Head.Width)) + SwitchGap, Top + (HeadHeight - SwitchHeight) / 2, SwitchWidth, SwitchHeight);

    /// <summary>What a click on the switch is tested against: its label and the track, with a little round them.</summary>
    public RectangleF SwitchArea => RectangleF.FromLTRB(Pad - 4, Switch.Y - 4, Switch.Right + 4, Switch.Bottom + 4);

    public RectangleF Foot => new(Pad, Body.Y - Bottom - FootHeight, Body.X - 2 * Pad, FootHeight);

    /// <summary>Whether the preview shows beside the list.</summary>
    public bool HasPreview => Body.X >= PreviewFrom;

    private RectangleF Content => RectangleF.FromLTRB(Pad, Top + HeadHeight + Gap, Body.X - Pad, Foot.Y - Gap);

    /// <summary>The list: the left 1.2 parts of 2.2 beside the preview, or all of the room without it.</summary>
    public RectangleF List => HasPreview ? Content with { Width = (Content.Width - ColumnGap) * 1.2f / 2.2f } : Content;

    /// <summary>The preview's card, right of the list; empty without it.</summary>
    public RectangleF Preview => HasPreview ? RectangleF.FromLTRB(List.Right + ColumnGap, Content.Top, Content.Right, Content.Bottom) : RectangleF.Empty;

    /// <summary>The Clear all button at the right end of the footer, as wide as its word (or its question) and the padding.</summary>
    public RectangleF ClearAll(bool asking)
    {
        float width = MathF.Ceiling(TextSlot.Measure(asking ? ClearAllAsk : ClearAllText, ButtonStyle, Foot.Width / 2)) + 2 * ClearPad;
        return new(Foot.Right - width, Foot.Y, width, FootHeight);
    }

    /// <summary>
    /// The list's lines for these rows: a heading wherever the group changes, then the row, each <see cref="LineGap"/>
    /// apart. The first heading is shorter, as it has nothing above it.
    /// </summary>
    public static IReadOnlyList<ClipboardLine> Lines(IReadOnlyList<ClipboardRow> rows, DateTimeOffset now)
    {
        var lines = new List<ClipboardLine>();
        float y = 0;
        string? group = null;
        for (int i = 0; i < rows.Count; i++)
        {
            string g = rows[i].Group(now);
            if (g != group)
            {
                float h = group is null ? HeadingHeight : LaterHeadingHeight;
                lines.Add(new(-1, g, y, h));
                y += h + LineGap;
                group = g;
            }

            lines.Add(new(i, null, y, RowHeight));
            y += RowPitch;
        }

        return lines;
    }

    /// <summary>A heading's words at the bottom of its line, in list coordinates.</summary>
    public static TextSlot HeadingSlot(ClipboardLine line, float width) =>
        new(line.Heading ?? "", TextStyle.Overline, new(RowPad, line.Y + line.Height - 6 - HeadingText, width - 2 * RowPad, HeadingText));

    /// <summary>How tall the lines are together.</summary>
    public static float Height(IReadOnlyList<ClipboardLine> lines) => lines.Count == 0 ? 0 : lines[^1].Y + lines[^1].Height;

    /// <summary>The square at a row's left end (its icon or swatch), and an image row's thumbnail there, in the row's own coordinates.</summary>
    public static RectangleF IconIn => new(RowPad, (RowHeight - Icon) / 2, Icon, Icon);

    public static RectangleF ThumbIn => new(RowPad, (RowHeight - ThumbHeight) / 2, ThumbWidth, ThumbHeight);

    /// <summary>What sits at a row's right end: its age, the pin, or the Enter keycap on the selected row.</summary>
    public enum RowEnd
    {
        Age,
        Pin,
        Enter,
    }

    /// <summary>The pin at a pinned row's right end, in the row's own coordinates.</summary>
    public static RectangleF PinIn(float rowWidth) => new(rowWidth - RowPad - PinGlyph, (RowHeight - PinGlyph) / 2, PinGlyph, PinGlyph);

    /// <summary>The Enter keycap at the selected row's right end, in the row's own coordinates.</summary>
    public static RectangleF EnterIn(float rowWidth)
    {
        float w = Keycap.Width(Enter);
        return new(rowWidth - RowPad - w, (RowHeight - Keycap.Height) / 2, w, Keycap.Height);
    }

    /// <summary>
    /// A row's end (its age, unless the pin or the keycap stands there) and its text from after the icon, swatch or
    /// thumbnail up to the end, in the row's own coordinates. A colour row's text is in mono.
    /// </summary>
    public static IReadOnlyList<TextSlot> RowText(ClipboardRow row, string ago, RowEnd end, float rowWidth)
    {
        float x = RowPad + (row.IsImage ? ThumbWidth : Icon) + IconGap;
        float endLeft = end switch
        {
            RowEnd.Pin => PinIn(rowWidth).X,
            RowEnd.Enter => EnterIn(rowWidth).X,
            _ => rowWidth - RowPad - MathF.Ceiling(TextSlot.Measure(ago, TextStyle.Small, rowWidth / 2)),
        };
        var text = new TextSlot(row.Display, row.Colour is null ? TextStyle.Primary : ColourStyle, new(x, 0, Math.Max(0, endLeft - IconGap - x), RowHeight));
        return end == RowEnd.Age ? [new(ago, Trailing(TextStyle.Small), RectangleF.FromLTRB(endLeft, 0, rowWidth - RowPad, RowHeight)), text] : [text];
    }

    /// <summary>The preview's meta line at its top, in body coordinates.</summary>
    public TextSlot PreviewMeta(string meta) => new(meta, MetaStyle, new(Preview.X + PreviewPad, Preview.Y + PreviewPad, Preview.Width - 2 * PreviewPad, MetaHeight));

    /// <summary>The room between the meta line and the buttons, where the text or the image goes.</summary>
    public RectangleF PreviewArea => RectangleF.FromLTRB(Preview.X + PreviewPad, Preview.Y + PreviewPad + MetaHeight + PreviewGap, Preview.Right - PreviewPad, Preview.Bottom - PreviewPad - ButtonHeight - PreviewGap);

    /// <summary>The item's whole text in the preview, wrapped to as many lines as fit and cut short after the last.</summary>
    public TextRun PreviewText(string text) => new(text, PreviewStyle, PreviewArea.Width, Math.Max(1, (int)(PreviewArea.Height / PreviewStyle.LineHeight)));

    /// <summary>An image of this many pixels fitted whole into the preview's room, centred, never larger than it is.</summary>
    public RectangleF PreviewImage(int width, int height)
    {
        var area = PreviewArea;
        if (width <= 0 || height <= 0 || area.Width <= 0 || area.Height <= 0)
        {
            return RectangleF.Empty;
        }

        float scale = Math.Min(1, Math.Min(area.Width / width, area.Height / height));
        float w = width * scale, h = height * scale;
        return new(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
    }

    /// <summary>The preview's buttons.</summary>
    public enum PreviewAction
    {
        Paste,
        Pin,
        Shelf,
        Remove,
    }

    public static string ActionId(PreviewAction action) => $"preview:{action}";

    /// <summary>A button's word: Pin turns to Unpin on a pinned row; Remove shows its glyph only.</summary>
    public static string ActionText(PreviewAction action, bool pinned) => action switch
    {
        PreviewAction.Paste => "Paste",
        PreviewAction.Pin => pinned ? "Unpin" : "Pin",
        PreviewAction.Shelf => "To shelf",
        _ => "",
    };

    /// <summary>
    /// The preview's buttons along its bottom, in body coordinates: Paste takes what the others leave, Pin and To shelf
    /// are as wide as their words (Pin as wide as Unpin, so it never moves), Remove a square with its glyph.
    /// </summary>
    public IReadOnlyList<(PreviewAction Action, RectangleF Box)> Actions()
    {
        float Fit(string word) => MathF.Ceiling(TextSlot.Measure(word, SecondaryButtonStyle, Preview.Width)) + 2 * ButtonPad;
        float y = Preview.Bottom - PreviewPad - ButtonHeight, right = Preview.Right - PreviewPad;
        var remove = new RectangleF(right - ButtonHeight, y, ButtonHeight, ButtonHeight);
        float shelfW = Fit(ActionText(PreviewAction.Shelf, false)), pinW = Fit(ActionText(PreviewAction.Pin, true));
        var shelf = new RectangleF(remove.X - ButtonGap - shelfW, y, shelfW, ButtonHeight);
        var pin = new RectangleF(shelf.X - ButtonGap - pinW, y, pinW, ButtonHeight);
        float left = Preview.X + PreviewPad;
        return [(PreviewAction.Paste, RectangleF.FromLTRB(left, y, Math.Max(left, pin.X - ButtonGap), y + ButtonHeight)), (PreviewAction.Pin, pin), (PreviewAction.Shelf, shelf), (PreviewAction.Remove, remove)];
    }

    /// <summary>The heading-less words in the list's box while history is off, empty, or the filter matches nothing: centred, line under line.</summary>
    public IReadOnlyList<TextSlot> BoxText(bool on, ClipboardRetention kept, bool filtered = false)
    {
        string[] lines = !on ? [$"History is off. Turn it on to keep {kept.MaxItems} pieces of text and {kept.MaxImages} images on this PC.", NeverKept + "."]
            : filtered ? ["Nothing matches the filter."] : ["Nothing copied yet."];
        var area = Content;
        float y = area.Y + (area.Height - lines.Length * BoxLineHeight) / 2;
        return [.. lines.Select((line, i) => new TextSlot(line, (i == 0 ? TextStyle.Secondary : TextStyle.Small) with { Align = TextAlign.Center }, new(area.X + 20, y + i * BoxLineHeight, area.Width - 40, BoxLineHeight)))];
    }

    /// <summary>The box the off or empty words sit in.</summary>
    public RectangleF Box => Content;

    /// <summary>The footer's lock glyph at its left end.</summary>
    public RectangleF LockBox => new(Foot.X + 4, Foot.Y + (FootHeight - LockGlyph) / 2, LockGlyph, LockGlyph);

    /// <summary>
    /// Under the list: after the lock, what is never kept and how much is, ending before the button at its widest so it
    /// never moves; and Clear all's word or question in the button.
    /// </summary>
    public IReadOnlyList<TextSlot> FootText(bool asking, ClipboardRetention kept)
    {
        float x = LockBox.Right + LockGap;
        return
        [
            new($"{NeverKept} · last {kept.MaxItems} text, {kept.MaxImages} images", FootStyle, new(x, Foot.Y, Math.Max(0, ClearAll(true).X - LockGap - x), FootHeight)),
            new(asking ? ClearAllAsk : ClearAllText, asking ? AskStyle : ButtonStyle, ClearAll(asking)),
        ];
    }

    public const float BoxLineHeight = 18;

    private static TextStyle Trailing(TextStyle style) => style with { Align = TextAlign.Trailing };

    /// <summary>How far the list can scroll for these lines.</summary>
    public float MaxScroll(IReadOnlyList<ClipboardLine> lines) => Math.Max(0, Height(lines) - List.Height);

    /// <summary>A wheel delta (120 a notch, positive away from the user) moves the list one row per notch, toward the newest first.</summary>
    public float Scrolled(float scroll, int delta, IReadOnlyList<ClipboardLine> lines) => Math.Clamp(scroll - delta / 120f * RowPitch, 0, MaxScroll(lines));

    /// <summary>
    /// The scroll that shows row <paramref name="row"/> whole, moving the list as little as it can; the first row brings
    /// its heading with it.
    /// </summary>
    public float ScrolledTo(IReadOnlyList<ClipboardLine> lines, int row, float scroll)
    {
        int at = -1;
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Row == row && !lines[i].IsHeading)
            {
                at = i;
                break;
            }
        }

        if (at < 0)
        {
            return scroll;
        }

        float top = at == 1 ? 0 : lines[at].Y, bottom = lines[at].Y + lines[at].Height;
        float to = top < scroll ? top : bottom > scroll + List.Height ? bottom - List.Height : scroll;
        return Math.Clamp(to, 0, MaxScroll(lines));
    }

    public const string SwitchId = "switch", ClearId = "clear", FilterId = "filter";

    public static string RowId(long id) => $"row:{id}";

    /// <summary>
    /// The clickable parts in Tab order: the switch, then while history is on the filter field, each row (one group that
    /// Up and Down move along), the selected row's preview buttons (a group of their own) and Clear all while there are
    /// rows. In body space, rows cut to the list where it scrolls. A row out of view keeps its place with no height, so
    /// nothing hits it but the arrow keys still find it. <paramref name="any"/> says history has rows the filter may hide.
    /// </summary>
    public IReadOnlyList<Target> Targets(bool on, IReadOnlyList<ClipboardRow> rows, IReadOnlyList<ClipboardLine> lines, float scroll, bool asking, ClipboardRow? selected = null, bool any = false)
    {
        var all = new List<Target> { new(SwitchId, SwitchArea, 8, TargetKind.Toggle, Tip: $"Keep clipboard history: {(on ? "on" : "off")}", Checked: on) };
        if (!on)
        {
            return all;
        }

        all.Add(new(FilterId, Filter, Filter.Height / 2, TargetKind.Box));
        foreach (var line in lines.Where(l => !l.IsHeading))
        {
            var r = new RectangleF(0, line.Y, List.Width, line.Height);
            all.Add(new(RowId(rows[line.Row].Id), InList(r, scroll), Corner, TargetKind.Row, Tip: "Copy again", Group: 1, Key: "Enter"));
        }

        if (selected is not null && HasPreview)
        {
            foreach (var (action, box) in Actions())
            {
                all.Add(new(ActionId(action), box, ButtonCorner, TargetKind.Button, Tip: action == PreviewAction.Remove ? "Remove" : null, Group: 2, Key: action == PreviewAction.Remove ? "Delete" : null));
            }
        }

        if (any || rows.Count > 0)
        {
            all.Add(new(ClearId, ClearAll(asking), 8, TargetKind.Button));
        }

        return all;
    }

    // A rectangle in list coordinates to body space, cut to the list; out of view it keeps its place with no height.
    private RectangleF InList(RectangleF r, float scroll)
    {
        r.Offset(List.X, List.Y - scroll);
        var shown = RectangleF.Intersect(r, List);
        return shown.IsEmpty ? r with { Height = 0 } : shown;
    }
}

/// <summary>
/// Where the Scratchpad panel's parts sit, in DIPs from the body's top-left corner: the panel's title line with the
/// Preview and Edit switch at its end, the card the note is shown or typed in, and the footer.
/// </summary>
public sealed class ScratchpadPanelLayout(Vector2 body)
{
    public const float Pad = Section.BodyPad, Top = Section.BodyTop, Bottom = Section.BodyPad, HeadHeight = 22, FootHeight = 14, Gap = 12, Corner = Section.Corner;
    public const float TextInset = Section.Pad;

    /// <summary>The switch: a capsule this tall, its segments inset by <see cref="SegmentInset"/>, each its label plus <see cref="SegmentPad"/> a side.</summary>
    public const float SegmentHeight = 26, SegmentInset = 3, SegmentPad = 12;

    public const string PreviewLabel = "Preview", EditLabel = "Edit", PreviewId = "mode:preview", EditId = "mode:edit";

    /// <summary>A label of the switch: the current one in primary ink, the other in secondary.</summary>
    public static TextStyle SegmentStyle(bool current) => new(12, 500, current ? Theme.Ink : Theme.Ink2, TextAlign.Center);

    public Vector2 Body { get; } = body;

    public RectangleF Head => new(Pad, Top, Body.X - 2 * Pad, HeadHeight);

    /// <summary>The card the note is shown or typed in.</summary>
    public RectangleF Note => RectangleF.FromLTRB(Pad, Top + HeadHeight + Gap, Body.X - Pad, Body.Y - Bottom - FootHeight - Gap);

    public RectangleF Foot => new(Pad, Body.Y - Bottom - FootHeight, Body.X - 2 * Pad, FootHeight);

    /// <summary>The note's text size in DIPs.</summary>
    public const float NoteSize = 14;

    /// <summary>Said in the card when the note cannot be read; what is typed then waits for it.</summary>
    public const string Unreadable = "The note could not be opened. What you type is kept and goes in once it opens.";

    /// <summary>Said in the card when the note shown has nothing in it yet.</summary>
    public const string Empty = "Nothing in the note yet. Click here or press Enter to write.";

    /// <summary>The text box the note is typed in, which is also where the shown note sits: the card less its insets. Its text scrolls inside it.</summary>
    public RectangleF Editor => RectangleF.FromLTRB(Note.X + TextInset, Note.Y + 12, Note.Right - TextInset, Note.Bottom - 12);

    /// <summary>The footer's keys while the note is typed in, and while it is shown.</summary>
    public const string Keys = "Tab indents, Esc shows the note, Ctrl+P pins", PreviewKeys = "Click a line to edit it, Space ticks a box, Ctrl+E edits";

    /// <summary>The switch's capsule and its two segments, at the end of the title line and centred on it.</summary>
    public (RectangleF Track, RectangleF Preview, RectangleF Edit) Segments
    {
        get
        {
            float preview = SegmentWidth(PreviewLabel), edit = SegmentWidth(EditLabel), inner = SegmentHeight - 2 * SegmentInset;
            var track = new RectangleF(Head.Right - preview - edit - 2 * SegmentInset, Head.Y + (HeadHeight - SegmentHeight) / 2, preview + edit + 2 * SegmentInset, SegmentHeight);
            float y = track.Y + SegmentInset;
            return (track, new(track.X + SegmentInset, y, preview, inner), new(track.X + SegmentInset + preview, y, edit, inner));
        }
    }

    private static float SegmentWidth(string label) => MathF.Ceiling(TextSlot.Measure(label, SegmentStyle(true), 200)) + 2 * SegmentPad;

    /// <summary>The heading, what goes beside it, and the footer's two ends; the footer names the keys of the mode shown.</summary>
    public IReadOnlyList<TextSlot> FrameText(bool editing = true)
    {
        const string title = "Scratchpad";
        string keysText = editing ? Keys : PreviewKeys;
        float after = TextSlot.Measure(title, TextStyle.PageTitle, Head.Width) + 10;
        float keys = MathF.Ceiling(TextSlot.Measure(keysText, TextStyle.Small, Foot.Width - 150));
        return
        [
            new(title, TextStyle.PageTitle, Head),
            new("one note, saved locally", Section.Note with { Size = 13 }, new(Head.X + after, Head.Y, Math.Max(0, Segments.Track.X - 10 - Head.X - after), HeadHeight)),
            new("Saved on this PC", TextStyle.Small, new(Foot.X, Foot.Y, 140, FootHeight)),
            new(keysText, TextStyle.Small with { Align = TextAlign.Trailing }, new(Foot.Right - keys, Foot.Y, keys, FootHeight)),
        ];
    }
}

/// <summary>A task box in the shown note: the line it is on (that line's start in the note), whether it is ticked, its square, and the name it is read out by.</summary>
public readonly record struct NoteTask(int Line, bool Done, RectangleF Box, string Name)
{
    public string Id => NotePreviewLayout.TaskId(Line);
}

/// <summary>
/// The note shown rather than typed: its Markdown read as the note reads it (<see cref="Markdown.Note"/>) and laid out as
/// answers are (<see cref="DropResultLayout.Lay"/>), task items with a box in place of their marker. Positions are in
/// DIPs from the top-left of the text box's place (<see cref="ScratchpadPanelLayout.Editor"/>), before any scroll.
/// </summary>
public sealed class NotePreviewLayout
{
    public const string TaskPrefix = "task:";

    /// <summary>The words after the last line laid out when the note is longer than a preview lays out.</summary>
    public const string Rest = "The note goes on. Press Ctrl+E to see all of it.";

    public static TextStyle Style => TextStyle.Body;

    public static TextStyle Heading => new(16, 600, LineHeight: 23);

    /// <summary>A wheel notch scrolls three lines.</summary>
    public const float WheelStep = 3 * 21;

    private readonly List<(MarkdownBlock Block, TextAt At, float Height)> parts = [];
    private readonly List<NoteTask> tasks = [];

    public NotePreviewLayout(string note, float width)
    {
        var shift = new Vector2(DropResultLayout.Pad, 0);
        var (runs, height) = DropResultLayout.Lay(Markdown.Note(note, 0, note.Length, fenced: false), Style, width, cut: false, rest: Rest, heading: Heading, laid: (block, at, h, box) =>
        {
            parts.Add((block, at with { At = at.At - shift }, h));
            if (box is { } b && block.Task is bool done)
            {
                tasks.Add(new(block.Source, done, new(b.X - shift.X, b.Y, b.Width, b.Height), $"{(done ? "Done" : "To do")}: {block.Text.Replace('\n', ' ')}"));
            }
        });
        Runs = [.. runs.Select(r => r with { At = r.At - shift })];
        Height = height;
    }

    public IReadOnlyList<TextAt> Runs { get; }

    public float Height { get; }

    public IReadOnlyList<NoteTask> Tasks => tasks;

    /// <summary>The id of the target for the task box on the line that starts at <paramref name="line"/>, the same on Home and on the Scratchpad tab.</summary>
    public static string TaskId(int line) => TaskPrefix + line.ToString(CultureInfo.InvariantCulture);

    /// <summary>The line start a task target's id names; null for any other id.</summary>
    public static int? TaskLine(string id) =>
        id.StartsWith(TaskPrefix, StringComparison.Ordinal) && int.TryParse(id.AsSpan(TaskPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out int line) ? line : null;

    /// <summary>How far the note scrolls in a place <paramref name="band"/> tall.</summary>
    public float MaxScroll(float band) => Math.Max(0, Height - band);

    /// <summary>
    /// Where in the note a click at <paramref name="at"/> (unscrolled) puts the caret: the character under it in the
    /// block it is on, the start of an item's text when it is on the marker or the box, the end of a block when it is in
    /// the gap under it, and the note's end below the last block.
    /// </summary>
    public int SourceAt(Vector2 at, int noteLength)
    {
        if (parts.Count == 0)
        {
            return noteLength;
        }

        int i = parts.FindLastIndex(p => p.At.At.Y <= at.Y);
        var (block, run, height) = parts[Math.Max(0, i)];
        if (at.Y >= run.At.Y + height)
        {
            return i == parts.Count - 1 ? noteLength : block.SourceOf(block.Text.Length);
        }

        if (at.X < run.At.X || at.Y < run.At.Y)
        {
            return block.SourceOf(0);
        }

        using var laid = TextLayout.Of(run.Run);
        return block.SourceOf(laid.Hit(at.X - run.At.X, at.Y - run.At.Y));
    }
}
