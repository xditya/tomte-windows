using Tomte.AI.Palette;
using Tomte.Core;

namespace Tomte.Shell;

/// <summary>
/// Drives the Palette state on the island for one session: opens at 640 x 150 and grows toward 480
/// with the gentle preset as the answer streams in. The palette shows its prompt box (or, by voice,
/// what was said), chips, answer, a tool's question and footer (<see cref="PaletteView"/>) and grows
/// to their measured height. The window activates only for the palette hotkey, a click on the tomte
/// or a click into the prompt box (<see cref="TypingFocus"/>). A voice command enters from the
/// rambler's pill; its question is answered with the buttons, Win+Alt+Enter or Esc, since the island
/// has no keys then.
/// </summary>
internal sealed partial class PaletteHost(IslandWindow island, IslandStateMachine machine, Action<string> log, TextRenderer text, TypingFocus? focus = null, Action? hide = null)
{
    private double _targetHeight;

    private PaletteView? running;

    /// <summary>Device actions the session ran, in order. The shell raises them as island events once the palette has retracted.</summary>
    public List<DeviceEvent> Events { get; } = [];

    /// <summary>The question the running palette's card shows now; null with none.</summary>
    public ToolRequest? Shown => running?.Shown;

    /// <summary>What the footer's AI history button does: Panels on the History tab.</summary>
    public Action? OpenHistory { get; set; }

    /// <summary>What the back arrow of the next typed palette does; null for a palette opened from Hidden, which has none.</summary>
    public Action? Back { get; set; }

    /// <param name="question">The question the session asks through, drawn with its buttons; null for a driven run that answers itself.</param>
    public async Task RunAsync(PaletteSession session, ToolQuestion? question = null)
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
        var view = running = new PaletteView(island, machine, text, focus, session, (float)Width, null, question) { OpenHistory = OpenHistory };
        void Rest() => RestWhileAsked(question!);
        if (question is not null)
            question.Changed += Rest;
        try
        {
            _targetHeight = PanelSize.PaletteMinHeight;
            await island.MorphTo(Geometry(_targetHeight), Spring.PresetFor(from, IslandState.Palette));
            // Esc or the lock screen took the palette down while it opened: nothing is sent into a palette that is gone.
            if (machine.State != IslandState.Palette)
                return;
            log($"{DateTime.Now:HH:mm:ss.fff} palette open {island.Current.Width}x{island.Current.Depth}");
            log($"{DateTime.Now:HH:mm:ss.fff} palette commands: [{string.Join(", ", session.Commands.Select(c => $"/{c.Skill} \"{c.Name}\""))}]");

            Task growth = Task.CompletedTask;
            void Grown()
            {
                double height = view.Height;
                if (machine.State != IslandState.Palette || height <= 0 || height == _targetHeight)
                    return;
                _targetHeight = height;
                log($"{DateTime.Now:HH:mm:ss.fff} palette grow -> {height} ({session.AnswerText.Length} chars, {session.ToolSteps} steps)");
                growth = island.MorphTo(Geometry(height), SpringPreset.Gentle);
            }

            view.HeightChanged += Grown;
            Grown();
            // The size line and each file's line, logged for a driven run.
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
            if (question is not null)
                question.Changed -= Rest;
        }
    }

    // The tomte rests while the palette waits for the user's answer, and thinks again once it is given.
    private void RestWhileAsked(ToolQuestion question)
    {
        if (machine.State == IslandState.Palette)
            island.Mascot.Set(question.Asked is null ? MascotState.Thinking : MascotState.Compact);
    }

    private IslandGeometry Geometry(double height)
    {
        var (monitorW, monitorH) = island.MonitorSize;
        return IslandGeometry.For(IslandState.Palette, Math.Min(PanelSize.PaletteWidth, 0.6 * monitorW), Math.Min(height, 0.5 * monitorH));
    }
}
