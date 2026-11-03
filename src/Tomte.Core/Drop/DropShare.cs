using System.Buffers.Text;
using System.Collections.Concurrent;
using System.Globalization;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Shell;
using ZXing.QrCode.Internal;
using QrEncoder = ZXing.QrCode.Internal.Encoder;

namespace Tomte.Core.Drop;

/// <summary>A file or folder a Tomte Drop offers: the name the phone sees, and where it is on this PC.</summary>
public sealed record DropFile(string Name, string Path);

public enum DropActivityKind
{
    /// <summary>A device opened the page for the first time in this share.</summary>
    Opened,

    /// <summary>A download started: <see cref="DropActivity.Name"/>.</summary>
    Sending,

    /// <summary>A download finished whole: <see cref="DropActivity.Name"/>.</summary>
    Sent,

    /// <summary>An upload started: <see cref="DropActivity.Name"/>.</summary>
    Receiving,

    /// <summary>An upload was saved whole: <see cref="DropActivity.Name"/> at <see cref="DropActivity.Path"/>.</summary>
    Received,

    /// <summary>Text or a link was sent from the page: <see cref="DropActivity.Text"/>.</summary>
    Text,

    /// <summary>A transfer broke off or was refused: <see cref="DropActivity.Name"/>, and <see cref="DropActivity.Text"/> says why.</summary>
    Failed,

    /// <summary>The share ended by itself: nothing was asked of it for <see cref="DropOptions.Idle"/>. Its device is empty.</summary>
    Stopped,
}

/// <param name="Device">What the page's browser runs on, as the card names it: "iPhone", "Android phone", "Mac", or "A device".</param>
public sealed record DropActivity(DropActivityKind Kind, string Device, string? Name = null, string? Path = null, string? Text = null);

/// <param name="ReceiveFolder">Where files sent from the page are saved; made when the first one comes.</param>
/// <param name="PcName">What the page calls this PC.</param>
/// <param name="Accent">The island's accent, 0xRRGGBB, which the page takes as its own.</param>
public sealed record DropOptions(string ReceiveFolder, string PcName, uint Accent)
{
    /// <summary>How long a share waits with nothing asked of it, and no transfer running, before it stops by itself.</summary>
    public TimeSpan Idle { get; init; } = DefaultIdle;

    /// <summary>How long a share waits with nothing asked of it before it stops; <see cref="Timeout.InfiniteTimeSpan"/> never stops it.</summary>
    public static readonly TimeSpan DefaultIdle = TimeSpan.FromMinutes(15);

    /// <summary>The address to listen on; null picks this PC's address on the local network. Tests give loopback.</summary>
    public IPAddress? Address { get; init; }

    /// <summary>Downloads\Tomte Drop, wherever the user keeps Downloads.</summary>
    public static string DefaultReceiveFolder => Path.Combine(Downloads(), "Tomte Drop");

    // The Downloads known folder, which follows it when the user has moved it; the usual place when Windows cannot say.
    private static unsafe string Downloads()
    {
        HRESULT result = PInvoke.SHGetKnownFolderPath(PInvoke.FOLDERID_Downloads, KNOWN_FOLDER_FLAG.KF_FLAG_DEFAULT, null, out PWSTR path);
        try
        {
            if (result.Succeeded && path.Value is not null)
            {
                return path.ToString();
            }
        }
        finally
        {
            // Freed whether the call succeeded or not.
            PInvoke.CoTaskMemFree(path);
        }

        return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), "Downloads");
    }
}

/// <summary>
/// One Tomte Drop: a small web server on this PC's local network address that a phone reaches through the QR code of
/// <see cref="Link"/>. Its page lists <see cref="Files"/> to download and takes files and text sent back. Every request
/// must carry the share's random token in its path; anything else gets a bare 404. It listens from <see cref="Start"/>
/// until <see cref="Dispose"/> or <see cref="DropOptions.Idle"/>, and never otherwise.
/// One request per connection, each on a thread of its own, at most <see cref="MaxConnections"/> at once; more wait in
/// the listen queue. Files are offered only by their place in <see cref="Files"/>, never by a path a request names, and
/// a received file's name keeps only its last part, so nothing outside the receive folder is ever read or written.
/// </summary>
public sealed class DropShare : IDisposable
{
    /// <summary>The ports a share tries in turn; the Store package's firewall rule opens these on private networks.</summary>
    public const int FirstPort = 53320, LastPort = 53329;

