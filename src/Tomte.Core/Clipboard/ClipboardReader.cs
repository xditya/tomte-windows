namespace Tomte.Core.Clipboard;

/// <summary>
/// The clipboard as the history reads it: the real one is Win32 on the listener's thread, tests use
/// fakes. Everything but <see cref="TryOpen"/>, <see cref="Wait"/> and the two exe names is called
/// only while the clipboard is open.
/// </summary>
public interface IClipboardSource
{
    /// <summary>The process whose window owns what is on the clipboard, 0 when no window does.</summary>
    int OwnerProcessId { get; }

    /// <summary>Executable name of the clipboard owner's process, or null.</summary>
    string? OwnerExe { get; }

    /// <summary>Executable name of the app in front, or null.</summary>
    string? ForegroundExe { get; }

    /// <summary>One attempt; false while another app has the clipboard open.</summary>
    bool TryOpen();

    /// <summary>Waits between attempts, still answering messages sent to the listener's window.</summary>
    void Wait(int milliseconds);

    void Close();

    /// <summary>Whether a registered format is on the clipboard. Reads nothing of it.</summary>
    bool Has(string format);

    /// <summary>The first four bytes of a registered format as a number; 0 when it holds fewer; null when the format is not there.</summary>
    uint? Number(string format);

    /// <summary>Size of the Unicode text in characters, end mark included and rounded up by the allocator, or -1 when there is none. Reads none of it.</summary>
    int TextLength { get; }

    /// <summary>The Unicode text up to its end mark.</summary>
    string ReadText();

    /// <summary>
    /// The image on the clipboard: the "PNG" format if it is there, otherwise the device-independent bitmap,
    /// which Windows also makes from a plain bitmap; with its size in bytes, rounded up by the allocator.
    /// Null when there is neither. Reads none of it.
    /// </summary>
    (bool Png, long Bytes)? Image { get; }

    /// <summary>A copy of the bytes of the format <see cref="Image"/> named.</summary>
    byte[] ReadImage(bool png);
}

/// <summary>
/// What one clipboard update came to: the text or the image to keep, or neither; and a line for the log that never
/// contains the text.
/// </summary>
public readonly record struct ClipboardRead(string? Text, string Outcome, CopiedImage? Image = null);

/// <summary>
/// Decides for one clipboard update whether history keeps it, reading as little as it can. Paused,
/// nothing is opened. Otherwise who wrote it is checked first, then the formats that ask not to be
/// monitored; only when all of that passes is the text, or with no text the image, read.
/// </summary>
public static class ClipboardReader
{
    /// <summary>Set by password managers: nothing on the clipboard may be monitored or kept.</summary>
    public const string ExcludeFormat = "ExcludeClipboardContentFromMonitorProcessing";

    /// <summary>The older convention for the same request, still set by some password managers.</summary>
    public const string ViewerIgnoreFormat = "Clipboard Viewer Ignore";

    /// <summary>A number: 0 keeps the item out of clipboard history.</summary>
    public const string HistoryFormat = "CanIncludeInClipboardHistory";

    /// <summary>A number: 0 keeps the item off the cloud clipboard. Treated like the history flag.</summary>
    public const string CloudFormat = "CanUploadToCloudClipboard";

    /// <summary>A PNG file's bytes, which browsers and most image apps put beside the bitmap.</summary>
    public const string PngFormat = "PNG";

    // Windows' own history and other monitors open the clipboard right after every change. 250 ms in all.
    private static readonly int[] RetryMs = [5, 5, 10, 10, 20, 20, 40, 40, 50, 50];

