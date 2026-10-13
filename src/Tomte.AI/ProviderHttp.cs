using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Tomte.AI;

/// <summary>HTTP plumbing shared by the adapters. Nothing here logs; errors are scrubbed of the key.</summary>
internal static class ProviderHttp
{
    /// <summary>A streaming POST: JSON body in, server-sent events out.</summary>
    public static HttpRequestMessage Post(string url, JsonNode body)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("text/event-stream"));
        return request;
    }

    public static async Task<HttpResponseMessage> SendAsync(
        HttpClient http, HttpRequestMessage request, string? key, CancellationToken ct)
    {
        HttpResponseMessage response;
        try
        {
            response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (HttpRequestException e)
        {
            throw new ProviderException(Scrub(e.Message, key));
        }
        if (response.IsSuccessStatusCode)
            return response;

        var text = await response.Content.ReadAsStringAsync(ct);
        var status = (int)response.StatusCode;
        response.Dispose();
        throw new ProviderException(Scrub($"HTTP {status}: {ErrorMessage(text)}", key), status);
    }

    public static async Task<JsonDocument> GetJsonAsync(HttpClient http, HttpRequestMessage request, string? key, CancellationToken ct)
    {
        using var response = await SendAsync(http, request, key, ct);
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct);
    }

    /// <summary>Yields the data payload of each server-sent event, in order.</summary>
    public static async IAsyncEnumerable<string> ReadSseAsync(
        HttpResponseMessage response, [EnumeratorCancellation] CancellationToken ct)
    {
        using (response)
        {
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var data = new StringBuilder();
            while (await ReadLineAsync(reader, ct) is { } line)
            {
                if (line.Length == 0)
                {
                    if (data.Length > 0)
                        yield return data.ToString();
                    data.Clear();
                }
                else if (line.StartsWith("data:", StringComparison.Ordinal))
                {
                    if (data.Length > 0)
                        data.Append('\n');
                    data.Append(line.AsSpan(line.StartsWith("data: ", StringComparison.Ordinal) ? 6 : 5));
                }
            }
            if (data.Length > 0)
                yield return data.ToString();
        }
    }

    private static async Task<string?> ReadLineAsync(StreamReader reader, CancellationToken ct)
    {
        try
        {
            return await reader.ReadLineAsync(ct);
        }
        catch (IOException e)
        {
            throw new ProviderException($"The connection dropped mid-stream: {e.Message}");
        }
    }

    /// <summary>Pulls a readable message out of the error body shapes the three APIs use.</summary>
    public static string ErrorMessage(string body)
    {
        try
        {
            var node = JsonNode.Parse(body);
            if (node is JsonArray { Count: > 0 } array)
                node = array[0];
            var error = node?["error"];
            var message = error?["message"]?.GetValue<string>() ?? (error as JsonValue)?.GetValue<string>();
            if (message is not null)
                return message;
        }
        catch (JsonException)
        {
        }
        return body.Length > 300 ? body[..300] : body;
    }

    public static string Scrub(string text, string? key) =>
        string.IsNullOrEmpty(key) ? text : text.Replace(key, "[key]", StringComparison.Ordinal);

    /// <summary>Parses accumulated tool arguments. Empty means no arguments.</summary>
    public static JsonElement ParseArguments(string json, string? key)
    {
        try
        {
            return JsonDocument.Parse(json.Length == 0 ? "{}" : json).RootElement.Clone();
        }
        catch (JsonException)
        {
            throw new ProviderException(Scrub($"Tool call arguments are not valid JSON: {json}", key));
        }
    }
}
