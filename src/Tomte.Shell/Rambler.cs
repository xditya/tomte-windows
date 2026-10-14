using System.Diagnostics;
using System.Numerics;
using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Rambler;
using Windows.UI;
using Windows.UI.Composition;

namespace Tomte.Shell;

/// <summary>
/// Hold the chord, speak, release: the recording becomes a transcript. With the AI layer off the transcript is
/// the raw note on the scratchpad. With it on, the pill becomes the palette and the model reads the transcript as
/// a command or a note; the palette asks before anything runs. Runs on the UI thread. The microphone is open only
/// between the press and the release, and the hold is the visible user action that lets audio (to a speech-to-text
/// provider) or the transcript (to the AI provider) leave the PC.
/// </summary>
/// <param name="voice">A new voice session per transcript, made from the provider set up at that moment; it returns null while there is none, and is null with no way to make one.</param>
/// <param name="text">The island's text renderer: the voice palette shows the transcript, the answer and the question with it.</param>
internal sealed class Rambler(
    IslandWindow island,
    IslandStateMachine machine,
    ISpeechToText speech,
    ProviderSend? speechSend,
    Func<Task<PaletteSession?>>? voice,
    ScratchpadNote scratchpad,
    SendLog sendLog,
    PaletteOptions prices,
    TextRenderer text,
    Action<string> log)
{
    // The design's rambler pill is wider than the 220 Compact so the bars fit beside the mascot.
    private const double PillWidth = 262;
    private const float GroupWidth = 6 + 8 + Waveform.Bars * Waveform.BarWidth + (Waveform.Bars - 1) * Waveform.Gap;
    private const float GroupHeight = Waveform.MinHeight + Waveform.Swing;
    private static readonly Color Mic = Color.FromArgb(255, 0xFF, 0x6B, 0x6B);

    // A slow or stuck provider must not hold the rambler: every later hold waits for this one to finish.
    private static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(30);

    private readonly Stopwatch clock = new();
    private readonly float[] heights = new float[Waveform.Bars];
    private MicCapture? capture;
    private ShapeVisual? view;
    private CompositionRoundedRectangleGeometry[]? bars;
    private CompositionSpriteShape[]? barShapes;
    private bool transcribing;
    private PaletteSession? command;
    private PaletteHost? palette;
    private TaskCompletionSource? replyHold;
    private readonly ToolQuestion question = new();

    /// <summary>Microphone level 0..1, once per capture packet while recording, on the UI thread. The mascot bounces to it.</summary>
    public event Action<double>? Bounce;

    /// <summary>The pill is gone after a hold; the triggers release pointer tracking and the Esc hotkey here.</summary>
    public event Action? Retracted;

    /// <summary>The palette asks before a voice command's tool runs.</summary>
    public event Action<ToolRequest>? Asking;

    /// <summary>A voice command's session has finished, before the island retracts.</summary>
    public event Action<PaletteSession>? CommandDone;

    /// <summary>
    /// The answer to the palette's question (Win+Alt+Enter is yes, Esc no); with <paramref name="to"/>, only while that
    /// is still the question. Without it a yes goes to the question the card shows, never to one that opened after the
    /// press; a no ends whatever is asked. False when nothing was answered.
    /// </summary>
    public bool Answer(bool allow, ToolRequest? to = null)
    {
        to ??= palette?.Shown;
        return (to is not null || !allow) && question.Answer(allow, to);
    }

    /// <summary>The palette is going away under a voice command: what it was about to ask or run never runs.</summary>
    public void Cancel()
    {
        command?.Cancel();
        question.Answer(allow: false);
    }

    public void Begin()
    {
        // A new hold during a spoken reply's hold ends that hold: the reply gives way to the new recording.
        var hold = replyHold;
        if (capture is not null || (transcribing && hold is null))
            return;
        clock.Restart();
        // The capture thread posts each packet's level; the bars change when a packet arrives, with no timer.
        capture = MicCapture.Start(rms => island.Queue.TryEnqueue(() => Level(rms)));
        ShowView();
        var from = machine.State;
        if (hold is not null && from == IslandState.Palette)
        {
            // The table has no Palette to Compact edge; the state passes through Hidden while the shape morphs across.
            machine.TransitionTo(IslandState.Hidden);
            island.PanelLook = false;
            InputThread.SetEscape(false);
        }

        if (IslandStateMachine.CanTransition(machine.State, IslandState.Compact))
        {
            machine.TransitionTo(IslandState.Compact);
            island.Mascot.Follow(from, IslandState.Compact);
            _ = island.MorphTo(IslandGeometry.Compact with { Width = PillWidth }, SpringPreset.Snappy);
        }

        island.Mascot.Set(MascotState.Rambler);
        log($"{DateTime.Now:HH:mm:ss.fff} rambler: recording");
        // Last, so the reply's End sees this recording and leaves the island to it.
        hold?.TrySetResult();
    }

    public async void End(bool keep)
    {
        var recording = capture;
        if (recording is null)
            return;
        capture = null;
        transcribing = true;
        island.Mascot.Set(MascotState.Compact);
        try
        {
            float[] samples = await Task.Run(recording.Stop);
            if (!keep || samples.Length < MicCapture.SampleRate / 4)
            {
                log($"{DateTime.Now:HH:mm:ss.fff} rambler: tap, nothing kept");
                return;
            }

            ShowTranscribing();
            var watch = Stopwatch.StartNew();
            // whisper.cpp cannot be stopped mid-run; the timeout only bounds the provider path.
            using var speechTimeout = new CancellationTokenSource(SendTimeout);
            string transcript;
            try
            {
                transcript = await speech.TranscribeAsync(samples, speechTimeout.Token);
            }
            finally
            {
                // Local whisper sends nothing; a provider transcription is logged whether or not it succeeded.
                if (speechSend is not null)
                    LogSend(speechSend, 0, 0, "audio");
            }

            log($"{DateTime.Now:HH:mm:ss.fff} rambler: transcribed {samples.Length / (double)MicCapture.SampleRate:F1} s in {watch.ElapsedMilliseconds} ms");
            // whisper.cpp writes [BLANK_AUDIO] and similar for silence.
            if (transcript.Length == 0 || (transcript.StartsWith('[') && transcript.EndsWith(']')))
            {
                log($"{DateTime.Now:HH:mm:ss.fff} rambler: nothing heard ({transcript})");
                return;
            }

            if (voice is not null && machine.State == IslandState.Compact)
            {
                try
                {
                    // The note tool reads and writes the loaded note; if it cannot be read, the words go in as the raw note.
                    await scratchpad.LoadAsync();
                    command = await voice();
                }
                catch (Exception e)
                {
                    log($"{DateTime.Now:HH:mm:ss.fff} voice: not started, keeping the words as a note: {e.Message}");
                }
            }

            // Only while the rambler's pill is still up: a hold that the lock screen or a fullscreen app took down,
            // before or while the note loaded, keeps the words as a note and opens nothing.
            if (machine.State != IslandState.Compact)
                command = null;
            if (command is not null)
            {
                RemoveView();
                command.ConfirmTool = ConfirmAsync;
                log($"{DateTime.Now:HH:mm:ss.fff} voice: transcript \"{transcript}\"");
            }

            var session = command;
            await VoiceCommand.RouteAsync(transcript, KeepAsync, session, s => (palette = new PaletteHost(island, machine, log, text)).RunAsync(s, question));
            if (session is not null)
            {
                log($"{DateTime.Now:HH:mm:ss.fff} voice: {session.Status}{(session.Error is null ? "" : ": " + session.Error)} steps={session.ToolSteps} reply=\"{session.AnswerText.ReplaceLineEndings(" | ")}\"");
                CommandDone?.Invoke(session);
                // A reply in words stays up long enough to read, at about 15 characters a second; Esc, anything else
                // the user opens, or a new hold ends it sooner.
                if (session.Status == PaletteStatus.Done && session.AnswerText.Trim() is { Length: > 0 } reply && machine.State == IslandState.Palette)
                {
                    var hold = replyHold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                    void OnState(IslandState from, IslandState to)
                    {
                        if (to != IslandState.Palette)
                            hold.TrySetResult();
                    }

                    machine.Changed += OnState;
                    await Task.WhenAny(Task.Delay(TimeSpan.FromSeconds(Math.Clamp(reply.Length / 15.0, 4, 15))), hold.Task);
                    machine.Changed -= OnState;
                }
            }
        }
        catch (Exception e)
        {
            log($"{DateTime.Now:HH:mm:ss.fff} rambler: failed: {e.Message}");
        }
        finally
        {
            transcribing = false;
            replyHold = null;
            // Once the palette took the pill over, the triggers own the retract: what shows now may be an event pill. A
            // new hold that ended the reply's hold owns the island, so nothing here takes it away.
            bool handedOver = palette is not null;
            command = null;
            palette = null;
            if (capture is not null)
                log($"{DateTime.Now:HH:mm:ss.fff} rambler: the reply gave way to a new hold");
            else if (handedOver)
                Retracted?.Invoke();
            else
                await Retract();
        }
    }

    /// <summary>The raw words as a note. A locked database does not lose it: it waits in memory and is saved as soon as it can be.</summary>
    private async Task KeepAsync(string note)
    {
        bool saved = await scratchpad.AppendAsync(note, DateTime.Now);
        log($"{DateTime.Now:HH:mm:ss.fff} rambler: note {(saved ? "appended" : "kept until the database is free")} ({note.Length} chars)");
    }

    /// <summary>Waits for the user's answer while the palette shows the question with its buttons. A hide answers no.</summary>
    private async Task<PermissionAnswer> ConfirmAsync(ToolRequest request, CancellationToken ct)
    {
        var asked = question.AskAsync(request, ct);
        // A chord that cannot be registered comes back as a no from the triggers.
        InputThread.SetConfirm(true);
        log($"{DateTime.Now:HH:mm:ss.fff} voice: asks {request.Tier}: {request.Summary} (Win+Alt+Enter runs it, Esc cancels)");
        Asking?.Invoke(request);
        try
        {
            var given = await asked;
            log($"{DateTime.Now:HH:mm:ss.fff} voice: answer {given}");
            return given;
        }
        finally
        {
            InputThread.SetConfirm(false);
        }
    }

    /// <summary>
    /// One send-log line per request that left the PC, priced as the palette prices its sends. A transcription reports
    /// no token counts, so it counts as 0 until a per-minute price exists.
    /// </summary>
    private void LogSend(ProviderSend send, int inputTokens, int outputTokens, string attached) =>
        sendLog.Append(new SendLogEntry(DateTimeOffset.Now, send.Provider, send.Model, inputTokens, outputTokens, SendLog.Estimate(prices, send.Model, inputTokens, outputTokens), [attached], null));

    private void Level(float rms)
    {
        if (capture is null || bars is null || barShapes is null)
            return;
        double level = Waveform.Level(rms);
        Waveform.Heights(level, clock.Elapsed.TotalSeconds, heights);
        SetBars(heights);
        Bounce?.Invoke(level);
    }

    private void SetBars(ReadOnlySpan<float> h)
    {
        for (int i = 0; i < Waveform.Bars; i++)
        {
            bars![i].Size = new Vector2(Waveform.BarWidth, h[i]);
            barShapes![i].Offset = new Vector2(barShapes[i].Offset.X, (GroupHeight - h[i]) / 2);
        }
    }

    /// <summary>A red capsule for the microphone and the seven bars, centred in the pill, in DIPs scaled to the monitor.</summary>
    private void ShowView()
    {
        var c = island.Content.Compositor;
        float s = (float)island.Scale;
        view = c.CreateShapeVisual();
        view.Size = new Vector2(GroupWidth, GroupHeight);
        view.Scale = new Vector3(s, s, 1);
        // Content is as wide as the current outline, so half its width is the pill's centre at every frame of the morph.
        view.RelativeOffsetAdjustment = new Vector3(0.5f, 0, 0);
        // With the tomte on, centre the group in the room right of it rather than in the whole pill.
        float shift = island.Mascot.Enabled ? (MascotVisual.InsetX + MascotVisual.SizeDips) / 2 : 0;
        view.Offset = new Vector3((shift - GroupWidth / 2) * s, (float)(IslandGeometry.Compact.Depth - GroupHeight) / 2 * s, 0);

        var mic = c.CreateRoundedRectangleGeometry();
        mic.Size = new Vector2(6, 11);
        mic.CornerRadius = new Vector2(3, 3);
        var micShape = c.CreateSpriteShape(mic);
        micShape.FillBrush = c.CreateColorBrush(Mic);
        micShape.Offset = new Vector2(0, (GroupHeight - 11) / 2);
        view.Shapes.Add(micShape);

        var white = c.CreateColorBrush(Color.FromArgb(255, 255, 255, 255));
        bars = new CompositionRoundedRectangleGeometry[Waveform.Bars];
        barShapes = new CompositionSpriteShape[Waveform.Bars];
        for (int i = 0; i < Waveform.Bars; i++)
        {
            bars[i] = c.CreateRoundedRectangleGeometry();
            bars[i].CornerRadius = new Vector2(Waveform.BarWidth / 2, Waveform.BarWidth / 2);
            barShapes[i] = c.CreateSpriteShape(bars[i]);
            barShapes[i].FillBrush = white;
            barShapes[i].Offset = new Vector2(14 + i * (Waveform.BarWidth + Waveform.Gap), 0);
            view.Shapes.Add(barShapes[i]);
        }

        Array.Fill(heights, Waveform.MinHeight);
        SetBars(heights);
        island.Content.Children.InsertAtTop(view);
        view.Opacity = 0;
        view.StartAnimation("Opacity", Fade(c, 1, delayMs: 100, durationMs: 150));
    }

    /// <summary>Bars flat and dimmed while the speech is turned into text.</summary>
    private void ShowTranscribing()
    {
        if (view is null)
            return;
        Array.Fill(heights, Waveform.MinHeight);
        SetBars(heights);
        view.StartAnimation("Opacity", Fade(view.Compositor, 0.4f, 0, 150));
    }

    private async Task Retract()
    {
        var gone = view;
        view = null;
        bars = null;
        barShapes = null;
        gone?.StartAnimation("Opacity", Fade(gone.Compositor, 0, 0, 100));
        if (machine.State == IslandState.Compact)
        {
            machine.TransitionTo(IslandState.Hidden);
            island.Mascot.Follow(IslandState.Compact, IslandState.Hidden);
            await island.MorphTo(IslandGeometry.Hidden, SpringPreset.Snappy);
        }

        if (gone is not null)
        {
            island.Content.Children.Remove(gone);
            gone.Dispose();
        }

        Retracted?.Invoke();
    }

    /// <summary>The microphone and the bars leave at once when the pill turns into the palette.</summary>
    private void RemoveView()
    {
        if (view is not null)
        {
            island.Content.Children.Remove(view);
            view.Dispose();
        }

        view = null;
        bars = null;
        barShapes = null;
    }

    private static ScalarKeyFrameAnimation Fade(Compositor c, float to, int delayMs, int durationMs)
    {
        var fade = c.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(1, to);
        fade.DelayTime = TimeSpan.FromMilliseconds(delayMs);
        fade.Duration = TimeSpan.FromMilliseconds(durationMs);
        return fade;
    }
}

/// <summary>Which provider and model a rambler send goes to, for the send log.</summary>
internal sealed record ProviderSend(string Provider, string Model);
