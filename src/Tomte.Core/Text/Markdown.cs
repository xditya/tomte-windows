namespace Tomte.Core.Text;

public enum MarkdownKind
{
    Paragraph,

    /// <summary>A line that starts with one to six #: a bold line.</summary>
    Heading,

    /// <summary>A list item that starts with -, * or +.</summary>
    Bullet,

    /// <summary>A list item that starts with a number and a dot or a parenthesis.</summary>
    Numbered,

    /// <summary>The lines between two ``` fences, as they are.</summary>
    Code,
}

/// <summary>
/// How a piece of text looks. The parser marks the first five; the scratchpad adds the rest as it styles the note in
/// place (<see cref="NoteMarkdown"/>).
/// </summary>
[Flags]
public enum MarkdownLook
{
    Plain = 0,
    Bold = 1,
    Italic = 2,
    Strike = 4,
    Code = 8,
    Link = 16,

    /// <summary>Markdown's own characters: #, **, a list marker, a link's brackets and address, a fence. Dimmed.</summary>
    Marker = 32,

    /// <summary>A checked task's text.</summary>
    Done = 64,

    /// <summary>A heading's line, by level; levels 4 to 6 share the last.</summary>
    Heading1 = 128,
    Heading2 = 256,
    Heading3 = 512,
    Heading4 = 1024,
}

/// <summary>
/// A piece of a block's text and its look. <see cref="Text"/> is always the source's own characters at
/// <see cref="Source"/>, so a caller can style the source in place; a line break inside a block is a span "\n".
/// </summary>
public readonly record struct MarkdownSpan(string Text, MarkdownLook Look, int Source)
{
    public bool Bold => (Look & MarkdownLook.Bold) != 0;
}

/// <summary>
/// One block: what it is, how deep in a list (0 at the first level and outside lists), the marker as written ("-",
/// "3."; empty outside lists), where its first line starts in the source, and its text as spans.
/// </summary>
public sealed record MarkdownBlock(MarkdownKind Kind, int Depth, string Marker, int Source, IReadOnlyList<MarkdownSpan> Spans)
{
    /// <summary>The text without its markers.</summary>
    public string Text => string.Concat(Spans.Select(s => s.Text));

    /// <summary>The bold parts of <see cref="Text"/> as start and length.</summary>
    public IReadOnlyList<(int Start, int Length)> Bold => Parts(MarkdownLook.Bold);

    /// <summary>The italic parts of <see cref="Text"/>.</summary>
    public IReadOnlyList<(int Start, int Length)> Italic => Parts(MarkdownLook.Italic);

    /// <summary>The parts of <see cref="Text"/> written as code.</summary>
    public IReadOnlyList<(int Start, int Length)> Code => Parts(MarkdownLook.Code);

    private List<(int, int)> Parts(MarkdownLook look)
    {
        var parts = new List<(int, int)>();
        int at = 0;
        foreach (var span in Spans)
        {
            if ((span.Look & look) != 0)
            {
                parts.Add((at, span.Text.Length));
            }

            at += span.Text.Length;
        }

        return parts;
    }
}

/// <summary>
/// The small part of Markdown that model answers use: paragraphs, line breaks kept as they are, headings, bullets
/// and numbered lists with their nesting, **bold**, *italic*, `code`, fenced code kept as it is, and rules dropped.
/// Anything else stays as written, and so does a marker that is not closed on its line.
/// </summary>
public static class Markdown
{
    public static IReadOnlyList<MarkdownBlock> Parse(string text) => Parse(text, 0, text.Length, fenced: false, note: false);

    /// <summary>
    /// The scratchpad note's Markdown, which adds *italic* and _italic_, __bold__, ~~struck~~, `code`, [links](address),
    /// bare web addresses, and task boxes ("- [ ] ", "- [x] ") read as part of an item's marker. Only the lines from
    /// <paramref name="from"/> (a line's start) to <paramref name="to"/> (a line's end), inside a code fence when
    /// <paramref name="fenced"/>; the spans keep their place in the whole text.
    /// </summary>
    public static IReadOnlyList<MarkdownBlock> Note(string text, int from, int to, bool fenced) => Parse(text, from, to, fenced, note: true);

