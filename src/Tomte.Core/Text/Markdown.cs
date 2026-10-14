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
/// A piece of a block's text, bold or not. <see cref="Text"/> is always the source's own characters at
/// <see cref="Source"/>, so a caller can style the source in place; a line break inside a block is a span "\n".
/// </summary>
public readonly record struct MarkdownSpan(string Text, bool Bold, int Source);

/// <summary>
/// One block: what it is, how deep in a list (0 at the first level and outside lists), the marker as written ("-",
/// "3."; empty outside lists), where its first line starts in the source, and its text as spans.
/// </summary>
public sealed record MarkdownBlock(MarkdownKind Kind, int Depth, string Marker, int Source, IReadOnlyList<MarkdownSpan> Spans)
{
    /// <summary>The text without its markers.</summary>
    public string Text => string.Concat(Spans.Select(s => s.Text));

    /// <summary>The bold parts of <see cref="Text"/> as start and length.</summary>
    public IReadOnlyList<(int Start, int Length)> Bold
    {
        get
        {
            var bold = new List<(int, int)>();
            int at = 0;
            foreach (var span in Spans)
            {
                if (span.Bold)
                {
                    bold.Add((at, span.Text.Length));
                }

                at += span.Text.Length;
            }

            return bold;
        }
    }
}

/// <summary>
/// The small part of Markdown that model answers use: paragraphs, line breaks kept as they are, headings, bullets
/// and numbered lists with their nesting, **bold**, fenced code kept as it is, and rules dropped. Anything else
/// stays as written. Unclosed bold stays as written too, so a streaming answer reads right at every step.
/// </summary>
public static class Markdown
{
    public static IReadOnlyList<MarkdownBlock> Parse(string text)
    {
        var blocks = new List<MarkdownBlock>();
        var indents = new List<int>();
        List<MarkdownSpan>? spans = null;
        (MarkdownKind Kind, int Depth, string Marker, int Source) open = (MarkdownKind.Paragraph, 0, "", 0);
        bool fenced = false;
        int lineBreak = -1;

        for (int start = 0; start <= text.Length;)
        {
            int end = text.IndexOf('\n', start);
            int next = end < 0 ? text.Length + 1 : end + 1;
            int stop = end < 0 ? text.Length : end > start && text[end - 1] == '\r' ? end - 1 : end;
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
                Add(MarkdownKind.Code, 0, "", start, [(start, stop - start, false)]);
            }
            else if (trimmed.IsWhiteSpace() || IsRule(trimmed))
            {
                Close();
            }
            else if (Heading(trimmed) is int hashes)
            {
                Close();
                indents.Clear();
                Add(MarkdownKind.Heading, 0, "", start, Inline(text, lead + hashes, stop, bold: true));
                Close();
            }
            else if (Item(trimmed) is (var kind, int width))
            {
                Close();
                int depth = Depth(indents, Width(line[..(line.Length - trimmed.Length)]));
                Add(kind, depth, trimmed[..(width - 1)].ToString().TrimEnd(), start, Inline(text, lead + width, stop, bold: false));
            }
            else
            {
                if (spans is null && line.Length == trimmed.Length)
                {
                    // Text at the margin after a blank line ends the list.
                    indents.Clear();
                }

                Add(MarkdownKind.Paragraph, 0, "", start, Inline(text, lead, stop, bold: false));
            }

            lineBreak = end;
            start = next;
        }

        Close();
        return blocks;

        // A line of the open block, or the first line of a new one; a line after the first comes after a line break.
        void Add(MarkdownKind kind, int depth, string marker, int source, IEnumerable<(int Start, int Length, bool Bold)> parts)
        {
            if (spans is null)
            {
                spans = [];
                open = (kind, depth, marker, source);
            }
            else
            {
                spans.Add(new MarkdownSpan("\n", false, lineBreak));
            }

            foreach (var (from, length, bold) in parts)
            {
                if (length > 0)
                {
                    spans.Add(new MarkdownSpan(text.Substring(from, length), bold, from));
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

    /// <summary>A list item's kind and how far its text starts: past "- ", "* ", "+ ", "12. " or "12) ".</summary>
    private static (MarkdownKind, int)? Item(ReadOnlySpan<char> line)
    {
        if (line is ['-' or '*' or '+', ' ' or '\t', ..])
        {
            return (MarkdownKind.Bullet, 2);
        }

        int digits = 0;
        while (digits < line.Length && digits < 9 && char.IsAsciiDigit(line[digits]))
        {
            digits++;
        }

        return digits > 0 && line.Length > digits + 1 && line[digits] is '.' or ')' && line[digits + 1] is ' ' or '\t'
            ? (MarkdownKind.Numbered, digits + 2) : null;
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
    /// The source from <paramref name="from"/> to <paramref name="to"/> as plain and bold parts, without the white
    /// space at either end. Bold is ** before a character that is not a space and ** after one; a ** that is not
    /// closed on the line stays as text.
    /// </summary>
    private static IEnumerable<(int Start, int Length, bool Bold)> Inline(string text, int from, int to, bool bold)
    {
        while (from < to && char.IsWhiteSpace(text[from]))
        {
            from++;
        }

        while (to > from && char.IsWhiteSpace(text[to - 1]))
        {
            to--;
        }

        int plain = from;
        for (int i = from; i + 1 < to; i++)
        {
            if (text[i] != '*' || text[i + 1] != '*' || i + 2 >= to || char.IsWhiteSpace(text[i + 2]))
            {
                continue;
            }

            int close = text.IndexOf("**", i + 2, to - i - 2, StringComparison.Ordinal);
            while (close >= 0 && char.IsWhiteSpace(text[close - 1]))
            {
                close = close + 2 < to ? text.IndexOf("**", close + 1, to - close - 1, StringComparison.Ordinal) : -1;
            }

            if (close < 0)
            {
                break;
            }

            yield return (plain, i - plain, bold);
            yield return (i + 2, close - i - 2, true);
            plain = close + 2;
            i = close + 1;
        }

        yield return (plain, to - plain, bold);
    }
}
