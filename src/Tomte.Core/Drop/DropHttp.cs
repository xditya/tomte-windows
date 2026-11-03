using System.Globalization;
using System.Text;

namespace Tomte.Core.Drop;

/// <summary>
/// One request read off a connection: the head, parsed strictly, and its body read on demand. Everything here came from
/// another device, so nothing is trusted: a head is at most <see cref="MaxHead"/> bytes and has to arrive whole within
/// <see cref="HeadMs"/>, and anything that is not a plain HTTP/1.x request with a path for its target is refused.
/// </summary>
internal sealed class DropRequest
{
    public const int MaxHead = 16 << 10;
    public const int HeadMs = 10_000;

    /// <summary>A body or a response that moves no byte for this long ends the transfer.</summary>
    public const int StallMs = 60_000;

    private readonly Stream net;
    private readonly byte[] early;
    private int earlyAt;
    private readonly int earlyEnd;

    private DropRequest(Stream net, byte[] early, int earlyAt, int earlyEnd)
    {
        this.net = net;
        this.early = early;
        this.earlyAt = earlyAt;
        this.earlyEnd = earlyEnd;
    }

    public string Method { get; private init; } = "";

    /// <summary>The path's segments, each percent-decoded on its own, so an encoded slash never makes a segment.</summary>
    public IReadOnlyList<string> Segments { get; private init; } = [];

    public IReadOnlyDictionary<string, string> Query { get; private init; } = new Dictionary<string, string>();

    /// <summary>The body's length, or null when the request did not say.</summary>
    public long? ContentLength { get; private init; }

    /// <summary>The body is sent in chunks: refused wherever a body is taken, since every body needs its length up front.</summary>
    public bool Chunked { get; private init; }

    /// <summary>What is left of the body to read.</summary>
    public long Remaining { get; private set; }

    private Dictionary<string, string> Headers { get; init; } = new(StringComparer.OrdinalIgnoreCase);

    public string? Header(string name) => Headers.GetValueOrDefault(name);

    /// <summary>The request's head, or null when it is malformed, too large, too slow, or the connection closed first.</summary>
    public static DropRequest? Read(Stream net)
    {
        var buffer = new byte[MaxHead];
        int length = 0, stop;
        long deadline = Environment.TickCount64 + HeadMs;
        while ((stop = buffer.AsSpan(0, length).IndexOf("\r\n\r\n"u8)) < 0)
        {
            long left = deadline - Environment.TickCount64;
            if (length == buffer.Length || left <= 0)
            {
                return null;
            }

            net.ReadTimeout = (int)left;
            int got = net.Read(buffer, length, buffer.Length - length);
            if (got == 0)
            {
                return null;
            }

            length += got;
        }

        string[] lines = Encoding.Latin1.GetString(buffer, 0, stop).Split("\r\n");
        string[] first = lines[0].Split(' ');
        if (first.Length != 3 || first[0].Length == 0 || !first[0].All(char.IsAsciiLetterUpper)
            || first[2] is not ("HTTP/1.1" or "HTTP/1.0") || !first[1].StartsWith('/'))
        {
            return null;
        }

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string line in lines.AsSpan(1))
        {
            int colon = line.IndexOf(':');
            if (colon <= 0 || line.AsSpan(0, colon).ContainsAnyExceptInRange('!', '~'))
            {
                // No name, a space before the colon, or a folded line: none of these come from a browser.
                return null;
            }

            string name = line[..colon], value = line[(colon + 1)..].Trim(' ', '\t');
            if (!headers.TryAdd(name, value))
            {
                if (name.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) || name.Equals("Host", StringComparison.OrdinalIgnoreCase)
                    || name.Equals("Transfer-Encoding", StringComparison.OrdinalIgnoreCase))
                {
                    return null;
                }

                headers[name] += ", " + value;
            }
        }

        long? contentLength = null;
        if (headers.TryGetValue("Content-Length", out string? declared))
        {
            if (declared.Length is 0 or > 18 || !long.TryParse(declared, NumberStyles.None, CultureInfo.InvariantCulture, out long parsed))
            {
                return null;
            }

            contentLength = parsed;
        }

        string target = first[1];
        int mark = target.IndexOf('?');
        string path = mark < 0 ? target : target[..mark];
        var query = new Dictionary<string, string>();
        if (mark >= 0)
        {
            foreach (string pair in target[(mark + 1)..].Split('&'))
            {
                int equals = pair.IndexOf('=');
                query.TryAdd(Decode(equals < 0 ? pair : pair[..equals]), equals < 0 ? "" : Decode(pair[(equals + 1)..]));
            }
        }

        return new DropRequest(net, buffer, stop + 4, length)
        {
            Method = first[0],
            Segments = [.. path[1..].Split('/').Select(Uri.UnescapeDataString)],
            Query = query,
            Headers = headers,
            ContentLength = contentLength,
            Chunked = headers.ContainsKey("Transfer-Encoding"),
            Remaining = contentLength ?? 0,
        };

        static string Decode(string part) => Uri.UnescapeDataString(part.Replace('+', ' '));
    }

    /// <summary>
    /// Reads the body into <paramref name="into"/>: what came with the head first, then the connection. 0 at the end of
    /// the body; throws when the connection closes or stalls before it.
    /// </summary>
    public int ReadBody(Span<byte> into)
    {
        if (Remaining == 0 || into.IsEmpty)
        {
            return 0;
        }

        int want = (int)Math.Min(into.Length, Remaining), got;
        if (earlyAt < earlyEnd)
        {
            got = Math.Min(want, earlyEnd - earlyAt);
            early.AsSpan(earlyAt, got).CopyTo(into);
            earlyAt += got;
        }
        else
        {
            net.ReadTimeout = StallMs;
            got = net.Read(into[..want]);
            if (got == 0)
            {
                throw new IOException("The connection closed before the whole body came.");
            }
        }

        Remaining -= got;
        return got;
    }
}

