using System.Text;
using System.Text.Json.Serialization;
using Tomte.Core.Device;

namespace Tomte.Core;

public enum McpTransport { Stdio, Http }

/// <summary>
/// One MCP server the user added. A stdio server is a command Tomte starts as a child process; an
/// HTTP server is a Streamable HTTP endpoint. The optional header's value is a secret and lives in
/// Credential Manager under <see cref="CredentialId"/>, never here.
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

    public bool Enabled { get; init; } = true;

    /// <summary>
    /// Tier overrides by tool key: the name the model sees plus a hash of what the tool is (the AI layer
    /// makes the key). A tool not listed, or one that changed since the user chose, is an External effect.
    /// </summary>
    public IReadOnlyDictionary<string, ActionTier> ToolTiers { get; init; } = new Dictionary<string, ActionTier>();

    [JsonIgnore]
    public string CredentialId => "mcp/" + Name;

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

    public ActionTier TierFor(string toolKey) =>
        ToolTiers.TryGetValue(toolKey, out var tier) && AllowedTiers.Contains(tier) ? tier : ActionTier.ExternalEffect;

    /// <summary>What Tomte starts or connects to. A tool's permissions hold only while this stays the same.</summary>
    [JsonIgnore]
    public string Target => Transport == McpTransport.Stdio
        ? "stdio:" + string.Join('\0', [Command, .. Arguments])
        : "http:" + Url;

    /// <summary>
    /// This server as an edit of <paramref name="previous"/>: the on/off switch stays, and the per-tool
    /// tiers stay only when the name and the target did not change, since they were chosen for those tools.
    /// </summary>
    public McpServerSettings After(McpServerSettings? previous) => previous is null ? this : this with
    {
        Enabled = previous.Enabled,
        ToolTiers = previous.Name == Name && previous.Target == Target ? previous.ToolTiers : new Dictionary<string, ActionTier>(),
    };

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
            return server.Command.Trim().Length == 0 ? "Enter the command that starts the server, for example npx." : null;
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
