using System.Runtime.CompilerServices;
using Tomte.AI;
using Tomte.AI.Automation;
using Tomte.AI.Mcp;
using Tomte.AI.Palette;
using Tomte.AI.Skills;
using Tomte.Core;
using Tomte.Core.Triggers;

namespace Tomte.Shell;

/// <summary>The text boxes' focus and the palette hotkey's content.</summary>
internal static partial class Program
{
    // How long the palette waits for the UI Automation read of the selection and for the skills before the prompt takes
    // the keys, and with them the focus that read asks about. Measured: 2 to 6 ms warm and up to 90 ms for the
    // first call into an app.
    private static readonly TimeSpan SelectionWait = TimeSpan.FromMilliseconds(150);

    private static TextRenderer? text;
    private static TypingFocus? focus;
    private static PaletteHost? hotkeyPalette;
    private static HttpClient? paletteHttp;
    private static PermissionGrants? paletteGrants;

    // The conversation the History tab's Continue asked for, taken by the palette that opens next.
    private static AiExchange? continuing;

    /// <summary>The island's text renderer, made with the panels at start.</summary>
    internal static TextRenderer? Text => text;

    /// <summary>
    /// Who has the keys, made with the text renderer: Esc while typing hides the island, typing holds it open, and the
    /// palette hotkey fills the palette it opens. Nothing of the AI layer loads until that hotkey is pressed.
    /// </summary>
    private static TypingFocus StartTyping(IslandWindow island, TriggerController triggers, SettingsStore store, TextRenderer renderer, Action<string>? log)
    {
        text = renderer;
        focus = new TypingFocus(island, triggers.Machine, renderer, log) { Escape = triggers.Hide, Typing = triggers.Typing, Pin = triggers.TogglePin };
        triggers.PaletteOpened = async () =>
        {
            try
            {
                await OpenPalette(island, triggers, store, log);
            }
            catch (Exception e)
            {
                NotOpened(triggers, log, e);
            }
        };
        return focus;
    }

    /// <summary>
    /// The tray's "Ask AI": the palette about the window the user last worked in, since the menu and the taskbar have had
    /// the foreground after it. A palette already up, which the menu took the keys from, has its prompt take them again.
    /// </summary>
    private static async void AskFromTray(IslandWindow island, TriggerController triggers, SettingsStore store, Action<string>? log)
    {
        // An exception leaving an async void ends the process, so the whole of it is guarded.
        try
        {
            if (triggers.Machine.State == IslandState.Palette)
            {
                hotkeyPalette?.TakeKeys();
                return;
            }

            if (triggers.Ask())
            {
                await OpenPalette(island, triggers, store, log, front: triggers.LastApp);
            }
        }
        catch (Exception e)
        {
            NotOpened(triggers, log, e);
        }
    }

    /// <summary>
    /// The History tab's Continue: the AI box as the Ask pill opens it, with the conversation loaded and an empty prompt.
    /// Nothing is sent until Enter, which sends the conversation's text with the new question.
    /// </summary>
    private static void ContinueInPalette(TriggerController triggers, AiExchange conversation)
    {
        continuing = conversation;
        triggers.OpenPalette();
        continuing = null;
    }

    // The AI box goes back to the tab it came from. Back by a key (Esc, Alt+Left, Enter on the arrow) keeps the keys the
    // island had, for that tab's targets; back by a click lets them go with the prompt, so the tab retracts as usual.
    private static void GoBack(IslandWindow island, TriggerController triggers)
    {
        bool keys = focus?.HasKeys == true && island.Interaction.UsingKeys;
        triggers.Back();
        if (keys)
        {
            focus!.TakeKeys();
        }
    }

    // A palette that failed to open would leave the island in the Palette state with nothing in it.
    private static void NotOpened(TriggerController triggers, Action<string>? log, Exception e)
    {
        log?.Invoke($"palette: not opened: {e}");
        if (triggers.Machine.State == IslandState.Palette)
        {
            triggers.Hide();
        }
    }

