using System.Drawing;
using System.Numerics;
using Tomte.Core.Clipboard;
using Tomte.Core.Interaction;
using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>
/// Where the Clipboard panel's parts sit, in DIPs from the body's top-left corner (the body starts
/// under the tab strip). The same numbers draw the panel and place its targets. Rows scroll
/// inside the list; the scroll is how far the list has moved up.
/// </summary>
public sealed class ClipboardPanelLayout(Vector2 body)
{
    public const float Pad = 14, Top = 12, HeadHeight = 18, RowHeight = 40, RowGap = 6, Corner = 8;
    public const float Icon = 24, Remove = 24, FootHeight = 28, Gap = 10, ButtonPad = 12;
    public const float RowPitch = RowHeight + RowGap;

    /// <summary>Clear all asks once: a second click within this many seconds clears.</summary>
    public const int AskSeconds = 3;

    public const string ClearAllText = "Clear all", ClearAllAsk = "Clear all? Click again";

    /// <summary>Clear all's word at rest, and in the warn colour while it asks.</summary>
    public static readonly TextStyle ButtonStyle = TextStyle.Secondary with { Align = TextAlign.Center };
    public static readonly TextStyle AskStyle = ButtonStyle with { Color = TextStyle.WarnColor };

    public Vector2 Body { get; } = body;

    public RectangleF Head => new(Pad, Top, Body.X - 2 * Pad, HeadHeight);

    /// <summary>The switch as drawn.</summary>
    public RectangleF Switch => new(Body.X - Pad - 34, Top, 34, HeadHeight);

    /// <summary>What a click on the switch is tested against: the track and 3 DIP round it, 40 x 24.</summary>
    public RectangleF SwitchArea => RectangleF.Inflate(Switch, 3, 3);

    public RectangleF List => RectangleF.FromLTRB(Pad, Top + HeadHeight + Gap, Body.X - Pad, Body.Y - Top - FootHeight - Gap);

    public RectangleF Foot => new(Pad, Body.Y - Top - FootHeight, Body.X - 2 * Pad, FootHeight);

    /// <summary>The Clear all button at the right end of the footer, as wide as its word (or its question) and the padding.</summary>
    public RectangleF ClearAll(bool asking)
    {
        float width = MathF.Ceiling(TextSlot.Measure(asking ? ClearAllAsk : ClearAllText, ButtonStyle, Foot.Width / 2)) + 2 * ButtonPad;
        return new(Foot.Right - width, Foot.Y, width, FootHeight);
    }

    /// <summary>Row <paramref name="index"/> in list coordinates (before the scroll moves it).</summary>
    public static RectangleF Row(int index, float width) => new(0, index * RowPitch, width, RowHeight);

    /// <summary>The remove button inside a row, in the row's own coordinates.</summary>
    public static RectangleF RemoveIn(float rowWidth) => new(rowWidth - Gap - Remove, (RowHeight - Remove) / 2, Remove, Remove);

    /// <summary>The fixed square at a text row's left end for what kind of item it is, in the row's own coordinates.</summary>
    public static RectangleF IconIn => new(Gap, (RowHeight - Icon) / 2, Icon, Icon);

    public const float ThumbWidth = 48, ThumbHeight = 32;

    /// <summary>An image row's thumbnail at its left end, where a text row has its icon, in the row's own coordinates.</summary>
    public static RectangleF ThumbIn => new(Gap, (RowHeight - ThumbHeight) / 2, ThumbWidth, ThumbHeight);

    /// <summary>The heading, what goes beside it, and the switch's label left of the switch.</summary>
    public IReadOnlyList<TextSlot> HeadText(bool on, ClipboardRetention kept)
    {
        const string title = "Clipboard history";
        float after = TextSlot.Measure(title, TextStyle.Title, Head.Width) + Gap;
        var label = new RectangleF(Switch.X - Gap - 90, Top, 90, HeadHeight);
        return
        [
            new(title, TextStyle.Title, Head),
            new(on ? $"last {kept.MaxItems} pieces of text and {kept.MaxImages} images" : "off by default", TextStyle.Secondary, RectangleF.FromLTRB(Head.X + after, Top, label.X - Gap, Top + HeadHeight)),
            new("Keep history", Trailing(TextStyle.Secondary), label),
        ];
    }

    /// <summary>
    /// Under the rows: what is never kept, ending before the button at its widest so it never moves, and Clear all's
    /// word or question in the button.
    /// </summary>
    public IReadOnlyList<TextSlot> FootText(bool asking) =>
    [
        new(NeverKept, TextStyle.Small, new(Foot.X, Foot.Y, ClearAll(true).X - Gap - Foot.X, FootHeight)),
        new(asking ? ClearAllAsk : ClearAllText, asking ? AskStyle : ButtonStyle, ClearAll(asking)),
    ];

    /// <summary>In the list's box while history is off or empty: centred, line under line.</summary>
    public IReadOnlyList<TextSlot> BoxText(bool on, ClipboardRetention kept)
    {
        string[] lines = on ? ["Nothing copied yet."] : [$"History is off. Turn it on to keep {kept.MaxItems} pieces of text and {kept.MaxImages} images on this PC.", NeverKept];
        float y = List.Y + (List.Height - lines.Length * BoxLineHeight) / 2;
        return [.. lines.Select((line, i) => new TextSlot(line, (i == 0 ? TextStyle.Secondary : TextStyle.Small) with { Align = TextAlign.Center }, new(List.X + 20, y + i * BoxLineHeight, List.Width - 40, BoxLineHeight)))];
    }