    private const int MaxConnections = 16;

    // Text sent from the page, and the text of a shared .txt or .md the page shows (a larger one is only a download).
    private const int MaxText = 256 << 10, MaxShownText = 64 << 10;

    // What an upload must leave free on the receiving drive.
    private const long FreeMargin = 256L << 20;

    private const int LingerMs = 2000;
    private const string PlainType = "text/plain; charset=utf-8", JsonType = "application/json; charset=utf-8";

    // The page runs only its own inline script and style and talks only to this share; nothing it shows can reach anywhere else.
    private const string PageCsp = "Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; img-src 'self' data:; connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    // A shared file opened in the browser runs no script, whatever it is.
    private const string FileCsp = "Content-Security-Policy: sandbox; default-src 'none'; img-src 'self'; media-src 'self'; style-src 'unsafe-inline'";

    // The island's surfaces and text that the page's accent has to read against, from Theme's dark and light sets.
    private const uint DarkIsland = 0xFF111214, DarkInk = 0xFFF2F0EB, LightIsland = 0xFFF3F2EF, White = 0xFFFFFFFF, Black = 0xFF000000;

    private static readonly Lazy<string> Template = new(() =>
    {
        using var stream = typeof(DropShare).Assembly.GetManifestResourceStream("Tomte.Core.Drop.DropPage.html")!;
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    });

