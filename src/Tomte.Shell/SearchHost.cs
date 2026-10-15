using System.ComponentModel;
using System.Data.Common;
using System.Diagnostics;
using Tomte.Core;
using Tomte.Core.Device;
using Tomte.Core.Search;
using Tomte.Core.Shortcuts;
using Tomte.Core.Storage;
using Tomte.Core.Triggers;
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
    private const string Install = "install", Cancel = "cancel", Download = "download", Close = "close";

    private static readonly SearchCard Confirm = new(
        "Get Everything for whole-disk search",
        "Tomte installs Everything (free, from voidtools) with winget. Windows asks for permission once. Everything then runs in the background, indexes every drive and starts when you sign in.",
        [(Cancel, "Cancel"), (Install, "Install")]);

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
    private bool startedEverything;
    private Task? installing;

    // This search's own.
    private SearchView? view;
    private IReadOnlyList<SearchItem> files = [];
    private CancellationTokenSource? fileSearch;
    private string filesFrom = "";

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
    }

    /// <summary>The Ask row: the AI box opens with these words, and sends nothing until Enter there.</summary>
    public Action<string>? Ask { get; set; }

    /// <summary>The view of the search showing now, for the debug driver; null with none.</summary>
    public SearchView? View => view;

    /// <summary>The search hotkey opened the Palette state as the search box: its content, and the box takes the keys.</summary>
    public void Open()
    {
        var (width, height) = triggers.SearchSize;
        var shown = view = new SearchView(island, triggers.Machine, text, focus, (float)width, (float)height);
        shown.Typed += _ => Typed();
        shown.Chosen += Chosen;
        shown.CardAnswered += Answered;
        shown.HeightChanged += () =>
        {
            if (view == shown && triggers.Searching)
            {
                _ = island.MorphTo(IslandGeometry.For(IslandState.Palette, width, shown.Height), SpringPreset.Gentle);
            }
        };
        triggers.Machine.Changed += Gone;
        files = [];
        filesFrom = "";
        shown.TakeKeys();
        Refresh();
        _ = LoadAsync(shown);

        void Gone(IslandState from, IslandState to)
        {
            if (to != IslandState.Palette || !triggers.Searching)
            {
                triggers.Machine.Changed -= Gone;
                fileSearch?.Cancel();
                if (view == shown)
                {
                    view = null;
                }
            }
        }
    }

    // Each list is read off this thread and shown as it arrives; one that cannot be read keeps what it had.
    private async Task LoadAsync(SearchView shown)
    {
        var watch = Stopwatch.StartNew();
        await Task.WhenAll(
            Load(() => AppList.Load().Select(a => new SearchItem(SearchKind.App, a.Name, a.Target, "", a.Target)).ToList(), list => apps = list, "apps"),
            Load(Tiles, list => tiles = list, "tiles"),
            Load(history.Load, seen => (opened, launches) = seen, "history"),
            Load(EverythingNow, now => source = now, "file source"),
            Shelf());
        log?.Invoke($"search: loaded {apps.Count} apps, {tiles.Count} tiles, {shelf.Count} shelf items, {opened.Count} opened before, files from {source}, in {watch.ElapsedMilliseconds} ms");

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
        if (source == FileSource.Index && installing is null)
        {
            candidates = candidates.Append(new SearchItem(SearchKind.GetEverything, "Search every drive", "", "Get Everything, free, from voidtools"));
        }

        if (store.Current.Provider is not null)
        {
            candidates = candidates.Append(new SearchItem(SearchKind.Ask, "Ask Tomte", typed.Trim(), $"\"{typed.Trim()}\""));
        }

        long ranking = Stopwatch.GetTimestamp();
        var ranked = SearchRank.Order(candidates, typed, launches);
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
            found = await Task.Run(() =>
            {
                var excluded = FileQuery.Excluded();
                return everything && Tomte.Core.Search.Everything.Search(FileQuery.Everything(words, excluded), FileQuery.Fetch, cancel) is { } hits
                    ? ((IReadOnlyList<FileHit>?)hits, true)
                    : (WindowsIndex.Search(words, cancel), false);
            });
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

    private void Chosen(SearchItem item, bool folder)
    {
        switch (item.Kind)
        {
            case SearchKind.Ask:
                Ask?.Invoke(item.Target);
                return;
            case SearchKind.GetEverything:
                view?.ShowCard(Confirm);
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
                using var shown = Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{place}\"") { UseShellExecute = true });
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
