using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Graphics.DirectWrite;

namespace Tomte.Core.Text;

/// <summary>Where a run sits inside its width.</summary>
public enum TextAlign
{
    Leading,
    Center,
    Trailing,
}

/// <summary>
/// How a run looks: size in DIPs, weight (400 regular, 500 medium, 600 semibold), colour as 0xAARRGGBB,
/// where it sits inside its width, and tabular figures for clocks, so the digits do not shift as they change.
/// </summary>
public sealed record TextStyle(float Size, int Weight = 400, uint Color = TextStyle.White, TextAlign Align = TextAlign.Leading, bool Tabular = false, string Family = TextStyle.Ui)
{
    public const string Ui = "Segoe UI Variable Text";
    public const uint White = 0xFFFFFFFF;

    /// <summary>Secondary text, 60% white.</summary>
    public const uint Secondary = 0x99FFFFFF;

    /// <summary>Tertiary text, 38% white.</summary>
    public const uint Tertiary = 0x61FFFFFF;

    /// <summary>Something did not work.</summary>
    public const uint Warn = 0xFFF0B35A;
}

/// <summary>
/// One run of text: the string, how it looks, the widest it may be, and how many lines it may take. One line
/// ends in an ellipsis where it does not fit; more lines wrap at word breaks and the last one shown ends in an
/// ellipsis when the rest does not fit.
/// </summary>
public sealed record TextRun(string Text, TextStyle Style, float MaxWidth, int Lines = 1);

/// <summary>
/// A run laid out by DirectWrite in DIPs: wrapped and trimmed to its width and line count, measured, and ready
/// for Direct2D to draw. The layout does not depend on the display scale, so a DPI change only redraws it.
/// Use it on the thread that made it.
/// </summary>
public sealed class TextLayout : IDisposable
{
    private IDWriteTextLayout? layout;
    private nint native;

    private TextLayout(TextRun run)
    {
        Run = run;
        var (format, ellipsis) = Typesetter.Format(run.Style);
        float maxWidth = Math.Max(0, run.MaxWidth);
        int lines = Math.Max(1, run.Lines);
        // DirectWrite breaks at a line break even when it does not wrap, so a one-line run gets spaces instead.
        string text = lines == 1 ? OneLine(run.Text) : run.Text;
        Typesetter.Factory.CreateTextLayout(text, (uint)text.Length, format, maxWidth, Unbounded, out var made);
        layout = made;
        try
        {
            made.SetTextAlignment(run.Style.Align switch
            {
                TextAlign.Center => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_CENTER,
                TextAlign.Trailing => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_TRAILING,
                _ => DWRITE_TEXT_ALIGNMENT.DWRITE_TEXT_ALIGNMENT_LEADING,
            });
            if (run.Style.Tabular)
            {
                made.SetTypography(Typesetter.Tabular, new DWRITE_TEXT_RANGE { startPosition = 0, length = (uint)text.Length });
            }

            made.SetWordWrapping(lines == 1 ? DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_NO_WRAP : DWRITE_WORD_WRAPPING.DWRITE_WORD_WRAPPING_WRAP);

            // Laid out once untrimmed to see whether it fits; only a run that does not is cut.
            var natural = Metrics(made);
            Trimmed = lines == 1 ? natural.width > maxWidth + Slack : natural.lineCount > lines || natural.width > maxWidth + Slack;
            if (Trimmed)
            {
                if (natural.lineCount > lines)
                {
                    // The box ends under the last line allowed; DirectWrite drops the lines below it and puts the
                    // ellipsis on that one.
                    made.SetMaxHeight(HeightOf(made, natural.lineCount, lines) + Slack);
                }

                made.SetTrimming(new DWRITE_TRIMMING { granularity = DWRITE_TRIMMING_GRANULARITY.DWRITE_TRIMMING_GRANULARITY_CHARACTER }, ellipsis);
            }

            // After a cut DirectWrite still counts the lines it dropped; the height is that of the lines kept.
            var final = Metrics(made);
            (Width, Height, LineCount) = (final.width, final.height, Math.Min((int)final.lineCount, lines));
            native = Marshal.GetComInterfaceForObject<IDWriteTextLayout, IDWriteTextLayout>(made);
        }
        catch
        {
            Dispose();
            throw;
        }
    }

