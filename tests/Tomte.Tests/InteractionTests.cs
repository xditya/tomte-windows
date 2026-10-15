using System.Drawing;
using System.Numerics;
using Tomte.Core.Interaction;
using Xunit;

namespace Tomte.Tests;

public class InteractionTests
{
    private static readonly Vector2 Right = new(1, 0), Left = new(-1, 0), Down = new(0, 1), Up = new(0, -1);

    private static Target At(string id, float x, float y, float w = 20, float h = 20, bool enabled = true, int group = 0) =>
        new(id, new RectangleF(x, y, w, h), 4, TargetKind.Button, enabled, Group: group);

    [Fact]
    public void HitFindsTheTargetUnderThePointInTheViewsOwnSpace()
    {
        Target[] body = [At("a", 0, 0), At("b", 30, 0)];
        // The view's space starts 100 DIPs right of and 42 below island space's origin.
        var views = new List<(IReadOnlyList<Target>, Func<Vector2, Vector2>)> { (body, p => p - new Vector2(100, 42)) };

        Assert.Equal((0, 1), Target.Hit(views, new Vector2(135, 50)));
        Assert.Equal((0, 0), Target.Hit(views, new Vector2(100, 42)));
        // Right and bottom edges are outside, and so is the gap between targets.
        Assert.Null(Target.Hit(views, new Vector2(120, 50)));
        Assert.Null(Target.Hit(views, new Vector2(110, 62)));
    }

    [Fact]
    public void LaterTargetsAndLaterViewsSitOnTop()
    {
        // A row with its remove button inside it, then a disabled one: a press on it is still nobody else's.
        Target[] rows = [At("row", 0, 0, 200, 40), At("remove", 170, 8, 24, 24), At("off", 0, 0, 10, 10, enabled: false)];
        Target[] over = [At("tip", 180, 10)];
        var views = new List<(IReadOnlyList<Target>, Func<Vector2, Vector2>)> { (rows, p => p) };

        Assert.Equal((0, 1), Target.Hit(views, new Vector2(180, 20)));
        Assert.Equal((0, 0), Target.Hit(views, new Vector2(100, 20)));
        Assert.Equal((0, 2), Target.Hit(views, new Vector2(5, 5)));

        views.Add((over, p => p));
        Assert.Equal((1, 0), Target.Hit(views, new Vector2(185, 15)));
        Assert.Equal((0, 1), Target.Hit(views, new Vector2(172, 30)));
    }

    [Fact]
    public void TabGoesThroughEnabledTargetsInListOrderAndWraps()
    {
        Target[] list = [At("a", 0, 0), At("b", 30, 0, enabled: false), At("c", 60, 0), At("d", 90, 0)];

        Assert.Equal(0, Target.Next(list, -1, 1));
        Assert.Equal(3, Target.Next(list, -1, -1));
        Assert.Equal(2, Target.Next(list, 0, 1));
        Assert.Equal(0, Target.Next(list, 3, 1));
        Assert.Equal(0, Target.Next(list, 2, -1));
        Assert.Equal(3, Target.Next(list, 0, -1));
        // A lone enabled target keeps the focus.
        Assert.Equal(0, Target.Next([At("a", 0, 0), At("b", 30, 0, enabled: false)], 0, 1));
        Assert.Equal(-1, Target.Next([At("a", 0, 0, enabled: false)], -1, 1));
        Assert.Equal(-1, Target.Next([], -1, 1));
    }

    [Fact]
    public void AGroupIsOneTabStopEnteredAtItsFirstMember()
    {
        // A prompt, three chips in group 1 (the first one disabled), and a Send button.
        Target[] list =
        [
            At("prompt", 0, 0), At("chip-a", 0, 40, enabled: false, group: 1), At("chip-b", 40, 40, group: 1),
            At("chip-c", 80, 40, group: 1), At("send", 120, 0),
        ];

        // Tab from the prompt enters the group at its first enabled chip; Tab from any chip leaves the group.
        Assert.Equal(2, Target.Next(list, 0, 1));
        Assert.Equal(4, Target.Next(list, 2, 1));
        Assert.Equal(4, Target.Next(list, 3, 1));
        // Shift+Tab from Send enters the group at its first enabled chip too; from a chip it goes back to the prompt.
        Assert.Equal(2, Target.Next(list, 4, -1));
        Assert.Equal(0, Target.Next(list, 3, -1));
        // A list that is one group: Tab keeps the focus where it is.
        Assert.Equal(1, Target.Next([At("a", 0, 0, group: 2), At("b", 30, 0, group: 2)], 1, 1));
    }

