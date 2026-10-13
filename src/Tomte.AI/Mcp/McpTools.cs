using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Tomte.Core;
using Tomte.Core.Device;

namespace Tomte.AI.Mcp;

/// <summary>
/// The MCP servers one palette uses. Nothing starts until the first <see cref="ListAsync"/>: a stdio
/// server is started as a child process then, an HTTP server is connected then. Every server stops
/// when this object is disposed (the palette closed) or after <see cref="IdleTimeout"/> without a
/// list or a call, and starts again on the next one. Tool names reach the model as
/// "prefix__tool", so two servers can both have a tool called "search".
/// </summary>
public sealed class McpTools : IAsyncDisposable
{
    public const string Separator = "__";

    private readonly McpServerSettings[] _servers;
    private readonly Func<McpServerSettings, CancellationToken, Task<McpClient>> _connect;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Dictionary<string, McpClient> _clients = [];
    private readonly Dictionary<string, (McpServerSettings Server, Tool Tool)> _tools = new(StringComparer.Ordinal);
    private readonly List<ToolDefinition> _definitions = [];
    private readonly Timer _idle;
    private int _busy;

    /// <param name="connect">Starts or connects one server; tests pass an in-process one.</param>
    public McpTools(IEnumerable<McpServerSettings> servers, TimeSpan? idleTimeout = null, Func<McpServerSettings, CancellationToken, Task<McpClient>>? connect = null)
    {
        _servers = [.. servers.Where(s => s.Enabled)];
        _connect = connect ?? ConnectAsync;
        IdleTimeout = idleTimeout ?? TimeSpan.FromMinutes(5);
        _idle = new Timer(_ => _ = StopIfIdleAsync());
    }

    public TimeSpan IdleTimeout { get; }

    /// <summary>How many servers are started or connected right now.</summary>
    public int Running => _clients.Count;

    public bool Owns(string tool) => _tools.ContainsKey(tool);

    public ActionTier TierOf(string tool) => _tools[tool].Server.TierFor(_tools[tool].Tool.Name);

    /// <summary>One line for the confirmation prompt: which server, which tool, with what.</summary>
    public string Describe(string tool, JsonElement input)
    {
        var (server, mcpTool) = _tools[tool];
        string args = input.ValueKind == JsonValueKind.Object && input.EnumerateObject().Any() ? input.GetRawText() : "";
        return $"{server.Name}: {mcpTool.Name}{(args.Length == 0 ? "" : " " + (args.Length > 120 ? args[..120] + "..." : args))}";
    }

    /// <summary>Starts the servers that are not running and returns every tool, prefixed. A server that fails throws, naming it.</summary>
    public async Task<IReadOnlyList<ToolDefinition>> ListAsync(CancellationToken ct)
    {
        if (_servers.Length == 0)
            return [];
        await _gate.WaitAsync(ct);
        try
        {
            var missing = _servers.Where(s => !_clients.ContainsKey(s.Name)).ToArray();
            var started = await Task.WhenAll(missing.Select(s => StartAsync(s, ct)));
            Exception? failure = null;
            foreach (var (server, client, tools, error) in started)
            {
                if (client is null)
                {
                    failure ??= error;
                    continue;
                }

                _clients[server.Name] = client;
                foreach (var tool in tools)
                {
                    string name = ModelName(server, tool.Name);
                    if (_tools.TryAdd(name, (server, tool)))
                        _definitions.Add(new ToolDefinition(name, $"[{server.Name}] {tool.Description}".TrimEnd(), tool.InputSchema));
                }
            }

            if (failure is not null)
                throw failure;
            return [.. _definitions];
        }
        finally
        {
            Touch();
            _gate.Release();
        }
    }

    /// <summary>
    /// Runs one call on its server. A protocol error (unknown tool, bad arguments) and a tool that
    /// reports an error go back to the model; a server that died throws, and the session shows it.
    /// </summary>
    public async Task<ToolResultPart> CallAsync(ToolCallPart call, CancellationToken ct)
    {
        var (server, tool) = _tools[call.Name];
        Interlocked.Increment(ref _busy);
        try
        {
            McpClient? client;
            await _gate.WaitAsync(ct);
            try
            {
                _clients.TryGetValue(server.Name, out client);
            }
            finally
            {
                _gate.Release();
            }

            if (client is null)
            {
                // The idle timeout stopped it between the listing and this call.
                await ListAsync(ct);
                client = _clients[server.Name];
            }

            var args = call.Input.ValueKind == JsonValueKind.Object
                ? call.Input.EnumerateObject().ToDictionary(p => p.Name, p => (object?)p.Value.Clone())
                : [];
            var result = await client.CallToolAsync(tool.Name, args, cancellationToken: ct);
            return new ToolResultPart(call.Id, call.Name, ResultText(result), result.IsError == true);
        }
        catch (McpException e)
        {
            return new ToolResultPart(call.Id, call.Name, $"{server.Name}: {tool.Name} failed: {e.Message}", IsError: true);
        }
        finally
        {
            Interlocked.Decrement(ref _busy);
            Touch();
        }
    }

