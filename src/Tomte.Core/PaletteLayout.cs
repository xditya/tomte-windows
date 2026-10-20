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
/// <paramref name="Job"/> is a scheduled job's question: its yes says Schedule, and its summary is shown whole.
/// </summary>
public sealed record Confirmation(string Summary, string Line, bool AlwaysAllow, bool Destructive, bool Job = false)
{
    /// <summary>Why a tool asks, in plain words, then the keys that answer it.</summary>
    public static string LineFor(ActionTier tier, ConfirmKeys keys, bool job = false)
    {
        string why = job ? "Runs later on its own" : tier switch
        {
            ActionTier.Read => "Sends what it reads to the AI",
            ActionTier.AppLocalWrite => "Changes Tomte's own data",
            ActionTier.ExternalEffect => "Reaches outside Tomte",
            _ => "Asks every time",
        };
        string yes = job ? "schedules it" : "runs it";
        return why + keys switch
        {
            ConfirmKeys.Prompt => $" · Enter {yes}, Esc cancels",
            ConfirmKeys.Voice => $" · Win+Alt+Enter {yes}, Esc cancels",
            _ => " · Esc cancels",
        };
    }
}

/// <summary>The picture before a chip's name.</summary>
public enum ChipIcon { Selection, Window, Screenshot, Shelf, Note, Region }

/// <summary>A chip to draw: its short name, its picture, whether it is on, and whether it shows a thumbnail of what it holds.</summary>
public readonly record struct ChipSpec(string Name, ChipIcon Icon, bool On, bool Thumb = false);

/// <summary>
/// A chip laid out: its pill, which is also where it takes clicks, the picture at its left (the region's thumbnail for a
/// chip that holds one), its name, and the mark at its right, a "+" while off and a close cross while on.
/// </summary>
public readonly record struct LaidChip(RectangleF Box, RectangleF Icon, TextSlot Label, RectangleF Mark, bool On, bool Thumb = false)
{
    public float Corner => Thumb ? PaletteLayout.ThumbChipCorner : Box.Height / 2;
}

/// <summary>An action under an answer: its id, its pill, its word, and whether it is the outlined one.</summary>
public readonly record struct LaidAction(string Id, RectangleF Box, TextSlot Label, bool Outlined);

/// <summary>
/// The footer laid out: the divider above it, the status dot, where the request went, what this answer cost, what today
/// cost against the cap, and the meter for it with its fill's width.
/// </summary>
public readonly record struct PaletteFoot(RectangleF Divider, RectangleF Dot, TextSlot Provider, TextSlot Cost, TextSlot Spend, RectangleF Meter, float Fill);

/// <summary>
/// The palette's content in DIPs from its top-left corner: the prompt card after the tomte, with the chips first in it,
/// then the text box and Send; what the next send carries under it; the answer beside its sparkle; the actions under a
/// finished answer; a tool's question while one is open; and the footer along the bottom. The palette is as tall as that
/// needs, from 150 to 480 DIPs; an answer longer than the room left scrolls in its band. <paramref name="text"/> is the
/// prompt's own height (the text box's content height), which the card grows with. <paramref name="send"/> puts a Send
/// button at the card's right end, for a palette with a text box. <paramref name="sends"/> is the line under the card
/// saying what the next send carries; empty for none. <paramref name="back"/> puts the back arrow before the card, for a
/// palette opened from the panels. <paramref name="actions"/> are the actions under the answer, by id; none while it is
/// not finished.
/// </summary>
public sealed class PaletteLayout(float width, bool mascot, float text = 0, IReadOnlyList<ChipSpec>? chips = null, Confirmation? confirm = null, bool send = false, string sends = "", bool back = false, IReadOnlyList<string>? actions = null)
{
    public const float Pad = 16, Top = 14, Bottom = 16, Gap = 14, BlockGap = 8;

    /// <summary>The prompt card: one row of 40 inside a 6 inset (8 at the left), its text centred on that row.</summary>
    public const float PromptHeight = 52, PromptInset = 6, PromptInsetLeft = 8, PromptCorner = 18, RowHeight = 40, RowGap = 6;

