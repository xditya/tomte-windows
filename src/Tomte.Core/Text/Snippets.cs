using System.Drawing;
using System.Numerics;
using Microsoft.Data.Sqlite;
using Tomte.Core.Interaction;
using Tomte.Core.Search;
using Tomte.Core.Storage;

namespace Tomte.Core.Text;

/// <summary>A piece of text saved under a name, typed into the note as "/" and its name. <paramref name="Used"/> is when it last went in, in UTC ticks, 0 for never.</summary>
public sealed record Snippet(long Id, string Name, string Text, long Used = 0)
{
    /// <summary>Text as the note keeps it: every line break one '\n'.</summary>
    public static string Clean(string text) => text.ReplaceLineEndings("\n");
}

/// <summary>The snippet list open under the caret: the "/word" it replaces, the snippets that match it, the one chosen. No rows means none are saved yet, and the list says where to add them.</summary>
public sealed record SnippetMenu(int Start, string Query, IReadOnlyList<Snippet> Rows, int Selected)
{
    /// <summary>The "/word" from <see cref="Start"/> to the caret becomes the chosen snippet's text: one replacement, so one undo step takes it back.</summary>
    public Splice Insert(Snippet snippet) => new(Start, Query.Length + 1, Snippet.Clean(snippet.Text));

    /// <summary>The same list with the choice moved by <paramref name="by"/>, round from the last row to the first.</summary>
    public SnippetMenu Move(int by) => Rows.Count == 0 ? this : this with { Selected = ((Selected + by) % Rows.Count + Rows.Count) % Rows.Count };
}

/// <summary>When typing "/" opens the snippet list, and what it lists.</summary>
public static class SnippetSlash
{
    /// <summary>The most rows the list shows.</summary>
    public const int MaxRows = 5;

    /// <summary>
    /// The "/word" the caret is at the end of, when it opens the list: the "/" at the start of a line or after a space or
    /// tab, and only letters, digits, "-", "_" or "." after it. Not inside a word ("a/b", a web address), not inside code
    /// (a fenced block, or between backticks on its line), and not with text right after the caret.
    /// </summary>
    public static (int Start, string Query)? Find(string text, int caret)
    {
        if (caret < 1 || caret > text.Length || (caret < text.Length && !char.IsWhiteSpace(text[caret])))
        {
            return null;
        }

        int at = caret;
        while (at > 0 && Word(text[at - 1]))
        {
            at--;
        }

        int slash = at - 1;
        if (slash < 0 || text[slash] != '/' || (slash > 0 && text[slash - 1] is not (' ' or '\t' or '\n')))
        {
            return null;
        }

        int line = NoteMarkdown.LineStart(text, slash);
        if (text.AsSpan(line, slash - line).Count('`') % 2 == 1 || NoteMarkdown.InFence(text, line))
        {
            return null;
        }

        return (slash, text[at..caret]);
    }

    /// <summary>
    /// The snippets for <paramref name="query"/>, best first, at most <see cref="MaxRows"/>: ranked by name with the
    /// search box's matcher, the one used last first among equals, then by name. With nothing typed yet, the ones used
    /// last come first.
    /// </summary>
    public static IReadOnlyList<Snippet> Filter(IReadOnlyList<Snippet> all, string query) =>
    [
        .. all.Select(s => (s, Score: query.Length == 0 ? 1 : SearchMatch.Score(s.Name, query)))
            .Where(r => r.Score > 0)
            .OrderByDescending(r => r.Score).ThenByDescending(r => r.s.Used).ThenBy(r => r.s.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(MaxRows).Select(r => r.s),
    ];

    /// <summary>
    /// The list for the caret, or null for none. <paramref name="earlier"/> is the list shown before, whose choice is kept
    /// while the same word is typed on. With snippets saved but none matching, there is no list, so typing goes on as
    /// usual; with none saved, the list is the one row that says where to add them.
    /// </summary>
    public static SnippetMenu? Menu(string text, int caret, IReadOnlyList<Snippet> all, SnippetMenu? earlier)
    {
        if (Find(text, caret) is not var (start, query))
        {
            return null;
        }

        var rows = Filter(all, query);
        if (all.Count > 0 && rows.Count == 0)
        {
            return null;
        }

        int selected = earlier is not null && earlier.Start == start && earlier.Query == query ? Math.Min(earlier.Selected, Math.Max(0, rows.Count - 1)) : 0;
        return new SnippetMenu(start, query, rows, selected);
    }

    private static bool Word(char c) => char.IsLetterOrDigit(c) || c is '-' or '_' or '.';
}

/// <summary>
/// Where the snippet list sits and what its rows show, in the host's DIPs: a card in the tooltip's grey and hairline,
/// under the caret's line from the "/", or above it when the note's card has more room there, kept inside that card.
/// Rows use the search list's look: the name in primary text, the start of the snippet's text after it in tertiary ink,
/// and the chosen row on the search list's selected fill.
/// </summary>
public sealed class SnippetMenuLayout
{
    public const float RowHeight = 32, Pad = 4, Gap = 4, Corner = 12, RowCorner = 8, Inset = 6, Width = 300;

    public const string Hint = "No snippets yet. Add them in Settings, under Scratchpad.";

