using System.Drawing;
using System.Globalization;
using System.Numerics;
using Tomte.Core.Interaction;
using Tomte.Core.Text;

namespace Tomte.Core;

/// <summary>
/// Where the History panel's parts sit, in DIPs from the body's top-left corner (the body starts under the tab strip):
/// the heading, the list of conversations newest first, and a line under it. A row shows the source's icon, the first
/// question on one line and, under it, the turns, the time, the model and the cost. The opened row grows to show the
/// whole conversation, read as Markdown, then Continue and Copy answer. Rows and the opened conversation scroll together
/// inside the list; the scroll is how far the list has moved up. The same numbers draw the panel and say where its
/// targets are.
/// </summary>
public sealed class HistoryPanelLayout
{
    public const float Pad = Section.BodyPad, Top = Section.BodyTop, Bottom = Section.BodyPad, HeadHeight = 22, FootHeight = 14, Gap = 10, Corner = 14;
    public const float RowHeight = 44, RowGap = 6, Icon = 24, IconCorner = 8, IconGlyph = 16, ButtonCorner = Section.ButtonCorner;

    /// <summary>The opened conversation's inset inside its card, and the room between two turns.</summary>
    public const float Inset = DropResultLayout.Pad, TurnGap = 14, PartGap = 4;

    public const float ButtonHeight = StateLook.ActionHeight, ButtonPadX = 14, ButtonGap = 6;

    /// <summary>A wheel notch scrolls the list three lines of an answer.</summary>
    public const float WheelStep = DropResultLayout.WheelStep;

    /// <summary>The most lines of one conversation the panel lays out (see <see cref="DropResultLayout.MaxLines"/>).</summary>
    public const int MaxLines = DropResultLayout.MaxLines, PromptLines = 6;

    public const string ContinueId = "continue", CopyId = "copy", Continue = "Continue", Copy = "Copy answer";

    /// <summary>The line under a conversation cut at <see cref="MaxLines"/>.</summary>
    public const string Rest = "The rest of this conversation is not shown here.";

    public const float BoxLineHeight = 18;

    public static TextStyle Asked => TextStyle.Primary with { Weight = 600, LineHeight = 19 };
    public static TextStyle StepStyle => new(12, Color: Theme.Ink2, LineHeight: 18);
    public static TextStyle ErrorStyle => DropResultLayout.BodyStyle with { Color = Theme.Warn };
    public static TextStyle ButtonText => new(12, 500, Align: TextAlign.Center);

    private readonly List<RectangleF> rows = [];
    private readonly List<TextAt> conversation = [];

    /// <param name="opened">The key of the opened row, or null.</param>
    public HistoryPanelLayout(Vector2 body, IReadOnlyList<AiExchange> items, string? opened)
    {
        Body = body;
        Items = items;
        float y = 0, width = List.Width;
        for (int i = 0; i < items.Count; i++)
        {
            float height = RowHeight;
            if (items[i].Key == opened)
            {
                Opened = i;
                var (runs, laid) = Lay(items[i], width - 2 * Inset);
                conversation.AddRange(runs.Select(r => r with { At = r.At + new Vector2(0, y + RowHeight) }));
                float buttons = y + RowHeight + laid + Gap;
                float w = TextSlot.Measure(Continue, ButtonText, width) + 2 * ButtonPadX;
                ContinueButton = new RectangleF(Inset, buttons, w, ButtonHeight);
                CopyButton = new RectangleF(ContinueButton.Right + ButtonGap, buttons, TextSlot.Measure(Copy, ButtonText, width) + 2 * ButtonPadX, ButtonHeight);
                height = buttons + ButtonHeight + Inset - y;
            }

            rows.Add(new RectangleF(0, y, width, height));
            y += height + RowGap;
        }

        ContentHeight = Math.Max(0, y - RowGap);
    }

    public Vector2 Body { get; }

    public IReadOnlyList<AiExchange> Items { get; }

    /// <summary>The opened row's index, or -1.</summary>
    public int Opened { get; } = -1;

    public RectangleF Head => new(Pad, Top, Body.X - 2 * Pad, HeadHeight);

    public RectangleF List => RectangleF.FromLTRB(Pad, Top + HeadHeight + Gap, Body.X - Pad, Body.Y - Bottom - FootHeight - Gap);

    public RectangleF Foot => new(Pad, Body.Y - Bottom - FootHeight, Body.X - 2 * Pad, FootHeight);

    /// <summary>Each row's card in list coordinates, before the scroll moves it; the opened one is as tall as its conversation.</summary>
    public IReadOnlyList<RectangleF> Rows => rows;

    /// <summary>The opened conversation's runs, in list coordinates.</summary>
    public IReadOnlyList<TextAt> Conversation => conversation;

