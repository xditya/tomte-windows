using System.Numerics;

namespace Tomte.Core;

/// <summary>What the tomte is doing. Hidden draws nothing; the others sit at the island's left edge.</summary>
public enum MascotState
{
    Hidden,

    /// <summary>Resting at the left edge of Compact, Panels or the palette; drops in from above when the island opens.</summary>
    Compact,

    /// <summary>Tilts after the pointer while something is dragged over the island.</summary>
    ShelfDrop,

    /// <summary>Recording: body stretch and tip lift follow the mic meter.</summary>
    Rambler,

    /// <summary>Waiting on the AI: a sine on the tip rotation.</summary>
    Thinking,

    /// <summary>An answer arrived: a single nod and no sway afterwards.</summary>
    Result,

    /// <summary>A failed or refused action: drooped pose held until another state is set.</summary>
    Error,
}

/// <summary>The end pose a state springs to. Angles in degrees, offsets in viewBox units.</summary>
public readonly record struct MascotPose(double RootRotate = 0, double RootX = 0, double RootY = 0, double CapRotate = 0, double TipRotate = 0);

/// <summary>One row of the state table: the spring it moves with, its pose and its reduced-motion pose.</summary>
public readonly record struct MascotReaction(SpringPreset Preset, MascotPose Pose, MascotPose ReducedPose);

/// <summary>
/// The character in a 64 x 64 viewBox as flat-colour parts. The parts, their pivots and the draw order
/// are the contract the states animate; final art replaces the path data and keeps the rest.
/// </summary>
public static class MascotArt
{
    public const float ViewBox = 64;

    /// <summary>
    /// Rendered size of the 64-unit viewBox and its inset from the island's left edge, in DIPs. The inset leaves the
    /// art (8.1 to 55.9 across) as much room on its left as it has above it in the 42 DIP tab strip: 8.8 DIP.
    /// </summary>
    public const float SizeDips = 26, InsetX = 5.5f;

    /// <summary>The art's middle row, between the cap tip (1) and the bottom of the beard (61).</summary>
    public const float MiddleY = 31;

    /// <summary>Whole character and body: bottom centre. Cap: brim centre. Tip: where it leaves the cone.</summary>
    public static readonly Vector2 RootPivot = new(32, 61);
    public static readonly Vector2 BodyPivot = new(32, 61);
    public static readonly Vector2 CapPivot = new(32, 34);
    public static readonly Vector2 TipPivot = new(29, 10);

    public const uint CapRed = 0xC8322F;
    public const uint BeardColor = 0xECE6DC;
    public const uint BeardShadeColor = 0xD7CFC1;
    public const uint NoseColor = 0xE8A98C;
    public const double CapShadeFactor = 0.76;

    // Draw order: beard, beard shade, nose, cap cone, cap tip (curl, bulb), brim. The brim covers the nose's top.
    public static readonly OutlineSegment[] Beard = [M(13, 35), C(11, 48, 21, 61, 31, 61), C(41, 61, 53, 49, 51, 35)];

    public static readonly OutlineSegment[] BeardShade =
        [M(13, 35), L(51, 35), C(51, 38.5f, 49.5f, 40.5f, 47.5f, 41.5f), C(37.5f, 43.8f, 26.5f, 43.8f, 16.5f, 41.5f), C(14.5f, 40.5f, 13, 38.5f, 13, 35)];

    public static readonly (Vector2 Center, float Radius) Nose = (new(33, 39.8f), 5.6f);

    public static readonly OutlineSegment[] CapCone = [M(11, 34), C(12, 23, 18, 13, 27, 10), L(34, 10), C(42, 15, 48.5f, 24, 53, 34)];

    public static readonly OutlineSegment[] TipCurl =
        [M(27, 10), C(27, 3, 35, -1, 41, 2), C(46, 4, 48.5f, 10, 46.5f, 15.5f), C(44.5f, 11.5f, 40.5f, 9.2f, 36.5f, 9.6f), C(33, 10, 30, 11, 27, 10)];

    public static readonly (Vector2 Center, float Radius) TipBulb = (new(46.5f, 15.5f), 3.8f);

