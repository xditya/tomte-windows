using System.ComponentModel;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Rambler;
using Windows.UI;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Shell;

/// <summary>
/// Hold the chord, speak, release: the recording becomes a transcript, the transcript becomes a note
/// (structured by the AI provider when one is configured, raw otherwise) and the note lands on the
/// scratchpad. Runs on the UI thread. The microphone is open only between the press and the release,
/// and the hold is the visible user action that lets audio or text leave the PC.
/// </summary>
internal sealed class Rambler(
    IslandWindow island,
    IslandStateMachine machine,
    ISpeechToText speech,
    ProviderSend? speechSend,
    RamblerStructure? structure,
    IScratchpad scratchpad,
    SendLog sendLog,
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

    /// <summary>Microphone level 0..1, once per capture packet while recording, on the UI thread. The mascot bounces to it.</summary>
    public event Action<double>? Bounce;

    public void Begin()
    {
        if (transcribing || capture is not null)
            return;
        clock.Restart();
        // The capture thread posts each packet's level; the bars change when a packet arrives, with no timer.
        capture = MicCapture.Start(rms => island.Queue.TryEnqueue(() => Level(rms)));
        ShowView();
        if (IslandStateMachine.CanTransition(machine.State, IslandState.Compact))
        {
            machine.TransitionTo(IslandState.Compact);
            _ = island.MorphTo(IslandGeometry.Compact with { Width = PillWidth }, SpringPreset.Snappy);
        }
        log($"{DateTime.Now:HH:mm:ss.fff} rambler: recording");
    }

    public async void End(bool keep)
    {
        var recording = capture;
        if (recording is null)
            return;
        capture = null;
        transcribing = true;
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
            string text;
            try
            {
                text = await speech.TranscribeAsync(samples, speechTimeout.Token);
            }
            finally
            {
                // Local whisper sends nothing; a provider transcription is logged whether or not it succeeded.
                if (speechSend is not null)
                    LogSend(speechSend, 0, 0, "audio");
            }

            log($"{DateTime.Now:HH:mm:ss.fff} rambler: transcribed {samples.Length / (double)MicCapture.SampleRate:F1} s in {watch.ElapsedMilliseconds} ms");
            // whisper.cpp writes [BLANK_AUDIO] and similar for silence.
            if (text.Length == 0 || (text.StartsWith('[') && text.EndsWith(']')))
            {
                log($"{DateTime.Now:HH:mm:ss.fff} rambler: nothing heard ({text})");
                return;
            }

            string note = text;
            if (structure is not null)
            {
                int inputTokens = 0, outputTokens = 0;
                try
                {
                    using var timeout = new CancellationTokenSource(SendTimeout);
                    var structured = await structure.Run(text, timeout.Token);
                    (note, inputTokens, outputTokens) = (structured.Note, structured.InputTokens, structured.OutputTokens);
                }
                catch (Exception e) when (e is ProviderException or OperationCanceledException)
                {
                    // The transcript is kept as it is; losing what the user said is worse than an untidy note.
                    log($"{DateTime.Now:HH:mm:ss.fff} rambler: structuring failed, raw transcript kept: {e.Message}");
                }
                finally
                {
                    LogSend(structure.Send, inputTokens, outputTokens, "transcript");
                }
            }

            scratchpad.AppendNote(note, DateTime.Now);
            log($"{DateTime.Now:HH:mm:ss.fff} rambler: note appended ({note.Length} chars)");
        }
        catch (Exception e)
        {
            log($"{DateTime.Now:HH:mm:ss.fff} rambler: failed: {e.Message}");
        }
        finally
        {
            transcribing = false;
            await Retract();
        }
    }

    /// <summary>One send-log line per request that left the PC. There is no price table here yet, so the cost is 0.</summary>
    private void LogSend(ProviderSend send, int inputTokens, int outputTokens, string attached) =>
        sendLog.Append(new SendLogEntry(DateTimeOffset.Now, send.Provider, send.Model, inputTokens, outputTokens, 0m, [attached], null));

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
        view.Offset = new Vector3(-GroupWidth / 2 * s, (float)(IslandGeometry.Compact.Depth - GroupHeight) / 2 * s, 0);

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
            await island.MorphTo(IslandGeometry.Hidden, SpringPreset.Snappy);
        }

        if (gone is not null)
        {
            island.Content.Children.Remove(gone);
            gone.Dispose();
        }
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

/// <summary>The structuring step: where it sends, and the call that turns a transcript into a note.</summary>
internal sealed record RamblerStructure(ProviderSend Send, Func<string, CancellationToken, Task<RamblerNote.Structured>> Run);

