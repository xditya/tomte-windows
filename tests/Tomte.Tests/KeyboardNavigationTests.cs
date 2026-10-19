using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Media;
using Tomte.Core.Shortcuts;
using Xunit;

namespace Tomte.Tests;

/// <summary>
/// Tab, Shift+Tab, Left and Right over the island's real target lists: the tab strip as the panel host lists it, then
/// each tab's body from its own layout, walked the way the interaction host walks them.
/// </summary>
public class KeyboardNavigationTests
{
    private const float Width = 760;
    private static readonly Vector2 Body = new(Width, 318);

    // The Clipboard panel's targets with history on and these rows, all copied a moment ago.
    private static IReadOnlyList<Target> Clip(long[] ids, long? selected = null)
    {
        var now = DateTimeOffset.UnixEpoch.AddDays(1);
        var rows = ids.Select(id => new Tomte.Core.Clipboard.ClipboardRow(id, $"item {id}", now)).ToList();
        return new ClipboardPanelLayout(Body).Targets(on: true, rows, ClipboardPanelLayout.Lines(rows, now), 0, asking: false, rows.FirstOrDefault(r => r.Id == selected));
    }

    // One tab per panel in group 1 with the current one checked, then the search button and the settings button.
    private static Target[] Strip(PanelId current)
    {
        var row = TabStrip.Layout(Enum.GetValues<PanelId>(), current, Width, mascot: true, "Win+Alt+Space");
        return
        [
            .. row.Tabs.Select(t => new Target(t.Id.ToString(), t.Bounds, TabStrip.TabHeight / 2, TargetKind.Tab, Group: 1, Checked: t.Id == current)),
            new("search", row.Search, TabStrip.Button / 2, TargetKind.Button),
            new("settings", row.Settings, TabStrip.Button / 2, TargetKind.Button),
        ];
    }

    // Every stop Tab (+1) or Shift+Tab (-1) reaches from nothing focused, until it comes round to the first one again;
    // `last` names the member each group was last left at, as the host remembers it.
    private static List<string> Walk(IReadOnlyList<Target>[] views, int direction = 1, string? last = null)
    {
        var seen = new List<string>();
        (int View, int Index)? at = null;
        for (int k = 0; k < 50; k++)
        {
            at = Target.Stop(views, at, direction, (v, _) => v == 1 ? last : null);
            if (at is not { } a || seen.Contains($"{a.View}:{views[a.View][a.Index].Id}"))
            {
                break;
            }

            seen.Add($"{a.View}:{views[a.View][a.Index].Id}");
        }

        return seen;
    }

    private static Target At(string id, float x, float y, TargetKind kind = TargetKind.Button, int group = 0, bool enabled = true) =>
        new(id, new RectangleF(x, y, 20, 20), 4, kind, enabled, Group: group);

