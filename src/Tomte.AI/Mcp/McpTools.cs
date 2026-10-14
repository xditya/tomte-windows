using System.Buffers;
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

/// <summary>
/// One tool as Settings lists it: the key its tier is stored under, and its name and description cleaned
/// for display. <paramref name="LeftOut"/> says why the model is not offered the tool, or is null when it is.
/// </summary>
public sealed record McpToolInfo(string Key, string Name, string Description, string? LeftOut = null);

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
    public const string Separator = McpServerSettings.Separator;

    /// <summary>Longest server or tool name shown in a prompt or in Settings.</summary>
    public const int MaxName = 64;

    /// <summary>Longest tool description given to the model.</summary>
    public const int MaxDescription = 1000;

    /// <summary>Longest tool result given to the model, the answer and the log.</summary>
    public const int MaxResult = 20_000;

    /// <summary>Longest error text taken from a server or from the SDK.</summary>
    public const int MaxError = 300;

    /// <summary>Longest argument schema of one tool; the model gets every schema on every send, so a tool with a longer one is left out.</summary>
    public const int MaxSchema = 20_000;

    // For the confirmation prompt: readable, so letters outside ASCII and characters such as & are written as they are.
    private static readonly JsonSerializerOptions Shown = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private readonly McpServerSettings[] _servers;
    private readonly string? _invalid;
    private readonly Func<McpServerSettings, string?, IReadOnlyDictionary<string, string>, CancellationToken, Task<Connection>> _connect;
    private readonly Func<string, string?> _readSecret;
    private readonly SemaphoreSlim _gate = new(1, 1);

    // Cancelled first thing in dispose: a start in progress holds the gate until the server answers, and
    // the SDK waits 60 s for one that never does.
    private readonly CancellationTokenSource _closing = new();

    // Written under the gate; the idle timer's thread and Running read it without.
    private readonly ConcurrentDictionary<string, Connection> _clients = new();

    // By the name the model sees, in a fixed order: the model gets the same tool list on every send.
    private readonly SortedDictionary<string, Entry> _tools = new(StringComparer.Ordinal);

    // Tools the model is not offered because their schema is too long; Settings lists them with the reason.
    private readonly List<Entry> _leftOut = [];
    private readonly Timer _idle;
    private int _busy;
    private bool _disposed;

    /// <param name="connect">Starts or connects one server; tests pass an in-process one.</param>
    /// <param name="readSecret">Reads a header or variable value by credential id; Credential Manager unless a test passes its own.</param>
    public McpTools(IEnumerable<McpServerSettings> servers, TimeSpan? idleTimeout = null, Func<McpServerSettings, CancellationToken, Task<McpClient>>? connect = null, Func<string, string?>? readSecret = null)
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
        _connect = connect is null ? ConnectAsync : async (server, header, environment, ct) => new Connection(await connect(server, ct), null, Secrets(header, environment));
        _readSecret = readSecret ?? CredentialStore.Read;
        IdleTimeout = idleTimeout ?? TimeSpan.FromMinutes(5);
        _idle = new Timer(_ => _ = StopIfIdleAsync());
    }

    /// <summary>
    /// The enabled servers' tools, or null when no server is on. A palette passes the null on, so a user
    /// with no MCP server never loads the SDK.
    /// </summary>
    public static McpTools? For(IEnumerable<McpServerSettings> servers) => servers.Any(s => s.Enabled) ? new McpTools(servers) : null;

    public TimeSpan IdleTimeout { get; }

    /// <summary>How many servers are started or connected right now.</summary>
    public int Running => _clients.Count;

    /// <summary>Every tool listed so far, for Settings.</summary>
    public IReadOnlyList<McpToolInfo> Tools =>
    [
        .. _tools.Values.Select(t => Info(t, null)),
        .. _leftOut.Select(t => Info(t, $"The AI is not offered this tool: the description of its arguments is {t.Tool.InputSchema.GetRawText().Length:N0} characters long, and Tomte passes on at most {MaxSchema:N0}.")),
    ];

    private static McpToolInfo Info(Entry t, string? leftOut) => new(t.Key, Clean(t.Tool.Name, MaxName), Clean(t.Tool.Description, MaxDescription), leftOut);

    /// <summary>The servers a listing would start or connect now, by name: what the "where data goes" indicator shows.</summary>
    public IReadOnlyList<string> NotRunning => [.. _servers.Where(s => !_clients.ContainsKey(s.Name)).Select(s => Clean(s.Name, MaxName))];

    public bool Owns(string tool) => _tools.ContainsKey(tool);

    /// <summary>The name of the server a tool runs on, for the same indicator.</summary>
    public string ServerOf(string tool) => Clean(_tools[tool].Server.Name, MaxName);

    /// <summary>What this tool's tier override and "always allow" are stored under; see <see cref="Key"/>.</summary>
    public string KeyOf(string tool) => _tools[tool].Key;

    public ActionTier TierOf(string tool) => _tools[tool].Server.TierFor(_tools[tool].Key);

    /// <summary>One line for the confirmation prompt: which server, which tool, with what.</summary>
    public string Describe(string tool, JsonElement input)
    {
        var (server, mcpTool, _) = _tools[tool];
        // What is shown is what is sent, not what the model wrote. All of it is sent; only this line is cut.
        var args = Arguments(mcpTool, input);
        var (text, cut) = Bound(JsonSerializer.Serialize(args, Shown), 120, lines: false);
        string shown = args.Count == 0 ? "" : cut == 0 ? " " + text : $" {text} [{cut} more characters not shown]";
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
    /// Cut is how many characters of a long result were left out, for the step the user sees.
    /// </summary>
    public async Task<(ToolResultPart Result, int Cut)> CallAsync(ToolCallPart call, CancellationToken ct)
    {
        var (server, tool, key) = _tools[call.Name];
        Interlocked.Increment(ref _busy);
        try
        {
            Connection connection;
            await _gate.WaitAsync(ct);
            try
            {
                // The idle timeout may have stopped the server since the listing; this starts it again.
                await StartMissingAsync([server], ct);
                connection = _clients[server.Name];
            }
            finally
            {
                _gate.Release();
            }

            // A restart lists the tools again. What the user allowed was the tool as it was before.
            if (!_tools.TryGetValue(call.Name, out var now) || now.Key != key)
                return (new ToolResultPart(call.Id, call.Name, $"{Clean(server.Name, MaxName)} changed this tool when it restarted, so the call was not made. Call it again to use it as it is now.", IsError: true), 0);

            var args = Arguments(tool, call.Input);
            var client = connection.Client;
            try
            {
                var result = await UnlessGoneAsync(c => client.CallToolAsync(tool.Name, args, cancellationToken: c).AsTask(), client.Completion, ct);
                var (text, cut) = Result(result, connection.Secrets);
                return (new ToolResultPart(call.Id, call.Name, text, result.IsError == true), cut);
            }
            catch (McpException e)
            {
                return (new ToolResultPart(call.Id, call.Name, $"{Clean(server.Name, MaxName)}: {Clean(tool.Name, MaxName)} failed: {Explain(e, connection.Secrets)}", IsError: true), 0);
            }
            catch (Exception e) when (e is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // The process exited or the connection dropped. Forget the server, so the next call starts
                // it again instead of failing the same way for the rest of the session.
                await _gate.WaitAsync(CancellationToken.None);
                try
                {
                    if (_clients.TryRemove(new KeyValuePair<string, Connection>(server.Name, connection)))
                        await StopAsync(connection);
                }
                finally
                {
                    _gate.Release();
                }

                throw Failure(server, "stopped", e, connection.Secrets);
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
    /// The settings with one server added (no <paramref name="before"/>), edited, or removed (no
    /// <paramref name="now"/>). The always-allow grants for the server's tools go with it unless it
    /// keeps its name and target: a grant was given for what ran then, under the name it had then.
    /// </summary>
    public static TomteSettings WithServer(TomteSettings settings, McpServerSettings? before, McpServerSettings? now)
    {
        if (before is null)
            return now is null ? settings : settings with { McpServers = [.. settings.McpServers, now] };
        bool same = now is not null && now.Name == before.Name && now.Target == before.Target;
        return settings with
        {
            McpServers = [.. settings.McpServers.Where(s => now is not null || s.Name != before.Name).Select(s => s.Name == before.Name ? now! : s)],
            AlwaysAllowedTools = same ? settings.AlwaysAllowedTools : [.. settings.AlwaysAllowedTools.Where(g => !before.OwnsKey(g))],
        };
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
    /// description or arguments changed, gets another key, so it asks again. The key does not cover
    /// the header, the program a command resolves to, a package that updates without changing what
    /// its tools say, or the environment.
    /// </summary>
    internal static string Key(McpServerSettings server, Tool tool)
    {
        var identity = new StringBuilder();
        // Each part with its length in front, so no two different sets of parts make the same text.
        foreach (string part in (string[])[server.Target, tool.Name, tool.Description ?? "", Canonical(tool.InputSchema)])
            identity.Append(part.Length).Append(':').Append(part);
        return ModelName(server, tool.Name) + "#" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(identity.ToString())), 0, 8);
    }

    /// <summary>
    /// A JSON value as one fixed text: every object's properties in name order, no spacing. A server
    /// that sends the same schema with its properties in another order still gets the same key.
    /// </summary>
    internal static string Canonical(JsonElement value)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
            Write(writer, value);
        return Encoding.UTF8.GetString(buffer.WrittenSpan);

        static void Write(Utf8JsonWriter writer, JsonElement value)
        {
            switch (value.ValueKind)
            {
                case JsonValueKind.Object:
                    writer.WriteStartObject();
                    foreach (var property in value.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                    {
                        writer.WritePropertyName(property.Name);
                        Write(writer, property.Value);
                    }

                    writer.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    writer.WriteStartArray();
                    foreach (var item in value.EnumerateArray())
                        Write(writer, item);
                    writer.WriteEndArray();
                    break;
                default:
                    value.WriteTo(writer);
                    break;
            }
        }
    }

    /// <summary>
    /// What stands where Tomte left out the rest of a server's text. It names Tomte, so a model does
    /// not take it for part of the text, such as the content of a file it asked for.
    /// </summary>
    public static string CutNote(int characters) => $"[Cut by Tomte: {characters} more characters were not passed on]";

    /// <summary>
    /// Text a server wrote, made safe to show and cut to about <paramref name="max"/> characters, with a
    /// <see cref="CutNote"/> where it was cut. <see cref="Bound"/> says what is taken out.
    /// </summary>
    public static string Clean(string? text, int max, bool lines = false)
    {
        var (clean, cut) = Bound(text, max, lines);
        return cut == 0 ? clean : clean + (lines ? "\n" : " ") + CutNote(cut);
    }

    /// <summary>
    /// The cleaned start of a text, and how many characters after it were left out. Control characters
    /// go, and so do the characters that reorder text and the invisible tag characters, so a name cannot
    /// read as something else in a prompt. Whitespace runs become one space unless <paramref name="lines"/> keeps line breaks and tabs.
    /// </summary>
    internal static (string Text, int Cut) Bound(string? text, int max, bool lines)
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
            // Tag characters (U+E0000 to U+E007F) draw nothing, yet a model reads them: they could carry hidden words.
            if (c == '\uDB40' && i + 1 < text.Length && text[i + 1] is >= '\uDC00' and <= '\uDC7F')
            {
                i++;
                continue;
            }

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

        // Half of a surrogate pair is not text.
        if (i < text.Length && char.IsHighSurrogate(clean[^1]))
        {
            clean.Length--;
            i--;
        }

        return (clean.ToString(), text.Length - i);
    }

    /// <summary>A result as text; see <see cref="Result"/>.</summary>
    public static string ResultText(CallToolResult result) => Result(result, []).Text;

    /// <summary>
    /// A result as the text the model gets, and how many characters were cut from it. Text blocks as they
    /// are, other content as a short marker, since tool results reach the model as text. The header and
    /// variable values are hidden here too: a server can quote them back ("token ... is invalid").
    /// </summary>
    internal static (string Text, int Cut) Result(CallToolResult result, IReadOnlyList<string> secrets)
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
        var (clean, cut) = Bound(Hide(text, secrets), MaxResult, lines: true);
        return (clean.Length == 0 ? "(no output)" : cut == 0 ? clean : clean + "\n" + CutNote(cut), cut);
    }

    /// <summary>What a text becomes when it holds a saved value too short to cut out of it.</summary>
    public const string Withheld = "Not shown: this text holds a saved header or variable value, which is too short to hide on its own.";

    /// <summary>
    /// Replaces each saved value wherever it appears in <paramref name="text"/>: the whole value, and each
    /// long part of it, since "Bearer abc" can be quoted back without the scheme. Longest first, so a
    /// value that holds another is hidden whole. A value under <see cref="McpSecrets.MinLength"/>
    /// characters is not cut out: replacing it would mangle ordinary text (the value 2 in "502") and
    /// show where it stood, so the whole text is withheld instead.
    /// </summary>
    internal static string Hide(string text, IReadOnlyList<string> secrets)
    {
        var present = secrets.Where(s => s.Length > 0).ToArray();
        if (present.Any(s => s.Length < McpSecrets.MinLength && text.Contains(s, StringComparison.Ordinal)))
            return Withheld;
        var parts = present.Where(s => s.Length >= McpSecrets.MinLength)
            .SelectMany(s => s.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Where(p => p.Length >= McpSecrets.MinLength).Prepend(s));
        foreach (string part in parts.OrderByDescending(p => p.Length))
            text = text.Replace(part, "[hidden]", StringComparison.Ordinal);
        return text;
    }

    public async ValueTask DisposeAsync()
    {
        await _idle.DisposeAsync();
        _closing.Cancel();
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

    private static async Task<Connection> ConnectAsync(McpServerSettings server, string? header, IReadOnlyDictionary<string, string> environment, CancellationToken ct)
    {
        if (server.Transport == McpTransport.Stdio)
        {
            // Tomte starts the process and hands the SDK its streams. The SDK's own stdio transport wraps
            // every command in cmd.exe, which changes arguments, and leaves the process alive when Tomte dies.
            var process = McpProcess.Start(server, environment);
            try
            {
                return new Connection(await McpClient.CreateAsync(new StreamClientTransport(process.Input, process.Output), cancellationToken: ct), process, Secrets(header, environment));
            }
            catch (Exception e)
            {
                string said = e is OperationCanceledException ? "" : await process.ErrorTailAsync();
                process.Kill();
                if (said.Length == 0)
                    throw;
                throw new IOException($"{e.Message} It wrote: {said}");
            }
        }

        var transport = new HttpClientTransport(new HttpClientTransportOptions
        {
            Name = server.Name,
            Endpoint = new Uri(server.Url),
            TransportMode = HttpTransportMode.StreamableHttp,
            // The long-lived GET stream is for messages a server starts on its own; a client that only calls tools has no use for it.
            EnableStandaloneGetStream = false,
            AdditionalHeaders = header is null ? null : new Dictionary<string, string> { [server.HeaderName] = header },
        });
        try
        {
            return new Connection(await McpClient.CreateAsync(transport, cancellationToken: ct), transport, Secrets(header, environment));
        }
        catch
        {
            await transport.DisposeAsync();
            throw;
        }
    }

    private static string[] Secrets(string? header, IReadOnlyDictionary<string, string> environment) =>
        [.. header is null ? [] : new[] { header }, .. environment.Values];

    // The exception itself is not kept as the inner one: it and the errors inside it can hold the header
    // or a variable's value, and whoever logs this error logs those too.
    private static InvalidOperationException Failure(McpServerSettings server, string what, Exception e, IReadOnlyList<string> secrets) =>
        new($"MCP server \"{Clean(server.Name, MaxName)}\" {what}: {Explain(e, secrets)} Turn it off in Settings to send without it.");

    /// <summary>
    /// An error's text for the user and the model: the saved values hidden, then cleaned and cut like all
    /// server text. The SDK quotes a header value it could not add, and a server can send one back.
    /// </summary>
    private static string Explain(Exception e, IReadOnlyList<string> secrets)
    {
        string text = Clean(Hide(e.Message, secrets), MaxError);
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

        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct, _closing.Token);
        ct = stop.Token;

        // On the pool: starting a process blocks for a while before the first await, and the caller is the island's thread.
        var started = await Task.WhenAll(servers.Where(s => !_clients.ContainsKey(s.Name)).Select(s => Task.Run(() => StartAsync(s, ct))));
        Exception? failure = null;
        foreach (var (server, connection, tools, error) in started)
        {
            if (connection is null)
            {
                failure ??= error;
                continue;
            }

            if (Register(server, tools) is { } problem)
            {
                await StopAsync(connection);
                failure ??= new InvalidOperationException($"MCP server \"{Clean(server.Name, MaxName)}\" cannot be used: {problem}. Turn it off in Settings to send without it.");
                continue;
            }

            _clients[server.Name] = connection;
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
        var leftOut = new List<Entry>();
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
            // Left out, not offered: the model gets every schema on every send. The server's other tools stay.
            if (tool.InputSchema.GetRawText().Length > MaxSchema)
                leftOut.Add(new Entry(server, tool, Key(server, tool)));
            else
                mine[name] = new Entry(server, tool, Key(server, tool));
        }

        foreach (string stale in _tools.Where(t => t.Value.Server.Name == server.Name).Select(t => t.Key).ToArray())
            _tools.Remove(stale);
        foreach (var (name, entry) in mine)
            _tools[name] = entry;
        _leftOut.RemoveAll(t => t.Server.Name == server.Name);
        _leftOut.AddRange(leftOut);
        return null;
    }

    /// <summary>Never throws: a server that did not start comes back without a client, with the error unless the start was cancelled.</summary>
    private async Task<(McpServerSettings Server, Connection? Connection, IList<Tool> Tools, Exception? Error)> StartAsync(McpServerSettings server, CancellationToken ct)
    {
        Connection? connection = null;
        string? header = null;
        var environment = new Dictionary<string, string>();
        try
        {
            // Read once and kept with the connection: what gets hidden later is the value that was sent,
            // also when Settings has changed or deleted it since.
            header = server.HeaderName.Length > 0 ? _readSecret(server.CredentialId) : null;
            foreach (string variable in server.Environment)
            {
                environment[variable] = _readSecret(server.EnvCredentialId(variable))
                    ?? throw new InvalidOperationException($"The value of {variable} is not in Windows Credential Manager. Enter it again in Settings.");
            }

            // A process started now would only be killed again.
            ct.ThrowIfCancellationRequested();
            connection = await _connect(server, header, environment, ct);
            var client = connection.Client;
            var tools = await UnlessGoneAsync(c => client.ListToolsAsync(cancellationToken: c).AsTask(), client.Completion, ct);
            return (server, connection, [.. tools.Select(t => t.ProtocolTool)], null);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Connected but not listed (a cancel, a timeout, a bad answer): nobody else knows this server.
            if (connection is not null)
                await StopAsync(connection);
            return (server, null, [], e is OperationCanceledException && ct.IsCancellationRequested ? null : Failure(server, "did not start", e, Secrets(header, environment)));
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

    /// <summary>
    /// Never throws. Runs on the pool: ending a process tree takes tens of milliseconds, and the caller is
    /// the island's thread in the middle of the retract morph. The client goes first: for an HTTP server
    /// that ends the session, for a process it lets go of the streams.
    /// </summary>
    private static Task StopAsync(Connection connection) => Task.Run(async () =>
    {
        foreach (var part in new IAsyncDisposable?[] { connection.Client, connection.Owner })
        {
            try
            {
                if (part is not null)
                    await part.DisposeAsync();
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // It already exited, or it failed while stopping; either way there is nothing left to do for it.
            }
        }
    });

    private sealed record Entry(McpServerSettings Server, Tool Tool, string Key);

    /// <summary>
    /// A started server: the SDK's client, what Tomte has to stop itself (the process, or the HTTP
    /// transport with its connections), and the header and variable values it was given, to hide in what
    /// it sends back.
    /// </summary>
    private sealed record Connection(McpClient Client, IAsyncDisposable? Owner, IReadOnlyList<string> Secrets);
}
