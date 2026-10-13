using System.IO.Pipelines;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tomte.AI;
using Tomte.AI.Mcp;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Device;
using Xunit;

namespace Tomte.Tests;

/// <summary>An MCP server in this process, joined to the client by two pipes. Counts starts, stops and calls.</summary>
internal sealed class FakeMcpServers
{
    public int Starts;
    public int Stops;
    public List<string> Calls { get; } = [];
    public HashSet<string> Failing { get; } = [];

    public async Task<McpClient> ConnectAsync(McpServerSettings settings, CancellationToken ct)
    {
        if (Failing.Contains(settings.Name))
            throw new IOException("the command was not found");
        Interlocked.Increment(ref Starts);
        var toServer = new Pipe();
        var toClient = new Pipe();
        var options = new McpServerOptions
        {
            ServerInfo = new Implementation { Name = settings.Name, Version = "1" },
            ToolCollection =
            [
                McpServerTool.Create((string text) =>
                {
                    lock (Calls)
                        Calls.Add($"{settings.Name}:{text}");
                    return "echo: " + text;
                }, new McpServerToolCreateOptions { Name = "echo", Description = "Repeat the text." }),
                McpServerTool.Create(string () => throw new McpException("disk full"), new McpServerToolCreateOptions { Name = "broken" }),
            ],
        };
        var server = McpServer.Create(new StreamServerTransport(toServer.Reader.AsStream(), toClient.Writer.AsStream()), options);
        _ = server.RunAsync(CancellationToken.None);
        var client = await McpClient.CreateAsync(new StreamClientTransport(toServer.Writer.AsStream(), toClient.Reader.AsStream()), cancellationToken: ct);
        // Completion ends when the client session is disposed; for a stdio server that also ends the process.
        _ = client.Completion.ContinueWith(_ => Interlocked.Increment(ref Stops), TaskScheduler.Default);
        return client;
    }

    public async Task WaitForStopsAsync(int count)
    {
        for (int i = 0; i < 200 && Volatile.Read(ref Stops) < count; i++)
            await Task.Delay(10);
    }
}

public class McpToolsTests
{
    private static readonly McpServerSettings Files = new() { Name = "Files", Command = "files-server" };
    private static readonly McpServerSettings Web = new() { Name = "Web Search", Transport = McpTransport.Http, Url = "https://example.com/mcp" };

    private readonly FakeMcpServers _fake = new();

    private McpTools Tools(TimeSpan? idle = null, params McpServerSettings[] servers) =>
        new(servers.Length == 0 ? [Files, Web] : servers, idle, _fake.ConnectAsync);

    [Fact]
    public async Task ListsEveryServersToolsWithItsPrefix()
    {
        await using var tools = Tools();
        var definitions = await tools.ListAsync(default);

        Assert.Equal(["files__broken", "files__echo", "web_search__broken", "web_search__echo"], definitions.Select(d => d.Name));
        Assert.Equal("[Files] Repeat the text.", definitions[1].Description);
        Assert.Equal("string", definitions[1].InputSchema.GetProperty("properties").GetProperty("text").GetProperty("type").GetString());
        Assert.True(tools.Owns("web_search__echo"));
        Assert.False(tools.Owns("echo"));
    }

    [Fact]
    public async Task DisabledServersAreNeverStarted()
    {
        await using var tools = Tools(null, Files, Web with { Enabled = false });
        Assert.Equal(2, (await tools.ListAsync(default)).Count);
        Assert.Equal(1, _fake.Starts);
    }

    [Fact]
    public async Task EveryToolIsAnExternalEffectUntilTheUserSaysOtherwise()
    {
        var files = Files with { ToolTiers = new Dictionary<string, ActionTier> { ["echo"] = ActionTier.Read, ["broken"] = ActionTier.Destructive } };
        // Read on an HTTP server is ignored: each of its calls is a network call.
        var web = Web with { ToolTiers = new Dictionary<string, ActionTier> { ["echo"] = ActionTier.Read, ["broken"] = ActionTier.AppLocalWrite } };
        await using var tools = Tools(null, files, web);
        await tools.ListAsync(default);

        Assert.Equal(ActionTier.Read, tools.TierOf("files__echo"));
        Assert.Equal(ActionTier.Destructive, tools.TierOf("files__broken"));
        Assert.Equal(ActionTier.ExternalEffect, tools.TierOf("web_search__echo"));
        Assert.Equal(ActionTier.ExternalEffect, tools.TierOf("web_search__broken"));
        Assert.Equal(ActionTier.ExternalEffect, Files.TierFor("anything"));
    }

