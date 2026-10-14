using System.Runtime.CompilerServices;
using Tomte.AI;
using Tomte.AI.Files;
using Tomte.AI.Palette;
using Tomte.AI.Skills;
using Tomte.Core;

namespace Tomte.Shell;

/// <summary>
/// The AI drop zones: whether the Shelf-drop surface has them (only with a provider set up), and the drop that lands
/// on one. Such a drop is read inside Drop exactly as the shelf reads it, the item goes on the shelf, and everything
/// after that (reading the file, sending it, the result files) runs in a <see cref="ZoneRun"/> off the drop. Holds
/// only shell and Core types, so making it at start loads nothing of the AI layer.
/// </summary>
internal sealed class DropZones
{
    private readonly IslandWindow island;
    private readonly TriggerController triggers;
    private readonly ShelfStore shelf;
    private readonly TextRenderer text;
    private readonly SettingsStore store;
    private readonly Func<ProviderConfig?> provider;
    private readonly Action<string>? log;

    public DropZones(IslandWindow island, TriggerController triggers, ShelfStore shelf, TextRenderer text, SettingsStore store, Func<ProviderConfig?> provider, Action<string>? log)
    {
        (this.island, this.triggers, this.shelf, this.text, this.store, this.provider, this.log) = (island, triggers, shelf, text, store, provider, log);
        Surface = new ShelfDropSurface(island, triggers.Machine, triggers.Drops, text, Layout);
        triggers.DropOnZone = Take;
    }

    public ShelfDropSurface Surface { get; }

    /// <summary>The zones for the surface about to show: none without a provider that has a model.</summary>
    private DropZoneLayout? Layout() =>
        provider() is { DefaultModel: not null } config
            ? DropZoneLayout.For(config.Id, store.Current.DropZoneSkill, IslandGeometry.ShelfDrop.Width, IslandGeometry.ShelfDrop.Depth)
            : null;

    private bool Take(DroppedItems drop)
    {
        if (Surface.Layout is null || Surface.Hot is not { } zone || zone == DropZone.Shelf || provider() is not { DefaultModel: not null } config)
        {
            return false;
        }

        // What the source may take back after Drop returns is read here, as for any drop on the shelf.
        var accepted = shelf.Accept(drop);
        log?.Invoke($"drop zone: {zone}, {drop.Paths.Count + drop.Files.Count + (drop.Text is null ? 0 : 1)} items");
        Run(zone, accepted, drop.Text is not null && drop.Paths.Count + drop.Files.Count == 0, config);
        return true;
    }

    // Kept out of Take so the AI layer loads with the first drop on a zone, not before.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private void Run(DropZone zone, Task<IReadOnlyList<ShelfItem>> accepted, bool fromText, ProviderConfig config) =>
        _ = new ZoneRun(island, triggers, shelf, text, store, log).RunAsync(zone, accepted, fromText, config);
}

/// <summary>
/// One drop on a zone, from the drop to its result: the panel opens at once, the item goes on the shelf, then the
/// file is read and sent as a palette send of its own (its readers, size question, spend cap and send log), and what
/// comes back goes on the shelf as files the panel shows. Leaving the panel ends the send and answers an open size
/// question with cancel. On the island's thread; the reading and the sending await on the pool.
/// </summary>
internal sealed class ZoneRun(IslandWindow island, TriggerController triggers, ShelfStore shelf, TextRenderer text, SettingsStore store, Action<string>? log)
{
    private static HttpClient? http;
    private static PermissionGrants? grants;
    private DropResultView view = null!;
    private TaskCompletionSource<SizeChoice>? asking;
    private SizeQuestion? question;
    private bool queued;

