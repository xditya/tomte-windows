using System.Numerics;
using Tomte.AI.Palette;
using Tomte.Core;
using Windows.UI;
using Windows.UI.Composition;

namespace Tomte.Shell;

/// <summary>
/// Drives the Palette state on the island for one session: opens at 640 x 150, grows toward 480
/// with the gentle preset as the answer streams in, and hangs a placeholder where the prompt box
/// goes. The text renderer (DirectWrite in-process or an on-demand XAML island, not decided) will
/// replace the placeholder and measure the real height; until then the height is estimated from
/// the answer length. Focus rule for that renderer: the window keeps WS_EX_NOACTIVATE and only
/// activates when the user clicks into the prompt box.
/// </summary>
internal sealed class PaletteHost(IslandWindow island, IslandStateMachine machine, Action<string> log)
{
    private double _targetHeight;

    /// <summary>Device actions the session ran, in order. Each shows as a Compact event once the palette has retracted.</summary>
    public List<DeviceEvent> Events { get; } = [];

    public async Task RunAsync(PaletteSession session)
    {
        var from = machine.State;
        machine.TransitionTo(IslandState.Palette);
        island.Mascot.Follow(from, IslandState.Palette);
        island.Mascot.Set(MascotState.Thinking);
        island.PanelLook = true;
        var placeholder = Placeholder(24 + 14, 12, PanelSize.PaletteWidth - 28, 36);
        try
        {
            _targetHeight = PanelSize.PaletteMinHeight;
            await island.MorphTo(Geometry(_targetHeight), Spring.PresetFor(from, IslandState.Palette));
            log($"{DateTime.Now:HH:mm:ss.fff} palette open {island.Current.Width}x{island.Current.Depth}");

            Task growth = Task.CompletedTask;
            session.Changed += () =>
            {
                double height = HeightFor(session);
                if (height == _targetHeight)
                    return;
                _targetHeight = height;
                log($"{DateTime.Now:HH:mm:ss.fff} palette grow -> {height} ({session.AnswerText.Length} chars, {session.ToolSteps} steps)");
                growth = island.MorphTo(Geometry(height), SpringPreset.Gentle);
            };
            session.DeviceActionExecuted += e =>
            {
                Events.Add(e);
                log($"{DateTime.Now:HH:mm:ss.fff} palette device event: {e.Line}{(e.Undo is null ? "" : " (undo available)")}");
            };
            await session.SendAsync();
            island.Mascot.Set(session.Status == PaletteStatus.Done ? MascotState.Result : MascotState.Error);
            await growth;
            log($"{DateTime.Now:HH:mm:ss.fff} palette {session.Status}{(session.Error is null ? "" : ": " + session.Error)} indicator=\"{session.ProviderIndicator}\" steps={session.ToolSteps} height={island.Current.Depth}");
        }
        finally
        {
            island.Content.Children.Remove(placeholder);
        }
    }

    /// <summary>
    /// The one-line Compact event for a device action: Compact for 4 s, then Hidden. The state table
    /// enters Compact from Hidden only, so the caller retracts the palette first. The text renderer
    /// will draw the line and an undo affordance where the placeholder bar sits.
    /// </summary>
    public async Task ShowEventAsync(DeviceEvent e)
    {
        machine.TransitionTo(IslandState.Compact);
        island.Mascot.Follow(IslandState.Hidden, IslandState.Compact);
        island.PanelLook = false;
        var compact = IslandGeometry.For(IslandState.Compact, 0, 0);
        var placeholder = Placeholder(44, 11, compact.Width - 44 - 16, 16);
        try
        {
            await island.MorphTo(compact, Spring.PresetFor(IslandState.Hidden, IslandState.Compact));
            log($"{DateTime.Now:HH:mm:ss.fff} compact event shown: {e.Line}{(e.Undo is null ? "" : " [undo]")}");
            await Task.Delay(4000);
            machine.TransitionTo(IslandState.Hidden);
            island.Mascot.Follow(IslandState.Compact, IslandState.Hidden);
            await island.MorphTo(IslandGeometry.For(IslandState.Hidden, 0, 0), Spring.PresetFor(IslandState.Compact, IslandState.Hidden));
            log($"{DateTime.Now:HH:mm:ss.fff} compact event timed out");
        }
        finally
        {
            island.Content.Children.Remove(placeholder);
        }
    }

    /// <summary>A faint bar on the Content seam, in DIPs, standing in for text until the renderer lands.</summary>
    private SpriteVisual Placeholder(double x, double y, double width, double height)
    {
        var visual = island.Compositor.CreateSpriteVisual();
        visual.Brush = island.Compositor.CreateColorBrush(Color.FromArgb(28, 255, 255, 255));
        float s = (float)island.Scale;
        visual.Offset = new Vector3((float)x * s, (float)y * s, 0);
        visual.Size = new Vector2((float)width * s, (float)height * s);
        island.Content.Children.InsertAtTop(visual);
        return visual;
    }

    /// <summary>Text segments as their lines, a tool step as one line; the estimate itself lives in Tomte.Core.</summary>
    private static double HeightFor(IPaletteSession session) =>
        PanelSize.PaletteHeightFor(session.Answer.SelectMany(segment => segment is TextSegment text ? text.Text.Split('\n') : [""]));

    private IslandGeometry Geometry(double height)
    {
        var (monitorW, monitorH) = island.MonitorSize;
        return IslandGeometry.For(IslandState.Palette, Math.Min(PanelSize.PaletteWidth, 0.6 * monitorW), Math.Min(height, 0.5 * monitorH));
    }
}
