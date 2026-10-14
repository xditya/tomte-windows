using System.Text.RegularExpressions;
using Tomte.Core;

namespace Tomte.AI.Mcp;

/// <summary>
/// The secrets of an MCP server that Settings keeps in Credential Manager: an HTTP server's header value
/// under <see cref="McpServerSettings.CredentialId"/>, and the value of each of a stdio server's
/// environment variables under <see cref="McpServerSettings.EnvCredentialId"/>. The settings file holds
/// the names only. Reading, saving and deleting are passed in, so tests never touch the real vault.
/// </summary>
public static partial class McpSecrets
{
    /// <summary>
    /// The shortest value Settings saves. A shorter one cannot be cut out of what a server sends back
    /// without mangling ordinary text, so every text that holds it would be withheld.
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
        var wanted = new List<(string Id, string? Saved, string Subject, string Missing)>();
        if (now.HeaderName.Length > 0)
            wanted.Add((now.CredentialId, before is { HeaderName.Length: > 0 } ? before.CredentialId : null, "The header value", "Enter the header value."));
        foreach (string variable in now.Environment)
        {
            bool had = before?.Environment.Contains(variable, StringComparer.OrdinalIgnoreCase) == true;
            wanted.Add((now.EnvCredentialId(variable), had ? before!.EnvCredentialId(variable) : null, $"The value of {variable}", $"Enter a value for {variable}."));
        }

        bool keep = before is not null && before.Target == now.Target;
        foreach (var (id, saved, subject, missing) in wanted)
        {
            if (typed.TryGetValue(id, out var value))
            {
                if (value.Length < MinLength)
                    return $"{subject} needs at least {MinLength} characters. Tomte hides it in what the server sends back, and a shorter value cannot be told apart from ordinary text.";
                values[id] = value;
            }
            else if (keep && saved is not null && read(saved) is { } kept)
            {
                values[id] = kept;
            }
            else
            {
                return missing;
            }
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

    /// <summary>
    /// Whether an arguments line looks like it holds a token, for a warning in Settings that never blocks
    /// a save. Three signs: a value after an option named like --token, --api-key, --secret or --password;
    /// a word that starts the way known tokens do (sk-, ghp_, github_pat_, xoxb-, glpat-, AIza and
    /// others below, or Bearer and a value); or a run of 24 or more letters, digits and _+=- with both
    /// letters and digits that is as varied as a random key (4 bits of entropy per character or more),
    /// or is 32 or more hex digits, which have only 16 symbols and score lower.
    /// </summary>
    public static bool LooksLikeToken(string arguments) =>
        SecretOption().IsMatch(arguments) || KnownPrefix().IsMatch(arguments) || Run().Matches(arguments).Any(m => Random(m.Value));

    private static bool Random(string run)
    {
        if (!run.Any(char.IsAsciiDigit) || !run.Any(char.IsAsciiLetter))
            return false;
        if (run.Length >= 32 && run.All(char.IsAsciiHexDigit))
            return true;
        double entropy = -run.GroupBy(c => c).Sum(g => (double)g.Count() / run.Length * Math.Log2((double)g.Count() / run.Length));
        return entropy >= 4.0;
    }

    // a fixed list of option words and prefixes; a token that matches none of them and is not
    // a long random run gets no warning.
    [GeneratedRegex(@"--(?:[A-Za-z0-9]+[-_])*(?:token|api[-_]?key|secret(?:[-_]?key)?|password|access[-_]?key)(?:=|\s+)(?!-)\S", RegexOptions.IgnoreCase)]
    private static partial Regex SecretOption();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:(?:sk-|sk_live_|ghp_|gho_|ghu_|ghs_|github_pat_|xox[abpr]-|glpat-|AIza)[A-Za-z0-9_-]{8,}|Bearer\s+\S{8,})")]
    private static partial Regex KnownPrefix();

    [GeneratedRegex(@"[A-Za-z0-9_+=-]{24,}")]
    private static partial Regex Run();
}
