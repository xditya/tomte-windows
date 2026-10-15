using System.Drawing;
using System.Globalization;
using System.Numerics;
using Tomte.Core.Device;
using Tomte.Core.Interaction;
using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>The buttons of a tool's question, left to right.</summary>
public enum ConfirmButton { AlwaysAllow, Cancel, Run }

/// <summary>
/// Which keys answer a question: Enter while the prompt box has them, the Win+Alt+Enter chord by voice, or none but Esc
/// once the typed palette has lost them.
/// </summary>
public enum ConfirmKeys { Prompt, Voice, Pointer }

/// <summary>
/// A tool's question as the palette draws it: what will run, why it asks and which keys answer, whether "Always
/// allow" is offered, and whether Run takes the warning colour because the action can never be always allowed.
/// </summary>
public sealed record Confirmation(string Summary, string Line, bool AlwaysAllow, bool Destructive)
{
    /// <summary>Why a tool asks, in plain words, then the keys that answer it.</summary>
    public static string LineFor(ActionTier tier, ConfirmKeys keys) =>
        (tier switch
        {
            ActionTier.Read => "Sends what it reads to the AI",
            ActionTier.AppLocalWrite => "Changes Tomte's own data",
            ActionTier.ExternalEffect => "Reaches outside Tomte",
            _ => "Asks every time",
        }) + keys switch
        {
            ConfirmKeys.Prompt => " · Enter runs it, Esc cancels",
            ConfirmKeys.Voice => " · Win+Alt+Enter runs it, Esc cancels",
            _ => " · Esc cancels",
        };
}

/// <summary>The picture before a chip's name.</summary>
public enum ChipIcon { Selection, Window, Screenshot, Shelf, Note, Region }

/// <summary>A chip to draw: its short name, its picture and whether it is on.</summary>
public readonly record struct ChipSpec(string Name, ChipIcon Icon, bool On);

/// <summary>
/// A chip laid out: its pill, which is also where it takes clicks, the picture at its left, its name, and the mark at its
/// right, a "+" while off and a tick while on.
/// </summary>
public readonly record struct LaidChip(RectangleF Box, RectangleF Icon, TextSlot Label, RectangleF Mark, bool On);

/// <summary>
/// The palette's content in DIPs from its top-left corner: the prompt box after the tomte, the chips under it in as
/// many rows as they need, the answer, a tool's question while one is open, and the footer along the bottom. The
/// palette is as tall as that needs, from 150 to 480 DIPs; an answer longer than the room left scrolls in its band.
/// <paramref name="text"/> is the prompt's own height (the text box's content height), which the box grows with.
/// <paramref name="send"/> puts a Send button at the box's right end, for a palette with a text box. <paramref name="sends"/>
/// is the line under the chips saying what the next send carries; empty for none. <paramref name="back"/> puts the back
/// arrow before the prompt box, for a palette opened from the panels.
/// </summary>
public sealed class PaletteLayout(float width, bool mascot, float text = 0, IReadOnlyList<ChipSpec>? chips = null, Confirmation? confirm = null, bool send = false, string sends = "", bool back = false)
{
    public const float Pad = 14, Top = 12, Bottom = 10, Gap = 10, BlockGap = 6;
    public const float PromptHeight = 36, PromptInsetX = 12, PromptInsetY = 9, PromptCorner = 8;

    /// <summary>The prompt box grows with its text up to this, then its text scrolls: about four lines.</summary>
    public const float PromptMaxText = 72;

    /// <summary>
    /// A chip is as tall as its click area, so all of it is drawn: the picture, a gap, the name, a gap and the mark
    /// between the side paddings. "Include" stands before the first row, and the rows after it start under the first chip.
    /// </summary>
    public const float ChipHeight = StateLook.ChipHeight, ChipPadX = 10, ChipGap = 6, ChipCorner = ChipHeight / 2, ChipMaxText = 160;
    public const float IconSize = 16, IconGap = 6, MarkSize = 10, MarkGap = 6, IncludeGap = 8, SendsHeight = 16;

    /// <summary>Send sits inside the prompt's card at its right end, level with the last line, its corner inset with the card's.</summary>
    public const float SendSize = 28, SendInset = 4, SendGap = 6, SendCorner = PromptCorner - SendInset;

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
    public static readonly TextStyle ChipOn = Chip with { Color = StateLook.CheckedText };
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
    public const string Include = "Include", SendTip = "Send (Enter)";

