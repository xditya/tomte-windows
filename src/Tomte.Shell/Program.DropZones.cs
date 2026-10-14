using System.Runtime.CompilerServices;
using Tomte.AI.Palette;
using Tomte.Core;

namespace Tomte.Shell;

/// <summary>Where the AI drop zones send to, and the palette "Ask about it" opens.</summary>
internal static partial class Program
{
    /// <summary>
    /// The provider the drop zones send to; null draws no zone, so the drop surface is the shelf's alone. Until
    /// providers can be set up in settings it is the one a run names on its command line, as the rambler reads it.
    /// </summary>
    private static ProviderConfig? ZoneProvider { get; set; }

    /// <summary>--provider-id id [--provider-kind Anthropic|Gemini|OpenAICompatible] [--base-url url] [--model id]; the key is read from Credential Manager under the id.</summary>
    private static ProviderConfig? CommandLineProvider(string[] args) =>
        Arg(args, "--provider-id") is { } id
            ? new ProviderConfig(id, Enum.Parse<ProviderKind>(Arg(args, "--provider-kind") ?? nameof(ProviderKind.OpenAICompatible)), Arg(args, "--base-url"), Arg(args, "--model"))
            : null;

    /// <summary>
    /// "Ask about it" in the palette that can be typed in, once the Palette state is open: its prompt box takes the keys
    /// and the session's chip is the dropped file. False while the palette has no prompt box to type in; the zone's own
    /// panel then says what is attached. Called only after a drop on the zone, so nothing of the AI layer loads before.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static bool AskInPalette(IslandWindow island, TriggerController triggers, Action<string>? log, PaletteSession session) => false;
}
