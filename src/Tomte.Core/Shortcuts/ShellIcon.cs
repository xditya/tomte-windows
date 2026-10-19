using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Shell;

namespace Tomte.Core.Shortcuts;

/// <summary>Pixels as a composition surface takes them: blue, green, red, alpha, premultiplied, top row first.</summary>
public sealed record ShellImage(int Width, int Height, byte[] Pixels);

/// <summary>
/// The icon Explorer shows for a file, folder or link, drawn by the shell at the exact pixel size asked for, so
/// a tile gets a sharp icon at 100%, 125% and 150% alike. <see cref="For"/> runs no thumbnail handler, so a photo shows
/// its type's icon; <see cref="Thumbnail"/> asks for the photo. Extraction blocks for milliseconds to tens of milliseconds and can load the
/// shell's icon handlers, so call it from a worker thread, never the island's.
/// </summary>
public static class ShellIcon
{
    /// <summary>Null when the shell has no icon for the path (gone, unreadable).</summary>
    public static ShellImage? For(string path, int size) => Image(path, size, SIIGBF.SIIGBF_ICONONLY);

    /// <summary>
    /// The picture the shell keeps for a file, fitted into a square of <paramref name="size"/> pixels: a photo shows
    /// itself. A thumbnail the shell has to make can take a second or more. Null when it has none.
    /// </summary>
    public static ShellImage? Thumbnail(string path, int size) => Image(path, size, SIIGBF.SIIGBF_THUMBNAILONLY);

    private static unsafe ShellImage? Image(string path, int size, SIIGBF flags)
    {
        IShellItemImageFactory? factory = null;
        try
        {
            if (PInvoke.SHCreateItemFromParsingName(path, null, out factory).Failed)
            {
                return null;
            }

            factory.GetImage(new SIZE(size, size), flags, out var bitmap);
            using (bitmap)
            {
                return Read(bitmap);
            }
        }
        catch (Exception e) when (e is COMException or FileNotFoundException or UnauthorizedAccessException)
        {
            return null;
        }
        finally
        {
            if (factory is not null)
            {
                Marshal.FinalReleaseComObject(factory);
            }
        }
    }

    /// <summary>The default browser's icon, for a web tile: a site's own icon would need the network.</summary>
    public static ShellImage? ForWeb(int size)
    {
        Span<char> exe = stackalloc char[1024];
        uint length = (uint)exe.Length;
        return PInvoke.AssocQueryString(ASSOCF.ASSOCF_IS_PROTOCOL, ASSOCSTR.ASSOCSTR_EXECUTABLE, "https", "open", exe, ref length).Succeeded && length > 1
            ? For(new string(exe[..(int)(length - 1)]), size)
            : null;
    }

    private static unsafe ShellImage? Read(SafeHandle bitmap)
    {
        BITMAP info;
        if (PInvoke.GetObject(bitmap, new Span<byte>(&info, sizeof(BITMAP))) == 0)
        {
            return null;
        }

        int width = info.bmWidth, height = Math.Abs(info.bmHeight);
        var header = new BITMAPINFO
        {
            // Negative height asks for the top row first.
            bmiHeader = new BITMAPINFOHEADER { biSize = (uint)sizeof(BITMAPINFOHEADER), biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 },
        };
        var pixels = new byte[width * height * 4];
        var screen = PInvoke.GetDC(HWND.Null);
        try
        {
            fixed (byte* bits = pixels)
            {
                if (PInvoke.GetDIBits(screen, bitmap, 0, (uint)height, bits, &header, DIB_USAGE.DIB_RGB_COLORS) != height)
                {
                    return null;
                }
            }
        }
        finally
        {
            _ = PInvoke.ReleaseDC(HWND.Null, screen);
        }

        Premultiply(pixels);
        return new(width, height, pixels);
    }

    /// <summary>
    /// The shell hands back premultiplied alpha for icons that have it. An old icon with no alpha at all comes back
    /// with alpha 0 everywhere and is opaque; one with straight alpha (a channel above its alpha) is multiplied here.
    /// </summary>
    private static void Premultiply(byte[] bgra)
    {
        bool anyAlpha = false, straight = false;
        for (int i = 0; i < bgra.Length; i += 4)
        {
            byte a = bgra[i + 3];
            anyAlpha |= a != 0;
            straight |= bgra[i] > a || bgra[i + 1] > a || bgra[i + 2] > a;
        }

        for (int i = 0; i < bgra.Length; i += 4)
        {
            if (!anyAlpha)
            {
                bgra[i + 3] = 255;
            }
            else if (straight)
            {
                int a = bgra[i + 3];
                bgra[i] = (byte)(bgra[i] * a / 255);
                bgra[i + 1] = (byte)(bgra[i + 1] * a / 255);
                bgra[i + 2] = (byte)(bgra[i + 2] * a / 255);
            }
        }
    }
}