/// <summary>
/// Temporary Win+Alt+V registration for <c>--rambler</c>, until the triggers (A2) own every hotkey.
/// A dedicated input thread with a message-only window: RegisterHotKey reports the press, raw keyboard
/// input is registered only while the chord is held and reports the release. No keyboard hook.
/// </summary>
internal static unsafe class RamblerHotkey
{
    private const int HotKeyId = 0x5256;
    private static readonly HWND MessageOnlyParent = new(-3); // HWND_MESSAGE, a macro CsWin32 does not emit
    private static HoldDetector? detector;
    private static HWND window;
    private static Action<string>? log;
    private static int armError; // non-zero when arming raw input failed

    /// <summary>Starts the input thread. <paramref name="started"/> and <paramref name="ended"/> are posted to <paramref name="queue"/>.</summary>
    public static void Start(Windows.System.DispatcherQueue queue, Action started, Action<bool> ended, Action<string> log) =>
        new Thread(() => Run(queue, started, ended, log)) { IsBackground = true, Name = "tomte-input" }.Start();

    private static void Run(Windows.System.DispatcherQueue queue, Action started, Action<bool> ended, Action<string> logLine)
    {
        log = logLine;
        detector = new HoldDetector(Arm);
        detector.Started += () => queue.TryEnqueue(() => started());
        detector.Ended += keep => queue.TryEnqueue(() => ended(keep));

        var hInstance = new HINSTANCE(Marshal.GetHINSTANCE(typeof(RamblerHotkey).Module));
        fixed (char* className = "TomteRamblerInput")
        {
            var windowClass = new WNDCLASSEXW { cbSize = (uint)sizeof(WNDCLASSEXW), lpfnWndProc = &HandleMessage, hInstance = hInstance, lpszClassName = className };
            if (PInvoke.RegisterClassEx(in windowClass) == 0)
                throw new Win32Exception();
            window = PInvoke.CreateWindowEx(0, className, null, 0, 0, 0, 0, 0, MessageOnlyParent, HMENU.Null, hInstance, null);
        }

        const HOT_KEY_MODIFIERS chord = HOT_KEY_MODIFIERS.MOD_WIN | HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_NOREPEAT;
        bool registered = PInvoke.RegisterHotKey(window, HotKeyId, chord, 'V');
        logLine($"rambler hotkey: Win+Alt+V {(registered ? "registered" : $"failed, error {Marshal.GetLastPInvokeError()}")}");

        MSG message;
        while (PInvoke.GetMessage(&message, HWND.Null, 0, 0).Value > 0)
        {
            PInvoke.TranslateMessage(&message);
            PInvoke.DispatchMessage(&message);
        }
    }

    private static void Arm(bool on)
    {
        var device = new RAWINPUTDEVICE
        {
            usUsagePage = PInvoke.HID_USAGE_PAGE_GENERIC,
            usUsage = PInvoke.HID_USAGE_GENERIC_KEYBOARD,
            dwFlags = on ? RAWINPUTDEVICE_FLAGS.RIDEV_INPUTSINK : RAWINPUTDEVICE_FLAGS.RIDEV_REMOVE,
            hwndTarget = on ? window : HWND.Null,
        };
        if (!PInvoke.RegisterRawInputDevices(&device, 1, (uint)sizeof(RAWINPUTDEVICE)) && on)
            armError = Math.Max(1, Marshal.GetLastPInvokeError());
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT HandleMessage(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        try
        {
            if (message == PInvoke.WM_HOTKEY && wParam.Value == HotKeyId)
            {
                detector!.Press(Environment.TickCount64);
                // Without raw input no release would ever arrive and the microphone would stay open, so end the hold
                // as a tap. A quick tap can also lift V before the press is handled, with the same result.
                if (armError != 0)
                {
                    log?.Invoke($"rambler: raw input could not be armed (error {armError}), hold ended");
                    armError = 0;
                    detector.KeyUp('V', Environment.TickCount64);
                }
                else if ((PInvoke.GetAsyncKeyState('V') & 0x8000) == 0)
                {
                    detector.KeyUp('V', Environment.TickCount64);
                }
                return new LRESULT(0);
            }

            if (message == PInvoke.WM_INPUT)
            {
                RAWINPUT raw;
                uint size = (uint)sizeof(RAWINPUT);
                if (PInvoke.GetRawInputData(new HRAWINPUT(lParam.Value), RAW_INPUT_DATA_COMMAND_FLAGS.RID_INPUT, &raw, &size, (uint)sizeof(RAWINPUTHEADER)) != unchecked((uint)-1)
                    && raw.header.dwType == (uint)RID_DEVICE_INFO_TYPE.RIM_TYPEKEYBOARD
                    && (raw.data.keyboard.Flags & PInvoke.RI_KEY_BREAK) != 0)
                {
                    detector!.KeyUp(raw.data.keyboard.VKey, Environment.TickCount64);
                }
                // DefWindowProc below frees the raw input buffer.
            }
        }
        catch (Exception)
        {
            // An exception must not unwind into user32; the hold simply does not register.
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }
}
