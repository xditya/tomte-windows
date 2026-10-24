using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tomte.AI.Browser;

/// <summary>A DevTools command the browser answered with an error.</summary>
internal sealed class CdpException(string message) : Exception(message);

/// <summary>
/// One Chrome DevTools Protocol connection to the browser's own endpoint, with flat sessions: a command for a tab
/// carries that tab's session id. Commands are answered by id; events go to <see cref="Event"/> on the receiving
/// thread, so a handler only posts commands and never waits for one.
/// </summary>
internal sealed class Cdp : IDisposable
{
    private static readonly TimeSpan CommandLimit = TimeSpan.FromSeconds(20);

    private readonly ClientWebSocket _socket = new();
    private readonly SemaphoreSlim _send = new(1, 1);
    private readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> _pending = new();
    private readonly CancellationTokenSource _closed = new();
    private int _next;

    /// <summary>An event: its method, its params and the session it came from (null for the browser's own).</summary>
    public event Action<string, JsonElement, string?>? Event;

    private Cdp()
    {
    }

    public static async Task<Cdp> ConnectAsync(Uri endpoint, CancellationToken ct)
    {
        var cdp = new Cdp();
        try
        {
            await cdp._socket.ConnectAsync(endpoint, ct).ConfigureAwait(false);
        }
        catch
        {
            cdp.Dispose();
            throw;
        }

        _ = Task.Run(cdp.ReceiveAsync);
        return cdp;
    }

    /// <summary>The text of one command, as the browser reads it.</summary>
    internal static string Message(int id, string method, JsonObject? parameters, string? session)
    {
        var message = new JsonObject { ["id"] = id, ["method"] = method, ["params"] = parameters ?? [] };
        if (session is not null)
            message["sessionId"] = session;
        return message.ToJsonString();
    }

    /// <summary>Sends a command and returns its result.</summary>
    public async Task<JsonElement> SendAsync(string method, JsonObject? parameters = null, string? session = null, CancellationToken ct = default)
    {
        int id = Interlocked.Increment(ref _next);
        var answer = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = answer;
        try
        {
            byte[] bytes = Encoding.UTF8.GetBytes(Message(id, method, parameters, session));
            await _send.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                await _socket.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
            }
            finally
            {
                _send.Release();
            }

            return await answer.Task.WaitAsync(CommandLimit, ct).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException($"The browser did not answer {method} in {CommandLimit.TotalSeconds:0} s.");
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    /// <summary>Sends a command without waiting for it, for event handlers. Commands on one session run in the order sent.</summary>
    public void Post(string method, JsonObject? parameters = null, string? session = null) =>
        _ = SendAsync(method, parameters, session).ContinueWith(t => _ = t.Exception, TaskScheduler.Default);

    public void Dispose()
    {
        _closed.Cancel();
        _socket.Dispose();
        foreach (var pending in _pending.Values)
            pending.TrySetException(new IOException("The connection to the browser closed."));
    }

    private async Task ReceiveAsync()
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        try
        {
            while (_socket.State == WebSocketState.Open)
            {
                var received = await _socket.ReceiveAsync(buffer, _closed.Token).ConfigureAwait(false);
                if (received.MessageType == WebSocketMessageType.Close)
                    break;
                message.Write(buffer, 0, received.Count);
                if (!received.EndOfMessage)
                    continue;
                Dispatch(message.GetBuffer().AsMemory(0, (int)message.Length));
                message.SetLength(0);
            }
        }
        catch (Exception e) when (e is WebSocketException or OperationCanceledException or ObjectDisposedException)
        {
        }

        foreach (var pending in _pending.Values)
            pending.TrySetException(new IOException("The connection to the browser closed."));
    }

    private void Dispatch(ReadOnlyMemory<byte> json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.TryGetProperty("id", out var id))
        {
            if (_pending.TryGetValue(id.GetInt32(), out var answer))
            {
                if (root.TryGetProperty("error", out var error))
                    answer.TrySetException(new CdpException(error.TryGetProperty("message", out var m) ? m.GetString() ?? "error" : "error"));
                else
                    answer.TrySetResult(root.TryGetProperty("result", out var result) ? result.Clone() : default);
            }

            return;
        }

        if (root.TryGetProperty("method", out var method))
        {
            var parameters = root.TryGetProperty("params", out var p) ? p.Clone() : default;
            string? session = root.TryGetProperty("sessionId", out var s) ? s.GetString() : null;
            try
            {
                Event?.Invoke(method.GetString()!, parameters, session);
            }
            catch (Exception e) when (e is not OutOfMemoryException)
            {
                // A handler that fails must not end the connection.
            }
        }
    }
}