    /// <summary>The prompt's own text, and a line of it.</summary>
    public const float PromptSize = 15, PromptLine = 20, PromptInsetY = (PromptHeight - PromptLine) / 2;

    /// <summary>The card grows with its text up to this, then its text scrolls: four lines.</summary>
    public const float PromptMaxText = 4 * PromptLine;

    /// <summary>
    /// A chip: 30 high and fully round, the picture, a gap, the name, a gap and the mark between the side paddings. One
    /// that holds a picture (the circled region) is as tall as the card's row, with its thumbnail where the picture goes.
    /// </summary>
    public const float ChipHeight = 30, ChipPadX = 10, ChipGap = 6, ChipCorner = ChipHeight / 2, ChipMaxText = 160;
    public const float IconSize = 14, IconGap = 5, MarkSize = 14, MarkGap = 6;
    public const float ThumbChipHeight = RowHeight, ThumbChipCorner = 12, ThumbWidth = 40, ThumbHeight = 32, ThumbCorner = 8, ThumbInset = 4, ThumbGap = 8;

    /// <summary>The text box shares the chips' row while it keeps this much of it; else it goes under them.</summary>
    public const float MinEditor = 200, ChipEditorGap = 10;

    /// <summary>Send sits inside the card at its right end, level with the last line, its corner inset with the card's.</summary>
    public const float SendSize = 40, SendInset = PromptInset, SendGap = 10, SendCorner = 12, SendGlyph = 18;

    public const float SendsHeight = 16;

    /// <summary>The answer: a sparkle in a 30 DIP gutter, 8 in from the edge of the content, its words in the column after it.</summary>
    public const float AnswerInset = 8, Gutter = 30, SparkleSize = 18;

    /// <summary>The actions under a finished answer: round pills, aligned with the answer's words.</summary>
    public const float ActionHeight = 32, ActionPadX = 12, ActionGap = 6;

    /// <summary>The footer: a line of text under a divider with 12 above it; the meter 64 by 4.</summary>
    public const float FootHeight = 16, FootTop = 12, FootGap = 10, DotSize = 6, MeterWidth = 64, MeterHeight = 4, MeterMin = 4;

    /// <summary>A tool's question: a card above the footer, its summary on up to three lines, then why it asks and the buttons.</summary>
    public const float AskPad = 12, AskGap = 8, ButtonHeight = 32, ButtonPadX = 14, ButtonGap = 6, ButtonCorner = 10, ButtonMinWidth = 64;

    public const int AskLines = 3, PromptLines = 4;

    /// <summary>
    /// The most lines a job's summary may take at the full palette width. A job's card shows its summary whole, never
    /// cut, since the stored prompt is what runs; the planner refuses a job whose summary would need more.
    /// </summary>
    public const int JobLines = 8;

    /// <summary>A wheel notch scrolls the answer three of its lines, as Windows scrolls by default.</summary>
    public const float WheelStep = 3 * 21;

    public static TextStyle Placeholder => new(PromptSize, Color: Theme.Ink3);

    /// <summary>The model's words; the first paragraph of an answer stands larger, in <see cref="Lead"/>.</summary>
    public static TextStyle AnswerText => TextStyle.Body;

    public static TextStyle Lead => new(18, 500, LineHeight: 27, Tracking: -0.01f);
    public static TextStyle StepText => new(12, Color: Theme.Ink2, LineHeight: 18);
    public static TextStyle ErrorText => AnswerText with { Color = Theme.Warn };
    public static TextStyle Chip => TextStyle.Secondary;
    public static TextStyle ChipOn => Chip with { Color = Theme.Ink };
    public static TextStyle ThumbLabel => Chip with { Family = TextStyle.Mono };
    public static TextStyle Provider => TextStyle.Secondary with { Color = Theme.Ink3 };
    public static TextStyle Cost => Provider with { Family = TextStyle.Mono };
    public static TextStyle Spend => Cost with { Align = TextAlign.Trailing };
    public static TextStyle Action => new(12, Align: TextAlign.Center);
    public static TextStyle OutlinedAction => Action with { Color = Theme.Ink2 };

