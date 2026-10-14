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
    private static HttpClient? debugHttp;
    private static PermissionGrants? debugGrants;

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
    /// What the palette hotkey shows. No provider can be set up in settings yet, so the palette says the AI layer is off
    /// and takes no keys, unless --debug-palette is given: then each open is a session against local Ollama, as the
    /// --palette driver uses, whose device tools only pretend and whose other effects are refused (nobody can confirm
    /// them until the palette draws a confirmation).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void OpenPalette(IslandWindow island, TriggerController triggers, SettingsStore store, Action<string>? log)
    {
        hotkeyPalette ??= new PaletteHost(island, triggers.Machine, line => log?.Invoke(line), text, focus, triggers.Hide);
        hotkeyPalette.Type(Environment.GetCommandLineArgs().Contains("--debug-palette") ? DebugSession(triggers, store) : null);
    }

    private static PaletteSession DebugSession(TriggerController triggers, SettingsStore store)
    {
        // Read now, while the island is still not the foreground window.
        nint foreground = WindowContext.Foreground();
        var (app, title) = WindowContext.Describe(foreground);
        var shelf = new InMemoryShelf();
        // The note's chip reads it at send; the rambler and the panel share this one instance.
        _ = Scratchpad.LoadAsync();
        var config = new ProviderConfig("ollama", ProviderKind.OpenAICompatible, "http://127.0.0.1:11434/v1", "llama3.2:3b");
        debugHttp ??= new HttpClient();
        // One per store for the process: each holds a handler on the store.
        debugGrants ??= new PermissionGrants(store);
        var tools = new BuiltInTools(shelf, Scratchpad, DesktopEffects.CopyToClipboard, DesktopEffects.OpenUrl, (n, i) => BuiltInTools.RunDevice(n, i, dryRun: true));
        List<ContextChip> chips =
        [
            ContextChips.Selection(null),
            ContextChips.Window(app, title),
            ContextChips.Screenshot(app, ct => WindowCapture.CapturePngAsync(foreground, ct)),
            ContextChips.Shelf(shelf),
            ContextChips.Scratchpad(Scratchpad),
        ];
        var session = new PaletteSession(ProviderSetup.Create(config, debugHttp, null), config, new PaletteOptions(), new SendLog(SendLogPath), tools, debugGrants, chips);
        // The palette is up, so the event waits in the triggers' queue and shows once it hides.
        session.DeviceActionExecuted += e => triggers.Raise(new IslandEvent(e.Line, DateTimeOffset.Now));
        return session;
    }
}
