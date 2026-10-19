using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Text;

namespace TextCaptures;

/// <summary>The History tab, from the panel's own layout: off, empty, a list, and a row opened on its conversation.</summary>
internal static partial class Program
{
    private static readonly DateTimeOffset AskedAt = new(DateTime.Today.AddHours(9).AddMinutes(42));

    private static readonly AiExchange[] Conversations =
    [
        new("a", AiSource.Palette, AskedAt, "Anthropic", "claude-haiku-4-5", 0.0042m,
        [
            new("What does WS_EX_NOACTIVATE do to a window that the user clicks?", ["selection", "window:Code"], [],
                "It keeps the window from **becoming the active window** when it is clicked:\n\n- the click still reaches it\n- focus stays where it was\n- it never shows in Alt+Tab on its own"),
            new("And does it still get WM_LBUTTONDOWN?", [], [], "Yes. Mouse messages arrive as usual; only activation is skipped."),
        ]),
        new("b", AiSource.Voice, AskedAt.AddMinutes(-35), "Anthropic", "claude-haiku-4-5", 0.0008m,
            [new("turn the volume down to thirty", [], ["Set the volume to 30%: Volume 30%."], "")]),
        new("c", AiSource.CircleToAsk, AskedAt.AddHours(-3), "ollama", "llama3.2:3b", 0,
            [new("What is this error?", ["lasso"], [], "A missing semicolon on line 12.")]),
        new("kept:4", AiSource.DropZone, AskedAt.AddDays(-1), "ollama", "llama3.2:3b", 0,
            [new("Summarize: Q3-report.pdf", ["Q3-report.pdf"], [], "# Q3 report\n\n- Revenue went up 12% on the quarter.")]),
        new("d", AiSource.Job, AskedAt.AddDays(-2), "Anthropic", "claude-haiku-4-5", 0.0105m,
            [new("Morning notes: Read notes.txt and write a short summary of what is due today to the scratchpad", [], ["Read the shelf item notes.txt", "Add to the scratchpad: Dentist at 10.: Added."], "Done: one thing is due, the dentist at 10.")]),
        new("e", AiSource.Palette, AskedAt.AddDays(-6), "Anthropic", "claude-haiku-4-5", 0.0011m,
            [new("Rewrite this so it sounds less formal: We regret to inform you that the meeting has been moved.", ["selection"], [], "Heads up: the meeting has moved.")]),
    ];

    private static List<Scene> HistoryScenes() =>
    [
        new("history-off", Panel(PanelSize.History), c => History(c, on: false, [], null)),
        new("history-empty", Panel(PanelSize.History), c => History(c, on: true, [], null)),
        new("history-list", Panel(PanelSize.History), c => History(c, on: true, Conversations, null)),
        new("history-opened", Panel(PanelSize.History), c => History(c, on: true, Conversations, "a")),
    ];

    // The History panel as HistoryPanel builds it: heading, rows with their icons and two lines, the opened row's
    // conversation and buttons, all inside the list's clip, scrolled as opening the row scrolls it.
    private static void History(Canvas c, bool on, IReadOnlyList<AiExchange> rows, string? opened)
    {
        var layout = new HistoryPanelLayout(Body(PanelSize.History), rows, opened);
        var body = BodyOrigin(PanelSize.History);
        c.Pill();
        c.Mascot();
        Tabs(c, PanelId.History, PanelSize.History);
        foreach (var line in layout.HeadText(on))
        {
            c.Text(line, body);
        }

        if (!on || rows.Count == 0)
        {
            c.StrokeRect(Offset(layout.List, body), HistoryPanelLayout.Corner, 0x2EFFFFFF);
            foreach (var line in layout.BoxText(on))
            {
                c.Text(line, body);
            }

            return;
        }

        foreach (var line in layout.FootText())
        {
            c.Text(line, body);
        }

        float scroll = layout.Opened >= 0 ? layout.Reveal(0, layout.Opened) : 0;
        var list = layout.List;
        var origin = body + new Vector2(list.X, list.Y - scroll);
        c.Clip = Offset(list, body);
        for (int i = 0; i < rows.Count; i++)
        {
            var card = layout.Rows[i];
            if (card.Bottom < scroll || card.Y > scroll + list.Height)
            {
                continue;
            }

            // Shapes are not clipped on the canvas, so a card is drawn only as far as the list shows it.
            var shown = RectangleF.Intersect(Offset(card, origin), Offset(list, body));
            c.RoundRect(shown, HistoryPanelLayout.Corner, i == layout.Opened ? 0x1CFFFFFFu : 0x0FFFFFFFu);
            var rowAt = origin + new Vector2(card.X, card.Y);
            var icon = Offset(HistoryPanelLayout.IconIn, rowAt);
            if (icon.Y >= list.Y + body.Y && icon.Bottom <= list.Bottom + body.Y)
            {
                c.RoundRect(icon, 6, 0x1CFFFFFF);
                var at = new Vector2(icon.X + (icon.Width - HistoryPanelLayout.IconGlyph) / 2, icon.Y + (icon.Height - HistoryPanelLayout.IconGlyph) / 2);
                // Simplification: drawn at the icon set's own 24 DIP box, larger than the island draws it.
                c.Figures(HistoryPanelLayout.Glyph(rows[i].Source), at, 0x99FFFFFF);
            }

            foreach (var line in HistoryPanelLayout.RowText(rows[i], card.Width, opened: i == layout.Opened))
            {
                c.Text(line, rowAt);
            }
        }

        foreach (var (run, at) in layout.Conversation)
        {
            c.Text(run, at, origin);
        }

        foreach (var button in new[] { layout.ContinueButton, layout.CopyButton }.Where(b => b.Height > 0))
        {
            c.RoundRect(Offset(button, origin), HistoryPanelLayout.Corner, 0x1CFFFFFF);
        }

        foreach (var label in layout.ButtonLabels())
        {
            c.Text(label, origin);
        }

        c.Clip = null;
    }
}