    private static readonly JsonSerializerOptions Json = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull };

    // Shown as pictures on the page. SVG is not one: it can carry script.
    private static readonly HashSet<string> Images = new([".jpg", ".jpeg", ".png", ".gif", ".webp", ".avif", ".heic", ".heif", ".bmp"], StringComparer.OrdinalIgnoreCase);

    private readonly DropFile[] files;
    private readonly TimeSpan idleAfter;
    private readonly string folder;
    private readonly TcpListener listener;
    private readonly byte[] token;
    private readonly string host, origin;
    private readonly byte[] page;
    private readonly Action<string>? log;
    private readonly Lazy<bool[,]> qr;
    private readonly Timer idle;
    private readonly CancellationTokenSource stopping = new();
    private readonly SemaphoreSlim slots = new(MaxConnections);
    private readonly ConcurrentDictionary<Socket, byte> sockets = new();
    private readonly ConcurrentDictionary<string, byte> partials = new(StringComparer.OrdinalIgnoreCase);
    private readonly Lock gate = new();
    private readonly Lock keeping = new();

    // Under gate.
    private readonly HashSet<string> devices = [];
    private Action<DropActivity>? activity;

    private long lastAsked = Environment.TickCount64;
    private int busy, disposed;

    private DropShare(IReadOnlyList<DropFile> files, DropOptions options, TcpListener listener, bool publicNetwork, Action<string>? log)
    {
        this.files = [.. files];
        this.listener = listener;
        this.log = log;
        idleAfter = options.Idle;
        PublicNetwork = publicNetwork;
        folder = Path.TrimEndingDirectorySeparator(Path.GetFullPath(options.ReceiveFolder));
        host = ((IPEndPoint)listener.LocalEndpoint).ToString();
        origin = "http://" + host;
        string secret = Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(16));
        token = Encoding.ASCII.GetBytes(secret);
        Link = new Uri($"{origin}/{secret}/");
        page = Page(options);
        qr = new(() => QrOf(Link.AbsoluteUri));
        idle = new Timer(_ => IdleCheck(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        idle.Change(idleAfter, Timeout.InfiniteTimeSpan);
        new Thread(AcceptLoop) { IsBackground = true, Name = "Tomte Drop" }.Start();
    }

    /// <summary>The page's address, token included: what the QR code holds.</summary>
    public Uri Link { get; }

    /// <summary>Windows has the network this address is on set to Public and its firewall has no rule that lets phones reach Tomte there.</summary>
    public bool PublicNetwork { get; }

    /// <summary>The QR code of <see cref="Link"/>: [row, column], true for a dark module, with no quiet zone.</summary>
    public bool[,] Qr => (bool[,])qr.Value.Clone();

    public IReadOnlyList<DropFile> Files => files;

    /// <summary>Raised on a background thread, in the order things happen on each connection; never after <see cref="Dispose"/>.</summary>
    public event Action<DropActivity>? Activity
    {
        add
        {
            lock (gate)
            {
                activity += value;
            }
        }

        remove
        {
            lock (gate)
            {
                activity -= value;
            }
        }
    }

    /// <summary>
    /// Starts listening, or null with <paramref name="problem"/> saying why in a sentence for the card: the PC is on no
    /// local network, or every port is taken. Reads the network's category: call it on a worker thread.
    /// </summary>
    public static DropShare? Start(IReadOnlyList<DropFile> files, DropOptions options, out string? problem, Action<string>? log = null)
    {
        var address = options.Address ?? DropNetwork.LanAddress();
        if (address is null)
        {
            problem = "This PC is not on a local network. Connect it to Wi-Fi or Ethernet and try again.";
            log?.Invoke("drop: no local network address");
            return null;
        }

        for (int port = FirstPort; port <= LastPort; port++)
        {
            // Exclusive, so no other program can bind the same port alongside and take the requests.
            var listener = new TcpListener(address, port) { ExclusiveAddressUse = true };
            try
            {
                listener.Start(32);
            }
            catch (SocketException)
            {
                listener.Dispose();
                continue;
            }

            problem = null;
            // On a Public network the firewall keeps phones out unless a rule for Tomte lets them in.
            bool blocked = DropNetwork.IsPublic(address) && !DropNetwork.PublicLetsIn(Environment.ProcessPath);
            var share = new DropShare(files, options, listener, blocked, log);
            log?.Invoke($"drop: sharing {files.Count} items on port {port}{(share.PublicNetwork ? " of a public network the firewall blocks" : "")}");
            return share;
        }

        problem = "Another app is using every port Tomte Drop can use.";
        log?.Invoke("drop: no free port");
        return null;
    }

    /// <summary>Stops listening and ends every transfer; the link stops working. Safe to call twice.</summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) != 0)
        {
            return;
        }

        stopping.Cancel();
        listener.Stop();
        idle.Dispose();
        foreach (var socket in sockets.Keys)
        {
            socket.Dispose();
        }

        // An upload's own cleanup deletes its partial file as its connection breaks; this catches one still open.
        foreach (string partial in partials.Keys)
        {
            TryDelete(partial);
        }

        log?.Invoke("drop: stopped sharing");
    }

    /// <summary>What a User-Agent runs on, as the card names it.</summary>
    internal static string DeviceName(string? agent) => agent switch
    {
        null => "A device",
        _ when agent.Contains("iPhone", StringComparison.Ordinal) || agent.Contains("iPod", StringComparison.Ordinal) => "iPhone",
        _ when agent.Contains("iPad", StringComparison.Ordinal) => "iPad",
        _ when agent.Contains("Android", StringComparison.Ordinal) => agent.Contains("Mobile", StringComparison.Ordinal) ? "Android phone" : "Android tablet",
        // Safari on an iPad asks for desktop pages by default and says it is a Mac; only script on the page could tell.
        _ when agent.Contains("Macintosh", StringComparison.Ordinal) => "Mac",
        _ when agent.Contains("Windows", StringComparison.Ordinal) => "Windows PC",
        _ when agent.Contains("Linux", StringComparison.Ordinal) => "Linux PC",
        _ => "A device",
    };

    /// <summary>
    /// The name a received file is saved under: the last part of what the page sent, after either kind of slash, without
    /// the characters that reorder text (they can dress "gpj.exe" up as "exe.jpg"), made a safe file name.
    /// </summary>
    internal static string ReceiveName(string? sent)
    {
        string name = (sent ?? "").Replace('\\', '/');
        name = name[(name.LastIndexOf('/') + 1)..];
        return ShelfStore.SafeName(new string([.. name.Where(c => c is not ('؜' or '‎' or '‏' or (>= '‪' and <= '‮') or (>= '⁦' and <= '⁩')))]));
    }

    private void AcceptLoop()
    {
        try
        {
            while (true)
            {
                slots.Wait(stopping.Token);
                Socket socket;
                try
                {
                    socket = listener.AcceptSocket();
                }
                catch
                {
                    slots.Release();
                    throw;
                }

                sockets.TryAdd(socket, 0);
                if (Volatile.Read(ref disposed) != 0)
                {
                    // Dispose ran between the accept and the line above and did not see this one.
                    sockets.TryRemove(socket, out _);
                    socket.Dispose();
                    return;
                }

                new Thread(() => Serve(socket)) { IsBackground = true, Name = "Tomte Drop request" }.Start();
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException or OperationCanceledException or InvalidOperationException)
        {
            // Dispose stopped the listener.
        }
    }

    private void Serve(Socket socket)
    {
        try
        {
            using var net = new NetworkStream(socket, ownsSocket: false);
            var request = DropRequest.Read(net);
            if (request is null)
            {
                return;
            }

            if (!Mine(request))
            {
                net.Write(DropResponse.NotFound);
                return;
            }

            Interlocked.Increment(ref busy);
            try
            {
                Route(request, net, socket);
            }
            finally
            {
                Interlocked.Exchange(ref lastAsked, Environment.TickCount64);
                Interlocked.Decrement(ref busy);
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            // The device went away, stalled, or the share stopped.
        }
        catch (Exception e)
        {
            log?.Invoke($"drop: a request failed: {e.GetType().Name}: {e.Message}");
        }
        finally
        {
            Linger(socket);
            sockets.TryRemove(socket, out _);
            slots.Release();
        }
    }

    /// <summary>
    /// The request carries the token, compared in constant time, and names this share's address as its host, so a web
    /// page elsewhere that points a name of its own at this address gets nothing.
    /// </summary>
    private bool Mine(DropRequest request) =>
        request.Segments.Count > 0
        && CryptographicOperations.FixedTimeEquals(Encoding.ASCII.GetBytes(request.Segments[0]), token)
        && string.Equals(request.Header("Host"), host, StringComparison.OrdinalIgnoreCase);

    private void Route(DropRequest request, NetworkStream net, Socket socket)
    {
        bool get = request.Method == "GET", send = request.Method is "PUT" or "POST";
        string device = DeviceName(request.Header("User-Agent"));
        if (send && request.Header("Origin") is { } from && !string.Equals(from, origin, StringComparison.OrdinalIgnoreCase))
        {
            // A browser names the page a POST comes from: one on another site cannot send files or text here.
            DropResponse.Send(net, 403, PlainType, "Send from the Tomte Drop page.");
            return;
        }

        switch (request.Segments.Skip(1).ToArray())
        {
            case [] when get:
                DropResponse.Head(net, 301, null, 0, "Location: " + Link.AbsolutePath);
                break;
            case [""] when get:
                ServePage(request, net, socket, device);
                break;
            case ["list"] when get:
                DropResponse.Send(net, 200, JsonType, JsonSerializer.Serialize(files.Select(Describe).ToList(), Json));
                break;
            case ["file", var id] when get && int.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out int index) && index < files.Length:
                Download(request, net, device, files[index], request.Query.GetValueOrDefault("inline") == "1");
                break;
            case ["all"] when get && files.Where(f => Path.Exists(f.Path)).ToArray() is { Length: > 0 } present:
                Zip(net, device, present, present.Length == 1 ? ZipName(present[0]) : $"{present.Length} files.zip");
                break;
            case ["upload"] when send:
                Upload(request, net, device);
                break;
            case ["text"] when send:
                Text(request, net, device);
                break;
            default:
                net.Write(DropResponse.NotFound);
                break;
        }
    }

    private void ServePage(DropRequest request, NetworkStream net, Socket socket, string device)
    {
        string address = (socket.RemoteEndPoint as IPEndPoint)?.Address.ToString() ?? "";
        bool first;
        lock (gate)
        {
            first = devices.Add(address + " " + request.Header("User-Agent"));
        }

        DropResponse.Head(net, 200, "text/html; charset=utf-8", page.Length, PageCsp);
        net.Write(page);
        if (first)
        {
            Raise(new DropActivity(DropActivityKind.Opened, device));
        }
    }

    /// <summary>A file by its place in the list. Folders go as a zip; an image may be asked for inline, as a preview, which raises nothing.</summary>
    private void Download(DropRequest request, NetworkStream net, string device, DropFile file, bool inline)
    {
        if (Directory.Exists(file.Path))
        {
            Zip(net, device, [file], ZipName(file));
            return;
        }

        FileStream source;
        try
        {
            source = new FileStream(file.Path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            net.Write(DropResponse.NotFound);
            return;
        }

        using (source)
        {
            long length = source.Length;
            inline &= Images.Contains(Path.GetExtension(file.Path));
            var written = File.GetLastWriteTimeUtc(file.Path);
            string modified = written.ToString("R", CultureInfo.InvariantCulture), tag = $"\"{written.Ticks:x}-{length:x}\"";

            // A resume names the copy it has; a range of a file that changed since would splice two files together.
            string? ifRange = request.Header("If-Range");
            var range = ifRange is not null && ifRange != tag && ifRange != modified ? null : DropResponse.Range(request.Header("Range"), length);
            string[] headers = ["Accept-Ranges: bytes", "Last-Modified: " + modified, "ETag: " + tag, "Content-Disposition: " + DropResponse.Disposition(file.Name, inline), FileCsp];
            if (range == DropResponse.Unsatisfiable)
            {
                DropResponse.Head(net, 416, null, 0, [$"Content-Range: bytes */{length}", .. headers]);
                return;
            }

            var (from, to) = range ?? (0, length - 1);
            DropResponse.Head(net, range is null ? 200 : 206, DropResponse.TypeOf(file.Path), to - from + 1,
                range is null ? headers : [$"Content-Range: bytes {from}-{to}/{length}", .. headers]);
            if (inline)
            {
                source.Position = from;
                Copy(source, net, to - from + 1);
                return;
            }

            Raise(new DropActivity(DropActivityKind.Sending, device, file.Name));
            try
            {
                source.Position = from;
                Copy(source, net, to - from + 1);
            }
            catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
            {
                Fail(device, file.Name, "The download stopped before the end.");
                throw;
            }

            Raise(new DropActivity(DropActivityKind.Sent, device, file.Name));
        }
    }

    /// <summary>
    /// The items as one zip, written as it is read: no temporary file, and no length up front, so the body ends when the
    /// connection closes. A folder's links are left out, not followed, so a junction loop cannot run forever; a file that
    /// cannot be read now is left out too.
    /// </summary>
    private void Zip(NetworkStream net, string device, IReadOnlyList<DropFile> items, string name)
    {
        DropResponse.Head(net, 200, "application/zip", null, "Content-Disposition: " + DropResponse.Disposition(name, inline: false), FileCsp);
        Raise(new DropActivity(DropActivityKind.Sending, device, name));
        try
        {
            using var zip = new ZipArchive(net, ZipArchiveMode.Create, leaveOpen: true);
            var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in items)
            {
                string entry = ShelfStore.Unique(ShelfStore.SafeName(item.Name), taken);
                taken.Add(entry);
                if (Directory.Exists(item.Path))
                {
                    AddFolder(zip, item.Path, entry + "/");
                }
                else
                {
                    AddFile(zip, item.Path, entry);
                }
            }
        }
        catch (Exception e) when (e is IOException or SocketException or ObjectDisposedException)
        {
            Fail(device, name, "The download stopped before the end.");
            throw;
        }

        Raise(new DropActivity(DropActivityKind.Sent, device, name));

        static void AddFolder(ZipArchive zip, string folder, string prefix)
        {
            zip.CreateEntry(prefix);
            foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos("*", new EnumerationOptions { AttributesToSkip = 0 }))
            {
                if (entry.LinkTarget is not null)
                {
                    continue;
                }

                if (entry is DirectoryInfo)
                {
                    AddFolder(zip, entry.FullName, prefix + entry.Name + "/");
                }
                else
                {
                    AddFile(zip, entry.FullName, prefix + entry.Name);
                }
            }
        }

        static void AddFile(ZipArchive zip, string path, string name)
        {
            FileStream source;
            try
            {
                source = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                return;
            }

            using (source)
            {
                var entry = zip.CreateEntry(name, CompressionLevel.Fastest);
                var written = File.GetLastWriteTime(path);
                if (written.Year is >= 1980 and <= 2107)
                {
                    entry.LastWriteTime = written;
                }

                using var into = entry.Open();
                source.CopyTo(into);
            }
        }
    }

    /// <summary>
    /// A file sent from the page, its name in the query and the bytes as the body. It is written to a temporary name in
    /// the receive folder as it comes and takes its own name only once it is whole; the temporary file goes on any failure.
    /// </summary>
    private void Upload(DropRequest request, NetworkStream net, string device)
    {
        string name = ReceiveName(request.Query.GetValueOrDefault("name"));
        if (request.Chunked || request.ContentLength is not { } length)
        {
            DropResponse.Send(net, 411, PlainType, "Send the file with its length.");
            return;
        }

        try
        {
            Directory.CreateDirectory(folder);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"drop: could not make the receive folder: {e.Message}");
            Fail(device, name, "Tomte could not make the folder it saves files in.");
            DropResponse.Send(net, 500, PlainType, "The PC could not make the folder it saves files in.");
            return;
        }

        if (length > FreeSpace(folder) - FreeMargin)
        {
            Fail(device, name, "There is not enough free space on this PC.");
            DropResponse.Send(net, 507, PlainType, "There is not enough free space on this PC.");
            return;
        }

        Raise(new DropActivity(DropActivityKind.Receiving, device, name));
        string partial = Path.Combine(folder, $"~{Guid.NewGuid():N}.partial");
        partials.TryAdd(partial, 0);
        bool kept = false;
        try
        {
            // Delete sharing, so Dispose can take the file away while this still has it open.
            using (var file = new FileStream(partial, new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.Delete, PreallocationSize = length, BufferSize = 1 << 16 }))
            {
                var buffer = new byte[1 << 16];
                int got;
                while ((got = request.ReadBody(buffer)) > 0)
                {
                    file.Write(buffer, 0, got);
                }
            }

            if (Volatile.Read(ref disposed) != 0)
            {
                return;
            }

            string saved = Keep(partial, name);
            kept = true;
            MarkFromNetwork(saved);
            Raise(new DropActivity(DropActivityKind.Received, device, Path.GetFileName(saved), saved));
            DropResponse.Send(net, 200, JsonType, JsonSerializer.Serialize(new { name = Path.GetFileName(saved) }));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SocketException or ObjectDisposedException)
        {
            if (Volatile.Read(ref disposed) == 0)
            {
                log?.Invoke($"drop: an upload broke off: {e.Message}");
                Fail(device, name, "The file did not arrive whole.");
                // The device may still be listening, when it was the disk that failed.
                try
                {
                    DropResponse.Send(net, 500, PlainType, "The file did not arrive whole.");
                }
                catch (Exception again) when (again is IOException or SocketException or ObjectDisposedException)
                {
                }
            }
        }
        finally
        {
            if (!kept)
            {
                TryDelete(partial);
            }

            partials.TryRemove(partial, out _);
        }
    }

    private void Text(DropRequest request, NetworkStream net, string device)
    {
        if (request.Chunked || request.ContentLength is not { } length)
        {
            DropResponse.Send(net, 411, PlainType, "Send the text with its length.");
            return;
        }

        if (length > MaxText)
        {
            DropResponse.Send(net, 413, PlainType, "That text is too long.");
            return;
        }

        var bytes = new byte[length];
        for (int at = 0; at < bytes.Length;)
        {
            at += request.ReadBody(bytes.AsSpan(at));
        }

        string text = Encoding.UTF8.GetString(bytes);
        if (string.IsNullOrWhiteSpace(text))
        {
            DropResponse.Send(net, 400, PlainType, "There is no text.");
            return;
        }

        Raise(new DropActivity(DropActivityKind.Text, device, Text: text));
        DropResponse.Send(net, 200, JsonType, "{}");
    }

    /// <summary>
    /// Moves a whole upload to <paramref name="name"/>, or "name (2)" and so on when the folder has that name already.
    /// One at a time, so two uploads of one name never take the same one.
    /// </summary>
    private string Keep(string partial, string name)
    {
        lock (keeping)
        {
            for (int attempt = 0; ; attempt++)
            {
                var taken = Directory.EnumerateFileSystemEntries(folder).Select(Path.GetFileName).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);
                string saved = Path.Combine(folder, ShelfStore.Unique(name, taken));
                if (!string.Equals(Path.GetDirectoryName(Path.GetFullPath(saved)), folder, StringComparison.OrdinalIgnoreCase))
                {
                    throw new IOException("A received name would leave the receive folder.");
                }

                try
                {
                    File.Move(partial, saved);
                    return saved;
                }
                catch (IOException) when (attempt < 3 && Path.Exists(saved))
                {
                    // Another program took that name just now: the next one.
                }
            }
        }
    }

    private Item Describe(DropFile file, int id)
    {
        try
        {
            var info = new FileInfo(file.Path);
            if (info.Exists)
            {
                string extension = Path.GetExtension(file.Path).ToLowerInvariant();
                bool small = info.Length <= MaxShownText;
                return extension switch
                {
                    ".txt" or ".md" => new Item(id, file.Name, info.Length, "text", small ? ShelfStore.ReadTextFile(file.Path) : null),
                    ".url" when small && UrlOf(file.Path) is { } url => new Item(id, file.Name, info.Length, "link", Url: url),
                    _ when Images.Contains(extension) => new Item(id, file.Name, info.Length, "image"),
                    _ => new Item(id, file.Name, info.Length, "file"),
                };
            }

            if (Directory.Exists(file.Path))
            {
                return new Item(id, file.Name, null, "folder");
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
        }

        return new Item(id, file.Name, null, "missing");
    }

    /// <summary>An Internet shortcut's address, when it is an http or https one; the page puts it in a link.</summary>
    private static string? UrlOf(string path)
    {
        foreach (string line in File.ReadLines(path))
        {
            if (line.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
            {
                return Uri.TryCreate(line[4..].Trim(), UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" ? uri.AbsoluteUri : null;
            }
        }

        return null;
    }

    private static string ZipName(DropFile file) => ShelfStore.SafeName(file.Name) + ".zip";

    private void IdleCheck()
    {
        if (Volatile.Read(ref disposed) != 0)
        {
            return;
        }

        var left = idleAfter - TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref lastAsked));
        if (Volatile.Read(ref busy) > 0 || left > TimeSpan.Zero)
        {
            try
            {
                idle.Change(left > TimeSpan.Zero ? left : idleAfter, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
            }

            return;
        }

        log?.Invoke("drop: nothing asked for a while, stopping");
        Raise(new DropActivity(DropActivityKind.Stopped, ""));
        Dispose();
    }

    private void Raise(DropActivity what)
    {
        Action<DropActivity>? handlers;
        lock (gate)
        {
            handlers = activity;
        }

        if (Volatile.Read(ref disposed) == 0)
        {
            handlers?.Invoke(what);
        }
    }

    private void Fail(string device, string name, string why) => Raise(new DropActivity(DropActivityKind.Failed, device, name, Text: why));

    private static void Copy(Stream from, Stream to, long count)
    {
        var buffer = new byte[1 << 16];
        while (count > 0)
        {
            int got = from.Read(buffer, 0, (int)Math.Min(buffer.Length, count));
            if (got == 0)
            {
                throw new IOException("The file got shorter while it was sent.");
            }

            to.Write(buffer, 0, got);
            count -= got;
        }
    }

    /// <summary>
    /// Ends a connection so the device reads the whole answer: the sending side closes first, then whatever the device is
    /// still sending (the rest of a refused upload) is read and dropped for a moment. Closing with bytes unread resets
    /// the connection, and the device can lose the answer with it.
    /// </summary>
    private static void Linger(Socket socket)
    {
        try
        {
            socket.Shutdown(SocketShutdown.Send);
            socket.ReceiveTimeout = LingerMs;
            var buffer = new byte[16 << 10];
            long until = Environment.TickCount64 + LingerMs;
            while (Environment.TickCount64 < until && socket.Receive(buffer) > 0)
            {
            }
        }
        catch (Exception e) when (e is SocketException or ObjectDisposedException)
        {
        }
        finally
        {
            socket.Dispose();
        }
    }

    private static long FreeSpace(string folder) =>
        PInvoke.GetDiskFreeSpaceEx(folder, out ulong free, out _, out _) ? (long)Math.Min(free, long.MaxValue) : long.MaxValue;

    /// <summary>
    /// Marks a received file as from the internet, as a browser marks its downloads, so SmartScreen checks a program and
    /// Office opens a document in Protected View. A drive without alternate streams (FAT) keeps it unmarked.
    /// </summary>
    private static void MarkFromNetwork(string path)
    {
        try
        {
            File.WriteAllText(path + ":Zone.Identifier", "[ZoneTransfer]\r\nZoneId=3\r\n");
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException)
        {
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
        }
    }

    private static bool[,] QrOf(string text)
    {
        var matrix = QrEncoder.encode(text, ErrorCorrectionLevel.M).Matrix;
        var modules = new bool[matrix.Height, matrix.Width];
        for (int row = 0; row < matrix.Height; row++)
        {
            for (int column = 0; column < matrix.Width; column++)
            {
                modules[row, column] = matrix[column, row] == 1;
            }
        }

        return modules;
    }

    /// <summary>
    /// The page with this PC's name and accent in it. The accent reads as the island draws it: lifted until it stands at
    /// 3:1 on the dark island, deepened to 4.5:1 on the light one, with the ink that stands out more on it.
    /// </summary>
    private static byte[] Page(DropOptions options)
    {
        uint chosen = 0xFF000000 | (options.Accent & 0xFFFFFF);
        uint dark = Readable(chosen, White, DarkIsland, 3);
        uint light = chosen == Theme.DefaultAccent ? Theme.DeepCoral : Readable(chosen, Black, LightIsland, 4.5);
        uint onDark = Theme.Contrast(dark, Theme.DarkOnAccent) >= Theme.Contrast(dark, DarkInk) ? Theme.DarkOnAccent : DarkInk;
        uint onLight = Theme.Contrast(light, Theme.DarkOnAccent) >= Theme.Contrast(light, White) ? Theme.DarkOnAccent : White;
        string pc = string.IsNullOrWhiteSpace(options.PcName) ? "this PC" : options.PcName.Trim();
        string html = Template.Value
            .Replace("{{accent-dark}}", Hex(dark), StringComparison.Ordinal)
            .Replace("{{on-accent-dark}}", Hex(onDark), StringComparison.Ordinal)
            .Replace("{{accent-light}}", Hex(light), StringComparison.Ordinal)
            .Replace("{{on-accent-light}}", Hex(onLight), StringComparison.Ordinal)
            // Last, so nothing in the name is taken for a placeholder.
            .Replace("{{pc}}", WebUtility.HtmlEncode(pc), StringComparison.Ordinal);
        return Encoding.UTF8.GetBytes(html);

        static string Hex(uint argb) => "#" + (argb & 0xFFFFFF).ToString("X6", CultureInfo.InvariantCulture);
    }

    // Moves `argb` toward `target` in small steps until it stands at `ratio` against `against`, as Theme moves its accent.
    private static uint Readable(uint argb, uint target, uint against, double ratio)
    {
        uint c = argb;
        for (int i = 1; i <= 50 && Theme.Contrast(c, against) < ratio; i++)
        {
            double t = i / 50.0;
            uint Channel(int shift) => (uint)Math.Round((argb >> shift & 0xFF) + (((target >> shift & 0xFF) - (double)(argb >> shift & 0xFF)) * t));
            c = 0xFF000000 | Channel(16) << 16 | Channel(8) << 8 | Channel(0);
        }

        return c;
    }

    /// <summary>One row of the page's list. Size is null for a folder and a missing item.</summary>
    private sealed record Item(int Id, string Name, long? Size, string Kind, string? Text = null, string? Url = null);
}
