// A small MCP server over stdio, for the tests that need a real child process.
//
//   McpEchoServer serve <infoFile> [--linger] [--child <infoFile>] [anything else]
//       Writes its process id, working folder, arguments and the environment variables whose names
//       start with TOMTE_ECHO_ to infoFile, then serves one tool,
//       echo(text). echo("exit") ends the process without an answer, like a crash. --linger keeps the
//       process running after its input closes, like a server that ignores the request to stop.
//       --child starts a second process first (sleep mode), like a server that starts helpers.
//   McpEchoServer sleep <infoFile>
//       Writes its process id and sleeps for a minute.
//   McpEchoServer own <infoFile>
//       Starts "serve <infoFile> --linger" through McpTools, prints "ready" and sleeps. A test kills
//       this process to see what becomes of the server.
using System.Diagnostics;
using System.Text.Json;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using Tomte.AI.Mcp;
using Tomte.Core;

if (args.Length < 2 || args[0] is not ("serve" or "sleep" or "own"))
{
    Console.Error.WriteLine("usage: McpEchoServer serve|sleep|own <infoFile> [--linger] [--child <infoFile>] [arguments]");
    return 2;
}

string infoFile = args[1];
var nap = TimeSpan.FromMinutes(1);

if (args[0] == "own")
{
    await using var tools = new McpTools([new McpServerSettings { Name = "Echo", Command = Environment.ProcessPath!, Arguments = ["serve", infoFile, "--linger"] }]);
    await tools.ListAsync(default);
    Console.WriteLine("ready");
    await Task.Delay(nap);
    return 0;
}

// Written under another name first, so a reader never sees half a file.
var env = Environment.GetEnvironmentVariables().Cast<System.Collections.DictionaryEntry>()
    .Where(e => ((string)e.Key).StartsWith("TOMTE_ECHO_", StringComparison.OrdinalIgnoreCase))
    .ToDictionary(e => (string)e.Key, e => (string?)e.Value);
File.WriteAllText(infoFile + ".tmp", JsonSerializer.Serialize(new { pid = Environment.ProcessId, cwd = Environment.CurrentDirectory, args = args[2..], env }));
File.Move(infoFile + ".tmp", infoFile, overwrite: true);

if (args[0] == "sleep")
{
    await Task.Delay(nap);
    return 0;
}

int child = Array.IndexOf(args, "--child");
if (child >= 0)
    Process.Start(new ProcessStartInfo(Environment.ProcessPath!, ["sleep", args[child + 1]]) { UseShellExecute = false, CreateNoWindow = true });

var options = new McpServerOptions
{
    ServerInfo = new Implementation { Name = "echo", Version = "1" },
    ToolCollection =
    [
        McpServerTool.Create((string text) =>
        {
            if (text == "exit")
                Environment.Exit(3);
            return "echo: " + text;
        }, new McpServerToolCreateOptions { Name = "echo", Description = "Repeat the text." }),
    ],
};
await using (var server = McpServer.Create(new StdioServerTransport("echo"), options))
    await server.RunAsync();

if (args.Contains("--linger"))
    await Task.Delay(nap);
return 0;
