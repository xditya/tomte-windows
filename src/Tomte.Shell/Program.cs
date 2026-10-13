using Tomte.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Shell;

internal static class Program
{
    private const int CycleHotKey = 1;
    private const int QuitHotKey = 2;

    // Until the triggers exist, --debug-cycle walks this legal path through the state table, one hotkey press per step.
    private static readonly (IslandState State, double W, double H)[] DebugCycle =
    [
        (IslandState.Compact, 0, 0),
        (IslandState.Panels, PanelSize.Shelf.W, PanelSize.Shelf.H),
        (IslandState.Panels, PanelSize.Clipboard.W, PanelSize.Clipboard.H),
        (IslandState.Panels, PanelSize.Media.W, PanelSize.Media.H),
        (IslandState.Hidden, 0, 0),
        (IslandState.ShelfDrop, 0, 0),
        (IslandState.Panels, PanelSize.Shelf.W, PanelSize.Shelf.H),
        (IslandState.Hidden, 0, 0),
        (IslandState.Palette, PanelSize.PaletteWidth, PanelSize.PaletteMinHeight),
        (IslandState.Palette, PanelSize.PaletteWidth, PanelSize.PaletteMaxHeight),
        (IslandState.Hidden, 0, 0),
    ];

    [STAThread]
    private static unsafe int Main(string[] args)
    {
        bool debugAuto = args.Contains("--debug-auto");
        bool debug = debugAuto || args.Contains("--debug-cycle");

        using var island = new IslandWindow { ForceReduceMotion = args.Contains("--debug-reduce-motion") };
        SynchronizationContext.SetSynchronizationContext(new DispatcherQueueContext(island.Queue));
        var machine = new IslandStateMachine();
        StreamWriter? log = null;
        int step = 0;
        if (debug)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tomte");
            Directory.CreateDirectory(dir);
            log = new StreamWriter(Path.Combine(dir, "island-debug.log"), append: true) { AutoFlush = true };
            island.Log = log.WriteLine;
            log.WriteLine($"{DateTime.Now:O} start args=[{string.Join(' ', args)}] scale={island.MonitorSize}");
            // Win+Alt+Shift+T steps, Win+Alt+Shift+Q quits (Win+Alt+T is taken on the owner's PC). Thread hotkeys arrive
            // in the loop below with a null window.
            const HOT_KEY_MODIFIERS chord = HOT_KEY_MODIFIERS.MOD_WIN | HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_SHIFT | HOT_KEY_MODIFIERS.MOD_NOREPEAT;
            bool cycle = PInvoke.RegisterHotKey(HWND.Null, CycleHotKey, chord, 'T');
            bool quit = PInvoke.RegisterHotKey(HWND.Null, QuitHotKey, chord, 'Q');
            log.WriteLine($"hotkeys: Win+Alt+Shift+T cycle={cycle} Win+Alt+Shift+Q quit={quit}");
        }

        if (debugAuto)
        {
            _ = AutoCycle(island, machine, log!);
        }

        MSG message;
        while (PInvoke.GetMessage(&message, HWND.Null, 0, 0).Value > 0)
        {
            if (message.message == PInvoke.WM_HOTKEY)
            {
                if (message.wParam.Value == QuitHotKey)
                {
                    PInvoke.PostQuitMessage(0);
                }
                else
                {
                    _ = Step(island, machine, log!, DebugCycle[step++ % DebugCycle.Length]);
                }

                continue;
            }

            PInvoke.TranslateMessage(&message);
            PInvoke.DispatchMessage(&message);
        }

        log?.Dispose();
        return 0;
    }

    private static async Task AutoCycle(IslandWindow island, IslandStateMachine machine, StreamWriter log)
    {
        try
        {
            await Task.Delay(1000);
            for (int loop = 0; loop < 3; loop++)
            {
                foreach (var next in DebugCycle)
                {
                    await Step(island, machine, log, next);
                    await Task.Delay(350);
                }
            }

            log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} auto cycle done, staying hidden");
        }
        catch (Exception e)
        {
            log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} auto cycle failed: {e}");
        }
    }

    private static async Task Step(IslandWindow island, IslandStateMachine machine, StreamWriter log, (IslandState State, double W, double H) next)
    {
        try
        {
            var from = machine.State;
            machine.TransitionTo(next.State);
            island.PanelLook = next.State is IslandState.Panels or IslandState.Palette;
            var (monitorW, monitorH) = island.MonitorSize;
            var geometry = IslandGeometry.For(next.State, Math.Min(next.W, 0.6 * monitorW), Math.Min(next.H, 0.5 * monitorH));
            await island.MorphTo(geometry, Spring.PresetFor(from, next.State));
            double rate = island.LastMorphMs > 0 ? island.LastMorphTicks / (island.LastMorphMs / 1000) : 0;
            log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {from}->{next.State} {geometry.Width}x{geometry.Depth} ticks={island.LastMorphTicks} wall={island.LastMorphMs:F0}ms rate={rate:F1}/s");
        }
        catch (Exception e)
        {
            log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} step to {next.State} failed: {e}");
        }
    }

    /// <summary>Routes await continuations back to the UI thread's dispatcher queue.</summary>
    private sealed class DispatcherQueueContext(Windows.System.DispatcherQueue queue) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => queue.TryEnqueue(() => d(state));
    }
}
