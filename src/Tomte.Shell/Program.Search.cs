using System.Diagnostics;
using System.Text.Json;
using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Search;

namespace Tomte.Shell;

/// <summary>The search box's AI search and its debug driver.</summary>
internal static partial class Program
{
    /// <summary>
    /// AI search's send: the AI box's provider, model, cap, prices and send log, kept in the AI history, with the file
    /// finder over Everything or the Windows index as its only tools. Made when the row is chosen; null when no provider
    /// can be used (the reason is logged).
    /// </summary>
    private static PaletteSession? AiSearchSession(SettingsStore store, string words, Action<string>? log)
    {
        if (Configured(store, log) is not { } configured)
        {
            return null;
        }

        // The session wants the palette's tools and grants; it offers neither, so empty ones do.
        var unused = new BuiltInTools(new InMemoryShelf(), new InMemoryScratchpad(), _ => { }, _ => { });
        return new PaletteSession(configured.Provider, configured.Config, configured.Options with { MaxToolSteps = FileFinder.MaxCalls + 3 }, new SendLog(SendLogPath), unused, new PermissionGrants(), [])
        {
            Prompt = words,
            FileSearch = new FileFinder(FileFinder.Local),
            History = History,
        };
    }

    /// <summary>
    /// --debug-find "json" with --debug-log=path: runs find_files with these arguments as the model would send them, with
    /// no model, and logs the search Everything or the index is asked and what goes back. Nothing is sent anywhere.
    /// </summary>
    private static void DebugFind(string json, Action<string> log)
    {
        var input = JsonDocument.Parse(json).RootElement;
        if (FileFinder.Read(input, out var filter) is { } bad)
        {
            log($"debug find: {bad}");
            return;
        }

        log($"debug find: Everything {(Everything.Running ? "runs" : "is not running")}; its search: {FileQuery.Everything(filter!, FileQuery.Excluded())}");
        log($"debug find: the index's SQL: {FileQuery.IndexSql(filter!, FileQuery.Excluded(), FileFinder.PerCall)}");
        var watch = Stopwatch.StartNew();
        var (summary, result, error) = new FileFinder(FileFinder.Local).Run(new ToolCallPart("debug", FileFinder.FindFiles, input), CancellationToken.None);
        log($"debug find: {summary} in {watch.ElapsedMilliseconds} ms{(error ? " (error)" : "")}:\n{result}");
    }

    /// <summary>
    /// --debug-record folder: every provider answer is also written there whole, numbered, with the request beside it, to
    /// make test fixtures and count tokens. Answers then arrive whole instead of streamed.
    /// </summary>
    private sealed class Recorder(string folder) : DelegatingHandler(new SocketsHttpHandler { AllowAutoRedirect = false })
    {
        private int count;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            int n = Interlocked.Increment(ref count);
            Directory.CreateDirectory(folder);
            if (request.Content is not null)
            {
                await File.WriteAllTextAsync(Path.Combine(folder, $"{n}.request.json"), await request.Content.ReadAsStringAsync(cancellationToken), cancellationToken);
            }

            var response = await base.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            await File.WriteAllBytesAsync(Path.Combine(folder, $"{n}.sse"), body, cancellationToken);
            var copy = new ByteArrayContent(body);
            foreach (var header in response.Content.Headers)
            {
                copy.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            response.Content = copy;
            return response;
        }
    }

    /// <summary>
    /// --debug-search "words" with --debug-log=path: opens the search box as its hotkey does, types the words a letter at a
    /// time as a person would, logs the rows after each letter and once the file search has answered, presses the keys in
    /// --debug-keys (Down, Up, Tab, ShiftTab, Enter, CtrlEnter, Esc; comma separated; WaitAi waits for an AI search to end and
    /// logs what it shows) and logs the selection after each, then hides the island. Opens nothing: a row Enter would open
    /// is logged instead; Enter on Search with AI does run the AI search. --debug-race types the words and
    /// presses Enter at once, before Start's app list has been read, to show Enter waiting for it. --debug-everything name
    /// searches a named Everything instance instead of the usual one, and --debug-quit ends Tomte half a second after the
    /// island hid.
    /// </summary>
    private static async Task DebugSearch(TriggerController triggers, string words, string? keys, bool race, bool quit, Action<string> log)
    {
        // After the launch flight, which any open would cut short.
        await Task.Delay(1500);
        var watch = Stopwatch.StartNew();
        search!.Dry = true;
        if (!triggers.OpenSearch() || search.View is not { } view)
        {
            log("debug search: did not open");
            return;
        }

        log($"debug search: open in {watch.Elapsed.TotalMilliseconds:F1} ms, the box {(focus?.Focused is not null ? "has" : "does not have")} the keys");
        if (race)
        {
            view.Fill(words);
            // The box reads its text on the next turn.
            await Task.Delay(1);
            Rows($"typed \"{words}\" at once");
            log($"debug search: Enter {(view.Press(0x0D, ctrl: false, shift: false) ? "taken" : "not taken")} {watch.ElapsedMilliseconds} ms after the open");
            await Task.Delay(3000);
            await End();
            return;
        }

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
            if (key == "WaitAi")
            {
                await Task.WhenAny(search.AiRun, Task.Delay(TimeSpan.FromSeconds(150)));
                // The last answer reaches the view on the next turn.
                await Task.Delay(100);
                Rows($"after the AI search, line \"{view.Lead}\"");
                continue;
            }

            bool took = key switch
            {
                "Esc" => view.OnEscape(),
                "Enter" or "CtrlEnter" => view.Press(0x0D, ctrl: key == "CtrlEnter", shift: false),
                _ => view.Press(key switch { "Down" => 0x28, "Up" => 0x26, "Tab" or "ShiftTab" => 0x09, _ => 0 }, ctrl: false, shift: key == "ShiftTab"),
            };
            await Task.Delay(50);
            log($"debug search: {key} {(took ? "taken" : "not taken")}, selected {view.Selected?.Kind} \"{view.Selected?.Name}\", card \"{view.Card?.Title}\" on \"{view.CardButton}\", text \"{view.Text}\", rows {view.Rows.Count}");
        }

        await End();

        async Task End()
        {
            triggers.Hide();
            log("debug search: hidden");
            // The working set is trimmed two seconds after the island hid.
            await Task.Delay(2500);
            if (quit)
            {
                Windows.Win32.PInvoke.PostQuitMessage(0);
            }
        }

        void Rows(string when) => log($"debug search: {when}: {view.Rows.Count} rows, selected \"{view.Selected?.Name}\": "
            + string.Join(" | ", view.Rows.Select(r => $"{r.Kind} {r.Name}{(r.Detail.Length > 0 ? " (" + r.Detail + ")" : "")}")));
    }
}
