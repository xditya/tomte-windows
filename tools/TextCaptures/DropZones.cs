using System.Drawing;
using System.Numerics;
using Tomte.Core;

namespace TextCaptures;

/// <summary>The Shelf-drop pill, the drop zones a hold on it opens, and a zone's panel in the Palette state, from the island's own layouts.</summary>
internal static partial class Program
{
    private static List<Scene> DropZoneScenes()
    {
        const string sent = "Sent to ollama · llama3.2:3b";
        var summary = new DropResult("Summarize · Q3-report.pdf", sent, "Kept on the shelf.", false,
            "# Q3 report\n\n- Revenue went up 12% on the quarter, most of it from the northern region.\n- Costs held flat; hiring paused in August.\n- The board asks for a forecast by 15 November.",
            [new("Q3-report summary.md", ResultTile.OnShelf, 0x5D6D7E)], []);
        var tables = new DropResult("Tables to CSV · sales.xlsx.pdf", sent, "2 files kept on the shelf.", false, "The file has two tables.\n\n```csv\nRegion,Q3,Q4\nNorth,\"1,204\",\"1,310\"\nSouth,980,1012\n```",
            [new("sales table 1.csv", ResultTile.OnShelf, 0x2E8B57), new("sales table 2.csv", ResultTile.OnShelf, 0x2E8B57)], []);
        var rename = new DropResult("Rename by content · scan0042.pdf", sent, "A copy is on the shelf. scan0042.pdf keeps its own name.", false,
            "Suggested name: Invoice October 2026 Nordic Paper.pdf", [new("Invoice October 2026 Nordic Paper.pdf", ResultTile.OnShelf, 0xC0392B)], []);
        var question = new DropResult("Summarize · server.log", "Nothing sent", "About 41,200 tokens (estimate), more than llama3.2:3b takes in one request (about 3,072).", false, "", [],
            ["Truncate: send the start of server.log, about 2,950 tokens (estimate).", "Chunk: send server.log in 14 parts, one request each, about 42,800 tokens in all (estimate).", "Cancel: send nothing."]);
        var failed = new DropResult("Summarize · photo.heic", "Nothing sent", "photo.heic is an image, and llama3.2:3b does not take images. Pick a model that reads images, or leave the file out. Nothing was sent.", true, "", [], []);
        var longOne = new DropResult("Summarize · board-minutes-2026-10.docx", sent, "Kept on the shelf.", false, LongAnswer,
            [new("board-minutes-2026-10 summary.md", ResultTile.OnShelf, 0x5D6D7E)], []);
        // As DropZoneRequest.Kept builds it, for a summary kept from an earlier drop of the same file.
        var made = new KeptResult(DropZone.Summarize, null, "Q3-report.pdf", @"C:\Users\a\Q3-report.pdf", "api.anthropic.com", "claude-haiku-4-5", 0.0031m,
            new DateTimeOffset(2026, 10, 14, 9, 42, 0, TimeSpan.Zero), summary.Body, []);
        var kept = new DropResult("Summarize · Q3-report.pdf", "Nothing sent", made.Line(System.Globalization.CultureInfo.InvariantCulture), false, made.Answer,
            summary.Tiles, ["Run again: send Q3-report.pdf to claude-haiku-4-5.", "Close: nothing is sent."]);
        var ask = new DropResult("Ask about it · Q3-report.pdf", "Nothing sent", "File · Q3-report.pdf is attached. Nothing is sent until you ask.", false, "Ask about Q3-report.pdf", [], []);
        return
        [
            new("shelf-drop-pill", IslandGeometry.ShelfDrop, c => DropPill(c, hint: true)),
            new("shelf-drop-pill-no-provider", IslandGeometry.ShelfDrop, c => DropPill(c, hint: false)),
            Zones("shelf-drop-zones", null, DropZone.Summarize, DropZoneLayout.Width),
            Zones("shelf-drop-zones-ask", null, DropZone.Ask, DropZoneLayout.Width),
            Zones("shelf-drop-zones-skill", "invoice-parser", DropZone.Skill, DropZoneLayout.Width),
            Zones("shelf-drop-zones-narrow", null, DropZone.Shelf, 820),
            Result("zone-result-summary", summary),
            Result("zone-result-long", longOne),
            Result("zone-result-long-scrolled", longOne, scrolled: 1),
            Result("zone-result-kept", kept),
            Result("zone-result-tables", tables),
            Result("zone-result-rename", rename),
            Result("zone-size-question", question),
            Result("zone-failed", failed),
            Result("zone-ask", ask),
        ];
    }

    // The pill as ShelfDropSurface builds it: the accent ring inside the outline, the tray glyph, the label and the hint.
    private static void DropPill(Canvas c, bool hint)
    {
        var g = IslandGeometry.ShelfDrop;
        float inset = DropZoneLayout.RingInset;
        c.Pill();
        c.Mascot();
        c.Stroke(new RectangleF((float)(-g.Width / 2) + inset, inset, (float)g.Width - (2 * inset), (float)g.Depth - (2 * inset)), ((float)g.Depth / 2) - inset, Theme.Accent, DropZoneLayout.RingWidth);
        var (glyph, label, more) = DropZoneLayout.Pill(hint);
        c.Figures(Glyphs.Shelf, new Vector2(glyph.X, glyph.Y), Theme.Accent);
        c.Text(label, Vector2.Zero);
        c.Text(more, Vector2.Zero);
    }

