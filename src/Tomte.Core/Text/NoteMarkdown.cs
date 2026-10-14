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

    private static int LineStart(string text, int at) => at == 0 ? 0 : text.LastIndexOf('\n', at - 1) + 1;

    private static int LineEnd(string text, int at)
    {
        int end = text.IndexOf('\n', Math.Min(at, text.Length));
        return end < 0 ? text.Length : end;
    }
}