    [Fact]
    public void ArrowsMoveWithinAGroupByWhereTargetsAre()
    {
        // Two rows of three tiles in group 1, a disabled tile at the end of the second row and a button in no group.
        Target[] grid =
        [
            At("a", 0, 0, 100, 80, group: 1), At("b", 110, 0, 100, 80, group: 1), At("c", 220, 0, 100, 80, group: 1),
            At("d", 0, 90, 100, 80, group: 1), At("e", 110, 90, 100, 80, group: 1), At("f", 220, 90, 100, 80, enabled: false, group: 1),
            At("add", 330, 0, 100, 80),
        ];

        Assert.Equal(1, Target.Arrow(grid, 0, Right));
        Assert.Equal(3, Target.Arrow(grid, 0, Down));
        Assert.Equal(1, Target.Arrow(grid, 4, Up));
        Assert.Equal(3, Target.Arrow(grid, 4, Left));
        // Down from the last column: its own cell below is disabled, so the nearest one that way, not across the row.
        Assert.Equal(4, Target.Arrow(grid, 2, Down));
        // No wrapping, nothing past the edge, and the button outside the group is never reached.
        Assert.Equal(-1, Target.Arrow(grid, 2, Right));
        Assert.Equal(-1, Target.Arrow(grid, 0, Up));
        Assert.Equal(-1, Target.Arrow(grid, 6, Left));
        Assert.Equal(-1, Target.Arrow(grid, -1, Right));
    }

    [Fact]
    public void TheFocusRingSitsOutsideTheBoundsAndFollowsTheCorner()
    {
        var (ring, corner) = StateLook.FocusRing(new RectangleF(10, 20, 100, 28), 8);

        // The stroke's middle line: 2 DIP out, then half the 2 DIP stroke, so its inner edge is 2 DIP off the bounds.
        Assert.Equal(new RectangleF(7, 17, 106, 34), ring);
        Assert.Equal(11, corner);
    }

    private static readonly object Strip = new(), Body = new();
    private static readonly TargetRef Ask = new(Strip, "ask"), Media = new(Strip, "Media");

    // The press at a screen point and time, with Windows' default double-click box and time.
    private static void Press(PointerPress p, TargetRef at, int x = 100, long ms = 0) => p.Down(at, x, 10, ms, 4, 4, 500);

    [Fact]
    public void AClickActsOnReleaseOverTheSameTarget()
    {
        var p = new PointerPress();
        Assert.True(p.Move(Ask));
        Assert.False(p.Move(Ask));
        Assert.Equal((Ask, StateLook.Hover), p.Layer(_ => true));
        // A disabled target under the pointer gets no hover look.
        Assert.Null(p.Layer(_ => false));

        Press(p, Ask);
        Assert.Equal((Ask, StateLook.Pressed), p.Layer(_ => true));
        Assert.Equal((Ask, ActivateBy.Click), p.Up(Ask));
        Assert.Null(p.Pressed);
        Assert.Equal(Ask, p.Hovered);
    }

    [Fact]
    public void APressHoldsTheIslandUntilItEndsAndLetsGoBeforeItsClickActs()
    {
        var p = new PointerPress();
        var held = new List<bool>();
        p.Holding += on =>
        {
            // Raised with the press already in its new state: a click on the pill opens the panels only when nothing
            // holds the island, so the release must have let go by the time the click acts.
            Assert.Equal(on, p.Pressed is not null);
            held.Add(on);
        };

        Press(p, Ask);
        Assert.Equal([true], held);
        Assert.Equal((Ask, ActivateBy.Click), p.Up(Ask));
        Assert.Equal([true, false], held);

        // A release off the target, a cancel and a reset each end the hold once; with no press there is nothing to end.
        Press(p, Ask, ms: 2000);
        Assert.Null(p.Up(null));
        Press(p, Ask, ms: 4000);
        p.Cancel();
        p.Cancel();
        Press(p, Ask, ms: 6000);
        p.Reset();
        Assert.Null(p.Up(Ask));
        Assert.Equal([true, false, true, false, true, false, true, false], held);
    }

