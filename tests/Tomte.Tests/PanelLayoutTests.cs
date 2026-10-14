using System.Numerics;
using Tomte.Core;
using Xunit;

namespace Tomte.Tests;

public sealed class PanelLayoutTests
{
    // The Clipboard panel's body: 560 x 360 less the 42 DIP tab strip.
    private static readonly ClipboardPanelLayout Clipboard = new(new Vector2(560, 318));

    [Fact]
    public void ThePartsSitInsideTheBody()
    {
        var list = Clipboard.List;
        Assert.Equal(14, list.X);
        Assert.Equal(40, list.Y);
        Assert.Equal(532, list.Width);
        Assert.Equal(318 - 12 - 14 - 10, list.Bottom);
        Assert.Equal(560 - 14, Clipboard.Switch.Right);
        Assert.True(Clipboard.Foot.Bottom <= 318 - 12);
    }

    [Fact]
    public void APressFindsTheRowItsRemoveButtonTheSwitchAndClearAll()
    {
        float rowMid = Clipboard.List.Y + ClipboardPanelLayout.RowHeight / 2;
        Assert.Equal((ClipboardTarget.Row, 0), Clipboard.Hit(new(100, rowMid), 3, 0, on: true));
        Assert.Equal((ClipboardTarget.Row, 1), Clipboard.Hit(new(100, rowMid + ClipboardPanelLayout.RowPitch), 3, 0, on: true));
        // The gap between rows hits nothing; nor does the space under the last row.
        Assert.Equal((ClipboardTarget.None, -1), Clipboard.Hit(new(100, Clipboard.List.Y + ClipboardPanelLayout.RowHeight + 3), 3, 0, on: true));
        Assert.Equal((ClipboardTarget.None, -1), Clipboard.Hit(new(100, rowMid + 3 * ClipboardPanelLayout.RowPitch), 3, 0, on: true));

        var remove = ClipboardPanelLayout.RemoveIn(Clipboard.List.Width);
        var onRemove = new Vector2(Clipboard.List.X + remove.X + remove.Width / 2, rowMid);
        Assert.Equal((ClipboardTarget.RemoveRow, 0), Clipboard.Hit(onRemove, 3, 0, on: true));

        var sw = Clipboard.Switch;
        Assert.Equal((ClipboardTarget.Switch, -1), Clipboard.Hit(new(sw.X + 5, sw.Y + 5), 0, 0, on: false));
        var clear = Clipboard.ClearAll;
        Assert.Equal((ClipboardTarget.ClearAll, -1), Clipboard.Hit(new(clear.X + 5, clear.Y + 5), 3, 0, on: true));
        // Off or empty there are no rows and no Clear all to press.
        Assert.Equal((ClipboardTarget.None, -1), Clipboard.Hit(new(100, rowMid), 3, 0, on: false));
        Assert.Equal((ClipboardTarget.None, -1), Clipboard.Hit(new(clear.X + 5, clear.Y + 5), 0, 0, on: true));
    }

    [Fact]
    public void TheWheelScrollsOneRowANotchWithinTheList()
    {
        int rows = 50;
        float max = Clipboard.MaxScroll(rows);
        Assert.Equal(rows * ClipboardPanelLayout.RowPitch - ClipboardPanelLayout.RowGap - Clipboard.List.Height, max);
        Assert.Equal(0, Clipboard.MaxScroll(3));

        float down = Clipboard.Scrolled(0, -120, rows);
        Assert.Equal(ClipboardPanelLayout.RowPitch, down);
        Assert.Equal(0, Clipboard.Scrolled(down, 240, rows));
        Assert.Equal(max, Clipboard.Scrolled(max - 1, -120, rows));

        // Scrolled by one row, the top of the list is the second row.
        float top = Clipboard.List.Y + ClipboardPanelLayout.RowHeight / 2;
        Assert.Equal((ClipboardTarget.Row, 1), Clipboard.Hit(new(100, top), rows, down, on: true));
    }

    [Fact]
    public void NoteLinesBreakAtTheWidthAndStopAtTheCard()
    {
        Assert.Equal(["abc", "de", "", "xyz"], ScratchpadPanelLayout.Lines("abcde\r\n\nxyz", 3, 10));
        Assert.Equal(["abc", "de"], ScratchpadPanelLayout.Lines("abcde\nmore", 3, 2));
        Assert.Equal([""], ScratchpadPanelLayout.Lines("", 3, 10));
        // 320 tall less the strip: a 202 DIP card, 10 DIP of padding at either end, 19.5 a line.
        Assert.Equal(9, new ScratchpadPanelLayout(new Vector2(520, 278)).VisibleLines);
    }
}