    /// <summary>
    /// A row's age at its right end before the remove button, then its text from after the icon up to the age, in
    /// the row's own coordinates. An image row's text starts after its thumbnail.
    /// </summary>
    public static IReadOnlyList<TextSlot> RowText(string display, string ago, float rowWidth, bool image = false)
    {
        var remove = RemoveIn(rowWidth);
        float x = Gap + (image ? ThumbWidth : Icon) + Gap, age = TextSlot.Measure(ago, TextStyle.Small, 48);
        return
        [
            new(ago, Trailing(TextStyle.Small), new(remove.X - Gap - 48, 0, 48, RowHeight)),
            new(display, TextStyle.Primary, new(x, 0, remove.X - Gap - age - Gap - x, RowHeight)),
        ];
    }

    public const float BoxLineHeight = 18;
    private const string NeverKept = "Items marked by password managers are never kept.";

    private static TextStyle Trailing(TextStyle style) => style with { Align = TextAlign.Trailing };

    /// <summary>How far the list can scroll for this many rows.</summary>
    public float MaxScroll(int rows) => Math.Max(0, rows * RowPitch - RowGap - List.Height);

    /// <summary>A wheel delta (120 a notch, positive away from the user) moves the list one row per notch, toward the newest first.</summary>
    public float Scrolled(float scroll, int delta, int rows) => Math.Clamp(scroll - delta / 120f * RowPitch, 0, MaxScroll(rows));

    /// <summary>The scroll that shows row <paramref name="index"/> whole, moving the list as little as it can.</summary>
    public float ScrolledTo(int index, float scroll, int rows)
    {
        float top = index * RowPitch, bottom = top + RowHeight;
        float to = top < scroll ? top : bottom > scroll + List.Height ? bottom - List.Height : scroll;
        return Math.Clamp(to, 0, MaxScroll(rows));
    }

    public const string SwitchId = "switch", ClearId = "clear";

    public static string RowId(long id) => $"row:{id}";

    public static string RemoveId(long id) => $"remove:{id}";

    /// <summary>
    /// The clickable parts in Tab order: the switch, each row with its remove button after it (so the button is on top of
    /// the row), then Clear all; rows and their buttons are one arrow-key group. In body space, cut to the list where it
    /// scrolls. A row out of view keeps its place with no height, so nothing hits it but the arrow keys still find it.
    /// </summary>
    public IReadOnlyList<Target> Targets(bool on, IReadOnlyList<long> rows, float scroll, bool asking)
    {
        var all = new List<Target> { new(SwitchId, SwitchArea, SwitchArea.Height / 2, TargetKind.Toggle, Tip: $"Keep clipboard history: {(on ? "on" : "off")}", Checked: on) };
        if (!on || rows.Count == 0)
        {
            return all;
        }

        for (int i = 0; i < rows.Count; i++)
        {
            var row = Row(i, List.Width);
            var remove = RemoveIn(List.Width);
            remove.Offset(row.X, row.Y);
            all.Add(new(RowId(rows[i]), InList(row, scroll), Corner, TargetKind.Row, Tip: "Copy again", Group: 1));
            all.Add(new(RemoveId(rows[i]), InList(remove, scroll), Remove / 2, TargetKind.Button, Tip: "Remove", Group: 1));
        }

        all.Add(new(ClearId, ClearAll(asking), Corner, TargetKind.Button));
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

/// <summary>Where the Scratchpad panel's parts sit, in DIPs from the body's top-left corner.</summary>
public sealed class ScratchpadPanelLayout(Vector2 body)
{
    public const float Pad = 14, Top = 12, HeadHeight = 18, FootHeight = 14, Gap = 10, Corner = 8;
    public const float TextInset = 12;

    public Vector2 Body { get; } = body;

    public RectangleF Head => new(Pad, Top, Body.X - 2 * Pad, HeadHeight);

    /// <summary>The card the note is typed into.</summary>
    public RectangleF Note => RectangleF.FromLTRB(Pad, Top + HeadHeight + Gap, Body.X - Pad, Body.Y - Top - FootHeight - Gap);

    public RectangleF Foot => new(Pad, Body.Y - Top - FootHeight, Body.X - 2 * Pad, FootHeight);

    /// <summary>The note's text size in DIPs.</summary>
    public const float NoteSize = 13;

    /// <summary>Said in the card when the note cannot be read; what is typed then waits for it.</summary>
    public const string Unreadable = "The note could not be opened. What you type is kept and goes in once it opens.";

    /// <summary>The text box the note is typed in: the card less its insets. Its text scrolls inside it.</summary>
    public RectangleF Editor => RectangleF.FromLTRB(Note.X + TextInset, Note.Y + 10, Note.Right - TextInset, Note.Bottom - 10);

    /// <summary>The heading, what goes beside it, and the footer's two ends.</summary>
    public IReadOnlyList<TextSlot> FrameText()
    {
        const string title = "Scratchpad";
        float after = TextSlot.Measure(title, TextStyle.Title, Head.Width) + 10;
        return
        [
            new(title, TextStyle.Title, Head),
            new("one note, saved locally", TextStyle.Secondary, new(Head.X + after, Head.Y, Head.Width - after, HeadHeight)),
            new("Saved on this PC", TextStyle.Small, new(Foot.X, Foot.Y, 140, FootHeight)),
            new("Esc closes, Ctrl+P pins", TextStyle.Small with { Align = TextAlign.Trailing }, new(Foot.Right - 160, Foot.Y, 160, FootHeight)),
        ];
    }
}
