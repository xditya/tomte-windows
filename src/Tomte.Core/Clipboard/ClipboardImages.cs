using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Imaging;
using Windows.Win32.System.Com;
using Windows.Win32.System.Com.StructuredStorage;
using Windows.Win32.System.Memory;

namespace Tomte.Core.Clipboard;

/// <summary>
/// An image as the listener copied it off the clipboard, in one block of global memory until disposed: the bytes
/// of the "PNG" format, or a packed device-independent bitmap (a BITMAPINFO header, any masks and colour table, then
/// the pixels) with 14 bytes free in front of it for the header that makes it a .bmp file. The codec reads the block
/// where it is, so the copy is never copied again.
/// </summary>
public sealed unsafe class CopiedImage : IDisposable
{
    private const int FileHeader = 14;
    private HGLOBAL block;

    public CopiedImage(ReadOnlySpan<byte> bytes, bool png)
    {
        (Png, Length) = (png, bytes.Length);
        block = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)(Front + bytes.Length));
        if (block.IsNull)
        {
            throw new OutOfMemoryException();
        }

        bytes.CopyTo(new Span<byte>((byte*)PInvoke.GlobalLock(block) + Front, bytes.Length));
        PInvoke.GlobalUnlock(block);
    }

    ~CopiedImage() => Free();

    public bool Png { get; }

    /// <summary>How many bytes the format held.</summary>
    public int Length { get; }

    /// <summary>A copy of the format's bytes in managed memory.</summary>
    public byte[] ToArray() => Read(span => span.ToArray());

    public void Dispose()
    {
        Free();
        GC.SuppressFinalize(this);
    }

    /// <summary>The format's bytes, held still while <paramref name="use"/> runs.</summary>
    internal T Read<T>(Func<ReadOnlySpan<byte>, T> use)
    {
        ObjectDisposedException.ThrowIf(block.IsNull, this);
        try
        {
            return use(new ReadOnlySpan<byte>((byte*)PInvoke.GlobalLock(block) + Front, Length));
        }
        finally
        {
            PInvoke.GlobalUnlock(block);
        }
    }

    /// <summary>A stream over the block as a file: for a bitmap, its file header is written into the free bytes first.</summary>
    internal IStream File()
    {
        ObjectDisposedException.ThrowIf(block.IsNull, this);
        if (!Png)
        {
            var header = Read(ClipboardImages.BmpHeader);
            header.CopyTo(new Span<byte>(PInvoke.GlobalLock(block), FileHeader));
            PInvoke.GlobalUnlock(block);
        }

        // The block stays this object's; the stream only reads it.
        PInvoke.CreateStreamOnHGlobal(block, false, out var stream).ThrowOnFailure();
        return stream;
    }

    private int Front => Png ? 0 : FileHeader;

    private void Free()
    {
        if (!block.IsNull)
        {
            PInvoke.GlobalFree(block);
            block = default;
        }
    }
}

/// <summary>An image as history keeps it: a PNG, its size in pixels, and a small PNG for the panel's row.</summary>
public sealed record KeptImage(byte[] Png, int Width, int Height, byte[] Thumb);

/// <summary>
/// Clipboard images: what history keeps of a copy (WIC, from the copy straight to the PNG), the bitmap a pick puts
/// back and a row's thumbnail at the panel's scale (WinRT imaging). Each decodes or encodes, up to a few hundred
/// milliseconds for a large image, so call them on a worker, never on the listener's thread or the island's.
/// </summary>
public static class ClipboardImages
{
    /// <summary>The stored thumbnail: 3:2, sharp in the panel's row slot up to 300% and enough for a glance in its preview.</summary>
    public const int ThumbWidth = 144, ThumbHeight = 96;

    private const int InfoHeader = 40;

    // A PNG ends with its empty IEND chunk, whose checksum never changes.
    private static readonly byte[] PngEnd = [0, 0, 0, 0, (byte)'I', (byte)'E', (byte)'N', (byte)'D', 0xAE, 0x42, 0x60, 0x82];

