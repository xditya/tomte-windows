using System.ComponentModel;
using System.Diagnostics;
using Tomte.Core;
using Tomte.Core.Drop;

namespace Tomte.Shell;

/// <summary>
/// One Tomte Drop, from the choice to the card closing: the island opens in the Palette state on a drop result card,
/// the share starts on the pool, and the card shows its QR code with the link and what it offers beside it, the warning
/// that the phone must be on this Wi-Fi, and a line that follows what the phone does. A file the phone sends goes on
/// the shelf where it was saved, and the newest show as tiles, which a drag takes out and a double click opens; text
/// goes on the shelf as the shelf keeps text. The share lives exactly as long as the card: Stop sharing, Esc, or anything else that
/// takes the island out of the Palette state ends it, and a share that stopped by itself turns the card to Close. Needs
/// no provider and loads nothing of the AI layer; a copied row loads the assembly the clipboard code is in, as the
/// shelf's Copy path does. On the island's thread; the share's events are posted to it.
/// </summary>
internal sealed class DropRun
{
    private readonly IslandWindow island;
    private readonly TriggerController triggers;
    private readonly ShelfStore shelf;
    private readonly Action<string>? log;
    private readonly DropResultView view;

    // The newest files that came from the phone, behind the card's tiles in the same order.
    private readonly List<ShelfItem> got = [];
    private DropResult card = DropCard.Opening;
    private DropShare? share;
    private TimeSpan idle;

    private DropRun(IslandWindow island, TriggerController triggers, ShelfStore shelf, TextRenderer text, Action<string>? log)
    {
        (this.island, this.triggers, this.shelf, this.log) = (island, triggers, shelf, log);
        view = new DropResultView(island, triggers, shelf, text, log);
        view.Chose += Chose;
        view.ValueChosen += CopyValue;
        // The share goes with the card, whichever way the island leaves it.
        view.Closed += End;
        shelf.Changed += OnShelf;
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

        _ = new DropRun(island, triggers, shelf, text, log).RunAsync(offered, none);
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
                var options = new DropOptions(DropOptions.DefaultReceiveFolder, Environment.MachineName, accent);
                var running = DropShare.Start(files, options, out string? why, log);
                return (running, why, options, files.Sum(f => new FileInfo(f.Path) is { Exists: true } file ? file.Length : 0), files.Any(f => Directory.Exists(f.Path)));
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

            (share, idle) = (started, options.Idle);
            started.Activity += activity => island.Queue.TryEnqueue(() => On(activity));
            log?.Invoke($"tomte drop: sharing {files.Count} items{(started.PublicNetwork ? ", the network is set to Public" : "")}");
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

    /// <summary>What the phone did, on the island's thread: the line says it, and what it sent goes on the shelf.</summary>
    private void On(DropActivity activity)
    {
        if (share is null || !view.Open)
        {
            return;
        }

        switch (activity.Kind)
        {
            case DropActivityKind.Stopped:
                End();
                Finish(DropCard.Stopped(card, idle));
                return;
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
        if (device is null || added is not [var item, ..] || !view.Open)
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

    // Network settings opens Windows' page for the network; Stop sharing and Close take the island down, and the share with it.
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

        triggers.Hide();
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
        triggers.ZonePalette(view.Height, busy: false);
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
        if (share is { } running)
        {
            share = null;
            log?.Invoke("tomte drop: stopped");
            // Off the island's thread: ending the transfers under way may take a moment.
            _ = Task.Run(running.Dispose);
        }
    }
}
