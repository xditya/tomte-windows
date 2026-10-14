using System.Runtime.CompilerServices;
using Tomte.AI;
using Tomte.AI.Files;
using Tomte.AI.Palette;
using Tomte.AI.Skills;
using Tomte.Core;
using Tomte.Core.Storage;

namespace Tomte.Shell;

/// <summary>
/// The AI drop zones: whether the Shelf-drop surface has them (only with a provider set up in settings), and the drop
/// that lands on one. Such a drop is read inside Drop exactly as the shelf reads it and the item goes on the shelf;
/// everything after that (loading the AI layer, the panel, reading the file, sending it, the result files) runs in a
/// <see cref="ZoneRun"/> once Drop has returned to the source. Holds only shell and Core types and reads only the
/// settings, so making it at start, and a drag over it, loads nothing of the AI layer and reads no key, and the kept
/// results are not opened until a drop on a zone.
/// </summary>
internal sealed class DropZones
{
    private readonly IslandWindow island;
    private readonly TriggerController triggers;
    private readonly ShelfStore shelf;
    private readonly TextRenderer text;
    private readonly SettingsStore store;
    private readonly KeptResults results;
    private readonly Action<string>? log;

    public DropZones(IslandWindow island, TriggerController triggers, ShelfStore shelf, TextRenderer text, SettingsStore store, TomteDb db, Action<string>? log)
    {
        (this.island, this.triggers, this.shelf, this.text, this.store, this.log) = (island, triggers, shelf, text, store, log);
        results = new KeptResults(db, log);
        Surface = new ShelfDropSurface(island, triggers.Machine, triggers.Drops, text, Layout);
        triggers.DropOnZone = Take;
    }

    public ShelfDropSurface Surface { get; }

    /// <summary>The zones for the surface about to show: none without a provider that has a model.</summary>
    private DropZoneLayout? Layout() => DropZoneLayout.For(store.Current, IslandGeometry.ShelfDrop.Width, IslandGeometry.ShelfDrop.Depth);

    private bool Take(DroppedItems drop)
    {
        if (Surface.Layout is null || Surface.Hot is not { } zone || zone == DropZone.Shelf || !DropZoneLayout.HasProvider(store.Current))
        {
            return false;
        }

        // What the source may take back after Drop returns is read here, as for any drop on the shelf.
        var accepted = shelf.Accept(drop);
        bool fromText = drop.Text is not null && drop.Paths.Count + drop.Files.Count == 0;
        log?.Invoke($"drop zone: {zone}, {drop.Paths.Count + drop.Files.Count + (drop.Text is null ? 0 : 1)} items");
        // The rest waits until Drop has returned: the source is not held while the AI layer loads and the island morphs.
        island.Queue.TryEnqueue(() => Run(zone, accepted, fromText));
        return true;
    }

    // Kept out of Take so the AI layer loads with the first drop on a zone, not before.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Run(DropZone zone, Task<IReadOnlyList<ShelfItem>> accepted, bool fromText) =>
        _ = new ZoneRun(island, triggers, shelf, text, store, results, log).RunAsync(zone, accepted, fromText);
}

/// <summary>
/// One drop on a zone, from the drop to its result: the panel opens, the item is on the shelf, and a result this file
/// already has from this zone shows first, with a choice to run it again. Otherwise, or on that choice, the file is
/// read and sent as a palette send of its own to the provider set up in settings (its key, cap and prices, the
/// readers, the size question and the send log), what comes back goes on the shelf as files the panel shows, and the
/// result is kept. The island stays out while the send runs, while a question waits for an answer, and then while the
/// result shows, until the user dismisses it; Esc, or anything else that takes the island down, ends the send and
/// answers a question with cancel. On the island's thread; the reading and the sending await on the pool.
/// </summary>
internal sealed class ZoneRun(IslandWindow island, TriggerController triggers, ShelfStore shelf, TextRenderer text, SettingsStore store, KeptResults results, Action<string>? log)
{
    private const string NoProvider = "The provider in settings cannot be used. Set it up again in Settings. Nothing was sent.";
    private static PermissionGrants? grants;
    private DropResultView view = null!;
    private TaskCompletionSource<SizeChoice>? asking;
    private TaskCompletionSource<bool>? offering;
    private SizeQuestion? question;
    private string? skillName;
    private bool queued;

