using System.Drawing;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Search;
using Tomte.Core.Shortcuts;
using Tomte.Core.Text;
using Tomte.Shell;

namespace TextCaptures;

/// <summary>The search box, from its own layout: empty, what was opened before, results with the first app selected, a selection scrolled to, and the Get Everything card.</summary>
internal static partial class Program
{
    private static readonly string WinDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

    // Icons from real places on any Windows PC, so the captures show what the shell hands the rows.
    private static readonly SearchItem[] CodeRows =
    [
        new(SearchKind.App, "Code Writer", "app:1", "", Path.Combine(WinDir, "notepad.exe")),
        new(SearchKind.App, "Codec Settings", "app:2", "", Path.Combine(WinDir, "System32", "control.exe")),
        new(SearchKind.App, "Command Prompt", "app:3", "", Path.Combine(WinDir, "System32", "cmd.exe")),
        new(SearchKind.Shortcut, "Code projects", "4", @"D:\Projects\code", Path.Combine(WinDir, "Web"), @"D:\Projects\code"),
        new(SearchKind.File, "code review notes.md", @"D:\Work\code review notes.md", @"D:\Work", Path.Combine(WinDir, "win.ini"), @"D:\Work\code review notes.md"),
        new(SearchKind.Folder, "code", @"D:\Projects\code", @"D:\Projects", Path.Combine(WinDir, "Web"), @"D:\Projects\code"),
        new(SearchKind.File, "Codes and passwords for the very long named family accounts spreadsheet.xlsx", @"D:\Home\Codes.xlsx", @"D:\Home\Paperwork\Accounts\Family\2026\Shared with everyone in the house", Path.Combine(WinDir, "win.ini"), @"D:\Home\Codes.xlsx"),
        new(SearchKind.GetEverything, "Search every drive", "", "Get Everything, free, from voidtools"),
        new(SearchKind.Ask, "Ask Tomte", "code", "\"code\""),
    ];

    private static readonly SearchItem[] OpenedRows =
    [
        new(SearchKind.App, "Notepad", "app:n", "", Path.Combine(WinDir, "notepad.exe")),
        new(SearchKind.App, "Command Prompt", "app:3", "", Path.Combine(WinDir, "System32", "cmd.exe")),
        new(SearchKind.Folder, "Web", Path.Combine(WinDir, "Web"), WinDir, Path.Combine(WinDir, "Web")),
    ];

    private static readonly SearchCard GetEverything = new(
        "Get Everything for whole-disk search",
        "Tomte installs Everything (free, from voidtools) with winget. Windows asks for permission once. Everything then runs in the background, indexes every drive and starts when you sign in. Not now hides this row; Settings, Hotkeys keeps a Get Everything button.",
        [("notnow", "Not now"), ("install", "Install")]);

    private static List<Scene> SearchScenes() =>
    [
        SearchScene("search-empty", "", [], -1, SearchLayout.NothingYet, "Files: Windows index"),
        SearchScene("search-opened-before", "", OpenedRows, 0, SearchLayout.NothingYet, "Files: Everything"),
        SearchScene("search-results", "code", CodeRows, 0, SearchLayout.NoResults, "Files: Windows index"),
        SearchScene("search-results-last", "code", CodeRows, CodeRows.Length - 1, SearchLayout.NoResults, "Files: Windows index"),
        SearchScene("search-nothing", "qqzx", [], -1, SearchLayout.NoResults, "Files: Everything"),
        // The card shows over a box that grew for the results first; it never shrinks while open.
        SearchScene("search-get-everything", "code", CodeRows, 7, SearchLayout.NoResults, "", GetEverything, grown: CodeRows),
        // The tallest at 150% on a 1080p screen: half of 720 DIPs.
        SearchScene("search-results-short", "code", CodeRows, 6, SearchLayout.NoResults, "Files: Windows index", tallest: SearchLayout.HeightFor(720)),
        // AI search: its row first for a sentence, its progress, and what it picked under its line on why.
        SearchScene("search-ai-row", Sentence, AiRows, 0, SearchLayout.NoResults, "Files: Everything"),
        SearchScene("search-ai-running", Sentence, [], -1, "Searching with AI. Searched files: invoice, files, from 2026-09-01, to 2026-09-30. Esc goes back.",
            "Sending to Anthropic · claude-haiku-5-5", grown: AiRows, ai: true),
        SearchScene("search-ai-results", Sentence, AiPicked, 0, "", "Sent to Anthropic · claude-haiku-5-5", grown: AiRows, ai: true,
            lead: "AI · The September invoice is invoice-2026-09-globex.pdf, and the Invoices folder in Work holds the rest of your invoices."),
        // Places: under an app that matches, then first for "display settings", and Tomte's own beside Windows'.
        SearchScene("search-places", "sound", PlaceRows("sound"), 0, SearchLayout.NoResults, "Files: Everything"),
        SearchScene("search-places-first", "display settings", PlaceRows("display settings"), 0, SearchLayout.NoResults, "Files: Everything"),
        SearchScene("search-places-tomte", "clipboard", PlaceRows("clipboard"), 1, SearchLayout.NoResults, "Files: Everything"),
        // The show in folder button on a selected file, and under the pointer with its state layer and tooltip.
        SearchScene("search-folder", "code", CodeRows, 4, SearchLayout.NoResults, "Files: Everything"),
        SearchScene("search-folder-tip", "code", CodeRows, 4, SearchLayout.NoResults, "Files: Everything", folderTip: true),
    ];