    [Fact]
    public void TabWalksTheStripAndEveryBodyInOrderAndWraps()
    {
        ShelfItem Item(int i) => new(i, $"file {i}.pdf", $@"C:\docs\file {i}.pdf", ShelfKind.Reference, default);
        var items = Enumerable.Range(1, 3).Select(Item).ToList();
        // The shelf with the band showing the first tile's actions: the tiles are one stop, the band another, the filter a third.
        var shelf = ShelfLayout.Targets(Body, 0, items, items[0], [items[0]], false);
        Assert.Equal(["0:Shelf", "0:search", "0:settings", "1:tile:1", "1:act:Open", "1:filter:All", "1:clear"], Walk([Strip(PanelId.Shelf), shelf]));
        Assert.Equal(["1:clear", "1:filter:All", "1:act:Open", "1:tile:1", "0:settings", "0:search", "0:Shelf"], Walk([Strip(PanelId.Shelf), shelf], -1));

        // Shortcuts: the tiles and the add tile are one stop.
        var shortcuts = ShortcutGrid.Targets([new Shortcut(1, "Notes", @"C:\notes.exe", null)], 0, Body);
        Assert.Equal(["0:Shortcuts", "0:search", "0:settings", "1:tile:1"], Walk([Strip(PanelId.Shortcuts), shortcuts]));

        // Media: the seek bar, then each transport button on its own.
        var media = new MediaPanelLayout(Width).Targets(NowPlaying.For("Spotify.exe").WithPlayback(true, 1, MediaCommand.Play | MediaCommand.Pause
            | MediaCommand.Toggle | MediaCommand.Next | MediaCommand.Previous | MediaCommand.Seek, DateTimeOffset.UnixEpoch)
            .WithTimeline(TimeSpan.Zero, TimeSpan.FromSeconds(195), TimeSpan.FromSeconds(48), DateTimeOffset.UnixEpoch, DateTimeOffset.UnixEpoch));
        Assert.Equal(["0:Media", "0:search", "0:settings", "1:seek", "1:previous", "1:playpause", "1:next"], Walk([Strip(PanelId.Media), media]));

        // Scratchpad: the note is a stop, after the strip, and Shift+Tab from the first stop reaches it.
        Target[] note = [new("note", new ScratchpadPanelLayout(Body).Note, 8, TargetKind.Box)];
        Assert.Equal(["0:Scratchpad", "0:search", "0:settings", "1:note"], Walk([Strip(PanelId.Scratchpad), note]));
        Assert.Equal(["1:note", "0:settings", "0:search", "0:Scratchpad"], Walk([Strip(PanelId.Scratchpad), note], -1));

        // Clipboard: the switch, the filter box, the rows (one stop), Clear all.
        var clipboard = Clip([7, 8, 9]);
        Assert.Equal(["0:Clipboard", "0:search", "0:settings", "1:switch", "1:filter", "1:row:7", "1:clear"], Walk([Strip(PanelId.Clipboard), clipboard]));
        Assert.Equal(["1:clear", "1:row:7", "1:filter", "1:switch", "0:settings", "0:search", "0:Clipboard"], Walk([Strip(PanelId.Clipboard), clipboard], -1));
        // A row focused last: Tab comes back into the list at it.
        Assert.Equal(["0:Clipboard", "0:search", "0:settings", "1:switch", "1:filter", "1:row:8", "1:clear"], Walk([Strip(PanelId.Clipboard), clipboard], 1, "row:8"));
        // With a row selected, its preview's buttons are a stop of their own after the rows.
        Assert.Equal(["0:Clipboard", "0:search", "0:settings", "1:switch", "1:filter", "1:row:7", "1:preview:Paste", "1:clear"], Walk([Strip(PanelId.Clipboard), Clip([7, 8, 9], selected: 7)]));

        // History with a conversation open: the rows are one stop, entered at the open one, then Continue and Copy answer.
        var rows = Enumerable.Range(0, 4).Select(i => AiHistoryTests.Talk($"k{i}", DateTimeOffset.UnixEpoch.AddMinutes(-i))).ToList();
        var history = new HistoryPanelLayout(Body, rows, opened: "k2").Targets(0);
        Assert.Equal(["0:History", "0:search", "0:settings", $"1:{HistoryPanelLayout.RowId(rows[2])}", $"1:{HistoryPanelLayout.ContinueId}", $"1:{HistoryPanelLayout.CopyId}"], Walk([Strip(PanelId.History), history]));
    }

    [Fact]
    public void ARowsOwnButtonIsOneTabOnAndShiftTabGoesBackToItsRow()
    {
        Target[] list =
        [
            At("switch", 0, 0, TargetKind.Toggle), At("row:1", 0, 30, TargetKind.Row, 1), At("remove:1", 100, 30, group: 1),
            At("row:2", 0, 60, TargetKind.Row, 1), At("remove:2", 100, 60, group: 1), At("clear", 0, 90),
        ];

        // From any row Tab goes to that row's button, then out of the group; Shift+Tab from the button is its row.
        Assert.Equal(4, Target.Next(list, 3, 1));
        Assert.Equal(5, Target.Next(list, 4, 1));
        Assert.Equal(3, Target.Next(list, 4, -1));
        Assert.Equal(0, Target.Next(list, 3, -1));
        // A disabled button is no stop: Tab from its row leaves the group.
        Assert.Equal(5, Target.Next([.. list[..4], list[4] with { Enabled = false }, list[5]], 3, 1));
        // Groups of one kind are untouched: tiles, tabs, chips.
        Assert.Equal(2, Target.Next([At("a", 0, 0, TargetKind.Tile, 1), At("b", 30, 0, TargetKind.Tile, 1), At("c", 0, 40)], 0, 1));
    }

