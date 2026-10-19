namespace Tomte.Core.Rambler;

/// <summary>
/// The rambler's seven 3 px bars. Heights follow the island design: 4 DIP at rest, up to
/// 4 + 18 x weight, weights .35/.62/.85/1 mirrored, and each bar breathing on its own staggered
/// period (0.9, 1.07, 1.24 s, 50 ms apart) so equal levels never look like one block.
/// The microphone level scales the whole envelope. Once the key is up, <see cref="Take"/> draws the whole take for the
/// card the words show on.
/// </summary>
public static class Waveform
{
    public const int Bars = 7;
    public const float BarWidth = 3;
    public const float Gap = 2;
    public const float MinHeight = 4;
    public const float Swing = 18;

    // Calibration: -50 dBFS (a quiet room) draws flat bars, -12 dBFS (speech close to a laptop mic) fills them.
    public const double FloorDb = -50;
    public const double CeilingDb = -12;

    private static readonly float[] Weights = [.35f, .62f, .85f, 1f, .85f, .62f, .35f];
    private static readonly double[] Periods = [.9, 1.07, 1.24, .9, 1.07, 1.24, .9];

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

    /// <summary>The take on the card: bars this wide and this far apart, this tall at most and at least, about this many a second.</summary>
    public const float TakeBar = 3, TakeGap = 3, TakeHeight = 34, TakeMin = 4, TakePerSecond = 6.5f;

    /// <summary>A bar quieter than this level counts as silence when it ends the take.</summary>
    public const double SilenceLevel = 0.08;

    /// <summary>How many bars a take this long gets in a band this wide: about six and a half a second, eight at least, never more than fit.</summary>
    public static int TakeBars(double seconds, float width) =>
        Math.Max(1, Math.Min((int)((width + TakeGap) / (TakeBar + TakeGap)), Math.Max(8, (int)Math.Round(seconds * TakePerSecond))));

    /// <summary>
    /// The take as <paramref name="bars"/> bars: each the loudness of its stretch of the samples on the same decibel scale
    /// as the live bars, from <see cref="TakeMin"/> to <see cref="TakeHeight"/>; the quiet bars that end the take are
    /// silence, drawn flat and dim.
    /// </summary>
    public static (float Height, bool Silent)[] Take(ReadOnlySpan<float> samples, int bars)
    {
        var take = new (float Height, bool Silent)[bars];
        var levels = new double[bars];
        for (int i = 0; i < bars; i++)
        {
            int from = (int)((long)samples.Length * i / bars), to = (int)((long)samples.Length * (i + 1) / bars);
            levels[i] = Level(Rms(samples[from..to]));
            take[i] = (TakeMin + (float)((TakeHeight - TakeMin) * levels[i]), false);
        }

        for (int i = bars - 1; i >= 0 && levels[i] < SilenceLevel; i--)
        {
            take[i] = (TakeMin, true);
        }

        return take;
    }

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