    // The rows a search gives with two apps beside the places, and Ask. Tomte's places take this program's icon here.
    private static IReadOnlyList<SearchItem> PlaceRows(string typed)
    {
        SearchItem[] apps = [new(SearchKind.App, "Sound Recorder", "app:sr", "", Path.Combine(WinDir, "System32", "SoundRecorder.exe")), new(SearchKind.App, "Settings", "app:s", "", Places.SettingsApp)];
        return [.. SearchRank.Order(apps.Concat(Places.Rows(apps)), typed, new Dictionary<string, Launches>()), new(SearchKind.Ask, "Ask Tomte", typed, $"\"{typed}\"")];
    }

    private const string Sentence = "the invoices I got last month";

    private static readonly SearchItem[] AiRows =
    [
        new(SearchKind.Ask, "Ask Tomte", Sentence, $"\"{Sentence}\""),
        new(SearchKind.AiSearch, SearchLayout.AiRow, Sentence, "Sends file names to Anthropic, never what is in them"),
    ];

    private static readonly SearchItem[] AiPicked =
    [
        new(SearchKind.File, "invoice-2026-09-globex.pdf", @"D:\Work\Invoices\invoice-2026-09-globex.pdf", @"D:\Work\Invoices", Path.Combine(WinDir, "win.ini"), @"D:\Work\Invoices\invoice-2026-09-globex.pdf"),
        new(SearchKind.Folder, "Invoices", @"D:\Work\Invoices", @"D:\Work", Path.Combine(WinDir, "Web"), @"D:\Work\Invoices"),
    ];

    // A scene at the height the view grows to for these rows (or for <paramref name="grown"/>, shown earlier).
    private static Scene SearchScene(string name, string typed, IReadOnlyList<SearchItem> rows, int select, string hint, string source, SearchCard? card = null, IReadOnlyList<SearchItem>? grown = null, float tallest = (float)PanelSize.PaletteMaxHeight, bool ai = false, string? lead = null, bool folderTip = false)
    {
        float height = Math.Max(SearchLayout.Fit((float)PanelSize.PaletteWidth, true, rows, card, tallest, lead), SearchLayout.Fit((float)PanelSize.PaletteWidth, true, grown ?? [], null, tallest));
        var geometry = IslandGeometry.Panel(PanelSize.PaletteWidth, height);
        return new(name, geometry, c => Search(c, typed, rows, select, hint, source, card, height, ai, lead, folderTip ? geometry : null));
    }