    /// <summary>The foot's provider line while a card asks before a tool runs: nothing is on its way until the user answers.</summary>
    public const string Waiting = "Waiting for you";

    /// <summary>
    /// An off chip: a faint fill, an outline at about 4.5:1 against the palette, and grey ink for its picture and "+".
    /// An on chip takes the accent fill with black ink. Glyph ink is opaque, so a glyph fades as one.
    /// </summary>
    public const uint ChipFill = 0x0FFFFFFF, ChipOutline = 0x73FFFFFF, ChipInk = 0xFFA3A3A5;

    /// <summary>
    /// The path a chip's outline follows and the stroke's width in DIPs: one pixel wide, half a pixel inside the pill's
    /// edges put on whole pixels, so it lights whole pixels at any scale; two half-lit ones would fall under 3:1.
    /// <paramref name="parent"/> is where the pill's space starts, in DIPs from a whole pixel.
    /// </summary>
    public static (RectangleF Path, float Corner, float Stroke) ChipStroke(RectangleF pill, Vector2 parent, double scale)
    {
        float half = (float)(0.5 / scale);
        float left = PixelGrid.Snap(parent.X, pill.Left, scale), top = PixelGrid.Snap(parent.Y, pill.Top, scale);
        float right = PixelGrid.Snap(parent.X, pill.Right, scale), bottom = PixelGrid.Snap(parent.Y, pill.Bottom, scale);
        return (RectangleF.FromLTRB(left + half, top + half, right - half, bottom - half), ChipCorner - half, 2 * half);
    }

    /// <summary>The prompt card's outline, which takes the accent while the box has the keys.</summary>
    public const uint PromptLine = 0x1AFFFFFF, PromptFocusLine = 0x996FA8FF;

    /// <summary>Send: a white button with a black arrow.</summary>
    public const uint SendFill = 0xFFFFFFFF, SendInk = 0xFF000000;

    /// <summary>
    /// The question's card carries the accent in its outline so it reads as waiting for the user. Its buttons are filled
    /// and outlined as the chips are; Run is white, or the warning colour for what is asked every time.
    /// </summary>
    public const uint AskOutline = 0x666FA8FF, ButtonFill = 0x1AFFFFFF, ButtonOutline = ChipOutline, RunFill = 0xFFFFFFFF, RunWarn = 0xFFF0B35A;

    private IReadOnlyList<LaidChip>? row;
    private float? askHeight, includeWidth;

    public float Width => width;

    /// <summary>The back arrow: as big as Send, level with the prompt's first line, after the tomte.</summary>
    public const float BackSize = SendSize, BackGap = 6, BackCorner = SendCorner;

    public const string BackId = "back", BackTip = "Back", BackKey = "Alt+Left";

    public float Left => Start + (back ? BackSize + BackGap : 0);

    /// <summary>The back arrow before the prompt box; empty without one.</summary>
    public RectangleF Back => back ? new(Start, Top + ((PromptHeight - BackSize) / 2), BackSize, BackSize) : RectangleF.Empty;

    private float Start => mascot ? MascotArt.InsetX + MascotArt.SizeDips + 10 : Pad;

    /// <summary>The prompt's card: one line tall at least, growing with its text up to about four lines.</summary>
    public RectangleF Prompt => new(Left, Top, width - Pad - Left, Math.Clamp(text + 2 * PromptInsetY, PromptHeight, PromptMaxText + 2 * PromptInsetY));

    /// <summary>Where the prompt's text box sits inside the card, short of Send when there is one.</summary>
    public RectangleF Editor
    {
        get
        {
            var card = Prompt;
            float right = send ? SendInset + SendSize + SendGap : PromptInsetX;
            return new(card.X + PromptInsetX, card.Y + PromptInsetY, card.Width - PromptInsetX - right, card.Height - 2 * PromptInsetY);
        }
    }

    /// <summary>The Send button at the card's right end, level with its last line; empty without one.</summary>
    public RectangleF Send
    {
        get
        {
            var card = Prompt;
            return send ? new(card.Right - SendInset - SendSize, card.Bottom - SendInset - SendSize, SendSize, SendSize) : RectangleF.Empty;
        }
    }

    /// <summary>Send works while the prompt has more than white space and nothing is being sent.</summary>
    public static bool CanSend(string prompt, bool sending) => !sending && !string.IsNullOrWhiteSpace(prompt);

    public float ChipsTop => Top + Prompt.Height + Gap;