    /// <param name="card">The note's card, which the list stays inside.</param>
    /// <param name="slash">The left end of the caret's line at the "/", in the host.</param>
    /// <param name="lineHeight">The caret's height.</param>
    /// <param name="rows">Rows shown; 0 shows the hint's one row.</param>
    public SnippetMenuLayout(RectangleF card, Vector2 slash, float lineHeight, int rows)
    {
        Count = Math.Max(1, rows);
        float w = Math.Min(Width, card.Width - (2 * Inset)), h = (Count * RowHeight) + (2 * Pad);
        float x = Math.Clamp(slash.X - Pad - SearchLayout.RowPadX, card.X + Inset, card.Right - Inset - w);
        float below = slash.Y + lineHeight + Gap, above = slash.Y - Gap - h;
        float y = below + h <= card.Bottom - Inset || card.Bottom - below > slash.Y - card.Y ? below : above;
        Box = new RectangleF(x, Math.Clamp(y, card.Y + Inset, Math.Max(card.Y + Inset, card.Bottom - Inset - h)), w, h);
    }

    public int Count { get; }

    public RectangleF Box { get; }

    public static uint Fill => StateLook.TipFill;

    public static uint Line => StateLook.TipLine;

    public static uint Selected => SearchLayout.SelectedFill;

    public RectangleF Row(int i) => new(Box.X + Pad, Box.Y + Pad + (i * RowHeight), Box.Width - (2 * Pad), RowHeight);

    /// <summary>A row's name and, after it, as much of the text's first line as fits in under half the row.</summary>
    public (TextSlot Name, TextSlot Detail) Slots(int i, Snippet snippet)
    {
        var row = Row(i);
        float x = row.X + SearchLayout.RowPadX, right = row.Right - SearchLayout.RowPadX, room = Math.Max(0, right - x);
        string detail = FirstLine(snippet.Text);
        float dw = Math.Min(TextSlot.Measure(detail, SearchLayout.Detail, room * 0.45f), room * 0.45f);
        return (new TextSlot(snippet.Name, SearchLayout.Name, new RectangleF(x, row.Y, Math.Max(0, room - dw - (dw > 0 ? SearchLayout.DetailGap : 0)), row.Height)),
            new TextSlot(detail, SearchLayout.Detail, new RectangleF(right - dw, row.Y, dw, row.Height)));
    }

    /// <summary>The one row with no snippets saved.</summary>
    public TextSlot HintSlot()
    {
        var row = Row(0);
        return new TextSlot(Hint, SearchLayout.Foot, RectangleF.FromLTRB(row.X + SearchLayout.RowPadX, row.Y, row.Right - SearchLayout.RowPadX, row.Bottom));
    }

    private static string FirstLine(string text)
    {
        string t = text.TrimStart();
        int end = t.IndexOfAny(['\r', '\n']);
        return end < 0 ? t : t[..end];
    }
}

/// <summary>
/// The saved snippets, in tomte.db: a name, the text, and when each last went in. Every method opens the database:
/// worker threads only.
/// </summary>
public sealed class SnippetStore(TomteDb db)
{
    /// <summary>The longest name kept; a name is one line.</summary>
    public const int MaxName = 60;

    private const string Feature = "snippets";

    // The whole history, oldest first. Never edit or reorder a step that has merged; add at the end.
    private static readonly string[] Schema =
    [
        "CREATE TABLE snippets_items(id INTEGER PRIMARY KEY, name TEXT NOT NULL, text TEXT NOT NULL, used INTEGER NOT NULL DEFAULT 0)",
    ];

    /// <summary>Every snippet, by name.</summary>
    public IReadOnlyList<Snippet> Load()
    {
        using var connection = db.Open(Feature, Schema);
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT id, name, text, used FROM snippets_items ORDER BY name COLLATE NOCASE, id";
        using var reader = command.ExecuteReader();
        var all = new List<Snippet>();
        while (reader.Read())
        {
            all.Add(new Snippet(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3)));
        }

        return all;
    }

    /// <summary>A new snippet; its id.</summary>
    public long Add(string name, string text)
    {
        using var connection = db.Open(Feature, Schema);
        return (long)Scalar(connection, "INSERT INTO snippets_items(name, text) VALUES ($name, $text) RETURNING id", ("$name", Name(name)), ("$text", Snippet.Clean(text)))!;
    }

    /// <summary>A new name or text for a snippet, or both.</summary>
    public void Update(long id, string name, string text)
    {
        using var connection = db.Open(Feature, Schema);
        Scalar(connection, "UPDATE snippets_items SET name = $name, text = $text WHERE id = $id", ("$id", id), ("$name", Name(name)), ("$text", Snippet.Clean(text)));
    }

    public void Delete(long id)
    {
        using var connection = db.Open(Feature, Schema);
        Scalar(connection, "DELETE FROM snippets_items WHERE id = $id", ("$id", id));
    }

    /// <summary>The snippet went into the note now, so it comes first among equals next time.</summary>
    public void Used(long id, DateTimeOffset now)
    {
        using var connection = db.Open(Feature, Schema);
        Scalar(connection, "UPDATE snippets_items SET used = $used WHERE id = $id", ("$id", id), ("$used", now.UtcTicks));
    }

    /// <summary>A name as kept: one line, trimmed, at most <see cref="MaxName"/> characters.</summary>
    public static string Name(string name)
    {
        string one = string.Join(' ', name.Split(['\r', '\n', '\t'], StringSplitOptions.RemoveEmptyEntries)).Trim();
        return one.Length <= MaxName ? one : one[..MaxName].TrimEnd();
    }

    private static object? Scalar(SqliteConnection connection, string sql, params ReadOnlySpan<(string Name, object Value)> parameters)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command.ExecuteScalar();
    }
}