    [Fact]
    public void MovingOffCancelsAndMovingBackOnClicksAgain()
    {
        var p = new PointerPress();
        Press(p, Ask);

        // Off the target the pressed look goes and nothing else hovers while the button is down.
        Assert.True(p.Move(Media));
        Assert.Null(p.Layer(_ => true));
        Assert.Equal(Ask, p.Hovered);
        Assert.True(p.Move(Ask));
        Assert.Equal((Ask, StateLook.Pressed), p.Layer(_ => true));
        Assert.Equal((Ask, ActivateBy.Click), p.Up(Ask));

        Press(p, Ask, ms: 2000);
        p.Move(null);
        Assert.Null(p.Up(null));
        Assert.Null(p.Hovered);

        // Leaving the window mid-press takes the pressed look but keeps the press; coming back and letting go clicks.
        Press(p, Ask, ms: 4000);
        Assert.True(p.Leave());
        Assert.Null(p.Layer(_ => true));
        Assert.Equal(Ask, p.Pressed);
        p.Move(Ask);
        Assert.Equal((Ask, ActivateBy.Click), p.Up(Ask));

        // The mouse going elsewhere mid-press clicks nothing.
        Press(p, Ask, ms: 6000);
        p.Cancel();
        Assert.Null(p.Up(Ask));
    }

    [Fact]
    public void ASecondClickOnTheSameTargetSoonAfterIsADoubleClick()
    {
        var p = new PointerPress();
        Press(p, Ask, ms: 0);
        Assert.Equal((Ask, ActivateBy.Click), p.Up(Ask));
        Press(p, Ask, x: 101, ms: 300);
        Assert.Equal((Ask, ActivateBy.DoubleClick), p.Up(Ask));
        // A third click starts over.
        Press(p, Ask, ms: 400);
        Assert.Equal((Ask, ActivateBy.Click), p.Up(Ask));

        // Too late, too far, or on another target: two clicks.
        Press(p, Ask, ms: 1000);
        Assert.Equal((Ask, ActivateBy.Click), p.Up(Ask));
        Press(p, Media, ms: 1100);
        Assert.Equal((Media, ActivateBy.Click), p.Up(Media));
        Press(p, Media, x: 110, ms: 1200);
        Assert.Equal((Media, ActivateBy.Click), p.Up(Media));
        Press(p, Media, x: 110, ms: 1900);
        Assert.Equal((Media, ActivateBy.Click), p.Up(Media));

        // A cancelled press does not pair with the next one.
        Press(p, Ask, ms: 3000);
        p.Cancel();
        Press(p, Ask, ms: 3100);
        Assert.Equal((Ask, ActivateBy.Click), p.Up(Ask));
    }

    [Fact]
    public void TabCrossesViewsAndEntersAGroupWhereItWasLeft()
    {
        // The strip: a button, three tabs with Media current, the Ask pill. The body: a button and three tiles, its own group 1.
        IReadOnlyList<Target>[] views =
        [
            [At("button", 0, 0), At("Shelf", 30, 0, group: 1), At("Media", 60, 0, group: 1) with { Checked = true }, At("Clipboard", 90, 0, group: 1), At("ask", 120, 0)],
            [At("add", 0, 50), At("t1", 30, 50, group: 1), At("t2", 60, 50, group: 1), At("t3", 90, 50, group: 1)],
        ];
        string? last = null;
        (int, int)? Tab((int, int)? from, int direction = 1) => Target.Stop(views, from, direction, (v, g) => v == 1 && g == 1 ? last : v == 0 ? "Clipboard" : null);

        Assert.Equal((0, 0), Tab(null));
        // The tab group is entered at the current tab: not its first one, nor the one focused there last.
        Assert.Equal((0, 2), Tab((0, 0)));
        Assert.Equal((0, 4), Tab((0, 2)));
        Assert.Equal((1, 0), Tab((0, 4)));
        // The body's group 1 is not the strip's: Tab enters it, at its first tile until one was focused there.
        Assert.Equal((1, 1), Tab((1, 0)));
        last = "t3";
        Assert.Equal((1, 3), Tab((1, 0)));
        Assert.Equal((0, 0), Tab((1, 3)));
        Assert.Equal((1, 3), Tab((0, 0), -1));
        Assert.Equal((1, 3), Tab(null, -1));
        Assert.Null(Target.Stop([[At("a", 0, 0, enabled: false)]], null, 1, (_, _) => null));
    }

