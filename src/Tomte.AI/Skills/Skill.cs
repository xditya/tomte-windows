using System.Globalization;
using System.Text;
using System.Text.Json;
using Tomte.AI.Mcp;

namespace Tomte.AI.Skills;

/// <summary>A palette command a skill's tomte.json registers. Picking it puts <see cref="Text"/> in the prompt.</summary>
public sealed record SkillCommand(string Skill, string Name, string Prompt)
{
    /// <summary>The prompt as sent: it names the skill, so the model reads that skill before it answers.</summary>
    public string Text => $"Use the {Skill} skill. {Prompt}";
}

/// <summary>A drop zone a skill's tomte.json registers: its label, and which of the shelf's kinds it takes.</summary>
public sealed record SkillDropZone(string Skill, string Label, IReadOnlyList<string> Accepts);

/// <summary>
/// One folder in the skills folder, as read from its SKILL.md and its optional tomte.json. A skill
/// with a <see cref="Problem"/> is listed in Settings with the reason and never reaches a palette.
/// </summary>
public sealed record Skill(
    string Name,
    string Key,
    string Description,
    string? License,
    SkillCommand? Command,
    SkillDropZone? DropZone,
    IReadOnlyList<string> Scripts,
    IReadOnlyList<string> IgnoredKeys,
    string? Problem)
{
    /// <summary>The longest SKILL.md or other skill file the model gets, and how much of SKILL.md is read for its header.</summary>
    public const int MaxFile = 20_000;

    /// <summary>The longest tomte.json.</summary>
    public const int MaxManifest = 4_000;

    /// <summary>The kinds a drop zone may accept: the kinds of item the shelf takes.</summary>
    public static readonly IReadOnlyList<string> DropKinds = ["files", "folders", "text", "images", "urls"];

    /// <summary>The format's limit for a description.</summary>
    public const int MaxDescription = 1024;

    private const int MaxCommandName = 40;
    private const int MaxPrompt = 1000;

    // Files whose name says they run as code; Tomte runs none of them. Settings says so.
    private static readonly HashSet<string> ScriptExtensions = new(
        [".py", ".sh", ".bash", ".zsh", ".ps1", ".psm1", ".bat", ".cmd", ".js", ".mjs", ".cjs", ".ts", ".rb", ".pl", ".php", ".lua", ".exe", ".dll", ".com", ".vbs", ".wsf", ".jar", ".msi"],
        StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Reads the skill in <paramref name="name"/>, a folder of <paramref name="skillsFolder"/>. Never throws.
    /// Only Settings needs the scripts, which take a walk of the whole folder, so a palette passes false.
    /// </summary>
    public static Skill Read(string skillsFolder, string name, bool listScripts)
    {
        try
        {
            var (text, _, skillMd, error) = SkillFile.Read(skillsFolder, name, "SKILL.md", MaxFile);
            if (error is not null)
                return Broken(name, "SKILL.md: " + error);
            var (values, ignored, problem) = FrontMatter(text);
            problem ??= Check(values, name);
            if (problem is not null)
                return Broken(name, "SKILL.md: " + problem, ignored);

            SkillCommand? command = null;
            SkillDropZone? zone = null;
            byte[]? manifest = null;
            if (File.Exists(Path.Combine(skillsFolder, name, "tomte.json")))
            {
                (var json, bool longer, manifest, var manifestError) = SkillFile.Read(skillsFolder, name, "tomte.json", MaxManifest);
                (command, zone, problem) = manifestError is not null ? (null, null, manifestError)
                    : longer ? (null, null, $"it is longer than {MaxManifest} characters.")
                    : Manifest(name, json);
            }

            return new Skill(name, SkillFile.Key(name, skillMd, manifest), McpTools.Clean(values["description"], MaxDescription),
                values.TryGetValue("license", out var license) ? McpTools.Clean(license, 100) : null, problem is null ? command : null, problem is null ? zone : null,
                listScripts ? FindScripts(Path.Combine(skillsFolder, name)) : [], ignored, problem is null ? null : "tomte.json: " + problem);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.ComponentModel.Win32Exception)
        {
            return Broken(name, $"Could not read the folder: {McpTools.Clean(e.Message, McpTools.MaxError)}");
        }
    }

    // A skill that does not load has no key: there is nothing it could be turned on as.
    private static Skill Broken(string name, string problem, IReadOnlyList<string>? ignored = null) =>
        new(name, name, "", null, null, null, [], ignored ?? [], problem);

    /// <summary>
    /// The front matter of a SKILL.md: the YAML between a first line of --- and the next ---. Only the part
    /// of YAML a skill header uses is read: "key: value" lines, quoted values, | and &gt; blocks, and values that
    /// go on over indented lines. metadata, and keys the format does not define, are skipped with what is
    /// indented under them; the latter are returned so Settings can name them.
    /// </summary>
    internal static (Dictionary<string, string> Values, List<string> Ignored, string? Problem) FrontMatter(string text)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        var ignored = new List<string>();
        (Dictionary<string, string>, List<string>, string?) Fail(string problem) => (values, ignored, problem);

        string[] lines = text.Split('\n');
        if (lines[0].TrimEnd() != "---")
            return Fail("it has no front matter. It must start with a line of ---, then name: and description: lines, then another line of ---.");
        int end = Array.FindIndex(lines, 1, l => l.TrimEnd() is "---" or "...");
        if (end < 0)
            return Fail("the front matter has no closing line of ---.");

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 1; i < end;)
        {
            string line = lines[i];
            int number = i + 1;
            if (line.Trim().Length == 0 || line.TrimStart().StartsWith('#'))
            {
                i++;
                continue;
            }

            if (char.IsWhiteSpace(line[0]))
                return Fail($"line {number} is indented but belongs to no key.");
            int colon = line.IndexOf(':');
            if (colon <= 0 || (colon + 1 < line.Length && !char.IsWhiteSpace(line[colon + 1])))
                return Fail($"line {number} is not \"key: value\".");
            string key = line[..colon].Trim();
            if (!seen.Add(key))
                return Fail($"{key} is given twice (line {number}).");

            // The lines indented under the key belong to it.
            int next = i + 1;
            while (next < end && (lines[next].Trim().Length == 0 || char.IsWhiteSpace(lines[next][0])))
                next++;
            string rest = line[(colon + 1)..].Trim();
            string[] block = lines[(i + 1)..next];
            i = next;

            string? problem;
            switch (key)
            {
                case "name" or "description" or "license" or "compatibility":
                    (values[key], problem) = Scalar(rest, block, number);
                    break;
                case "metadata":
                    // The format's place for anything a client wants; Tomte keeps none of it, so it is not checked.
                    problem = null;
                    break;
                default:
                    // allowed-tools among them: a skill cannot approve a tool, so the list is not read.
                    ignored.Add(McpTools.Clean(key, McpTools.MaxName));
                    problem = null;
                    break;
            }

            if (problem is not null)
                return Fail(problem);
        }

        return (values, ignored, null);
    }

    /// <summary>
    /// A text value: plain, in double or single quotes, a | or &gt; block, or plain text that goes on over the
    /// indented lines after it.
    /// </summary>
    private static (string Value, string? Problem) Scalar(string rest, string[] block, int number)
    {
        if (rest.StartsWith('|') || rest.StartsWith('>'))
        {
            if (WithoutComment(rest) is not ("|" or "|-" or "|+" or ">" or ">-" or ">+"))
                return ("", $"line {number}: a block value starts with | or > and nothing after it.");
            var content = block.Select(l => l.TrimEnd()).ToArray();
            int indent = content.Where(l => l.Length > 0).Select(l => l.Length - l.TrimStart().Length).DefaultIfEmpty(0).Min();
            var kept = content.Select(l => l.Length >= indent ? l[indent..] : "");
            string value = rest[0] == '|'
                ? string.Join('\n', kept)
                : string.Join('\n', string.Join('\n', kept).Split("\n\n").Select(p => p.ReplaceLineEndings(" ")));
            return (value.Trim(), null);
        }

        string text = string.Join(' ', block.Select(l => l.Trim()).Where(l => l.Length > 0 && !l.StartsWith('#')).Prepend(rest)).Trim();
        if (text.Length == 0)
            return ("", null);
        if (text[0] is '"' or '\'')
        {
            var (quoted, after) = Quoted(text);
            return quoted is not null && WithoutComment(after).Length == 0
                ? (quoted, null)
                : ("", $"line {number}: the quoted value is not closed, holds an escape Tomte does not read, or has text after its closing quote.");
        }

        // These start something other than text in YAML (a list, a map, an anchor, a tag).
        if (text[0] is '[' or '{' or '&' or '*' or '!' or '%' or '@' or '`')
            return ("", $"line {number}: put this value in quotes.");
        return (WithoutComment(text), null);
    }

    // A comment starts at a # with a space before it, outside quotes.
    private static string WithoutComment(string text)
    {
        int hash = text.StartsWith('#') ? 0 : text.IndexOf(" #", StringComparison.Ordinal);
        return (hash < 0 ? text : text[..hash]).Trim();
    }

    /// <summary>The value of a quoted text and what follows its closing quote; null when the quote is not closed or holds an unknown escape.</summary>
    private static (string? Value, string After) Quoted(string text)
    {
        var value = new StringBuilder();
        char quote = text[0];
        for (int i = 1; i < text.Length; i++)
        {
            char c = text[i];
            if (quote == '\'' && c == '\'')
            {
                if (i + 1 < text.Length && text[i + 1] == '\'')
                {
                    value.Append('\'');
                    i++;
                    continue;
                }

                return (value.ToString(), text[(i + 1)..]);
            }

            if (quote == '"' && c == '"')
                return WellFormed(value.ToString()) ? (value.ToString(), text[(i + 1)..]) : (null, "");
            if (quote == '"' && c == '\\')
            {
                if (++i == text.Length)
                    return (null, "");
                switch (text[i])
                {
                    case '"' or '\\' or '/' or ' ':
                        value.Append(text[i]);
                        break;
                    case 'n':
                        value.Append('\n');
                        break;
                    case 't':
                        value.Append('\t');
                        break;
                    case 'u' when i + 4 < text.Length && ushort.TryParse(text.AsSpan(i + 1, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var code):
                        value.Append((char)code);
                        i += 4;
                        break;
                    default:
                        return (null, "");
                }

                continue;
            }

            value.Append(c);
        }

        return (null, "");
    }

    /// <summary>False for half of a surrogate pair standing alone, which an escape such as \uD800 can make and which is not text.</summary>
    internal static bool WellFormed(string text)
    {
        for (int i = 0; i < text.Length; i++)
        {
            if (char.IsSurrogate(text[i]) && !(char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[++i])))
                return false;
        }

        return true;
    }

    /// <summary>The format's rule for a name: 1 to 64 of a-z, 0-9 and single hyphens, no hyphen at either end.</summary>
    public static bool IsName(string name) =>
        name.Length is > 0 and <= 64 && name[0] != '-' && name[^1] != '-' && !name.Contains("--") && name.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-');

    /// <summary>The format's rules for the fields Tomte uses. The name must also be the folder's name.</summary>
    private static string? Check(Dictionary<string, string> values, string folder)
    {
        string name = values.GetValueOrDefault("name", "");
        if (name.Length == 0)
            return "the front matter has no name.";
        if (!IsName(name))
            return "name must be 1 to 64 lowercase letters, digits and single hyphens, with no hyphen at the start or end.";
        if (name != folder)
            return $"name is {name} but the folder is called {McpTools.Clean(folder, McpTools.MaxName)}; they must be the same.";
        string description = values.GetValueOrDefault("description", "");
        if (description.Length == 0)
            return "the front matter has no description.";
        if (description.Length > MaxDescription)
            return $"description is longer than {MaxDescription} characters.";
        if (values.TryGetValue("compatibility", out var compatibility) && compatibility.Length is 0 or > 500)
            return "compatibility must be 1 to 500 characters.";
        return null;
    }

    /// <summary>
    /// A tomte.json, read strictly: one object that holds exactly one of paletteCommand {name, prompt} and
    /// dropZone {label, accepts}, with no other keys. A skill with a bad one is not loaded at all.
    /// </summary>
    internal static (SkillCommand? Command, SkillDropZone? DropZone, string? Problem) Manifest(string skill, string json)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 8 });
        }
        catch (JsonException e)
        {
            return (null, null, $"it is not valid JSON (line {e.LineNumber + 1}).");
        }

        using (document)
        {
            if (Fields(document.RootElement, "tomte.json", ["paletteCommand", "dropZone"], out var root) is { } problem)
                return (null, null, problem);
            if (root.Count != 1)
                return (null, null, root.Count == 0
                    ? "it registers nothing. Add paletteCommand or dropZone, or delete the file."
                    : "it registers a palette command and a drop zone. A skill registers one of them.");

            if (root.TryGetValue("paletteCommand", out var command))
            {
                if (Fields(command, "paletteCommand", ["name", "prompt"], out var fields) is { } bad)
                    return (null, null, bad);
                string? name = Text(fields, "name", MaxCommandName, lines: false, out bad);
                string? prompt = bad is null ? Text(fields, "prompt", MaxPrompt, lines: true, out bad) : null;
                return bad is null ? (new SkillCommand(skill, name!, prompt!), null, null) : (null, null, "paletteCommand: " + bad);
            }

            if (Fields(root["dropZone"], "dropZone", ["label", "accepts"], out var zone) is { } wrong)
                return (null, null, wrong);
            string? label = Text(zone, "label", MaxCommandName, lines: false, out wrong);
            if (wrong is not null)
                return (null, null, "dropZone: " + wrong);
            if (!zone.TryGetValue("accepts", out var accepts) || accepts.ValueKind != JsonValueKind.Array || accepts.GetArrayLength() == 0
                || accepts.EnumerateArray().Any(a => a.ValueKind != JsonValueKind.String || !DropKinds.Contains(a.GetString()!))
                || accepts.EnumerateArray().Select(a => a.GetString()).Distinct().Count() != accepts.GetArrayLength())
            {
                return (null, null, $"dropZone: accepts must be a list of one or more of {string.Join(", ", DropKinds)}, each once.");
            }

            return (null, new SkillDropZone(skill, label!, [.. accepts.EnumerateArray().Select(a => a.GetString()!)]), null);
        }
    }

    /// <summary>The properties of an object that may only hold <paramref name="allowed"/>, each once.</summary>
    private static string? Fields(JsonElement value, string what, string[] allowed, out Dictionary<string, JsonElement> fields)
    {
        fields = new(StringComparer.Ordinal);
        if (value.ValueKind != JsonValueKind.Object)
            return $"{what} must be a JSON object.";
        foreach (var property in value.EnumerateObject())
        {
            if (!allowed.Contains(property.Name))
                return $"{what} has a key Tomte does not know: {McpTools.Clean(property.Name, McpTools.MaxName)}. It may hold {string.Join(" and ", allowed)}.";
            if (!fields.TryAdd(property.Name, property.Value))
                return $"{what} gives {property.Name} twice.";
        }

        return null;
    }

    // The text must already be clean: what Settings shows and the model gets is exactly what the author wrote.
    private static string? Text(Dictionary<string, JsonElement> fields, string key, int max, bool lines, out string? problem)
    {
        string? text = null;
        try
        {
            text = fields.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
        }
        catch (InvalidOperationException)
        {
            // An escape for half a surrogate pair cannot be turned into text.
            text = "\uD800";
        }

        problem = text is null || text.Trim().Length == 0 ? $"{key} must be text."
            : !WellFormed(text) ? $"{key} holds half of a surrogate pair, which is not text."
            : text.Length > max ? $"{key} is longer than {max} characters."
            : McpTools.Bound(text, text.Length, lines).Text != text
                ? lines ? $"{key} holds control characters." : $"{key} must be one line of text with single spaces and no control characters."
            : null;
        return problem is null ? text : null;
    }

    /// <summary>The files in a skill's folder that look like code to run, by folder or by extension; links are not followed.</summary>
    private static IReadOnlyList<string> FindScripts(string folder)
    {
        var options = new EnumerationOptions { RecurseSubdirectories = true, MaxRecursionDepth = 8, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
        return [.. Directory.EnumerateFiles(folder, "*", options).Take(1000)
            .Select(f => Path.GetRelativePath(folder, f).Replace('\\', '/'))
            .Where(f => f.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase) || ScriptExtensions.Contains(Path.GetExtension(f)))
            .Order(StringComparer.OrdinalIgnoreCase)];
    }
}
