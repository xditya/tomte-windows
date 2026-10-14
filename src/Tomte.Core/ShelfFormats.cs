using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;

namespace Tomte.Core;

/// <summary>The drop effects OLE uses, with its values, so the shell can cast them straight to DROPEFFECT.</summary>
[Flags]
public enum ShelfEffect
{
    None = 0,
    Copy = 1,
    Move = 2,
    Link = 4,
}

/// <summary>One entry of a FileGroupDescriptorW. DeclaredSize is null when the source did not set FD_FILESIZE.</summary>
public readonly record struct VirtualFile(string Name, long? DeclaredSize, bool IsFolder);

/// <summary>Reads up to buffer.Length bytes into the buffer and returns how many: 0 at the end, negative when the read failed.</summary>
public delegate int ReadChunk(Span<byte> buffer);

/// <summary>
/// The byte layouts that cross the OLE boundary in both directions, and the rules for what a drag offers.
/// Everything that comes in here was written by another process, so every read is bounds-checked.
/// </summary>
public static class ShelfFormats
{
    // DROPFILES: pFiles, pt.x, pt.y, fNC, fWide.
    private const int DropFilesHeader = 20;

    // FILEDESCRIPTORW: dwFlags, clsid, sizel, pointl, dwFileAttributes, three FILETIMEs, nFileSizeHigh, nFileSizeLow,
    // cFileName[260].
    public const int DescriptorSize = 592;
    private const int AttributesAt = 36, SizeHighAt = 64, SizeLowAt = 68, NameAt = 72, NameChars = 260;
    private const uint FdAttributes = 0x4, FdFileSize = 0x40, FdProgressUi = 0x4000, FdUnicode = 0x80000000;
    private const uint FileAttributeDirectory = 0x10;

    /// <summary>The paths in a CF_HDROP block (a DROPFILES header and a double-null-terminated list).</summary>
    public static List<string> ParseDropFiles(ReadOnlySpan<byte> bytes)
    {
        var paths = new List<string>();
        if (bytes.Length < DropFilesHeader)
        {
            return paths;
        }

        uint offset = BinaryPrimitives.ReadUInt32LittleEndian(bytes);
        bool wide = BinaryPrimitives.ReadInt32LittleEndian(bytes[16..]) != 0;
        if (offset < DropFilesHeader || offset >= bytes.Length)
        {
            return paths;
        }

        var list = bytes[(int)offset..];
        if (wide)
        {
            var chars = MemoryMarshal.Cast<byte, char>(list[..(list.Length & ~1)]);
            while (chars.Length > 0 && chars[0] != '\0')
            {
                int end = chars.IndexOf('\0');
                paths.Add(new string(end < 0 ? chars : chars[..end]));
                chars = end < 0 ? [] : chars[(end + 1)..];
            }
        }
        else
        {
            // the ANSI form is read as Latin-1, which differs from code page 1252 in 0x80 to 0x9F; no source
            // tested sends it. Register the code page provider if one ever does.
            while (list.Length > 0 && list[0] != 0)
            {
                int end = list.IndexOf((byte)0);
                paths.Add(Encoding.Latin1.GetString(end < 0 ? list : list[..end]));
                list = end < 0 ? [] : list[(end + 1)..];
            }
        }

        return paths;
    }

    /// <summary>A CF_HDROP block with wide names.</summary>
    public static byte[] DropFiles(IReadOnlyList<string> paths)
    {
        int chars = paths.Sum(p => p.Length + 1) + 1;
        var bytes = new byte[DropFilesHeader + chars * 2];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, DropFilesHeader);
        BinaryPrimitives.WriteInt32LittleEndian(bytes.AsSpan(16), 1);
        var list = MemoryMarshal.Cast<byte, char>(bytes.AsSpan(DropFilesHeader));
        foreach (var path in paths)
        {
            path.AsSpan().CopyTo(list);
            list = list[(path.Length + 1)..];
        }

