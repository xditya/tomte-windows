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
    /// <summary>How much larger than in the pill the tomte pops in: a splash. The art is vector, so it stays sharp.</summary>
    public const double PopScale = 3;

    /// <summary>Seconds after it appears that the tomte sets off for the pill.</summary>
    public const double SetOffSeconds = 0.55;

    /// <summary>
    /// Seconds after it appears that the pill is opened, while the tomte still holds: the first reveal after launch
    /// costs the UI thread a frame or more, which a moving tomte would show as a jump.
    /// </summary>
    public const double OpenPillSeconds = 0.4;

    /// <summary>
    /// Seconds after it appears that the opened pill starts to grow out of the edge: when the tomte, on the gentle
    /// spring from <see cref="SetOffSeconds"/>, is three quarters of the way up, so the pill is there to take it in.
    /// </summary>
    public const double PillGrowsSeconds = 0.74;

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
