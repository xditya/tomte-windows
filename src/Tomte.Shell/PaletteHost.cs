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
    private const double LineHeight = 21;
    private const double CharsPerLine = 88;
    private const double GrowStep = 24;
    private double _targetHeight;

    public async Task RunAsync(PaletteSession session)
    {
        var from = machine.State;
        machine.TransitionTo(IslandState.Palette);
        island.PanelLook = true;
        var placeholder = island.Compositor.CreateSpriteVisual();
        placeholder.Brush = island.Compositor.CreateColorBrush(Color.FromArgb(28, 255, 255, 255));
        float s = (float)island.Scale;
        placeholder.Offset = new Vector3((24 + 14) * s, 12 * s, 0);
        placeholder.Size = new Vector2((float)(PanelSize.PaletteWidth - 28) * s, 36 * s);
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
            await session.SendAsync();
            await growth;
            log($"{DateTime.Now:HH:mm:ss.fff} palette {session.Status}{(session.Error is null ? "" : ": " + session.Error)} indicator=\"{session.ProviderIndicator}\" steps={session.ToolSteps} height={island.Current.Depth}");
        }
        finally
        {
            island.Content.Children.Remove(placeholder);
        }
    }

    /// <summary>Estimated content height, quantised so a streaming answer retargets the spring every few lines, not every token.</summary>
    internal static double HeightFor(IPaletteSession session)
    {
        double lines = 0;
        foreach (var segment in session.Answer)
        {
            lines += segment is TextSegment text
                ? text.Text.Split('\n').Sum(line => Math.Max(1, Math.Ceiling(line.Length / CharsPerLine)))
                : 1;
        }

        double needed = PanelSize.PaletteMinHeight + Math.Ceiling(lines * LineHeight / GrowStep) * GrowStep;
        return Math.Clamp(needed, PanelSize.PaletteMinHeight, PanelSize.PaletteMaxHeight);
    }

    private IslandGeometry Geometry(double height)
    {
        var (monitorW, monitorH) = island.MonitorSize;
        return IslandGeometry.For(IslandState.Palette, Math.Min(PanelSize.PaletteWidth, 0.6 * monitorW), Math.Min(height, 0.5 * monitorH));
    }
}