    /// <summary>What was said, where a palette with no text box (the voice palette) shows its prompt.</summary>
    public static TextStyle PromptText => new(PromptSize);

    public static TextStyle AskSummary => AnswerText with { Color = Theme.Ink };
    public static TextStyle AskLine => new(11, Color: Theme.Ink2);
    public static TextStyle Button => new(12, 500, Align: TextAlign.Center);
    public static TextStyle RunButton => Button with { Weight = 600, Color = Theme.OnAccent };

    public const string Hint = "Ask about what is in front of you…";
    public const string Off = "The AI layer is off";
    public const string Setup = "Add a provider key to use the palette.";
    public const string Run = "Run", Schedule = "Schedule", Cancel = "Cancel", AlwaysAllow = "Always allow";
    public const string SendTip = "Send";

    /// <summary>The actions under a finished answer, by id and word: three filled, then Follow up outlined.</summary>
    public const string CopyId = "copy", NoteId = "note", ShelfId = "shelf", FollowId = "follow";

    public static string ActionLabel(string id) => id switch
    {
        CopyId => "Copy",
        NoteId => "Add to note",
        ShelfId => "Keep on shelf",
        _ => "Follow up",
    };

    /// <summary>What an action's pill says once it has done its work, so a second click is not needed to find out.</summary>
    public static string ActionDone(string id) => id switch
    {
        CopyId => "Copied",
        NoteId => "Added to note",
        ShelfId => "On the shelf",
        _ => ActionLabel(id),
    };

    /// <summary>The foot's provider line while a card asks before a tool runs: nothing is on its way until the user answers.</summary>
    public const string Waiting = "Waiting for you";

    /// <summary>
    /// An off chip: no fill, a strong outline, and secondary ink for its picture and "+". An on chip, which holds something
    /// for the send, is raised with the main ink and a close cross. Glyph ink is opaque, so a glyph fades as one.
    /// </summary>
    public static uint ChipOutline => Theme.LineStrong;

    public static uint ChipInk => Theme.Ink2;

    public static uint ChipOnFill => Theme.Raised;

    public static uint ChipOnInk => Theme.Ink;

    /// <summary>
    /// The path a chip's outline follows and the stroke's width in DIPs: one pixel wide, half a pixel inside the pill's
    /// edges put on whole pixels, so it lights whole pixels at any scale; two half-lit ones would fall under 3:1.
    /// <paramref name="parent"/> is where the pill's space starts, in DIPs from a whole pixel.
    /// </summary>
    public static (RectangleF Path, float Corner, float Stroke) ChipStroke(LaidChip chip, Vector2 parent, double scale)
    {
        var pill = chip.Box;
        float half = (float)(0.5 / scale);
        float left = PixelGrid.Snap(parent.X, pill.Left, scale), top = PixelGrid.Snap(parent.Y, pill.Top, scale);
        float right = PixelGrid.Snap(parent.X, pill.Right, scale), bottom = PixelGrid.Snap(parent.Y, pill.Bottom, scale);
        return (RectangleF.FromLTRB(left + half, top + half, right - half, bottom - half), chip.Corner - half, 2 * half);
    }

    /// <summary>Send: an accent button with the accent's ink for its arrow.</summary>
    public static uint SendFill => Theme.Accent;

    public static uint SendInk => Theme.OnAccent;

    /// <summary>
    /// The question's card carries the accent in its outline so it reads as waiting for the user. Its buttons are filled
    /// raised and outlined as the chips are; Run takes the accent, or the warning colour for what is asked every time.
    /// </summary>
    public static uint AskOutline => Theme.WithOpacity(Theme.Accent, 0.4f);

    public static uint ButtonFill => Theme.Raised;

    public static uint ButtonOutline => ChipOutline;

    public static uint RunWarn => Theme.Warn;

    public static uint RunFill => Theme.Accent;

    /// <summary>The filled actions are raised; Follow up is outlined as an off chip is. The divider over the footer, and the meter.</summary>
    public static uint ActionFill => Theme.Raised;

    public static uint ActionOutline => Theme.LineStrong;

    public static uint Divider => Theme.Raised;

    public static uint MeterTrack => Theme.Track;

