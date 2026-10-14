using System.Collections.Concurrent;
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
    private readonly string? _invalid;
    private readonly Func<McpServerSettings, CancellationToken, Task<McpClient>> _connect;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Written under the gate; the idle timer's thread and Running read it without.
    private readonly ConcurrentDictionary<string, McpClient> _clients = new();
    private readonly Dictionary<string, (McpServerSettings Server, Tool Tool)> _tools = new(StringComparer.Ordinal);
    private readonly List<ToolDefinition> _definitions = [];
    private readonly Timer _idle;
    private int _busy;
    private bool _disposed;

    /// <param name="connect">Starts or connects one server; tests pass an in-process one.</param>
    public McpTools(IEnumerable<McpServerSettings> servers, TimeSpan? idleTimeout = null, Func<McpServerSettings, CancellationToken, Task<McpClient>>? connect = null)
    {
        // Settings checks a server when it is saved; a hand-edited file skips that, and two servers with
        // one name would share a slot here, so the second would never be stopped.
        var usable = new List<McpServerSettings>();
        foreach (var server in servers.Where(s => s.Enabled))
        {
            if (McpServerSettings.Validate(usable, server) is { } problem)
                _invalid ??= $"MCP server \"{server.Name}\" cannot be used: {problem} Fix or remove it in Settings.";
            else
                usable.Add(server);
        }

        _servers = [.. usable];
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
        if (_servers.Length == 0 && _invalid is null)
            return [];
        await _gate.WaitAsync(ct);
        try
        {
            await StartMissingAsync(_servers, ct);
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
            McpClient client;
            await _gate.WaitAsync(ct);
            try
            {
                // The idle timeout may have stopped the server since the listing; this starts it again.
                await StartMissingAsync([server], ct);
                client = _clients[server.Name];
            }
            finally
            {
                _gate.Release();
            }

            // Small models send null for optional arguments they do not use; servers read that as a wrong type.
            var args = call.Input.ValueKind == JsonValueKind.Object
                ? call.Input.EnumerateObject().Where(p => p.Value.ValueKind != JsonValueKind.Null).ToDictionary(p => p.Name, p => (object?)p.Value.Clone())
                : [];
            try
            {
                var result = await UnlessGoneAsync(c => client.CallToolAsync(tool.Name, args, cancellationToken: c).AsTask(), client.Completion, ct);
                return new ToolResultPart(call.Id, call.Name, ResultText(result), result.IsError == true);
            }
            catch (McpException e)
            {
                return new ToolResultPart(call.Id, call.Name, $"{server.Name}: {tool.Name} failed: {e.Message}", IsError: true);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // The process exited or the connection dropped. Forget the server, so the next call starts
                // it again instead of failing the same way for the rest of the session.
                await _gate.WaitAsync(CancellationToken.None);
                try
                {
                    if (_clients.TryRemove(new KeyValuePair<string, McpClient>(server.Name, client)))
                        await StopAsync(client);
                }
                finally
                {
                    _gate.Release();
                }

                throw Failure(server, "stopped", e);
            }
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
            // From here on nothing starts: a call that arrives late must not leave a server nobody stops.
            _disposed = true;
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

    private static InvalidOperationException Failure(McpServerSettings server, string what, Exception e) =>
        new($"MCP server \"{server.Name}\" {what}: {e.Message} Turn it off in Settings to send without it.", e);

    /// <summary>
    /// Starts those of <paramref name="servers"/> that are not running. The caller holds the gate. Every
    /// server that started is registered before this throws, so a cancel or a failure of one server never
    /// leaves another one running without an owner.
    /// </summary>
    private async Task StartMissingAsync(IEnumerable<McpServerSettings> servers, CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_invalid is not null)
            throw new InvalidOperationException(_invalid);

        // On the pool: starting a process blocks for a while before the first await, and the caller is the island's thread.
        var started = await Task.WhenAll(servers.Where(s => !_clients.ContainsKey(s.Name)).Select(s => Task.Run(() => StartAsync(s, ct))));
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

        ct.ThrowIfCancellationRequested();
        if (failure is not null)
            throw failure;
    }

    /// <summary>Never throws: a server that did not start comes back without a client, with the error unless the start was cancelled.</summary>
    private async Task<(McpServerSettings Server, McpClient? Client, IList<Tool> Tools, Exception? Error)> StartAsync(McpServerSettings server, CancellationToken ct)
    {
        McpClient? client = null;
        try
        {
            client = await _connect(server, ct);
            var listing = client;
            var tools = await UnlessGoneAsync(c => listing.ListToolsAsync(cancellationToken: c).AsTask(), client.Completion, ct);
            return (server, client, [.. tools.Select(t => t.ProtocolTool)], null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Connected but not listed (a cancel, a timeout, a bad answer): nobody else knows this client.
            if (client is not null)
                await StopAsync(client);
            return (server, null, [], e is OperationCanceledException && ct.IsCancellationRequested ? null : Failure(server, "did not start", e));
        }
    }

    /// <summary>
    /// Awaits <paramref name="request"/>, but gives up when <paramref name="gone"/> completes first. A
    /// stream transport does not fail a request whose answer can no longer come (the server closed its
    /// output, or exited between two calls), so without this the request waits for the session timeout.
    /// </summary>
    private static async Task<T> UnlessGoneAsync<T>(Func<CancellationToken, Task<T>> request, Task gone, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var task = request(linked.Token);
        if (await Task.WhenAny(task, gone) != task && !task.IsCompletedSuccessfully)
        {
            linked.Cancel();
            // Looked at, so the abandoned request's failure never reaches the unobserved-exception event.
            _ = task.ContinueWith(t => t.Exception, TaskScheduler.Default);
            ct.ThrowIfCancellationRequested();
            throw new IOException("The server closed the connection.");
        }

        return await task;
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

    // The tool table stays: the model may still call a tool it was shown, and that call starts the server again.
    private async Task StopAllAsync()
    {
        var running = _clients.Values.ToArray();
        _clients.Clear();
        await Task.WhenAll(running.Select(StopAsync));
    }

    private static async Task StopAsync(McpClient client)
    {
        try
        {
            await client.DisposeAsync();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // It already exited, or it failed while stopping; either way there is nothing left to do for it.
        }
    }
}