    [Fact]
    public void KeysMapToMovesAndTabStaysTextInTheNote()
    {
        Assert.Equal(NavKey.Next, NavKeys.Of(NavKeys.Tab, shift: false, ctrl: false));
        Assert.Equal(NavKey.Previous, NavKeys.Of(NavKeys.Tab, shift: true, ctrl: false));
        Assert.Equal(NavKey.NextTab, NavKeys.Of(NavKeys.Tab, shift: false, ctrl: true));
        Assert.Equal(NavKey.PreviousTab, NavKeys.Of(NavKeys.Tab, shift: true, ctrl: true));
        Assert.Equal(NavKey.Activate, NavKeys.Of(NavKeys.Return, false, false));
        // Space clicks as Enter does, but stays its own key: on Media it plays or pauses.
        Assert.Equal(NavKey.Space, NavKeys.Of(NavKeys.Space, true, false));
        Assert.Equal(NavKey.None, NavKeys.Of(NavKeys.Space, false, ctrl: true));
        Assert.Equal(NavKey.Left, NavKeys.Of(0x25, false, false));
        Assert.Equal(NavKey.Down, NavKeys.Of(0x28, false, false));
        Assert.Equal(NavKey.Escape, NavKeys.Of(NavKeys.Escape, false, false));
        Assert.Equal(NavKey.None, NavKeys.Of('A', false, false));
        // Alt+Left is the AI box's back arrow; Ctrl+Alt is AltGr, which types.
        Assert.Equal(NavKey.Back, NavKeys.Of(0x25, false, false, alt: true));
        Assert.Equal(NavKey.None, NavKeys.Of(0x25, false, ctrl: true, alt: true));
        Assert.Equal(NavKey.None, NavKeys.Of(0x27, false, false, alt: true));
        Assert.True(NavKeys.Offered(NavKey.Back));
        Assert.False(NavKeys.Offered(NavKey.Left));
        Assert.Equal(NavKey.Home, NavKeys.Of(0x24, false, false));
        Assert.Equal(NavKey.End, NavKeys.Of(0x23, false, false));
        Assert.Equal(NavKey.Remove, NavKeys.Of(0x2E, false, false));
        Assert.Equal(new Vector2(0, -1), NavKeys.Direction(NavKey.Up));
        Assert.Equal(Vector2.Zero, NavKeys.Direction(NavKey.Next));

        // The prompt gives up Tab; the note keeps it and gives up only Ctrl+Tab; other keys are typing in both.
        Assert.True(NavKeys.LeavesBox(NavKey.Next, tabLeaves: true));
        Assert.False(NavKeys.LeavesBox(NavKey.Next, tabLeaves: false));
        Assert.True(NavKeys.LeavesBox(NavKey.NextTab, tabLeaves: false));
        Assert.False(NavKeys.LeavesBox(NavKey.Activate, tabLeaves: true));
        Assert.False(NavKeys.LeavesBox(NavKey.Left, tabLeaves: true));
        // Ctrl+1 to Ctrl+6 pick a tab from any box, the note included.
        Assert.True(NavKeys.LeavesBox(NavKey.Tab1, tabLeaves: false));
        Assert.True(NavKeys.LeavesBox(NavKey.Tab6, tabLeaves: false));
        Assert.False(NavKeys.LeavesBox(NavKey.Ask, tabLeaves: true));
    }

