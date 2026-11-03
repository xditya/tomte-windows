using System.ComponentModel;
using System.Diagnostics;
using Tomte.Core;
using Tomte.Core.Drop;
using Tomte.Core.Triggers;

namespace Tomte.Shell;

/// <summary>
/// One Tomte Drop, from the choice to the card closing: the island opens in the Palette state on a drop result card,
/// the share starts on the pool, and the card shows its QR code with the link and what it offers beside it, the warning
/// that the phone must be on this Wi-Fi, and a line that follows what the phone does. A file the phone sends goes on
/// the shelf where it was saved, and the newest show as tiles, which a drag takes out and a double click opens; text
/// goes on the shelf as the shelf keeps text. Only Stop sharing ends the share, or nothing asked of a share that offers
/// files for a while; one that only receives stays until stopped. Esc, a click elsewhere or anything else that takes
/// the island closes the card and leaves the share running, one at a time: the shelf's Tomte Drop button and the tray
/// bring the card back, and what the phone sends meanwhile shows as an event pill. Needs
/// no provider and loads nothing of the AI layer; a copied row loads the assembly the clipboard code is in, as the
/// shelf's Copy path does. On the island's thread; the share's events are posted to it.
/// </summary>
internal sealed class DropRun
{
    // The share that runs, with its card up or not.
    private static DropRun? running;

    private readonly IslandWindow island;
    private readonly TriggerController triggers;
    private readonly ShelfStore shelf;
    private readonly TextRenderer text;
    private readonly Action<string>? log;

    // The card while it is up; a new one each time it comes back.
    private DropResultView view = null!;

    // The newest files that came from the phone, behind the card's tiles in the same order.
    private readonly List<ShelfItem> got = [];
    private DropResult card = DropCard.Opening;
    private DropShare? share;
    private TimeSpan idle;

    private DropRun(IslandWindow island, TriggerController triggers, ShelfStore shelf, TextRenderer text, Action<string>? log)
    {
        (this.island, this.triggers, this.shelf, this.text, this.log) = (island, triggers, shelf, text, log);
        NewView();
        shelf.Changed += OnShelf;
    }

    /// <summary>A share runs, with its card up or not.</summary>
    public static bool Running => running is not null;

    /// <summary>
    /// The running share's card, or with none a share that only receives: the tray's Tomte Drop. Whatever the island
    /// shows goes first, as for the tray's Settings, and the card opens in the Palette state the way Undo brings one back.
    /// </summary>
    public static void Receive(IslandWindow island, TriggerController triggers, ShelfStore shelf, TextRenderer text, Action<string>? log)
    {
        if (running?.view.Open == true)
        {
            return;
        }

        triggers.Hide();
        if (!triggers.Reopen(PanelSize.PaletteMinHeight))
        {
            return;
        }

        if (!Reopen())
        {
            Start(island, triggers, shelf, text, Task.FromResult<IReadOnlyList<DropFile>>([]), log);
        }
    }

    /// <summary>Brings back the running share's card; false when no share runs.</summary>
    public static bool Reopen()
    {
        if (running is not { } run)
        {
            return false;
        }

        if (!run.view.Open)
        {
            // First, as in Start: on the way from Panels the state passes through Hidden.
            run.triggers.ZonePalette(PanelSize.PaletteMinHeight, busy: false, sharing: true);
            if (run.triggers.Machine.State != IslandState.Palette)
            {
                return true;
            }

            run.NewView();
            run.Show();
            run.island.Mascot.Set(MascotState.Result);
        }

        return true;
    }

    private void NewView()
    {
        view = new DropResultView(island, triggers, shelf, text, log);
        view.Chose += Chose;
        view.ValueChosen += CopyValue;
        view.Closed += Closed;
    }

    /// <summary>
    /// The card in the Palette state, from the Shelf-drop state or from Panels, and the share once
    /// <paramref name="offered"/> is known. Nothing offered is a share that only receives, unless <paramref name="none"/>
    /// says why that is a problem.
    /// </summary>
    public static void Start(IslandWindow island, TriggerController triggers, ShelfStore shelf, TextRenderer text, Task<IReadOnlyList<DropFile>> offered, Action<string>? log,
        string? none = null)
    {
        // First: from Panels the state passes through Hidden, which would close a view made before it.
        triggers.ZonePalette(PanelSize.PaletteMinHeight, busy: false);
        if (triggers.Machine.State != IslandState.Palette)
        {
            return;
        }

        // One share at a time: a new one replaces the one that runs.
        running?.End();
        _ = new DropRun(island, triggers, shelf, text, log).RunAsync(offered, none);
    }

