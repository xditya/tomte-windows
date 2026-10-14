using System.Runtime.CompilerServices;
using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Triggers;

namespace Tomte.Shell;

/// <summary>The text boxes' focus and the palette hotkey's content.</summary>
internal static partial class Program
{
    private static TextRenderer? text;
    private static TypingFocus? focus;
    private static PaletteHost? hotkeyPalette;
    private static HttpClient? paletteHttp;
    private static PermissionGrants? paletteGrants;

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
    /// against local Ollama instead, as the --palette driver uses, whose device tools only pretend. Either way the tools
    /// that ask are refused until the palette draws a confirmation; reads, Tomte's own writes and the tools the user
    /// always allowed run.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void OpenPalette(IslandWindow island, TriggerController triggers, SettingsStore store, Action<string>? log)
    {
        hotkeyPalette ??= new PaletteHost(island, triggers.Machine, line => log?.Invoke(line), text, focus, triggers.Hide);
        hotkeyPalette.Type(Environment.GetCommandLineArgs().Contains("--debug-palette") ? DebugSession(triggers, store) : ConfiguredSession(triggers, store, log));
    }

    private static PaletteSession DebugSession(TriggerController triggers, SettingsStore store)
    {
        var config = new ProviderConfig("ollama", ProviderKind.OpenAICompatible, "http://127.0.0.1:11434/v1", "llama3.2:3b");
        return Session(triggers, store, ProviderSetup.Create(config, paletteHttp ??= new HttpClient(), null), config, new PaletteOptions(), dry: true);
    }

    /// <summary>The provider set up in settings, with its key from Credential Manager and its daily cap; null when none is.</summary>
    private static PaletteSession? ConfiguredSession(TriggerController triggers, SettingsStore store, Action<string>? log)
    {
        (IChatProvider Provider, ProviderConfig Config, PaletteOptions Options)? palette;
        try
        {
            palette = ProviderSetup.ForPalette(store.Current, paletteHttp ??= new HttpClient(), CredentialStore.Read);
        }
        catch (Exception e) when (e is InvalidOperationException or ArgumentException)
        {
            // Credential Manager would not read the key, or a hand-edited settings file has no address for the server.
            log?.Invoke($"palette: the provider in settings cannot be used: {e.Message}");
            return null;
        }

        if (palette is not { } configured)
        {
            return null;
        }

        var session = Session(triggers, store, configured.Provider, configured.Config, configured.Options, dry: false);
        _ = DescribeAsync(session, configured.Provider, configured.Config);
        return session;
    }

    // What a local Ollama says the model reads and takes, set when it answers. The palette does not wait for it: Windows
    // takes about 2 s to refuse a connection to a server that is not running, and the keys would reach the app in front
    // meanwhile. A send made before the answer uses the provider's defaults.
    private static async Task DescribeAsync(PaletteSession session, IChatProvider provider, ProviderConfig config) =>
        session.Model = await ProviderSetup.DescribeAsync(provider, config);

    private static PaletteSession Session(TriggerController triggers, SettingsStore store, IChatProvider provider, ProviderConfig config, PaletteOptions options, bool dry)
    {
        // Read now, while the island is still not the foreground window.
        nint foreground = WindowContext.Foreground();
        var (app, title) = WindowContext.Describe(foreground);
        var shelf = new InMemoryShelf();
        // The note's chip reads it at send; the rambler and the panel share this one instance.
        _ = Scratchpad.LoadAsync();
        // One per store for the process: each holds a handler on the store.
        paletteGrants ??= new PermissionGrants(store);
        var tools = new BuiltInTools(shelf, Scratchpad, DesktopEffects.CopyToClipboard, DesktopEffects.OpenUrl, dry ? (n, i) => BuiltInTools.RunDevice(n, i, dryRun: true) : null);
        List<ContextChip> chips =
        [
            ContextChips.Selection(null),
            ContextChips.Window(app, title),
            ContextChips.Screenshot(app, ct => WindowCapture.CapturePngAsync(foreground, ct)),
            ContextChips.Shelf(shelf),
            ContextChips.Scratchpad(Scratchpad),
        ];
        var session = new PaletteSession(provider, config, options, new SendLog(SendLogPath), tools, paletteGrants, chips);
        // The palette is up, so the event waits in the triggers' queue and shows once it hides.
        session.DeviceActionExecuted += e => triggers.Raise(new IslandEvent(e.Line, DateTimeOffset.Now));
        return session;
    }
}