    /// <summary>The opened row's two buttons, in list coordinates; empty with none opened.</summary>
    public RectangleF ContinueButton { get; }

    public RectangleF CopyButton { get; }

    public float ContentHeight { get; }

    public float MaxScroll => Math.Max(0, ContentHeight - List.Height);

    public static string RowId(AiExchange item) => "row:" + item.Key;

    /// <summary>The source's icon square inside a row, in the row's own coordinates.</summary>
    public static RectangleF IconIn => new(Gap, (RowHeight - Icon) / 2, Icon, Icon);

    /// <summary>A wheel delta (120 a notch, positive away from the user) moves the list, back toward the newest.</summary>
    public float Scrolled(float scroll, int delta) => Math.Clamp(scroll - delta / 120f * WheelStep, 0, MaxScroll);

    /// <summary>The scroll that shows as much of row <paramref name="index"/> as fits, its top first.</summary>
    public float Reveal(float scroll, int index) => Reveal(scroll, rows[index]);

    /// <summary>The same for any rectangle in list coordinates; one already in view leaves the scroll as it is.</summary>
    public float Reveal(float scroll, RectangleF r) => Math.Clamp(Math.Min(Math.Max(scroll, r.Bottom - List.Height), r.Y), 0, MaxScroll);

    /// <summary>The heading and what goes beside it.</summary>
    public IReadOnlyList<TextSlot> HeadText(bool on)
    {
        const string title = "AI history";
        float after = TextSlot.Measure(title, TextStyle.PageTitle, Head.Width) + Gap;
        return
        [
            new(title, TextStyle.PageTitle, Head),
            new(on ? $"the last {AiHistory.MaxItems} conversations, up to {AiHistory.MaxDays} days, on this PC only" : "off",
                Section.Note with { Size = 13 }, new(Head.X + after, Top, Head.Width - after, HeadHeight)),
        ];
    }

    public IReadOnlyList<TextSlot> FootText() => [new("Turn it off or clear it in Settings, AI history.", TextStyle.Small, Foot)];

    /// <summary>In the list's box while history is off or empty: centred, line under line.</summary>
    public IReadOnlyList<TextSlot> BoxText(bool on)
    {
        string[] lines = on
            ? ["No AI answers yet.", "Answers from the AI box, voice, circle-to-ask, drop zones and jobs show here."]
            : ["AI history is off.", "Turn it on in Settings, AI history, to keep answers on this PC."];
        float y = List.Y + (List.Height - lines.Length * BoxLineHeight) / 2;
        return [.. lines.Select((line, i) => new TextSlot(line, (i == 0 ? TextStyle.Secondary : TextStyle.Small) with { Align = TextAlign.Center }, new(List.X + 20, y + i * BoxLineHeight, List.Width - 40, BoxLineHeight)))];
    }

    /// <summary>
    /// A row's question on its first line and its facts on the second, after the icon, in the row's own coordinates.
    /// Opened, the question is the conversation's first line, in full, so the row's own line says only the facts.
    /// </summary>
    public static IReadOnlyList<TextSlot> RowText(AiExchange item, float rowWidth, IFormatProvider? culture = null, bool opened = false)
    {
        float x = Gap + Icon + Gap, width = rowWidth - x - Gap;
        return opened
            ? [new(Facts(item, culture), TextStyle.Small, new(x, 0, width, RowHeight))]
            :
            [
                new(OneLine(item.Question), TextStyle.Primary, new(x, 6, width, 18)),
                new(Facts(item, culture), TextStyle.Small, new(x, 24, width, 14)),
            ];
    }

    /// <summary>"3 turns · 14 Oct, 09:42 · claude-haiku-4-5 · $0.0031", the time in this PC's zone and culture.</summary>
    public static string Facts(AiExchange item, IFormatProvider? culture = null)
    {
        var at = item.At.ToLocalTime();
        var c = culture ?? CultureInfo.CurrentCulture;
        string turns = item.Turns.Count == 1 ? "1 turn" : string.Create(c, $"{item.Turns.Count} turns");
        return string.Create(c, $"{turns} · {at.ToString("d MMM", c)}, {at.ToString("t", c)} · {item.Model} · {KeptResult.Cost(item.CostUsd)}");
    }

    /// <summary>The text on the opened row's buttons, in list coordinates.</summary>
    public IReadOnlyList<TextSlot> ButtonLabels() =>
        Opened < 0 ? [] : [new(Continue, ButtonText, ContinueButton), new(Copy, ButtonText, CopyButton)];

