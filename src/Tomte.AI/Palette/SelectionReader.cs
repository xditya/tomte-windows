using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Accessibility;

namespace Tomte.AI.Palette;

/// <summary>What UI Automation said about the focused element's selection.</summary>
public sealed record UiaSelection(string? Text, string? ElementName, string? ClassName, string? Error);

/// <summary>The selection rule, kept pure so it is testable without a desktop.</summary>
public static partial class SelectionRule
{
    /// <summary>
    /// Trust UIA. An empty selection means nothing is selected and must not cost a Ctrl+C round
    /// trip, except in VS Code's editor before its accessibility mode is on, which reports an
    /// empty selection while text is selected. A UIA failure (no text pattern, a hung target) falls back.
    /// </summary>
    public static bool ShouldFallBack(UiaSelection s) =>
        s.Error is not null || (string.IsNullOrEmpty(s.Text) && LooksNotAccessible(s));

    public static bool LooksNotAccessible(UiaSelection s) =>
        s.ElementName?.Contains("not accessible", StringComparison.OrdinalIgnoreCase) == true
        || string.Equals(s.ClassName, "native-edit-context", StringComparison.Ordinal);

    /// <summary>VS Code copies the whole line when nothing is selected and says so in its custom MIME payload.</summary>
    public static bool IsEmptySelectionCopy(string? chromiumMime) =>
        chromiumMime is not null && EmptySelectionFlag().IsMatch(chromiumMime);

    /// <summary>Both paths give the same text: one line-ending style, no trailing whitespace.</summary>
    public static string Normalize(string text) => text.ReplaceLineEndings("\n").TrimEnd();

    /// <summary>What UIA says is selected, for a palette that has no fallback; null when nothing is, or only a Ctrl+C could tell.</summary>
    public static string? Text(UiaSelection s) => ShouldFallBack(s) || string.IsNullOrWhiteSpace(s.Text) ? null : Normalize(s.Text);

    [GeneratedRegex("""isFromEmptySelection\\?["\\]*\s*:\s*true""")]
    private static partial Regex EmptySelectionFlag();
}

/// <summary>Reads the current selection: UI Automation first, simulated Ctrl+C with a full clipboard restore only when the rule says so.</summary>
public static unsafe class SelectionReader
{
    private static IUIAutomation2? uia;

    /// <summary>
    /// The selection chip for the window in front: UIA now (passive), and when UIA could not tell,
    /// the Ctrl+C fallback armed on the chip so it runs only when the user turns the chip on.
    /// <paramref name="app"/> names the app in the chip's tooltip.
    /// </summary>
    public static ContextChip Chip(nint expectedForeground, string app = "")
    {
        var selection = ReadUia();
        if (!SelectionRule.ShouldFallBack(selection))
            return ContextChips.Selection(string.IsNullOrEmpty(selection.Text) ? null : SelectionRule.Normalize(selection.Text), app: app);
        return ContextChips.Selection(null, () => Fallback(expectedForeground), app);
    }

    /// <summary>
    /// The focused element's selection through UI Automation alone, for the hotkey palette: its prompt takes the keys
    /// right after, so by the time the user turns the chip on a Ctrl+C would land on the island. Off the UI thread.
    /// </summary>
    public static string? Read() => SelectionRule.Text(ReadUia());

    /// <summary>
    /// Simulated Ctrl+C with the clipboard put back. Null when nothing was copied, the copy was VS Code's
    /// whole-line copy, or the foreground window moved away from <paramref name="expectedForeground"/>
    /// (the chord would land in the wrong app).
    /// </summary>
    public static string? Fallback(nint expectedForeground)
    {
        var copy = ClipboardSnapshot.CopySelection(expectedForeground);
        if (copy is not { Text: { Length: > 0 } text } || SelectionRule.IsEmptySelectionCopy(copy.Value.ChromiumMime))
            return null;
        return SelectionRule.Normalize(text);
    }

    public static UiaSelection ReadUia()
    {
        string? name = null, className = null;
        try
        {
            if (uia is null)
            {
                uia = (IUIAutomation2)new CUIAutomation8();
                // A hung target would otherwise block for the default 20 s.
                uia.ConnectionTimeout = 1000;
                uia.TransactionTimeout = 1000;
            }

            var element = uia.GetFocusedElement();
            name = Text(element.CurrentName);
            className = Text(element.CurrentClassName);
            if (element.GetCurrentPattern(UIA_PATTERN_ID.UIA_TextPatternId) is IUIAutomationTextPattern pattern)
            {
                var ranges = pattern.GetSelection();
                var parts = new List<string>();
                for (int i = 0; i < ranges.Length; i++)
                    parts.Add(Text(ranges.GetElement(i).GetText(-1)));
                return new(string.Concat(parts), name, className, null);
            }

            return new(null, name, className, "no TextPattern");
        }
        catch (COMException e)
        {
            return new(null, name, className, $"UIA 0x{e.HResult:X8}");
        }
    }

    private static string Text(BSTR value)
    {
        var s = value.ToString() ?? "";
        Marshal.FreeBSTR(value);
        return s;
    }
}
