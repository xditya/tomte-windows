using System.Diagnostics;
using Tomte.Core.Search;

namespace Tomte.Shell;

/// <summary>The search box's debug driver.</summary>
internal static partial class Program
{
    /// <summary>
    /// --debug-search "words" with --debug-log=path: opens the search box as its hotkey does, types the words a letter at a
    /// time as a person would, logs the rows after each letter and once the file search has answered, presses the keys in
    /// --debug-keys (Down, Up, Tab, ShiftTab, comma separated) and logs the selection after each, then hides the island.
    /// Opens nothing. --debug-everything name searches a named Everything instance instead of the usual one, and
    /// --debug-quit ends Tomte half a second after the island hid.
    /// </summary>
    private static async Task DebugSearch(TriggerController triggers, string words, string? keys, bool quit, Action<string> log)
    {
        // After the launch flight, which any open would cut short.
        await Task.Delay(1500);
        var watch = Stopwatch.StartNew();
        if (!triggers.OpenSearch() || search?.View is not { } view)
        {
            log("debug search: did not open");
            return;
        }

        log($"debug search: open in {watch.Elapsed.TotalMilliseconds:F1} ms, the box {(focus?.Focused is not null ? "has" : "does not have")} the keys");
        await Task.Delay(2500);
        Rows("empty box");
        for (int i = 1; i <= words.Length; i++)
        {
            view.Fill(words[..i]);
            // The box reads its text on the next turn, and ranks on it then.
            await Task.Delay(120);
            Rows($"typed \"{words[..i]}\"");
        }

        await Task.Delay(1500);
        Rows("after the file search");
        foreach (string key in (keys ?? "").Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            int code = key switch { "Down" => 0x28, "Up" => 0x26, "Tab" or "ShiftTab" => 0x09, _ => 0 };
            bool took = view.Press(code, ctrl: false, shift: key == "ShiftTab");
            log($"debug search: {key} {(took ? "taken" : "not taken")}, selected {view.Selected?.Kind} \"{view.Selected?.Name}\"");
        }

        triggers.Hide();
        log("debug search: hidden");
        if (quit)
        {
            await Task.Delay(500);
            Windows.Win32.PInvoke.PostQuitMessage(0);
        }

        void Rows(string when) => log($"debug search: {when}: {view.Rows.Count} rows, selected \"{view.Selected?.Name}\": "
            + string.Join(" | ", view.Rows.Select(r => $"{r.Kind} {r.Name}{(r.Detail.Length > 0 ? " (" + r.Detail + ")" : "")}")));
    }
}