    // The search box as SearchView builds it: the box with the typed text or the placeholder, the footer, then the list
    // with a heading per group (or AI search's line above its rows), the selected row filled and outlined and kept in view,
    // with its show in folder button when it is a file, or the card in the list's place. With <paramref name="tipIn"/> the
    // pointer is on that button: the host's state layer over it and its tooltip.
    private static void Search(Canvas c, string typed, IReadOnlyList<SearchItem> rows, int select, string hint, string source, SearchCard? card = null, float height = (float)PanelSize.PaletteMaxHeight, bool ai = false, string? lead = null, IslandGeometry? tipIn = null)
    {
        var origin = new Vector2(-(float)PanelSize.PaletteWidth / 2, 0);
        var layout = new SearchLayout((float)PanelSize.PaletteWidth, mascot: true, height);
        c.Pill();
        c.Mascot();
        c.RoundRect(Offset(layout.Box, origin), PaletteLayout.PromptCorner, 0x0FFFFFFF);
        c.Stroke(Offset(layout.Box, origin), PaletteLayout.PromptCorner, PaletteLayout.PromptFocusLine, 1);
        var box = layout.Editor;
        if (typed.Length == 0)
        {
            c.Text(SearchLayout.Placeholder, PaletteLayout.Placeholder, box, origin);
        }
        else
        {
            var editor = Typing.Captured(typed, PaletteLayout.PromptSize, box, c.Scale, typed.Length);
            editor.Resize(PixelGrid.Cover(box.Width, c.Scale), PixelGrid.Cover(box.Height, c.Scale), EditorScale.Zoom(c.Scale, TextServices.SystemDpi));
            c.Editor(editor, box, origin);
            editor.Dispose();
        }

        var (left, right) = layout.Footer(card is not null ? "" : ai ? SearchLayout.AiKeys : typed.Length > 0 ? SearchLayout.Keys : SearchLayout.KeysEmpty, card is null ? source : "");
        c.Text(left, origin);
        c.Text(right, origin);
        var band = layout.Band;
        if (card is not null)
        {
            var (at, title, body, bodyAt, buttons) = layout.Card(card);
            c.RoundRect(Offset(at, origin), PaletteLayout.PromptCorner, 0x0FFFFFFF);
            c.Stroke(Offset(at, origin), PaletteLayout.PromptCorner, PaletteLayout.AskOutline, 1);
            c.Text(title, origin);
            c.Text(body, new Vector2(bodyAt.X, bodyAt.Y), origin);
            for (int i = 0; i < buttons.Count; i++)
            {
                c.RoundRect(Offset(buttons[i].Box, origin), PaletteLayout.ButtonCorner, i == card.Focus ? PaletteLayout.RunFill : PaletteLayout.ButtonFill);
                c.Text(buttons[i].Label, origin);
            }

            return;
        }

        if (rows.Count == 0)
        {
            c.Text(new TextRun(hint, TextStyle.Secondary, band.Width - (2 * SearchLayout.RowPadX), 3), new Vector2(band.X + SearchLayout.RowPadX, band.Y + 8), origin);
            return;
        }

        var lines = SearchLayout.Lines(rows, lead);
        var chosen = lines.First(l => l.Heading is null && l.Row == Math.Max(0, select));
        float content = lines[^1].Top + lines[^1].Height;
        float scroll = layout.ScrollTo(0, chosen.Top, chosen.Top + chosen.Height, content);
        var list = origin + new Vector2(band.X, band.Y - scroll);
        int pixels = (int)Math.Round(SearchLayout.IconSize * c.Scale);
        foreach (var line in lines)
        {
            // Shapes are not clipped on the canvas, so only the lines the band shows whole are drawn.
            if (line.Top < scroll || line.Top + line.Height > scroll + band.Height)
            {
                continue;
            }

            if (line.Heading is { } heading)
            {
                c.Text(heading, SearchLayout.Heading, new RectangleF(SearchLayout.RowPadX, line.Top, band.Width - (2 * SearchLayout.RowPadX), line.Height), list);
                continue;
            }

            var item = rows[line.Row];
            if (line.Row == select)
            {
                var fill = Offset(new RectangleF(0.5f, line.Top + 0.5f, band.Width - 1, SearchLayout.RowHeight - 1), list);
                c.RoundRect(fill, SearchLayout.RowCorner, SearchLayout.SelectedFill);
                c.Stroke(fill, SearchLayout.RowCorner, SearchLayout.SelectedLine, 1);
            }

            var (_, icon, name, detail, kind) = layout.Row(line.Top, item);
            if (item.Icon is { } from && ShellIcon.For(from, pixels) is { } image)
            {
                c.Picture(Offset(icon, list), 0, image.Pixels, image.Width, image.Height);
            }

            c.Text(name, list);
            c.Text(detail, list);
            bool folder = line.Row == select && SearchLayout.HasFolder(item);
            c.Text(folder ? kind with { Text = "" } : kind, list);
            if (folder)
            {
                var (button, glyph) = layout.Folder(line.Top);
                c.RoundRect(Offset(button, list), SearchLayout.FolderCorner, SearchLayout.FolderFill);
                c.Figures(SearchLayout.FolderGlyph, new Vector2(glyph.X, glyph.Y) + list, SearchLayout.FolderInk);
                if (tipIn is { } g)
                {
                    c.RoundRect(Offset(button, list), SearchLayout.FolderCorner, StateLook.Hover);
                    string tip = new Target("folder", button, SearchLayout.FolderCorner, TargetKind.Button, Tip: SearchLayout.FolderTip, Key: SearchLayout.FolderKey).TipWith(keys: true)!;
                    var (tipCard, _, label) = StateLook.Tip(tip, false, Offset(button, list), new RectangleF((float)(-g.Width / 2), 0, (float)g.Width, (float)g.Depth));
                    c.RoundRect(tipCard, StateLook.TipCorner, StateLook.TipFill);
                    c.StrokeRect(tipCard, StateLook.TipCorner, StateLook.TipLine);
                    c.Text(label, Vector2.Zero);
                }
            }
        }
    }
}