    public static readonly OutlineSegment[] Brim = [M(9, 31), L(55, 31), C(56.2f, 33, 56.2f, 35, 55, 37), L(9, 37), C(7.8f, 35, 7.8f, 33, 9, 31)];

    /// <summary>
    /// The tomte's box in island space (x from the shape's centre line, y from its top) for the shape it sits in: at the
    /// left edge, with the art centred down the top row, which is the shape's depth up to the tab strip's height. The
    /// peek pill is the box with its inset on both sides, so there the tomte is centred both ways.
    /// </summary>
    public static System.Drawing.RectangleF Box(in IslandGeometry shape)
    {
        float row = (float)Math.Min(shape.Depth, PanelSpace.TabStripHeight);
        return new(-(float)shape.Width / 2 + InsetX, row / 2 - MiddleY * SizeDips / ViewBox, SizeDips, SizeDips);
    }

    /// <summary>
    /// The shape the tomte keeps to through a morph, or null for the live outline. Revealing a pill (Compact, the peek
    /// badge, the rambler's wider pill: Compact's depth), it drops in where the pill will be and the pill opens around
    /// it; retracting, it stays at the open shape's edge as it goes. Revealing anything deeper (the palette, the drop
    /// surface) it rides the outline's left edge, as it does when a reveal turns into Panels, so it is never drawn
    /// beside a shape still growing toward it.
    /// </summary>
    public static IslandGeometry? Anchor(in IslandGeometry from, in IslandGeometry to) =>
        from.Depth <= 0.5 ? (to.Depth <= IslandGeometry.Compact.Depth ? to : null) : to.Depth <= 0.5 ? from : null;

    /// <summary>The brim colour: the cap colour at 76% brightness, so a user-chosen cap keeps its shading.</summary>
    public static uint Shade(uint rgb)
    {
        static uint Channel(uint c) => (uint)Math.Round(c * CapShadeFactor);
        return (Channel((rgb >> 16) & 0xFF) << 16) | (Channel((rgb >> 8) & 0xFF) << 8) | Channel(rgb & 0xFF);
    }

    private static OutlineSegment M(float x, float y) => OutlineSegment.Move(new(x, y));

    private static OutlineSegment L(float x, float y) => OutlineSegment.Line(new(x, y));

    private static OutlineSegment C(float x1, float y1, float x2, float y2, float x, float y) => OutlineSegment.Cubic(new(x1, y1), new(x2, y2), new(x, y));
}

/// <summary>The state table and the timeline values of the seven states.</summary>
public static class MascotStates
{
    /// <summary>The reveal's start pose: the bottom of the beard (61) above the island's top edge from the tab strip's inset.</summary>
    public const double PeekFromY = -82;
    public const double IslandLeadSeconds = 0.07;
    public const double PeekToRestSeconds = 0.7;
    public const double SwayAngle = 3;
    public const double SwayStepSeconds = 0.55;
    public const double SwayPauseMinSeconds = 4;
    public const double SwayPauseMaxSeconds = 7;
    public const double LeanReachPx = 40;
    public const double LeanAngle = 12;
    public const double LeanShift = 1.5;
    public const double NodAngle = 12;
    public const double NodHoldSeconds = 0.18;
    public const double NodSettleSeconds = 0.5;
    public const double ResultDelaySeconds = 0.22;
    public const double RamblerScaleY = 0.25;
    public const double RamblerScaleX = 0.08;
    public const double RamblerTip = -10;
    public const double LevelSmoothingPer60HzFrame = 0.35;
    public const double ThinkAmplitude = 14;
    public const double ThinkPeriodSeconds = 0.8;

    /// <summary>How far the tomte lifts while the pointer is on it, in DIPs, on top of whatever pose it holds.</summary>
    public const double HoverLiftDips = 1.5;

