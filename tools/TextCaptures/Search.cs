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
        WebSearch.Row("code", WebSearchEngine.Google),
        new(SearchKind.Ask, "Ask Tomte about “code”", "code"),
    ];

    private static readonly SearchItem[] OpenedRows =
    [
        new(SearchKind.App, "Notepad", "app:n", "", Path.Combine(WinDir, "notepad.exe")),
        new(SearchKind.App, "Command Prompt", "app:3", "", Path.Combine(WinDir, "System32", "cmd.exe")),
        new(SearchKind.Folder, "Web", Path.Combine(WinDir, "Web"), WinDir, Path.Combine(WinDir, "Web")),
    ];

    private static readonly SearchCard GetEverything = new(
        "Get Everything for whole-disk search",
        "Tomte installs Everything (free, from voidtools) with winget. Windows asks for permission once. Everything then runs in the background, indexes every drive and starts when you sign in. Not now hides this row; Settings, Search keeps a Get Everything button.",
        [("notnow", "Not now"), ("install", "Install")]);

    private static List<Scene> SearchScenes() =>
    [
        SearchScene("search-empty", "", [], -1, SearchLayout.NothingYet, "Windows index"),
        SearchScene("search-opened-before", "", OpenedRows, 0, SearchLayout.NothingYet, "Everything"),
        SearchScene("search-results", "code", CodeRows, 0, SearchLayout.NoResults, "Windows index"),
        SearchScene("search-results-last", "code", CodeRows, CodeRows.Length - 1, SearchLayout.NoResults, "Windows index"),
        SearchScene("search-nothing", "qqzx", [], -1, SearchLayout.NoResults, "Everything"),
        // The card shows over a box that grew for the results first; it never shrinks while open.
        SearchScene("search-get-everything", "code", CodeRows, 7, SearchLayout.NoResults, "", GetEverything, grown: CodeRows),
        // The tallest at 150% on a 1080p screen: half of 720 DIPs.
        SearchScene("search-results-short", "code", CodeRows, 6, SearchLayout.NoResults, "Windows index", tallest: SearchLayout.HeightFor(720)),
        // AI search: its row first for a sentence, its progress, and what it picked under its line on why.
        SearchScene("search-ai-row", Sentence, AiRows, 0, SearchLayout.NoResults, "Everything"),
        SearchScene("search-ai-running", Sentence, [], -1, "Searching with AI. Searched files: invoice, files, from 2026-09-01, to 2026-09-30. Esc goes back.",
            "Sending to Anthropic · claude-haiku-5-5", grown: AiRows, ai: true),
        SearchScene("search-ai-results", Sentence, AiPicked, 0, "", "Sent to Anthropic · claude-haiku-5-5", grown: AiRows, ai: true,
            lead: "AI · The September invoice is invoice-2026-09-globex.pdf, and the Invoices folder in Work holds the rest of your invoices."),
        // Places: under an app that matches, then first for "display settings", and Tomte's own beside Windows'.
        SearchScene("search-places", "sound", PlaceRows("sound"), 0, SearchLayout.NoResults, "Everything"),
        SearchScene("search-places-first", "display settings", PlaceRows("display settings"), 0, SearchLayout.NoResults, "Everything"),
        SearchScene("search-places-tomte", "clipboard", PlaceRows("clipboard"), 1, SearchLayout.NoResults, "Everything"),
        // A selected file, and the show in folder button under the pointer with its state layer and tooltip.
        SearchScene("search-folder", "code", CodeRows, 4, SearchLayout.NoResults, "Everything"),
        SearchScene("search-folder-tip", "code", CodeRows, 4, SearchLayout.NoResults, "Everything", folderTip: true),
        // Open in browser: searching the web for the words, and opening the address they are.
        SearchScene("search-web", "code", CodeRows, 8, SearchLayout.NoResults, "Everything"),
        SearchScene("search-web-address", "example.com/docs", [WebSearch.Row("example.com/docs", WebSearchEngine.Google), new(SearchKind.Ask, "Ask Tomte about “example.com/docs”", "example.com/docs")], 0, SearchLayout.NoResults, "Everything"),
    ];

    // The rows a search gives with two apps beside the places, and Ask. Tomte's places take this program's icon here.
    private static IReadOnlyList<SearchItem> PlaceRows(string typed)
    {
        SearchItem[] apps = [new(SearchKind.App, "Sound Recorder", "app:sr", "", Path.Combine(WinDir, "System32", "SoundRecorder.exe")), new(SearchKind.App, "Settings", "app:s", "", Places.SettingsApp)];
        return [.. SearchRank.Order(apps.Concat(Places.Rows(apps)), typed, new Dictionary<string, Launches>()), new(SearchKind.Ask, $"Ask Tomte about “{typed}”", typed)];
    }

    private const string Sentence = "the invoices I got last month";

    private static readonly SearchItem[] AiRows =
    [
        new(SearchKind.Ask, $"Ask Tomte about “{Sentence}”", Sentence),
        new(SearchKind.AiSearch, SearchLayout.AiRow, Sentence, "Sends file names to Anthropic, never what is in them"),
        WebSearch.Row(Sentence, WebSearchEngine.Google),
    ];

    private static readonly SearchItem[] AiPicked =
    [
        new(SearchKind.File, "invoice-2026-09-globex.pdf", @"D:\Work\Invoices\invoice-2026-09-globex.pdf", @"D:\Work\Invoices", Path.Combine(WinDir, "win.ini"), @"D:\Work\Invoices\invoice-2026-09-globex.pdf"),
        new(SearchKind.Folder, "Invoices", @"D:\Work\Invoices", @"D:\Work", Path.Combine(WinDir, "Web"), @"D:\Work\Invoices"),
    ];

    // A scene at the height the view grows to for these rows (or for <paramref name="grown"/>, shown earlier).
    private static Scene SearchScene(string name, string typed, IReadOnlyList<SearchItem> rows, int select, string hint, string source, SearchCard? card = null, IReadOnlyList<SearchItem>? grown = null, float tallest = (float)PanelSize.PaletteMaxHeight, bool ai = false, string? lead = null, bool folderTip = false)
    {
        const float Width = (float)PanelSize.SearchWidth;
        bool hit = typed.Length > 0;
        float height = Math.Max(SearchLayout.Fit(Width, true, rows, card, tallest, lead, hit), SearchLayout.Fit(Width, true, grown ?? [], null, tallest, null, hit));
        var geometry = IslandGeometry.Panel(Width, height);
        return new(name, geometry, c => Search(c, typed, rows, select, hint, source, card, height, ai, lead, folderTip ? geometry : null));
    }

    // The search box as SearchView builds it: the field with the magnifier, the typed text or the placeholder and where
    // files come from, the key hints, then the list with the top hit, a heading per group (or AI search's line above its
    // rows) and the rule before the AI rows, the selected row raised and kept in view, or the card in the list's place.
    // With <paramref name="tipIn"/> the pointer is on the selected file's show in folder button: the host's state layer
    // over it and its tooltip.
    private static void Search(Canvas c, string typed, IReadOnlyList<SearchItem> rows, int select, string hint, string source, SearchCard? card = null, float height = (float)PanelSize.PaletteMaxHeight, bool ai = false, string? lead = null, IslandGeometry? tipIn = null)
    {
        const float Width = (float)PanelSize.SearchWidth;
        var origin = new Vector2(-Width / 2, 0);
        var layout = new SearchLayout(Width, mascot: true, height);
        c.Pill();
        c.Mascot(shift: SearchLayout.Seat.Shift);
        c.RoundRect(Offset(layout.Box, origin), SearchLayout.BoxCorner, Theme.Card);
        var glass = Offset(layout.Glass, origin);
        c.Glyph(Glyphs.Search, new Vector2(glass.X, glass.Y), SearchLayout.GlassSize, Theme.Ink2);
        string shownSource = card is null ? source : "";
        var (dot, status) = layout.Source(shownSource);
        if (!dot.IsEmpty)
        {
            c.Circle(new Vector2(dot.X + (dot.Width / 2), dot.Y + (dot.Height / 2)) + origin, dot.Width / 2, SearchLayout.SourceDot(shownSource));
        }

        c.Text(status, origin);
        var box = layout.EditorFor(shownSource);
        if (typed.Length == 0)
        {
            c.Text(SearchLayout.PlaceholderText, SearchLayout.Placeholder, box, origin);
        }
        else
        {
            var editor = Typing.Captured(typed, SearchLayout.BoxText, box, c.Scale, typed.Length);
            editor.Resize(PixelGrid.Cover(box.Width, c.Scale), PixelGrid.Cover(box.Height, c.Scale), EditorScale.Zoom(c.Scale, TextServices.SystemDpi));
            c.Editor(editor, box, origin);
            editor.Dispose();
        }

        if (card is null)
        {
            var (left, right) = SearchLayout.Keys(ai, typed.Length > 0, back: false);
            foreach (var (cap, keys, word) in layout.Footer(left, right))
            {
                Keycap(c, cap, keys, origin);
                c.Text(word, origin);
            }
        }

        var band = layout.Band;
        if (card is not null)
        {
            var (at, title, body, bodyAt, buttons) = layout.Card(card);
            c.RoundRect(Offset(at, origin), SearchLayout.BoxCorner, Theme.Card);
            c.Stroke(Offset(at, origin), SearchLayout.BoxCorner, PaletteLayout.AskOutline, 1);
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

        var lines = SearchLayout.Lines(rows, lead, typed.Length > 0);
        var chosen = lines.First(l => l.Heading is null && !l.Rule && l.Row == Math.Max(0, select));
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

            if (line.Rule)
            {
                c.RoundRect(Offset(new RectangleF(SearchLayout.RowPadX, line.Top + SearchLayout.RuleInset, band.Width - (2 * SearchLayout.RowPadX), 1), list), 0, SearchLayout.RuleColor);
                continue;
            }

            if (line.Heading is { } heading)
            {
                c.Text(heading, heading == lead ? SearchLayout.Lead : SearchLayout.Heading, new RectangleF(SearchLayout.RowPadX, line.Top, band.Width - (2 * SearchLayout.RowPadX), line.Height), list);
                continue;
            }

            var item = rows[line.Row];
            var laid = layout.Row(line, item, line.Row == select);
            if (line.Row == select)
            {
                c.RoundRect(Offset(laid.Row, list), laid.Corner, SearchLayout.SelectedFill);
            }

            var badge = laid.Badge;
            uint fill = SearchLayout.BadgeFill(item);
            if (fill != 0)
            {
                c.RoundRect(Offset(badge, list), laid.BadgeCorner, fill);
            }

            if (SearchLayout.BadgeText(item) is { } kind)
            {
                c.Text(kind, line.Hit ? SearchLayout.HitBadgeText : SearchLayout.RowBadgeText, badge, list);
            }
            else if (item.Icon is { } from && item.Kind is not (SearchKind.Folder or SearchKind.Ask or SearchKind.AiSearch) && ShellIcon.For(from, pixels) is { } image)
            {
                float inset = (badge.Width - SearchLayout.IconSize) / 2;
                c.Picture(Offset(new RectangleF(badge.X + inset, badge.Y + inset, SearchLayout.IconSize, SearchLayout.IconSize), list), 0, image.Pixels, image.Width, image.Height);
            }
            else
            {
                var (glyph, ink) = SearchLayout.BadgeGlyph(item);
                float size = fill == 0 || line.Hit ? 18 : 15;
                c.Glyph(glyph, new Vector2(badge.X + ((badge.Width - size) / 2), badge.Y + ((badge.Height - size) / 2)) + list, size, ink);
            }

            var matched = ai ? [] : SearchLayout.Matched(item.Name, typed);
            c.Text(new TextRun(laid.Name.Text, laid.Name.Style, laid.Name.Slot.Width) { Bold = matched, Tint = matched, TintColor = Theme.Accent }, laid.Name.Slot, list);
            bool folder = tipIn is not null && line.Row == select && SearchLayout.HasFolder(item);
            c.Text(folder && !line.Hit ? laid.Detail with { Text = "" } : laid.Detail, list);
            if (laid.Key is { } key && !folder)
            {
                Keycap(c, laid.KeyCap, key, list);
            }

            if (folder && tipIn is { } g)
            {
                var (button, glyphBox) = layout.Folder(line);
                c.RoundRect(Offset(button, list), SearchLayout.FolderCorner, SearchLayout.FolderFill);
                c.Glyph(Glyphs.Folder, new Vector2(glyphBox.X, glyphBox.Y) + list, SearchLayout.FolderGlyphSize, SearchLayout.FolderInk);
                c.RoundRect(Offset(button, list), SearchLayout.FolderCorner, StateLook.Hover);
                string tip = new Target("folder", button, SearchLayout.FolderCorner, TargetKind.Button, Tip: SearchLayout.FolderTip, Key: SearchLayout.FolderKey).TipWith(keys: true)!;
                var (tipCard, _, label) = StateLook.Tip(tip, false, Offset(button, list), new RectangleF((float)(-g.Width / 2), 0, (float)g.Width, (float)g.Depth));
                c.RoundRect(tipCard, StateLook.TipCorner, StateLook.TipFill);
                c.StrokeRect(tipCard, StateLook.TipCorner, StateLook.TipLine);
                c.Text(label, Vector2.Zero);
            }
        }
    }

    // A key hint as Keycaps draws it: the raised cap, its 1 DIP border, and the keys in it.
    private static void Keycap(Canvas c, RectangleF cap, string keys, Vector2 host)
    {
        var at = Offset(cap, host);
        c.RoundRect(at, Tomte.Core.Keycap.Corner, Tomte.Core.Keycap.Fill);
        c.Stroke(new RectangleF(at.X + 0.5f, at.Y + 0.5f, at.Width - 1, at.Height - 1), Tomte.Core.Keycap.Corner, Tomte.Core.Keycap.Border, 1);
        c.Text(keys, Tomte.Core.Keycap.Style, cap, host);
    }
}
