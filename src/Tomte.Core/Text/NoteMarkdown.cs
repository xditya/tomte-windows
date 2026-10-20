namespace Tomte.Core.Text;

/// <summary>A stretch of the note and how it looks.</summary>
public readonly record struct NoteRun(int Start, int Length, MarkdownLook Look);

/// <summary>
/// One line of the note: where it starts, and how many of its first characters hang (a list item's indent, marker
/// and task box), so the lines it wraps onto start under its text.
/// </summary>
public readonly record struct NoteLine(int Start, int Hang);

/// <summary>
/// The scratchpad note's Markdown, styled in place: the text stays exactly what was typed, markers included, and
/// each character only gets a look. Looks go by line and only a code fence reaches past its own line, so after an
/// edit only the lines it touched are styled again, or everything after them when one of them holds a fence.
/// </summary>
public static class NoteMarkdown
{
    private const string Fence = "```";

    /// <summary>
    /// The lines to style again after the text went from <paramref name="before"/> to <paramref name="after"/>: from
    /// a line's start to a line's end in <paramref name="after"/>, or null when nothing changed.
    /// </summary>
    public static (int From, int To)? Changed(string before, string after)
    {
        var splice = Splice.Between(before, after);
        if (splice.IsEmpty)
        {
            return null;
        }

        int end = splice.Start + splice.Text.Length;
        bool fence = TouchesFence(before, splice.Start, splice.End) || TouchesFence(after, splice.Start, end);
        return (LineStart(after, splice.Start), fence ? after.Length : LineEnd(after, end));
    }

    /// <summary>
    /// <paramref name="count"/> lines (fewer at the text's end) from the one <paramref name="at"/> is on: from a line's
    /// start to a line's end, a piece of a long note to style at a time.
    /// </summary>
    public static (int From, int To) Lines(string text, int at, int count)
    {
        int start = LineStart(text, Math.Min(at, text.Length));
        for (int end = start, i = 1; ; i++)
        {
            end = text.IndexOf('\n', end);
            if (end < 0 || i >= count)
            {
                return (start, end < 0 ? text.Length : end);
            }

            end++;
        }
    }

    /// <summary>About <paramref name="count"/> lines around the one <paramref name="at"/> is on, half of them before it.</summary>
    public static (int From, int To) Around(string text, int at, int count)
    {
        int start = LineStart(text, Math.Min(at, text.Length));
        for (int i = 0; i < count / 2 && start > 0; i++)
        {
            start = LineStart(text, start - 1);
        }

        return Lines(text, start, count);
    }

    /// <summary>
    /// How the lines from <paramref name="from"/> (a line's start) to <paramref name="to"/> (a line's end) look: each
    /// line, and runs that cover every character of them, the line break after the last one included.
    /// </summary>
    public static (List<NoteLine> Lines, List<NoteRun> Runs) Style(string text, int from, int to)
    {
        var lines = new List<NoteLine>();
        for (int at = from; ;)
        {
            lines.Add(new NoteLine(at, 0));
            int end = text.IndexOf('\n', at, to - at);
            if (end < 0)
            {
                break;
            }

            at = end + 1;
        }

        var runs = new List<NoteRun>();
        int done = from, line = 0;
        foreach (var block in Markdown.Note(text, from, to, InFence(text, from)))
        {
            var look = block.Kind switch
            {
                MarkdownKind.Heading => Level(text, block.Source),
                MarkdownKind.Code => MarkdownLook.Code,
                _ => MarkdownLook.Plain,
            };
            if (block.Kind is MarkdownKind.Bullet or MarkdownKind.Numbered)
            {
                while (lines[line].Start < block.Source)
                {
                    line++;
                }

                lines[line] = lines[line] with { Hang = Hang(text, block) };
            }

            // A checked task's first line is done; the lines under it stay as they are.
            bool ticked = block.Marker.EndsWith("[x]", StringComparison.OrdinalIgnoreCase);
            foreach (var span in block.Spans)
            {
                if (span.Text == "\n")
                {
                    ticked = false;
                    continue;
                }

                Gap(text, runs, done, span.Source, block.Source, look);
                Add(runs, span.Source, span.Text.Length, span.Look | look | (ticked ? MarkdownLook.Done : 0));
                done = span.Source + span.Text.Length;
            }

            // The markers that close the block's last line are on it too.
            int last = LineEnd(text, done);
            Gap(text, runs, done, last, block.Source, look);
            done = last;
        }

        Gap(text, runs, done, to < text.Length ? to + 1 : to, int.MaxValue, MarkdownLook.Plain);
        return (lines, runs);
    }

