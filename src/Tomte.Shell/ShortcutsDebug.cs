using System.Diagnostics;
using System.Numerics;
using Tomte.Core;
using Tomte.Core.Shortcuts;
using Tomte.Core.Storage;

namespace Tomte.Shell;

/// <summary>
/// --debug-shortcuts [--debug-press N] [--debug-hold ms] [--debug-cycles N]: stands in for the panel host, which is
/// not on this branch. Opens Panels at the Shortcuts size with the grid in it, under a 42 DIP strip left empty for
/// the tabs, logs memory once the tiles and icons are in, presses tile N if asked (from code, so Windows may open the
/// app behind the current window), hides the island and logs memory again, as many times as asked. The shell then
/// stays hidden, so its idle cost after use can be measured.
/// </summary>
internal static class ShortcutsDebug
{
    // The tab strip row the host keeps at the top of every panel.
    private const float PanelStrip = 42;

    public static async void Run(IslandWindow island, TriggerController triggers, SettingsStore store, Action<string> log, int? press, int holdMs, int cycles)
    {
        try
        {
            log($"shortcuts debug: memory before {Memory()}");
            var shortcuts = new ShortcutStore(new TomteDb(), () => store.Current.ScriptShortcuts);
            var panel = new ShortcutsPanel(island, shortcuts, () => store.Current.ScriptShortcuts, () => log("shortcuts debug: would open settings"), log);
            for (int cycle = 1; cycle <= cycles; cycle++)
            {
                log($"shortcuts debug: showing {cycle} of {cycles}");
                await Show(island, triggers, panel, log, press, holdMs);
            }
        }
        catch (Exception e)
        {
            log($"shortcuts debug failed: {e}");
        }
    }

    private static async Task Show(IslandWindow island, TriggerController triggers, ShortcutsPanel panel, Action<string> log, int? press, int holdMs)
    {
        var (monitorW, monitorH) = island.MonitorSize;
        float width = (float)Math.Min(PanelSize.Shortcuts.W, 0.6 * monitorW), height = (float)Math.Min(PanelSize.Shortcuts.H, 0.5 * monitorH);
        float scale = (float)island.Scale;

        var body = island.Compositor.CreateContainerVisual();
        body.Scale = new Vector3(scale, scale, 1);
        // The island's centre line inside Content (the outline plus a 1 px margin each side), less half the body.
        var place = island.Compositor.CreateExpressionAnimation(FormattableString.Invariant($"Vector3((content.Size.X - 2) / 2 - {width / 2 * scale}, {PanelStrip * scale}, 0)"));
        place.SetReferenceParameter("content", island.Content);
        body.StartAnimation("Offset", place);
        island.Content.Children.InsertAtTop(body);

        triggers.Machine.TransitionTo(IslandState.Panels);
        island.Mascot.Follow(IslandState.Hidden, IslandState.Panels);
        island.PanelLook = true;
        var shown = Stopwatch.StartNew();
        panel.Shown(body, new Vector2(width, height - PanelStrip));
        log($"shortcuts debug: Shown returned in {shown.Elapsed.TotalMilliseconds:F2} ms");
        await island.MorphTo(IslandGeometry.Panel(width, height), SpringPreset.Snappy);
        await panel.Ready;
        log($"shortcuts debug: tiles and icons in {shown.Elapsed.TotalMilliseconds:F0} ms; memory shown {Memory()}");

        if (press is int cell)
        {
            var at = ShortcutGrid.Cell(cell, new Vector2(width, height - PanelStrip));
            panel.Press(new Vector2(at.X + at.Width / 2, at.Y + at.Height / 2));
        }

        await Task.Delay(holdMs);
        triggers.Hide();
        panel.Hidden();
        island.Content.Children.Remove(body);
        body.Dispose();
        place.Dispose();
        await Task.Delay(1500);
        GC.Collect();
        GC.WaitForPendingFinalizers();
        await Task.Delay(1000);
        log($"shortcuts debug: memory hidden again {Memory()}");
    }

    private static string Memory()
    {
        using var process = Process.GetCurrentProcess();
        return $"ws={process.WorkingSet64 / 1048576.0:F1}MB private={process.PrivateMemorySize64 / 1048576.0:F1}MB";
    }
}
