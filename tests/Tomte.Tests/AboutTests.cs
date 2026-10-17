using System.Numerics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Xml.Linq;
using Tomte.Core;
using Xunit;

namespace Tomte.Tests;

public sealed class AboutTests
{
    [Fact]
    public void ALinkWithNoValueIsNotShownAndNoDonationMeansNoSupport()
    {
        AboutLink[] links =
        [
            new(AboutLinkKind.Profile, "Profile", ""),
            new(AboutLinkKind.Source, "Source code", "https://example.com/code"),
            new(AboutLinkKind.Donation, "Page", "  "),
            new(AboutLinkKind.Donation, "Not a web page", "javascript:alert(1)"),
            new(AboutLinkKind.Payment, "ID", ""),
        ];
        Assert.Equal(["Source code"], About.Shown(links).Select(l => l.Name));
        Assert.Empty(About.Support(links));

        // A donation page needs a web address; a payment only a value.
        links[2] = links[2] with { Value = "https://example.com/donate" };
        links[4] = links[4] with { Value = "name@bank" };
        Assert.Equal(["Page", "ID"], About.Support(links).Select(l => l.Name));
    }

    [Fact]
    public void EveryLinkWithAValueIsShown()
    {
        // A mistyped web address would hide its button without a word.
        Assert.Equal(About.Links.Where(l => l.Value.Trim().Length > 0), About.Shown(About.Links));
    }

    [Fact]
    public void TheUsdtAddressesAreWellFormed()
    {
        Assert.Matches("^0x[0-9a-fA-F]{40}$", Payment("BEP20"));

        string tron = Payment("TRC20");
        Assert.True(TronChecksumHolds(tron));
        Assert.False(TronChecksumHolds(tron[..^1] + (tron[^1] == 'W' ? 'X' : 'W')));
    }

    [Fact]
    public void TheVersionIsThePackagesWhenInstalledElseTheBuildsWithItsCommit()
    {
        Assert.Equal("0.2.0.0", About.Version("0.2.0.0", "0.1.0+285ff22abcdef"));
        Assert.Equal("0.1.0 (285ff22)", About.Version(null, "0.1.0+285ff22abcdef"));
        Assert.Equal("0.1.0", About.Version(null, "0.1.0"));
    }

    [Fact]
    public void TheNoticesNameEveryShippedPackageAtItsVersion()
    {
        string root = RepoRoot();
        string notices = File.ReadAllText(Path.Combine(root, "src", "Tomte.Shell", "THIRD-PARTY-NOTICES.txt"));
        // Build tools and test packages do not ship. A new or upgraded package fails here until the notices name it.
        string[] notShipped = ["Microsoft.Windows.CsWin32", "xunit", "xunit.runner.visualstudio", "Microsoft.NET.Test.Sdk"];
        foreach (var package in XDocument.Load(Path.Combine(root, "Directory.Packages.props")).Descendants("PackageVersion"))
        {
            string id = (string)package.Attribute("Include")!;
            if (!notShipped.Contains(id))
            {
                Assert.Contains($"  {id} {(string)package.Attribute("Version")!}", notices, StringComparison.Ordinal);
            }
        }
    }

    private static string Payment(string network) =>
        About.Links.Single(l => l.Kind == AboutLinkKind.Payment && l.Name.Contains(network, StringComparison.Ordinal)).Value;

    // A Tron address is base58 of 25 bytes: 0x41, the 20-byte account, then the first 4 bytes of SHA-256 taken twice over
    // the 21 bytes before them.
    private static bool TronChecksumHolds(string address)
    {
        const string Digits = "123456789ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnopqrstuvwxyz";
        BigInteger n = 0;
        foreach (char c in address)
        {
            int digit = Digits.IndexOf(c, StringComparison.Ordinal);
            Assert.True(digit >= 0, $"'{c}' is not a base58 digit");
            n = (n * 58) + digit;
        }

        byte[] bytes = [.. Enumerable.Repeat((byte)0, address.TakeWhile(c => c == '1').Count()), .. n.ToByteArray(isUnsigned: true, isBigEndian: true)];
        return bytes.Length == 25 && bytes[0] == 0x41 && SHA256.HashData(SHA256.HashData(bytes.AsSpan(0, 21))).AsSpan(0, 4).SequenceEqual(bytes.AsSpan(21));
    }

    private static string RepoRoot([CallerFilePath] string here = "") => Path.GetFullPath(Path.Combine(Path.GetDirectoryName(here)!, "..", ".."));
}