    // The zones as ShelfDropSurface builds them, the one under the cursor lifted and filled with the accent.
    private static Scene Zones(string name, string? skill, DropZone hot, float width)
    {
        var layout = DropZoneLayout.For("Anthropic", skill, width)!;
        return new(name, IslandGeometry.Panel(width, layout.Height), c =>
        {
            c.Pill();
            c.Mascot(shift: DropZoneLayout.Seat.Shift);
            foreach (var line in layout.Head("cover.png", DropZoneLayout.Facts(1_258_291, 1090), mascot: true))
            {
                c.Text(line, Vector2.Zero);
            }

            foreach (var (zone, rect) in layout.Zones)
            {
                bool lit = zone == hot, empty = zone == DropZone.Skill && layout.SlotEmpty;
                var r = lit ? rect with { Y = rect.Y - DropZoneLayout.Lift } : rect;
                if (empty)
                {
                    c.StrokeRect(r, DropZoneLayout.TileCorner, Theme.Dash);
                }
                else
                {
                    c.RoundRect(r, DropZoneLayout.TileCorner, lit ? Theme.Accent : Theme.Card);
                }

                var host = new Vector2(r.X, r.Y);
                var (glyph, label, sub) = layout.TileText(zone, r.Size);
                c.Figures(empty ? Glyphs.Plus : zone == DropZone.Shelf ? Glyphs.Shelf : Glyphs.Summarize, host + new Vector2(glyph.X, glyph.Y), lit ? Theme.OnAccent : Theme.Ink);
                c.Text(lit ? label with { Style = label.Style with { Color = Theme.OnAccent } } : label, host);
                c.Text(lit ? sub with { Style = sub.Style with { Color = Theme.OnAccent } } : sub, host, lit ? 0.8f : 1);
            }

            c.Text(layout.FootSlot, Vector2.Zero);
        });
    }

    // What a model sends back: a heading, bold, nested bullets, a list past nine, a word longer than the panel, code.
    private const string LongAnswer = """
        # Board minutes, October 2026

        The board met on **14 October** with all seven members present. The main points:

        - **Budget:** the 2027 budget passed with one change: travel is cut by 15%, and the saving goes to hiring.
        - **Hiring:** two engineers and one designer start in January.
          - The designer works on the mobile app first.
          - Both engineers join the platform team.
        - **Office:** the lease runs out in March; three offers are on the table.

        ## Decisions

        1. Accept the budget as changed.
        2. Ask for a second quote for the office move.
        3. Hold the next meeting on 11 November.
        4. Publish these minutes on the intranet.
        5. Send the forecast to the bank.
        6. Review the travel policy.
        7. Name a data protection lead.
        8. Renew the insurance.
        9. Close the old bank account.
        10. Thank the outgoing chair.

        Reference: https://intranet.example.com/board/2026/october/minutes/final-version-approved-by-the-chair-and-the-secretary.docx

        ```
        vote: 6 for, 1 against
        ```

        Next steps are with the secretary.
        """;

    // A zone's panel as DropResultView builds it, in the palette's width, the answer scrolled a fraction of the way down.
    private static Scene Result(string name, DropResult result, float scrolled = 0)
    {
        const float width = (float)PanelSize.PaletteWidth;
        var layout = new DropResultLayout(result, width, mascot: true);
        return new(name, IslandGeometry.Panel(width, layout.Height), c =>
        {
            var host = new Vector2(-width / 2, 0);
            c.Pill();
            c.Mascot(shift: DropResultLayout.Seat.Shift);
            for (int i = 0; i < layout.Choices.Count; i++)
            {
                var r = Offset(layout.Choices[i], host);
                if (i == layout.Choices.Count - 1)
                {
                    c.RoundRect(r, DropResultLayout.Corner, Tomte.Core.Interaction.StateLook.Accent);
                }
                else
                {
                    c.RoundRect(r, DropResultLayout.Corner, Theme.Raised);
                }
            }

            for (int i = 0; i < layout.Tiles.Count; i++)
            {
                c.RoundRect(Offset(layout.Tiles[i], host), DropResultLayout.TileCorner, Theme.Card);
                c.RoundRect(Offset(DropResultLayout.Icon(layout.Tiles[i]), host), DropResultLayout.IconCorner, 0xFF000000 | result.Tiles[i].Color);
            }

            foreach (var line in layout.Slots)
            {
                c.Text(line, host);
            }

            var band = layout.Band;
            c.Clip = Offset(band, host);
            foreach (var (run, at) in layout.Body)
            {
                c.Text(run, new Vector2(at.X, band.Y + at.Y - layout.MaxScroll * scrolled), host);
            }

            c.Clip = null;
        });
    }
}
