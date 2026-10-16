using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics;
using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Device;
using Tomte.Core.Search;
using Tomte.Core.Shortcuts;
using Tomte.Core.Storage;
using Tomte.Core.Triggers;
using Windows.System;
using Windows.Win32;

namespace Tomte.Shell;

/// <summary>
/// Runs the search box: what it searches and what opening a row does. Apps, tiles and shelf items are ranked on the
/// island's thread at each keystroke, so they show at once; files come from Everything when it runs, else the Windows
/// index, off the island's thread after a short pause in typing, and a newer keystroke cancels an older search. Nothing
/// loads before the first search: the app list, the tiles, the shelf and what was opened before are read each time the
/// box opens (the last ones show meanwhile). Opening a row hides the island and lets what opens come to the front.
/// Island thread only.
/// </summary>
internal sealed class SearchHost
{
    // A pause in typing before a file search: Everything answers in a millisecond, the index takes a few hundred.
    private const int EverythingPauseMs = 30, IndexPauseMs = 150;

    // After the island hides from a search, this long (the fade, the morph and an app's start) before the working set is trimmed.
    private const int TrimAfterMs = 2000;
    private const string Install = "install", NotNow = "notnow", Download = "download", Close = "close";

    // Explorer by its full path, never a name looked up where something else could stand in for it.
    private static readonly string Explorer = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "explorer.exe");

    // Enter presses "Not now"; Install takes Tab to it, or a click.
    private static readonly SearchCard Confirm = new(
        "Get Everything for whole-disk search",
        "Tomte installs Everything (free, from voidtools) with winget. Windows asks for permission once. Everything then runs in the background, indexes every drive and starts when you sign in. Not now hides this row; Settings, Hotkeys keeps a Get Everything button.",
        [(NotNow, "Not now"), (Install, "Install")]);

    private static readonly SearchCard Installing = new(
        "Installing Everything",
        "winget is installing it now; Windows may ask for permission. You can close this: Tomte says when Everything is ready.",
        [(Close, "Close")]);

    private readonly IslandWindow island;
    private readonly TriggerController triggers;
    private readonly TextRenderer text;
    private readonly TypingFocus focus;
    private readonly SettingsStore store;
    private readonly ShortcutStore shortcuts;
    private readonly SearchHistory history;
    private readonly Action<string>? log;

    // Kept for the session: the next search shows them at once while they are read again.
    private IReadOnlyList<SearchItem> apps = [], tiles = [], shelf = [], opened = [];
    private IReadOnlyDictionary<string, Launches> launches = new Dictionary<string, Launches>();
    private FileSource source = FileSource.Index;
    private bool startedEverything, offerDismissed;
    private Task? installing;

    // Start's app list has not been read yet this session; Enter waits for it, held here with whether Ctrl was down.
    private bool appsPending = true;
    private bool? held;
    private DispatcherQueueTimer? trim;

    // A search opened since the last trim: the next time the island hides, whatever it went to from the search (the tab
    // it came from, the AI box), its pages are handed back.
    private bool searched;

    // This search's own.
    private SearchView? view;
    private IReadOnlyList<SearchItem> files = [];
    private CancellationTokenSource? fileSearch;
    private string filesFrom = "";

    // AI search for the text in the box: showing in the list's place, and its send while it runs and after (null when no
    // provider could be used).
    private bool aiShowing;
    private PaletteSession? ai;

    // An AI search that closed, on its way back with Undo: the next open shows it as it was.
    private Shut? restoring;

    public SearchHost(IslandWindow island, TriggerController triggers, TextRenderer text, TypingFocus focus, SettingsStore store, TomteDb db, ShortcutStore shortcuts, Action<string>? log)
    {
        (this.island, this.triggers, this.text, this.focus, this.store, this.shortcuts, this.log) = (island, triggers, text, focus, store, shortcuts, log);
        history = new SearchHistory(db);
        // The box lost the keys to another window: the search goes, unless the island is pinned.
        focus.Lost += () =>
        {
            if (triggers.Searching)
            {
                triggers.FocusLost();
            }
        };
        triggers.Machine.Changed += (_, to) =>
        {
            if (to == IslandState.Hidden && searched)
            {
                TrimLater();
            }
        };
    }

    /// <summary>The Ask row: the AI box opens with these words, and sends nothing until Enter there.</summary>
    public Action<string>? Ask { get; set; }

    /// <summary>Esc in a search opened from a tab: the island goes back to that tab.</summary>
    public Action? Back { get; set; }

    /// <summary>
    /// The Search with AI row: a send of the typed sentence to the AI box's provider, with its cap, send log and history,
    /// that picks files through the local file search. Null when no provider can be used. Made only when the row is chosen.
    /// </summary>
    public Func<string, PaletteSession?>? AiSearch { get; set; }

    /// <summary>The latest AI search, for the debug driver to wait on.</summary>
    public Task AiRun { get; private set; } = Task.CompletedTask;

    /// <summary>The view of the search showing now, for the debug driver; null with none.</summary>
    public SearchView? View => view;

    /// <summary>The debug driver's runs: a row that would open is logged and the island hides; nothing opens, nothing is counted.</summary>
    public bool Dry { get; set; }

    /// <summary>
    /// The search hotkey, the search button or the tray opened the Palette state as the search box: its content, and the
    /// box takes the keys.
    /// </summary>
    public void Open()
    {
        var (width, height) = triggers.SearchSize;
        var shown = view = new SearchView(island, triggers.Machine, text, focus, (float)width, (float)height);
        shown.Typed += _ => Typed();
        shown.Clicked += item => Chosen(item, folder: false);
        shown.Entered += Entered;
        shown.CardAnswered += Answered;
        shown.Back += LeaveAi;
        shown.Leave = triggers.FromPanels ? Back : null;
        shown.HeightChanged += () =>
        {
            if (view == shown && triggers.Searching)
            {
                _ = island.MorphTo(IslandGeometry.For(IslandState.Palette, width, shown.Height), SpringPreset.Gentle);
            }
        };
        triggers.Machine.Changed += Gone;
        (files, filesFrom) = restoring is { } back ? (back.Files, back.FilesFrom) : ([], "");
        held = null;
        trim?.Stop();
        searched = true;
        shown.TakeKeys();
        if (restoring is { } was)
        {
            // Undo: the text, the AI search with what arrived meanwhile, and the row and scroll it had.
            shown.Restore(was.Text);
            (ai, aiShowing) = (was.Ai, true);
            Refresh();
            shown.Reselect(was.Selected, was.Scroll);
        }
        else
        {
            Refresh();
        }

        _ = LoadAsync(shown);

        void Gone(IslandState from, IslandState to)
        {
            if (to != IslandState.Palette || !triggers.Searching)
            {
                triggers.Machine.Changed -= Gone;
                fileSearch?.Cancel();
                held = null;
                if (ai is { Holds: true } session)
                {
                    // AI search went to the provider: kept for Undo, its send going on meanwhile.
                    var shut = new Shut(session, shown.Text, shown.Selected?.Key, shown.Scroll, files, filesFrom);
                    (ai, aiShowing) = (null, false);
                    triggers.Closed(AiSearchClosed, () => Reopen(shut), session.Cancel);
                }
                else
                {
                    LeaveAi(refresh: false);
                }

                if (view == shown)
                {
                    view = null;
                }
            }
        }
    }

    /// <summary>What the undo pill says after an AI search closed.</summary>
    internal const string AiSearchClosed = "AI search closed";

    // Undo: the search box in the pill's place, as the tray's Search opens it, showing the AI search as it was.
    private bool Reopen(Shut shut)
    {
        restoring = shut;
        try
        {
            return triggers.OpenSearch(ignorePause: true);
        }
        finally
        {
            restoring = null;
        }
    }

    // Enter in the box. Before Start's app list has been read for the first time the best app is not known yet, so Enter
    // waits for it, then the list is ranked again and the top row opens; with the list known, the selected row opens.
    private void Entered(bool folder)
    {
        if (appsPending && !aiShowing)
        {
            held = folder;
            log?.Invoke("search: Enter waits for the app list");
            return;
        }

        if (view?.Selected is { } item)
        {
            Chosen(item, folder);
        }
    }

    // The search's loading, its view, its text box and the shell's app code leave pages behind that the hidden island does
    // not use: once the island has been hidden a moment, they are handed back. One call; nothing runs after it.
    private void TrimLater()
    {
        if (trim is null)
        {
            trim = island.Queue.CreateTimer();
            trim.IsRepeating = false;
            trim.Interval = TimeSpan.FromMilliseconds(TrimAfterMs);
            trim.Tick += (_, _) =>
            {
                if (triggers.Machine.State != IslandState.Hidden)
                {
                    return;
                }

                searched = false;
                long before = Environment.WorkingSet;
                bool trimmed = PInvoke.SetProcessWorkingSetSize(PInvoke.GetCurrentProcess(), nuint.MaxValue, nuint.MaxValue);
                log?.Invoke($"search: working set trimmed ({trimmed}) from {before / 1048576.0:F1} MB to {Environment.WorkingSet / 1048576.0:F1} MB");
            };
        }

        trim.Stop();
        trim.Start();
    }

    // Each list is read off this thread and shown as it arrives; one that cannot be read keeps what it had.
    private async Task LoadAsync(SearchView shown)
    {
        var watch = Stopwatch.StartNew();
        await Task.WhenAll(
            Apps(),
            Load(Tiles, list => tiles = list, "tiles"),
            Load(history.Load, seen => (opened, launches) = seen, "history"),
            Load(EverythingNow, now => source = now, "file source"),
            Shelf());
        log?.Invoke($"search: loaded {apps.Count} apps, {tiles.Count} tiles, {shelf.Count} shelf items, {opened.Count} opened before, files from {source}, in {watch.ElapsedMilliseconds} ms");

        // Start's apps. Once read (or failed), an Enter that waited for them opens the top row of the list ranked with them.
        async Task Apps()
        {
            try
            {
                await Load(() => AppList.Load().Select(a => new SearchItem(SearchKind.App, a.Name, a.Target, "", a.Target)).ToList(), list => apps = list, "apps");
            }
            finally
            {
                appsPending = false;
                if (held is bool folder && view == shown)
                {
                    held = null;
                    Refresh();
                    // Never the AI row: a send needs the row chosen, not an Enter typed before the list was known.
                    if (shown.Selected is { Kind: not SearchKind.AiSearch } top)
                    {
                        log?.Invoke("search: the app list is in; the held Enter opens the top row");
                        Chosen(top, folder);
                    }
                }
            }
        }

        // The shelf reads its rows on its own and hands them to this thread before its task ends.
        async Task Shelf()
        {
            if (Jobs.Shelf is { } store)
            {
                await store.Load();
                shelf = [.. store.Items.Where(i => !i.Missing).Select(i => new SearchItem(SearchKind.Shelf, i.Name, i.Path, Path.GetDirectoryName(i.Path) ?? "", i.Path, i.Path))];
                if (view == shown)
                {
                    Refresh();
                }
            }
        }

        async Task Load<T>(Func<T> read, Action<T> keep, string what)
        {
            try
            {
                var value = await Task.Run(read);
                keep(value);
                if (view == shown)
                {
                    Refresh();
                }
            }
            catch (Exception e) when (e is DbException or IOException or UnauthorizedAccessException or System.Runtime.InteropServices.COMException or Win32Exception or InvalidOperationException)
            {
                log?.Invoke($"search: {what} not read: {e.Message}");
            }
        }
    }

    // The tiles, from tomte.db, each with its target read for the icon and, for a file or folder, the place to show.
    private IReadOnlyList<SearchItem> Tiles() =>
        [.. shortcuts.Load().Select(tile =>
        {
            var target = ShortcutTarget.Read(tile.Target);
            string? place = target.Kind is ShortcutKind.File or ShortcutKind.Folder ? target.Open : null;
            return new SearchItem(SearchKind.Shortcut, tile.Name, tile.Id.ToString(System.Globalization.CultureInfo.InvariantCulture), tile.Target, target.Icon, place);
        })];

    // Where files come from now. Everything installed with its service but not running is started once a session, which
    // needs no admin rights; the next search uses it once its window is up.
    private FileSource EverythingNow()
    {
        var now = EverythingSetup.Source();
        if (now == FileSource.EverythingStopped && !startedEverything && EverythingSetup.Exe() is { } exe)
        {
            startedEverything = true;
            log?.Invoke($"search: starting Everything ({EverythingSetup.Start(exe)})");
        }

        return now;
    }

    private void Typed()
    {
        // An Enter held for the first app list was meant for the old text, and AI search's answer was for it too.
        held = null;
        LeaveAi(refresh: false);
        Refresh();
        fileSearch?.Cancel();
        fileSearch = null;
        var words = FileQuery.Words(view?.Text ?? "");
        if (words.Count > 0)
        {
            fileSearch = new CancellationTokenSource();
            _ = FilesAsync(words, fileSearch.Token);
        }
    }

    // The rows for the text in the box now: what matches, ranked, or with an empty box what was opened most.
    private void Refresh()
    {
        if (view is not { } shown)
        {
            return;
        }

        // Lists that arrive while AI search shows (the app list, files) wait for Esc.
        if (aiShowing)
        {
            ShowAi();
            return;
        }

        string typed = shown.Text;
        var words = FileQuery.Words(typed);
        if (words.Count == 0)
        {
            shown.Show(SearchRank.Recent(Known(), launches), Source(), SearchLayout.NothingYet);
            return;
        }

        // Files found for earlier text stay while they still hold every word, so the list does not flicker as one types on.
        var still = files.Where(f => words.All(w => f.Name.Contains(w, StringComparison.CurrentCultureIgnoreCase)));
        var candidates = apps.Concat(tiles).Concat(shelf).Concat(still);
        if (EverythingSetup.Offers(source, offerDismissed || store.Current.EverythingOfferDismissed) && installing is null)
        {
            candidates = candidates.Append(new SearchItem(SearchKind.GetEverything, "Search every drive", "", "Get Everything, free, from voidtools"));
        }

        if (store.Current.Provider is { } provider)
        {
            candidates = candidates
                .Append(new SearchItem(SearchKind.AiSearch, SearchLayout.AiRow, typed.Trim(), $"Sends file names to {ProviderSetup.NameOf(provider.Id)}, never what is in them"))
                .Append(new SearchItem(SearchKind.Ask, "Ask Tomte", typed.Trim(), $"\"{typed.Trim()}\""));
        }

        long ranking = Stopwatch.GetTimestamp();
        var ranked = SearchRank.AiFirst(SearchRank.Order(candidates, typed, launches), typed);
        log?.Invoke($"search: ranked {apps.Count + tiles.Count + shelf.Count + files.Count} candidates in {Stopwatch.GetElapsedTime(ranking).TotalMilliseconds:F2} ms");
        shown.Show(ranked, Source(), SearchLayout.NoResults);
    }

    // What was opened before, as it is now: an app still installed, a tile still there with its current name, a shelf
    // item still on the shelf; files as they were stored.
    private IEnumerable<SearchItem> Known()
    {
        var now = apps.Concat(tiles).Concat(shelf).DistinctBy(i => i.Key).ToDictionary(i => i.Key, StringComparer.Ordinal);
        foreach (var seen in opened)
        {
            if (now.TryGetValue(seen.Key, out var current))
            {
                yield return current;
            }
            else if (seen.Kind is SearchKind.File or SearchKind.Folder)
            {
                yield return new FileHit(seen.Target, seen.Kind == SearchKind.Folder).Item();
            }
        }
    }

    private string Source() => filesFrom.Length > 0 ? filesFrom : source switch
    {
        FileSource.Everything => "Files: Everything",
        FileSource.EverythingStopped => "Files: starting Everything",
        _ => "Files: Windows index",
    };

    private async Task FilesAsync(IReadOnlyList<string> words, CancellationToken cancel)
    {
        bool everything = Everything.Running;
        try
        {
            await Task.Delay(everything ? EverythingPauseMs : IndexPauseMs, cancel);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        var watch = Stopwatch.StartNew();
        (IReadOnlyList<FileHit>? Hits, bool Everything) found;
        try
        {
            found = await Task.Run(() => FileSearch.Run(new FileFilter(words), FileQuery.Fetch, cancel));
        }
        catch (Exception e) when (e is Win32Exception or InvalidOperationException or System.Runtime.InteropServices.COMException)
        {
            log?.Invoke($"search: file search failed: {e.Message}");
            return;
        }

        if (cancel.IsCancellationRequested || view is null)
        {
            return;
        }

        log?.Invoke($"search: {found.Hits?.Count.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "no"} files from {(found.Everything ? "Everything" : "the Windows index")} in {watch.ElapsedMilliseconds} ms");
        files = found.Hits?.Select(h => h.Item()).ToList() ?? [];
        filesFrom = found.Everything ? "Files: Everything" : found.Hits is null ? "Files: Windows Search is off" : "Files: Windows index";
        if (found.Everything)
        {
            source = FileSource.Everything;
        }

        Refresh();
    }

    // The Search with AI row was chosen: the sentence goes to the provider now, never before. The list shows the send's
    // progress, then the files it picked under its line on why, or what went wrong. Esc or typing goes back.
    private async Task RunAiAsync(string words)
    {
        // The local file search goes on: its files wait for Esc. An Enter held for the app list was for the local rows.
        held = null;
        LeaveAi(refresh: false);
        aiShowing = true;
        var session = ai = AiSearch?.Invoke(words);
        if (session is null)
        {
            log?.Invoke("search: AI search has no provider that can be used");
            ShowAi();
            return;
        }

        session.Changed += () =>
        {
            if (ai == session)
            {
                ShowAi();
            }
        };
        ShowAi();
        var watch = Stopwatch.StartNew();
        await session.SendAsync();
        var finder = session.FileSearch!;
        log?.Invoke($"search: AI search {session.Status} in {watch.ElapsedMilliseconds} ms, {finder.Calls} file searches, {finder.Found.Count} files returned, "
            + $"{finder.Picked?.Count ?? 0} picked{(session.Error is { } error ? ": " + error : "")}");
        if (ai == session)
        {
            ShowAi();
        }
    }

    // AI search's state in the list's place: progress while it runs, the picked files under the AI line once done, or a
    // plain line on what went wrong. Footer: where the words went.
    private void ShowAi()
    {
        if (view is not { } shown)
        {
            return;
        }

        if (ai is not { } session)
        {
            shown.Show([], "", "Search with AI needs an AI provider that works. Set one up in Settings, AI.", aiSearch: true);
            return;
        }

        var finder = session.FileSearch!;
        string sent = session.ProviderIndicator;
        switch (session.Status)
        {
            case PaletteStatus.Idle or PaletteStatus.Sending:
                string step = session.Answer.OfType<ToolStepSegment>().LastOrDefault()?.Summary is { } last ? $" {last}." : "";
                shown.Show([], sent, $"Searching with AI.{step} Esc goes back.", aiSearch: true);
                break;
            case PaletteStatus.Done when finder.Picked is { Count: > 0 } picked:
                shown.Show([.. picked.Select(f => new FileHit(f.Path, f.Folder).Item())], sent, "", aiSearch: true, why: finder.Reason.Length > 0 ? $"AI · {finder.Reason}" : "AI");
                break;
            case PaletteStatus.Done:
                string said = finder.Picked is not null ? finder.Reason : session.AnswerText.Trim();
                shown.Show([], sent, $"AI search found nothing that fits.{(said.Length > 0 ? " " + said : "")}", aiSearch: true);
                break;
            case PaletteStatus.Failed:
                shown.Show([], sent, session.Error ?? "AI search stopped.", aiSearch: true);
                break;
        }
    }

    // Back to the results for the typed text; a send still running is cancelled.
    private void LeaveAi() => LeaveAi(refresh: true);

    private void LeaveAi(bool refresh)
    {
        ai?.Cancel();
        ai = null;
        if (aiShowing)
        {
            aiShowing = false;
            if (refresh)
            {
                Refresh();
            }
        }
    }

    // An AI search as it closed: its send, the box's text, the selected row, the scroll, and the files found for the text.
    private sealed record Shut(PaletteSession Ai, string Text, string? Selected, float Scroll, IReadOnlyList<SearchItem> Files, string FilesFrom);

    private void Chosen(SearchItem item, bool folder)
    {
        switch (item.Kind)
        {
            case SearchKind.Ask when !Dry:
                Ask?.Invoke(item.Target);
                return;
            case SearchKind.AiSearch:
                AiRun = RunAiAsync(item.Target);
                return;
            case SearchKind.GetEverything:
                view?.ShowCard(Confirm);
                return;
        }

        if (Dry)
        {
            log?.Invoke($"search: would open {item.Kind} \"{item.Name}\"{(folder ? " in its folder" : "")}");
            triggers.Hide();
            return;
        }

        log?.Invoke($"search: opening {item.Kind}{(folder ? " in its folder" : "")}");
        // The island has the foreground now, from the hotkey: what opens next may take it. The island goes without handing
        // the foreground back, which would cover what opens.
        PInvoke.AllowSetForegroundWindow(unchecked((uint)-1));
        focus.Forget();
        triggers.Hide();
        _ = OpenAsync(item, folder);
    }

    private async Task OpenAsync(SearchItem item, bool folder)
    {
        string? failed = await Task.Run(() => Open(item, folder));
        if (failed is not null)
        {
            log?.Invoke($"search: not opened: {failed}");
            triggers.Raise(new IslandEvent(failed, DateTimeOffset.Now));
            return;
        }

        try
        {
            await Task.Run(() => history.Record(item, DateTimeOffset.UtcNow));
        }
        catch (Exception e) when (e is DbException or IOException or UnauthorizedAccessException)
        {
            log?.Invoke($"search: not counted: {e.Message}");
        }
    }

    // Opens the row, or shows it in its folder; null when it opened, else what went wrong in words for the user. Off the
    // island's thread: the shell can take a while, more so for a file on a slow drive.
    private string? Open(SearchItem item, bool folder)
    {
        try
        {
            if (folder && item.Location is { } place)
            {
                using var shown = Process.Start(new ProcessStartInfo(Explorer, $"/select,\"{place}\"") { UseShellExecute = true });
                return null;
            }

            switch (item.Kind)
            {
                case SearchKind.App:
                    var app = Apps.OpenApp(item.Target);
                    return app.Ok ? null : app.Message;
                case SearchKind.Shortcut:
                    var tile = shortcuts.Launch(long.Parse(item.Target, System.Globalization.CultureInfo.InvariantCulture));
                    return tile.Outcome == ShortcutOutcome.Opened ? null : tile.Message;
                default:
                    using (Process.Start(new ProcessStartInfo(item.Target) { UseShellExecute = true }))
                    {
                        return null;
                    }
            }
        }
        catch (Exception e) when (e is Win32Exception or DbException or IOException or UnauthorizedAccessException or InvalidOperationException or FormatException)
        {
            return $"Could not open {item.Name}: {e.Message}";
        }
    }

    // A card's button, or Esc on it (null).
    private void Answered(string? id)
    {
        switch (id)
        {
            case Install when installing is null:
                installing = InstallAsync();
                view?.ShowCard(Installing);
                break;
            case NotNow:
                // For good: the row goes now, and the setting keeps it gone; Settings keeps its Get Everything button.
                offerDismissed = true;
                view?.ShowCard(null);
                Refresh();
                _ = Task.Run(() =>
                {
                    try
                    {
                        store.Save(store.Current with { EverythingOfferDismissed = true });
                    }
                    catch (Exception e) when (e is IOException or UnauthorizedAccessException)
                    {
                        log?.Invoke($"search: \"Not now\" not saved: {e.Message}");
                    }
                });
                break;
            case Download:
                PInvoke.AllowSetForegroundWindow(unchecked((uint)-1));
                focus.Forget();
                triggers.Hide();
                _ = Task.Run(() =>
                {
                    try
                    {
                        using var page = Process.Start(new ProcessStartInfo(EverythingSetup.DownloadPage) { UseShellExecute = true });
                    }
                    catch (Win32Exception e)
                    {
                        log?.Invoke($"search: the download page did not open: {e.Message}");
                    }
                });
                break;
            default:
                view?.ShowCard(null);
                break;
        }
    }

    // winget runs off this thread; what it ends in shows on the search when it is still open, else as an island event.
    private async Task InstallAsync()
    {
        string? error;
        try
        {
            error = await Task.Run(() => EverythingSetup.InstallAsync(log, CancellationToken.None));
        }
        catch (OperationCanceledException)
        {
            error = "winget took more than ten minutes, so Tomte stopped waiting.";
        }

        installing = null;
        source = await Task.Run(EverythingSetup.Source);
        log?.Invoke($"search: Everything install ended: {error ?? "ready"}, files from {source}");
        if (view is { } shown)
        {
            shown.ShowCard(error is null ? null : new SearchCard("Everything was not installed", error + " You can get it from voidtools' website.", [(Close, "Close"), (Download, "Open the download page")]));
            Typed();
        }
        else
        {
            triggers.Raise(new IslandEvent(error ?? "Everything is ready: search covers every drive now.", DateTimeOffset.Now));
        }
    }
}
