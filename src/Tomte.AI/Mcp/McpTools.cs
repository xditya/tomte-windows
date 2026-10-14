using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Tomte.Core;
using Tomte.Core.Device;

namespace Tomte.AI.Mcp;

/// <summary>One tool as Settings lists it: the key its tier is stored under, and its name and description cleaned for display.</summary>
public sealed record McpToolInfo(string Key, string Name, string Description);

/// <summary>
/// The MCP servers one palette uses. Nothing starts until the first <see cref="ListAsync"/>: a stdio
/// server is started as a child process then, an HTTP server is connected then. Every server stops
/// when this object is disposed (the palette closed) or after <see cref="IdleTimeout"/> without a
/// list or a call, and starts again on the next one. Tool names reach the model as
/// "prefix__tool", so two servers can both have a tool called "search". Everything a server writes
/// (names, descriptions, results, errors) is cleaned and cut to a fixed length before it is shown,
/// logged or given to the model.
/// </summary>
public sealed class McpTools : IAsyncDisposable
{
    public const string Separator = "__";

    /// <summary>Longest server or tool name shown in a prompt or in Settings.</summary>
    public const int MaxName = 64;

    /// <summary>Longest tool description given to the model.</summary>
    public const int MaxDescription = 1000;

    /// <summary>Longest tool result given to the model, the answer and the log.</summary>
    public const int MaxResult = 20_000;

    /// <summary>Longest error text taken from a server or from the SDK.</summary>
    public const int MaxError = 300;

    /// <summary>Longest argument schema of one tool; the model gets every schema on every send, so a server with a longer one is refused.</summary>
    public const int MaxSchema = 20_000;

    // For the confirmation prompt: readable, so letters outside ASCII and characters such as & are written as they are.
    private static readonly JsonSerializerOptions Shown = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly McpServerSettings[] _servers;
    private readonly string? _invalid;
    private readonly Func<McpServerSettings, CancellationToken, Task<McpClient>> _connect;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Written under the gate; the idle timer's thread and Running read it without.
    private readonly ConcurrentDictionary<string, McpClient> _clients = new();

    // By the name the model sees, in a fixed order: the model gets the same tool list on every send.
    private readonly SortedDictionary<string, Entry> _tools = new(StringComparer.Ordinal);
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
                _invalid ??= Clean($"MCP server \"{server.Name}\" cannot be used: {problem}", MaxError) + " Fix or remove it in Settings.";
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

    /// <summary>Every tool listed so far, for Settings.</summary>
    public IReadOnlyList<McpToolInfo> Tools =>
        [.. _tools.Values.Select(t => new McpToolInfo(t.Key, Clean(t.Tool.Name, MaxName), Clean(t.Tool.Description, MaxDescription)))];

    public bool Owns(string tool) => _tools.ContainsKey(tool);

    /// <summary>What this tool's tier override and "always allow" are stored under; see <see cref="Key"/>.</summary>
    public string KeyOf(string tool) => _tools[tool].Key;

    public ActionTier TierOf(string tool) => _tools[tool].Server.TierFor(_tools[tool].Key);