    private static IReadOnlyList<MarkdownBlock> Parse(string text, int from, int to, bool fenced, bool note)
    {
        var blocks = new List<MarkdownBlock>();
        var indents = new List<int>();
        List<MarkdownSpan>? spans = null;
        (MarkdownKind Kind, int Depth, string Marker, int Source) open = (MarkdownKind.Paragraph, 0, "", 0);
        int lineBreak = -1;

        for (int start = from; start <= to;)
        {
            int end = text.IndexOf('\n', start, to - start);
            int next = end < 0 ? to + 1 : end + 1;
            int stop = end < 0 ? to : end > start && text[end - 1] == '\r' ? end - 1 : end;
            var line = text.AsSpan(start, stop - start);
            var trimmed = line.TrimStart();
            int lead = start + line.Length - trimmed.Length;

            if (trimmed.StartsWith("```"))
            {
                // A fence opens or closes a code block; its own line is not shown.
                Close();
                fenced = !fenced;
            }
            else if (fenced)
            {
                // Code keeps every line as it is, blank ones too.
                Add(MarkdownKind.Code, 0, "", start, [(start, stop - start, MarkdownLook.Plain)]);
            }
            else if (trimmed.IsWhiteSpace() || IsRule(trimmed))
            {
                Close();
            }
            else if (Heading(trimmed) is int hashes)
            {
                Close();
                indents.Clear();
                Add(MarkdownKind.Heading, 0, "", start, Parts(lead + hashes, stop, heading: true));
                Close();
            }
            else if (Item(trimmed, note) is (var kind, int width))
            {
                Close();
                int depth = Depth(indents, Width(line[..(line.Length - trimmed.Length)]));
                Add(kind, depth, trimmed[..width].ToString().TrimEnd(), start, Parts(lead + width, stop, heading: false));
            }
            else
            {
                if (spans is null && line.Length == trimmed.Length)
                {
                    // Text at the margin after a blank line ends the list.
                    indents.Clear();
                }

                Add(MarkdownKind.Paragraph, 0, "", start, Parts(lead, stop, heading: false));
            }

            lineBreak = end;
            start = next;
        }

        Close();
        return blocks;

        // A heading is bold in an answer; the note gives a heading's line a look of its own instead.
        IEnumerable<(int Start, int Length, MarkdownLook Look)> Parts(int first, int last, bool heading) =>
            Inline(text, first, last, heading && !note ? MarkdownLook.Bold : MarkdownLook.Plain, note);

        // A line of the open block, or the first line of a new one; a line after the first comes after a line break.
        void Add(MarkdownKind kind, int depth, string marker, int source, IEnumerable<(int Start, int Length, MarkdownLook Look)> parts)
        {
            if (spans is null)
            {
                spans = [];
                open = (kind, depth, marker, source);
            }
            else
            {
                spans.Add(new MarkdownSpan("\n", MarkdownLook.Plain, lineBreak));
            }

            foreach (var (at, length, look) in parts)
            {
                if (length > 0)
                {
                    spans.Add(new MarkdownSpan(text.Substring(at, length), look, at));
                }
            }
        }

        void Close()
        {
            if (spans is { Count: > 0 } && spans.Any(s => s.Text != "\n"))
            {
                blocks.Add(new MarkdownBlock(open.Kind, open.Depth, open.Marker, open.Source, spans));
            }

            spans = null;
        }
    }

    /// <summary>A rule: three or more of the same -, * or _, spaces allowed between.</summary>
    private static bool IsRule(ReadOnlySpan<char> line)
    {
        char mark = line[0];
        if (mark is not ('-' or '*' or '_'))
        {
            return false;
        }

        int count = 0;
        foreach (char c in line)
        {
            if (c == mark)
            {
                count++;
            }
            else if (!char.IsWhiteSpace(c))
            {
                return false;
            }
        }

        return count >= 3;
    }

    /// <summary>How far the heading's text starts: past one to six # and the space after them.</summary>
    private static int? Heading(ReadOnlySpan<char> line)
    {
        int hashes = line.Length - line.TrimStart('#').Length;
        return hashes is >= 1 and <= 6 && (line.Length == hashes || line[hashes] == ' ') ? hashes : null;
    }

