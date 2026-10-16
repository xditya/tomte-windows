using System.Diagnostics;
using Tomte.Core.Device;

namespace Tomte.Core.Search;

/// <summary>The search engine the search box's Open in browser row asks. Stored by name in settings.json.</summary>
public enum WebSearchEngine
{
    Google,
    Bing,
    DuckDuckGo,
}

/// <summary>
/// The Open in browser row: the typed text searched for with the chosen engine, or opened when it already is a web
/// address. Nothing is sent until the row is chosen, and then the browser does it.
/// </summary>
public static class WebSearch
{
    // The endings of names taken as web addresses when typed without http:// or https://. Only common ones, and none
    // that is also a common file extension (md, py, rs, sh, pl, ps, zip, mov), so "readme.md" or "notes.txt" is searched
    // for, not opened. With http:// or https:// any address opens.
    private static readonly HashSet<string> Endings = new(StringComparer.OrdinalIgnoreCase)
    {
        "com", "org", "net", "edu", "gov", "mil", "int", "io", "dev", "app", "co", "me", "info", "biz", "xyz", "tech", "site",
        "online", "store", "shop", "blog", "page", "cloud", "ai", "tv", "gg", "news", "wiki", "eu", "uk", "us", "ca", "au",
        "nz", "ie", "de", "fr", "nl", "be", "at", "ch", "it", "es", "pt", "se", "no", "dk", "fi", "is", "pl", "cz", "gr", "ru",
        "ua", "tr", "in", "jp", "cn", "kr", "tw", "hk", "sg", "my", "id", "th", "vn", "ph", "pk", "br", "mx", "ar", "cl",
        "za", "ng", "ke", "eg", "il", "ae", "sa",
    };

    public static string Name(WebSearchEngine engine) => engine switch
    {
        WebSearchEngine.Bing => "Bing",
        WebSearchEngine.DuckDuckGo => "DuckDuckGo",
        _ => "Google",
    };

    /// <summary>The engine's results page for the text, the text escaped as the one query value.</summary>
    public static Uri SearchUri(WebSearchEngine engine, string text) => new(engine switch
    {
        WebSearchEngine.Bing => "https://www.bing.com/search?q=",
        WebSearchEngine.DuckDuckGo => "https://duckduckgo.com/?q=",
        _ => "https://www.google.com/search?q=",
    } + Uri.EscapeDataString(text.Trim()));

    /// <summary>
    /// The web address the text is, or null when it is words to search for. An address has no spaces and is either a
    /// full http:// or https:// address, or a name made of dotted parts that ends in a common web ending (example.com,
    /// docs.example.co.uk/path), localhost or an IPv4 address, each with an optional port and path; those open over https,
    /// localhost and IP addresses over http. Nothing else is opened: no other scheme, no "c#", no "file.txt".
    /// </summary>
    public static Uri? Address(string text)
    {
        string t = text.Trim();
        if (t.Length == 0 || t.Any(char.IsWhiteSpace) || t.Contains('@', StringComparison.Ordinal))
        {
            return null;
        }

        if (t.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || t.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            return Uri.TryCreate(t, UriKind.Absolute, out var full) && full.Scheme is "http" or "https" && full.Host.Length > 0 ? full : null;
        }

        int end = t.IndexOfAny(['/', '?', '#']);
        string authority = end < 0 ? t : t[..end];
        int colon = authority.LastIndexOf(':');
        string host = colon < 0 ? authority : authority[..colon];
        if (colon >= 0 && !(colon < authority.Length - 1 && authority[(colon + 1)..].All(char.IsAsciiDigit)))
        {
            return null;
        }

        string? scheme = host.Equals("localhost", StringComparison.OrdinalIgnoreCase) || IsIpv4(host) ? "http"
            : IsName(host) ? "https"
            : null;
        return scheme is not null && Uri.TryCreate($"{scheme}://{t}", UriKind.Absolute, out var uri) ? uri : null;
    }

    /// <summary>The row for the typed text: open the address it is (with a leading open, go to or show dropped), or search for it.</summary>
    public static SearchItem Row(string typed, WebSearchEngine engine) => (Address(SearchRank.Query(typed)) ?? Address(typed)) is { } address
        ? new(SearchKind.Web, "Open in browser", address.AbsoluteUri, address.AbsoluteUri)
        : new(SearchKind.Web, $"Search {Name(engine)}", SearchUri(engine, typed).AbsoluteUri, $"\"{typed.Trim()}\"");

    /// <summary>Opens an http or https address in the default browser through the shell; anything else is refused. A worker thread only.</summary>
    public static ActionResult Open(string address) =>
        Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            ? Apps.Launch(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true }, "the browser")
            : ActionResult.Fail("That is not a web address.");

    // Two or more dotted parts of letters, digits and hyphens, the last a common web ending.
    private static bool IsName(string host)
    {
        var parts = host.Split('.');
        return parts.Length >= 2
            && parts.All(p => p.Length is > 0 and <= 63 && p[0] != '-' && p[^1] != '-' && p.All(c => char.IsAsciiLetterOrDigit(c) || c == '-'))
            && Endings.Contains(parts[^1]);
    }

    private static bool IsIpv4(string host)
    {
        var parts = host.Split('.');
        return parts.Length == 4 && parts.All(p => p.Length is > 0 and <= 3 && p.All(char.IsAsciiDigit) && int.Parse(p, System.Globalization.CultureInfo.InvariantCulture) <= 255);
    }
}
