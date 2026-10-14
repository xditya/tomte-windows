using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Tomte.AI.Files;
using Tomte.AI.Mcp;
using Tomte.AI.Skills;
using Tomte.Core;

namespace Tomte.AI.Palette;

public sealed partial class PaletteSession
{
    /// <summary>
    /// A drop zone's send: one file and the zone's instruction. The model is offered no tool but the read tool of the
    /// zone's skill, when the zone is the skill slot, so the file's text has nothing to call.
    /// </summary>
    public bool FromDropZone { get; init; }
}

/// <summary>A result file a zone made, before it goes on the shelf.</summary>
public sealed record ZoneFile(string Name, byte[] Content);

/// <summary>
/// What a drop on an AI zone sends and what comes back as files. Each zone is one palette send through the same
/// readers, size question, spend cap and send log as the palette, with the dropped shelf item as its only chip.
/// </summary>
public static partial class DropZoneRequest
{
    /// <summary>What the zone asks of the model about the attached file.</summary>
    public static string Prompt(DropZone zone, string? skill) => zone switch
    {
        DropZone.Summarize => "Summarize the attached file. Answer in Markdown: a short title line, then the main points as a few bullets. Answer with the summary only.",
        DropZone.Tables => "Find the tables in the attached file. Give each table as CSV in a code block of its own that starts with ```csv: a header row first, " +
            "commas between fields, and double quotes around a field that holds a comma, a quote or a line break. Leave out any text outside the tables. " +
            "If the file has no table, say so in one sentence and give no code block.",
        DropZone.Rename => "Suggest a file name for the attached file from what it contains: a few words a person would search for, at most 60 characters, " +
            "without the extension. Answer with the name only, on one line.",
        DropZone.Skill => $"Use the {skill} skill on the attached file.",
        _ => "",
    };

    /// <summary>The dropped item as the send's one chip, on: the drop was the user's action. The send log names the zone with it.</summary>
    public static ContextChip Chip(string title, string item) => new(ContextKind.ShelfItem,
        $"File · {PreparedFile.Clean(item)}",
        $"drop zone {title}: {PreparedFile.Clean(item)}",
        _ => Task.FromResult<IReadOnlyList<ContentPart>>([new ShelfFilePart(item)]),
        on: true);

    /// <summary>
    /// The send for a zone. <paramref name="skill"/> is, for the skill slot, a set holding only the picked skill, so
    /// only its name and description go and its files are the only thing the model can read; null for the other zones.
    /// </summary>
    public static PaletteSession Session(DropZone zone, string title, string item, IChatProvider provider, ProviderConfig config, PaletteOptions options, SendLog log, BuiltInTools tools, PermissionGrants grants, SkillSet? skill = null, ModelInfo? model = null) =>
        new(provider, config, options, log, tools, grants, [Chip(title, item)])
        {
            Prompt = Prompt(zone, skill?.Skills.FirstOrDefault()?.Name),
            FromDropZone = true,
            Skills = zone == DropZone.Skill ? skill : null,
            Model = model,
        };

    /// <summary>"Ask about it": the palette with the file attached and an empty prompt. Nothing is sent until the user asks.</summary>
    public static PaletteSession Ask(string item, IChatProvider provider, ProviderConfig config, PaletteOptions options, SendLog log, BuiltInTools tools, PermissionGrants grants, ModelInfo? model = null) =>
        new(provider, config, options, log, tools, grants, [Chip(DropZoneLayout.Label(DropZone.Ask, null), item)]) { Model = model };

    /// <summary>
    /// The size question as a zone asks it. A name is one answer about the whole file, and parts would each give one,
    /// so Rename by content offers no chunks.
    /// </summary>
    public static SizeQuestion Asked(DropZone zone, SizeQuestion question) => zone == DropZone.Rename ? question with { Chunk = null } : question;

