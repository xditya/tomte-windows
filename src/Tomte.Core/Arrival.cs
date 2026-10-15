using System.Drawing;
using Tomte.Core.Triggers;

namespace Tomte.Core;

/// <summary>
/// The tomte's flight at launch: it pops in at the middle of the island's monitor, larger than in the pill, rises to
/// the top edge and lands in the Compact pill, which grows out of the edge as it comes. The motion is the island's
/// springs; these are when it sets off, when the pill starts, where it lands and whether it plays at all.
/// </summary>
public static class Arrival
{
    /// <summary>How much larger than in the pill the tomte pops in.</summary>
    public const double PopScale = 1.6;

    /// <summary>Seconds after it appears that the tomte sets off for the pill.</summary>
    public const double SetOffSeconds = 0.55;

    /// <summary>
    /// The pill starts to grow out of the edge once the tomte has come this much of the way up, so it is there to take
    /// the tomte in, and its content starts when the tomte is slowing down, not at full speed.
    /// </summary>
    public const double PillAt = 0.75;

    /// <summary>
    /// Whether it plays: Windows animation effects on, a tomte to show, and a desktop that would let an event show now,
    /// which rules out the lock screen, a fullscreen or presenting app, an app rule against it, and quiet hours or Focus.
    /// </summary>
    public static bool Plays(bool animations, bool tomte, SuppressionState desktop, IReadOnlyList<AppRule> rules) =>
        animations && tomte && Suppression.Allows(RevealTrigger.Event, desktop, rules);

    /// <summary>
    /// The top-left of the tomte's box, in screen pixels, where the island draws it at rest in <paramref name="pill"/>
    /// with the pill centred on pixel column <paramref name="centerX"/>.
    /// </summary>
    public static PointF Landing(in IslandGeometry pill, double scale, int centerX, bool attached)
    {
        var window = IslandOutline.WindowBounds(pill, scale, centerX, attached);
        var (side, top, _) = IslandOutline.Insets(scale, attached);
        var box = MascotArt.Box(pill);
        return new((float)(window.X + side + (pill.OuterWidth / 2 + box.X) * scale), (float)(top + box.Y * scale));
    }
}
