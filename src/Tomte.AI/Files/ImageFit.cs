using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace Tomte.AI.Files;

/// <summary>The largest image a provider documents taking: the long edge, the short edge and the pixel count.</summary>
public sealed record ImageBox(int MaxLongEdge, int MaxShortEdge, long MaxPixels);

/// <summary>An image ready to send: its bytes and size, and the size it had on disk.</summary>
public sealed record FittedImage(string MediaType, byte[] Data, int Width, int Height, int SourceWidth, int SourceHeight);

/// <summary>
/// Downscales an image to what the provider takes and strips its metadata, with WinRT imaging. The pixels are decoded
/// (turned upright by the EXIF orientation, colour-managed to sRGB) and written into a new encoder; nothing from the
/// source's property sets is copied, so EXIF, GPS location, XMP, IPTC and colour profiles stay behind. The only fields
/// the new file has are the ones its encoder writes itself (size, a 96 DPI resolution). Photos stay JPEG; everything
/// else becomes PNG, or JPEG when the PNG would pass a provider's per-image limit.
/// </summary>
public static class ImageFit
{
    // Anthropic takes 10 MB an image directly and 5 MB through Bedrock and Google Cloud; base64 adds a third.
    private const int MaxPngBytes = 3_500_000;

    public static (int Width, int Height) Fit(int width, int height, ImageBox box)
    {
        double scale = Math.Min(1, Math.Min((double)box.MaxLongEdge / Math.Max(width, height), (double)box.MaxShortEdge / Math.Min(width, height)));
        scale = Math.Min(scale, Math.Sqrt((double)box.MaxPixels / ((double)width * height)));
        return scale >= 1 ? (width, height) : (Math.Max(1, (int)(width * scale)), Math.Max(1, (int)(height * scale)));
    }

    public static async Task<FittedImage> FitAsync(Stream file, ImageBox box, FileLimits limits, CancellationToken ct)
    {
        if (file.Length > limits.MaxBytes)
            throw new FileReadException($"is larger than the {limits.MaxBytes >> 20} MB Tomte opens.");
        using var source = file.AsRandomAccessStream();
        BitmapDecoder decoder;
        try
        {
            decoder = await BitmapDecoder.CreateAsync(source).AsTask(ct);
        }
        catch (Exception e) when (e is not OperationCanceledException and not OutOfMemoryException)
        {
            throw new FileReadException("could not be read as an image: it is damaged, or Windows has no codec for its format.");
        }

        int w = (int)decoder.OrientedPixelWidth, h = (int)decoder.OrientedPixelHeight;
        if ((long)w * h > limits.MaxImagePixels)
            throw new FileReadException($"is {(long)w * h / 1_000_000:N0} megapixels, more than Tomte decodes.");
        var (tw, th) = Fit(w, h, box);
        // The transform scales before it turns the image upright, so its sizes are in the stored orientation.
        bool turned = decoder.OrientedPixelWidth != decoder.PixelWidth;
        var transform = new BitmapTransform
        {
            ScaledWidth = (uint)(turned ? th : tw),
            ScaledHeight = (uint)(turned ? tw : th),
            InterpolationMode = BitmapInterpolationMode.Fant,
        };
        bool photo = decoder.DecoderInformation.CodecId == BitmapDecoder.JpegDecoderId;
        var pixels = await decoder.GetPixelDataAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Straight, transform,
            ExifOrientationMode.RespectExifOrientation, ColorManagementMode.ColorManageToSRgb).AsTask(ct);
        byte[] data = pixels.DetachPixelData();

        byte[] encoded = await EncodeAsync(data, tw, th, photo, ct);
        if (!photo && encoded.Length > MaxPngBytes)
        {
            (encoded, photo) = (await EncodeAsync(data, tw, th, jpeg: true, ct), true);
        }

        return new FittedImage(photo ? "image/jpeg" : "image/png", encoded, tw, th, w, h);
    }

    private static async Task<byte[]> EncodeAsync(byte[] pixels, int width, int height, bool jpeg, CancellationToken ct)
    {
        using var stream = new InMemoryRandomAccessStream();
        var encoder = jpeg
            ? await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, stream,
                [new KeyValuePair<string, BitmapTypedValue>("ImageQuality", new BitmapTypedValue(0.85f, Windows.Foundation.PropertyType.Single))]).AsTask(ct)
            : await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, stream).AsTask(ct);
        encoder.SetPixelData(BitmapPixelFormat.Bgra8, jpeg ? BitmapAlphaMode.Ignore : BitmapAlphaMode.Straight, (uint)width, (uint)height, 96, 96, pixels);
        await encoder.FlushAsync().AsTask(ct);
        var bytes = new byte[stream.Size];
        using var reader = new DataReader(stream.GetInputStreamAt(0));
        await reader.LoadAsync((uint)stream.Size).AsTask(ct);
        reader.ReadBytes(bytes);
        return bytes;
    }
}
