using System.Drawing;
using System.Numerics;
using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>What a press on the clipboard panel hits.</summary>
public enum ClipboardTarget
{
    None,
    Switch,
    Row,
    RemoveRow,
    ClearAll,
}

/// <summary>
/// Where the Clipboard panel's parts sit, in DIPs from the body's top-left corner (the body starts
/// under the tab strip). The same numbers draw the panel and hit-test presses on it. Rows scroll
/// inside the list; the scroll is how far the list has moved up.
/// </summary>
public sealed class ClipboardPanelLayout(Vector2 body)
{
    public const float Pad = 14, Top = 12, HeadHeight = 18, RowHeight = 40, RowGap = 6, Corner = 8;
    public const float Icon = 24, Remove = 18, FootHeight = 14, Gap = 10;
    public const float RowPitch = RowHeight + RowGap;

    public Vector2 Body { get; } = body;

    public RectangleF Head => new(Pad, Top, Body.X - 2 * Pad, HeadHeight);

    public RectangleF Switch => new(Body.X - Pad - 34, Top, 34, HeadHeight);

    public RectangleF List => RectangleF.FromLTRB(Pad, Top + HeadHeight + Gap, Body.X - Pad, Body.Y - Top - FootHeight - Gap);

    public RectangleF Foot => new(Pad, Body.Y - Top - FootHeight, Body.X - 2 * Pad, FootHeight);

    /// <summary>"Clear all" at the right end of the footer.</summary>
    public RectangleF ClearAll => new(Body.X - Pad - 64, Foot.Y - 3, 64, FootHeight + 6);

    /// <summary>Row <paramref name="index"/> in list coordinates (before the scroll moves it).</summary>
    public static RectangleF Row(int index, float width) => new(0, index * RowPitch, width, RowHeight);

    /// <summary>The remove button inside a row, in the row's own coordinates.</summary>
    public static RectangleF RemoveIn(float rowWidth) => new(rowWidth - Gap - Remove, (RowHeight - Remove) / 2, Remove, Remove);

    /// <summary>The heading, what goes beside it, and the switch's label left of the switch.</summary>
    public IReadOnlyList<TextSlot> HeadText(bool on, int keeps)
    {
        const string title = "Clipboard history";
        float after = TextSlot.Measure(title, TextStyle.Title, Head.Width) + Gap;
        return
        [
            new(title, TextStyle.Title, Head),
            new(on ? $"last {keeps} items, this PC only" : "off by default", TextStyle.Secondary, new(Head.X + after, Top, 160, HeadHeight)),
            new("Keep history", Trailing(TextStyle.Secondary), new(Switch.X - Gap - 90, Top, 90, HeadHeight)),
        ];
    }

    /// <summary>Under the rows: what is never kept, and Clear all at the right end.</summary>
    public IReadOnlyList<TextSlot> FootText() =>
    [
        new(NeverKept, TextStyle.Small, new(Foot.X, Foot.Y, Foot.Width - 90, FootHeight)),
        new("Clear all", Trailing(TextStyle.Secondary), new(Foot.Right - 64, Foot.Y, 64, FootHeight)),
    ];

    /// <summary>In the list's box while history is off or empty: centred, line under line.</summary>
    public IReadOnlyList<TextSlot> BoxText(bool on, int keeps)
    {
        string[] lines = on ? ["Nothing copied yet."] : [$"History is off. Turn it on to keep the last {keeps} items on this PC.", NeverKept];
        float y = List.Y + (List.Height - lines.Length * BoxLineHeight) / 2;
        return [.. lines.Select((line, i) => new TextSlot(line, (i == 0 ? TextStyle.Secondary : TextStyle.Small) with { Align = TextAlign.Center }, new(List.X + 20, y + i * BoxLineHeight, List.Width - 40, BoxLineHeight)))];
    }

    /// <summary>
    /// A row's age at its right end before the remove button, then its text from after the icon up to the age, in
    /// the row's own coordinates.
    /// </summary>
    public static IReadOnlyList<TextSlot> RowText(string display, string ago, float rowWidth)
    {
        var remove = RemoveIn(rowWidth);
        float x = Gap + Icon + Gap, age = TextSlot.Measure(ago, TextStyle.Small, 48);
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

    public (ClipboardTarget Target, int Index) Hit(Vector2 point, int rows, float scroll, bool on)
    {
        if (Switch.Contains(point.X, point.Y))
        {
            return (ClipboardTarget.Switch, -1);
        }

        if (!on || rows == 0)
        {
            return (ClipboardTarget.None, -1);
        }

        if (ClearAll.Contains(point.X, point.Y))
        {
            return (ClipboardTarget.ClearAll, -1);
        }

        var list = List;
        if (!list.Contains(point.X, point.Y))
        {
            return (ClipboardTarget.None, -1);
        }

        float y = point.Y - list.Y + scroll;
        int index = (int)(y / RowPitch);
        if (index >= rows || y - index * RowPitch >= RowHeight)
        {
            return (ClipboardTarget.None, -1);
        }

        var remove = RemoveIn(list.Width);
        return remove.Contains(point.X - list.X, y - index * RowPitch) ? (ClipboardTarget.RemoveRow, index) : (ClipboardTarget.Row, index);
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
