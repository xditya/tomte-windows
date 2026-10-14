using Tomte.AI.Mcp;
using Tomte.Core;
using Xunit;

namespace Tomte.Tests;

/// <summary>Header and variable values of MCP servers, against a vault in memory in place of Credential Manager.</summary>
public class McpSecretsTests
{
    private static readonly McpServerSettings Files = new() { Name = "Files", Command = "npx", Arguments = ["-y", "server"], Environment = ["GITHUB_TOKEN"] };
    private static readonly McpServerSettings Web = new() { Name = "Web", Transport = McpTransport.Http, Url = "https://example.com/mcp", HeaderName = "X-Api-Key" };

    // Credential Manager ignores case in its target names.
    private readonly Dictionary<string, string> _vault = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>What Settings does on Save: collect the values, then write them.</summary>
    private string? Save(McpServerSettings? before, McpServerSettings now, Dictionary<string, string>? typed = null)
    {
        string? error = McpSecrets.Collect(before, now, typed ?? [], id => _vault.GetValueOrDefault(id), out var values);
        if (error is null)
            Write(before, now, values);
        return error;
    }

    private void Write(McpServerSettings? before, McpServerSettings? now, Dictionary<string, string> values) =>
        McpSecrets.Write(before, now, values, (id, value) => _vault[id] = value, id => _vault.Remove(id));

    [Fact]
    public void VariableValuesGoToTheVaultAndOnlyTheirNamesToTheSettingsFile()
    {
        var files = Files with { Environment = ["GITHUB_TOKEN", "API_KEY"] };
        Assert.Null(Save(null, files, new() { [files.EnvCredentialId("GITHUB_TOKEN")] = "ghp_0123456789abcdef", [files.EnvCredentialId("API_KEY")] = "key-0123456789" }));
        Assert.Equal("ghp_0123456789abcdef", _vault["mcp-env/Files/GITHUB_TOKEN"]);
        Assert.Equal("key-0123456789", _vault["mcp-env/Files/API_KEY"]);
        Assert.Equal(2, _vault.Count);

        string json = SettingsStore.Serialize(new TomteSettings { McpServers = [files] });
        Assert.Contains("\"GITHUB_TOKEN\"", json);
        Assert.DoesNotContain("ghp_0123456789abcdef", json);
        Assert.DoesNotContain("key-0123456789", json);
    }

    [Fact]
    public void AnEmptyBoxKeepsTheSavedValueOnlyForTheSameCommandAndArguments()
    {
        Assert.Null(Save(null, Files, new() { [Files.EnvCredentialId("GITHUB_TOKEN")] = "first-value-123" }));

        // A rename with the box left empty moves the value to the new name.
        var docs = Files with { Name = "Docs" };
        Assert.Null(Save(Files, docs));
        Assert.Equal(["mcp-env/Docs/GITHUB_TOKEN"], _vault.Keys);
        Assert.Equal("first-value-123", _vault["mcp-env/Docs/GITHUB_TOKEN"]);

        // Other arguments: the value was entered for what ran before, so it has to be entered again.
        var other = docs with { Arguments = ["-y", "other-server"] };
        Assert.Equal("Enter a value for GITHUB_TOKEN.", Save(docs, other));
        Assert.Equal("first-value-123", _vault["mcp-env/Docs/GITHUB_TOKEN"]);
        Assert.Null(Save(docs, other, new() { [other.EnvCredentialId("GITHUB_TOKEN")] = "second-value-456" }));
        Assert.Equal("second-value-456", _vault["mcp-env/Docs/GITHUB_TOKEN"]);

        // A new variable has no saved value to keep.
        Assert.Equal("Enter a value for SECOND.", Save(other, other with { Environment = ["GITHUB_TOKEN", "SECOND"] }));
    }

    [Fact]
    public void DeletingAVariableOrRemovingTheServerDeletesItsEntries()
    {
        var files = Files with { Environment = ["A_TOKEN", "B_TOKEN"] };
        Assert.Null(Save(null, files, new() { [files.EnvCredentialId("A_TOKEN")] = "a-value-123", [files.EnvCredentialId("B_TOKEN")] = "b-value-123" }));

        var fewer = files with { Environment = ["B_TOKEN"] };
        Assert.Null(Save(files, fewer));
        Assert.Equal(["mcp-env/Files/B_TOKEN"], _vault.Keys);

        // What the Remove button does.
        Write(fewer, null, []);
        Assert.Empty(_vault);

        Assert.Null(Save(null, Web, new() { [Web.CredentialId] = "header-value-123" }));
        Write(Web, null, []);
        Assert.Empty(_vault);
    }