    /// <summary>
    /// One pill per chip, 6 apart, in rows after "Include": a chip that does not fit beside the one before starts the
    /// next row, under the first chip. A name is cut with an ellipsis at 160 DIP, or at the row's width.
    /// </summary>
    public IReadOnlyList<LaidChip> Chips => row ??= LayChips();

    /// <summary>"Include" before the first row, as tall as a chip; empty with no chips.</summary>
    public TextSlot IncludeLabel => new(chips is { Count: > 0 } ? Include : "", Chip, new RectangleF(Pad, ChipsTop, IncludeWidth, ChipHeight));

    /// <summary>What the next send carries, on one line under the chips from where they start; empty while no chip is on.</summary>
    public TextSlot SendsLine
    {
        get
        {
            float x = Pad + IncludeWidth + IncludeGap;
            return new(sends, Chip, new RectangleF(x, ChipsBottom + ChipGap, width - Pad - x, sends.Length > 0 ? SendsHeight : 0));
        }
    }

    public float AnswerTop => (sends.Length > 0 ? SendsLine.Slot.Bottom : ChipsBottom) + Gap;

    private float ChipsBottom => Chips.Count > 0 ? Chips[^1].Box.Bottom : ChipsTop - Gap;

    public float AnswerWidth => width - 2 * Pad;

    /// <summary>The palette's height for an answer this tall.</summary>
    public float Height(float answer) =>
        (float)Math.Clamp(AnswerTop + (answer > 0 ? answer + Gap : 0) + AskRoom + FootHeight + Bottom, PanelSize.PaletteMinHeight, PanelSize.PaletteMaxHeight);

    /// <summary>The hint in an empty prompt box. With skill commands it names them: typing one is the only way to find them.</summary>
    public static string HintFor(IEnumerable<string> commands) =>
        commands.Any() ? $"Ask about what is in front of you, or type {string.Join(", ", commands.Select(c => "/" + c))}" : Hint;

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

    /// <summary>
    /// A question's button as a target: its kind and the number of the question it is for, so a press that began on
    /// one question's button answers nothing once the next question has opened.
    /// </summary>
    public static string ButtonId(ConfirmButton button, int question) => string.Create(CultureInfo.InvariantCulture, $"{button}:{question}");

    /// <summary>The key that answers a question as its button does, for the button's tooltip while the island has the keys.</summary>
    public static string ButtonKey(ConfirmButton button) => button switch
    {
        ConfirmButton.Run => "Enter",
        ConfirmButton.Cancel => "Esc",
        _ => "Alt+A",
    };

    /// <summary>The button an id names on question <paramref name="question"/>; null for another question's, or another target.</summary>
    public static ConfirmButton? ButtonFor(string id, int question) =>
        Enum.GetValues<ConfirmButton>().Cast<ConfirmButton?>().FirstOrDefault(b => ButtonId(b!.Value, question) == id);

    // The question's card: padding, the summary's lines, the gap and the row of buttons; 0 with none.
    private float AskHeight => askHeight ??= confirm is null ? 0 : AskPad + SummaryHeight(confirm.Summary) + AskGap + ButtonHeight + AskPad;

    private float AskRoom => AskHeight > 0 ? AskHeight + Gap : 0;

    private float SummaryHeight(string summary)
    {
        using var laid = TextLayout.Of(new TextRun(summary, AskSummary, AnswerWidth - 2 * AskPad, AskLines));
        return laid.Height;
    }

    /// <summary>The AI history button at the footer's left: a clock, as big as the smallest target, centred on the footer's line.</summary>
    public RectangleF History(float height) => new(Pad - 5, height - Bottom - (FootHeight / 2) - (StateLook.MinTarget / 2), StateLook.MinTarget, StateLook.MinTarget);

    public Target HistoryTarget(float height) => new(HistoryId, History(height), 6, TargetKind.Button, Tip: HistoryTip, Key: "Ctrl+H");

    public const string HistoryId = "history", HistoryTip = "AI history";

    /// <summary>The clock's ring radius and stroke, and its two hands as bars from the centre, in the button's own DIPs.</summary>
    public const float ClockRadius = 5.5f, ClockStroke = 1.5f;

    public static readonly RectangleF[] ClockHands = [new(11.25f, 8.5f, 1.5f, 4.25f), new(11.25f, 11.25f, 3.5f, 1.5f)];

