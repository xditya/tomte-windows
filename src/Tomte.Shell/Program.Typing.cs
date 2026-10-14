using System.Runtime.CompilerServices;
using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Triggers;

namespace Tomte.Shell;

/// <summary>The text boxes' focus and the palette hotkey's content.</summary>
internal static partial class Program
{
    // How long the palette waits for the UI Automation read of the selection before the prompt takes the keys, and with
    // them the focus that read asks about. Measured: 2 to 6 ms warm and up to 90 ms for the first call into an app.
    private static readonly TimeSpan SelectionWait = TimeSpan.FromMilliseconds(150);

    private static TextRenderer? text;
    private static TypingFocus? focus;
    private static PaletteHost? hotkeyPalette;
    private static HttpClient? paletteHttp;
    private static PermissionGrants? paletteGrants;

    /// <summary>The island's text renderer, made with the panels at start.</summary>
    internal static TextRenderer? Text => text;

    /// <summary>
    /// Who has the keys, made with the text renderer: Esc while typing hides the island, typing holds it open, and the
    /// palette hotkey fills the palette it opens. Nothing of the AI layer loads until that hotkey is pressed.
    /// </summary>
    private static TypingFocus StartTyping(IslandWindow island, TriggerController triggers, SettingsStore store, TextRenderer renderer, Action<string>? log)
    {
        text = renderer;
        focus = new TypingFocus(island, triggers.Machine, renderer, log) { Escape = triggers.Hide, Typing = triggers.Typing };
        triggers.PaletteOpened = () => OpenPalette(island, triggers, store, log);
        return focus;
    }

    /// <summary>
    /// What the palette hotkey shows: a session against the provider set up in settings, read at each open, or, with
    /// none, the line that says the AI layer is off, which takes no keys. --debug-palette makes each open a session
    /// against local Ollama instead, as the --palette driver uses, whose device tools only pretend. The selection in the
    /// app in front is read through UI Automation, off the island's thread, before the prompt takes the keys; a read
    /// slower than <see cref="SelectionWait"/> offers no selection.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async void OpenPalette(IslandWindow island, TriggerController triggers, SettingsStore store, Action<string>? log)
    {
        hotkeyPalette ??= new PaletteHost(island, triggers.Machine, line => log?.Invoke(line), text!, focus, triggers.Hide);
        bool debug = Environment.GetCommandLineArgs().Contains("--debug-palette");
        if ((debug ? DebugProvider() : Configured(store, log)) is not { } configured)
        {
            hotkeyPalette.Type(null);
            return;
        }

        // Read now, while the island is still not the foreground window.
        nint foreground = WindowContext.Foreground();
        var selection = Task.Run(SelectionReader.Read);
        // The Shelf chip names what is on the shelf, which is read on its first use: a few milliseconds, once. The load
        // reports its own failures, so the continuation only waits.
        var shelf = Jobs.Shelf?.Load().ContinueWith(_ => { }, TaskScheduler.Default) ?? Task.CompletedTask;
        await Task.WhenAll(shelf, Task.WhenAny(selection, Task.Delay(SelectionWait)));
        if (triggers.Machine.State != IslandState.Palette)
        {
            return;
        }

        var session = Session(triggers, store, configured, dry: debug, foreground, selection.IsCompletedSuccessfully ? selection.Result : null);
        if (!debug)
        {
            _ = DescribeAsync(session, configured.Provider, configured.Config);
        }

        hotkeyPalette.Type(session);
    }

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

    private static PaletteSession Session(TriggerController triggers, SettingsStore store, (IChatProvider Provider, ProviderConfig Config, PaletteOptions Options) configured, bool dry, nint foreground, string? selection)
    {
        var (app, title) = WindowContext.Describe(foreground);
        // The real shelf, so the Shelf chip offers what is on it and a file the AI makes stays there.
        IShelf shelf = (IShelf?)Jobs.Shelf ?? new InMemoryShelf();
        // The note's chip reads it at send; the rambler and the panel share this one instance.
        _ = Scratchpad.LoadAsync();
        // One per store for the process: each holds a handler on the store.
        paletteGrants ??= new PermissionGrants(store);
        var tools = new BuiltInTools(shelf, Scratchpad, DesktopEffects.CopyToClipboard, DesktopEffects.OpenUrl, dry ? (n, i) => BuiltInTools.RunDevice(n, i, dryRun: true) : null);
        var chips = ContextChips.For(null, selection, app, title, ct => WindowCapture.CapturePngAsync(foreground, ct), shelf, Scratchpad);
        var session = new PaletteSession(configured.Provider, configured.Config, configured.Options, new SendLog(SendLogPath), tools, paletteGrants, chips);
        // The palette is up, so the event waits in the triggers' queue and shows once it hides.
        session.DeviceActionExecuted += e => triggers.Raise(new IslandEvent(e.Line, DateTimeOffset.Now));
        return session;
    }
}