    /// <summary>
    /// A list item's kind and how far its text starts: past "- ", "* ", "+ ", "12. " or "12) ", and in the note also
    /// past a task box right after that, "[ ]", "[x]" or "[X]" and a space.
    /// </summary>
    internal static (MarkdownKind, int)? Item(ReadOnlySpan<char> line, bool note)
    {
        int digits = 0;
        while (digits < line.Length && digits < 9 && char.IsAsciiDigit(line[digits]))
        {
            digits++;
        }

        (MarkdownKind Kind, int Width)? item = line is ['-' or '*' or '+', ' ' or '\t', ..] ? (MarkdownKind.Bullet, 2)
            : digits > 0 && line.Length > digits + 1 && line[digits] is '.' or ')' && line[digits + 1] is ' ' or '\t' ? (MarkdownKind.Numbered, digits + 2)
            : null;
        if (note && item is var (kind, width))
        {
            var box = line[width..];
            if (box is ['[', ' ' or 'x' or 'X', ']'] or ['[', ' ' or 'x' or 'X', ']', ' ' or '\t', ..])
            {
                return (kind, width + Math.Min(4, box.Length));
            }
        }

        return item;
    }

    /// <summary>Leading white space as columns, a tab four.</summary>
    private static int Width(ReadOnlySpan<char> indent)
    {
        int width = 0;
        foreach (char c in indent)
        {
            width += c == '\t' ? 4 : 1;
        }

        return width;
    }

    /// <summary>
    /// The list depth of an item indented this far: deeper than the item above when indented more, back to the level
    /// it lines up with when indented less. Two or four spaces a level both work.
    /// </summary>
    private static int Depth(List<int> indents, int indent)
    {
        while (indents.Count > 0 && indent < indents[^1])
        {
            indents.RemoveAt(indents.Count - 1);
        }

        if (indents.Count == 0 || indent > indents[^1])
        {
            indents.Add(indent);
        }

        return indents.Count - 1;
    }

    /// <summary>
    /// The inline Markdown from <paramref name="from"/> to <paramref name="to"/>, without the white space at either end,
    /// as parts of one look each, on top of <paramref name="look"/>; the markers fall between the parts. Emphasis is
    /// closed by the same run of its marker (*, **, *** or __, ~~) against its text on both sides, and _ never inside a
    /// word, so snake_case stays as written. Code is closed by the same run of backticks and nothing inside it is read.
    /// An answer reads only * and backticks; the <paramref name="note"/> also reads _, ~~, links and bare addresses.
    /// What is not closed on the line stays as text.
    /// </summary>
    private static List<(int Start, int Length, MarkdownLook Look)> Inline(string text, int from, int to, MarkdownLook look, bool note)
    {
        while (from < to && char.IsWhiteSpace(text[from]))
        {
            from++;
        }

        while (to > from && char.IsWhiteSpace(text[to - 1]))
        {
            to--;
        }

        var parts = new List<(int, int, MarkdownLook)>();
        Inner(text, from, to, look, note, parts);
        return parts;
    }

    private static void Inner(string text, int from, int to, MarkdownLook look, bool note, List<(int Start, int Length, MarkdownLook Look)> parts)
    {
        int plain = from;
        for (int i = from; i < to;)
        {
            char c = text[i];
            int run = c is '*' or '_' or '~' or '`' ? Run(text, i, to) : 1;
            var found = c switch
            {
                '`' => CodeSpan(text, i, run, to),
                '*' => Emphasis(text, i, run, to),
                _ when !note => null,
                '[' => LinkSpan(text, i, to),
                '_' or '~' => Emphasis(text, i, run, to),
                'h' => Address(text, i, to),
                _ => null,
            };
            if (found is not var (start, end, resume, add, nested))
            {
                i += run;
                continue;
            }

            parts.Add((plain, i - plain, look));
            if (nested)
            {
                Inner(text, start, end, look | add, note, parts);
            }
            else
            {
                parts.Add((start, end - start, look | add));
            }

            i = plain = resume;
        }

        parts.Add((plain, to - plain, look));
    }