    [Fact]
    public void AGroupIsNotEnteredAtAChipThatIsOn()
    {
        // The AI box: chips in group 1 with the second one on, then Send.
        Target[] box = [At("chip:Window", 0, 0, TargetKind.Toggle, 1), At("chip:Shelf", 40, 0, TargetKind.Toggle, 1) with { Checked = true }, At("send", 80, 0)];
        Assert.Equal((0, 0), Target.Stop([box], null, 1, (_, _) => null));
        // A checked tab or open row still is where its group is entered.
        Target[] tabs = [At("a", 0, 0, TargetKind.Tab, 1), At("b", 30, 0, TargetKind.Tab, 1) with { Checked = true }];
        Assert.Equal((0, 1), Target.Stop([tabs], null, 1, (_, _) => null));
    }

    [Fact]
    public void LeftAndRightGoAlongAGridAndHaveNothingToDoInAListOrOnATab()
    {
        // Two rows of tiles, read left to right then down; then a band of buttons in group 2.
        Target[] grid =
        [
            At("t1", 0, 0, TargetKind.Tile, 1), At("t2", 30, 0, TargetKind.Tile, 1), At("t3", 0, 30, TargetKind.Tile, 1),
            At("t4", 30, 30, TargetKind.Tile, 1, enabled: false), At("t5", 60, 30, TargetKind.Tile, 1),
            At("open", 0, -40, group: 2), At("copy", 30, -40, group: 2),
        ];
        Assert.Equal(1, Target.Sideways(grid, 0, 1));
        // The end of a row goes on to the next row's first tile, and back.
        Assert.Equal(2, Target.Sideways(grid, 1, 1));
        Assert.Equal(1, Target.Sideways(grid, 2, -1));
        // A disabled tile is passed over; past the first or last tile there is nothing (the host switches tabs).
        Assert.Equal(4, Target.Sideways(grid, 2, 1));
        Assert.Equal(-1, Target.Sideways(grid, 4, 1));
        Assert.Equal(-1, Target.Sideways(grid, 0, -1));
        // A band of buttons goes by where they are, and ends at its ends.
        Assert.Equal(6, Target.Sideways(grid, 5, 1));
        Assert.Equal(-1, Target.Sideways(grid, 6, 1));

        // A list's rows and their buttons, a tab, and a target in no group: Left and Right are the host's tab switch.
        Target[] list = [At("row:1", 0, 0, TargetKind.Row, 1), At("remove:1", 100, 0, group: 1), At("clear", 0, 40)];
        Assert.Equal(-1, Target.Sideways(list, 0, 1));
        Assert.Equal(-1, Target.Sideways(list, 1, -1));
        Assert.Equal(-1, Target.Sideways(list, 2, 1));
        Assert.Equal(-1, Target.Sideways([At("a", 0, 0, TargetKind.Tab, 1), At("b", 30, 0, TargetKind.Tab, 1)], 0, 1));
        Assert.Equal(-1, Target.Sideways(list, -1, 1));
    }

