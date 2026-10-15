using System.Drawing;
using System.Numerics;
using Tomte.Core.Text;

namespace Tomte.Core.Interaction;

/// <summary>
/// What a clickable part of a view is, which says how the host treats it (a tab, say, switches as focus reaches it). A
/// slider draws its own hover (its knob), so the host draws no state layer over it.
/// </summary>
public enum TargetKind { Button, Toggle, Tile, Row, Tab, Link, Slider }

/// <summary>How a target was activated, for a view where a click, a double click and a key mean different things.</summary>
public enum ActivateBy { Click, DoubleClick, Key }

/// <summary>
/// One clickable part of a view. <paramref name="Bounds"/> is in the view's own DIP space (a panel's body space, or island
/// space for a view that sits on the island itself) and <paramref name="Corner"/> rounds it. A view's list order is its Tab
/// order, and a later target sits on top of an earlier one it overlaps, so a button inside a row comes after the row.
/// Arrow keys move between targets of the same <paramref name="Group"/> of the same view, by where they are; 0 is no
/// group. <paramref name="Tip"/> is the tooltip, which an icon-only target needs. A press on a
/// <paramref name="Draggable"/> target may start a drag rather than a click.
/// </summary>
public readonly record struct Target(string Id, RectangleF Bounds, float Corner, TargetKind Kind, bool Enabled = true,
    string? Tip = null, int Group = 0, bool Draggable = false, bool Checked = false)
{
    /// <summary>
    /// The target under an island point: the views in the order they registered, each with its list and the function
    /// from island space to its own space. A later view sits on top of an earlier one, and within a view a later target
    /// on top of an earlier one. Disabled targets count, so a press on one is nobody else's. Null over none.
    /// </summary>
    public static (int View, int Index)? Hit(IReadOnlyList<(IReadOnlyList<Target> Targets, Func<Vector2, Vector2> ToView)> views, Vector2 at)
    {
        for (int v = views.Count - 1; v >= 0; v--)
        {
            var (targets, toView) = views[v];
            var p = toView(at);
            for (int i = targets.Count - 1; i >= 0; i--)
            {
                var b = targets[i].Bounds;
                if (p.X >= b.Left && p.X < b.Right && p.Y >= b.Top && p.Y < b.Bottom)
                {
                    return (v, i);
                }
            }
        }

        return null;
    }

    /// <summary>
    /// Tab (+1) or Shift+Tab (-1) from <paramref name="from"/>: the next Tab stop in list order, wrapping at both ends.
    /// A target in no group is a stop of its own; a group is one stop, entered at its first enabled member (the host may
    /// put focus on the member last focused instead) and left by Tab, while the arrows move inside it. From -1, nothing
    /// focused yet, Tab goes to the first stop and Shift+Tab to the last. -1 when none is enabled.
    /// </summary>
    public static int Next(IReadOnlyList<Target> targets, int from, int direction)
    {
        int n = targets.Count, step = direction < 0 ? -1 : 1;
        int i = from < 0 ? (step > 0 ? -1 : n) : from;
        int group = from >= 0 && from < n ? targets[from].Group : 0;
        for (int k = 0; k < n; k++)
        {
            i = (((i + step) % n) + n) % n;
            if (!targets[i].Enabled || (group != 0 && targets[i].Group == group && i != from))
            {
                continue;
            }

            if (i == from)
            {
                return i;
            }

            return targets[i].Group == 0 ? i : First(targets, targets[i].Group);
        }

        return -1;
    }

    private static int First(IReadOnlyList<Target> targets, int group)
    {
        for (int i = 0; i < targets.Count; i++)
        {
            if (targets[i].Group == group && targets[i].Enabled)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// An arrow key from <paramref name="from"/> toward <paramref name="direction"/> (a unit step such as (1, 0) for
    /// Right): the nearest enabled target of the same group whose centre lies that way, preferring one in line over one
    /// off to the side (distance along the arrow plus twice the distance across it). No wrapping. -1 when there is none
    /// or the target is in no group.
    /// </summary>
    public static int Arrow(IReadOnlyList<Target> targets, int from, Vector2 direction)
    {
        if (from < 0 || from >= targets.Count || targets[from].Group == 0)
        {
            return -1;
        }

        var start = Centre(targets[from].Bounds);
        int best = -1;
        float bestScore = float.MaxValue;
        for (int i = 0; i < targets.Count; i++)
        {
            var t = targets[i];
            if (i == from || !t.Enabled || t.Group != targets[from].Group)
            {
                continue;
            }

            var delta = Centre(t.Bounds) - start;
            float along = Vector2.Dot(delta, direction);
            if (along <= 0)
            {
                continue;
            }

            float score = along + (2 * (delta - (along * direction)).Length());
            if (score < bestScore)
            {
                (best, bestScore) = (i, score);
            }
        }

        return best;
    }

    /// <summary>
    /// Tab (+1) or Shift+Tab (-1) across several views' lists in the order they registered: <see cref="Next"/> over them
    /// as one list, each view's groups its own. A group is entered at a checked member (the current tab), else at the one
    /// <paramref name="lastIn"/> names for it (the one last focused there), else at its first enabled member. Null when
    /// nothing is enabled.
    /// </summary>
    public static (int View, int Index)? Stop(IReadOnlyList<IReadOnlyList<Target>> views, (int View, int Index)? from, int direction, Func<int, int, string?> lastIn)
    {
        var flat = new List<Target>();
        var at = new List<(int View, int Index)>();
        var groups = new Dictionary<(int View, int Group), int>();
        int start = -1;
        for (int v = 0; v < views.Count; v++)
        {
            for (int i = 0; i < views[v].Count; i++)
            {
                var t = views[v][i];
                if (t.Group != 0 && !groups.ContainsKey((v, t.Group)))
                {
                    groups[(v, t.Group)] = groups.Count + 1;
                }

                start = from == (v, i) ? flat.Count : start;
                flat.Add(t with { Group = t.Group == 0 ? 0 : groups[(v, t.Group)] });
                at.Add((v, i));
            }
        }

        int to = Next(flat, start, direction);
        if (to < 0)
        {
            return null;
        }

        var (view, index) = at[to];
        var list = views[view];
        int group = list[index].Group;
        if (group == 0 || (from is { } f && f.View == view && list[f.Index].Group == group))
        {
            return (view, index);
        }

        string? last = lastIn(view, group);
        int pick = Index(list, m => m.Group == group && m.Enabled && m.Checked);
        pick = pick >= 0 ? pick : Index(list, m => m.Group == group && m.Enabled && m.Id == last);
        return (view, pick >= 0 ? pick : index);
    }

    private static int Index(IReadOnlyList<Target> targets, Func<Target, bool> match)
    {
        for (int i = 0; i < targets.Count; i++)
        {
            if (match(targets[i]))
            {
                return i;
            }
        }

        return -1;
    }

    private static Vector2 Centre(RectangleF r) => new(r.X + (r.Width / 2), r.Y + (r.Height / 2));
}

/// <summary>One target of one registered view, by the view and the target's id, which outlive a change of its list.</summary>
public readonly record struct TargetRef(object View, string Id);

/// <summary>What a key does to the island's targets.</summary>
public enum NavKey { None, Next, Previous, Left, Right, Up, Down, Activate, NextTab, PreviousTab }

/// <summary>Which keys move between the island's targets, and which ones a text box gives up.</summary>
public static class NavKeys
{
    public const int Tab = 0x09, Return = 0x0D, Escape = 0x1B, Space = 0x20;
    private const int VkLeft = 0x25, VkUp = 0x26, VkRight = 0x27, VkDown = 0x28;

    /// <summary>Tab and Shift+Tab move between stops, the arrows inside a group, Enter and Space click, Ctrl+Tab and Ctrl+Shift+Tab switch panel tabs.</summary>
    public static NavKey Of(int virtualKey, bool shift, bool ctrl) => virtualKey switch
    {
        Tab when ctrl => shift ? NavKey.PreviousTab : NavKey.NextTab,
        Tab => shift ? NavKey.Previous : NavKey.Next,
        VkLeft => NavKey.Left,
        VkRight => NavKey.Right,
        VkUp => NavKey.Up,
        VkDown => NavKey.Down,
        Return or Space when !ctrl => NavKey.Activate,
        _ => NavKey.None,
    };

    /// <summary>The unit step of an arrow key in DIP space, y down; zero for any other key.</summary>
    public static Vector2 Direction(NavKey key) => key switch
    {
        NavKey.Left => new(-1, 0),
        NavKey.Right => new(1, 0),
        NavKey.Up => new(0, -1),
        NavKey.Down => new(0, 1),
        _ => Vector2.Zero,
    };

    /// <summary>
    /// A key down in a text box that the box gives up to the targets: Ctrl+Tab from any box, which switches panel tabs,
    /// and Tab or Shift+Tab from one where Tab leaves (the palette's prompt). Tab in the note is a tab.
    /// </summary>
    public static bool LeavesBox(int virtualKey, bool ctrl, bool tabLeaves) => virtualKey == Tab && (ctrl || tabLeaves);
}

/// <summary>
/// The pointer's side of the island's interaction, with no window: the target under the pointer, the one the button went
/// down on and whether the pointer is still on it, and what letting go does. A release on the pressed target activates
/// it; a release anywhere else, or capture going elsewhere, cancels. A second click on the same target within the
/// system's double-click time and box is a double click, and a third one starts over. While the button is down the
/// hover stays on the pressed target.
/// </summary>
public sealed class PointerPress
{
    private TargetRef? lastClick;
    private long lastAt;
    private int lastX, lastY;
    private ActivateBy pending;

    public TargetRef? Hovered { get; private set; }

    public TargetRef? Pressed { get; private set; }

    /// <summary>
    /// True when the button goes down on a target, false when that press ends: released, cancelled or reset. Raised
    /// before a release hands back its click, so whatever the click does finds the press already let go.
    /// </summary>
    public event Action<bool>? Holding;

    /// <summary>The pointer is on the pressed target, which then shows the pressed look; off it, a release cancels.</summary>
    public bool OnPressed { get; private set; }

    /// <summary>The pointer is over <paramref name="under"/> now, null over no target. True when the hover or the pressed look changed.</summary>
    public bool Move(TargetRef? under)
    {
        if (Pressed is { } pressed)
        {
            bool on = under == pressed;
            (bool changed, OnPressed) = (on != OnPressed, on);
            return changed;
        }

        if (under == Hovered)
        {
            return false;
        }

        Hovered = under;
        return true;
    }

    /// <summary>
    /// The pointer left the window: nothing is hovered, and a press that holds the mouse loses its pressed look until the
    /// pointer comes back onto its target. True when the look changed.
    /// </summary>
    public bool Leave()
    {
        if (Pressed is null)
        {
            return Move(null);
        }

        bool was = OnPressed;
        OnPressed = false;
        return was;
    }

    /// <summary>The button went down on <paramref name="target"/> at this screen point and time, with the system's double-click box and time.</summary>
    public void Down(TargetRef target, int x, int y, long ms, int boxX, int boxY, uint timeMs)
    {
        pending = IslandPlace.IsDoubleClick(lastClick == target, x - lastX, y - lastY, ms - lastAt, boxX, boxY, timeMs) ? ActivateBy.DoubleClick : ActivateBy.Click;
        (lastX, lastY, lastAt) = (x, y, ms);
        bool was = Pressed is not null;
        (Pressed, Hovered, OnPressed) = (target, target, true);
        if (!was)
        {
            Holding?.Invoke(true);
        }
    }

    /// <summary>The button came up over <paramref name="under"/>: the pressed target and how it was clicked, or null when the press is cancelled.</summary>
    public (TargetRef Target, ActivateBy By)? Up(TargetRef? under)
    {
        var pressed = Pressed;
        (Pressed, Hovered, OnPressed) = (null, under, false);
        if (pressed is not null)
        {
            Holding?.Invoke(false);
        }

        if (pressed is not { } target || under != target)
        {
            lastClick = null;
            return null;
        }

        lastClick = pending == ActivateBy.Click ? target : null;
        return (target, pending);
    }

    /// <summary>Capture went elsewhere, a drag took the press, or the island hid: nothing activates and no click waits for a second one.</summary>
    public void Cancel()
    {
        bool was = Pressed is not null;
        (Pressed, OnPressed, lastClick) = (null, false, null);
        if (was)
        {
            Holding?.Invoke(false);
        }
    }

    /// <summary>The state layer: the pressed look on the pressed target while the pointer is on it, else the hover look on an enabled hovered one.</summary>
    public (TargetRef Target, uint Fill)? Layer(Func<TargetRef, bool> enabled)
    {
        if (Pressed is { } pressed)
        {
            return OnPressed ? (pressed, StateLook.Pressed) : null;
        }

        return Hovered is { } hovered && enabled(hovered) ? (hovered, StateLook.Hover) : null;
    }

    /// <summary>The island hid: nothing is hovered or pressed.</summary>
    public void Reset()
    {
        Cancel();
        Hovered = null;
    }
}

/// <summary>The one source of interaction colours, sizes and times; colours are ARGB.</summary>
public static class StateLook
{
    /// <summary>The state layer the host lays over a hovered target, on top of the target's own fill: white at about 8%.</summary>
    public const uint Hover = 0x15FFFFFF;

    /// <summary>The same while the button is down on it: about 3%, lighter than hover as in Windows 11's dark theme.</summary>
    public const uint Pressed = 0x08FFFFFF;

    /// <summary>A disabled target's content is drawn at this opacity.</summary>
    public const float DisabledOpacity = 0.4f;

    /// <summary>The accent: the focus ring, and a checked target's fill with its text in <see cref="CheckedText"/>.</summary>
    public const uint Accent = 0xFF6FA8FF, CheckedFill = Accent, CheckedText = 0xFF000000;

    /// <summary>The focus ring is a stroke this wide, this far outside the target's bounds.</summary>
    public const float FocusWidth = 2, FocusGap = 2;

    /// <summary>Smallest targets: any target, a chip's height, a panel action's height.</summary>
    public const float MinTarget = 24, ChipHeight = 32, ActionHeight = 28;

    /// <summary>The state layer's opacity may fade over this; nothing about a shape tweens.</summary>
    public const int FadeMs = 80;

    /// <summary>A tooltip shows after the pointer rests this long on a target with a tip, and goes after this long.</summary>
    public const int TipDelayMs = 600, TipLifetimeMs = 5000;

    /// <summary>A confirmation after an action (a tick and "Copied") shows this long.</summary>
    public const int AcknowledgeMs = 1200;

    /// <summary>
    /// The tooltip's card: this tall, this much room either side of its line, this far off the target and the island's
    /// edges, with these corners; a grey a step above the island's black, with a faint hairline.
    /// </summary>
    public const float TipHeight = 24, TipPadX = 8, TipGap = 5, TipCorner = 6;

    public const uint TipFill = 0xFF2C2C30, TipLine = 0x1AFFFFFF;

    public static readonly TextStyle TipText = new(12, Color: 0xEBFFFFFF);

    /// <summary>An acknowledgement's tick: a filled check this square, this far before the word, in the accent.</summary>
    public const float TickSize = 10, TickGap = 5;

    public static readonly OutlineSegment[] Tick =
    [
        OutlineSegment.Move(new(0, 5.3f)), OutlineSegment.Line(new(1.3f, 4)), OutlineSegment.Line(new(3.8f, 6.5f)),
        OutlineSegment.Line(new(8.7f, 1.6f)), OutlineSegment.Line(new(10, 2.9f)), OutlineSegment.Line(new(3.8f, 9.1f)),
    ];

    /// <summary>The focus ring's stroke line for a target: its bounds and corner pushed out to the middle of the stroke.</summary>
    public static (RectangleF Bounds, float Corner) FocusRing(RectangleF bounds, float corner)
    {
        const float Out = FocusGap + (FocusWidth / 2);
        return (RectangleF.Inflate(bounds, Out, Out), corner + Out);
    }

    /// <summary>
    /// A tooltip, or with <paramref name="tick"/> an acknowledgement, for a target, everything in island space: the card
    /// under the target, else above it, else over it, always inside <paramref name="island"/> (the shape's box), centred
    /// on the target as far as the edges allow; the tick's box; and the one line of text, cut to the room there is.
    /// </summary>
    public static (RectangleF Card, RectangleF Tick, TextSlot Label) Tip(string text, bool tick, RectangleF target, RectangleF island)
    {
        float lead = tick ? TickSize + TickGap : 0;
        float room = Math.Max(0, island.Width - (2 * TipGap) - (2 * TipPadX) - lead);
        float textWidth = TextSlot.Measure(text, TipText, room);
        float width = (2 * TipPadX) + lead + textWidth;
        float x = Math.Clamp(target.X + ((target.Width - width) / 2), island.Left + TipGap, Math.Max(island.Left + TipGap, island.Right - TipGap - width));
        float below = target.Bottom + TipGap, above = target.Top - TipGap - TipHeight;
        float y = below + TipHeight <= island.Bottom - TipGap ? below
            : above >= island.Top + TipGap ? above
            : Math.Clamp(target.Y + ((target.Height - TipHeight) / 2), island.Top, Math.Max(island.Top, island.Bottom - TipHeight));
        var card = new RectangleF(x, y, width, TipHeight);
        var mark = new RectangleF(x + TipPadX, y + ((TipHeight - TickSize) / 2), tick ? TickSize : 0, tick ? TickSize : 0);
        return (card, mark, new TextSlot(text, TipText, new RectangleF(x + TipPadX + lead, y, textWidth, TipHeight)));
    }
}