    // Big enough for any island; DirectWrite takes a finite height.
    private const float Unbounded = 1 << 20;

    // DirectWrite's widths are sums of floats; a run that fits exactly must not count as cut.
    private const float Slack = 0.01f;

    public TextRun Run { get; }

    /// <summary>The widest line as drawn, ellipsis included, without trailing spaces. DIPs.</summary>
    public float Width { get; }

    /// <summary>The lines shown, top of the first to the bottom of the last. DIPs.</summary>
    public float Height { get; }

    public int LineCount { get; }

    /// <summary>The run did not fit its width or its line count and was cut with an ellipsis.</summary>
    public bool Trimmed { get; }

    /// <summary>The layout as an IDWriteTextLayout pointer for Direct2D's DrawTextLayout, valid until Dispose.</summary>
    public nint Native => native;

    public static TextLayout Of(TextRun run) => new(run);

    public void Dispose()
    {
        if (native != 0)
        {
            Marshal.Release(native);
            native = 0;
        }

        if (layout is not null)
        {
            Marshal.FinalReleaseComObject(layout);
            layout = null;
        }
    }

    private static readonly char[] Breaks = [(char)0x0D, (char)0x0A, (char)0x85, (char)0x2028, (char)0x2029];

    private static string OneLine(string text) =>
        text.IndexOfAny(Breaks) < 0 ? text : string.Join((char)0x20, text.Split(Breaks, StringSplitOptions.RemoveEmptyEntries));

    private static unsafe DWRITE_TEXT_METRICS Metrics(IDWriteTextLayout layout)
    {
        DWRITE_TEXT_METRICS metrics;
        layout.GetMetrics(&metrics);
        return metrics;
    }

    private static unsafe float HeightOf(IDWriteTextLayout layout, uint count, int lines)
    {
        var metrics = new DWRITE_LINE_METRICS[count];
        float height = 0;
        fixed (DWRITE_LINE_METRICS* all = metrics)
        {
            layout.GetLineMetrics(all, count, out _);
        }

        for (int i = 0; i < lines; i++)
        {
            height += metrics[i].height;
        }

        return height;
    }

    /// <summary>
    /// The DirectWrite factory and one text format per family, size and weight, for the life of the process:
    /// they are small, and every show would otherwise make them again.
    /// </summary>
    private static class Typesetter
    {
        private static readonly Lock Gate = new();
        private static readonly Dictionary<(string Family, float Size, int Weight), (IDWriteTextFormat Format, IDWriteInlineObject Ellipsis)> Formats = [];
        private static IDWriteFactory? factory;
        private static IDWriteTypography? tabular;

        public static IDWriteFactory Factory
        {
            get
            {
                lock (Gate)
                {
                    if (factory is null)
                    {
                        PInvoke.DWriteCreateFactory(DWRITE_FACTORY_TYPE.DWRITE_FACTORY_TYPE_SHARED, out IDWriteFactory made).ThrowOnFailure();
                        factory = made;
                    }

                    return factory;
                }
            }
        }

        public static IDWriteTypography Tabular
        {
            get
            {
                lock (Gate)
                {
                    if (tabular is null)
                    {
                        Factory.CreateTypography(out var made);
                        made.AddFontFeature(new DWRITE_FONT_FEATURE { nameTag = DWRITE_FONT_FEATURE_TAG.DWRITE_FONT_FEATURE_TAG_TABULAR_FIGURES, parameter = 1 });
                        tabular = made;
                    }

                    return tabular;
                }
            }
        }

        public static (IDWriteTextFormat Format, IDWriteInlineObject Ellipsis) Format(TextStyle style)
        {
            var key = (style.Family, style.Size, style.Weight);
            lock (Gate)
            {
                if (!Formats.TryGetValue(key, out var made))
                {
                    // A family that is not installed falls back to the system font, character by character.
                    Factory.CreateTextFormat(style.Family, null, (DWRITE_FONT_WEIGHT)style.Weight, DWRITE_FONT_STYLE.DWRITE_FONT_STYLE_NORMAL, DWRITE_FONT_STRETCH.DWRITE_FONT_STRETCH_NORMAL, style.Size, "en-us", out var format);
                    Factory.CreateEllipsisTrimmingSign(format, out var ellipsis);
                    made = (format, ellipsis);
                    Formats.Add(key, made);
                }

                return made;
            }
        }
    }
}