    public static uint MeterFill => Theme.Ink;

    private (IReadOnlyList<LaidChip> Chips, int Rows, bool Shared)? row;
    private float? askHeight;

    // What each run of the last answer measured.
    private Dictionary<TextRun, (float Width, float Height, int Lines, bool Trimmed)> measured = [];

    public float Width => width;

    /// <summary>The back arrow: as big as Send, level with the card's first row, after the tomte.</summary>
    public const float BackSize = SendSize, BackGap = 8, BackCorner = SendCorner;

    public const string BackId = "back", BackTip = "Back", BackKey = "Alt+Left";

    public float Left => Start + (back ? BackSize + BackGap : 0);

    /// <summary>The back arrow before the prompt card; empty without one.</summary>
    public RectangleF Back => back ? new(Start, Top + ((PromptHeight - BackSize) / 2), BackSize, BackSize) : RectangleF.Empty;

    /// <summary>
    /// The tomte beside the prompt card: centred on its first row, its art as far in from the left edge as the content
    /// below it.
    /// </summary>
    public static readonly MascotSeat Seat = new(Top + (PromptHeight / 2), Pad);

    private float Start => mascot ? Seat.After(10) : Pad;

    // The chips' room inside the card: from its left inset to Send's gap.
    private float RoomLeft => Left + PromptInsetLeft;

    private float RoomRight => width - Pad - (send ? SendInset + SendSize + SendGap : PromptInset);

    /// <summary>The card: one row tall at least, a row more for each row of chips that does not leave room for the text, growing with its text.</summary>
    public RectangleF Prompt
    {
        get
        {
            float body = Math.Clamp(text, PromptLine, PromptMaxText);
            return new(Left, Top, width - Pad - Left, Math.Max(PromptHeight, EditorTop - Top + body + PromptInsetY));
        }
    }

    // The text box's top: centred on the first row, or on the row under the chips when they took the whole width.
    private float EditorTop => Top + PromptInsetY + (Laid.Shared ? 0 : Laid.Rows * (RowHeight + RowGap));

    /// <summary>Where the prompt's text box sits inside the card: after the chips on their row when there is room, short of Send.</summary>
    public RectangleF Editor
    {
        get
        {
            var (laid, _, shared) = Laid;
            float left = shared && laid.Count > 0 ? laid[^1].Box.Right + ChipEditorGap : RoomLeft;
            return new(left, EditorTop, Math.Max(0, RoomRight - left), Math.Clamp(text, PromptLine, PromptMaxText));
        }
    }

    /// <summary>
    /// The placeholder's line: the text box's first line, so the hint sits where typing starts, under the chips when
    /// they took the row rather than across the middle of the whole card.
    /// </summary>
    public RectangleF HintLine => Editor with { Height = PromptLine };

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

    /// <summary>
    /// One pill per chip, 6 apart, in the card's rows from its left: a chip that does not fit beside the one before starts
    /// the next row. A name is cut with an ellipsis at 160 DIP, or at the row's width.
    /// </summary>
    public IReadOnlyList<LaidChip> Chips => Laid.Chips;

    /// <summary>What the next send carries, on one line under the card from where the chips start; empty while no chip is on.</summary>
    public TextSlot SendsLine => new(sends, TextStyle.Small, new RectangleF(Pad + AnswerInset, Prompt.Bottom + BlockGap, width - (2 * (Pad + AnswerInset)), sends.Length > 0 ? SendsHeight : 0));

    public float AnswerTop => (sends.Length > 0 ? SendsLine.Slot.Bottom : Prompt.Bottom) + Gap;

    /// <summary>The answer's words start after the sparkle's gutter.</summary>
    public float AnswerX => Pad + AnswerInset + Gutter;

    public float AnswerWidth => width - AnswerX - Pad - AnswerInset;

    /// <summary>The sparkle before the answer, in band space: in the gutter, centred on the lead's first line.</summary>
    public RectangleF Sparkle => new(Pad + AnswerInset, (Lead.LineHeight - SparkleSize) / 2, SparkleSize, SparkleSize);

