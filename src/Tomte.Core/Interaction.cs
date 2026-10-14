using System.Drawing;
using System.Numerics;

namespace Tomte.Core.Interaction;

/// <summary>What a clickable part of a view is, which says how the host treats it (a tab, say, switches as focus reaches it).</summary>
public enum TargetKind { Button, Toggle, Tile, Row, Tab, Link }

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
    /// Tab (+1) or Shift+Tab (-1) from <paramref name="from"/>: the next enabled target in list order, wrapping at both
    /// ends. From -1, nothing focused yet, Tab goes to the first and Shift+Tab to the last. -1 when none is enabled.
    /// </summary>
    public static int Next(IReadOnlyList<Target> targets, int from, int direction)
    {
        int n = targets.Count, step = direction < 0 ? -1 : 1;
        int i = from < 0 ? (step > 0 ? -1 : n) : from;
        for (int k = 0; k < n; k++)
        {
            i = (((i + step) % n) + n) % n;
            if (targets[i].Enabled)
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

    private static Vector2 Centre(RectangleF r) => new(r.X + (r.Width / 2), r.Y + (r.Height / 2));
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

    /// <summary>The focus ring's stroke line for a target: its bounds and corner pushed out to the middle of the stroke.</summary>
    public static (RectangleF Bounds, float Corner) FocusRing(RectangleF bounds, float corner)
    {
        const float Out = FocusGap + (FocusWidth / 2);
        return (RectangleF.Inflate(bounds, Out, Out), corner + Out);
    }
}
