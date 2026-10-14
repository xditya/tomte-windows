using System.Drawing;
using System.Globalization;
using System.Numerics;
using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>
/// The palette's content in DIPs from its top-left corner: the prompt box after the tomte, the chips under it in as
/// many rows as they need, the answer, and the footer along the bottom. The palette is as tall as that needs, from
/// 150 to 480 DIPs; an answer longer than the room left is cut at the last line that fits, with an ellipsis.
/// <paramref name="text"/> is the prompt's own height (the text box's content height), which the box grows with.
/// </summary>
public sealed class PaletteLayout(float width, bool mascot, float text = 0, IReadOnlyList<(string Label, bool On)>? chips = null)
{
    public const float Pad = 14, Top = 12, Bottom = 10, Gap = 10, BlockGap = 6;
    public const float PromptHeight = 36, PromptInsetX = 12, PromptInsetY = 9, PromptCorner = 8;

    /// <summary>The prompt box grows with its text up to this, then its text scrolls: about four lines.</summary>
    public const float PromptMaxText = 72;

    public const float ChipHeight = 24, ChipPadX = 9, ChipGap = 6, ChipCorner = 12, ChipMaxText = 200;
    public const float FootHeight = 14;

    /// <summary>The prompt's own text: 13 DIP white, as the text box draws it.</summary>
    public const float PromptSize = 13;

    public static readonly TextStyle Placeholder = new(PromptSize, Color: TextStyle.TertiaryColor);
    public static readonly TextStyle AnswerText = new(13, Color: 0xFFE9E9EE, LineHeight: 20);
    public static readonly TextStyle StepText = new(12, Color: TextStyle.SecondaryColor, LineHeight: 18);
    public static readonly TextStyle ErrorText = AnswerText with { Color = TextStyle.WarnColor };
    public static readonly TextStyle Chip = new(12, Color: TextStyle.SecondaryColor);
    public static readonly TextStyle ChipOn = Chip with { Color = TextStyle.WhiteColor };
    public static readonly TextStyle Provider = TextStyle.Small with { Color = 0xFFBCD3FF };
    public static readonly TextStyle Spend = TextStyle.Small with { Align = TextAlign.Trailing };

    public const string Hint = "Ask about what is in front of you…";
    public const string Off = "The AI layer is off";
    public const string Setup = "Add a provider key to use the palette.";

    private IReadOnlyList<(RectangleF Box, TextSlot Label)>? row;

    public float Width => width;

    public float Left => mascot ? MascotArt.InsetX + MascotArt.SizeDips + 10 : Pad;

    /// <summary>The prompt's card: one line tall at least, growing with its text up to about four lines.</summary>
    public RectangleF Prompt => new(Left, Top, width - Pad - Left, Math.Clamp(text + 2 * PromptInsetY, PromptHeight, PromptMaxText + 2 * PromptInsetY));

    /// <summary>Where the prompt's text box sits inside the card.</summary>
    public RectangleF Editor
    {
        get
        {
            var card = Prompt;
            return new(card.X + PromptInsetX, card.Y + PromptInsetY, card.Width - 2 * PromptInsetX, card.Height - 2 * PromptInsetY);
        }
    }

    public float ChipsTop => Top + Prompt.Height + Gap;

    /// <summary>
    /// One pill per chip, each 9 DIP wider than its label on both sides and 6 apart, in rows: a chip that does not fit
    /// beside the one before starts the next row. A label is cut with an ellipsis at 200 DIP, or at the row's width.
    /// </summary>
    public IReadOnlyList<(RectangleF Box, TextSlot Label)> Chips => row ??= LayChips();

    /// <summary>The chip under a point, or -1.</summary>
    public int ChipAt(Vector2 point)
    {
        for (int i = 0; i < Chips.Count; i++)
        {
            if (Chips[i].Box.Contains(point.X, point.Y))
            {
                return i;
            }
        }

        return -1;
    }

    public float AnswerTop => (Chips.Count > 0 ? Chips[^1].Box.Bottom : ChipsTop - Gap) + Gap;

