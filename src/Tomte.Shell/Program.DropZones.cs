using System.Runtime.CompilerServices;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Triggers;

namespace Tomte.Shell;

/// <summary>The palette "Ask about it" opens.</summary>
internal static partial class Program
{
    /// <summary>
    /// "Ask about it", once the Palette state is open: the typing palette on a session to the provider set up in
    /// settings (its key, cap and prices), whose one chip is the dropped shelf item, on, with an empty prompt. The prompt
    /// box takes the keys; nothing is sent until the user asks. False when no provider in settings can be used. Called
    /// only after a drop on the zone, so nothing of the AI layer loads before.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool AskInPalette(IslandWindow island, TriggerController triggers, SettingsStore store, ShelfStore shelf, Action<string>? log, string item)
    {
        if (text is null || Configured(store, log) is not { } configured)
        {
            return false;
        }

        // The note's chip and tool read it at send; the rambler and the panel share this one instance.
        _ = Scratchpad.LoadAsync();
        paletteGrants ??= new PermissionGrants(store);
        var tools = new BuiltInTools(shelf, Scratchpad, DesktopEffects.CopyToClipboard, DesktopEffects.OpenUrl, timer: triggers.Timer);
        var session = DropZoneRequest.Ask(item, configured.Provider, configured.Config, configured.Options, new SendLog(SendLogPath), tools, paletteGrants);
        // The palette is up, so the event waits in the triggers' queue and shows once it hides.
        session.DeviceActionExecuted += e => triggers.Raise(new IslandEvent(e.Line, DateTimeOffset.Now));
        _ = DescribeAsync(session, configured.Provider, configured.Config);
        hotkeyPalette ??= new PaletteHost(island, triggers.Machine, line => log?.Invoke(line), text!, focus, triggers.Hide);
        hotkeyPalette.Type(session);
        return true;
    }
}