    /// <summary>
    /// The line under the answer while the next question would go with the conversation's text: what that costs and what
    /// stays out. <paramref name="continuing"/> is the first question of a conversation from the history, before the
    /// first new answer.
    /// </summary>
    public static string ContinueLine(string? continuing) => continuing is null
        ? "A follow-up goes with this conversation's text, so it costs more. Earlier attachments are not included."
        : $"Continuing \"{Trim(continuing)}\". Your question goes with its text, so it costs more. Its attachments are not included.";

    private static string Trim(string text)
    {
        var line = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return line.Length <= 60 ? line : line[..59].TrimEnd() + "…";
    }

    /// <summary>The footer: where the request went or is going on the left after the history button, the steps and the spend on the right.</summary>
    public IReadOnlyList<TextSlot> Foot(string provider, int steps, decimal spent, decimal? cap, float height)
    {
        var foot = new RectangleF(Pad, height - Bottom - FootHeight, width - 2 * Pad, FootHeight);
        float half = foot.Width / 2, after = History(height).Right + 4 - foot.X;
        return
        [
            new(provider, Provider, new RectangleF(foot.X + after, foot.Y, half + 40 - after, FootHeight)),
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

    /// <summary>Page Down (+1) or Page Up (-1) moves the answer by its band less one line, which stays in view.</summary>
    public float Paged(float scroll, int direction, float answer, float height) =>
        Math.Clamp(scroll + (direction * Math.Max(WheelStep / 3, AnswerBand(height).Height - (WheelStep / 3))), 0, MaxScroll(answer, height));

    /// <summary>
    /// The scroll for an answer that changed: at its end while pinned there, so a streaming answer shows its newest
    /// line; else where the user left it, clamped to what the answer now allows.
    /// </summary>
    public float Follow(float scroll, bool pinned, float answer, float height) => pinned ? MaxScroll(answer, height) : Math.Clamp(scroll, 0, MaxScroll(answer, height));

    /// <summary>Pinned to the end: scrolled to the end, or to where the end shows anyway.</summary>
    public bool AtEnd(float scroll, float answer, float height) => scroll >= MaxScroll(answer, height) - 0.5f;

    private float IncludeWidth => includeWidth ??= TextSlot.Measure(Include, Chip, ChipMaxText);

    private List<LaidChip> LayChips()
    {
        const float Fixed = 2 * ChipPadX + IconSize + IconGap + MarkGap + MarkSize;
        float start = Pad + IncludeWidth + IncludeGap, x = start, y = ChipsTop, right = width - Pad;
        var laid = new List<LaidChip>();
        foreach (var (name, icon, on) in chips ?? [])
        {
            var style = on ? ChipOn : Chip;
            float w = Fixed + TextSlot.Measure(name, style, Math.Min(ChipMaxText, right - start - Fixed));
            if (x > start && x + w > right)
            {
                (x, y) = (start, y + ChipHeight + ChipGap);
            }

            float middle = y + ChipHeight / 2, labelX = x + ChipPadX + IconSize + IconGap;
            var mark = new RectangleF(x + w - ChipPadX - MarkSize, middle - MarkSize / 2, MarkSize, MarkSize);
            laid.Add(new(new RectangleF(x, y, w, ChipHeight), new RectangleF(x + ChipPadX, middle - IconSize / 2, IconSize, IconSize),
                new TextSlot(name, style, new RectangleF(labelX, y, mark.X - MarkGap - labelX, ChipHeight)), mark, on));
            x += w + ChipGap;
        }

        return laid;
    }
}

/// <summary>One closed figure of a glyph: filled, or stroked <see cref="PaletteGlyphs.Stroke"/> wide along its outline.</summary>
public readonly record struct GlyphFigure(OutlineSegment[] Path, bool Stroked = false);

/// <summary>
/// The palette's glyphs in DIPs from their box's top-left corner: a chip's picture in a 16 box, its "+" and tick in a
/// 10 box, and Send's arrow in a 16 box. No figure overlaps another, so a glyph fades evenly.
/// </summary>
public static class PaletteGlyphs
{
    public const float Stroke = 1.5f, SendSize = 16;

    // A frame stroked along these edges: its inside runs from 2.5 to 13.5 across and from 3.5 to 12.5 down.
    private static readonly GlyphFigure Frame = new(Rect(1.75f, 2.75f, 12.5f, 10.5f), Stroked: true);

    /// <summary>Selected text: a highlighted run with the text cursor after it.</summary>
    public static readonly GlyphFigure[] Selection =
    [
        new(Rect(1, 5, 8.5f, 6)),
        new(TabStrip.Polygon(10.5f, 2, 14.5f, 2, 14.5f, 3.5f, 13.25f, 3.5f, 13.25f, 12.5f, 14.5f, 12.5f, 14.5f, 14, 10.5f, 14, 10.5f, 12.5f, 11.75f, 12.5f, 11.75f, 3.5f, 10.5f, 3.5f)),
    ];

    /// <summary>A window: a frame with a title bar.</summary>
    public static readonly GlyphFigure[] Window = [Frame, new(Rect(2.5f, 3.5f, 11, 2.5f))];

    /// <summary>A picture: a frame with a hill and the sun.</summary>
    public static readonly GlyphFigure[] Screenshot = [Frame, new(TabStrip.Polygon(4, 11.5f, 7.5f, 7, 11, 11.5f)), new(Rect(10.25f, 4.75f, 2, 2))];

    /// <summary>A file: a page with its corner folded in.</summary>
    public static readonly GlyphFigure[] Shelf = [new(TabStrip.Polygon(3.25f, 1.75f, 9.25f, 1.75f, 9.25f, 5.25f, 12.75f, 5.25f, 12.75f, 14.25f, 3.25f, 14.25f), Stroked: true)];

    /// <summary>A note: a page with three lines.</summary>
    public static readonly GlyphFigure[] Note =
        [new(Rect(2.25f, 2.25f, 11.5f, 11.5f), Stroked: true), new(Rect(4.5f, 5, 7, 1.5f)), new(Rect(4.5f, 7.75f, 7, 1.5f)), new(Rect(4.5f, 10.5f, 4.5f, 1.5f))];

    /// <summary>A region: the four corners of a box drawn round something.</summary>
    public static readonly GlyphFigure[] Region =
    [
        new(TabStrip.Polygon(1.5f, 1.5f, 6, 1.5f, 6, 3, 3, 3, 3, 6, 1.5f, 6)),
        new(TabStrip.Polygon(14.5f, 1.5f, 14.5f, 6, 13, 6, 13, 3, 10, 3, 10, 1.5f)),
        new(TabStrip.Polygon(14.5f, 14.5f, 10, 14.5f, 10, 13, 13, 13, 13, 10, 14.5f, 10)),
        new(TabStrip.Polygon(1.5f, 14.5f, 1.5f, 10, 3, 10, 3, 13, 6, 13, 6, 14.5f)),
    ];

    /// <summary>An off chip's mark: add it.</summary>
    public static readonly GlyphFigure[] Plus =
        [new(TabStrip.Polygon(4.25f, 0.5f, 5.75f, 0.5f, 5.75f, 4.25f, 9.5f, 4.25f, 9.5f, 5.75f, 5.75f, 5.75f, 5.75f, 9.5f, 4.25f, 9.5f, 4.25f, 5.75f, 0.5f, 5.75f, 0.5f, 4.25f, 4.25f, 4.25f))];

    /// <summary>An on chip's mark: it goes with the question.</summary>
    public static readonly GlyphFigure[] Tick = [new(TabStrip.Polygon(0.6f, 5.3f, 1.7f, 4.2f, 3.9f, 6.4f, 8.3f, 2, 9.4f, 3.1f, 3.9f, 8.6f))];

    /// <summary>Send: an arrow pointing up and away.</summary>
    public static readonly GlyphFigure[] Send = [new(TabStrip.Polygon(2, 7.33f, 14, 2, 8.67f, 14, 7.33f, 8.67f))];

    /// <summary>Back: an arrow pointing left.</summary>
    public static readonly GlyphFigure[] Back = [new(TabStrip.Polygon(2, 8, 7.5f, 2.5f, 8.6f, 3.6f, 4.95f, 7.25f, 14, 7.25f, 14, 8.75f, 4.95f, 8.75f, 8.6f, 12.4f, 7.5f, 13.5f))];

    public static GlyphFigure[] Icon(ChipIcon icon) => icon switch
    {
        ChipIcon.Selection => Selection,
        ChipIcon.Window => Window,
        ChipIcon.Screenshot => Screenshot,
        ChipIcon.Shelf => Shelf,
        ChipIcon.Note => Note,
        _ => Region,
    };

    private static OutlineSegment[] Rect(float x, float y, float w, float h) => TabStrip.Polygon(x, y, x + w, y, x + w, y + h, x, y + h);
}
