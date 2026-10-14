using System.Buffers.Binary;
using System.Runtime.InteropServices.WindowsRuntime;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Tomte.Core.Clipboard;

/// <summary>
/// An image as the listener copied it off the clipboard: the bytes of the "PNG" format, or a packed
/// device-independent bitmap (a BITMAPINFO header, any masks and colour table, then the pixels).
/// </summary>
public sealed record CopiedImage(byte[] Bytes, bool Png);

/// <summary>An image as history keeps it: a PNG, its size in pixels, and a small PNG for the panel's row.</summary>
public sealed record KeptImage(byte[] Png, int Width, int Height, byte[] Thumb);

/// <summary>
/// Clipboard images through WinRT imaging: what history keeps of a copy, the bitmap a pick puts back, and
/// a row's thumbnail at the panel's scale. Each decodes or encodes, a few hundred milliseconds for a
/// large image, so call them on a worker, never on the listener's thread or the island's. The pixels stay
/// in native bitmaps that are freed when each call ends, so the only large managed arrays are the copy
/// and the result.
/// </summary>
public static class ClipboardImages
{
    /// <summary>The stored thumbnail, three times the panel's slot, so it is sharp up to 300%.</summary>
    public const int ThumbWidth = (int)(ClipboardPanelLayout.ThumbWidth * 3), ThumbHeight = (int)(ClipboardPanelLayout.ThumbHeight * 3);

    private const int InfoHeader = 40;

    /// <summary>
    /// What history keeps of a copied image, or null when its bitmap (4 bytes a pixel, as a pick puts it
    /// back) would be over <paramref name="maxBytes"/>. A PNG is kept as it came; a bitmap becomes one.
    /// Throws when the bytes are not an image Windows can decode.
    /// </summary>
    public static KeptImage? Keep(CopiedImage copied, long maxBytes)
    {
        // The header and the copy written one after the other, so the copy is not copied again in managed memory.
        using var stream = new InMemoryRandomAccessStream();
        if (!copied.Png)
        {
            Wait(stream.WriteAsync(BmpHeader(copied.Bytes).AsBuffer()));
        }

        Wait(stream.WriteAsync(copied.Bytes.AsBuffer()));
        stream.Seek(0);
        var decoder = Wait(BitmapDecoder.CreateAsync(stream));
        int width = (int)decoder.PixelWidth, height = (int)decoder.PixelHeight;
        if ((long)width * height * 4 > maxBytes)
        {
            return null;
        }

        byte[] png = copied.Png ? copied.Bytes : WholeAsPng(decoder);
        using var thumb = Bitmap(decoder, BitmapAlphaMode.Straight, Cover(decoder, ThumbWidth, ThumbHeight));
        return new KeptImage(png, width, height, Png(thumb));
    }

    /// <summary>
    /// A packed bitmap for CF_DIB: a BITMAPINFOHEADER, 32 bits a pixel with premultiplied alpha, bottom row
    /// first, which is what most apps read.
    /// </summary>
    public static byte[] Dib(byte[] png)
    {
        using var stream = new MemoryStream(png).AsRandomAccessStream();
        var decoder = Wait(BitmapDecoder.CreateAsync(stream));
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
        var decoder = Wait(BitmapDecoder.CreateAsync(stream));
        return Wait(decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, Cover(decoder, width, height),
            ExifOrientationMode.IgnoreExifOrientation, ColorManagementMode.DoNotColorManage)).DetachPixelData();
    }

    /// <summary>The 14-byte file header that makes a packed bitmap a .bmp file: its size, and where its pixels start.</summary>
    public static byte[] BmpHeader(byte[] dib)
    {
        int size = dib.Length >= InfoHeader ? BinaryPrimitives.ReadInt32LittleEndian(dib) : 0;
        if (size < InfoHeader || size > dib.Length)
        {
            throw new ArgumentException("Not a device-independent bitmap.", nameof(dib));
        }

        int bits = BinaryPrimitives.ReadUInt16LittleEndian(dib.AsSpan(14)), compression = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(16));
        int used = BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(32));
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

    // The whole image as a PNG; a bitmap without alpha (most are) stays without it.
    private static byte[] WholeAsPng(BitmapDecoder decoder)
    {
        using var bitmap = Bitmap(decoder, decoder.BitmapAlphaMode == BitmapAlphaMode.Ignore ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Straight, new BitmapTransform());
        return Png(bitmap);
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

    private static byte[] Png(SoftwareBitmap bitmap)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = Wait(BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream));
        encoder.SetSoftwareBitmap(bitmap);
        Wait(encoder.FlushAsync());
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        Wait(reader.LoadAsync((uint)stream.Size));
        reader.ReadBytes(bytes);
        return bytes;
    }

    // Callers are already on a worker, and the work is in order there, so each step is waited for.
    private static T Wait<T>(IAsyncOperation<T> operation) => operation.AsTask().GetAwaiter().GetResult();

    private static T Wait<T, TProgress>(IAsyncOperationWithProgress<T, TProgress> operation) => operation.AsTask().GetAwaiter().GetResult();

    private static void Wait(IAsyncAction action) => action.AsTask().GetAwaiter().GetResult();
}