    /// <summary>
    /// Characters no span has, from <paramref name="from"/> to <paramref name="to"/>: markers, in the look of their
    /// block's line when they are on it (a heading's #), else plain, a fence line in code's look; line breaks plain.
    /// </summary>
    private static void Gap(string text, List<NoteRun> runs, int from, int to, int block, MarkdownLook look)
    {
        while (from < to)
        {
            int end = text.IndexOf('\n', from, to - from);
            int stop = end < 0 ? to : end;
            if (stop > from)
            {
                var own = from >= block ? look : text.AsSpan(from, stop - from).TrimStart().StartsWith(Fence) ? MarkdownLook.Code : MarkdownLook.Plain;
                Add(runs, from, stop - from, MarkdownLook.Marker | own);
            }

            if (end < 0)
            {
                return;
            }

            Add(runs, end, 1, MarkdownLook.Plain);
            from = end + 1;
        }
    }

    // One run more, or the last one longer when it looks the same.
    private static void Add(List<NoteRun> runs, int start, int length, MarkdownLook look)
    {
        if (runs.Count > 0 && runs[^1] is var last && last.Look == look && last.Start + last.Length == start)
        {
            runs[^1] = last with { Length = last.Length + length };
        }
        else
        {
            runs.Add(new NoteRun(start, length, look));
        }
    }

    private static MarkdownLook Level(string text, int line)
    {
        var heading = text.AsSpan(line).TrimStart();
        return (heading.Length - heading.TrimStart('#').Length) switch
        {
            1 => MarkdownLook.Heading1,
            2 => MarkdownLook.Heading2,
            3 => MarkdownLook.Heading3,
            _ => MarkdownLook.Heading4,
        };
    }

    /// <summary>
    /// Tab (<paramref name="outdent"/> false) or Shift+Tab on the selection from <paramref name="start"/> to
    /// <paramref name="end"/>, as an editor does: Tab on a list item's line nests the item, Tab on lines selected across
    /// a line break indents each of them, and Tab anywhere else puts one indent in place of the selection; Shift+Tab takes
    /// one indent off the start of every line it touches. One indent is what the note already uses: a tab when its first
    /// indented line starts with one, else the fewest spaces an indented line starts with (2 to 8), else four spaces.
    /// The change and the selection after it; null when nothing changes.
    /// </summary>
    public static (Splice Change, int Start, int End)? Indent(string text, int start, int end, bool outdent)
    {
        (start, end) = (Math.Clamp(Math.Min(start, end), 0, text.Length), Math.Clamp(Math.Max(start, end), 0, text.Length));
        string unit = IndentUnit(text);
        int first = LineStart(text, start);
        // A selection that ends where a line starts leaves that line alone.
        int lastAt = end > start && text[end - 1] == '\n' ? end - 1 : end;
        int last = LineEnd(text, lastAt);
        bool lines = text.AsSpan(start, end - start).Contains('\n');
        if (!outdent && !lines && Markdown.Item(text.AsSpan(first, last - first).TrimStart(" \t"), note: true) is null)
        {
            return (new Splice(start, end - start, unit), start + unit.Length, start + unit.Length);
        }

        var block = new System.Text.StringBuilder();
        int newStart = start, newEnd = end, most = unit == "\t" ? 4 : unit.Length;
        for (int at = first; at <= last;)
        {
            int lineEnd = LineEnd(text, at);
            // Shift+Tab takes a leading tab, or else up to one indent's worth of leading spaces.
            int cut = outdent && at < lineEnd && text[at] == '\t' ? 1 : 0;
            while (outdent && cut < most && at + cut < lineEnd && text[at] != '\t' && text[at + cut] == ' ')
            {
                cut++;
            }

            // Tab leaves blank lines in the selection blank.
            int shift = outdent ? -cut : lineEnd > at ? unit.Length : 0;
            newStart += Moved(start, at, cut, shift);
            newEnd += Moved(end, at, cut, shift);
            block.Append(outdent || lineEnd == at ? "" : unit).Append(text, at + cut, lineEnd - at - cut);
            if (lineEnd < last)
            {
                block.Append('\n');
            }

            at = lineEnd + 1;
        }

        // Only what changed is replaced, so the lines' own formatting is left alone.
        var change = Splice.Between(text[first..last], block.ToString());
        return change.IsEmpty ? null : (change with { Start = first + change.Start }, newStart, newEnd);
    }