    /// <summary>
    /// Circle-to-ask after the lasso: the circled region cropped from its monitor, then the palette the hotkey opens,
    /// with the region as its first chip, on, and the prompt box ready for the question. Kept out of Main so Tomte.AI
    /// loads only on the first capture.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task CaptureToPalette(IslandWindow island, TriggerController triggers, SettingsStore store, Action<string>? log, CaptureOverlay overlay, PixelRect crop, nint foreground)
    {
        // Cropped before the palette opens, so the island is never in the picture.
        var png = await WindowCapture.CaptureMonitorRegionPngAsync(overlay.Monitor, crop, CancellationToken.None);
        var (w, h) = overlay.Lasso.BoxDips;
        // Esc, another hotkey or the lock screen ended the capture meanwhile.
        if (triggers.Machine.State != IslandState.Capture)
        {
            return;
        }

        triggers.PaletteFromCapture();
        try
        {
            await OpenPalette(island, triggers, store, log, ContextChips.Lasso(png, w, h), foreground);
        }
        catch (Exception e)
        {
            NotOpened(triggers, log, e);
        }
    }

    /// <summary>
    /// What the palette hotkey shows: a session against the provider set up in settings, read at each open, or, with
    /// none, the line that says the AI layer is off, which takes no keys. --debug-palette makes each open a session
    /// against local Ollama instead, as the --palette driver uses, whose device tools only pretend. The selection in the
    /// app in front is read through UI Automation, off the island's thread, before the prompt takes the keys; a read
    /// slower than <see cref="SelectionWait"/> offers no selection. Circle-to-ask passes its region and the window that
    /// was in front before its overlay, the tray the window the user last worked in (0 for none), and no selection is
    /// read then.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task OpenPalette(IslandWindow island, TriggerController triggers, SettingsStore store, Action<string>? log, ContextChip? lasso = null, nint? front = null)
    {
        // Read before the first wait: Continue sets it only for the open it starts.
        var earlier = continuing;
        hotkeyPalette ??= NewPaletteHost(island, triggers, log);
        // Opened from the panels, the AI box has a back arrow to the tab it came from; on the next turn, after the key or click.
        Action? back = triggers.FromPanels ? () => island.Queue.TryEnqueue(() => GoBack(island, triggers)) : null;
        bool debug = Environment.GetCommandLineArgs().Contains("--debug-palette");
        if ((debug ? DebugProvider() : Configured(store, log)) is not { } configured)
        {
            hotkeyPalette.Type(null, back);
            return;
        }

        // Read now, while the island is still not the foreground window.
        nint foreground = front ?? WindowContext.Foreground();
        var selection = front is null ? Task.Run(SelectionReader.Read) : Task.FromResult<string?>(null);
        // The app's name as people know it, for what the chips say: its exe's version details, a file read, so off this
        // thread too and in the same wait; the process name stands in when it is late.
        var product = Task.Run(() => WindowContext.Product(foreground, WindowContext.Describe(foreground).App));
        // Skills come from beside the settings file on each open, only those turned on, so one added or turned on since
        // the last open is there; their text goes to the provider only with a send.
        var skills = Task.Run(() => SkillSet.Load(SkillSet.FolderFor(store.FilePath), store.Current.EnabledSkills));
        // The Shelf chip names what is on the shelf, which is read on its first use: a few milliseconds, once. The load
        // reports its own failures, so the continuation only waits.
        var shelf = Jobs.Shelf?.Load().ContinueWith(_ => { }, TaskScheduler.Default) ?? Task.CompletedTask;
        // Keys typed before the prompt takes them go to the app in front, so the selection and the skills get the same
        // short wait; what is not read by then is left out of this open.
        await Task.WhenAll(shelf, Task.WhenAny(Task.WhenAll(selection, skills, product), Task.Delay(SelectionWait)));
        if (triggers.Machine.State != IslandState.Palette)
        {
            return;
        }

        if (!skills.IsCompletedSuccessfully)
        {
            log?.Invoke($"palette: skills left out: {(skills.IsFaulted ? skills.Exception!.InnerException?.Message : $"not read within {SelectionWait.TotalMilliseconds:0} ms")}");
        }

        var session = Session(triggers, store, configured, dry: debug, foreground, selection.IsCompletedSuccessfully ? selection.Result : null,
            skills.IsCompletedSuccessfully ? skills.Result : null, lasso, product.IsCompletedSuccessfully ? product.Result : null, earlier);
        if (!debug)
        {
            _ = DescribeAsync(session, configured.Provider, configured.Config);
        }

        hotkeyPalette.Type(session, back);
    }