    // Every key the panels and the AI box take, by panel.
    [Theory]
    // Panels: Ctrl+1 to Ctrl+6 pick a tab, Ctrl+K asks; a shifted or Alt one is nothing.
    [InlineData((int)'1', false, true, false, NavKey.Tab1)]
    [InlineData((int)'6', false, true, false, NavKey.Tab6)]
    [InlineData((int)'7', false, true, false, NavKey.None)]
    [InlineData((int)'1', true, true, false, NavKey.None)]
    [InlineData((int)'1', false, false, false, NavKey.None)]
    [InlineData((int)'K', false, true, false, NavKey.Ask)]
    [InlineData((int)'P', false, true, false, NavKey.Pin)]
    [InlineData((int)'P', false, false, false, NavKey.None)]
    [InlineData(0x24, false, false, false, NavKey.Home)]
    [InlineData(0x23, false, false, false, NavKey.End)]
    // Items: Delete removes (Shelf, Clipboard, Shortcuts), Ctrl+C copies (Shelf, Clipboard, History), Ctrl+Shift+E shows
    // a shelf item in its folder, Space plays and pauses (Media).
    [InlineData(0x2E, false, false, false, NavKey.Remove)]
    [InlineData((int)'C', false, true, false, NavKey.Copy)]
    [InlineData((int)'C', true, true, false, NavKey.None)]
    [InlineData((int)'E', true, true, false, NavKey.ShowInFolder)]
    [InlineData((int)'E', false, true, false, NavKey.None)]
    [InlineData(0x20, false, false, false, NavKey.Space)]
    // The AI box: Ctrl+H, Alt+A, the chips' Alt keys, Page Up and Page Down, Alt+Left.
    [InlineData((int)'H', false, true, false, NavKey.History)]
    [InlineData((int)'A', false, false, true, NavKey.AlwaysAllow)]
    [InlineData((int)'W', false, false, true, NavKey.WindowChip)]
    [InlineData((int)'S', false, false, true, NavKey.ScreenshotChip)]
    [InlineData((int)'H', false, false, true, NavKey.ShelfChip)]
    [InlineData((int)'N', false, false, true, NavKey.NoteChip)]
    [InlineData((int)'Q', false, false, true, NavKey.None)]
    [InlineData((int)'W', false, true, true, NavKey.None)]
    [InlineData(0x21, false, false, false, NavKey.PageUp)]
    [InlineData(0x22, false, false, false, NavKey.PageDown)]
    [InlineData(0x25, false, false, true, NavKey.Back)]
    // Ctrl with an arrow or Enter is nothing of the island's.
    [InlineData(0x25, false, true, false, NavKey.None)]
    [InlineData(0x0D, false, true, false, NavKey.None)]
    public void EveryKeyMapsToWhatItDoes(int key, bool shift, bool ctrl, bool alt, NavKey expected) =>
        Assert.Equal(expected, NavKeys.Of(key, shift, ctrl, alt));

    [Fact]
    public void TheAiBoxKeysReachItsViewFromThePromptAndTheChipsSayTheirKeys()
    {
        // From the prompt, the AI box's own keys go to its view first; typing and moving keys stay the box's.
        Assert.All(new[] { NavKey.Back, NavKey.History, NavKey.AlwaysAllow, NavKey.WindowChip, NavKey.NoteChip, NavKey.PageDown, NavKey.Ask }, k => Assert.True(NavKeys.Offered(k), k.ToString()));
        Assert.All(new[] { NavKey.Left, NavKey.Activate, NavKey.Copy, NavKey.Remove, NavKey.Tab1 }, k => Assert.False(NavKeys.Offered(k), k.ToString()));
        // A typed "/" maps through the typed-character path and is never offered from a text box.
        Assert.Equal(NavKey.Ask, NavKeys.Of('/'));

        // The chip a key toggles and the key a chip's tooltip names are the same pair; the selection has none.
        foreach (var icon in new[] { Tomte.Core.ChipIcon.Window, Tomte.Core.ChipIcon.Screenshot, Tomte.Core.ChipIcon.Shelf, Tomte.Core.ChipIcon.Note })
        {
            string key = NavKeys.ChipKey(icon)!;
            Assert.Equal(icon, NavKeys.Chip(NavKeys.Of(key[^1], false, false, alt: true)));
        }

        Assert.Equal("Alt+W", NavKeys.ChipKey(Tomte.Core.ChipIcon.Window));
        Assert.Null(NavKeys.ChipKey(Tomte.Core.ChipIcon.Selection));
        Assert.Null(NavKeys.Chip(NavKey.Copy));

        // "/" asks as Ctrl+K does, read as the character typed.
        Assert.Equal(NavKey.Ask, NavKeys.Of('/'));
        Assert.Equal(NavKey.None, NavKeys.Of('k'));
    }

    [Fact]
    public void TooltipsNameTheirKeyOnlyWhileTheIslandHasTheKeys()
    {
        var remove = At("x", 0, 0) with { Tip = "Remove", Key = "Delete" };
        Assert.Equal("Remove", remove.TipWith(keys: false));
        Assert.Equal("Remove (Delete)", remove.TipWith(keys: true));
        // A tab label says what it is: its tip is the key alone, and nothing without the keys.
        var tab = At("Shelf", 0, 0) with { Key = "Ctrl+1" };
        Assert.Null(tab.TipWith(keys: false));
        Assert.Equal("Ctrl+1", tab.TipWith(keys: true));
        Assert.Equal("Add", (At("a", 0, 0) with { Tip = "Add" }).TipWith(keys: true));
    }