    /// <summary>How many of the character at <paramref name="at"/> follow one another from there, before <paramref name="to"/>.</summary>
    private static int Run(string text, int at, int to)
    {
        int end = at + 1;
        while (end < to && text[end] == text[at])
        {
            end++;
        }

        return end - at;
    }

    // A construct found at a marker: its text from Start to End, where reading goes on after it, the look it adds, and
    // whether the Markdown inside its text is read too.
    private static (int Start, int End, int Resume, MarkdownLook Add, bool Nested)? CodeSpan(string text, int at, int run, int to)
    {
        for (int i = at + run; i < to;)
        {
            int close = text.IndexOf('`', i, to - i);
            if (close < 0)
            {
                return null;
            }

            int length = Run(text, close, to);
            if (length == run)
            {
                return (at + run, close, close + run, MarkdownLook.Code, false);
            }

            i = close + length;
        }

        return null;
    }

    private static (int, int, int, MarkdownLook, bool)? LinkSpan(string text, int at, int to)
    {
        int close = text.IndexOf(']', at + 1, to - at - 1);
        if (close <= at + 1 || close + 2 >= to || text[close + 1] != '(')
        {
            return null;
        }

        int end = text.IndexOf(')', close + 2, to - close - 2);
        return end > close + 2 && !text.AsSpan(close + 2, end - close - 2).ContainsAny(' ', '\t')
            ? (at + 1, close, end + 1, MarkdownLook.Link, true) : null;
    }

    private static (int, int, int, MarkdownLook, bool)? Emphasis(string text, int at, int run, int to)
    {
        char c = text[at];
        if (at + run >= to || char.IsWhiteSpace(text[at + run]) || (c == '_' && at > 0 && char.IsLetterOrDigit(text[at - 1])) || (c == '~' && run != 2))
        {
            return null;
        }

        for (int n = Math.Min(run, 3); n >= (c == '~' ? 2 : 1); n--)
        {
            if (Closer(text, at + n, n, to) is int close)
            {
                var look = c == '~' ? MarkdownLook.Strike : n == 1 ? MarkdownLook.Italic : n == 2 ? MarkdownLook.Bold : MarkdownLook.Bold | MarkdownLook.Italic;
                return (at + n, close, close + n, look, true);
            }
        }

        return null;
    }

    /// <summary>
    /// Where emphasis whose text starts at <paramref name="from"/> closes: the next run of exactly <paramref name="n"/>
    /// of its marker after at least one character, against that text, and for _ not before a letter or digit.
    /// </summary>
    private static int? Closer(string text, int from, int n, int to)
    {
        char c = text[from - 1];
        for (int i = from + 1; i < to;)
        {
            int close = text.IndexOf(c, i, to - i);
            if (close < 0)
            {
                return null;
            }

            int length = Run(text, close, to);
            if (length == n && text[close - 1] != c && !char.IsWhiteSpace(text[close - 1])
                && (c != '_' || close + n >= to || !char.IsLetterOrDigit(text[close + n])))
            {
                return close;
            }

            i = close + length;
        }

        return null;
    }

    /// <summary>A bare http or https address, up to white space, without the punctuation that ends a sentence after it.</summary>
    private static (int, int, int, MarkdownLook, bool)? Address(string text, int at, int to)
    {
        var rest = text.AsSpan(at, to - at);
        int scheme = rest.StartsWith("https://") ? 8 : rest.StartsWith("http://") ? 7 : 0;
        if (scheme == 0 || (at > 0 && char.IsLetterOrDigit(text[at - 1])))
        {
            return null;
        }

        int end = at + scheme;
        while (end < to && !char.IsWhiteSpace(text[end]))
        {
            end++;
        }

        while (end > at + scheme && text[end - 1] is '.' or ',' or ';' or ':' or '!' or '?' or ')' or '>' or '"' or '\'' or '*' or '_' or '~')
        {
            end--;
        }

        return end > at + scheme ? (at, end, end, MarkdownLook.Link, false) : null;
    }
}