    public static readonly IReadOnlyDictionary<MascotState, MascotReaction> Table = new Dictionary<MascotState, MascotReaction>
    {
        // Retracting lifts it back above the screen, the reveal run backwards.
        [MascotState.Hidden] = new(SpringPreset.Snappy, new(RootY: PeekFromY), new(RootY: PeekFromY)),
        [MascotState.Compact] = new(SpringPreset.Gentle, default, default),
        [MascotState.ShelfDrop] = new(SpringPreset.Snappy, default, default),
        [MascotState.Rambler] = new(SpringPreset.Snappy, default, default),
        [MascotState.Thinking] = new(SpringPreset.Gentle, default, new(TipRotate: 8)),
        [MascotState.Result] = new(SpringPreset.Gentle, default, default),
        [MascotState.Error] = new(SpringPreset.Gentle, new(RootY: 1.5, CapRotate: 8, TipRotate: 38), new(RootY: 1.5, CapRotate: 8, TipRotate: 38)),
    };

    /// <summary>
    /// The island state's mascot. Capture keeps the island hidden, so the tomte is too. Panels and the palette
    /// show it resting at their left edge; the AI and rambler reactions are set on top by those features.
    /// </summary>
    public static MascotState For(IslandState island) => island switch
    {
        IslandState.Hidden or IslandState.Capture => MascotState.Hidden,
        IslandState.ShelfDrop => MascotState.ShelfDrop,
        _ => MascotState.Compact,
    };
}

/// <summary>
/// The tomte's seven springs and its timeline. Stepped by the island's compositor tick; never runs a clock
/// of its own. Scheduled beats (sway, nod) only ask for a wake-up time, so a resting tomte parks the tick.
/// </summary>
public sealed class Mascot
{
    private readonly Func<double> clock;
    private readonly Func<bool> reduceMotion;
    private readonly Spring rootRotate = new();
    private readonly Spring rootX = new();
    private readonly Spring rootY = new();
    private readonly Spring scaleX = new(); // percent away from 1, so the spring's rest threshold suits it
    private readonly Spring scaleY = new();
    private readonly Spring capRotate = new();
    private readonly Spring tipRotate = new();
    private readonly Spring lift = new(); // viewBox units, added to RootY
    private readonly Spring[] springs;
    private bool enabled = true;
    private uint capColor = MascotArt.CapRed;
    private bool reduce;
    private Beat next;
    private double nextAt;
    private double enteredAt;
    private double level;
    private double smoothed;
    private double driveTip;
    private double driveScaleX;
    private double driveScaleY;
    private double islandLead;

    /// <param name="clock">Seconds, the same clock the island's tick uses.</param>
    /// <param name="reduceMotion">True when Windows animation effects are off; read at each state change.</param>
    public Mascot(Func<double> clock, Func<bool> reduceMotion)
    {
        this.clock = clock;
        this.reduceMotion = reduceMotion;
        springs = [rootRotate, rootX, rootY, scaleX, scaleY, capRotate, tipRotate, lift];
        rootY.Jump(MascotStates.PeekFromY);
    }

    private enum Beat
    {
        None,
        Rest,
        SwayLeft,
        SwayRight,
        SwayBack,
        Nod,
        NodBack,
        NodDone,
    }

    /// <summary>Raised on any change that needs a frame; the island resumes its tick if it is showing.</summary>
    public Action? Changed { get; set; }

    /// <summary>The settings off switch. Off draws nothing and costs nothing; the island works the same.</summary>
    public bool Enabled
    {
        get => enabled;
        set
        {
            enabled = value;
            Changed?.Invoke();
        }
    }

    /// <summary>Cap colour as 0xRRGGBB, defaulting to CapRed. The brim follows at 76% brightness.</summary>
    public uint CapColor
    {
        get => capColor;
        set
        {
            capColor = value & 0xFFFFFF;
            Changed?.Invoke();
        }
    }

    public MascotState State { get; private set; } = MascotState.Hidden;

    /// <summary>Drawn while on and not hidden, and while the retract lift is still moving.</summary>
    public bool Visible => enabled && (State != MascotState.Hidden || !AtRest);

    /// <summary>Microphone level 0..1 for the rambler state, at whatever rate the meter reports. Set it on the island's thread: it wakes the tick's timer.</summary>
    public double Level
    {
        get => level;
        set
        {
            level = Math.Clamp(value, 0, 1);
            Changed?.Invoke();
        }
    }

    public double RootRotate => rootRotate.Value;