    /// <summary>One line for the confirmation prompt: which server, which tool, with what.</summary>
    public string Describe(string tool, JsonElement input)
    {
        var (server, mcpTool, _) = _tools[tool];
        // What is shown is what is sent, not what the model wrote.
        var args = Arguments(mcpTool, input);
        string shown = args.Count == 0 ? "" : " " + Clean(JsonSerializer.Serialize(args, Shown), 120);
        return $"{Clean(server.Name, MaxName)}: {Clean(mcpTool.Name, MaxName)}{shown}";
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
            return [.. _tools.Select(t => new ToolDefinition(t.Key, Clean($"[{t.Value.Server.Name}] {t.Value.Tool.Description}", MaxDescription), t.Value.Tool.InputSchema))];
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
        var (server, tool, key) = _tools[call.Name];
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

            // A restart lists the tools again. What the user allowed was the tool as it was before.
            if (!_tools.TryGetValue(call.Name, out var now) || now.Key != key)
                return new ToolResultPart(call.Id, call.Name, $"{Clean(server.Name, MaxName)} changed this tool when it restarted, so the call was not made. Call it again to use it as it is now.", IsError: true);

            var args = Arguments(tool, call.Input);
            try
            {
                var result = await UnlessGoneAsync(c => client.CallToolAsync(tool.Name, args, cancellationToken: c).AsTask(), client.Completion, ct);
                return new ToolResultPart(call.Id, call.Name, ResultText(result), result.IsError == true);
            }
            catch (McpException e)
            {
                return new ToolResultPart(call.Id, call.Name, $"{Clean(server.Name, MaxName)}: {Clean(tool.Name, MaxName)} failed: {Explain(server, e)}", IsError: true);
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

    /// <summary>The tools of one server, for the settings window. Starts the server and stops it again.</summary>
    public static async Task<IReadOnlyList<McpToolInfo>> ProbeAsync(McpServerSettings server, CancellationToken ct)
    {
        await using var tools = new McpTools([server with { Enabled = true }]);
        await tools.ListAsync(ct);
        return tools.Tools;
    }

    public static string ModelName(McpServerSettings server, string tool)
    {
        string name = server.Prefix + Separator + new string([.. tool.Select(c => char.IsAsciiLetterOrDigit(c) || c is '_' or '-' ? c : '_')]);
        return name.Length > 64 ? name[..64] : name;
    }

    /// <summary>
    /// The arguments as they are sent. Small models send null for optional arguments they do not use,
    /// and servers read that as a wrong type, so such a null is left out. A null the schema asks for
    /// stays: the argument is required, or its type includes null (which is how a field is cleared).
    /// </summary>
    internal static Dictionary<string, object?> Arguments(Tool tool, JsonElement input)
    {
        var args = new Dictionary<string, object?>();
        if (input.ValueKind != JsonValueKind.Object)
            return args;
        foreach (var property in input.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Null || WantsNull(tool.InputSchema, property.Name))
                args[property.Name] = property.Value.Clone();
        }

        return args;
    }

    private static bool WantsNull(JsonElement schema, string name)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return false;
        if (schema.TryGetProperty("required", out var required) && required.ValueKind == JsonValueKind.Array
            && required.EnumerateArray().Any(r => r.ValueKind == JsonValueKind.String && r.ValueEquals(name)))
        {
            return true;
        }

        return schema.TryGetProperty("properties", out var properties) && properties.ValueKind == JsonValueKind.Object
            && properties.TryGetProperty(name, out var property) && AllowsNull(property);
    }

    private static bool AllowsNull(JsonElement schema)
    {
        if (schema.ValueKind != JsonValueKind.Object)
            return false;
        if (schema.TryGetProperty("type", out var type))
        {
            IEnumerable<JsonElement> types = type.ValueKind == JsonValueKind.Array ? type.EnumerateArray() : [type];
            if (types.Any(t => t.ValueKind == JsonValueKind.String && t.ValueEquals("null")))
                return true;
        }

        return ((string[])["anyOf", "oneOf"]).Any(k => schema.TryGetProperty(k, out var options) && options.ValueKind == JsonValueKind.Array && options.EnumerateArray().Any(AllowsNull));
    }

