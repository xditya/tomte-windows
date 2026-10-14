using System.Drawing;
using System.Numerics;
using Tomte.Core;

namespace TextCaptures;

/// <summary>The AI drop zones on the drop surface and a zone's panel in the Palette state, from the island's own layouts.</summary>
internal static partial class Program
{
    private static List<Scene> DropZoneScenes()
    {
        const string sent = "Sent to ollama · llama3.2:3b";
        var summary = new DropResult("Summarize · Q3-report.pdf", sent, "Kept on the shelf.", false,
            "# Q3 report\n\n- Revenue went up 12% on the quarter, most of it from the northern region.\n- Costs held flat; hiring paused in August.\n- The board asks for a forecast by 15 November.",
            [new("Q3-report summary.md", ResultTile.OnShelf, 0x5D6D7E)], []);
        var tables = new DropResult("Tables to CSV · sales.xlsx.pdf", sent, "2 files kept on the shelf.", false, "The file has two tables.",
            [new("sales table 1.csv", ResultTile.OnShelf, 0x2E8B57), new("sales table 2.csv", ResultTile.OnShelf, 0x2E8B57)], []);
        var rename = new DropResult("Rename by content · scan0042.pdf", sent, "A copy is on the shelf. scan0042.pdf keeps its own name.", false,
            "Suggested name: Invoice October 2026 Nordic Paper.pdf", [new("Invoice October 2026 Nordic Paper.pdf", ResultTile.OnShelf, 0xC0392B)], []);
        var question = new DropResult("Summarize · server.log", "Nothing sent", "About 41,200 tokens (estimate), more than llama3.2:3b takes in one request (about 3,072).", false, "", [],
            ["Truncate: send the start of server.log, about 2,950 tokens (estimate).", "Chunk: send server.log in 14 parts, one request each, about 42,800 tokens in all (estimate).", "Cancel: send nothing."]);
        var failed = new DropResult("Summarize · photo.heic", "Nothing sent", "photo.heic is an image, and llama3.2:3b does not take images. Pick a model that reads images, or leave the file out. Nothing was sent.", true, "", [], []);
        var ask = new DropResult("Ask about it · Q3-report.pdf", "Nothing sent", "File · Q3-report.pdf is attached. Nothing is sent until you ask.", false, "Ask about Q3-report.pdf", [], []);
        return
        [
            new("shelf-drop-zones", IslandGeometry.ShelfDrop, c => Zones(c, null, DropZone.Summarize)),
            new("shelf-drop-zones-ask", IslandGeometry.ShelfDrop, c => Zones(c, null, DropZone.Ask)),
            new("shelf-drop-zones-skill", IslandGeometry.ShelfDrop, c => Zones(c, "invoice-parser", DropZone.Skill)),
            new("shelf-drop-zones-shelf-hot", IslandGeometry.ShelfDrop, c => Zones(c, "invoice-parser", DropZone.Shelf)),
            Result("zone-result-summary", summary),
            Result("zone-result-tables", tables),
            Result("zone-result-rename", rename),
            Result("zone-size-question", question),
            Result("zone-failed", failed),
            Result("zone-ask", ask),
        ];
    }

    // The surface as ShelfDropSurface builds it: the target and the zones, the one under the cursor lit, and its line.
    private static void Zones(Canvas c, string? skill, DropZone hot)
    {
        var g = IslandGeometry.ShelfDrop;
        var layout = DropZoneLayout.For("ollama", skill, g.Width, g.Depth)!;
        c.Pill();
        c.Mascot();
        Card(c, layout.Target, hot == DropZone.Shelf, 0x59FFFFFF);
        var (word, glyph) = layout.TargetText();
        if (glyph is { } at)
        {
            c.StrokeRect(new RectangleF(at.X, at.Y, 7, 4), 1.5f, 0x99FFFFFF);
            c.StrokeRect(new RectangleF(at.X, at.Y + 2, 16, 12), 2, 0x99FFFFFF);
        }

        c.Text(word.Run, word.At, Vector2.Zero);
        foreach (var (zone, rect) in layout.Zones)
        {
            Card(c, rect, zone == hot, zone == DropZone.Skill ? 0x59FFFFFFu : 0x1AFFFFFFu);
            var words = layout.ZoneText(zone, rect);
            c.Text(words.Run, words.At, Vector2.Zero);
        }

        c.Text(layout.LineText(hot), Vector2.Zero);
    }

    private static void Card(Canvas c, RectangleF r, bool hot, uint stroke)
    {
        if (hot)
        {
            c.RoundRect(r, 8, 0x296FA8FF);
        }

        c.StrokeRect(r, 8, hot ? 0xFF6FA8FF : stroke);
    }

    // A zone's panel as DropResultView builds it, in the palette's width.
    private static Scene Result(string name, DropResult result)
    {
        const float width = (float)PanelSize.PaletteWidth;
        var layout = new DropResultLayout(result, width, mascot: true);
        return new(name, IslandGeometry.Panel(width, layout.Height), c =>
        {
            var host = new Vector2(-width / 2, 0);
            c.Pill();
            c.Mascot();
            for (int i = 0; i < layout.Choices.Count; i++)
            {
                var r = Offset(layout.Choices[i], host);
                c.RoundRect(r, DropResultLayout.Corner, 0x0FFFFFFF);
                c.StrokeRect(r, DropResultLayout.Corner, i == layout.Choices.Count - 1 ? 0xFF6FA8FF : 0x59FFFFFF);
            }

            for (int i = 0; i < layout.Tiles.Count; i++)
            {
                c.RoundRect(Offset(layout.Tiles[i], host), DropResultLayout.Corner, 0x0FFFFFFF);
                c.RoundRect(Offset(DropResultLayout.Icon(layout.Tiles[i]), host), 6, 0xFF000000 | result.Tiles[i].Color);
            }

            foreach (var line in layout.Slots)
            {
                c.Text(line, host);
            }

            if (layout.Body is { } body)
            {
                c.Text(body.Run, body.At, host);
            }
        });
    }
}