    /// <summary>
    /// The targets in body coordinates for the list scrolled by <paramref name="scroll"/>, in Tab order: every row, by
    /// its first 44 DIP cut to the list, then the opened row's two buttons. A part out of view keeps its place with no
    /// height, so nothing hits it but the arrow keys still find it. Rows are one group, one Tab stop with the arrow keys
    /// moving between them. Copy answer is off when the last turn has no answer.
    /// </summary>
    public IReadOnlyList<Target> Targets(float scroll)
    {
        var targets = new List<Target>();
        for (int i = 0; i < rows.Count; i++)
        {
            targets.Add(new Target(RowId(Items[i]), InList(rows[i] with { Height = RowHeight }, scroll), Corner, TargetKind.Row, Group: 1, Checked: i == Opened));
        }

        // The group of rows is one Tab stop; the opened row's buttons come after it.
        if (Opened >= 0)
        {
            targets.Add(new Target(ContinueId, InList(ContinueButton, scroll), ButtonCorner, TargetKind.Button));
            targets.Add(new Target(CopyId, InList(CopyButton, scroll), ButtonCorner, TargetKind.Button, Enabled: Items[Opened].LastAnswer.Length > 0, Key: "Ctrl+C"));
        }

        return targets;
    }

    /// <summary>A target's rectangle in list coordinates, before the scroll: where focus on it scrolls the list to.</summary>
    public RectangleF Place(string id) => id switch
    {
        ContinueId => ContinueButton,
        CopyId => CopyButton,
        _ => rows.Where((_, i) => RowId(Items[i]) == id).Select(r => r with { Height = RowHeight }).FirstOrDefault(),
    };

    // A rectangle in list coordinates to body space, cut to the list; out of view it keeps its place with no height.
    private RectangleF InList(RectangleF r, float scroll)
    {
        r.Offset(List.X, List.Y - scroll);
        var shown = RectangleF.Intersect(r, List);
        return shown.IsEmpty ? r with { Height = 0 } : shown;
    }

    /// <summary>
    /// The conversation from the card's top-left corner, under the row's own line: for each turn the question, the names
    /// of the chips that were on, a line per tool call, the answer as Markdown and what went wrong, at most
    /// <see cref="MaxLines"/> lines in all.
    /// </summary>
    private static (IReadOnlyList<TextAt> Runs, float Height) Lay(AiExchange item, float width)
    {
        var runs = new List<TextAt>();
        float y = 0;
        int lines = 0;
        bool cut = false;
        void Add(TextRun run)
        {
            using var laid = TextLayout.Of(run);
            runs.Add(new TextAt(run, new Vector2(Inset, y)));
            y += laid.Height + PartGap;
            lines += laid.LineCount;
        }

        foreach (var turn in item.Turns)
        {
            if (lines >= MaxLines)
            {
                cut = true;
                break;
            }

            y += runs.Count == 0 ? 0 : TurnGap - PartGap;
            Add(new TextRun(turn.Prompt, Asked, width, PromptLines));
            if (turn.Chips.Count > 0)
            {
                Add(new TextRun("With " + string.Join(", ", turn.Chips), TextStyle.Small with { LineHeight = 16 }, width, 2));
            }

            foreach (string step in turn.Steps)
            {
                Add(new TextRun(step, StepStyle, width, 2));
            }

            if (turn.Answer.Length > 0 && lines < MaxLines)
            {
                string answer = turn.Answer;
                int read = answer.Length <= DropResultLayout.MaxChars ? answer.Length : char.IsHighSurrogate(answer[DropResultLayout.MaxChars - 1]) ? DropResultLayout.MaxChars - 1 : DropResultLayout.MaxChars;
                var (body, height) = DropResultLayout.Lay(Markdown.Parse(answer[..read]), DropResultLayout.BodyStyle, width, read < answer.Length, MaxLines - lines, Rest);
                runs.AddRange(body.Select(r => r with { At = r.At + new Vector2(0, y) }));
                y += height + PartGap;
                lines += (int)MathF.Ceiling(height / DropResultLayout.BodyStyle.LineHeight);
            }

            if (turn.Error is { Length: > 0 } error)
            {
                Add(new TextRun(error, ErrorStyle, width, 3));
            }
        }

        if (cut)
        {
            Add(new TextRun(Rest, DropResultLayout.RestStyle, width));
        }

        return (runs, Math.Max(0, y - PartGap));
    }

    private static string OneLine(string text) => string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));

    /// <summary>The source's icon from the icon set, drawn in the row's icon square.</summary>
    public static GlyphFigure[] Glyph(AiSource source) => source switch
    {
        AiSource.Palette => Glyphs.Ask,
        AiSource.Voice => Glyphs.Mic,
        AiSource.CircleToAsk => Glyphs.Region,
        AiSource.DropZone => Glyphs.File,
        AiSource.Search => Glyphs.Search,
        _ => Glyphs.Timer,
    };
}