    public async Task RunAsync(DropZone zone, Task<IReadOnlyList<ShelfItem>> accepted, bool fromText)
    {
        if (zone == DropZone.Ask)
        {
            await AskAsync(accepted);
            return;
        }

        view = new DropResultView(island, triggers, shelf, text, log);
        string title = DropZoneLayout.Label(zone, store.Current.DropZoneSkill);
        PaletteSession? session = null;
        view.Closed += () =>
        {
            session?.Cancel();
            asking?.TrySetResult(SizeChoice.Cancel);
            offering?.TrySetResult(false);
        };
        view.Chose += i =>
        {
            if (offering is not null)
            {
                offering.TrySetResult(i == 0);
            }
            else if (question is not null && asking is not null && i < DropZoneRequest.Choices(question).Count)
            {
                var choice = DropZoneRequest.Choices(question)[i].Choice;
                question = null;
                asking.TrySetResult(choice);
            }
        };
        Show(new DropResult(title, "Nothing sent", "Putting it on the shelf", false, "", [], []), busy: true);
        try
        {
            var items = await accepted;
            if (!view.Open)
            {
                return;
            }

            if (items.Count != 1)
            {
                Finish(new DropResult(title, "Nothing sent", items.Count == 0 ? "It could not be put on the shelf, so nothing was sent." : $"One file at a time: the {items.Count} items are on the shelf, and nothing was sent.", true, "", [], []), ok: false);
                return;
            }

            var item = items[0];
            skillName = zone == DropZone.Skill ? store.Current.DropZoneSkill : null;
            // Paid for once: what this file already has from this zone shows first, and nothing is sent unless asked.
            // Running it again goes to the provider in settings now, which the choice names.
            var ready = Program.Configured(store, log);
            var kept = await results.Last(zone, skillName, item.Path);
            if (!view.Open || (kept is not null && !await Offer(title, kept, ready?.Config.DefaultModel)))
            {
                return;
            }

            if (ready is not { } configured)
            {
                Finish(new DropResult($"{title} · {item.Name}", "Nothing sent", NoProvider, true, "", [], []), ok: false);
                return;
            }

            var (provider, config, options) = configured;
            var model = await ProviderSetup.DescribeAsync(provider, config);
            // Esc while the model was looked up: nothing is sent into a panel that is gone.
            if (!view.Open)
            {
                return;
            }

            SkillSet? skill = null;
            if (zone == DropZone.Skill)
            {
                string? picked = skillName;
                skill = await Task.Run(() => SkillSet.Picked(SkillSet.FolderFor(store.FilePath), store.Current.EnabledSkills, picked));
                if (!view.Open)
                {
                    return;
                }

                if (skill.DropZones is not [var spec, ..])
                {
                    Finish(new DropResult($"{title} · {item.Name}", "Nothing sent", $"The {picked} skill is off or has changed since it was turned on. Turn it on in Settings, Skills. Nothing was sent.", true, "", [], []), ok: false);
                    return;
                }

                title = spec.Label;
                if (!DropZoneRequest.Accepts(spec.Accepts, item.Name, item.IsFolder, fromText))
                {
                    Finish(new DropResult($"{title} · {item.Name}", "Nothing sent", $"{title} takes {string.Join(", ", spec.Accepts)}. Nothing was sent.", true, "", [], []), ok: false);
                    return;
                }
            }

            grants ??= new PermissionGrants(store);
            var tools = new BuiltInTools(shelf, new InMemoryScratchpad(), DesktopEffects.CopyToClipboard, DesktopEffects.OpenUrl);
            var running = session = DropZoneRequest.Session(zone, title, item.Name, provider, config, options, NewSendLog(), tools, grants, skill, model);
            running.ChooseSize = (q, _) =>
            {
                // A panel that is already gone answers cancel, since nothing would ever answer this question.
                if (!view.Open)
                {
                    return Task.FromResult(SizeChoice.Cancel);
                }

                // Asked in the panel after the drop; it waits for a choice, and Esc answers cancel, the default.
                question = DropZoneRequest.Asked(zone, q);
                asking = new TaskCompletionSource<SizeChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
                Update(running, item.Name, title);
                return asking.Task;
            };
            running.Changed += () => Update(running, item.Name, title);
            // Before the file is read: an edit during the send makes it a new file, not one this answer is about.
            string? stamp = await Task.Run(() => KeptResults.Stamp(item.Path));
            island.Mascot.Set(MascotState.Thinking);
            decimal before = running.SpentTodayUsd;
            await running.SendAsync();
            if (!view.Open)
            {
                return;
            }

            await Done(zone, running, item, title, config.Id, running.SpentTodayUsd - before, stamp);
        }
        catch (Exception e)
        {
            // Nothing escapes into the dispatcher; the panel says it failed.
            log?.Invoke($"drop zone: failed: {e}");
            if (view.Open)
            {
                Finish(new DropResult(title, session?.ProviderIndicator ?? "Nothing sent", "Something went wrong. Nothing more was sent.", true, "", [], []), ok: false);
            }
        }
    }

    /// <summary>
    /// "Ask about it": once the item is on the shelf, which takes a moment while the surface stays, the Palette state
    /// opens with the typing palette on a session to the configured provider that has the item's chip on and an empty
    /// prompt; its prompt box takes the keys and nothing is sent until the user asks. Without a usable provider this
    /// zone's panel says so.
    /// </summary>
    private async Task AskAsync(Task<IReadOnlyList<ShelfItem>> accepted)
    {
        if (await accepted is not [var item, ..] || triggers.Machine.State != IslandState.ShelfDrop)
        {
            return;
        }

        triggers.ZonePalette(PanelSize.PaletteMinHeight, busy: false, hold: false);
        if (!Program.AskInPalette(island, triggers, store, shelf, log, item.Name))
        {
            view = new DropResultView(island, triggers, shelf, text, log);
            Finish(new DropResult($"{DropZoneLayout.Label(DropZone.Ask, null)} · {item.Name}", "Nothing sent", NoProvider, true, "", [], []), ok: false);
        }
    }