    /// <summary>The size question's choices in the order the panel shows them, cancel last: it is the default.</summary>
    public static IReadOnlyList<(string Text, SizeChoice Choice)> Choices(SizeQuestion question) =>
        [.. new[] { (question.Truncate, SizeChoice.Truncate), (question.Chunk, SizeChoice.Chunk) }.Where(c => c.Item1 is not null).Select(c => (c.Item1!, c.Item2)), (question.Cancel, SizeChoice.Cancel)];

    /// <summary>
    /// What the zone's panel shows now: the zone and the file, where the send went, then the open size question with
    /// its choices, or a line of progress, of what went wrong, or of what came of it (<paramref name="outcome"/>), with
    /// the answer (or <paramref name="body"/> in its place) and the result files.
    /// </summary>
    public static DropResult Panel(string title, string item, IPaletteSession session, SizeQuestion? asking, string? outcome = null, string? body = null, IReadOnlyList<ResultTile>? tiles = null)
    {
        string head = $"{title} · {PreparedFile.Clean(item)}";
        if (asking is not null)
            return new(head, session.ProviderIndicator, asking.Reason, false, "", [], [.. Choices(asking).Select(c => c.Text)]);
        string? line = session.Status switch
        {
            PaletteStatus.Failed => session.Error,
            PaletteStatus.Cancelled => "Cancelled.",
            PaletteStatus.Done => outcome,
            _ => session.Estimate?.Line ?? $"Reading {PreparedFile.Clean(item)}",
        };
        return new(head, session.ProviderIndicator, line, session.Status == PaletteStatus.Failed, body ?? session.AnswerText, tiles ?? [], []);
    }

    /// <summary>
    /// A result this file already has from this zone, as the panel shows it before anything is sent: the answer (for
    /// Rename, the name it suggested), when it was made, by which model and at what cost, its files still on the shelf,
    /// and two choices: run it again, or close, the default, which costs nothing. Running again sends to the provider
    /// in settings now, so the choice names <paramref name="model"/>, that provider's model, not the kept one's.
    /// </summary>
    public static DropResult Kept(string title, KeptResult kept, IReadOnlyList<ResultTile> tiles, string? model)
    {
        string body = kept.Zone == DropZone.Rename && SuggestedName(kept.Answer, kept.SourceName) is { } name ? $"Suggested name: {name}" : kept.Answer;
        string file = PreparedFile.Clean(kept.SourceName);
        return new($"{title} · {file}", "Nothing sent", kept.Line(), false, body, tiles, [$"Run again: send {file} to {model ?? "the provider in settings"}.", "Close: nothing is sent."]);
    }

    /// <summary>
    /// What a dropped item counts as for a skill's <c>accepts</c> list: folders, urls (an Internet shortcut), text (a
    /// text drag), images, or files. An image is taken by a skill that takes files too.
    /// </summary>
    public static bool Accepts(IReadOnlyList<string> accepts, string name, bool isFolder, bool fromText)
    {
        string ext = Path.GetExtension(name).ToLowerInvariant();
        string kind = isFolder ? "folders" : ext == ".url" ? "urls" : fromText ? "text"
            : ext is ".png" or ".jpg" or ".jpeg" or ".gif" or ".webp" or ".bmp" ? "images" : "files";
        return accepts.Contains(kind) || (kind == "images" && accepts.Contains("files"));
    }

    /// <summary>
    /// The files a finished zone leaves on the shelf: the summary or the skill's answer as Markdown, each CSV block of
    /// the tables as a .csv of its own. None for an empty answer, for tables when there were none, and for Rename and
    /// Ask, which leave no file of their own.
    /// </summary>
    public static IReadOnlyList<ZoneFile> Files(DropZone zone, string item, string answer, string? skill)
    {
        string stem = Path.GetFileNameWithoutExtension(item), text = answer.Trim();
        if (text.Length == 0)
            return [];
        switch (zone)
        {
            case DropZone.Summarize:
                return [new($"{stem} summary.md", Encoding.UTF8.GetBytes(text + "\n"))];
            case DropZone.Skill:
                return [new($"{stem} {skill}.md", Encoding.UTF8.GetBytes(text + "\n"))];
            case DropZone.Tables:
                var tables = CsvBlock().Matches(answer).Select(m => Csv(m.Groups[1].Value)).Where(t => t.Length > 0).ToList();
                return [.. tables.Select((t, i) => new ZoneFile(tables.Count == 1 ? $"{stem} table.csv" : $"{stem} table {i + 1}.csv", [.. Encoding.UTF8.Preamble, .. Encoding.UTF8.GetBytes(t)]))];
            default:
                return [];
        }
    }