    // How far a position moves when a line starting at `line` gains (shift > 0) or loses `cut` characters at its start.
    private static int Moved(int position, int line, int cut, int shift) =>
        position < line ? 0 : shift >= 0 ? shift : -Math.Min(cut, position - line);

    // What one indent is in this note: see Indent.
    private static string IndentUnit(string text)
    {
        int least = int.MaxValue;
        for (int at = 0; at < text.Length; at = LineEnd(text, at) + 1)
        {
            int lead = at;
            while (lead < text.Length && text[lead] is ' ' or '\t')
            {
                lead++;
            }

            if (lead == at || lead == text.Length || text[lead] == '\n')
            {
                continue;
            }

            if (text[at] == '\t')
            {
                if (least == int.MaxValue)
                {
                    return "\t";
                }

                continue;
            }

            int spaces = text.AsSpan(at, lead - at).IndexOf('\t') is int tab and >= 0 ? tab : lead - at;
            least = Math.Min(least, spaces);
        }

        return new string(' ', least == int.MaxValue ? 4 : Math.Clamp(least, 2, 8));
    }

    // The indent, the marker as written (the task box included) and the white space after it.
    private static int Hang(string text, MarkdownBlock item)
    {
        int at = item.Source;
        while (at < text.Length && text[at] is ' ' or '\t')
        {
            at++;
        }

        at += item.Marker.Length;
        while (at < text.Length && text[at] is ' ' or '\t')
        {
            at++;
        }

        return at - item.Source;
    }

    /// <summary>Whether <paramref name="lineStart"/> is inside a code block: an odd number of fence lines before it.</summary>
    private static bool InFence(string text, int lineStart)
    {
        bool fenced = false;
        for (int at = 0; at < lineStart;)
        {
            int fence = text.IndexOf(Fence, at, lineStart - at, StringComparison.Ordinal);
            if (fence < 0)
            {
                break;
            }

            int start = LineStart(text, fence);
            if (text.AsSpan(start, fence - start).IsWhiteSpace())
            {
                fenced = !fenced;
            }

            at = LineEnd(text, fence) + 1;
        }

        return fenced;
    }

    // Whether a line from the one at a to the one at b holds three backticks.
    private static bool TouchesFence(string text, int a, int b)
    {
        int start = LineStart(text, a);
        return text.AsSpan(start, LineEnd(text, b) - start).Contains(Fence, StringComparison.Ordinal);
    }

    /// <summary>
    /// The task box on the line that starts at <paramref name="lineStart"/>: where its mark (the space or x between the
    /// brackets) is, and whether it is ticked. Null when that is not a line's start, the line is no task item, or it sits
    /// in a code block.
    /// </summary>
    public static (int Mark, bool Done)? TaskBox(string text, int lineStart)
    {
        if (lineStart < 0 || lineStart >= text.Length || (lineStart > 0 && text[lineStart - 1] != '\n') || InFence(text, lineStart))
        {
            return null;
        }

        int end = LineEnd(text, lineStart);
        end -= end > lineStart && text[end - 1] == '\r' ? 1 : 0;
        var line = text.AsSpan(lineStart, end - lineStart);
        var trimmed = line.TrimStart();
        if (Markdown.Item(trimmed, note: false) is not (_, int width) || Markdown.Item(trimmed, note: true) is not (_, int boxed) || boxed == width)
        {
            return null;
        }

        int mark = lineStart + (line.Length - trimmed.Length) + width + 1;
        return (mark, text[mark] != ' ');
    }

    /// <summary>The note with the task box on the line at <paramref name="lineStart"/> ticked or cleared, nothing else changed; null when there is none.</summary>
    public static string? ToggleTask(string text, int lineStart) =>
        TaskBox(text, lineStart) is var (mark, done) ? string.Concat(text.AsSpan(0, mark), done ? " " : "x", text.AsSpan(mark + 1)) : null;

    private static int LineStart(string text, int at) => at == 0 ? 0 : text.LastIndexOf('\n', at - 1) + 1;

    private static int LineEnd(string text, int at)
    {
        int end = text.IndexOf('\n', Math.Min(at, text.Length));
        return end < 0 ? text.Length : end;
    }
}
