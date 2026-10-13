using System.Net;

namespace Tomte.Tests;

/// <summary>
/// Answers every request with a fixture file and keeps the last request for assertions. No network.
/// A non-zero <paramref name="readSize"/> hands the body out that many bytes per read, so events
/// and UTF-8 sequences split across reads the way they do on a slow connection.
/// </summary>
internal sealed class RecordedHandler(string fixture, HttpStatusCode status = HttpStatusCode.OK, int readSize = 0) : HttpMessageHandler
{
    public HttpRequestMessage? Request { get; private set; }
    public string? Body { get; private set; }

    public static HttpClient Client(out RecordedHandler handler, string fixture, HttpStatusCode status = HttpStatusCode.OK, int readSize = 0)
    {
        handler = new RecordedHandler(fixture, status, readSize);
        return new HttpClient(handler);
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
    {
        Request = request;
        Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(ct);
        var bytes = await File.ReadAllBytesAsync(Path.Combine(AppContext.BaseDirectory, "Fixtures", fixture), ct);
        Stream body = readSize > 0 ? new TrickleStream(bytes, readSize) : new MemoryStream(bytes);
        return new HttpResponseMessage(status) { Content = new StreamContent(body) };
    }

    private sealed class TrickleStream(byte[] bytes, int readSize) : MemoryStream(bytes)
    {
        public override int Read(byte[] buffer, int offset, int count) => base.Read(buffer, offset, Math.Min(count, readSize));

        public override int Read(Span<byte> buffer) => base.Read(buffer[..Math.Min(buffer.Length, readSize)]);

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default) =>
            base.ReadAsync(buffer[..Math.Min(buffer.Length, readSize)], ct);

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken ct) =>
            base.ReadAsync(buffer, offset, Math.Min(count, readSize), ct);
    }
}
