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
/// Hold the chord, speak, release: the recording becomes a transcript, and the pill grows into a card that shows it
/// with three choices (<see cref="VoiceCard"/>). Insert types it into the window that had the keys when the hold
/// started; Add to note puts it on the scratchpad; Ask Tomte turns the card into the palette, where the model reads it
/// as a command or a note and asks before anything runs; Ask keeps the raw words as a note only when nothing could be
/// sent, not after an answer. Nothing is typed, sent or saved before a choice. Runs on the
/// UI thread. The microphone is open only between the press and the release; the hold is the visible user action that
/// lets audio go to a speech-to-text provider, and Ask the one that lets the transcript go to the AI provider.
/// </summary>
/// <param name="voice">A new voice session for a transcript the user asks about, made from the provider set up at that moment; it returns null while there is none, and is null with no way to make one.</param>
/// <param name="enterDoes">The choice in settings that Enter picks on the card.</param>
/// <param name="askOff">Why Ask cannot be offered now, or null when a provider can be used; nothing is sent to find out.</param>
/// <param name="text">The island's text renderer: the card and the voice palette show the transcript with it.</param>
internal sealed class Rambler(
    IslandWindow island,
    IslandStateMachine machine,
    ISpeechToText speech,
    ProviderSend? speechSend,
    Func<Task<PaletteSession?>>? voice,
    Func<VoiceChoice> enterDoes,
    Func<string?> askOff,
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
    private nint spokenInto, spokenField;
    private VoiceCard? card;
    private TaskCompletionSource<VoiceChoice?>? choosing;

    /// <summary>Microphone level 0..1, once per capture packet while recording, on the UI thread. The mascot bounces to it.</summary>
    public event Action<double>? Bounce;

    /// <summary>The pill is gone after a hold; the triggers release pointer tracking and the Esc hotkey here.</summary>
    public event Action? Retracted;

    /// <summary>The palette asks before a voice command's tool runs.</summary>
    public event Action<ToolRequest>? Asking;

    /// <summary>A voice command's session has finished, before the island retracts.</summary>
    public event Action<PaletteSession>? CommandDone;

    /// <summary>A line for the user once the pill has gone, such as a hold in which nothing was heard.</summary>
    public event Action<string>? Notice;

    /// <summary>The card shows the words and waits for a choice.</summary>
    public event Action? Choosing;

    /// <summary>What Insert types with: the window the words were spoken into, the field in it that had the keys, and the words. Null when typed, else why not.</summary>
    public Func<nint, nint, string, string?> Insert { get; init; } = DesktopEffects.TypeInto;

    /// <summary>
    /// A key on the card: Enter (null) picks the highlighted choice, Alt+I, Alt+A and Alt+N their own. Enter does nothing
    /// once Insert was refused, even one already on its way, so it never copies. False when no card waits.
    /// </summary>
    public bool Choose(VoiceChoice? alt)
    {
        if (card is null || choosing is null)
            return false;
        if (VoiceChoices.Pick(alt, card.Highlighted, card.CanAsk, card.Refused) is { } picked)
            choosing.TrySetResult(picked);
        return true;
    }

    /// <summary>
    /// The window in front changed. While the card waits, any window but the one spoken into (or the island) in front
    /// means Insert would type nowhere: the card says so and offers Copy, and Enter goes back to the app in front, so
    /// pressing it there neither picks nor copies.
    /// </summary>
    public void ForegroundChanged()
    {
        // A pick that already landed keeps its choice: a window change in the gap before it runs must not turn it to Copy.
        if (choosing is not { Task.IsCompleted: false } || card is not { Refused: false } shown)
            return;
        nint front = WindowContext.Foreground();
        if (front == (nint)island.Hwnd || VoiceChoices.Refusal(spokenInto, open: true, front, elevated: false, sameField: true) is not { } why)
            return;
        log($"{DateTime.Now:HH:mm:ss.fff} voice: {why}");
        shown.Refuse(why);
        InputThread.SetChoices(true, enter: false);
    }

    /// <summary>Esc on the card: the words are thrown away. False when no card waits.</summary>
    public bool Discard() => choosing?.TrySetResult(null) ?? false;

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

    /// <summary>
    /// A hold may start: none is recording, and no words wait on the card or run in the palette. A spoken reply's hold
    /// gives way to the new recording.
    /// </summary>
    public bool CanBegin => capture is null && (!transcribing || replyHold is not null);

    /// <summary>The voice key is held: the microphone records and the pill is the rambler's.</summary>
    public bool Recording => capture is not null;

    /// <summary>
    /// The hold starts: the microphone opens and the island grows into the rambler's pill. The triggers have taken down
    /// whatever showed, so the state comes from Hidden while the shape morphs from where it is. <paramref name="backTo"/>
    /// is the app the keys go back to when the island had them (the AI box, the note): Insert types there.
    /// </summary>
    public void Begin(nint backTo)
    {
        if (!CanBegin)
            return;
        clock.Restart();
        // Where Insert types: the window in front as the hold starts, or the app the island's keys go back to.
        nint front = WindowContext.Foreground();
        spokenInto = front != 0 && front != (nint)island.Hwnd ? front : backTo;
        spokenField = DesktopEffects.FocusIn(spokenInto);
        // The capture thread posts each packet's level; the bars change when a packet arrives, with no timer.
        capture = MicCapture.Start(rms => island.Queue.TryEnqueue(() => Level(rms)));
        ShowView();
        var from = machine.State;
        if (IslandStateMachine.CanTransition(from, IslandState.Compact))
        {
            machine.TransitionTo(IslandState.Compact);
            island.Mascot.Follow(from, IslandState.Compact);
            _ = island.MorphTo(IslandGeometry.Compact with { Width = PillWidth }, SpringPreset.Snappy);
        }

        island.Mascot.Set(MascotState.Rambler);
        log($"{DateTime.Now:HH:mm:ss.fff} rambler: recording");
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
                Notice?.Invoke("Nothing heard. Hold the voice key while you speak.");
                return;
            }

            // Only while the rambler's pill is still up: a hold that the lock screen or a fullscreen app took down keeps
            // the words as a note and opens nothing.
            if (machine.State != IslandState.Compact)
            {
                await KeepAsync(transcript);
                return;
            }

            log($"{DateTime.Now:HH:mm:ss.fff} voice: transcript \"{transcript}\"");
            string? off = voice is null ? VoiceChoices.NoProvider : askOff();
            var shown = card = new VoiceCard(island, text, transcript, VoiceChoices.Default(enterDoes(), off is null), off);
            shown.Picked += c => choosing?.TrySetResult(c);
            RemoveView();
            _ = island.MorphTo(shown.Shape, SpringPreset.Snappy);
            PaletteSession? session = null;
            while (true)
            {
                var choice = await ChooseAsync();
                // A takedown picks Note; a pick may also land in the same turn as one.
                bool takenDown = machine.State != IslandState.Compact;
                log($"{DateTime.Now:HH:mm:ss.fff} voice: {(choice is null ? "thrown away" : "picked " + choice)}{(takenDown ? ", the card was taken down" : "")}");
                if (choice is not { } picked)
                    break;
                if (takenDown)
                    RemoveCard();
                if (picked == VoiceChoice.Insert && shown.Refused)
                {
                    await CopyAsync(transcript);
                    break;
                }

                (string? refused, session) = await VoiceCommand.PickedAsync(picked, transcript, words => Insert(spokenInto, spokenField, words), KeepAsync, StartAsync, s => (palette = new PaletteHost(island, machine, log, text)).RunAsync(s, question));
                if (refused is null)
                    break;
                log($"{DateTime.Now:HH:mm:ss.fff} voice: not typed: {refused}");
                if (card is null)
                {
                    // The card was taken down with the pick, so there is no Copy to offer: the words go to the note.
                    await KeepAsync(transcript);
                    break;
                }

                shown.Refuse(refused);
            }

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

    /// <summary>
    /// Waits on the card for a click, a key, or Esc (null). Anything else that takes the island from the card (the lock
    /// screen, a fullscreen app, another hotkey) keeps the words as a note, as a hold taken down before the card does.
    /// </summary>
    private async Task<VoiceChoice?> ChooseAsync()
    {
        var waiting = choosing = new TaskCompletionSource<VoiceChoice?>(TaskCreationOptions.RunContinuationsAsynchronously);
        void OnState(IslandState from, IslandState to)
        {
            if (to != IslandState.Compact)
                waiting.TrySetResult(VoiceChoice.Note);
        }

        machine.Changed += OnState;
        // The user may have switched apps while the words were transcribed, when no card listened for it: look now,
        // so a card that opens over another app is refused before Enter is ever taken from that app.
        ForegroundChanged();
        // Esc and the choice keys come to the island only while the card waits; the app in front has them otherwise.
        // Enter not at all once Insert was refused: the user may be typing in another app by then.
        InputThread.SetEscape(true);
        InputThread.SetChoices(true, enter: card?.Refused == false);
        try
        {
            Choosing?.Invoke();
            return await waiting.Task;
        }
        finally
        {
            machine.Changed -= OnState;
            choosing = null;
            InputThread.SetChoices(false);
            // A state that took the island over has set Esc for itself.
            if (machine.State == IslandState.Compact)
                InputThread.SetEscape(false);
        }
    }

    /// <summary>
    /// Ask's session, made only once Ask is picked, so nothing reaches a provider before. The note tool reads and writes
    /// the loaded note; when it cannot be read, or no session can be made, the words go in as the raw note, as they do
    /// when the send fails.
    /// </summary>
    private async Task<PaletteSession?> StartAsync()
    {
        try
        {
            await scratchpad.LoadAsync();
            command = voice is null ? null : await voice();
        }
        catch (Exception e)
        {
            log($"{DateTime.Now:HH:mm:ss.fff} voice: not started, keeping the words as a note: {e.Message}");
        }

        // Only while the card is still up: one the lock screen or a fullscreen app took down meanwhile opens nothing.
        if (machine.State != IslandState.Compact)
            command = null;
        if (command is not null)
        {
            // The card leaves at once as it turns into the palette.
            RemoveCard();
            command.ConfirmTool = ConfirmAsync;
        }

        return command;
    }

    /// <summary>Copy, in Insert's place once it could not type. A clipboard another app keeps open sends the words to the note instead.</summary>
    private async Task CopyAsync(string words)
    {
        try
        {
            DesktopEffects.CopyToClipboard(words);
            Notice?.Invoke("Copied. Paste it where you want it.");
        }
        catch (InvalidOperationException e)
        {
            log($"{DateTime.Now:HH:mm:ss.fff} voice: not copied: {e.Message}");
            await KeepAsync(words);
            Notice?.Invoke("The clipboard was busy, so the words are in the note.");
        }
    }

    private void RemoveCard()
    {
        card?.Dispose();
        card = null;
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
        var leaving = card;
        card = null;
        leaving?.FadeOut();
        if (machine.State == IslandState.Compact)
        {
            machine.TransitionTo(IslandState.Hidden);
            island.Mascot.Follow(IslandState.Compact, IslandState.Hidden);
            await island.MorphTo(IslandGeometry.Hidden, SpringPreset.Snappy);
        }

        leaving?.Dispose();
        if (gone is not null)
        {
            island.Content.Children.Remove(gone);
            gone.Dispose();
        }

        // A hold that began during the morph (still recording, or already transcribing) has the pill now: its own end
        // releases the island.
        if (capture is null && !transcribing)
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