    /// <summary>The shelf items a Tomte Drop offers: those whose file is still there, by their names on the shelf.</summary>
    public static IReadOnlyList<DropFile> Offered(IEnumerable<ShelfItem> items) =>
        [.. items.Where(i => ShelfStore.Allows(ShelfAction.Drop, i)).Select(i => new DropFile(i.Name, i.Path))];

    /// <summary>The same, once a drop's items are on the shelf.</summary>
    public static async Task<IReadOnlyList<DropFile>> Offered(Task<IReadOnlyList<ShelfItem>> items) => Offered(await items);

    /// <summary>A saved set's files that are still where they were, looked at on the pool; the shelf does not change.</summary>
    public static async Task<IReadOnlyList<DropFile>> Offered(Task<IReadOnlyList<ShelfEntry>> members)
    {
        var all = await members;
        return await Task.Run(() => (IReadOnlyList<DropFile>)[.. all.Where(m => File.Exists(m.Path) || Directory.Exists(m.Path)).Select(m => new DropFile(m.Name, m.Path!))]);
    }

    private async Task RunAsync(Task<IReadOnlyList<DropFile>> offered, string? none)
    {
        Show();
        island.Mascot.Set(MascotState.Result);
        try
        {
            var files = await offered;
            if (!view.Open)
            {
                return;
            }

            if (files.Count == 0 && none is not null)
            {
                Finish(DropCard.Problem(none));
                return;
            }

            uint accent = Theme.Accent & 0xFFFFFF;
            // Start reads the network's category and binds a port; the sizes are read from the disk with it.
            var (started, problem, options, bytes, folders) = await Task.Run(() =>
            {
                // A share that only receives offers nothing to forget about: it stays until stopped.
                var options = new DropOptions(DropOptions.DefaultReceiveFolder, Environment.MachineName, accent) { Idle = files.Count == 0 ? Timeout.InfiniteTimeSpan : DropOptions.DefaultIdle };
                var begun = DropShare.Start(files, options, out string? why, log);
                return (begun, why, options, files.Sum(f => new FileInfo(f.Path) is { Exists: true } file ? file.Length : 0), files.Any(f => Directory.Exists(f.Path)));
            });
            if (!view.Open)
            {
                // Closed while it started: it never shows.
                started?.Dispose();
                return;
            }

            if (started is null)
            {
                Finish(DropCard.Problem(problem ?? DropCard.CouldNotStart));
                return;
            }

            (share, idle, running) = (started, options.Idle, this);
            started.Activity += activity => island.Queue.TryEnqueue(() => On(activity));
            log?.Invoke($"tomte drop: sharing {files.Count} items{(started.PublicNetwork ? ", the firewall blocks phones on this Public network" : "")}");
            card = DropCard.Sharing(files.Count, folders, bytes, started.Link, started.Qr, started.PublicNetwork);
            Show();
        }
        catch (Exception e)
        {
            // Nothing escapes into the dispatcher; the card says it did not start.
            log?.Invoke($"tomte drop: failed: {e}");
            if (view.Open)
            {
                End();
                Finish(DropCard.Problem(DropCard.CouldNotStart));
            }
        }
    }

    /// <summary>
    /// What the phone did, on the island's thread: the line says it, and what it sent goes on the shelf. A phone that
    /// opened the page got through, so the Public network warning goes. With the card closed what it sent shows as an
    /// event pill.
    /// </summary>
    private void On(DropActivity activity)
    {
        if (share is null)
        {
            return;
        }

        if (!view.Open && DropCard.Pill(activity) is { } pill)
        {
            triggers.Raise(new IslandEvent(pill, DateTimeOffset.Now));
        }

        switch (activity.Kind)
        {
            case DropActivityKind.Stopped:
                End();
                Finish(DropCard.Stopped(card, idle));
                return;
            case DropActivityKind.Opened when card.Warning is not null:
                card = DropCard.Reached(card);
                break;
            case DropActivityKind.Received when activity.Path is { } path:
                // Where the share saved it: the shelf points there and copies nothing.
                _ = Keep(new ShelfEntry(ShelfKind.Reference, Path.GetFileName(path), path), activity.Device);
                break;
            case DropActivityKind.Text when activity.Text is { Length: > 0 } words:
                var (name, bytes) = ShelfFormats.FromText(words);
                _ = Keep(new ShelfEntry(ShelfKind.Content, name, Content: bytes), null);
                break;
        }

        card = card with { Line = DropCard.Line(activity) ?? card.Line };
        Show();
    }

