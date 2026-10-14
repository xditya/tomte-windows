using System.IO.Compression;
using System.Text;
using System.Xml;
using Windows.Win32;

namespace Tomte.AI.Files;

/// <summary>
/// How much of a file is read. Files are someone else's input: every reader stops at the character cap, refuses a file
/// over the byte cap, and runs off the island's thread under the time limit.
/// </summary>
public sealed record FileLimits
{
    /// <summary>Characters of text kept from one file, about 250,000 to 330,000 tokens.</summary>
    public int MaxChars { get; init; } = 1_000_000;

    /// <summary>A PDF, Word file or image larger than this is not opened. Plain text is read only up to the character cap.</summary>
    public long MaxBytes { get; init; } = 100L << 20;

    /// <summary>The unpacked size of a Word file's text part: past it the file is treated as a zip bomb.</summary>
    public long MaxDocxXmlBytes { get; init; } = 64L << 20;

    /// <summary>Parts in a Word file's zip. Real documents hold tens; a zip bomb can claim millions.</summary>
    public int MaxDocxEntries { get; init; } = 5_000;

    /// <summary>An image with more pixels than this is not decoded.</summary>
    public long MaxImagePixels { get; init; } = 200_000_000;

    public TimeSpan TimeLimit { get; init; } = TimeSpan.FromSeconds(20);

    /// <summary>The most memory the PDF and Word reader process may commit before it is stopped.</summary>
    public long ChildMemoryBytes { get; init; } = 256L << 20;

    /// <summary>The program that reads PDF and Word files in a process of its own; tests point it at themselves.</summary>
    public ExtractorCommand Extractor { get; init; } = ExtractorCommand.ThisProgram;
}

/// <summary>
/// Text read from a file. Cut is true when the reader stopped at the character cap; Pages counts a PDF's pages, PagesRead
/// how many were read, and Encrypted says the PDF needed its (empty) password to open.
/// </summary>
public sealed record Extracted(string Text, bool Cut, int Pages = 0, int PagesRead = 0, bool Encrypted = false);

/// <summary>A file Tomte could not read. The message completes a sentence that starts with the file's name, and never holds its path.</summary>
public sealed class FileReadException(string message) : Exception(message);

public enum FileKind { Text, Docx, Pdf, Image }

public static class TextExtraction
{
    private const string WordMain = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string WordStrict = "http://purl.oclc.org/ooxml/wordprocessingml/main";
    private const int MaxDepth = 256;
    private const int MaxAttributes = 256;