    public double RootX => rootX.Value;

    public double RootY => rootY.Value + lift.Value;

    public double ScaleX => 1 + (scaleX.Value + driveScaleX) / 100;

    public double ScaleY => 1 + (scaleY.Value + driveScaleY) / 100;

    public double CapRotate => capRotate.Value;

    public double TipRotate => tipRotate.Value + driveTip;

    /// <summary>Seconds until the next scheduled beat, or null when nothing is scheduled or the tomte is off.</summary>
    public double? WakeIn => !enabled || next == Beat.None ? null : Math.Max(0, nextAt - clock());

    private bool AtRest => Array.TrueForAll(springs, static s => s.Settled);

    /// <summary>Follow an island state change: peek in from Hidden, nod on a drop, keep a running reaction otherwise.</summary>
    public void Follow(IslandState from, IslandState to)
    {
        var target = MascotStates.For(to);
        if (target == MascotState.Compact && from == IslandState.ShelfDrop)
        {
            Drop();
        }
        else if (target != MascotState.Compact ? target != State : State == MascotState.Hidden)
        {
            Set(target);
        }
    }

    public void Set(MascotState state)
    {
        bool peek = state == MascotState.Compact && !Visible;
        // A reaction set during the reveal keeps the drop-in snappy; only the peek schedules Rest.
        bool peekMoving = State == MascotState.Compact && next == Beat.Rest;
        Fold();
        reduce = reduceMotion();
        State = state;
        enteredAt = clock();
        next = Beat.None;
        var reaction = MascotStates.Table[state];
        var pose = reduce ? reaction.ReducedPose : reaction.Pose;
        var preset = peek ? SpringPreset.Snappy : reaction.Preset;
        To(rootRotate, pose.RootRotate, preset);
        To(rootX, pose.RootX, preset);
        To(rootY, pose.RootY, peekMoving ? SpringPreset.Snappy : preset);
        To(scaleX, 0, preset);
        To(scaleY, 0, preset);
        To(capRotate, pose.CapRotate, preset);
        To(tipRotate, pose.TipRotate, preset);
        if (peek && !reduce)
        {
            // The tomte drops in from above the edge; the shape starts 70 ms later.
            rootY.Jump(MascotStates.PeekFromY);
            rootY.Target = 0;
            islandLead = enabled ? MascotStates.IslandLeadSeconds : 0;
            At(MascotStates.PeekToRestSeconds, Beat.Rest);
        }
        else if (state == MascotState.Compact)
        {
            ScheduleSway();
        }
        else if (state == MascotState.Result && !reduce)
        {
            At(MascotStates.ResultDelaySeconds, Beat.Nod);
        }

        Changed?.Invoke();
    }

    /// <summary>The dragged item's x minus the mascot's x, in screen pixels. Only moves the tomte during Shelf-drop.</summary>
    public void Lean(double dx)
    {
        if (State != MascotState.ShelfDrop)
        {
            return;
        }

        double k = Math.Clamp(dx / MascotStates.LeanReachPx, -1, 1);
        To(rootRotate, k * MascotStates.LeanAngle, SpringPreset.Snappy);
        To(rootX, Math.Sign(k) * MascotStates.LeanShift, SpringPreset.Snappy);
        Changed?.Invoke();
    }

    /// <summary>
    /// The pointer came onto the tomte, or left it: it lifts <see cref="MascotStates.HoverLiftDips"/> on the snappy
    /// spring and settles back, a retarget from wherever it is.
    /// </summary>
    public void Hover(bool on)
    {
        double target = on ? -MascotStates.HoverLiftDips * MascotArt.ViewBox / MascotArt.SizeDips : 0;
        if (target != lift.Target)
        {
            To(lift, target, SpringPreset.Snappy);
            Changed?.Invoke();
        }
    }

    /// <summary>The island just hid: drop every animation so nothing wakes the tick while hidden.</summary>
    public void Park()
    {
        Set(MascotState.Hidden);
        lift.Target = 0;
        foreach (var spring in springs)
        {
            spring.Jump(spring.Target);
        }
    }