    [Fact]
    public void HomeAndEndJumpToTheEndsOfTheGroupOfTheSameKind()
    {
        // Rows with a remove button each in group 1, the last row's button disabled, and a button in no group.
        static Target In(string id, float y, TargetKind kind, bool enabled = true) => new(id, new RectangleF(0, y, 20, 20), 4, kind, enabled, Group: 1);
        Target[] list =
        [
            In("row1", 0, TargetKind.Row), In("x1", 0, TargetKind.Button), In("row2", 30, TargetKind.Row), In("x2", 30, TargetKind.Button),
            In("row3", 60, TargetKind.Row), In("x3", 60, TargetKind.Button, enabled: false), At("clear", 0, 90),
        ];

        Assert.Equal(4, Target.Edge(list, 0, last: true));
        Assert.Equal(0, Target.Edge(list, 4, last: false));
        // From a remove button: the last enabled one.
        Assert.Equal(3, Target.Edge(list, 1, last: true));
        Assert.Equal(1, Target.Edge(list, 3, last: false));
        // Outside a group, or nothing focused: nowhere.
        Assert.Equal(-1, Target.Edge(list, 6, last: true));
        Assert.Equal(-1, Target.Edge(list, -1, last: false));
    }

    [Fact]
    public void ATipSitsUnderItsTargetElseAboveElseOverItAndStaysOnTheIsland()
    {
        var island = new RectangleF(-300, 0, 600, 300);

        var (card, tick, label) = StateLook.Tip("Ask Tomte anything", tick: false, new RectangleF(240, 7, 50, 28), island);
        Assert.Equal(7 + 28 + StateLook.TipGap, card.Y);
        Assert.Equal(StateLook.TipHeight, card.Height);
        // Centred on the target would run past the right edge, so it is kept the gap inside it.
        Assert.Equal(island.Right - StateLook.TipGap, card.Right, 3);
        Assert.Equal(0, tick.Width);
        Assert.Equal(card.X + StateLook.TipPadX, label.Slot.X, 3);
        Assert.Equal(card.Width, label.Slot.Width + (2 * StateLook.TipPadX), 3);

        // No room under a target at the bottom: above it. A pill too shallow for either: over it, inside the shape.
        Assert.Equal(250 - StateLook.TipGap - StateLook.TipHeight, StateLook.Tip("Remove", false, new RectangleF(0, 250, 24, 40), island).Card.Y);
        var pill = new RectangleF(-110, 0, 220, 38);
        var over = StateLook.Tip("Copied", tick: true, new RectangleF(-100, 6, 26, 26), pill).Card;
        Assert.Equal(7, over.Y);
        Assert.Equal(pill.Left + StateLook.TipGap, over.X, 3);

        // An acknowledgement's tick comes before its word.
        var ack = StateLook.Tip("Copied", tick: true, new RectangleF(-50, 50, 100, 40), island);
        Assert.Equal(StateLook.TickSize, ack.Tick.Width);
        Assert.Equal(ack.Tick.Right + StateLook.TickGap, ack.Label.Slot.X, 3);
    }

    /// <summary>
    /// The tooltip's card drawn by the compositor, offscreen in tools/TextCaptures: one card shows a tip and then a
    /// "Copied", as the island reuses it, and the acknowledgement's mark must be drawn. Nothing is shown on screen.
    /// </summary>
    [GraphicsCaptureFact]
    public void AnAcknowledgementAfterATipDrawsItsTick()
    {
        string dir = Path.Combine(Path.GetTempPath(), "tomte-composition-" + Guid.NewGuid().ToString("N"));
        try
        {
            var start = new System.Diagnostics.ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "TextCaptures.exe"), ["--composition", dir])
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var check = System.Diagnostics.Process.Start(start)!;
            string output = check.StandardOutput.ReadToEnd();
            Assert.True(check.WaitForExit(60_000));
            Assert.True(check.ExitCode == 0, output);
        }
        finally
        {
            if (Directory.Exists(dir))
            {
                Directory.Delete(dir, recursive: true);
            }
        }
    }
}

/// <summary>Runs only where Windows.Graphics.Capture can draw a visual offscreen (not on some server images).</summary>
public sealed class GraphicsCaptureFactAttribute : FactAttribute
{
    public GraphicsCaptureFactAttribute()
    {
        if (!Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported())
            Skip = "Windows.Graphics.Capture is not supported here, so a visual cannot be drawn offscreen.";
    }
}