    /// <summary>
    /// What history keeps of a copied image, or null when its bitmap (4 bytes a pixel, as a pick puts it
    /// back) would be over <paramref name="maxBytes"/>. A PNG is kept as it came; a bitmap becomes one.
    /// Throws when the bytes are not a PNG or a bitmap, as each says it is.
    /// </summary>
    public static unsafe KeptImage? Keep(CopiedImage copied, long maxBytes)
    {
        // Straight through WIC: the decoder reads the copy where it is, the PNG encoder and the thumbnail's scaler
        // pull rows from the decoder a band at a time, so no whole-image bitmap is ever made.
        var made = new Stack<object>();
        try
        {
            var factory = Made(made, Factory());
            var file = Made(made, copied.File());
            // The codec named, so no other one installed on the PC parses what an app put on the clipboard.
            var format = copied.Png ? PInvoke.GUID_ContainerFormatPng : PInvoke.GUID_ContainerFormatBmp;
            var decoder = Made(made, factory.CreateDecoder(&format, null));
            decoder.Initialize(file, WICDecodeOptions.WICDecodeMetadataCacheOnDemand);
            decoder.GetFrame(0, out var frame);
            Made(made, frame);
            frame.GetSize(out uint width, out uint height);
            if (!BitmapFits(width, height, maxBytes))
            {
                return null;
            }

            frame.GetPixelFormat(out Guid pixels);
            bool alpha = pixels == PInvoke.GUID_WICPixelFormat32bppBGRA || pixels == PInvoke.GUID_WICPixelFormat32bppPBGRA;
            // A bitmap without alpha (most are) stays without it.
            byte[] png = copied.Png ? copied.Read(Trimmed) : Encode(factory, frame, width, height, alpha);
            return new KeptImage(png, (int)width, (int)height, Encode(factory, Cover(factory, made, frame, width, height), ThumbWidth, ThumbHeight, alpha: true));
        }
        finally
        {
            // Each stage let go now, in reverse, rather than when a finalizer gets to it.
            while (made.TryPop(out var item))
            {
                Marshal.FinalReleaseComObject(item);
            }

            // The decoder reads the copy's memory until the stream is released above; a caller that does not hold the
            // copy must not see it finalized, and its memory freed, before then.
            GC.KeepAlive(copied);
        }
    }

    /// <summary>Whether the image as a 32-bit bitmap is at most <paramref name="maxBytes"/>, for any size a header can claim.</summary>
    public static bool BitmapFits(uint width, uint height, long maxBytes) => (ulong)width * height <= (ulong)maxBytes / 4;

