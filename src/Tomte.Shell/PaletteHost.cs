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
/// activates when the user clicks into the prompt box. A voice command enters from the rambler's pill, and while
/// its session waits for the user's answer a second placeholder marks where the confirmation goes.
/// </summary>
internal sealed class PaletteHost(IslandWindow island, IslandStateMachine machine, Action<string> log)
{
    private double _targetHeight;

    /// <summary>Device actions the session ran, in order. The shell raises them as island events once the palette has retracted.</summary>
    public List<DeviceEvent> Events { get; } = [];

    public async Task RunAsync(PaletteSession session)
    {
        var from = machine.State;
        // The table has no Compact to Palette edge; from the rambler's pill the state passes through Hidden while the
        // shape morphs straight across.
        if (!IslandStateMachine.CanTransition(from, IslandState.Palette))
            machine.TransitionTo(IslandState.Hidden);
        machine.TransitionTo(IslandState.Palette);
        island.Mascot.Follow(from, IslandState.Palette);
        island.Mascot.Set(MascotState.Thinking);
        island.PanelLook = true;
        // Esc closes the palette, as it does every state the user opened on purpose.
        InputThread.SetEscape(true);
        var placeholder = Placeholder(12);
        try
        {
            _targetHeight = PanelSize.PaletteMinHeight;
            await island.MorphTo(Geometry(_targetHeight), Spring.PresetFor(from, IslandState.Palette));
            // Esc or the lock screen took the palette down while it opened: nothing is sent into a palette that is gone.
            if (machine.State != IslandState.Palette)
                return;
            log($"{DateTime.Now:HH:mm:ss.fff} palette open {island.Current.Width}x{island.Current.Depth}");
            // The command list the prompt box will offer; until text renders on the island, the placeholder stands for it.
            log($"{DateTime.Now:HH:mm:ss.fff} palette commands: [{string.Join(", ", session.Commands.Select(c => $"/{c.Skill} \"{c.Name}\""))}]");

            Task growth = Task.CompletedTask;
            session.Changed += () =>
            {
                if (machine.State != IslandState.Palette)
                    return;
                double height = HeightFor(session);
                if (height == _targetHeight)
                    return;
                _targetHeight = height;
                log($"{DateTime.Now:HH:mm:ss.fff} palette grow -> {height} ({session.AnswerText.Length} chars, {session.ToolSteps} steps)");
                growth = island.MorphTo(Geometry(height), SpringPreset.Gentle);
            };
            // The size line and each file's line, logged where the palette will show them once text renders.
            Tomte.AI.Files.TokenEstimate? shown = null;
            session.Changed += () =>
            {
                if (session.Estimate is { } estimate && estimate != shown)
                    log($"{DateTime.Now:HH:mm:ss.fff} palette estimate: {(shown = estimate).Line} [{string.Join("; ", estimate.Files)}]");
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

    /// <summary>Holds a second placeholder, under the prompt's, until the answer comes; the tomte rests meanwhile.</summary>
    public async Task<T> AskAsync<T>(Task<T> answer)
    {
        var row = Placeholder(56);
        island.Mascot.Set(MascotState.Compact);
        try
        {
            return await answer;
        }
        finally
        {
            island.Content.Children.Remove(row);
            island.Mascot.Set(MascotState.Thinking);
        }
    }

    /// <summary>A bar the width of the prompt box, <paramref name="top"/> DIPs from the top of the content.</summary>
    private SpriteVisual Placeholder(float top)
    {
        var placeholder = island.Compositor.CreateSpriteVisual();
        placeholder.Brush = island.Compositor.CreateColorBrush(Color.FromArgb(28, 255, 255, 255));
        float s = (float)island.Scale;
        // From the middle of the content box, so it sits the same in the pill and in the notch with its ramps.
        placeholder.RelativeOffsetAdjustment = new Vector3(0.5f, 0, 0);
        placeholder.Size = new Vector2((float)(PanelSize.PaletteWidth - 28) * s, 36 * s);
        placeholder.Offset = new Vector3(-placeholder.Size.X / 2, top * s, 0);
        island.Content.Children.InsertAtTop(placeholder);
        return placeholder;
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
