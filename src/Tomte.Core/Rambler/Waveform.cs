namespace Tomte.Core.Rambler;

/// <summary>
/// The rambler's nine 3 DIP bars, 2 apart. Heights: 4 DIP at rest, up to 4 + 16 x weight (20 at most), weights
/// .35/.55/.75/.9/1 mirrored, and each bar breathing on its own staggered period (0.9, 1.07, 1.24 s, 50 ms apart) so
/// equal levels never look like one block. The microphone level scales the whole envelope. The bars fill in from the
/// left, one a second, so a short hold shows how short it is.
/// </summary>
public static class Waveform
{
    public const int Bars = 9;
    public const float BarWidth = 3;
    public const float Gap = 2;
    public const float MinHeight = 4;
    public const float Swing = 16;

    // Calibration: -50 dBFS (a quiet room) draws flat bars, -12 dBFS (speech close to a laptop mic) fills them.
    public const double FloorDb = -50;
    public const double CeilingDb = -12;

    /// <summary>One more bar is reached each this many seconds of the hold.</summary>
    public const double StepSeconds = 1;

    private static readonly float[] Weights = [.35f, .55f, .75f, .9f, 1f, .9f, .75f, .55f, .35f];
    private static readonly double[] Periods = [.9, 1.07, 1.24, .9, 1.07, 1.24, .9, 1.07, 1.24];

    /// <summary>How many bars, from the left, the hold has reached after <paramref name="seconds"/>: one at once, the n-th in its n-th second, all nine past 8 s.</summary>
    public static int Reached(double seconds) => Math.Clamp((int)Math.Ceiling(Math.Max(0, seconds) / StepSeconds), 1, Bars);

    public static float Rms(ReadOnlySpan<float> samples)
    {
        if (samples.IsEmpty)
            return 0;
        double sum = 0;
        foreach (var s in samples)
            sum += s * s;
        return (float)Math.Sqrt(sum / samples.Length);
    }

    /// <summary>RMS (full scale 1) to 0..1 on a decibel scale between <see cref="FloorDb"/> and <see cref="CeilingDb"/>.</summary>
    public static double Level(float rms) =>
        rms <= 0 ? 0 : Math.Clamp((20 * Math.Log10(rms) - FloorDb) / (CeilingDb - FloorDb), 0, 1);

    /// <summary>Bar heights in DIP for a level at time <paramref name="seconds"/>.</summary>
    public static void Heights(double level, double seconds, Span<float> heights)
    {
        for (int i = 0; i < Bars; i++)
        {
            double phase = (seconds - 0.05 * i) / Periods[i];
            double breath = 0.55 + 0.45 * (0.5 - 0.5 * Math.Cos(2 * Math.PI * phase));
            heights[i] = MinHeight + (float)(Swing * Weights[i] * level * breath);
        }
    }
}