    [Fact]
    public async Task CallsGoToTheRightServerAndToolErrorsGoBackToTheModel()
    {
        await using var tools = Tools();
        await tools.ListAsync(default);

        var ok = await tools.CallAsync(Call("web_search__echo", """{"text":"hi"}"""), default);
        Assert.Equal(new ToolResultPart("c1", "web_search__echo", "echo: hi"), ok);
        Assert.Equal(["Web Search:hi"], _fake.Calls);

        var failed = await tools.CallAsync(Call("files__broken", "{}"), default);
        Assert.True(failed.IsError);
        Assert.Contains("disk full", failed.Content);
    }

    [Fact]
    public void ResultContentIsMappedToText()
    {
        var result = new CallToolResult
        {
            Content =
            [
                new TextContentBlock { Text = "two files" },
                new ImageContentBlock { Data = "AAAA"u8.ToArray(), MimeType = "image/png" },
                new ResourceLinkBlock { Uri = "file:///c/a.txt", Name = "a.txt" },
                new EmbeddedResourceBlock { Resource = new TextResourceContents { Uri = "file:///c/b.txt", Text = "inside b" } },
            ],
        };
        Assert.Equal("two files\n[image image/png]\n[link file:///c/a.txt]\ninside b", McpTools.ResultText(result));
        Assert.Equal("(no output)", McpTools.ResultText(new CallToolResult()));
    }

    [Fact]
    public async Task NothingRunsUntilFirstUseAndEverythingStopsOnDispose()
    {
        var tools = Tools();
        Assert.Equal(0, _fake.Starts);
        await tools.ListAsync(default);
        await tools.ListAsync(default);
        Assert.Equal(2, _fake.Starts);
        Assert.Equal(2, tools.Running);

        await tools.DisposeAsync();
        await _fake.WaitForStopsAsync(2);
        Assert.Equal(0, tools.Running);
        Assert.Equal(2, _fake.Stops);
    }

    [Fact]
    public async Task IdleServersStopAndStartAgainOnTheNextCall()
    {
        await using var tools = Tools(TimeSpan.FromMilliseconds(50), Files);
        await tools.ListAsync(default);
        await _fake.WaitForStopsAsync(1);
        Assert.Equal(1, _fake.Stops);
        Assert.Equal(0, tools.Running);

        var result = await tools.CallAsync(Call("files__echo", """{"text":"again"}"""), default);
        Assert.Equal("echo: again", result.Content);
        Assert.Equal(2, _fake.Starts);
    }

    [Fact]
    public async Task AServerThatDoesNotStartIsNamed()
    {
        _fake.Failing.Add("Web Search");
        await using var tools = Tools();
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.ListAsync(default));
        Assert.Contains("\"Web Search\" did not start: the command was not found", e.Message);
        Assert.Equal(1, tools.Running);
    }

    [Fact]
    public void PrefixedNamesStayWithinWhatProvidersAccept()
    {
        var name = McpTools.ModelName(new McpServerSettings { Name = "My very long server name, really" }, "read.file/" + new string('x', 80));
        Assert.Equal(64, name.Length);
        Assert.StartsWith("my_very_long_server__read_file_", name);
    }

    internal static ToolCallPart Call(string name, string json, string id = "c1") => new(id, name, JsonDocument.Parse(json).RootElement);
}

public class McpPaletteTests : IDisposable
{
    private readonly string _logPath = Path.Combine(Path.GetTempPath(), $"tomte-mcp-{Guid.NewGuid():N}", "send-log.jsonl");
    private readonly ScriptedProvider _provider = new();
    private readonly FakeMcpServers _fake = new();
    private readonly PermissionGrants _grants = new();
    private readonly List<ToolRequest> _asked = [];

