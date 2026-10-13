using System.Net;
using System.Text;

namespace Tomte.Tests;

/// <summary>Answers every request with a fixture file and keeps the last request for assertions. No network.</summary>
internal sealed class RecordedHandler(string fixture, HttpStatusCode status = HttpStatusCode.OK) : HttpMessageHandler
{
    public HttpRequestMessage? Request { get; private set; }
    public string? Body { get; private set; }

    public static HttpClient Client(out RecordedHandler handler, string fixture, HttpStatusCode status = HttpStatusCode.OK)
    {
        handler = new RecordedHandler(fixture, status);
        return new HttpClient(handler);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Request = request;
        Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var text = await File.ReadAllTextAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture), ct);
        return new HttpResponseMessage(status) { Content = new StringContent(text, Encoding.UTF8) };
    }
}