    public float AnswerWidth => width - 2 * Pad;

    /// <summary>The palette's height for an answer this tall.</summary>
    public float Height(float answer) =>
        (float)Math.Clamp(AnswerTop + (answer > 0 ? answer + Gap : 0) + FootHeight + Bottom, PanelSize.PaletteMinHeight, PanelSize.PaletteMaxHeight);

    /// <summary>The footer: where the request went or is going on the left, the steps and the spend on the right.</summary>
    public IReadOnlyList<TextSlot> Foot(string provider, int steps, decimal spent, decimal? cap, float height)
    {
        var foot = new RectangleF(Pad, height - Bottom - FootHeight, width - 2 * Pad, FootHeight);
        float half = foot.Width / 2;
        return
        [
            new(provider, Provider, new RectangleF(foot.X, foot.Y, half + 40, FootHeight)),
            new(SpendLine(steps, spent, cap), Spend, new RectangleF(foot.X + half + 50, foot.Y, half - 50, FootHeight)),
        ];
    }

    public static string SpendLine(int steps, decimal spent, decimal? cap) =>
        string.Create(CultureInfo.InvariantCulture, $"{steps} tool step{(steps == 1 ? "" : "s")} · ${spent:0.00} today{(cap is { } c ? $" of ${c:0.00} cap" : "")}");

    /// <summary>How tall the answer's blocks are laid out at the answer's width, with the gaps between them.</summary>
    public float Measure(IReadOnlyList<(string Text, TextStyle Style)> blocks)
    {
        float total = 0;
        int shown = 0;
        foreach (var (block, style) in blocks)
        {
            if (block.Length == 0)
            {
                continue;
            }

            using var layout = TextLayout.Of(new TextRun(block, style, AnswerWidth, Lines: int.MaxValue));
            total += layout.Height + (shown++ > 0 ? BlockGap : 0);
        }

        return total;
    }

    /// <summary>
    /// The answer's blocks as runs and where they start, in a palette of <paramref name="height"/>: whole while they
    /// fit, then the block that does not is cut at its last line that does, and the rest are left out.
    /// </summary>
    public IReadOnlyList<(TextRun Run, Vector2 At)> Answer(IReadOnlyList<(string Text, TextStyle Style)> blocks, float height)
    {
        float bottom = height - Bottom - FootHeight - Gap, y = AnswerTop;
        var runs = new List<(TextRun, Vector2)>();
        foreach (var (block, style) in blocks)
        {
            if (block.Length == 0)
            {
                continue;
            }

            using var layout = TextLayout.Of(new TextRun(block, style, AnswerWidth, Lines: int.MaxValue));
            if (y + layout.Height <= bottom + 0.5f)
            {
                runs.Add((new TextRun(block, style, AnswerWidth, layout.LineCount), new Vector2(Pad, y)));
                y += layout.Height + BlockGap;
                continue;
            }

            int lines = (int)((bottom - y) / LineHeight(style));
            if (lines >= 1)
            {
                runs.Add((new TextRun(block, style, AnswerWidth, lines), new Vector2(Pad, y)));
            }

            break;
        }

        return runs;
    }

    private static float LineHeight(TextStyle style) => style.LineHeight > 0 ? style.LineHeight : style.Size * 4 / 3;

    private List<(RectangleF, TextSlot)> LayChips()
    {
        float x = Pad, y = ChipsTop, right = width - Pad;
        var laid = new List<(RectangleF, TextSlot)>();
        foreach (var (label, on) in chips ?? [])
        {
            var style = on ? ChipOn : Chip;
            float w = Math.Min(TextSlot.Measure(label, style, ChipMaxText) + 2 * ChipPadX, right - Pad);
            if (x > Pad && x + w > right)
            {
                (x, y) = (Pad, y + ChipHeight + ChipGap);
            }

            laid.Add((new RectangleF(x, y, w, ChipHeight), new TextSlot(label, style, new RectangleF(x + ChipPadX, y, w - 2 * ChipPadX, ChipHeight))));
            x += w + ChipGap;
        }

        return laid;
    }
}