    public void Dispose()
    {
        if (Directory.Exists(Path.GetDirectoryName(_logPath)))
            Directory.Delete(Path.GetDirectoryName(_logPath)!, recursive: true);
    }

    private PaletteSession Session(McpTools mcp, PermissionAnswer answer)
    {
        var config = new ProviderConfig("p", ProviderKind.OpenAICompatible, "http://localhost", "m");
        var tools = new BuiltInTools(new InMemoryShelf(), new InMemoryScratchpad(), _ => { }, _ => { });
        return new PaletteSession(_provider, config, new PaletteOptions(), new SendLog(_logPath), tools, _grants, [])
        {
            Prompt = "hi",
            Mcp = mcp,
            ConfirmTool = (request, _) =>
            {
                _asked.Add(request);
                return Task.FromResult(answer);
            },
        };
    }

    private McpTools Tools(McpServerSettings? files = null) => new([files ?? new McpServerSettings { Name = "Files", Command = "x" }], null, _fake.ConnectAsync);

    [Fact]
    public async Task ConfirmedCallRunsAndItsResultReachesTheModel()
    {
        _provider.Turns.Add(ScriptedProvider.Call("files__echo", """{"text":"hi"}"""));
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "done"));
        await using var mcp = Tools();
        var session = Session(mcp, PermissionAnswer.AllowOnce);
        await session.SendAsync();

        Assert.Equal(PaletteStatus.Done, session.Status);
        Assert.Equal(7, _provider.Requests[0].Tools!.Count);
        var request = Assert.Single(_asked);
        Assert.Equal(new ToolRequest("files__echo", """Files: echo {"text":"hi"}""", ActionTier.ExternalEffect, OfferAlwaysAllow: true), request);
        var result = (ToolResultPart)_provider.Requests[1].Messages[^1].Parts[0];
        Assert.Equal("echo: hi", result.Content);
        Assert.False(result.IsError);
        Assert.Equal(["Files:hi"], _fake.Calls);
    }

    [Fact]
    public async Task DeniedCallNeverReachesTheServer()
    {
        _provider.Turns.Add(ScriptedProvider.Call("files__echo", """{"text":"hi"}"""));
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "ok"));
        await using var mcp = Tools();
        var session = Session(mcp, PermissionAnswer.Deny);
        await session.SendAsync();

        Assert.Empty(_fake.Calls);
        var step = Assert.Single(session.Answer.OfType<ToolStepSegment>());
        Assert.True(step.IsError);
        Assert.Equal("The user did not allow this.", step.Result);
    }

    [Fact]
    public async Task AlwaysAllowIsPerToolAndReadToolsDoNotAsk()
    {
        _provider.Turns.Add([
            new ToolCallEvent(McpToolsTests.Call("files__echo", """{"text":"a"}""", "c1")),
            new ToolCallEvent(McpToolsTests.Call("files__echo", """{"text":"b"}""", "c2")),
            new TurnDone("tool_use", 1, 1)]);
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "ok"));
        await using (var mcp = Tools())
            await Session(mcp, PermissionAnswer.AlwaysAllow).SendAsync();
        Assert.Single(_asked);
        Assert.Contains("files__echo", _grants.AlwaysAllowed);
        Assert.Equal(2, _fake.Calls.Count);

        _asked.Clear();
        _grants.Revoke("files__echo");
        _provider.Requests.Clear();
        var readOnly = new McpServerSettings { Name = "Files", Command = "x", ToolTiers = new Dictionary<string, ActionTier> { ["echo"] = ActionTier.Read } };
        await using (var mcp = Tools(readOnly))
            await Session(mcp, PermissionAnswer.Deny).SendAsync();
        Assert.Empty(_asked);
        Assert.Equal(4, _fake.Calls.Count);
    }

    [Fact]
    public async Task AServerThatDoesNotStartIsTheSessionsError()
    {
        _fake.Failing.Add("Files");
        _provider.Turns.Add(ScriptedProvider.Text(10, 5, "never"));
        await using var mcp = Tools();
        var session = Session(mcp, PermissionAnswer.Deny);
        await session.SendAsync();

        Assert.Equal(PaletteStatus.Failed, session.Status);
        Assert.Contains("MCP server \"Files\" did not start", session.Error);
        Assert.Empty(_provider.Requests);
    }
}