    /// <summary>Something from the phone onto the shelf; a file shows as a tile too, from <paramref name="device"/>.</summary>
    private async Task Keep(ShelfEntry entry, string? device)
    {
        var added = await shelf.Add([entry]);
        if (device is null || added is not [var item, ..])
        {
            return;
        }

        got.Add(item);
        List<ResultTile> tiles = [.. card.Tiles, new ResultTile(item.Name, DropCard.From(device), ShelfLayout.KindColor(item))];
        if (got.Count > DropCard.MostTiles)
        {
            // The oldest makes room; it is on the shelf with the rest.
            got.RemoveAt(0);
            tiles.RemoveAt(0);
        }

        card = card with { Tiles = tiles };
        Show();
    }

    // A tile whose file left the shelf, by a drag out or on the Shelf tab, says so, and keeps saying it as the card changes.
    private void OnShelf()
    {
        var tiles = card.Tiles.Select((t, i) => i < got.Count && !shelf.Items.Any(s => s.Id == got[i].Id) ? t with { Meta = ResultTile.OffShelf } : t).ToList();
        if (!tiles.SequenceEqual(card.Tiles))
        {
            card = card with { Tiles = tiles };
            Show();
        }
    }

    // Network settings opens Windows' page for the network; Stop sharing ends the share, and it and Close take the island down.
    private void Chose(int i)
    {
        if (i < card.Choices.Count && card.Choices[i] == DropCard.NetworkSettings)
        {
            try
            {
                Process.Start(new ProcessStartInfo("ms-settings:network-status") { UseShellExecute = true })?.Dispose();
            }
            catch (Exception e) when (e is Win32Exception or InvalidOperationException)
            {
                log?.Invoke($"tomte drop: network settings did not open: {e.Message}");
            }

            return;
        }

        End();
        triggers.Hide();
    }

    // The card went: a share that runs keeps running for the card to come back; one that never started or has ended is done.
    private void Closed()
    {
        if (share is null)
        {
            End();
        }
    }

    // A value row puts its text on the clipboard and the card stays.
    private void CopyValue(int i)
    {
        string id = DropResultLayout.ValuePrefix + i;
        if (i >= card.Values.Count)
        {
            return;
        }

        try
        {
            ShelfPanel.CopyText(card.Values[i]);
            island.Interaction.Acknowledge(view, id);
        }
        catch (InvalidOperationException e)
        {
            // Another app kept the clipboard open.
            log?.Invoke($"tomte drop: not copied ({e.Message})");
            island.Interaction.Acknowledge(view, id, "Another app has the clipboard open", tick: false);
        }
    }

    // Drawn only while the card is up.
    private void Show()
    {
        if (!view.Open)
        {
            return;
        }

        view.Show(card, got);
        // While the share runs a click elsewhere leaves the card up; once it has ended the card is a result like any other.
        triggers.ZonePalette(view.Height, busy: false, sharing: share is not null);
        // After the change to the Palette state, which seats the tomte by the prompt box: this card's row is its head.
        island.Seat = DropResultLayout.Seat;
    }

    /// <summary>The card's last state, which holds the island until Close or Esc.</summary>
    private void Finish(DropResult result)
    {
        card = result;
        Show();
        if (view.Open)
        {
            island.Mascot.Set(result.Problem ? MascotState.Error : MascotState.Result);
        }
    }

    /// <summary>The share stops, once; the link stops working.</summary>
    private void End()
    {
        shelf.Changed -= OnShelf;
        if (running == this)
        {
            running = null;
        }

        if (share is { } stopping)
        {
            share = null;
            log?.Invoke("tomte drop: stopped");
            // Off the island's thread: ending the transfers under way may take a moment.
            _ = Task.Run(stopping.Dispose);
        }
    }
}
