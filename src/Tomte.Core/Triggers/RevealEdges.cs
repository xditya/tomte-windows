using System.Drawing;

namespace Tomte.Core.Triggers;

/// <summary>
/// Where an edge push or a drag can reveal the island: a reveal zone on each monitor it may show on, and the bounds of
/// every monitor, since one stacked above another leaves no edge to push against where they meet. Built whole on the UI
/// thread and read on the input thread without allocating.
/// </summary>
public sealed class RevealEdges
{
    public static readonly RevealEdges None = new([], []);

    private readonly RevealZone[] zones;
    private readonly Rectangle[] monitors;

    private RevealEdges(RevealZone[] zones, Rectangle[] monitors) => (this.zones, this.monitors) = (zones, monitors);

    public IReadOnlyList<RevealZone> Zones => zones;

    /// <summary>
    /// A zone on every monitor, or with <paramref name="everyMonitor"/> off only on the primary one, which is the one at
    /// the virtual screen's origin. Each is centred under the island's centre column on its monitor and measured at that
    /// monitor's scale.
    /// </summary>
    public static RevealEdges For(IReadOnlyList<(Rectangle Bounds, double Scale)> all, bool everyMonitor, Func<Rectangle, double, int> centerX, double fraction, double dragBandDip)
    {
        var zones = new List<RevealZone>(all.Count);
        foreach (var (bounds, scale) in all)
        {
            if (everyMonitor || bounds.Contains(0, 0))
            {
                zones.Add(RevealZone.For(bounds.Left, bounds.Top, bounds.Right, centerX(bounds, scale), fraction, dragBandDip, scale));
            }
        }

        return new RevealEdges([.. zones], [.. all.Select(m => m.Bounds)]);
    }

    /// <summary>The zone whose top row the pointer is on, with no monitor above that spot to move on to; -1 for none.</summary>
    public int EdgeAt(int x, int y)
    {
        for (int i = 0; i < zones.Length; i++)
        {
            if (y == zones[i].Top && zones[i].ContainsX(x) && !Above(x, y))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The pointer is in the drag band under one of the zones.</summary>
    public bool InDragBand(int x, int y)
    {
        foreach (var zone in zones)
        {
            if (zone.InDragBand(x, y))
            {
                return true;
            }
        }

        return false;
    }

    private bool Above(int x, int y)
    {
        foreach (var monitor in monitors)
        {
            if (monitor.Contains(x, y - 1))
            {
                return true;
            }
        }

        return false;
    }
}