    /// <summary>
    /// The model's suggested name as a file name with the item's own extension: the answer's first line without the
    /// characters that reorder text, quotes, a "Name:" label or the extension it may have added, made safe for Windows
    /// and at most 60 characters before the extension, cut on a whole character. Null when nothing is left of it.
    /// </summary>
    public static string? SuggestedName(string answer, string item)
    {
        string ext = Path.GetExtension(item);
        // Control and reordering characters go first, so a name reads as it is and nothing hides a quote.
        string line = answer.Split('\n').Select(l => McpTools.Bound(l, 200, lines: false).Text.Trim()).FirstOrDefault(l => l.Length > 0) ?? "";
        line = NameLabel().Replace(line.Trim('"', '\'', '`', '*', ' '), "").Trim('"', '\'', '`', '*', ' ');
        if (ext.Length > 0 && line.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
            line = line[..^ext.Length].TrimEnd();
        line = McpTools.Bound(line, 60, lines: false).Text.Trim();
        return line.Length == 0 ? null : ShelfStore.SafeName(line) + ext;
    }

    /// <summary>
    /// A CSV block re-written field by field: a header and rows as the model gave them (quotes and doubled quotes read
    /// as CSV reads them), line breaks as CRLF, and a field that a spreadsheet would run as a formula kept as text with
    /// a leading apostrophe: one that starts with a tab, or with =, +, -, or @ after any spaces, and is not a number.
    /// The block came from a file the user did not write, and a formula in it could fetch a web page when the CSV opens.
    /// </summary>
    public static string Csv(string block)
    {
        var rows = new List<List<string>>();
        var row = new List<string>();
        var field = new StringBuilder();
        bool quoted = false, any = false;
        string text = block.Replace("\r\n", "\n");
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (quoted)
            {
                if (c == '"' && i + 1 < text.Length && text[i + 1] == '"')
                {
                    field.Append('"');
                    i++;
                }
                else if (c == '"')
                {
                    quoted = false;
                }
                else
                {
                    field.Append(c);
                }
            }
            else if (c == '"' && field.Length == 0)
            {
                quoted = any = true;
            }
            else if (c == ',')
            {
                row.Add(field.ToString());
                field.Clear();
                any = true;
            }
            else if (c == '\n')
            {
                EndRow();
            }
            else
            {
                field.Append(c);
                any = true;
            }
        }

        EndRow();
        return string.Concat(rows.Select(r => string.Join(',', r.Select(Field)) + "\r\n"));

        void EndRow()
        {
            if (any || field.Length > 0)
            {
                row.Add(field.ToString());
                rows.Add(row);
            }

            row = [];
            field.Clear();
            any = false;
        }
    }

    private static string Field(string value)
    {
        // A spreadsheet skips leading spaces before it reads a formula, so " =HYPERLINK(..)" runs too.
        string lead = value.TrimStart(' ');
        bool control = value.Length > 0 && value[0] is '\t' or '\r';
        bool formula = lead.Length > 0 && lead[0] is '=' or '+' or '-' or '@'
            && !double.TryParse(lead, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        if (control || formula)
            value = "'" + value;
        return value.IndexOfAny([',', '"', '\n', '\r']) >= 0 || value != value.Trim() ? $"\"{value.Replace("\"", "\"\"")}\"" : value;
    }

    [GeneratedRegex(@"```[ \t]*(?:csv)?[ \t]*\r?\n(.*?)```", RegexOptions.Singleline | RegexOptions.IgnoreCase)]
    private static partial Regex CsvBlock();

    [GeneratedRegex(@"^(file\s*)?name\s*:\s*", RegexOptions.IgnoreCase)]
    private static partial Regex NameLabel();
}