    public async Task RunAsync(DropZone zone, Task<IReadOnlyList<ShelfItem>> accepted, bool fromText, ProviderConfig config)
    {
        if (zone == DropZone.Ask)
        {
            await AskAsync(accepted, config);
            return;
        }

        view = new DropResultView(island, triggers, shelf, text, log);
        string title = DropZoneLayout.Label(zone, store.Current.DropZoneSkill);
        PaletteSession? session = null;
        view.Closed += () =>
        {
            session?.Cancel();
            asking?.TrySetResult(SizeChoice.Cancel);
        };
        view.Chose += i =>
        {
            if (question is not null && asking is not null && i < DropZoneRequest.Choices(question).Count)
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
            var (provider, model, sendLog, tools) = await Connect(config);
            // Esc while the model was looked up: nothing is sent into a panel that is gone.
            if (!view.Open)
            {
                return;
            }

            SkillSet? skill = null;
            if (zone == DropZone.Skill)
            {
                string? picked = store.Current.DropZoneSkill;
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

            var running = session = DropZoneRequest.Session(zone, title, item.Name, provider, config, new PaletteOptions(), sendLog, tools, grants!, skill, model);
            running.ChooseSize = (q, _) =>
            {
                // Asked in the panel after the drop; leaving the panel answers it with cancel, the default.
                question = DropZoneRequest.Asked(zone, q);
                asking = new TaskCompletionSource<SizeChoice>(TaskCreationOptions.RunContinuationsAsynchronously);
                Update(running, item.Name, title);
                return asking.Task;
            };
            running.Changed += () => Update(running, item.Name, title);
            island.Mascot.Set(MascotState.Thinking);
            await running.SendAsync();
            if (!view.Open)
            {
                return;
            }

            await Done(zone, running, item, title);
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
    /// opens with a session that has the item's chip on and an empty prompt, and nothing is sent until the user asks.
    /// The palette that can be typed in shows it when there is one; otherwise this zone's panel says what is attached.
    /// </summary>
    private async Task AskAsync(Task<IReadOnlyList<ShelfItem>> accepted, ProviderConfig config)
    {
        if (await accepted is not [var item, ..] || triggers.Machine.State != IslandState.ShelfDrop)
        {
            return;
        }

        var (provider, model, sendLog, tools) = await Connect(config, describe: false);
        var ask = DropZoneRequest.Ask(item.Name, provider, config, new PaletteOptions(), sendLog, tools, grants!, model);
        triggers.ZonePalette(PanelSize.PaletteMinHeight, busy: false);
        if (Program.AskInPalette(island, triggers, log, ask))
        {
            return;
        }

        view = new DropResultView(island, triggers, shelf, text, log);
        Finish(new DropResult($"{DropZoneLayout.Label(DropZone.Ask, null)} · {item.Name}", ask.ProviderIndicator, $"{ask.Chips[0].Label} is attached. Nothing is sent until you ask.", false, $"Ask about {item.Name}", [], []), ok: true);
    }

    /// <summary>
    /// The provider with its key from Credential Manager, what a local Ollama says about the model, the send log beside
    /// the settings file and the tools over the real shelf. A zone's send is offered none of the tools; Ask's palette is.
    /// </summary>
    private async Task<(IChatProvider Provider, ModelInfo? Model, SendLog Log, BuiltInTools Tools)> Connect(ProviderConfig config, bool describe = true)
    {
        http ??= new HttpClient();
        grants ??= new PermissionGrants(store);
        var provider = ProviderSetup.Load(config, http);
        var model = describe ? await ProviderSetup.DescribeAsync(provider, config) : null;
        var sendLog = new SendLog(Path.Combine(Path.GetDirectoryName(Path.GetFullPath(store.FilePath))!, "send-log.jsonl"));
        return (provider, model, sendLog, new BuiltInTools(shelf, new InMemoryScratchpad(), DesktopEffects.CopyToClipboard, DesktopEffects.OpenUrl));
    }

    /// <summary>The result: its files on the shelf, or the renamed copy, and the panel shows them.</summary>
    private async Task Done(DropZone zone, PaletteSession session, ShelfItem item, string title)
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
                kept, ok: copy is not null, done: $"A renamed copy of {item.Name} is on the shelf");
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
        string done = added.Count == 1 ? $"{added[0].Name} is on the shelf" : $"{added.Count} files from {item.Name} are on the shelf";
        Finish(DropZoneRequest.Panel(title, item.Name, session, null, outcome, null, [.. added.Select(ResultTile.Of)]), added, ok: true, done);
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
                // While the size question is open nothing is in flight: leaving the panel then cancels, as the default does.
                Show(DropZoneRequest.Panel(title, item, session, question), busy: question is null);
            }
        });
    }

    private void Show(DropResult result, bool busy, IReadOnlyList<ShelfItem>? items = null, string? done = null)
    {
        view.Show(result, items ?? []);
        triggers.ZonePalette(view.Height, busy, done);
    }

    private void Finish(DropResult result, bool ok) => Finish(result, [], ok);

    /// <summary>The last state of the panel. <paramref name="done"/> is the event line for files kept, shown if the pointer is away.</summary>
    private void Finish(DropResult result, IReadOnlyList<ShelfItem> items, bool ok, string? done = null)
    {
        if (!view.Open)
        {
            return;
        }

        Show(result, busy: false, items, items.Count > 0 ? done : null);
        island.Mascot.Set(ok ? MascotState.Result : MascotState.Error);
    }
}