/// <summary>Writing a response's head. Every response closes its connection, and none is ever cached or sniffed.</summary>
internal static class DropResponse
{
    /// <summary>The whole answer to a request without the share's token: nothing that says what is listening.</summary>
    public static readonly byte[] NotFound = "HTTP/1.1 404 Not Found\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"u8.ToArray();

    // By the file's extension; anything else is a plain download.
    private static readonly Dictionary<string, string> Types = new(StringComparer.OrdinalIgnoreCase)
    {
        [".pdf"] = "application/pdf",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".png"] = "image/png",
        [".gif"] = "image/gif",
        [".webp"] = "image/webp",
        [".avif"] = "image/avif",
        [".heic"] = "image/heic",
        [".heif"] = "image/heif",
        [".bmp"] = "image/bmp",
        [".mp4"] = "video/mp4",
        [".m4v"] = "video/mp4",
        [".mov"] = "video/quicktime",
        [".webm"] = "video/webm",
        [".mp3"] = "audio/mpeg",
        [".m4a"] = "audio/mp4",
        [".wav"] = "audio/wav",
        [".txt"] = "text/plain; charset=utf-8",
        [".md"] = "text/markdown; charset=utf-8",
        [".csv"] = "text/csv; charset=utf-8",
        [".json"] = "application/json",
        [".zip"] = "application/zip",
        [".docx"] = "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
        [".xlsx"] = "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
        [".pptx"] = "application/vnd.openxmlformats-officedocument.presentationml.presentation",
    };

    public static string TypeOf(string path) => Types.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");

    /// <summary>
    /// Content-Disposition for a file: <paramref name="name"/> as UTF-8 for browsers that read filename*, and with every
    /// character outside printable ASCII made "_" for those that do not.
    /// </summary>
    public static string Disposition(string name, bool inline)
    {
        string plain = new([.. name.Select(c => c is >= ' ' and < '\x7F' and not '"' and not '\\' ? c : '_')]);
        return $"{(inline ? "inline" : "attachment")}; filename=\"{plain}\"; filename*=UTF-8''{Uri.EscapeDataString(name)}";
    }

    /// <summary>
    /// Writes the head. <paramref name="length"/> null leaves the body to run until the connection closes. Each of
    /// <paramref name="extra"/> is one "Name: value" line; values come from the share, never from the request.
    /// </summary>
    public static void Head(Stream net, int status, string? type, long? length, params string[] extra)
    {
        var head = new StringBuilder(256);
        head.Append(CultureInfo.InvariantCulture, $"HTTP/1.1 {status} {Reason(status)}\r\n");
        if (type is not null)
        {
            head.Append("Content-Type: ").Append(type).Append("\r\n");
        }

        if (length is { } bytes)
        {
            head.Append(CultureInfo.InvariantCulture, $"Content-Length: {bytes}\r\n");
        }

        head.Append("Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nReferrer-Policy: no-referrer\r\nConnection: close\r\n");
        foreach (string line in extra)
        {
            head.Append(line).Append("\r\n");
        }

        head.Append("\r\n");
        net.WriteTimeout = DropRequest.StallMs;
        net.Write(Encoding.ASCII.GetBytes(head.ToString()));
    }

    /// <summary>A short answer with a body of its own, such as a JSON reply or a plain error.</summary>
    public static void Send(Stream net, int status, string type, string body, params string[] extra)
    {
        byte[] bytes = Encoding.UTF8.GetBytes(body);
        Head(net, status, type, bytes.Length, extra);
        net.Write(bytes);
    }

    /// <summary>
    /// The one range a Range header asks of a file <paramref name="length"/> bytes long: null for the whole file (no
    /// header, one this does not read, or several ranges), and Unsatisfiable for one wholly past the end.
    /// </summary>
    public static (long From, long To)? Range(string? header, long length)
    {
        if (header is null || !header.StartsWith("bytes=", StringComparison.OrdinalIgnoreCase) || header.Contains(','))
        {
            return null;
        }

        string spec = header[6..].Trim();
        int dash = spec.IndexOf('-');
        if (dash < 0)
        {
            return null;
        }

        bool hasFrom = long.TryParse(spec[..dash], NumberStyles.None, CultureInfo.InvariantCulture, out long from);
        bool hasTo = long.TryParse(spec[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out long to);
        if (hasFrom && (dash + 1 == spec.Length || hasTo))
        {
            if (hasTo && to < from)
            {
                return null;
            }

            return from >= length ? Unsatisfiable : (from, hasTo ? Math.Min(to, length - 1) : length - 1);
        }

        if (dash == 0 && hasTo)
        {
            // The last `to` bytes.
            return to == 0 || length == 0 ? Unsatisfiable : (Math.Max(0, length - to), length - 1);
        }

        return null;
    }

    public static readonly (long From, long To) Unsatisfiable = (-1, -1);

    private static string Reason(int status) => status switch
    {
        200 => "OK",
        206 => "Partial Content",
        301 => "Moved Permanently",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        411 => "Length Required",
        413 => "Content Too Large",
        416 => "Range Not Satisfiable",
        500 => "Internal Server Error",
        507 => "Insufficient Storage",
        _ => "Unknown",
    };
}