    [Fact]
    public void FocusGoesToWhatTookThePlaceOfARemovedTarget()
    {
        var before = Clip([7, 8, 9]);
        // The middle row goes: the row now in its place.
        var after = Clip([7, 9]);
        Assert.Equal("row:9", after[Target.Refocus(after, 3, before[3], _ => null)].Id);
        // The last row goes: the row before it, not Clear all.
        var last = Clip([7, 8]);
        Assert.Equal("row:8", last[Target.Refocus(last, 4, before[4], _ => null)].Id);
        // The only row goes: the filter box before it.
        var none = Clip([]);
        Assert.Equal("filter", none[Target.Refocus(none, 2, before[2], _ => null)].Id);

        // A closed conversation's Continue: the rows, entered at the one that was open.
        var rows = Enumerable.Range(0, 4).Select(i => AiHistoryTests.Talk($"k{i}", DateTimeOffset.UnixEpoch.AddMinutes(-i))).ToList();
        var open = new HistoryPanelLayout(Body, rows, opened: "k1").Targets(0);
        var closed = new HistoryPanelLayout(Body, rows, opened: null).Targets(0);
        int cont = open.ToList().FindIndex(t => t.Id == HistoryPanelLayout.ContinueId);
        Assert.Equal(HistoryPanelLayout.RowId(rows[1]), closed[Target.Refocus(closed, cont, open[cont], g => g == 1 ? HistoryPanelLayout.RowId(rows[1]) : null)].Id);

        Assert.Equal(-1, Target.Refocus([], 0, before[1], _ => null));
    }

    [Fact]
    public void LeftIntoATabLandsOnItsLastItemAndUpFromTheTopGoesToTheLabels()
    {
        // The last row of a list, not Clear all; the last tile of a grid (the add tile); a view with no group: its last stop.
        var clipboard = Clip([7, 8, 9]);
        Assert.Equal("row:9", clipboard[Target.Last(clipboard)].Id);
        var shortcuts = ShortcutGrid.Targets([new Shortcut(1, "Notes", @"C:\notes.exe", null), new Shortcut(2, "Mail", @"C:\mail.exe", null)], 0, Body);
        Assert.Equal(ShortcutGrid.AddId, shortcuts[Target.Last(shortcuts)].Id);
        Target[] media = [At("seek", 0, 0), At("previous", 0, 40), At("next", 40, 40), At("off", 80, 40, enabled: false)];
        Assert.Equal(2, Target.Last(media));
        Assert.Equal(-1, Target.Last([]));

        // Up from the first row or the top row of a grid has nowhere to go; from lower down it has.
        Assert.True(Target.AtTop(clipboard, 2));
        Assert.False(Target.AtTop(clipboard, 3));
        Assert.True(Target.AtTop(shortcuts, 0));
        // In no group: the switch at the top, the seek bar over the buttons, but not a button under the seek bar.
        Assert.True(Target.AtTop(clipboard, 0));
        Assert.False(Target.AtTop(clipboard, clipboard.Count - 1));
        Assert.True(Target.AtTop(media, 0));
        Assert.False(Target.AtTop(media, 1));
    }

    [Fact]
    public void ThePointerNeverHitsABoxWhoseOwnPressesAreTheTextBoxes()
    {
        Target[] body = [new("note", new RectangleF(0, 0, 100, 100), 8, TargetKind.Box), At("under", 10, 10)];
        Assert.Equal((0, 1), Target.Hit([(body, p => p)], new Vector2(15, 15)));
        Assert.Null(Target.Hit([(body, p => p)], new Vector2(60, 60)));
    }

    [Fact]
    public void LeftAndRightOffTheShelfPageScrollARow()
    {
        // Two rows of four on the page, twelve items: Right from the page's last tile scrolls a row, Left back.
        var body = new Vector2(760, (float)PanelSize.Shelf.H - PanelSpace.TabStripHeight);
        Assert.Equal(2, ShelfLayout.Rows(body));
        Assert.Equal(4, ShelfLayout.ScrollSideways(0, 7, 1, 12, body));
        Assert.Equal(0, ShelfLayout.ScrollSideways(4, 4, -1, 12, body));
        // Inside a row, or past the last or first item, nothing scrolls.
        Assert.Equal(0, ShelfLayout.ScrollSideways(0, 5, 1, 12, body));
        Assert.Equal(4, ShelfLayout.ScrollSideways(4, 11, 1, 12, body));
        Assert.Equal(0, ShelfLayout.ScrollSideways(0, 0, -1, 12, body));
    }
}