    /// <summary>
    /// The typed palette's host, whose footer button opens the History tab on the next turn, after the click. It goes
    /// when it loses the keys, unless the island is pinned.
    /// </summary>
    private static PaletteHost NewPaletteHost(IslandWindow island, TriggerController triggers, Action<string>? log) =>
        new(island, triggers.Machine, line => log?.Invoke(line), text!, focus, triggers.FocusLost)
        {
            OpenHistory = () => island.Queue.TryEnqueue(() => triggers.OpenPanel(PanelId.History)),
        };

    private static (IChatProvider Provider, ProviderConfig Config, PaletteOptions Options) DebugProvider()
    {
        var config = new ProviderConfig("ollama", ProviderKind.OpenAICompatible, "http://127.0.0.1:11434/v1", "llama3.2:3b");
        return (ProviderSetup.Create(config, paletteHttp ??= ProviderSetup.NewHttp(), null), config, new PaletteOptions());
    }

    /// <summary>
    /// The provider set up in settings, with its key from Credential Manager, its cap and its prices, for the palette,
    /// voice commands and new jobs. Null when there is none, or, with the reason logged, when it cannot be used.
    /// </summary>
    internal static (IChatProvider Provider, ProviderConfig Config, PaletteOptions Options)? Configured(SettingsStore store, Action<string>? log)
    {
        // With no provider there is no HTTP client either.
        if (store.Current.Provider is null)
        {
            return null;
        }

        try
        {
            return ProviderSetup.ForPalette(store.Current, paletteHttp ??= ProviderSetup.NewHttp(), CredentialStore.Read, log);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            // Credential Manager would not read the key, or a hand-edited settings file has no address for the server.
            log?.Invoke($"palette: the provider in settings cannot be used: {e.Message}");
            return null;
        }
    }

    // What a local Ollama says the model reads and takes, set when it answers. The palette does not wait for it: Windows
    // takes about 2 s to refuse a connection to a server that is not running, and the keys would reach the app in front
    // meanwhile. A send made before the answer uses the provider's defaults.
    private static async Task DescribeAsync(PaletteSession session, IChatProvider provider, ProviderConfig config) =>
        session.Model = await ProviderSetup.DescribeAsync(provider, config);

    /// <summary>
    /// The hotkey palette's session: the chips, the built-in tools, the skills read at this open, the user's MCP servers
    /// (started on the first send, stopped when the palette closes) and, when Settings turned it on, UI Automation on the
    /// window that was in front, every list and action of it asked.
    /// </summary>
    private static PaletteSession Session(TriggerController triggers, SettingsStore store, (IChatProvider Provider, ProviderConfig Config, PaletteOptions Options) configured, bool dry, nint foreground, string? selection, SkillSet? skills, ContextChip? lasso, string? product, AiExchange? earlier)
    {
        var (app, title) = WindowContext.Describe(foreground);
        // The real shelf, so the Shelf chip offers what is on it and a file the AI makes stays there.
        IShelf shelf = (IShelf?)Jobs.Shelf ?? new InMemoryShelf();
        // The note's chip reads it at send; the rambler and the panel share this one instance.
        _ = Scratchpad.LoadAsync();
        // One per store for the process: each holds a handler on the store.
        paletteGrants ??= new PermissionGrants(store);
        var tools = new BuiltInTools(shelf, Scratchpad, DesktopEffects.CopyToClipboard, DesktopEffects.OpenUrl, dry ? (n, i) => BuiltInTools.RunDevice(n, i, dryRun: true) : null, triggers.Timer);
        var chips = ContextChips.For(lasso, selection, app, title, ct => WindowCapture.CapturePngAsync(foreground, ct), shelf, Scratchpad, product);
        var settings = store.Current;
        var session = new PaletteSession(configured.Provider, configured.Config, configured.Options, new SendLog(SendLogPath), tools, paletteGrants, chips)
        {
            Skills = skills,
            History = History,
            Continuing = earlier,
            Mcp = McpTools.For(settings.McpServers),
            // With no window to point them at (the tray's Ask AI with none remembered) the tools are not offered.
            Automation = AutomationTools.For(settings.UiAutomation.Enabled && foreground != 0, settings.UiAutomation, settings.AppRules, foreground, () => triggers.OnSecureSurface),
        };
        // The palette is up, so the event waits in the triggers' queue and shows once it hides.
        session.DeviceActionExecuted += e => triggers.Raise(new IslandEvent(e.Line, DateTimeOffset.Now));
        return session;
    }
}
