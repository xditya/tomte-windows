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

    /// <summary>Device actions the session ran, in order. The shell raises them as island events once the palette has retracted.</summary>
    public List<DeviceEvent> Events { get; } = [];

    public async Task RunAsync(PaletteSession session)
    {
        var from = machine.State;
        machine.TransitionTo(IslandState.Palette);
        island.Mascot.Follow(from, IslandState.Palette);
        island.Mascot.Set(MascotState.Thinking);
        island.PanelLook = true;
        var placeholder = island.Compositor.CreateSpriteVisual();
        placeholder.Brush = island.Compositor.CreateColorBrush(Color.FromArgb(28, 255, 255, 255));
        float s = (float)island.Scale;
        // From the middle of the content box, so it sits the same in the pill and in the notch with its ramps.
        placeholder.RelativeOffsetAdjustment = new Vector3(0.5f, 0, 0);
        placeholder.Size = new Vector2((float)(PanelSize.PaletteWidth - 28) * s, 36 * s);
        placeholder.Offset = new Vector3(-placeholder.Size.X / 2, 12 * s, 0);
        island.Content.Children.InsertAtTop(placeholder);
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

    /// <summary>Text segments as their lines, a tool step as one line; the estimate itself lives in Tomte.Core.</summary>
    private static double HeightFor(IPaletteSession session) =>
        PanelSize.PaletteHeightFor(session.Answer.SelectMany(segment => segment is TextSegment text ? text.Text.Split('\n') : [""]));

    private IslandGeometry Geometry(double height)
    {
        var (monitorW, monitorH) = island.MonitorSize;
        return IslandGeometry.For(IslandState.Palette, Math.Min(PanelSize.PaletteWidth, 0.6 * monitorW), Math.Min(height, 0.5 * monitorH));
    }
}
