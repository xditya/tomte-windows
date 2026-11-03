using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Tomte.Core.Drop;
using Xunit;
using ZXing;

namespace Tomte.Tests;

/// <summary>
/// Tomte Drop's server, through HTTP on loopback: the token, the page, the list, downloads whole and in ranges, folders
/// as zips, uploads with hostile names, text, the idle stop and Dispose, and the QR code read back by ZXing.
/// </summary>
public sealed class DropShareTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"tomte-drop-{Guid.NewGuid():N}");
    private readonly List<DropShare> _shares = [];
    private readonly ConcurrentQueue<DropActivity> _activity = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(10) };

    public DropShareTests() => Directory.CreateDirectory(Shared);

    private string Shared => Path.Combine(_dir, "shared");

    private string Received => Path.Combine(_dir, "received");

    public void Dispose()
    {
        foreach (var share in _shares)
        {
            share.Dispose();
        }

        _http.Dispose();
        Directory.Delete(_dir, recursive: true);
    }

    private DropShare Share(IReadOnlyList<DropFile> files, string pc = "DESKTOP-TEST", TimeSpan? idle = null)
    {
        var options = new DropOptions(Received, pc, 0x3E8E6A) { Address = IPAddress.Loopback, Idle = idle ?? TimeSpan.FromMinutes(5) };
        var share = DropShare.Start(files, options, out string? problem) ?? throw new InvalidOperationException(problem);
        _shares.Add(share);
        share.Activity += _activity.Enqueue;
        return share;
    }

    private DropFile Put(string name, byte[] bytes)
    {
        string path = Path.Combine(Shared, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes);
        return new DropFile(Path.GetFileName(name), path);
    }

    private DropFile Put(string name, string text) => Put(name, Encoding.UTF8.GetBytes(text));

    private static byte[] Bytes(int length) => [.. Enumerable.Range(0, length).Select(i => (byte)(i * 7 + 3))];

    private static Uri At(DropShare share, string path) => new(share.Link, path);

    private List<DropActivity> Seen(DropActivityKind kind) => [.. _activity.Where(a => a.Kind == kind)];

    // Activities are raised on the share's own threads, some after the last byte went out.
    private static async Task WaitFor(Func<bool> condition)
    {
        for (int i = 0; i < 500 && !condition(); i++)
        {
            await Task.Delay(10);
        }

        Assert.True(condition());
    }

    /// <summary>Sends a request head (and nothing more) on a socket of its own and reads the answer to its end.</summary>
    private static async Task<string> Raw(DropShare share, string requestLine, params string[] headers)
    {
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, share.Link.Port);
        var stream = client.GetStream();
        string head = $"{requestLine}\r\nHost: {share.Link.Authority}\r\n{string.Concat(headers.Select(h => h + "\r\n"))}\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(head));
        using var reader = new StreamReader(stream, Encoding.ASCII);
        return await reader.ReadToEndAsync();
    }

    [Fact]
    public async Task ARequestWithoutTheTokenGetsABare404()
    {
        var share = Share([Put("report.pdf", Bytes(100))]);
        string token = share.Link.Segments[1].TrimEnd('/');
        Assert.Equal(22, token.Length);
        Assert.Equal($"http://127.0.0.1:{share.Link.Port}/{token}/", share.Link.AbsoluteUri);
        Assert.InRange(share.Link.Port, DropShare.FirstPort, DropShare.LastPort);
        Assert.False(share.PublicNetwork);

        string wrong = (token[0] == 'A' ? 'B' : 'A') + token[1..];
        foreach (string path in new[] { "/", $"/{wrong}/", $"/{wrong}/list", $"/{wrong}/file/0", "/list", "/file/0", $"/x{token}/", $"/{token[..^1]}/list" })
        {
            using var response = await _http.GetAsync(new Uri(share.Link, path));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
            Assert.Empty(await response.Content.ReadAsByteArrayAsync());
            Assert.DoesNotContain(response.Headers, h => h.Key != "Connection");
        }

        // The right token for another host name: a page elsewhere that pointed its own name at this address.
        using var rebound = new HttpRequestMessage(HttpMethod.Get, At(share, "list"));
        rebound.Headers.Host = $"tomte.example:{share.Link.Port}";
        using (var response = await _http.SendAsync(rebound))
        {
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        // Unknown paths behind the token are 404 too; no route takes a path from the request.
        foreach (string path in new[] { "file/1", "file/-1", "file/x", "file/0/..", "nothing", "upload" })
        {
            using var response = await _http.GetAsync(At(share, path));
            Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        }

        Assert.Empty(_activity);
    }

    [Fact]
    public async Task ThePageLoadsWithThePcNameEscapedAndAsksForNothingOutside()
    {
        var share = Share([Put("report.pdf", Bytes(10))], pc: "<b>Ana's \"PC\" & {{accent-dark}}</b>");

        using var request = new HttpRequestMessage(HttpMethod.Get, share.Link);
        request.Headers.UserAgent.ParseAdd("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1");
        using var response = await _http.SendAsync(request);
        string page = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        Assert.Contains("default-src 'none'", string.Join(";", response.Headers.GetValues("Content-Security-Policy")));
        Assert.Contains("<title>Tomte Drop</title>", page);
        Assert.Contains("&lt;b&gt;Ana&#39;s &quot;PC&quot; &amp; {{accent-dark}}&lt;/b&gt;", page);
        Assert.DoesNotContain("<b>Ana", page);
        Assert.DoesNotContain("{{pc}}", page);
        Assert.DoesNotContain("{{on-accent", page);
        Assert.Matches("--accent: #[0-9A-F]{6};", page);
        // Nothing the page loads comes from anywhere but the PC: no stylesheet, font or script by address.
        Assert.DoesNotContain("<link", page);
        Assert.DoesNotContain("url(", page);
        Assert.DoesNotContain("@import", page);
        Assert.DoesNotMatch(@"(src|href)\s*=\s*[""']?(https?:)?//", page);
        Assert.DoesNotContain("—", page);

        // A second load from the same phone opens nothing new; another device does.
        using (var again = new HttpRequestMessage(HttpMethod.Get, share.Link))
        {
            again.Headers.UserAgent.ParseAdd("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1");
            (await _http.SendAsync(again)).Dispose();
        }

        using (var android = new HttpRequestMessage(HttpMethod.Get, share.Link))
        {
            android.Headers.UserAgent.ParseAdd("Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36");
            (await _http.SendAsync(android)).Dispose();
        }

        await WaitFor(() => Seen(DropActivityKind.Opened).Count == 2);
        Assert.Equal(["iPhone", "Android phone"], Seen(DropActivityKind.Opened).Select(a => a.Device));

        // Without the closing slash the page's own requests would miss the token, so it sends the phone there.
        string token = share.Link.Segments[1].TrimEnd('/');
        using var bare = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false });
        using var moved = await bare.GetAsync(new Uri(share.Link, "/" + token));
        Assert.Equal(HttpStatusCode.MovedPermanently, moved.StatusCode);
        Assert.Equal(share.Link.AbsolutePath, moved.Headers.Location?.OriginalString);
    }

    [Theory]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 18_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Mobile/15E148 Safari/604.1", "iPhone")]
    [InlineData("Mozilla/5.0 (iPad; CPU OS 17_0 like Mac OS X) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/17.0 Mobile/15E148 Safari/604.1", "iPad")]
    [InlineData("Mozilla/5.0 (Linux; Android 15; Pixel 9) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Mobile Safari/537.36", "Android phone")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; SM-X710) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36", "Android tablet")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15 (KHTML, like Gecko) Version/18.0 Safari/605.1.15", "Mac")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36 Edg/131.0.0.0", "Windows PC")]
    [InlineData("Mozilla/5.0 (X11; Linux x86_64; rv:133.0) Gecko/20100101 Firefox/133.0", "Linux PC")]
    [InlineData("curl/8.9.1", "A device")]
    [InlineData(null, "A device")]
    public void TheDeviceIsNamedFromItsUserAgent(string? agent, string expected) => Assert.Equal(expected, DropShare.DeviceName(agent));

    [Fact]
    public async Task TheListGivesEachItemsKindAndTheTextOrLinkThePageShows()
    {
        Directory.CreateDirectory(Path.Combine(Shared, "Assets"));
        DropFile[] files =
        [
            Put("report.pdf", Bytes(1200)),
            Put("photo.jpg", Bytes(300)),
            Put("notes.txt", "Buy milk\nCall Ana"),
            Put("example.com.url", "[InternetShortcut]\r\nURL=https://example.com/a?b=1\r\n"),
            new DropFile("Assets", Path.Combine(Shared, "Assets")),
            new DropFile("gone.pdf", Path.Combine(Shared, "gone.pdf")),
            Put("long.md", new string('x', 70_000)),
            Put("sneaky.url", "[InternetShortcut]\r\nURL=javascript:alert(1)\r\n"),
        ];
        var share = Share(files);

        using var response = await _http.GetAsync(At(share, "list"));
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.True(response.Headers.CacheControl?.NoStore);
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var items = json.RootElement.EnumerateArray().ToList();

        Assert.Equal(Enumerable.Range(0, files.Length), items.Select(i => i.GetProperty("id").GetInt32()));
        Assert.Equal(files.Select(f => f.Name), items.Select(i => i.GetProperty("name").GetString()));
        Assert.Equal(["file", "image", "text", "link", "folder", "missing", "text", "file"], items.Select(i => i.GetProperty("kind").GetString()));
        Assert.Equal(1200, items[0].GetProperty("size").GetInt64());
        Assert.False(items[4].TryGetProperty("size", out _));
        Assert.False(items[5].TryGetProperty("size", out _));
        Assert.Equal("Buy milk\nCall Ana", items[2].GetProperty("text").GetString());
        Assert.Equal("https://example.com/a?b=1", items[3].GetProperty("url").GetString());
        // Too long to show: a download only. A shortcut to anything but a web page is never given as a link.
        Assert.False(items[6].TryGetProperty("text", out _));
        Assert.False(items[7].TryGetProperty("url", out _));
    }

    [Fact]
    public async Task ADownloadIsTheFileByteForByteAndAGoneFileIs404()
    {
        var bytes = Bytes(200_000);
        var pdf = Put("Résumé ✓.pdf", bytes);
        var photo = Put("photo.jpg", Bytes(500));
        var gone = Put("gone.txt", "soon gone");
        var share = Share([pdf, photo, gone]);

        using (var response = await _http.GetAsync(At(share, "file/0")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            Assert.Equal(bytes, await response.Content.ReadAsByteArrayAsync());
            Assert.Equal("application/pdf", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("attachment", response.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("Résumé ✓.pdf", response.Content.Headers.ContentDisposition?.FileNameStar);
            Assert.Equal(["bytes"], response.Headers.AcceptRanges);
        }

        await WaitFor(() => Seen(DropActivityKind.Sent).Count == 1);
        Assert.Equal([(DropActivityKind.Sending, "Résumé ✓.pdf"), (DropActivityKind.Sent, "Résumé ✓.pdf")], _activity.Select(a => (a.Kind, a.Name)));

        // A preview of a picture is shown in place and is not a download.
        using (var preview = await _http.GetAsync(At(share, "file/1?inline=1")))
        {
            Assert.Equal("inline", preview.Content.Headers.ContentDisposition?.DispositionType);
            Assert.Equal("image/jpeg", preview.Content.Headers.ContentType?.MediaType);
            Assert.Equal(500, (await preview.Content.ReadAsByteArrayAsync()).Length);
        }

        File.Delete(gone.Path);
        using (var missing = await _http.GetAsync(At(share, "file/2")))
        {
            Assert.Equal(HttpStatusCode.NotFound, missing.StatusCode);
        }

        Assert.Equal(2, _activity.Count);
    }

    [Fact]
    public async Task ARangeGivesThatSliceWith206()
    {
        var bytes = Bytes(1000);
        var share = Share([Put("clip.mp4", bytes)]);

        async Task<(HttpStatusCode Status, byte[] Body, string? Range)> Get(string range, string? ifRange = null)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, At(share, "file/0"));
            request.Headers.TryAddWithoutValidation("Range", range);
            if (ifRange is not null)
            {
                request.Headers.TryAddWithoutValidation("If-Range", ifRange);
            }

            using var response = await _http.SendAsync(request);
            return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(), response.Content.Headers.ContentRange?.ToString());
        }

        var middle = await Get("bytes=10-19");
        Assert.Equal(HttpStatusCode.PartialContent, middle.Status);
        Assert.Equal(bytes[10..20], middle.Body);
        Assert.Equal("bytes 10-19/1000", middle.Range);

        Assert.Equal(bytes[995..], (await Get("bytes=-5")).Body);
        Assert.Equal(bytes[600..], (await Get("bytes=600-")).Body);
        Assert.Equal(bytes[990..], (await Get("bytes=990-5000")).Body);

        var past = await Get("bytes=1000-");
        Assert.Equal(HttpStatusCode.RequestedRangeNotSatisfiable, past.Status);
        Assert.Equal("bytes */1000", past.Range);

        // Several ranges, a range it cannot read, or a resume of another copy of the file: the whole file.
        Assert.Equal((HttpStatusCode.OK, 1000), ((await Get("bytes=0-1,5-6")).Status, (await Get("bytes=0-1,5-6")).Body.Length));
        Assert.Equal(HttpStatusCode.OK, (await Get("bytes=x-y")).Status);
        Assert.Equal(HttpStatusCode.OK, (await Get("bytes=10-19", "\"not-this-one\"")).Status);

        // A resume of this copy, by the tag it was given.
        using var whole = await _http.GetAsync(At(share, "file/0"));
        string tag = whole.Headers.ETag!.Tag;
        var resumed = await Get("bytes=500-", tag);
        Assert.Equal(HttpStatusCode.PartialContent, resumed.Status);
        Assert.Equal(bytes[500..], resumed.Body);
    }

    [Fact]
    public async Task AFolderDownloadsAsAZipOfItsFilesAndAllAsOneZip()
    {
        var a = Put(Path.Combine("Assets", "a.txt"), "first");
        var b = Put(Path.Combine("Assets", "sub", "b.bin"), Bytes(70_000));
        var folder = new DropFile("Assets", Path.GetDirectoryName(a.Path)!);
        var note = Put("note.txt", "a note");
        var share = Share([folder, note]);

        using (var response = await _http.GetAsync(At(share, "file/0")))
        {
            Assert.Equal("application/zip", response.Content.Headers.ContentType?.MediaType);
            Assert.Equal("Assets.zip", response.Content.Headers.ContentDisposition?.FileNameStar);
            using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
            Assert.Equal(["Assets/", "Assets/a.txt", "Assets/sub/", "Assets/sub/b.bin"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
            Assert.Equal("first", new StreamReader(zip.GetEntry("Assets/a.txt")!.Open()).ReadToEnd());
            using var inner = new MemoryStream();
            zip.GetEntry("Assets/sub/b.bin")!.Open().CopyTo(inner);
            Assert.Equal(File.ReadAllBytes(b.Path), inner.ToArray());
        }

        using (var response = await _http.GetAsync(At(share, "all")))
        {
            Assert.Equal("2 files.zip", response.Content.Headers.ContentDisposition?.FileNameStar);
            using var zip = new ZipArchive(new MemoryStream(await response.Content.ReadAsByteArrayAsync()));
            Assert.Equal(["Assets/", "Assets/a.txt", "Assets/sub/", "Assets/sub/b.bin", "note.txt"], zip.Entries.Select(e => e.FullName).Order(StringComparer.Ordinal));
            Assert.Equal("a note", new StreamReader(zip.GetEntry("note.txt")!.Open()).ReadToEnd());
        }

        // Each zip is one download, named for the folder or the count.
        await WaitFor(() => Seen(DropActivityKind.Sent).Count == 2);
        Assert.Equal(["Assets.zip", "2 files.zip"], Seen(DropActivityKind.Sent).Select(a => a.Name));
        Assert.Equal(["Assets.zip", "2 files.zip"], Seen(DropActivityKind.Sending).Select(a => a.Name));
    }

    [Fact]
    public async Task UploadsLandInsideTheReceiveFolderUnderSafeUniqueNames()
    {
        var share = Share([]);
        string[] sent = ["../../x", "..\\x", "C:\\x", "CON", "", "photo.jpg", "photo.jpg", "invoice\u202Egpj.exe", new string('a', 300) + ".txt", "/", "..", "a:b.txt"];
        var saved = new List<string?>();
        foreach (string name in sent)
        {
            using var response = await _http.PutAsync(At(share, "upload?name=" + Uri.EscapeDataString(name)), new ByteArrayContent(Encoding.UTF8.GetBytes("from " + name)));
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
            saved.Add(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("name").GetString());
        }

        Assert.Equal(["x", "x (2)", "x (3)", "_CON", "Item", "photo.jpg", "photo (2).jpg", "invoicegpj.exe", new string('a', 116) + ".txt", "Item (2)", "Item (3)", "a_b.txt"], saved);
        var received = Seen(DropActivityKind.Received);
        Assert.Equal(saved, received.Select(r => r.Name));
        Assert.All(received, r => Assert.Equal(Received, Path.GetDirectoryName(r.Path)));
        Assert.Equal(sent.Length, Seen(DropActivityKind.Receiving).Count);

        // Everything is in the folder, whole, and nothing anywhere above it; no partial file is left.
        Assert.Equal(saved.Order(StringComparer.OrdinalIgnoreCase), Directory.GetFiles(Received).Select(Path.GetFileName).Order(StringComparer.OrdinalIgnoreCase));
        Assert.Empty(Directory.GetFiles(_dir));
        Assert.Empty(Directory.GetFiles(Shared, "*", SearchOption.AllDirectories));
        Assert.Equal("from ../../x", File.ReadAllText(Path.Combine(Received, "x")));
        Assert.Equal("from invoice\u202Egpj.exe", File.ReadAllText(Path.Combine(Received, "invoicegpj.exe")));
        // Marked as from the internet, as a browser marks a download.
        Assert.Contains("ZoneId=3", File.ReadAllText(Path.Combine(Received, "photo.jpg") + ":Zone.Identifier"));
    }

    [Fact]
    public async Task AnUploadNeedsItsLengthAndRoomOnTheDrive()
    {
        var share = Share([]);
        string path = share.Link.AbsolutePath;

        string chunked = await Raw(share, $"PUT {path}upload?name=a.bin HTTP/1.1", "Transfer-Encoding: chunked");
        Assert.StartsWith("HTTP/1.1 411 ", chunked);
        Assert.StartsWith("HTTP/1.1 411 ", await Raw(share, $"PUT {path}upload?name=a.bin HTTP/1.1"));

        string huge = await Raw(share, $"PUT {path}upload?name=huge.bin HTTP/1.1", $"Content-Length: {1L << 55}");
        Assert.StartsWith("HTTP/1.1 507 ", huge);
        Assert.Contains("Connection: close", huge);
        var failed = Assert.Single(Seen(DropActivityKind.Failed));
        Assert.Equal(("huge.bin", "There is not enough free space on this PC."), (failed.Name, failed.Text));
        Assert.Empty(Seen(DropActivityKind.Receiving));
        Assert.Empty(Directory.Exists(Received) ? Directory.GetFiles(Received) : []);

        // A head that is not HTTP, or too large, is closed without an answer.
        Assert.Equal("", await Raw(share, "HELLO"));
        Assert.Equal("", await Raw(share, $"GET {path} HTTP/1.1", "X-Fill: " + new string('a', 17_000)));
    }

    [Fact]
    public async Task TextFromThePageRaisesTextAndOnlyThePageMaySendIt()
    {
        var share = Share([]);

        using (var response = await _http.PostAsync(At(share, "text"), new StringContent("Hello from the phone ✓\nhttps://example.com")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await WaitFor(() => Seen(DropActivityKind.Text).Count == 1);
        Assert.Equal("Hello from the phone ✓\nhttps://example.com", Seen(DropActivityKind.Text)[0].Text);
        Assert.Equal("A device", Seen(DropActivityKind.Text)[0].Device);

        using (var tooLong = await _http.PostAsync(At(share, "text"), new StringContent(new string('a', (256 << 10) + 1))))
        {
            Assert.Equal(HttpStatusCode.RequestEntityTooLarge, tooLong.StatusCode);
        }

        using (var blank = await _http.PostAsync(At(share, "text"), new StringContent(" \n ")))
        {
            Assert.Equal(HttpStatusCode.BadRequest, blank.StatusCode);
        }

        // A page on another site can make a phone's browser post here; its Origin gives it away.
        using var forged = new HttpRequestMessage(HttpMethod.Post, At(share, "text")) { Content = new StringContent("pwned") };
        forged.Headers.TryAddWithoutValidation("Origin", "http://evil.example");
        using (var response = await _http.SendAsync(forged))
        {
            Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        }

        using var own = new HttpRequestMessage(HttpMethod.Post, At(share, "text")) { Content = new StringContent("mine") };
        own.Headers.TryAddWithoutValidation("Origin", $"http://{share.Link.Authority}");
        using (var response = await _http.SendAsync(own))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        await WaitFor(() => Seen(DropActivityKind.Text).Count == 2);
        Assert.Equal("mine", Seen(DropActivityKind.Text)[1].Text);
    }

    [Fact]
    public async Task AfterDisposeNothingAnswersAndAPartialUploadIsGone()
    {
        var share = Share([Put("report.pdf", Bytes(10))]);
        using (var response = await _http.GetAsync(At(share, "list")))
        {
            Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        }

        // An upload under way: its head and the first megabyte of ten.
        using var client = new TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, share.Link.Port);
        var stream = client.GetStream();
        await stream.WriteAsync(Encoding.ASCII.GetBytes($"PUT {share.Link.AbsolutePath}upload?name=big.bin HTTP/1.1\r\nHost: {share.Link.Authority}\r\nContent-Length: {10 << 20}\r\n\r\n"));
        await stream.WriteAsync(new byte[1 << 20]);
        await WaitFor(() => Directory.Exists(Received) && Directory.GetFiles(Received).Length == 1);
        Assert.EndsWith(".partial", Directory.GetFiles(Received)[0]);

        share.Dispose();
        share.Dispose();

        await WaitFor(() => Directory.GetFiles(Received).Length == 0);
        // The open connection is closed, and nothing listens on the port any more.
        try
        {
            Assert.Equal(0, await stream.ReadAsync(new byte[1]).AsTask().WaitAsync(TimeSpan.FromSeconds(5)));
        }
        catch (IOException)
        {
            // Reset rather than closed: as good.
        }

        AssertNobodyListens(share);
        Assert.Empty(Seen(DropActivityKind.Received));
        Assert.Empty(Seen(DropActivityKind.Failed));
    }

    [Fact]
    public async Task AShareAskedNothingForItsIdleTimeStopsByItself()
    {
        var share = Share([Put("report.pdf", Bytes(10))], idle: TimeSpan.FromMilliseconds(1500));

        // A request puts the stop off: past the first idle time, it is still there.
        await Task.Delay(500);
        (await _http.GetAsync(At(share, "list"))).Dispose();
        await Task.Delay(1200);
        Assert.Empty(Seen(DropActivityKind.Stopped));

        await WaitFor(() => Seen(DropActivityKind.Stopped).Count == 1);
        Assert.Equal("", Seen(DropActivityKind.Stopped)[0].Device);
        // Stopped is raised just before the listener closes.
        await WaitFor(() =>
        {
            try
            {
                AssertNobodyListens(share);
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        });
    }

    // A port can be taken for exclusive use only when no one listens on it. (A refused connection takes Windows about
    // two seconds on loopback, so this asks the port instead.)
    private static void AssertNobodyListens(DropShare share)
    {
        using var listener = new TcpListener(IPAddress.Loopback, share.Link.Port) { ExclusiveAddressUse = true };
        listener.Start();
    }

    [Fact]
    public void StartSaysWhyWhenEveryPortIsTaken()
    {
        var taken = new List<TcpListener>();
        try
        {
            for (int port = DropShare.FirstPort; port <= DropShare.LastPort; port++)
            {
                var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
                try
                {
                    listener.Start();
                    taken.Add(listener);
                }
                catch (SocketException)
                {
                    // Something else has it already, which takes it just as well.
                    listener.Dispose();
                }
            }

            var share = DropShare.Start([], new DropOptions(Received, "PC", 0xFF6B4A) { Address = IPAddress.Loopback }, out string? problem);
            Assert.Null(share);
            Assert.Equal("Another app is using every port Tomte Drop can use.", problem);
        }
        finally
        {
            taken.ForEach(l => l.Dispose());
        }
    }

    [Fact]
    public void TheQrCodeReadsBackAsTheLink()
    {
        var share = Share([]);
        bool[,] modules = share.Qr;
        int size = modules.GetLength(0);
        Assert.Equal(size, modules.GetLength(1));
        Assert.Equal(0, (size - 17) % 4);
        // Finder patterns sit in three corners with no quiet zone around them.
        Assert.True(modules[0, 0] && modules[0, size - 1] && modules[size - 1, 0]);

        // Drawn as a scanner would see it: four pixels a module, inside a light border four modules wide.
        const int scale = 4, quiet = 4;
        int pixels = (size + 2 * quiet) * scale;
        var gray = new byte[pixels * pixels];
        for (int y = 0; y < pixels; y++)
        {
            for (int x = 0; x < pixels; x++)
            {
                int row = y / scale - quiet, column = x / scale - quiet;
                bool dark = row >= 0 && row < size && column >= 0 && column < size && modules[row, column];
                gray[y * pixels + x] = dark ? (byte)0 : (byte)255;
            }
        }

        var reader = new BarcodeReaderGeneric { Options = { PossibleFormats = [BarcodeFormat.QR_CODE] } };
        var result = reader.Decode(new RGBLuminanceSource(gray, pixels, pixels, RGBLuminanceSource.BitmapFormat.Gray8));
        Assert.Equal(share.Link.AbsoluteUri, result?.Text);

        // The matrix handed out is a copy: drawing over it changes nothing.
        modules[0, 0] = false;
        Assert.True(share.Qr[0, 0]);
    }

    [Fact]
    public void TheDefaultReceiveFolderIsTomteDropInDownloads()
    {
        string folder = DropOptions.DefaultReceiveFolder;
        Assert.Equal("Tomte Drop", Path.GetFileName(folder));
        Assert.True(Path.IsPathFullyQualified(folder));
        Assert.True(Directory.Exists(Path.GetDirectoryName(folder)), folder);
    }
}
