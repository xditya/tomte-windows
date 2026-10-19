using System.Diagnostics;
using System.Numerics;
using System.Text.Json;
using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Interaction;
using Tomte.Core.Search;

namespace Tomte.Shell;

/// <summary>The search box's AI search and its debug drivers.</summary>
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
    /// --debug-strip with --debug-log=path and --debug-settings file: the tab strip's search button, with no input to the
    /// desktop. Opens the island as the tray's Open island does, pinned, and clicks the button by posting the island window
    /// its own button messages at the button's centre, with Esc posted the moment the search opens, during its morph; then
    /// a click with Esc after the search settles, then Tab to the button and Enter, then Space, each with Esc. Keys are
    /// posted to the island window as the keyboard sends them. Then Tab from the Scratchpad tab's label into the note with
    /// Space and Enter, a hide with the working set five seconds later, and the search as the tray's Search opens it. Logs
    /// the island's state, the target with focus and the island's bounds after each step, then quits.
    /// </summary>
    private static async Task DebugStrip(IslandWindow island, TriggerController triggers, Action<string> log)
    {
        const uint Escape = 0x1B, Space = 0x20, Enter = 0x0D;
        // After the launch flight, which any open would cut short.
        await Task.Delay(2500);
        triggers.Toggle();
        // Pinned, so the island stays with nobody's pointer on it; the hide near the end unpins it.
        triggers.TogglePin();
        await Step("Open island");

        Click();
        var wait = Stopwatch.StartNew();
        while (!triggers.Searching && wait.ElapsedMilliseconds < 2000)
        {
            await Task.Delay(1);
        }

        Press(Escape);
        await Step($"Esc posted as the search opened, {wait.ElapsedMilliseconds} ms after the click");

        Click();
        await Step("click");
        Press(Escape);
        await Step("Esc");

        foreach (var key in new[] { NavKey.Activate, NavKey.Space })
        {
            Walk("search");
            // Long enough for the focused button's tooltip to show.
            await Step("on the button");
            island.Interaction.Key(key, false);
            await Step(key == NavKey.Activate ? "Enter" : "Space");
            Press(Escape);
            await Step("Esc");
        }

        island.Interaction.Key(NavKey.Tab4, false);
        await Step("Ctrl+4");
        Walk("settings");
        // On into the note, which takes the keys: Space and Enter are the note's and open nothing.
        island.Interaction.Key(NavKey.Next, false);
        await Step("Tab into the note");
        Press(Space);
        Press(Enter);
        await Step("Space and Enter");
        log($"debug strip: the note reads \"{Scratchpad.Text.Replace("\r", "\\r").Replace("\n", "\\n")}\"");

        // The island retracts after searches left with Esc: what they used is handed back once it has been hidden a moment.
        triggers.Hide();
        log($"debug strip: hidden, working set {Environment.WorkingSet / 1048576.0:F1} MB");
        await Task.Delay(5000);
        log($"debug strip: hidden 5 s, working set {Environment.WorkingSet / 1048576.0:F1} MB");

        // The hide handed the foreground back, so this search has no keys (the tray's menu would have given them); its
        // line shows whether Esc would go back or close.
        triggers.OpenSearch(ignorePause: true);
        await Step("the tray's Search");
        triggers.Hide();
        Windows.Win32.PInvoke.PostQuitMessage(0);

        void Click()
        {
            var card = TabStrip.Layout(Enum.GetValues<PanelId>(), PanelId.Home, (float)island.Current.Width, island.Mascot.Enabled).Search;
            var at = island.IslandToScreen(new Vector2(card.X + (card.Width / 2) - ((float)island.Current.Width / 2), card.Y + (card.Height / 2)));
            Windows.Win32.PInvoke.ScreenToClient(island.Hwnd, ref at);
            nint point = (at.Y << 16) | (at.X & 0xFFFF);
            Windows.Win32.PInvoke.PostMessage(island.Hwnd, Windows.Win32.PInvoke.WM_LBUTTONDOWN, 1, point);
            Windows.Win32.PInvoke.PostMessage(island.Hwnd, Windows.Win32.PInvoke.WM_LBUTTONUP, 0, point);
        }

        void Press(uint key)
        {
            Windows.Win32.PInvoke.PostMessage(island.Hwnd, Windows.Win32.PInvoke.WM_KEYDOWN, key, 1);
            Windows.Win32.PInvoke.PostMessage(island.Hwnd, Windows.Win32.PInvoke.WM_KEYUP, key, unchecked((nint)0xC0000001));
        }

        // The island settles and a tooltip shows before the line; the wait after it leaves time to capture the screen.
        async Task Step(string what)
        {
            await Task.Delay(1000);
            log($"debug strip: {what}: island {triggers.Machine.State}{(triggers.Searching ? $", searching, {(search?.View?.Leave is not null ? "Esc goes back" : "Esc closes")}" : "")}, "
                + $"focus on {island.Interaction.FocusedId ?? "nothing"}, bounds {island.TargetBounds}");
            await Task.Delay(800);
        }

        // Tab until the target, as many times as it takes, at most a dozen.
        void Walk(string to)
        {
            var seen = new List<string?>();
            for (int i = 0; i < 12 && island.Interaction.FocusedId != to; i++)
            {
                island.Interaction.Key(NavKey.Next, false);
                seen.Add(island.Interaction.FocusedId);
            }

            log($"debug strip: Tab to {to}: {string.Join(", ", seen)}");
        }
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
    /// --debug-keys (Down, Up, Tab, ShiftTab, Enter, CtrlEnter, CtrlShiftEnter, Esc; comma separated; WaitAi waits for an AI
    /// search to end and logs what it shows; ClickFolder clicks the selected row's show in folder button) and logs the
    /// selection after each, then hides the island. Opens nothing: a row
    /// Enter would open is logged instead; Search with AI (its row, or Ctrl+Enter) does run, and a row for one of the
    /// island's tabs does show it. --debug-race types the words and
    /// presses Enter at once, before Start's app list has been read, to show Enter waiting for it. --debug-everything name
    /// searches a named Everything instance instead of the usual one, and --debug-quit ends Tomte half a second after the
    /// island hid.
    /// </summary>
    private static async Task DebugSearch(IslandWindow island, TriggerController triggers, string words, string? keys, bool race, bool quit, Action<string> log)
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

            if (key == "ClickFolder")
            {
                ClickFolder();
                await Task.Delay(300);
                log($"debug search: ClickFolder done, island {triggers.Machine.State}");
                continue;
            }

            bool took = key switch
            {
                "Esc" => view.OnEscape(),
                "Enter" or "CtrlEnter" or "CtrlShiftEnter" => view.Press(0x0D, ctrl: key != "Enter", shift: key == "CtrlShiftEnter"),
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

        // A click on the selected row's show in folder button: the island window's own button messages, posted at the
        // button's centre, so the click goes through the real hit test.
        void ClickFolder()
        {
            string id = SearchView.FolderId + view.Rows.ToList().IndexOf(view.Selected!);
            if (view.Targets.FirstOrDefault(t => t.Id == id) is not { Id: not null } button)
            {
                log("debug search: the selected row has no folder button");
                return;
            }

            var at = island.IslandToScreen(new Vector2(button.Bounds.X + (button.Bounds.Width / 2) - ((float)island.Current.Width / 2), button.Bounds.Y + (button.Bounds.Height / 2)));
            Windows.Win32.PInvoke.ScreenToClient(island.Hwnd, ref at);
            nint point = (at.Y << 16) | (at.X & 0xFFFF);
            log($"debug search: clicking {button.Id} at {at.X},{at.Y} in the island window, tip \"{button.TipWith(keys: true)}\"");
            Windows.Win32.PInvoke.PostMessage(island.Hwnd, Windows.Win32.PInvoke.WM_LBUTTONDOWN, 1, point);
            Windows.Win32.PInvoke.PostMessage(island.Hwnd, Windows.Win32.PInvoke.WM_LBUTTONUP, 0, point);
        }
    }
}