    /// <summary>
    /// A packed bitmap for CF_DIB: a BITMAPINFOHEADER, 32 bits a pixel with premultiplied alpha, bottom row
    /// first, which is what most apps read.
    /// </summary>
    public static byte[] Dib(byte[] png)
    {
        using var stream = new MemoryStream(png).AsRandomAccessStream();
        var decoder = Wait(BitmapDecoder.CreateAsync(BitmapDecoder.PngDecoderId, stream));
        int width = (int)decoder.PixelWidth, height = (int)decoder.PixelHeight, size = width * height * 4;
        using var bitmap = Bitmap(decoder, BitmapAlphaMode.Premultiplied, new BitmapTransform { Flip = BitmapFlip.Vertical });
        var dib = new byte[InfoHeader + size];
        var header = dib.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(header, InfoHeader);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        // A positive height: the rows run bottom to top, as the flip above has them.
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height);
        BinaryPrimitives.WriteInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[14..], 32);
        BinaryPrimitives.WriteInt32LittleEndian(header[20..], size);
        bitmap.CopyToBuffer(dib.AsBuffer(InfoHeader, size));
        return dib;
    }

    /// <summary>A stored thumbnail scaled to cover <paramref name="width"/> by <paramref name="height"/> pixels and cut to them: premultiplied BGRA, top row first.</summary>
    public static byte[] Thumbnail(byte[] thumb, int width, int height)
    {
        using var stream = new MemoryStream(thumb).AsRandomAccessStream();
        var decoder = Wait(BitmapDecoder.CreateAsync(BitmapDecoder.PngDecoderId, stream));
        return Wait(decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, Cover(decoder, width, height),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
    }

    /// <summary>The 14-byte file header that makes a packed bitmap a .bmp file: its size, and where its pixels start.</summary>
    public static byte[] BmpHeader(ReadOnlySpan<byte> dib)
    {
        int size = dib.Length >= InfoHeader ? BinaryPrimitives.ReadInt32LittleEndian(dib) : 0;
        if (size < InfoHeader || size > dib.Length)
        {
            throw new ArgumentException("Not a device-independent bitmap.", nameof(dib));
        }

        int bits = BinaryPrimitives.ReadUInt16LittleEndian(dib[14..]), compression = BinaryPrimitives.ReadInt32LittleEndian(dib[16..]);
        int used = BinaryPrimitives.ReadInt32LittleEndian(dib[32..]);
        // Only the oldest header has the colour masks after it (BI_BITFIELDS, BI_ALPHABITFIELDS); later ones hold them.
        int masks = size == InfoHeader ? compression switch { 3 => 12, 6 => 16, _ => 0 } : 0;
        int colours = used > 0 ? used : bits <= 8 ? 1 << bits : 0;
        long offset = 14 + size + masks + 4L * colours;
        if (offset > 14 + dib.Length)
        {
            throw new ArgumentException("The bitmap's header runs past its data.", nameof(dib));
        }

        var header = new byte[14];
        header[0] = (byte)'B';
        header[1] = (byte)'M';
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(2), 14 + dib.Length);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(10), (int)offset);
        return header;
    }

    // The clipboard's memory can run past the file it holds; what follows the end is dropped, so the same image
    // copied twice is the same bytes.
    private static byte[] Trimmed(ReadOnlySpan<byte> png)
    {
        int end = png.LastIndexOf(PngEnd) + PngEnd.Length;
        return (end < PngEnd.Length ? png : png[..end]).ToArray();
    }

    private static IWICImagingFactory Factory()
    {
        PInvoke.CoCreateInstance(PInvoke.CLSID_WICImagingFactory, null, CLSCTX.CLSCTX_INPROC_SERVER, out IWICImagingFactory factory).ThrowOnFailure();
        return factory;
    }

    private static T Made<T>(Stack<object> made, T item)
        where T : class
    {
        made.Push(item);
        return item;
    }

    // The source scaled to cover the stored thumbnail, centred, and cut to it; both steps pull rows as asked.
    private static unsafe IWICBitmapSource Cover(IWICImagingFactory factory, Stack<object> made, IWICBitmapSource source, uint width, uint height)
    {
        double k = Math.Max((double)ThumbWidth / width, (double)ThumbHeight / height);
        uint scaledWidth = Math.Max(ThumbWidth, (uint)Math.Round(width * k)), scaledHeight = Math.Max(ThumbHeight, (uint)Math.Round(height * k));
        factory.CreateBitmapScaler(out var scaler);
        Made(made, scaler).Initialize(source, scaledWidth, scaledHeight, WICBitmapInterpolationMode.WICBitmapInterpolationModeFant);
        factory.CreateBitmapClipper(out var clipper);
        var cut = new WICRect { X = (int)(scaledWidth - ThumbWidth) / 2, Y = (int)(scaledHeight - ThumbHeight) / 2, Width = ThumbWidth, Height = ThumbHeight };
        Made(made, clipper).Initialize(scaler, &cut);
        return clipper;
    }

    // A PNG of the source, encoded as the encoder pulls its rows; the only whole buffer is the PNG itself.
    private static unsafe byte[] Encode(IWICImagingFactory factory, IWICBitmapSource source, uint width, uint height, bool alpha)
    {
        var made = new Stack<object>();
        try
        {
            PInvoke.CreateStreamOnHGlobal(default, true, out var output).ThrowOnFailure();
            Made(made, output);
            var container = PInvoke.GUID_ContainerFormatPng;
            var encoder = Made(made, factory.CreateEncoder(&container, null));
            encoder.Initialize(output, WICBitmapEncoderCacheOption.WICBitmapEncoderNoCache);
            IPropertyBag2 options = null!;
            encoder.CreateNewFrame(out var frame, ref options);
            Made(made, frame);
            if (options is not null)
            {
                Made(made, options);
            }

            frame.Initialize(options);
            frame.SetSize(width, height);
            var format = alpha ? PInvoke.GUID_WICPixelFormat32bppBGRA : PInvoke.GUID_WICPixelFormat24bppBGR;
            frame.SetPixelFormat(ref format);
            source.GetPixelFormat(out Guid from);
            if (from != format)
            {
                factory.CreateFormatConverter(out var converter);
                Made(made, converter).Initialize(source, &format, WICBitmapDitherType.WICBitmapDitherTypeNone, null, 0, WICBitmapPaletteType.WICBitmapPaletteTypeCustom);
                source = converter;
            }

            frame.WriteSource(source, null);
            frame.Commit();
            encoder.Commit();
            output.Stat(out var stat, STATFLAG.STATFLAG_NONAME);
            // The stream owns this memory and frees it on release. A plain handle, not the generated overload's
            // SafeHandle, which would free it a second time when collected, by then often another allocation's.
            HGLOBAL memory;
            PInvoke.GetHGlobalFromStream(output, &memory).ThrowOnFailure();
            var bytes = new byte[stat.cbSize];
            new ReadOnlySpan<byte>(PInvoke.GlobalLock(memory), bytes.Length).CopyTo(bytes);
            PInvoke.GlobalUnlock(memory);
            return bytes;
        }
        finally
        {
            while (made.TryPop(out var item))
            {
                Marshal.FinalReleaseComObject(item);
            }
        }
    }

    private static SoftwareBitmap Bitmap(BitmapDecoder decoder, BitmapAlphaMode alpha, BitmapTransform transform) =>
        Wait(decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, alpha, transform, ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage));

    // Scaled to cover the box, centred, and cut to it.
    private static BitmapTransform Cover(BitmapDecoder decoder, int width, int height)
    {
        double k = Math.Max((double)width / decoder.PixelWidth, (double)height / decoder.PixelHeight);
        uint scaledWidth = Math.Max((uint)width, (uint)Math.Round(decoder.PixelWidth * k));
        uint scaledHeight = Math.Max((uint)height, (uint)Math.Round(decoder.PixelHeight * k));
        return new BitmapTransform
        {
            ScaledWidth = scaledWidth,
            ScaledHeight = scaledHeight,
            InterpolationMode = BitmapInterpolationMode.Fant,
            Bounds = new BitmapBounds { X = (scaledWidth - (uint)width) / 2, Y = (scaledHeight - (uint)height) / 2, Width = (uint)width, Height = (uint)height },
        };
    }

    // Callers are already on a worker, and the work is in order there, so each step is waited for.
    private static T Wait<T>(IAsyncOperation<T> operation) => operation.AsTask().GetAwaiter().GetResult();
}