        return bytes;
    }

    /// <summary>The entries of a FileGroupDescriptorW block; only as many as the block really holds.</summary>
    public static List<VirtualFile> ParseDescriptor(ReadOnlySpan<byte> bytes)
    {
        var files = new List<VirtualFile>();
        if (bytes.Length < 4)
        {
            return files;
        }

        long count = Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(bytes), (bytes.Length - 4) / DescriptorSize);
        for (int i = 0; i < count; i++)
        {
            var d = bytes.Slice(4 + i * DescriptorSize, DescriptorSize);
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(d);
            var name = MemoryMarshal.Cast<byte, char>(d.Slice(NameAt, NameChars * 2));
            int end = name.IndexOf('\0');
            long size = ((long)BinaryPrimitives.ReadUInt32LittleEndian(d[SizeHighAt..]) << 32) | BinaryPrimitives.ReadUInt32LittleEndian(d[SizeLowAt..]);
            bool folder = (flags & FdAttributes) != 0 && (BinaryPrimitives.ReadUInt32LittleEndian(d[AttributesAt..]) & FileAttributeDirectory) != 0;
            files.Add(new VirtualFile(new string(end < 0 ? name : name[..end]), (flags & FdFileSize) != 0 ? size : null, folder));
        }

        return files;
    }

    /// <summary>A FileGroupDescriptorW block: wide names, real sizes and the shell's progress dialog for large copies.</summary>
    public static byte[] Descriptor(IReadOnlyList<(string Name, long Size)> files)
    {
        var bytes = new byte[4 + files.Count * DescriptorSize];
        BinaryPrimitives.WriteUInt32LittleEndian(bytes, (uint)files.Count);
        for (int i = 0; i < files.Count; i++)
        {
            var d = bytes.AsSpan(4 + i * DescriptorSize, DescriptorSize);
            BinaryPrimitives.WriteUInt32LittleEndian(d, FdUnicode | FdFileSize | FdProgressUi);
            BinaryPrimitives.WriteUInt32LittleEndian(d[SizeHighAt..], (uint)(files[i].Size >> 32));
            BinaryPrimitives.WriteUInt32LittleEndian(d[SizeLowAt..], (uint)files[i].Size);
            var name = files[i].Name.AsSpan();
            name[..Math.Min(name.Length, NameChars - 1)].CopyTo(MemoryMarshal.Cast<byte, char>(d.Slice(NameAt, NameChars * 2)));
        }

        return bytes;
    }

    /// <summary>
    /// A virtual file's stream copied into <paramref name="into"/> in 64 KB chunks until the stream says it is done: the
    /// size in the descriptor is a hint that browsers set to 0, so it never stops the read early. False when a read
    /// fails, so a transfer that breaks partway is not taken for a whole file, and when the stream holds more than
    /// <paramref name="cap"/> bytes; the read stops there, so a stream that never ends cannot hang it.
    /// </summary>
    public static bool CopyCapped(ReadChunk read, Stream into, long cap)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        int got;
        while ((got = read(buffer)) > 0)
        {
            total += got;
            if (total > cap)
            {
                return false;
            }

            into.Write(buffer, 0, got);
        }

        return got == 0;
    }

    /// <summary>
    /// How much of an HGLOBAL FileContents block is the file: the declared size when there is one that fits, else the
    /// whole block. GlobalSize can round up, so without a declared size a few zero bytes may follow the content.
    /// </summary>
    public static int ContentLength(long? declared, int blockSize) =>
        declared is > 0 and var d && d <= blockSize ? (int)d : blockSize;

    /// <summary>
    /// Dropped text as a file: a lone http or https address becomes an Internet shortcut named after its host, anything
    /// else a .txt named after its first line.
    /// </summary>
    public static (string Name, byte[] Bytes) FromText(string text)
    {
        string trimmed = text.Trim();
        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && !trimmed.Any(char.IsWhiteSpace))
        {
            return (ShelfStore.SafeName(uri.Host) + ".url", Encoding.ASCII.GetBytes($"[InternetShortcut]\r\nURL={uri.AbsoluteUri}\r\n"));
        }

        string first = trimmed.Split('\n', 2)[0].Trim();
        string name = ShelfStore.SafeName(first.Length > 40 ? first[..40].TrimEnd() : first);
        return ((name == ShelfStore.FallbackName ? "Text" : name) + ".txt", Encoding.UTF8.GetBytes(text));
    }

    /// <summary>
    /// A drag that holds only content goes out as virtual files, which targets copy during the drop. Anything else is
    /// CF_HDROP for every item, the content items by their own files in the shelf folder, because a target takes one of
    /// the two formats and would drop the rest of a mixed drag.
    /// </summary>
    public static bool AsVirtualFiles(IReadOnlyList<ShelfItem> items) => items.All(i => i.Kind == ShelfKind.Content);

    /// <summary>
    /// The user's own files may be copied or moved, and the target decides from the keys as in any Windows drag: Ctrl
    /// copies, Shift moves, and with neither Explorer moves on the same volume and copies across. Anything the shelf
    /// holds itself is copy only: a moved copy would leave the shelf without its file and the target would report nothing.
    /// </summary>
    public static ShelfEffect Effects(IReadOnlyList<ShelfItem> items) =>
        items.All(i => i.Kind == ShelfKind.Reference) ? ShelfEffect.Copy | ShelfEffect.Move : ShelfEffect.Copy;

    /// <summary>
    /// The items that left the shelf with a drag: references the target moved away. Explorer's same-volume move returns
    /// none and writes "Performed DropEffect" instead, so both are read; the file being gone is what decides.
    /// </summary>
    public static List<ShelfItem> MovedAway(IReadOnlyList<ShelfItem> dragged, ShelfEffect returned, ShelfEffect performed, Func<string, bool> exists) =>
        ((returned | performed) & ShelfEffect.Move) == 0 ? [] : [.. dragged.Where(i => i.Kind == ShelfKind.Reference && !exists(i.Path))];
}

/// <summary>
/// When the drop target lets the system's drag image helper in on a drag. The helper's DragEnter costs tens of
/// milliseconds (it reads the drag image from the source across processes), so while the island's shape is moving it
/// waits for the first DragOver after the shape has stopped. Its DragOver, DragLeave and Drop follow only an enter.
/// One per drop target, on the island's thread.
/// </summary>
public sealed class DragImageGate
{
    private bool waiting;

    public bool Entered { get; private set; }

    /// <summary>A drag entered the island. True when the helper should enter now.</summary>
    public bool Begin(bool shapeMoving)
    {
        (waiting, Entered) = (shapeMoving, !shapeMoving);
        return Entered;
    }

    /// <summary>A DragOver. True on the first one after the shape stopped, when the helper should enter.</summary>
    public bool Over(bool shapeMoving)
    {
        if (!waiting || shapeMoving)
        {
            return false;
        }

        (waiting, Entered) = (false, true);
        return true;
    }

    /// <summary>The drag left or dropped. True when the helper had entered and must hear it.</summary>
    public bool End()
    {
        bool was = Entered;
        (waiting, Entered) = (false, false);
        return was;
    }
}
