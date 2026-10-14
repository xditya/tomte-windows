using System.Drawing;
using System.Numerics;

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
    public const float TextInset = 12, LineHeight = 19.5f;

    public Vector2 Body { get; } = body;

    public RectangleF Head => new(Pad, Top, Body.X - 2 * Pad, HeadHeight);

    /// <summary>The card the note is typed into.</summary>
    public RectangleF Note => RectangleF.FromLTRB(Pad, Top + HeadHeight + Gap, Body.X - Pad, Body.Y - Top - FootHeight - Gap);

    public RectangleF Foot => new(Pad, Body.Y - Top - FootHeight, Body.X - 2 * Pad, FootHeight);

    public int VisibleLines => Math.Max(0, (int)((Note.Height - 2 * 10) / LineHeight));

    /// <summary>
    /// The lines the note's start takes, each broken at <paramref name="chars"/> characters, at most
    /// <paramref name="max"/> of them. Only for placing placeholders: a text renderer measures and
    /// wraps by itself.
    /// </summary>
    public static List<string> Lines(string text, int chars, int max)
    {
        var lines = new List<string>();
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.TrimEnd('\r');
            int start = 0;
            do
            {
                if (lines.Count == max)
                {
                    return lines;
                }

                lines.Add(line.Substring(start, Math.Min(chars, line.Length - start)));
                start += chars;
            }
            while (start < line.Length);
        }

        return lines;
    }
}