public class McpSettingsTests
{
    [Fact]
    public void ValidatesWhatTheUserTyped()
    {
        McpServerSettings[] others = [new() { Name = "Files", Command = "npx" }];
        Assert.Null(McpServerSettings.Validate(others, new() { Name = "Git", Command = "uvx" }));
        Assert.NotNull(McpServerSettings.Validate(others, new() { Name = "files", Command = "npx" }));
        Assert.NotNull(McpServerSettings.Validate([], new() { Name = "!!", Command = "npx" }));
        Assert.NotNull(McpServerSettings.Validate([], new() { Name = "Git", Command = " " }));
        Assert.NotNull(McpServerSettings.Validate([], new() { Name = "Web", Transport = McpTransport.Http, Url = "ftp://x" }));
        Assert.NotNull(McpServerSettings.Validate([], new() { Name = "Web", Transport = McpTransport.Http, Url = "https://x/mcp", HeaderName = "Bad Header" }));
        Assert.Null(McpServerSettings.Validate([], new() { Name = "Web", Transport = McpTransport.Http, Url = "https://x/mcp", HeaderName = "Authorization" }));
    }

    [Fact]
    public void ArgumentsSplitAtSpacesOutsideQuotes()
    {
        Assert.Equal(["-y", "@modelcontextprotocol/server-filesystem", @"C:\My Files", ""], McpServerSettings.SplitArguments(@" -y  @modelcontextprotocol/server-filesystem ""C:\My Files"" """""));
        Assert.Equal(@"-y ""C:\My Files"" """"", McpServerSettings.JoinArguments(["-y", @"C:\My Files", ""]));
    }

    [Fact]
    public void ServersSurviveTheSettingsFile()
    {
        var settings = new TomteSettings
        {
            McpServers = [new() { Name = "Files", Command = "npx", Arguments = ["-y", "x"], ToolTiers = new Dictionary<string, ActionTier> { ["read"] = ActionTier.Read } }],
        };
        string json = SettingsStore.Serialize(settings);
        Assert.Contains("\"read\": \"Read\"", json);
        var path = Path.Combine(Path.GetTempPath(), $"tomte-mcp-{Guid.NewGuid():N}.json");
        File.WriteAllText(path, json);
        try
        {
            var server = Assert.Single(SettingsStore.Load(path).McpServers);
            Assert.Equal(["-y", "x"], server.Arguments);
            Assert.Equal(ActionTier.Read, server.TierFor("read"));
            Assert.True(server.Enabled);
        }
        finally
        {
            File.Delete(path);
        }
    }
}

/// <summary>Opt-in (TOMTE_MCP_E2E=1): downloads and runs the reference filesystem server through npx.</summary>
public sealed class McpEndToEndFactAttribute : FactAttribute
{
    public McpEndToEndFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("TOMTE_MCP_E2E") != "1")
            Skip = "Set TOMTE_MCP_E2E=1 to run against @modelcontextprotocol/server-filesystem (needs Node and npm access).";
    }
}

public class McpEndToEndTests
{
    [McpEndToEndFact]
    public async Task ReadsAFileThroughTheFilesystemServer()
    {
        string dir = Directory.CreateTempSubdirectory("tomte-mcp-e2e").FullName;
        File.WriteAllText(Path.Combine(dir, "note.txt"), "hello from disk");
        var server = new McpServerSettings { Name = "fs", Command = "npx", Arguments = ["-y", "@modelcontextprotocol/server-filesystem@2026.8.31", dir] };
        await using var tools = new McpTools([server]);
        using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(2));

        var definitions = await tools.ListAsync(cts.Token);
        Assert.Contains(definitions, d => d.Name == "fs__read_text_file");
        var result = await tools.CallAsync(McpToolsTests.Call("fs__read_text_file", JsonSerializer.Serialize(new { path = Path.Combine(dir, "note.txt") })), cts.Token);
        Assert.Equal("hello from disk", result.Content);
        Assert.Equal(1, tools.Running);
    }
}
