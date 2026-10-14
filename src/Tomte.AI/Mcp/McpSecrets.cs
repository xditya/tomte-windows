using Tomte.Core;

namespace Tomte.AI.Mcp;

/// <summary>
/// The secrets of an MCP server that Settings keeps in Credential Manager: an HTTP server's header value
/// under <see cref="McpServerSettings.CredentialId"/>, and the value of each of a stdio server's
/// environment variables under <see cref="McpServerSettings.EnvCredentialId"/>. The settings file holds
/// the names only. Reading, saving and deleting are passed in, so tests never touch the real vault.
/// </summary>
public static class McpSecrets
{
    /// <summary>
    /// The shortest value that is cut out of what a server sends back. Cutting out a shorter one would
    /// mangle ordinary text, so a text that holds it is withheld whole.
    /// </summary>
    public const int MinLength = 8;

    /// <summary>
    /// Null and the values an edit saves, by credential id; or a message for the user. A typed value is
    /// taken as typed. An empty box keeps the saved value while the command and its arguments, or the URL,
    /// stay the same, also through a rename: the value was entered for what ran then.
    /// </summary>
    /// <param name="typed">What the user typed, by the credential id it is for on <paramref name="now"/>; empty boxes left out.</param>
    public static string? Collect(McpServerSettings? before, McpServerSettings now, IReadOnlyDictionary<string, string> typed, Func<string, string?> read, out Dictionary<string, string> values)
    {
        values = [];
        var wanted = new List<(string Id, string? Saved, string Missing)>();
        if (now.HeaderName.Length > 0)
            wanted.Add((now.CredentialId, before is { HeaderName.Length: > 0 } ? before.CredentialId : null, "Enter the header value."));
        foreach (string variable in now.Environment)
        {
            bool had = before?.Environment.Contains(variable, StringComparer.OrdinalIgnoreCase) == true;
            wanted.Add((now.EnvCredentialId(variable), had ? before!.EnvCredentialId(variable) : null, $"Enter a value for {variable}."));
        }

        bool keep = before is not null && before.Target == now.Target;
        foreach (var (id, saved, missing) in wanted)
        {
            if (typed.TryGetValue(id, out var value))
                values[id] = value;
            else if (keep && saved is not null && read(saved) is { } kept)
                values[id] = kept;
            else
                return missing;
        }

        return null;
    }

    /// <summary>
    /// Saves <paramref name="values"/>, then deletes every entry <paramref name="before"/> or
    /// <paramref name="now"/> can have that is not among them: what a removed server (no
    /// <paramref name="now"/>), a renamed one, a deleted variable or a dropped header left behind.
    /// Credential Manager ignores case in its names, so the comparison does too.
    /// </summary>
    public static void Write(McpServerSettings? before, McpServerSettings? now, IReadOnlyDictionary<string, string> values, Action<string, string> save, Action<string> delete)
    {
        foreach (var (id, value) in values)
            save(id, value);
        foreach (string id in Ids(before).Concat(Ids(now)).Except(values.Keys, StringComparer.OrdinalIgnoreCase))
            delete(id);

        static IEnumerable<string> Ids(McpServerSettings? server) =>
            server is null ? [] : [server.CredentialId, .. server.Environment.Select(server.EnvCredentialId)];
    }
}
