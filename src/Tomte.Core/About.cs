using Tomte.Core.Search;

namespace Tomte.Core;

/// <summary>What a link on the About page in Settings is: where it shows and how it opens.</summary>
public enum AboutLinkKind
{
    /// <summary>The developer's page, under "Made by".</summary>
    Profile,

    /// <summary>Where the code is, under "Made by".</summary>
    Source,

    /// <summary>A donation page under Support: a button that opens the browser.</summary>
    Donation,

    /// <summary>An ID or address to pay to under Support: shown in full with a Copy button, never opened.</summary>
    Payment,
}

/// <summary>One link on the About page. <paramref name="Name"/> is what the page shows for it.</summary>
public sealed record AboutLink(AboutLinkKind Kind, string Name, string Value);

/// <summary>
/// Who made Tomte and how to support it, for the About page in Settings. Nothing here goes online: a page opens in the
/// browser only when its button is clicked.
/// </summary>
public static class About
{
    public const string Developer = "Aditya S";

    /// <summary>
    /// Every link the About page can show, in its order. A link with no value is not shown, and with no donation page or
    /// payment shown the Support section is not shown, so a link is added, changed or taken out on its own line.
    /// </summary>
    public static readonly IReadOnlyList<AboutLink> Links =
    [
        new(AboutLinkKind.Profile, "Aditya S on GitHub", "https://github.com/xditya"),
        new(AboutLinkKind.Source, "Source code", "https://github.com/xditya/tomte-windows"),
        new(AboutLinkKind.Donation, "GitHub Sponsors", "https://github.com/sponsors/xditya"),
        new(AboutLinkKind.Donation, "Buy Me a Coffee", "https://buymeacoffee.com/xditya"),
        new(AboutLinkKind.Donation, "Ko-fi", "https://ko-fi.com/xditya"),
        new(AboutLinkKind.Payment, "UPI ID", "botzhub@ybl"),
        // The coin and the network, since money sent on another network is lost.
        new(AboutLinkKind.Payment, "USDT on BNB Smart Chain (BEP20)", "0xc8cbfa55be28b0836a4ab830ea85c64516dffaf3"),
        new(AboutLinkKind.Payment, "USDT on Tron (TRC20)", "THDKzV4ZKeDBsrFRSbL5XZgqVQUEkPfWcW"),
    ];

    /// <summary>
    /// The links the page shows, in their order: a page only with a web address (the one <see cref="WebSearch.Open"/>
    /// opens), a payment only with a value.
    /// </summary>
    public static IReadOnlyList<AboutLink> Shown(IEnumerable<AboutLink> links) =>
        [.. links.Where(l => l.Kind == AboutLinkKind.Payment ? !string.IsNullOrWhiteSpace(l.Value) : WebSearch.Address(l.Value) is not null)];

    /// <summary>The donation pages and payments shown under Support; none means the page has no Support section.</summary>
    public static IReadOnlyList<AboutLink> Support(IEnumerable<AboutLink> links) =>
        [.. Shown(links).Where(l => l.Kind is AboutLinkKind.Donation or AboutLinkKind.Payment)];

    /// <summary>
    /// The version the page shows: the package's when Tomte is installed, else the build's own, where the SDK's
    /// "+commit" ending becomes the commit's first 7 characters in brackets.
    /// </summary>
    public static string Version(string? package, string? build)
    {
        if (package is not null)
        {
            return package;
        }

        string[] parts = (build ?? "").Split('+', 2);
        return parts is [var version, { Length: > 0 } commit] ? $"{version} ({commit[..Math.Min(7, commit.Length)]})" : parts[0];
    }
}
