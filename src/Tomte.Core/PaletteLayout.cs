using System.Drawing;
using System.Globalization;
using System.Numerics;
using Tomte.Core.Device;
using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>The buttons of a tool's question, left to right.</summary>
public enum ConfirmButton { AlwaysAllow, Cancel, Run }

/// <summary>
/// A tool's question as the palette draws it: what will run, why it asks and which keys answer, whether "Always
/// allow" is offered, and whether Run takes the warning colour because the action can never be always allowed.
/// </summary>
public sealed record Confirmation(string Summary, string Line, bool AlwaysAllow, bool Destructive)
{
    /// <summary>Why a tool asks, in plain words, then the keys: Enter in the typed palette, Win+Alt+Enter by voice, where the island has no keys.</summary>
    public static string LineFor(ActionTier tier, bool typed) =>
        (tier switch
        {
            ActionTier.Read => "Sends what it reads to the AI",
            ActionTier.AppLocalWrite => "Changes Tomte's own data",
            ActionTier.ExternalEffect => "Reaches outside Tomte",
            _ => "Asks every time",
        }) + (typed ? " · Enter runs it, Esc cancels" : " · Win+Alt+Enter runs it, Esc cancels");
}

/// <summary>
/// The palette's content in DIPs from its top-left corner: the prompt box after the tomte, the chips under it in as
/// many rows as they need, the answer, a tool's question while one is open, and the footer along the bottom. The
/// palette is as tall as that needs, from 150 to 480 DIPs; an answer longer than the room left scrolls in its band.
/// <paramref name="text"/> is the prompt's own height (the text box's content height), which the box grows with.
/// </summary>
public sealed class PaletteLayout(float width, bool mascot, float text = 0, IReadOnlyList<(string Label, bool On)>? chips = null, Confirmation? confirm = null)
{
    public const float Pad = 14, Top = 12, Bottom = 10, Gap = 10, BlockGap = 6;
    public const float PromptHeight = 36, PromptInsetX = 12, PromptInsetY = 9, PromptCorner = 8;

    /// <summary>The prompt box grows with its text up to this, then its text scrolls: about four lines.</summary>
    public const float PromptMaxText = 72;

    public const float ChipHeight = 24, ChipPadX = 9, ChipGap = 6, ChipCorner = 12, ChipMaxText = 200;
    public const float FootHeight = 14;

    /// <summary>A tool's question: a card above the footer, its summary on up to three lines, then why it asks and the buttons.</summary>
    public const float AskPad = 10, AskGap = 6, ButtonHeight = 28, ButtonPadX = 14, ButtonGap = 6, ButtonCorner = 8, ButtonMinWidth = 64;

    public const int AskLines = 3, PromptLines = 4;

    /// <summary>A wheel notch scrolls the answer three of its lines, as Windows scrolls by default.</summary>
    public const float WheelStep = 3 * 20;

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

    /// <summary>What was said, where a palette with no text box (the voice palette) shows its prompt.</summary>
    public static readonly TextStyle PromptText = new(PromptSize);

    public static readonly TextStyle AskSummary = AnswerText with { Color = TextStyle.WhiteColor };
    public static readonly TextStyle AskLine = new(11, Color: TextStyle.SecondaryColor);
    public static readonly TextStyle Button = new(12, 500, Align: TextAlign.Center);
    public static readonly TextStyle RunButton = Button with { Weight = 600, Color = 0xFF000000 };

    public const string Hint = "Ask about what is in front of you…";
    public const string Off = "The AI layer is off";
    public const string Setup = "Add a provider key to use the palette.";
    public const string Run = "Run", Cancel = "Cancel", AlwaysAllow = "Always allow";

    private IReadOnlyList<(RectangleF Box, TextSlot Label)>? row;
    private float? askHeight;

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
        (float)Math.Clamp(AnswerTop + (answer > 0 ? answer + Gap : 0) + AskRoom + FootHeight + Bottom, PanelSize.PaletteMinHeight, PanelSize.PaletteMaxHeight);