    /// <summary>
    /// The result this file already has from this zone, before anything is sent, with its files that are still on the
    /// shelf. True when the user chose to run it again; false when they closed it, which takes the island down, or
    /// dismissed it some other way.
    /// </summary>
    private async Task<bool> Offer(string title, KeptResult kept, string? model)
    {
        log?.Invoke($"drop zone: {kept.Zone} result kept from {kept.At:O}, shown");
        var files = shelf.Items.Where(i => kept.Files.Contains(i.Path, StringComparer.OrdinalIgnoreCase)).ToList();
        offering = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Finish(DropZoneRequest.Kept(title, kept, [.. files.Select(ResultTile.Of)], model), files, ok: true);
        bool again = await offering.Task;
        offering = null;
        if (!view.Open)
        {
            return false;
        }

        if (!again)
        {
            triggers.Hide();
            return false;
        }

        Show(new DropResult($"{title} · {kept.SourceName}", "Nothing sent", $"Reading {kept.SourceName}", false, "", [], []), busy: true);
        return true;
    }

    private SendLog NewSendLog() => new(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(store.FilePath))!, "send-log.jsonl"));

    /// <summary>The result: its files on the shelf, or the renamed copy, and the panel shows them; then it is kept.</summary>
    private async Task Done(DropZone zone, PaletteSession session, ShelfItem item, string title, string provider, decimal cost, string? stamp)
    {
        if (session.Status != PaletteStatus.Done)
        {
            Finish(DropZoneRequest.Panel(title, item.Name, session, null), ok: session.Status == PaletteStatus.Cancelled);
            return;
        }

        if (zone == DropZone.Rename)
        {
            // The user's own file is only read: the new name goes to a copy of it on the shelf.
            string? name = DropZoneRequest.SuggestedName(session.AnswerText, item.Name);
            var copy = name is null ? null : await shelf.AddCopy(item.Path, name);
            IReadOnlyList<ShelfItem> kept = copy is null ? [] : [copy];
            string line = name is null ? "No name came back that a file can have, so nothing was copied."
                : copy is null ? "The copy could not be made. Nothing changed."
                : $"A copy is on the shelf. {item.Name} keeps its own name.";
            Finish(DropZoneRequest.Panel(title, item.Name, session, null, line, name is null ? null : $"Suggested name: {name}", [.. kept.Select(ResultTile.Of)]),
                kept, ok: copy is not null);
            Keep(kept);
            return;
        }

        var files = DropZoneRequest.Files(zone, item.Name, session.AnswerText, zone == DropZone.Skill ? store.Current.DropZoneSkill : null);
        var added = files.Count == 0 ? [] : await shelf.Add([.. files.Select(f => new ShelfEntry(ShelfKind.Content, f.Name, Content: f.Content))]);
        string outcome = added.Count switch
        {
            0 when zone == DropZone.Tables => "No table came back, so there is no CSV.",
            0 => "Nothing came back to keep.",
            1 => "Kept on the shelf.",
            var n => $"{n} files kept on the shelf.",
        };
        Finish(DropZoneRequest.Panel(title, item.Name, session, null, outcome, null, [.. added.Select(ResultTile.Of)]), added, ok: true);
        Keep(added);

        // Every finished answer was paid for, files or not; the next drop of this file on this zone shows it first.
        void Keep(IReadOnlyList<ShelfItem> files) => _ = results.Keep(new KeptResult(zone, skillName, item.Name, item.Path, provider, session.ModelId, cost,
            DateTimeOffset.Now, session.AnswerText, [.. files.Select(f => f.Path)]), stamp);
    }

    /// <summary>Session changes come in bursts while the answer streams; the panel follows once per turn of the dispatcher.</summary>
    private void Update(PaletteSession session, string item, string title)
    {
        if (queued)
        {
            return;
        }

        queued = true;
        island.Queue.TryEnqueue(() =>
        {
            queued = false;
            if (view.Open && session.Status is PaletteStatus.Sending or PaletteStatus.Idle)
            {
                // An open size question holds the island like the send does, until it is answered or Esc cancels it.
                Show(DropZoneRequest.Panel(title, item, session, question), busy: true);
            }
        });
    }

    private void Show(DropResult result, bool busy, IReadOnlyList<ShelfItem>? items = null)
    {
        view.Show(result, items ?? []);
        triggers.ZonePalette(view.Height, busy);
    }

    private void Finish(DropResult result, bool ok) => Finish(result, [], ok);

    /// <summary>The last state of the panel, which holds the island until the user dismisses it.</summary>
    private void Finish(DropResult result, IReadOnlyList<ShelfItem> items, bool ok)
    {
        if (!view.Open)
        {
            return;
        }

        Show(result, busy: false, items);
        island.Mascot.Set(ok ? MascotState.Result : MascotState.Error);
    }
}