    /// <summary>The island's first IslandLeadSeconds of a reveal wait for the peek; read once per reveal.</summary>
    public double TakeIslandLead()
    {
        double lead = islandLead;
        islandLead = 0;
        return lead;
    }

    /// <summary>Advance one frame. Returns true while the tomte needs the next frame too.</summary>
    public bool Step(double dt)
    {
        if (!enabled)
        {
            return false;
        }

        double now = clock();
        while (next != Beat.None && now >= nextAt)
        {
            var beat = next;
            next = Beat.None;
            Fire(beat);
        }

        foreach (var spring in springs)
        {
            spring.Step(dt);
        }

        if (State == MascotState.Rambler)
        {
            // The smoothing is defined per 60 Hz frame; this keeps the same response at any refresh rate.
            double a = reduce ? 1 : 1 - Math.Pow(1 - MascotStates.LevelSmoothingPer60HzFrame, dt * 60);
            smoothed += (level - smoothed) * a;
            driveScaleY = 100 * MascotStates.RamblerScaleY * smoothed;
            driveScaleX = -100 * MascotStates.RamblerScaleX * smoothed;
            driveTip = MascotStates.RamblerTip * smoothed;
            // Reduced motion shows the pose for the current level only; the Level setter wakes the next frame.
            return !reduce;
        }

        if (State == MascotState.Thinking && !reduce)
        {
            // Starts at phase 0 on entry and rides on top of the tip spring, so entering is continuous.
            driveTip = MascotStates.ThinkAmplitude * Math.Sin(2 * Math.PI * (now - enteredAt) / MascotStates.ThinkPeriodSeconds);
            return true;
        }

        return !AtRest;
    }

    private void Fire(Beat beat)
    {
        switch (beat)
        {
            case Beat.Rest:
                ScheduleSway();
                break;
            case Beat.SwayLeft:
                To(rootRotate, -MascotStates.SwayAngle, SpringPreset.Gentle);
                At(MascotStates.SwayStepSeconds, Beat.SwayRight);
                break;
            case Beat.SwayRight:
                To(rootRotate, MascotStates.SwayAngle, SpringPreset.Gentle);
                At(MascotStates.SwayStepSeconds, Beat.SwayBack);
                break;
            case Beat.SwayBack:
                To(rootRotate, 0, SpringPreset.Gentle);
                ScheduleSway();
                break;
            case Beat.Nod:
                To(rootRotate, MascotStates.NodAngle, SpringPreset.Snappy);
                At(MascotStates.NodHoldSeconds, Beat.NodBack);
                break;
            case Beat.NodBack:
                To(rootRotate, 0, SpringPreset.Snappy);
                At(MascotStates.NodSettleSeconds, Beat.NodDone);
                break;
            case Beat.NodDone:
                // After a drop it rests and sways again; after a result it stays still.
                ScheduleSway();
                break;
        }
    }

    private void Drop()
    {
        Set(MascotState.Compact);
        next = Beat.None;
        if (!reduce)
        {
            At(0, Beat.Nod);
        }
    }

    private void ScheduleSway()
    {
        if (State == MascotState.Compact && !reduce)
        {
            At(MascotStates.SwayPauseMinSeconds + Random.Shared.NextDouble() * (MascotStates.SwayPauseMaxSeconds - MascotStates.SwayPauseMinSeconds), Beat.SwayLeft);
        }
    }

    private void At(double seconds, Beat beat) => (next, nextAt) = (beat, clock() + seconds);

    /// <summary>Hand the sine or mic drive over to the springs so leaving a driven state starts where the pose is.</summary>
    private void Fold()
    {
        if (driveTip == 0 && driveScaleX == 0 && driveScaleY == 0)
        {
            return;
        }

        tipRotate.Jump(TipRotate);
        scaleX.Jump(scaleX.Value + driveScaleX);
        scaleY.Jump(scaleY.Value + driveScaleY);
        (driveTip, driveScaleX, driveScaleY, smoothed) = (0, 0, 0, 0);
    }

    private void To(Spring spring, double target, SpringPreset preset)
    {
        spring.Use(preset);
        if (reduce)
        {
            spring.Jump(target);
        }
        else
        {
            spring.Target = target;
        }
    }
}
