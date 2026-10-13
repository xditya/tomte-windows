using System.Diagnostics;

namespace Tomte.AI.Palette;

/// <summary>The two built-in tools that reach outside the app, for the shell and the harness to hand to <see cref="BuiltInTools"/>.</summary>
public static class DesktopEffects
{
    public static void CopyToClipboard(string text) => ClipboardSnapshot.SetText(text);

    /// <summary>The URL is validated by the tool; the shell opens it in the default browser.</summary>
    public static void OpenUrl(string url) =>
        Process.Start(new ProcessStartInfo(url) { UseShellExecute = true })?.Dispose();
}
