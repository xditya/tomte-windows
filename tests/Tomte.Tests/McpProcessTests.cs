using System.Diagnostics;
using System.Text.Json;
using Tomte.AI.Mcp;
using Tomte.Core;
using Xunit;

namespace Tomte.Tests;

/// <summary>MCP over a real child process: tools/McpEchoServer, started the way a user's stdio server is.</summary>
public sealed class McpProcessTests : IDisposable
{
    private static readonly string Exe = Path.Combine(AppContext.BaseDirectory, "McpEchoServer.exe");

    private readonly string _folder = Directory.CreateTempSubdirectory("tomte-mcp-").FullName;
    private readonly CancellationTokenSource _limit = new(TimeSpan.FromSeconds(60));

    public void Dispose()
    {
        _limit.Dispose();
        try
        {
            Directory.Delete(_folder, recursive: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // A process that is still ending holds a file; the temp folder is cleaned by Windows.
        }
    }

    private string InFolder(string name) => Path.Combine(_folder, name);

    private McpServerSettings Echo(params string[] arguments) => new() { Name = "Echo", Command = Exe, Arguments = ["serve", .. arguments] };

    /// <summary>What the server wrote about itself when it started: pid, cwd and args.</summary>
    private async Task<JsonElement> InfoAsync(string name)
    {
        string path = InFolder(name);
        while (!File.Exists(path))
            await Task.Delay(10, _limit.Token);
        return JsonDocument.Parse(await File.ReadAllTextAsync(path)).RootElement;
    }

    private async Task<int> PidAsync(string name) => (await InfoAsync(name)).GetProperty("pid").GetInt32();

    private static bool Alive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<bool> GoneAsync(int pid)
    {
        for (int waited = 0; waited < 5000 && Alive(pid); waited += 25)
            await Task.Delay(25);
        return !Alive(pid);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ArgumentsReachTheServerAsTyped(bool throughCmd)
    {
        string[] typed =
        [
            "plain", "two words", "a&b", "%USERNAME%", "100%", "caret^z", "q\"uote", "pipe|x", "<in>", "(paren)", "!bang!",
            "https://h/x?a=1&b=2", @"C:\My Files\", @"back\slash", @"ends\", @"slash\""quote", "semi;colon,comma", "=eq", "",
        ];
        string command = Exe;
        if (throughCmd)
        {
            // What npx.cmd is: a batch file that hands its arguments on to a program.
            command = InFolder("shim.cmd");
            File.WriteAllText(command, $"@\"{Exe}\" %*\r\n");
        }

        await using var tools = new McpTools([new McpServerSettings { Name = "Echo", Command = command, Arguments = ["serve", InFolder("info.json"), .. typed] }]);
        var definitions = await tools.ListAsync(_limit.Token);
        Assert.Equal(["echo__echo"], definitions.Select(d => d.Name));

        var info = await InfoAsync("info.json");
        Assert.Equal(typed, info.GetProperty("args").EnumerateArray().Select(a => a.GetString()!));
        Assert.Equal(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), info.GetProperty("cwd").GetString());
        Assert.Equal("echo: hi", (await tools.CallAsync(McpToolsTests.Call("echo__echo", """{"text":"hi"}"""), _limit.Token)).Result.Content);
    }

    [Fact]
    public void BatchCommandLinesQuoteWhatCmdWouldRead()
    {
        Assert.Equal(
            @"/e:ON /v:OFF /d /c """"C:\Program Files\nodejs\npx.cmd"" -y @scope/server ""two words"" ""%%cd:~,%NAME%%cd:~,%"" ""a\\""""b""""",
            McpProcess.BatchCommandLine(@"C:\Program Files\nodejs\npx.cmd", ["-y", "@scope/server", "two words", "%NAME%", "a\\\"b"]));
        Assert.Throws<ArgumentException>(() => McpProcess.BatchCommandLine(@"C:\x.cmd", ["line\nbreak"]));
    }

    [Fact]
    public void CommandsAreFoundTheWayAShellFindsThem()
    {
        Assert.Equal(Path.Combine(Environment.SystemDirectory, "cmd.exe"), McpProcess.Find("cmd"), ignoreCase: true);
        Assert.Equal(Exe, McpProcess.Find(Path.ChangeExtension(Exe, null)), ignoreCase: true);
        Assert.Equal(Exe, McpProcess.Find($"\"{Exe}\""), ignoreCase: true);
        Assert.Null(McpProcess.Find("tomte-no-such-command"));
        Assert.Null(McpProcess.Find(" "));
    }

    [Fact]
    public async Task StoppingEndsAServerThatWillNotStopAndWhatItStarted()
    {
        var tools = new McpTools([Echo(InFolder("server.json"), "--linger", "--child", InFolder("child.json"))]);
        await tools.ListAsync(_limit.Token);
        int server = await PidAsync("server.json"), child = await PidAsync("child.json");
        Assert.True(Alive(server));
        Assert.True(Alive(child));

        // The server gets one second to exit after its input closes, then it is ended.
        var watch = Stopwatch.StartNew();
        await tools.DisposeAsync();
        Assert.InRange(watch.ElapsedMilliseconds, 0, 4000);
        Assert.True(await GoneAsync(server));
        Assert.True(await GoneAsync(child));
    }

    [Fact]
    public async Task AServerThatStopsWhenAskedIsNotMadeToWait()
    {
        var tools = new McpTools([Echo(InFolder("a.json")), Echo(InFolder("b.json")) with { Name = "Second" }, Echo(InFolder("c.json")) with { Name = "Third" }]);
        Assert.Equal(3, (await tools.ListAsync(_limit.Token)).Count);
        int[] pids = [await PidAsync("a.json"), await PidAsync("b.json"), await PidAsync("c.json")];

        var watch = Stopwatch.StartNew();
        await tools.DisposeAsync();
        Assert.InRange(watch.ElapsedMilliseconds, 0, 900);
        foreach (int pid in pids)
            Assert.False(Alive(pid));
    }

    [Fact]
    public async Task TheServerDiesWithTheProcessThatStartedIt()
    {
        using var owner = Process.Start(new ProcessStartInfo(Exe, ["own", InFolder("server.json")]) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true })!;
        try
        {
            Assert.Equal("ready", await owner.StandardOutput.ReadLineAsync(_limit.Token));
            int server = await PidAsync("server.json");
            Assert.True(Alive(server));

            // Killed, not closed: nothing of the owner's code runs any more, as when Task Manager ends
            // Tomte. The server ignores its closed input, so only the job object is left to end it.
            owner.Kill();
            Assert.True(await GoneAsync(server));
        }
        finally
        {
            owner.Kill(entireProcessTree: true);
        }
    }

    [Fact]
    public async Task AServerProcessThatExitsIsNamedAndStartedAgain()
    {
        await using var tools = new McpTools([Echo(InFolder("info.json"))]);
        await tools.ListAsync(_limit.Token);
        int first = await PidAsync("info.json");

        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => tools.CallAsync(McpToolsTests.Call("echo__echo", """{"text":"exit"}"""), _limit.Token));
        Assert.Contains("MCP server \"Echo\" stopped", e.Message);
        Assert.Equal(0, tools.Running);

        Assert.Equal("echo: again", (await tools.CallAsync(McpToolsTests.Call("echo__echo", """{"text":"again"}"""), _limit.Token)).Result.Content);
        Assert.NotEqual(first, await PidAsync("info.json"));
    }

    [Fact]
    public async Task AStartThatFailsSaysWhy()
    {
        await using var missing = new McpTools([new McpServerSettings { Name = "Echo", Command = "tomte-no-such-command" }]);
        var e = await Assert.ThrowsAsync<InvalidOperationException>(() => missing.ListAsync(_limit.Token));
        Assert.Contains("The command \"tomte-no-such-command\" was not found", e.Message);

        // Not a server: it writes a usage line to stderr and exits.
        await using var wrong = new McpTools([new McpServerSettings { Name = "Echo", Command = Exe, Arguments = ["nonsense"] }]);
        e = await Assert.ThrowsAsync<InvalidOperationException>(() => wrong.ListAsync(_limit.Token));
        Assert.Contains("usage: McpEchoServer", e.Message);
    }

    [Fact]
    public async Task ACancelledStartEndsTheProcess()
    {
        // "sleep" never answers the first request, like a command that is not an MCP server but keeps running.
        await using var tools = new McpTools([new McpServerSettings { Name = "Echo", Command = Exe, Arguments = ["sleep", InFolder("sleeper.json")] }]);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(_limit.Token);
        var listing = tools.ListAsync(cancel.Token);
        int sleeper = await PidAsync("sleeper.json");
        cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => listing);

        Assert.True(await GoneAsync(sleeper));
        Assert.Equal(0, tools.Running);
    }
}
