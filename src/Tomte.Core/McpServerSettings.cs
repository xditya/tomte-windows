using System.Text;
using System.Text.Json.Serialization;
using Tomte.Core.Device;

namespace Tomte.Core;

public enum McpTransport { Stdio, Http }

/// <summary>
/// One MCP server the user added. A stdio server is a command Tomte starts as a child process; an
/// HTTP server is a Streamable HTTP endpoint. The optional header's value and the values of a stdio
/// server's environment variables are secrets and live in Credential Manager under
/// <see cref="CredentialId"/> and <see cref="EnvCredentialId"/>, never here.
/// </summary>
public sealed record McpServerSettings
{
    public string Name { get; init; } = "";

    public McpTransport Transport { get; init; }

    public string Command { get; init; } = "";

    public IReadOnlyList<string> Arguments { get; init; } = [];

    public string Url { get; init; } = "";

    /// <summary>Sent on every request when not empty, for example "Authorization".</summary>
    public string HeaderName { get; init; } = "";

    /// <summary>Names of the variables a stdio server gets in its environment, for example GITHUB_TOKEN.</summary>
    public IReadOnlyList<string> Environment { get; init; } = [];

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Tier overrides by tool key: the name the model sees plus a hash of what the tool is (the AI layer
    /// makes the key). A tool not listed is an External effect; see <see cref="TierFor"/> for one that changed.
    /// </summary>
    public IReadOnlyDictionary<string, ActionTier> ToolTiers { get; init; } = new Dictionary<string, ActionTier>();

    /// <summary>Stands between a server's prefix and a tool's own name in the name the model sees.</summary>
    public const string Separator = "__";

    [JsonIgnore]
    public string CredentialId => "mcp/" + Name;

    /// <summary>
    /// Where one variable's value is kept. It has its own prefix, so no server name can make it collide
    /// with a header's id, and the variable is the last part, since a variable name holds no slash.
    /// </summary>
    public string EnvCredentialId(string variable) => $"mcp-env/{Name}/{variable}";

    /// <summary>What the model sees in front of each of this server's tool names.</summary>
    [JsonIgnore]
    public string Prefix => Slug(Name);

    /// <summary>
    /// The tiers a tool of this server may have. Every call to an HTTP server is a network call by an
    /// MCP tool, so it cannot be Read. No MCP tool is app-local: Tomte cannot undo what it did.
    /// </summary>
    [JsonIgnore]
    public IReadOnlyList<ActionTier> AllowedTiers => Transport == McpTransport.Stdio
        ? [ActionTier.Read, ActionTier.ExternalEffect, ActionTier.Destructive]
        : [ActionTier.ExternalEffect, ActionTier.Destructive];

    /// <summary>
    /// The tier of a tool. A choice holds for the tool as it was when the user chose. When the tool has
    /// changed since (another key under the same name), Read falls back to External effect, but a tool
    /// once marked Destructive stays Destructive: a change may make a tier stricter, never weaker.
    /// </summary>
    public ActionTier TierFor(string toolKey)
    {
        if (ToolTiers.TryGetValue(toolKey, out var tier) && AllowedTiers.Contains(tier))
            return tier;
        string name = NameOf(toolKey);
        return ToolTiers.Any(t => t.Value == ActionTier.Destructive && NameOf(t.Key) == name) ? ActionTier.Destructive : ActionTier.ExternalEffect;
    }

    /// <summary>A tool key as Settings shows it: the name the model sees, without the hash behind the # sign.</summary>
    public static string NameOf(string toolKey) => toolKey.Split('#')[0];

    /// <summary>Whether a stored key (a tier override or an always-allow grant) is for a tool of this server.</summary>
    public bool OwnsKey(string toolKey) => toolKey.StartsWith(Prefix + Separator, StringComparison.Ordinal);

    /// <summary>What Tomte starts or connects to. A tool's permissions hold only while this stays the same.</summary>
    [JsonIgnore]
    public string Target => Transport == McpTransport.Stdio
        ? "stdio:" + string.Join('\0', [Command, .. Arguments])
        : "http:" + Url;

    /// <summary>
    /// This server as an edit of <paramref name="previous"/>: the on/off switch stays. The per-tool tiers
    /// stay only when the name and the target did not change, since they were chosen for those tools,
    /// except Destructive marks, which always stay (under the new name after a rename): an edit never
    /// makes a tool easier to run.
    /// </summary>
    public McpServerSettings After(McpServerSettings? previous)
    {
        if (previous is null)
            return this;
        var tiers = previous.Name == Name && previous.Target == Target
            ? previous.ToolTiers
            : previous.ToolTiers.Where(t => t.Value == ActionTier.Destructive)
                .ToDictionary(t => previous.OwnsKey(t.Key) ? Prefix + t.Key[previous.Prefix.Length..] : t.Key, t => t.Value);
        return this with { Enabled = previous.Enabled, ToolTiers = tiers };
    }

    /// <summary>
    /// The user's choice for one tool. It replaces whatever was stored under that tool's name, a
    /// Destructive mark kept from before a change included.
    /// </summary>
    public McpServerSettings WithTier(string toolKey, ActionTier tier)
    {
        string name = NameOf(toolKey);
        var tiers = ToolTiers.Where(t => NameOf(t.Key) != name).ToDictionary();
        tiers[toolKey] = tier;
        return this with { ToolTiers = tiers };
    }

    /// <summary>
    /// Drops the choices for tools the server no longer has as they were when the user chose.
    /// Destructive marks stay, because <see cref="TierFor"/> still applies them by name.
    /// </summary>
    public McpServerSettings WithTiersFor(IReadOnlySet<string> toolKeys) =>
        this with { ToolTiers = ToolTiers.Where(t => toolKeys.Contains(t.Key) || t.Value == ActionTier.Destructive).ToDictionary() };

    [JsonIgnore]
    public string Summary => Transport == McpTransport.Stdio
        ? string.Join(' ', [Command, .. Arguments.Select(Quote)])
        : Url;

    /// <summary>Null when the server can be saved next to <paramref name="others"/>; otherwise a message for the user.</summary>
    public static string? Validate(IEnumerable<McpServerSettings> others, McpServerSettings server)
    {
        if (server.Name.Trim().Length == 0 || server.Prefix.Length == 0)
            return "Give the server a name with at least one letter or digit.";
        if (others.Any(o => o.Prefix == server.Prefix))
            return $"A server named like \"{server.Name}\" is already added.";
        if (server.Transport == McpTransport.Stdio)
        {
            return server.Command.Trim().Length == 0
                ? "Enter the command that starts the server, for example npx."
                : server.Environment.Select((v, i) => CheckVariable(server.Environment.Take(i), v)).FirstOrDefault(p => p is not null);
        }

        if (!Uri.TryCreate(server.Url, UriKind.Absolute, out var uri) || uri.Scheme is not ("http" or "https"))
            return "Enter the server's http or https URL.";
        if (server.HeaderName.Length == 0)
            return null;
        // The platform's own rule: false for a name that is not a token and for content headers such as
        // Content-Type, which a request cannot carry here.
        using var probe = new HttpRequestMessage();
        if (!probe.Headers.TryAddWithoutValidation(server.HeaderName, "x"))
            return "That header cannot be used. Use a request header such as Authorization or X-Api-Key.";
        if (uri.Scheme == "http" && !uri.IsLoopback)
            return "A header is only sent over https. Use an https URL or remove the header.";
        return null;
    }

    /// <summary>
    /// Null when <paramref name="variable"/> can be added after <paramref name="others"/>. Letters, digits
    /// and underscores only, so a name never holds the = or the slash that would break the environment
    /// block or the credential id. Windows reads variable names without regard to case.
    /// </summary>
    public static string? CheckVariable(IEnumerable<string> others, string variable)
    {
        if (variable.Length == 0 || char.IsAsciiDigit(variable[0]) || !variable.All(c => char.IsAsciiLetterOrDigit(c) || c == '_'))
            return "A variable name has only letters, digits and underscores, and does not start with a digit.";
        return others.Contains(variable, StringComparer.OrdinalIgnoreCase) ? $"{variable} is already in the list." : null;
    }

    /// <summary>Splits an arguments line at spaces; double quotes keep spaces inside one argument.</summary>
    public static IReadOnlyList<string> SplitArguments(string line)
    {
        var result = new List<string>();
        var current = new StringBuilder();
        bool quoted = false, any = false;
        foreach (char c in line)
        {
            if (c == '"')
            {
                quoted = !quoted;
                any = true;
            }
            else if (char.IsWhiteSpace(c) && !quoted)
            {
                if (any)
                    result.Add(current.ToString());
                current.Clear();
                any = false;
            }
            else
            {
                current.Append(c);
                any = true;
            }
        }

        if (any)
            result.Add(current.ToString());
        return result;
    }

    public static string JoinArguments(IEnumerable<string> arguments) => string.Join(' ', arguments.Select(Quote));

    private static string Quote(string argument) => argument.Length == 0 || argument.Any(char.IsWhiteSpace) ? $"\"{argument}\"" : argument;

    // Tool names must match ^[a-zA-Z0-9_-]{1,64}$ at every provider and start with a letter at Gemini,
    // so the prefix keeps to that and stays short. Runs of other characters become one underscore: the
    // prefix never holds the double underscore that separates it from the tool name.
    private static string Slug(string name)
    {
        var slug = new StringBuilder();
        foreach (char c in name.ToLowerInvariant())
        {
            if (c is (>= 'a' and <= 'z') or (>= '0' and <= '9'))
                slug.Append(c);
            else if (slug.Length > 0 && slug[^1] != '_')
                slug.Append('_');
        }

        if (slug.Length > 0 && char.IsAsciiDigit(slug[0]))
            slug.Insert(0, 's');
        return slug.ToString(0, Math.Min(slug.Length, 20)).TrimEnd('_');
    }
}
