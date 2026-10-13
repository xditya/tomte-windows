namespace Tomte.Core;

public enum SpringPreset
{
    /// <summary>420 / 38 / 0.7 (stiffness, damping, mass).</summary>
    Snappy,

    /// <summary>190 / 27 / 0.9.</summary>
    Gentle,
}

/// <summary>
/// A damped spring on one value. The target can change at any time, including mid-flight, and the
/// value keeps its velocity, so a cancelled reveal reverses without a pop.
/// </summary>
public sealed class Spring
{
    private const double SubStep = 0.004;
    private double stiffness = 420;
    private double damping = 38;
    private double mass = 0.7;

    public double Value { get; private set; }

    public double Velocity { get; private set; }

    public double Target { get; set; }

    // Half a pixel at 200%: tight enough that the snap on settle is invisible, loose enough that the window
    // shrinks and the region lands about when the shape looks still rather than a quarter second later.
    public bool Settled => Math.Abs(Value - Target) < 0.25 && Math.Abs(Velocity) < 5;

    public void Use(SpringPreset preset) =>
        (stiffness, damping, mass) = preset == SpringPreset.Gentle ? (190, 27, 0.9) : (420, 38, 0.7);

    /// <summary>Set the value without motion (start-up, reduced motion, snapping on settle).</summary>
    public void Jump(double value) => (Value, Target, Velocity) = (value, value, 0);

    public void Step(double dt)
    {
        int steps = Math.Max(1, (int)Math.Ceiling(dt / SubStep));
        double h = dt / steps;
        for (int i = 0; i < steps; i++)
        {
            double acceleration = (-stiffness * (Value - Target) - damping * Velocity) / mass;
            Velocity += acceleration * h;
            Value += Velocity * h;
        }
    }

    /// <summary>Leaving Hidden, retracting and the drop surface are snappy; panel and palette changes are gentle.</summary>
    public static SpringPreset PresetFor(IslandState from, IslandState to) =>
        from != IslandState.Hidden && (to is IslandState.Panels or IslandState.Palette) ? SpringPreset.Gentle : SpringPreset.Snappy;
}