    /// <summary>
    /// The open question's card in a palette of <paramref name="height"/>, above the footer: the summary from its top
    /// left, then a row with why it asks on the left and the buttons on the right, Run last. Null with no question.
    /// </summary>
    public (RectangleF Card, TextRun Summary, Vector2 SummaryAt, TextSlot Line, IReadOnlyList<(ConfirmButton Button, RectangleF Box, TextSlot Label)> Buttons)? Ask(float height)
    {
        if (confirm is null)
        {
            return null;
        }

        var card = new RectangleF(Pad, height - Bottom - FootHeight - Gap - AskHeight, AnswerWidth, AskHeight);
        float inner = card.Width - 2 * AskPad, rowY = card.Bottom - AskPad - ButtonHeight, right = card.Right - AskPad;
        var buttons = new List<(ConfirmButton, RectangleF, TextSlot)>();
        foreach (var (button, label, style) in new[] { (ConfirmButton.Run, Run, RunButton), (ConfirmButton.Cancel, Cancel, Button), (ConfirmButton.AlwaysAllow, AlwaysAllow, Button) })
        {
            if (button == ConfirmButton.AlwaysAllow && !confirm.AlwaysAllow)
            {
                continue;
            }

            float w = Math.Max(ButtonMinWidth, TextSlot.Measure(label, style, inner) + 2 * ButtonPadX);
            var box = new RectangleF(right - w, rowY, w, ButtonHeight);
            buttons.Insert(0, (button, box, new TextSlot(label, style, box)));
            right = box.X - ButtonGap;
        }

        float left = card.X + AskPad;
        return (card, new TextRun(confirm.Summary, AskSummary, inner, AskLines), new Vector2(left, card.Y + AskPad),
            new TextSlot(confirm.Line, AskLine, new RectangleF(left, rowY, Math.Max(0, right - left), ButtonHeight)), buttons);
    }

    /// <summary>The question's button under a point, or null.</summary>
    public ConfirmButton? ButtonAt(Vector2 point, float height)
    {
        foreach (var (button, box, _) in Ask(height)?.Buttons ?? [])
        {
            if (box.Contains(point.X, point.Y))
            {
                return button;
            }
        }

        return null;
    }

    // The question's card: padding, the summary's lines, the gap and the row of buttons; 0 with none.
    private float AskHeight => askHeight ??= confirm is null ? 0 : AskPad + SummaryHeight(confirm.Summary) + AskGap + ButtonHeight + AskPad;

    private float AskRoom => AskHeight > 0 ? AskHeight + Gap : 0;

    private float SummaryHeight(string summary)
    {
        using var laid = TextLayout.Of(new TextRun(summary, AskSummary, AnswerWidth - 2 * AskPad, AskLines));
        return laid.Height;
    }

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

    /// <summary>
    /// The answer's blocks laid out whole, one under the other, each where it starts from the top of the answer, and the
    /// height of them all. Empty blocks take no room.
    /// </summary>
    public (IReadOnlyList<(TextRun Run, Vector2 At)> Runs, float Height) Answer(IReadOnlyList<(string Text, TextStyle Style)> blocks)
    {
        float y = 0;
        var runs = new List<(TextRun, Vector2)>();
        foreach (var (block, style) in blocks)
        {
            if (block.Length == 0)
            {
                continue;
            }

            using var layout = TextLayout.Of(new TextRun(block, style, AnswerWidth, Lines: int.MaxValue));
            y += runs.Count > 0 ? BlockGap : 0;
            runs.Add((new TextRun(block, style, AnswerWidth, layout.LineCount), new Vector2(Pad, y)));
            y += layout.Height;
        }

        return (runs, y);
    }

    /// <summary>Where the answer shows in a palette of <paramref name="height"/>: under the chips, above the question and the footer. It scrolls inside.</summary>
    public RectangleF AnswerBand(float height) => new(0, AnswerTop, width, Math.Max(0, height - Bottom - FootHeight - Gap - AskRoom - AnswerTop));

    public float MaxScroll(float answer, float height) => Math.Max(0, answer - AnswerBand(height).Height);

    /// <summary>A wheel delta (120 a notch, positive away from the user) moves the answer three lines a notch, back toward its start.</summary>
    public float Scrolled(float scroll, int delta, float answer, float height) => Math.Clamp(scroll - delta / 120f * WheelStep, 0, MaxScroll(answer, height));

    /// <summary>
    /// The scroll for an answer that changed: at its end while pinned there, so a streaming answer shows its newest
    /// line; else where the user left it, clamped to what the answer now allows.
    /// </summary>
    public float Follow(float scroll, bool pinned, float answer, float height) => pinned ? MaxScroll(answer, height) : Math.Clamp(scroll, 0, MaxScroll(answer, height));

    /// <summary>Pinned to the end: scrolled to the end, or to where the end shows anyway.</summary>
    public bool AtEnd(float scroll, float answer, float height) => scroll >= MaxScroll(answer, height) - 0.5f;

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