    [Fact]
    public void ARenameThatOnlyChangesCaseKeepsTheValues()
    {
        // Saving "mcp-env/FILES/..." and then deleting "mcp-env/Files/..." would delete the value just saved.
        Assert.Null(Save(null, Files, new() { [Files.EnvCredentialId("GITHUB_TOKEN")] = "first-value-123" }));
        var upper = Files with { Name = "FILES", Environment = ["github_token"] };
        Assert.Null(Save(Files, upper));
        Assert.Equal("first-value-123", Assert.Single(_vault).Value);
    }

    [Fact]
    public void TheHeaderValueKeepsItsRules()
    {
        Assert.Equal("Enter the header value.", Save(null, Web));
        Assert.Null(Save(null, Web, new() { [Web.CredentialId] = "header-value-123" }));
        Assert.Null(Save(Web, Web with { Enabled = false }));
        Assert.Equal("header-value-123", _vault[Web.CredentialId]);
        Assert.Equal("Enter the header value.", Save(Web, Web with { Url = "https://elsewhere.example/mcp" }));

        // The header is dropped: its entry goes.
        Assert.Null(Save(Web, Web with { HeaderName = "" }));
        Assert.Empty(_vault);
    }

    [Fact]
    public void VariableNamesAreCheckedWhenAddedAndWhenTheServerIsSaved()
    {
        Assert.Null(McpServerSettings.CheckVariable([], "GITHUB_TOKEN"));
        Assert.Null(McpServerSettings.CheckVariable([], "_x1"));
        Assert.NotNull(McpServerSettings.CheckVariable([], ""));
        Assert.NotNull(McpServerSettings.CheckVariable([], "1TOKEN"));
        Assert.NotNull(McpServerSettings.CheckVariable([], "A=B"));
        Assert.NotNull(McpServerSettings.CheckVariable([], "a/b"));
        Assert.NotNull(McpServerSettings.CheckVariable([], "MY TOKEN"));
        Assert.Equal("TOKEN is already in the list.", McpServerSettings.CheckVariable(["token"], "TOKEN"));

        // A hand-edited settings file gets the same check.
        Assert.Null(McpServerSettings.Validate([], Files));
        Assert.NotNull(McpServerSettings.Validate([], Files with { Environment = ["A", "a"] }));
        Assert.NotNull(McpServerSettings.Validate([], Files with { Environment = ["PATH=x"] }));
    }

    [Fact]
    public void EverySavedValueIsHiddenLongestFirst()
    {
        Assert.Equal("a [hidden] b [hidden]", McpTools.Hide("a abcdefgh1234 b abcdefgh", ["abcdefgh", "abcdefgh1234"]));
        // One short value among them is enough to withhold a text that holds it.
        Assert.Equal(McpTools.Withheld, McpTools.Hide("version 2 of abcdefgh1234", ["abcdefgh1234", "2"]));
        Assert.Equal("nothing here", McpTools.Hide("nothing here", ["abcdefgh1234", "", "2"]));
    }

    [Fact]
    public async Task AVariableWithoutASavedValueStopsTheStartAndIsNamed()
    {
        var fake = new FakeMcpServers();
        await using var tools = new McpTools([Files], null, fake.ConnectAsync, _ => null);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.ListAsync(default));
        Assert.Contains("\"Files\" did not start: The value of GITHUB_TOKEN is not in Windows Credential Manager. Enter it again in Settings.", e.Message);
        Assert.Equal(0, fake.Starts);
    }

    [Fact]
    public async Task AVariableValueNeverReachesAnErrorMessage()
    {
        var fake = new FakeMcpServers { FailingMessage = "login failed for token tomte-secret-value-99" };
        fake.Failing.Add(Files.Name);
        await using var tools = new McpTools([Files], null, fake.ConnectAsync, id => id == Files.EnvCredentialId("GITHUB_TOKEN") ? "tomte-secret-value-99" : null);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.ListAsync(default));
        Assert.Contains("login failed for token [hidden]", e.Message);
        Assert.DoesNotContain("tomte-secret", e.ToString());
    }
}