    /// <summary>The palette's height for an answer this tall.</summary>
    public float Height(float answer) =>
        (float)Math.Clamp(AnswerTop + (answer > 0 ? answer + Gap : 0) + ActionRoom + AskRoom + FootRoom + Bottom, PanelSize.PaletteMinHeight, PanelSize.PaletteMaxHeight);

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

        var card = new RectangleF(Pad, height - Bottom - FootRoom - Gap - AskHeight, width - (2 * Pad), AskHeight);
        float inner = card.Width - 2 * AskPad, rowY = card.Bottom - AskPad - ButtonHeight, right = card.Right - AskPad;
        var buttons = new List<(ConfirmButton, RectangleF, TextSlot)>();
        foreach (var (button, label, style) in new[] { (ConfirmButton.Run, confirm.Job ? Schedule : Run, RunButton), (ConfirmButton.Cancel, Cancel, Button), (ConfirmButton.AlwaysAllow, AlwaysAllow, Button) })
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
        return (card, new TextRun(confirm.Summary, AskSummary, inner, SummaryLines), new Vector2(left, card.Y + AskPad),
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

    private float ActionRoom => actions is { Count: > 0 } ? ActionHeight + Gap : 0;

    private const float FootRoom = FootTop + FootHeight;

    private float SummaryHeight(string summary)
    {
        using var laid = TextLayout.Of(new TextRun(summary, AskSummary, width - (2 * Pad) - 2 * AskPad, SummaryLines));
        return laid.Height;
    }

    // a palette narrowed by a small monitor gives a job's card a line or two more than JobLines rather than cut it.
    private int SummaryLines => confirm?.Job == true ? int.MaxValue : AskLines;

    /// <summary>True when a job's summary takes at most <see cref="JobLines"/> lines of the card at the full palette width.</summary>
    public static bool FitsJobCard(string summary)
    {
        using var laid = TextLayout.Of(new TextRun(summary, AskSummary, (float)PanelSize.PaletteWidth - 2 * Pad - 2 * AskPad, JobLines));
        return !laid.Trimmed;
    }

    /// <summary>
    /// The actions under the answer in a palette of <paramref name="height"/>, from the answer's column, above the
    /// question and the footer: filled pills, Follow up outlined. <paramref name="done"/> are the ones that did their work,
    /// which say so; their pills keep their width.
    /// </summary>
    public IReadOnlyList<LaidAction> Actions(float height, IReadOnlyCollection<string>? done = null)
    {
        var laid = new List<LaidAction>();
        float x = AnswerX, y = height - Bottom - FootRoom - AskRoom - Gap - ActionHeight;
        foreach (string id in actions ?? [])
        {
            bool outlined = id == FollowId;
            var style = outlined ? OutlinedAction : Action;
            float w = Math.Max(TextSlot.Measure(ActionLabel(id), style, 400), TextSlot.Measure(ActionDone(id), style, 400)) + (2 * ActionPadX);
            var box = new RectangleF(x, y, w, ActionHeight);
            laid.Add(new(id, box, new TextSlot(done?.Contains(id) == true ? ActionDone(id) : ActionLabel(id), style, box), outlined));
            x = box.Right + ActionGap;
        }

        return laid;
    }

    /// <summary>The AI history button at the footer's left: a clock, as big as the smallest target, centred on the footer's line.</summary>
    public RectangleF History(float height) => new(Pad + AnswerInset - 5, height - Bottom - (FootHeight / 2) - (StateLook.MinTarget / 2), StateLook.MinTarget, StateLook.MinTarget);

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

    /// <summary>
    /// The footer under its divider: the history button when there is one, the status dot, where the request went or is
    /// going and the tool steps, this answer's cost, then at the right what today cost against the cap and the meter.
    /// </summary>
    public PaletteFoot Foot(string provider, int steps, decimal cost, decimal spent, decimal? cap, float height, bool history = false)
    {
        float y = height - Bottom - FootHeight, left = Pad + AnswerInset, right = width - Pad - AnswerInset;
        var divider = new RectangleF(Pad, y - FootTop, width - (2 * Pad), 1);
        float x = history ? History(height).Right + 4 : left;
        var dot = new RectangleF(x, y + ((FootHeight - DotSize) / 2), DotSize, DotSize);
        var meter = cap is null ? RectangleF.Empty : new RectangleF(right - MeterWidth, y + ((FootHeight - MeterHeight) / 2), MeterWidth, MeterHeight);
        string spend = SpendLine(spent, cap);
        float spendRight = cap is null ? right : meter.X - FootGap, spendWidth = TextSlot.Measure(spend, Spend, right - left);
        var spendSlot = new RectangleF(spendRight - spendWidth, y, spendWidth, FootHeight);
        string line = steps > 0 ? string.Create(CultureInfo.InvariantCulture, $"{provider} · {steps} tool step{(steps == 1 ? "" : "s")}") : provider;
        string costLine = CostLine(cost);
        float from = dot.Right + FootGap, room = Math.Max(0, spendSlot.X - FootGap - from);
        float costWidth = costLine.Length == 0 ? 0 : Math.Min(TextSlot.Measure(costLine, Cost, room), room / 3);
        float providerWidth = Math.Min(TextSlot.Measure(line, Provider, room), room - costWidth - (costWidth > 0 ? 6 : 0));
        float fill = cap is { } c && c > 0 ? Math.Clamp((float)(spent / c), 0, 1) * MeterWidth : 0;
        return new(divider, dot, new TextSlot(line, Provider, new RectangleF(from, y, Math.Max(0, providerWidth), FootHeight)),
            new TextSlot(costLine, Cost, new RectangleF(from + providerWidth + 6, y, costWidth, FootHeight)),
            new TextSlot(spend, Spend, spendSlot), meter, cap is null ? 0 : Math.Max(MeterMin, fill));
    }

    /// <summary>Today's spend against the cap: "$0.12 / $10 today", or "$0.12 today" with no cap.</summary>
    public static string SpendLine(decimal spent, decimal? cap) =>
        cap is { } c ? string.Create(CultureInfo.InvariantCulture, $"${spent:0.00} / ${c:0.##} today") : string.Create(CultureInfo.InvariantCulture, $"${spent:0.00} today");

    /// <summary>What this answer cost, after a dot, to a tenth of a cent; empty while nothing has cost anything.</summary>
    public static string CostLine(decimal cost) => cost > 0 ? string.Create(CultureInfo.InvariantCulture, $"· ${cost:0.00#}") : "";

    /// <summary>The status dot: a send that went, one that failed, or nothing sent yet.</summary>
    public static uint StatusDot(string provider, bool failed) => failed ? Theme.Warn : provider.StartsWith("Sent to", StringComparison.Ordinal) ? Theme.Ok : Theme.Ink3;

    /// <summary>
    /// The answer's blocks laid out whole in the answer's column, one under the other, each where it starts from the top
    /// of the answer, and the height of them all. Blocks in <see cref="AnswerText"/> are the model's words, read as
    /// Markdown as the drop result reads them; the first paragraph of the first one stands in <see cref="Lead"/>. While the
    /// answer is <paramref name="streaming"/> the last block shows only what has settled (<see cref="Markdown.Settled"/>).
    /// Tool steps, an error and the follow-up line stay plain. Empty blocks take no room. A run the last answer had is not
    /// laid out again, so a streamed word costs about the block it changed.
    /// </summary>
    public (IReadOnlyList<TextAt> Runs, float Height) Answer(IReadOnlyList<(string Text, TextStyle Style)> blocks, bool streaming = false)
    {
        var before = measured;
        measured = [];
        (float Width, float Height, int Lines, bool Trimmed) Measure(TextRun run)
        {
            if (!before.TryGetValue(run, out var size))
            {
                size = DropResultLayout.Measure(run);
            }

            measured[run] = size;
            return size;
        }

        float y = 0;
        bool lead = true;
        var runs = new List<TextAt>();
        void Lay(IReadOnlyList<MarkdownBlock> parsed, TextStyle style)
        {
            float top = y + (runs.Count > 0 ? BlockGap : 0);
            var (laid, height) = DropResultLayout.Lay(parsed, style, AnswerWidth, cut: false, maxLines: int.MaxValue, measure: Measure);
            if (laid.Count > 0)
            {
                // Lay starts its runs at the drop result's padding, not the answer's column.
                runs.AddRange(laid.Select(r => r with { At = r.At + new Vector2(AnswerX - DropResultLayout.Pad, top) }));
                y = top + height;
            }
        }

        for (int i = 0; i < blocks.Count; i++)
        {
            var (block, style) = blocks[i];
            if (style == AnswerText)
            {
                var parsed = Markdown.Parse(streaming && i == blocks.Count - 1 ? Markdown.Settled(block) : block);
                if (lead && parsed.Count > 0)
                {
                    // The answer's first paragraph is its lead; a list or a heading first is not.
                    lead = false;
                    int first = parsed[0].Kind == MarkdownKind.Paragraph ? 1 : 0;
                    Lay([.. parsed.Take(first)], Lead);
                    Lay([.. parsed.Skip(first)], AnswerText);
                }
                else
                {
                    Lay(parsed, AnswerText);
                }
            }
            else if (block.Length > 0)
            {
                float top = y + (runs.Count > 0 ? BlockGap : 0);
                var size = Measure(new TextRun(block, style, AnswerWidth, Lines: int.MaxValue));
                runs.Add(new(new TextRun(block, style, AnswerWidth, size.Lines), new Vector2(AnswerX, top)));
                y = top + size.Height;
            }
        }

        return (runs, y);
    }

    /// <summary>Where the answer shows in a palette of <paramref name="height"/>: under the card, above the actions, the question and the footer. It scrolls inside.</summary>
    public RectangleF AnswerBand(float height) => new(0, AnswerTop, width, Math.Max(0, height - Bottom - FootRoom - Gap - AskRoom - ActionRoom - AnswerTop));

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

    private (IReadOnlyList<LaidChip> Chips, int Rows, bool Shared) Laid => row ??= LayChips();

    private (IReadOnlyList<LaidChip>, int, bool) LayChips()
    {
        float start = RoomLeft, right = RoomRight, x = start;
        int rows = chips is { Count: > 0 } ? 1 : 0;
        var laid = new List<LaidChip>();
        foreach (var (name, icon, on, thumb) in chips ?? [])
        {
            var style = thumb ? ThumbLabel : on ? ChipOn : Chip;
            float lead = thumb ? ThumbInset + ThumbWidth + ThumbGap : ChipPadX + IconSize + IconGap;
            float fixedWidth = lead + MarkGap + MarkSize + ChipPadX;
            float w = fixedWidth + TextSlot.Measure(name, style, Math.Max(0, Math.Min(ChipMaxText, right - start - fixedWidth)));
            if (x > start && x + w > right)
            {
                (x, rows) = (start, rows + 1);
            }

            float h = thumb ? ThumbChipHeight : ChipHeight, rowTop = Top + PromptInset + ((rows - 1) * (RowHeight + RowGap));
            float y = rowTop + ((RowHeight - h) / 2), middle = y + (h / 2), labelX = x + lead;
            var picture = thumb ? new RectangleF(x + ThumbInset, middle - (ThumbHeight / 2), ThumbWidth, ThumbHeight) : new RectangleF(x + ChipPadX, middle - (IconSize / 2), IconSize, IconSize);
            var mark = new RectangleF(x + w - ChipPadX - MarkSize, middle - (MarkSize / 2), MarkSize, MarkSize);
            laid.Add(new(new RectangleF(x, y, w, h), picture, new TextSlot(name, style, new RectangleF(labelX, y, mark.X - MarkGap - labelX, h)), mark, on, thumb));
            x += w + ChipGap;
        }

        // One row of chips that leaves the text box its room shares the row with it.
        bool shared = rows <= 1 && (laid.Count == 0 || laid[^1].Box.Right + ChipEditorGap + MinEditor <= right);
        return (laid, rows, shared);
    }
}

/// <summary>One figure of a glyph: filled, or stroked <see cref="PaletteGlyphs.Stroke"/> wide along its outline. Closed unless <paramref name="Open"/>.</summary>
public readonly record struct GlyphFigure(OutlineSegment[] Path, bool Stroked = false, bool Open = false);

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