    /// <summary>
    /// What a tier override and an "always allow" are stored under: the name the model sees plus a hash
    /// of what the tool is. A server pointed at another command or URL, or a tool whose name,
    /// description or arguments changed, gets another key, so it is an External effect again and asks.
    /// </summary>
    internal static string Key(McpServerSettings server, Tool tool)
    {
        var identity = new StringBuilder();
        // Each part with its length in front, so no two different sets of parts make the same text.
        foreach (string part in (string[])[server.Target, tool.Name, tool.Description ?? "", tool.InputSchema.GetRawText()])
            identity.Append(part.Length).Append(':').Append(part);
        return ModelName(server, tool.Name) + "#" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())), 0, 8);
    }

    /// <summary>
    /// Text a server wrote, made safe to show and cut to about <paramref name="max"/> characters. Control
    /// characters go, and so do the characters that reorder text, so a name cannot read as something else
    /// in a prompt. Whitespace runs become one space unless <paramref name="lines"/> keeps line breaks
    /// and tabs. What is cut is replaced by a count.
    /// </summary>
    public static string Clean(string? text, int max, bool lines = false)
    {
        text ??= "";
        var clean = new StringBuilder(Math.Min(text.Length, max));
        bool gap = false;
        int i = 0;
        for (; i < text.Length && clean.Length < max; i++)
        {
            char c = text[i];
            if (c is '\u061C' or '\u200E' or '\u200F' or (>= '\u202A' and <= '\u202E') or (>= '\u2066' and <= '\u2069'))
                continue;
            if (lines ? char.IsControl(c) && c is not ('\n' or '\t') : char.IsControl(c) || char.IsWhiteSpace(c))
            {
                gap = !lines;
                continue;
            }

            if (gap && clean.Length > 0)
                clean.Append(' ');
            gap = false;
            clean.Append(c);
        }

        if (i == text.Length)
            return clean.ToString();

        // Half of a surrogate pair is not text.
        if (char.IsHighSurrogate(clean[^1]))
        {
            clean.Length--;
            i--;
        }

        return clean.Append(lines ? '\n' : ' ').Append($"[{text.Length - i} more characters]").ToString();
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
        text = Clean(text, MaxResult, lines: true);
        return text.Length == 0 ? "(no output)" : text;
    }

    /// <summary>
    /// Replaces a secret wherever it appears in <paramref name="text"/>: the whole value, and each long
    /// part of it, since "Bearer abc" can be quoted back without the scheme.
    /// </summary>
    internal static string Hide(string text, string secret)
    {
        foreach (string part in secret.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(p => p.Length >= 8).Prepend(secret))
            text = text.Replace(part, "[hidden]", StringComparison.Ordinal);
        return text;
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

    // The exception itself is not kept as the inner one: it and the errors inside it can hold the header
    // value, and whoever logs this error logs those too.
    private static InvalidOperationException Failure(McpServerSettings server, string what, Exception e) =>
        new($"MCP server \"{Clean(server.Name, MaxName)}\" {what}: {Explain(server, e)} Turn it off in Settings to send without it.");

    /// <summary>An error's text for the user and the model: the header value hidden, then cleaned and cut like all server text.</summary>
    private static string Explain(McpServerSettings server, Exception e)
    {
        string text = e.Message;
        if (server.HeaderName.Length > 0)
        {
            // The SDK quotes a header value it could not add, and a server can echo one back.
            try
            {
                if (CredentialStore.Read(server.CredentialId) is { Length: > 0 } secret)
                    text = Hide(text, secret);
            }
            catch (InvalidOperationException)
            {
                text = "The details are not shown, because the saved header value could not be read to hide it.";
            }
        }

        text = Clean(text, MaxError);
        return text.Length == 0 || text[^1] is '.' or '!' or '?' or ']' ? text : text + ".";
    }

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

            if (Register(server, tools) is { } problem)
            {
                await StopAsync(client);
                failure ??= new InvalidOperationException($"MCP server \"{Clean(server.Name, MaxName)}\" cannot be used: {problem}. Turn it off in Settings to send without it.");
                continue;
            }

            _clients[server.Name] = client;
        }

        ct.ThrowIfCancellationRequested();
        if (failure is not null)
            throw failure;
    }

    /// <summary>
    /// Takes a started server's tools, or says why the server is refused. A restart lists again, and
    /// what the server says now replaces what it said before.
    /// </summary>
    private string? Register(McpServerSettings server, IList<Tool> tools)
    {
        var mine = new Dictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var tool in tools)
        {
            // Different names can become one name for the model (read.file and read_file, or two long
            // names cut at 64). Taking the first would let a server hand one tool's permission to
            // another by changing the order of its list.
            string name = ModelName(server, tool.Name);
            if (mine.TryGetValue(name, out var twin))
                return $"its tools {Clean(twin.Tool.Name, MaxName)} and {Clean(tool.Name, MaxName)} would both be called {name}";
            if (_tools.TryGetValue(name, out var other) && other.Server.Name != server.Name)
                return $"its tool {Clean(tool.Name, MaxName)} would be called {name}, which {Clean(other.Server.Name, MaxName)} already uses";
            if (tool.InputSchema.GetRawText().Length > MaxSchema)
                return $"the arguments of its tool {Clean(tool.Name, MaxName)} take more than {MaxSchema} characters to describe";
            mine[name] = new Entry(server, tool, Key(server, tool));
        }

        foreach (string stale in _tools.Where(t => t.Value.Server.Name == server.Name).Select(t => t.Key).ToArray())
            _tools.Remove(stale);
        foreach (var (name, entry) in mine)
            _tools[name] = entry;
        return null;
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

    private sealed record Entry(McpServerSettings Server, Tool Tool, string Key);
}