    /// <summary>The tool names and descriptions of one server, for the settings window. Starts the server and stops it again.</summary>
    public static async Task<IReadOnlyList<(string Name, string Description)>> ProbeAsync(McpServerSettings server, CancellationToken ct)
    {
        await using var client = await ConnectAsync(server, ct);
        return [.. (await client.ListToolsAsync(cancellationToken: ct)).Select(t => (t.Name, t.Description ?? ""))];
    }

    public static string ModelName(McpServerSettings server, string tool)
    {
        string name = server.Prefix + Separator + new string([.. tool.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_')]);
        return name.Length > 64 ? name[..64] : name;
    }

    /// <summary>Text blocks as they are; other content as a short marker, since tool results reach the model as text.</summary>
    public static string ResultText(CallToolResult result)
    {
        var parts = result.Content.Select(block => block switch
        {
            TextContentBlock text => text.Text,
            EmbeddedResourceBlock { Resource: TextResourceContents resource } => resource.Text,
            EmbeddedResourceBlock embedded => $"[resource {embedded.Resource.Uri}]",
            ResourceLinkBlock link => $"[link {link.Uri}]",
            ImageContentBlock image => $"[image {image.MimeType}]",
            AudioContentBlock audio => $"[audio {audio.MimeType}]",
            _ => $"[{block.Type}]",
        });
        string text = string.Join("\n", parts);
        if (text.Length == 0 && result.StructuredContent is { } structured)
            text = structured.ToString();
        return text.Length == 0 ? "(no output)" : text;
    }

    public async ValueTask DisposeAsync()
    {
        await _idle.DisposeAsync();
        await _gate.WaitAsync();
        try
        {
            await StopAllAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private static Task<McpClient> ConnectAsync(McpServerSettings server, CancellationToken ct)
    {
        IClientTransport transport = server.Transport == McpTransport.Stdio
            ? new StdioClientTransport(new StdioClientTransportOptions { Name = server.Name, Command = server.Command, Arguments = [.. server.Arguments] })
            : new HttpClientTransport(new HttpClientTransportOptions
            {
                Name = server.Name,
                Endpoint = new Uri(server.Url),
                TransportMode = HttpTransportMode.StreamableHttp,
                AdditionalHeaders = server.HeaderName.Length > 0 && CredentialStore.Read(server.CredentialId) is { } value
                    ? new Dictionary<string, string> { [server.HeaderName] = value }
                    : null,
            });
        return McpClient.CreateAsync(transport, cancellationToken: ct);
    }

    private async Task<(McpServerSettings Server, McpClient? Client, IList<Tool> Tools, Exception? Error)> StartAsync(McpServerSettings server, CancellationToken ct)
    {
        McpClient? client = null;
        try
        {
            client = await _connect(server, ct);
            var tools = await client.ListToolsAsync(cancellationToken: ct);
            return (server, client, [.. tools.Select(t => t.ProtocolTool)], null);
        }
        catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            if (client is not null)
                await client.DisposeAsync();
            return (server, null, [], new InvalidOperationException(
                $"MCP server \"{server.Name}\" did not start: {e.Message} Turn it off in Settings to send without it.", e));
        }
    }

    private void Touch()
    {
        try
        {
            _idle.Change(IdleTimeout, Timeout.InfiniteTimeSpan);
        }
        catch (ObjectDisposedException)
        {
            // Disposed while a call was finishing.
        }
    }

    private async Task StopIfIdleAsync()
    {
        if (!await _gate.WaitAsync(0))
        {
            Touch();
            return;
        }

        try
        {
            if (Volatile.Read(ref _busy) > 0)
                Touch();
            else
                await StopAllAsync();
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task StopAllAsync()
    {
        foreach (var client in _clients.Values)
        {
            try
            {
                await client.DisposeAsync();
            }
            catch (Exception e) when (e is IOException or InvalidOperationException or McpException)
            {
                // A server that already exited; nothing is left to stop.
            }
        }

        // The tool table stays: the model may still call a tool it was shown, and that call starts the server again.
        _clients.Clear();
    }
}