    /// <summary>What a file is, from its first bytes (and, for a zip, its extension). Anything else is tried as text.</summary>
    public static FileKind Sniff(ReadOnlySpan<byte> head, string name)
    {
        // A PDF may start with a little junk before its header.
        if (head.IndexOf("%PDF-"u8) is >= 0 and < 1024)
            return FileKind.Pdf;
        if (head.StartsWith("PK\x03\x04"u8) && Path.GetExtension(name).ToLowerInvariant() is ".docx" or ".docm" or ".dotx" or ".dotm")
            return FileKind.Docx;
        // Signatures a text file could begin with by chance (BM, GIF) are checked a little further.
        bool image = head.StartsWith((ReadOnlySpan<byte>)[0x89, 0x50, 0x4E, 0x47]) || head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xD8, 0xFF])
            || head.StartsWith("GIF87a"u8) || head.StartsWith("GIF89a"u8)
            || (head.Length >= 18 && head.StartsWith("BM"u8) && head[14] is 12 or 40 or 52 or 56 or 108 or 124 && head[15..18].IndexOfAnyExcept((byte)0) < 0)
            || head.StartsWith("II*\0"u8) || head.StartsWith("MM\0*"u8)
            || (head.Length >= 12 && head.StartsWith("RIFF"u8) && head[8..12].SequenceEqual("WEBP"u8))
            || (head.Length >= 12 && head[4..8].SequenceEqual("ftyp"u8) && IsImageBrand(head[8..12]));
        return image ? FileKind.Image : FileKind.Text;
    }

    /// <summary>
    /// Plain text and code: a byte order mark if there is one, else UTF-8 if the bytes are valid UTF-8, else the
    /// system code page. Line breaks are kept. A file with a NUL byte and no byte order mark is not text. Only the
    /// bytes the character cap can need are read, so a log of any size costs the same.
    /// </summary>
    public static Extracted ReadText(Stream file, FileLimits limits)
    {
        // Four bytes per character is the most any of the encodings below needs; one more tells whether the file goes on.
        var bytes = new byte[(int)Math.Min(file.CanSeek ? file.Length - file.Position : long.MaxValue, limits.MaxChars * 4L + 4)];
        int read = file.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false);
        bool more = file.ReadByte() >= 0;
        var span = bytes.AsSpan(0, read);

        var (encoding, preamble) = span switch
        {
            [0xEF, 0xBB, 0xBF, ..] => (new UTF8Encoding(false), 3),
            [0xFF, 0xFE, 0, 0, ..] => (new UTF32Encoding(false, false), 4),
            [0, 0, 0xFE, 0xFF, ..] => (new UTF32Encoding(true, false), 4),
            [0xFF, 0xFE, ..] => (new UnicodeEncoding(false, false), 2),
            [0xFE, 0xFF, ..] => (new UnicodeEncoding(true, false), 2),
            _ => ((Encoding?)null, 0),
        };
        if (encoding is null)
        {
            if (span.Contains((byte)0))
                throw new FileReadException("is not a text file. Tomte reads text, code, PDF, Word (.docx) and image files.");
            encoding = IsUtf8(span, atEnd: !more) ? new UTF8Encoding(false) : SystemCodePage();
        }

        // A decoder keeps a character cut in half at the end of the buffer for later, instead of turning it into junk.
        var decoder = encoding.GetDecoder();
        var chars = new char[encoding.GetMaxCharCount(read - preamble)];
        int count = decoder.GetChars(bytes, preamble, read - preamble, chars, 0, flush: !more);
        bool cut = more || count > limits.MaxChars;
        return new Extracted(Start(new string(chars, 0, count), limits.MaxChars), cut);
    }

    /// <summary>
    /// The text of a Word file: the runs of word/document.xml with tabs, line breaks and a line per paragraph.
    /// Headers, footers, footnotes and comments live in other parts and are not read. The zip is checked before it is
    /// opened (entry count and directory size from its end record) and the text part is unpacked through a counter,
    /// so a zip bomb stops at <see cref="FileLimits.MaxDocxXmlBytes"/>. DTDs are refused, so no entity expands.
    /// </summary>
    public static Extracted ReadDocx(Stream file, FileLimits limits, CancellationToken ct)
    {
        if (file.Length > limits.MaxBytes)
            throw new FileReadException($"is larger than the {limits.MaxBytes >> 20} MB Tomte opens.");
        var (entries, directoryBytes) = ZipDirectory(file) ?? throw new FileReadException("could not be read: it is damaged or not a Word document.");
        // A directory record is 46 bytes plus its name; a kilobyte each is generous for the short names Word uses.
        if (entries > limits.MaxDocxEntries || directoryBytes > limits.MaxDocxEntries * 1024L)
            throw new FileReadException($"holds more than {limits.MaxDocxEntries:N0} parts, which no Word document needs, so it was not opened.");

        file.Position = 0;
        try
        {
            using var zip = new ZipArchive(file, ZipArchiveMode.Read, leaveOpen: true);
            var entry = zip.GetEntry("word/document.xml") ?? throw new FileReadException("is not a Word document: it has no document text.");
            using var xml = new CappedStream(entry.Open(), limits.MaxDocxXmlBytes);
            using var reader = XmlReader.Create(xml, new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit,
                XmlResolver = null,
                IgnoreComments = true,
                IgnoreProcessingInstructions = true,
                // A bound in characters besides the byte counter, tied to the text cap: Word writes at most a few
                // dozen characters of markup for each character of text.
                MaxCharactersInDocument = limits.MaxChars * 32L,
            });

            var text = new StringBuilder();
            var chunk = new char[4096];
            int seen = 0, textDepth = -1;
            try
            {
                while (text.Length < limits.MaxChars && reader.Read())
                {
                    if ((++seen & 0xFFF) == 0)
                        ct.ThrowIfCancellationRequested();
                    // Word nests a few levels deep and puts a handful of attributes on an element; far more is no document.
                    if (reader.Depth > MaxDepth || reader.AttributeCount > MaxAttributes)
                        throw new FileReadException("could not be read: it is damaged or not a Word document.");
                    bool word = reader.NamespaceURI is WordMain or WordStrict;
                    switch (reader.NodeType)
                    {
                        case XmlNodeType.Element when word && reader.LocalName == "t":
                            textDepth = reader.IsEmptyElement ? -1 : reader.Depth;
                            break;
                        case XmlNodeType.Element when word:
                            text.Append(reader.LocalName switch { "tab" => "\t", "br" or "cr" => "\n", _ => "" });
                            break;
                        case XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace when textDepth >= 0:
                            // In pieces, so a run of any length costs no more than what is kept of it.
                            int n;
                            while (text.Length < limits.MaxChars && (n = reader.ReadValueChunk(chunk, 0, Math.Min(chunk.Length, limits.MaxChars - text.Length))) > 0)
                                text.Append(chunk, 0, n);
                            break;
                        case XmlNodeType.EndElement when word && reader.LocalName == "t":
                            textDepth = -1;
                            break;
                        case XmlNodeType.EndElement when word && reader.LocalName == "p":
                            text.Append('\n');
                            break;
                    }
                }
            }
            catch (XmlException) when (text.Length > 0)
            {
                // Past the character bound, or broken further on: what was read so far goes, marked as cut.
                return new Extracted(Start(text.ToString(), limits.MaxChars), Cut: true);
            }

            bool cut = text.Length >= limits.MaxChars;
            return new Extracted(cut ? Start(text.ToString(), limits.MaxChars) : text.ToString().TrimEnd('\n'), cut);
        }
        catch (CappedStream.TooLargeException)
        {
            throw new FileReadException($"unpacks to more than {limits.MaxDocxXmlBytes >> 20} MB of text markup, which can mean a damaged or hostile file, so it was not read.");
        }
        catch (Exception e) when (e is InvalidDataException or XmlException or NotSupportedException)
        {
            throw new FileReadException("could not be read: it is damaged or not a Word document.");
        }
    }

    /// <summary>The first <paramref name="length"/> characters, one fewer where the cut would split a surrogate pair.</summary>
    internal static string Start(string text, int length) =>
        length >= text.Length ? text : text[..(length > 0 && char.IsHighSurrogate(text[length - 1]) ? length - 1 : length)];

    // HEIF and AVIF photos; other ftyp files (MP4 video) are not images.
    private static bool IsImageBrand(ReadOnlySpan<byte> brand) =>
        brand.SequenceEqual("heic"u8) || brand.SequenceEqual("heix"u8) || brand.SequenceEqual("mif1"u8) || brand.SequenceEqual("avif"u8);

    private static bool IsUtf8(ReadOnlySpan<byte> bytes, bool atEnd)
    {
        try
        {
            // flush false leaves a sequence cut off by the buffer's end alone; anything invalid before it throws.
            new UTF8Encoding(false, throwOnInvalidBytes: true).GetDecoder().GetCharCount(bytes, flush: atEnd);
            return true;
        }
        catch (DecoderFallbackException)
        {
            return false;
        }
    }

    // The system code page, not the user's culture: it is what Windows applications without Unicode wrote files in.
    private static Encoding SystemCodePage()
    {
        uint cp = PInvoke.GetACP();
        return cp == 65001 ? Encoding.Latin1 : CodePagesEncodingProvider.Instance.GetEncoding((int)cp) ?? Encoding.Latin1;
    }

    /// <summary>Entry count and central directory size from the zip's end record, or null when there is none.</summary>
    private static (int Entries, long DirectoryBytes)? ZipDirectory(Stream file)
    {
        // The end record is 22 bytes plus a comment of at most 65,535, at the very end of the file.
        int tail = (int)Math.Min(file.Length, 22 + 65_535);
        var bytes = new byte[tail];
        file.Position = file.Length - tail;
        file.ReadExactly(bytes);
        for (int i = tail - 22; i >= 0; i--)
        {
            if (bytes[i] == 0x50 && bytes[i + 1] == 0x4B && bytes[i + 2] == 0x05 && bytes[i + 3] == 0x06)
            {
                // 0xFFFF means the real count is in a ZIP64 record, which only a zip of 65,535 entries or more needs.
                int entries = BitConverter.ToUInt16(bytes, i + 10);
                return (entries == 0xFFFF ? int.MaxValue : entries, BitConverter.ToUInt32(bytes, i + 12));
            }
        }

        return null;
    }

    /// <summary>Reads through to the inner stream and throws once more than the cap has come out of it.</summary>
    private sealed class CappedStream(Stream inner, long cap) : Stream
    {
        private long _read;

        public sealed class TooLargeException : IOException
        {
        }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position { get => _read; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            int n = inner.Read(buffer);
            _read += n;
            return _read > cap ? throw new TooLargeException() : n;
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

        public override void SetLength(long value) => throw new NotSupportedException();

        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                inner.Dispose();
            base.Dispose(disposing);
        }
    }
}