    public static ClipboardRead Read(IClipboardSource clipboard, bool paused, IReadOnlyList<AppRule> rules, ClipboardRetention limits)
    {
        int maxChars = limits.MaxChars;
        if (paused)
        {
            return Skip("paused");
        }

        if (!Open(clipboard))
        {
            return Skip("the clipboard stayed busy");
        }

        try
        {
            // Asked while the clipboard is held, so nobody can write between the question and the answer.
            if (OwnClipboardUse.Active || clipboard.OwnerProcessId == Environment.ProcessId)
            {
                return Skip("Tomte's own write");
            }

            if (rules.Count > 0 && (Denied(rules, clipboard.OwnerExe) || Denied(rules, clipboard.ForegroundExe)))
            {
                return Skip("an app with a deny rule");
            }

            foreach (string format in (ReadOnlySpan<string>)[ExcludeFormat, ViewerIgnoreFormat])
            {
                if (clipboard.Has(format))
                {
                    return Skip(format);
                }
            }

            // Only an explicit 1 lets the item in once the flag is there at all.
            foreach (string format in (ReadOnlySpan<string>)[HistoryFormat, CloudFormat])
            {
                if (clipboard.Number(format) is { } flag && flag != 1)
                {
                    return Skip($"{format} {flag}");
                }
            }

            // Text first: an app that copies text often adds a picture of it too, as a spreadsheet does for its cells.
            int length = clipboard.TextLength;
            if (length < 0)
            {
                return ReadImage(clipboard, limits.MaxImageReadBytes);
            }

            if (length > maxChars + 1)
            {
                return Skip($"{length} characters, over the {maxChars} kept");
            }

            string text = clipboard.ReadText();
            if (text.Length > maxChars)
            {
                return Skip($"{text.Length} characters, over the {maxChars} kept");
            }

            return string.IsNullOrWhiteSpace(text) ? Skip("blank text") : new(text, $"kept {text.Length} characters");
        }
        finally
        {
            clipboard.Close();
        }
    }

    // Only the copy happens here, with the clipboard held; decoding and encoding are the watch's worker's.
    private static ClipboardRead ReadImage(IClipboardSource clipboard, int maxBytes)
    {
        if (clipboard.Image is not var (png, bytes))
        {
            return Skip("no text or image");
        }

        if (bytes > maxBytes)
        {
            return Skip($"an image of {bytes} bytes, over the {maxBytes} kept");
        }

        var image = new CopiedImage(clipboard.ReadImage(png), png);
        return new(null, $"kept an image of {image.Bytes.Length} bytes ({(png ? "PNG" : "bitmap")})", image);
    }

    private static bool Open(IClipboardSource clipboard)
    {
        if (clipboard.TryOpen())
        {
            return true;
        }

        foreach (int ms in RetryMs)
        {
            clipboard.Wait(ms);
            if (clipboard.TryOpen())
            {
                return true;
            }
        }

        return false;
    }

    private static bool Denied(IReadOnlyList<AppRule> rules, string? exe) =>
        AppRules.Find(rules, exe)?.Mode is AppRuleMode.Deny or AppRuleMode.DenyWithHotkeys;

    private static ClipboardRead Skip(string why) => new(null, "skipped: " + why);
}

/// <summary>
/// Raised while Tomte itself drives the clipboard through another app: the selection fallback's
/// Ctrl+C and the put-back after it, and for <see cref="Grace"/> after the last one ends, because an
/// app can write its copy after the fallback has stopped waiting for it. History keeps nothing that
/// happens meanwhile.
/// </summary>
public static class OwnClipboardUse
{
    public static readonly TimeSpan Grace = TimeSpan.FromSeconds(1);

    private static int depth;
    private static long graceEnds;

    public static bool Active => Volatile.Read(ref depth) > 0 || Environment.TickCount64 < Volatile.Read(ref graceEnds);

    public static void Begin() => Interlocked.Increment(ref depth);

    // The deadline goes first, so there is no moment between the two in which nothing holds.
    public static void End()
    {
        Volatile.Write(ref graceEnds, Environment.TickCount64 + (long)Grace.TotalMilliseconds);
        Interlocked.Decrement(ref depth);
    }
}
