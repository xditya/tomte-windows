using System.Diagnostics;
using System.Runtime.CompilerServices;
using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Rambler;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Shell;

internal static class Program
{
    private const int CycleHotKey = 1;
    private const int QuitHotKey = 2;
    private const int MascotHotKey = 3;

    // --debug-mascot: Win+Alt+Shift+M steps the reactions the AI layer and the rambler will set.
    private static readonly MascotState[] MascotCycle = [MascotState.Rambler, MascotState.Thinking, MascotState.Result, MascotState.Error, MascotState.Compact];

    // Until the triggers exist, --debug-cycle walks this legal path through the state table, one hotkey press per step.
    // The palette steps stand in for an AI request: thinking while it opens, the result as the answer grows.
    private static readonly (IslandState State, double W, double H, MascotState? React)[] DebugCycle =
    [
        (IslandState.Compact, 0, 0, null),
        (IslandState.Panels, PanelSize.Shelf.W, PanelSize.Shelf.H, null),
        (IslandState.Panels, PanelSize.Clipboard.W, PanelSize.Clipboard.H, null),
        (IslandState.Panels, PanelSize.Media.W, PanelSize.Media.H, null),
        (IslandState.Hidden, 0, 0, null),
        (IslandState.ShelfDrop, 0, 0, null),
        (IslandState.Panels, PanelSize.Shelf.W, PanelSize.Shelf.H, null),
        (IslandState.Hidden, 0, 0, null),
        (IslandState.Palette, PanelSize.PaletteWidth, PanelSize.PaletteMinHeight, MascotState.Thinking),
        (IslandState.Palette, PanelSize.PaletteWidth, PanelSize.PaletteMaxHeight, MascotState.Result),
        (IslandState.Hidden, 0, 0, null),
    ];

    private static Process? settingsProcess;

    [STAThread]
    private static unsafe int Main(string[] args)
    {
        if (args.Contains("--settings"))
        {
            RunSettings();
            return 0;
        }

        bool debugAuto = args.Contains("--debug-auto");
        bool debugMascot = args.Contains("--debug-mascot");
        int paletteArg = Array.IndexOf(args, "--palette");
        string? palettePrompt = paletteArg >= 0 && paletteArg + 1 < args.Length ? args[paletteArg + 1] : null;
        bool rambler = args.Contains("--rambler");
        bool debug = debugAuto || debugMascot || rambler || palettePrompt is not null || args.Contains("--debug-cycle");

        using var island = new IslandWindow { ForceReduceMotion = args.Contains("--debug-reduce-motion") };
        SynchronizationContext.SetSynchronizationContext(new DispatcherQueueContext(island.Queue));
        var machine = new IslandStateMachine();
        // Stand-ins for the settings until they exist: the off switch and a cap colour as RRGGBB hex.
        island.Mascot.Enabled = !args.Contains("--debug-mascot-off");
        int cap = Array.IndexOf(args, "--debug-cap");
        if (cap >= 0 && cap + 1 < args.Length)
        {
            island.Mascot.CapColor = Convert.ToUInt32(args[cap + 1], 16);
        }

        TextWriter? log = null;
        int step = 0;
        int reaction = 0;
        if (debug)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tomte");
            Directory.CreateDirectory(dir);
            // Synchronized: the rambler's input thread writes here too.
            log = TextWriter.Synchronized(OpenLog(dir));
            island.Log = log.WriteLine;
            log.WriteLine($"{DateTime.Now:O} start args=[{string.Join(' ', args)}] scale={island.MonitorSize}");
            // Win+Alt+Shift+T steps, Win+Alt+Shift+Q quits (Win+Alt+T is taken on the owner's PC). Thread hotkeys arrive
            // in the loop below with a null window.
            const HOT_KEY_MODIFIERS chord = HOT_KEY_MODIFIERS.MOD_WIN | HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_SHIFT | HOT_KEY_MODIFIERS.MOD_NOREPEAT;
            bool cycle = PInvoke.RegisterHotKey(HWND.Null, CycleHotKey, chord, 'T');
            bool quit = PInvoke.RegisterHotKey(HWND.Null, QuitHotKey, chord, 'Q');
            log.WriteLine($"hotkeys: Win+Alt+Shift+T cycle={cycle} Win+Alt+Shift+Q quit={quit}");
            if (debugMascot)
            {
                log.WriteLine($"hotkeys: Win+Alt+Shift+M mascot={PInvoke.RegisterHotKey(HWND.Null, MascotHotKey, chord, 'M')}");
                StartMascotFeed(island);
            }
        }

        if (rambler)
        {
            StartRambler(args, island, machine, log!);
        }

        if (debugAuto)
        {
            _ = AutoCycle(island, machine, log!);
        }
        else if (palettePrompt is not null)
        {
            _ = PaletteRun(island, machine, log!, palettePrompt);
        }

        var pause = new PauseState();
        using var tray = new TrayIcon(pause);
        tray.OpenIsland += () => _ = Step(island, machine, log, machine.State == IslandState.Hidden
            ? (IslandState.Panels, PanelSize.Shelf.W, PanelSize.Shelf.H, null)
            : (IslandState.Hidden, 0, 0, null));
        tray.OpenSettings += OpenSettings;
        tray.Paused += () =>
        {
            if (machine.State != IslandState.Hidden)
            {
                _ = Step(island, machine, log, (IslandState.Hidden, 0, 0, null));
            }
        };
        tray.Quit += () => PInvoke.PostQuitMessage(0);

        MSG message;
        while (PInvoke.GetMessage(&message, HWND.Null, 0, 0).Value > 0)
        {
            if (message.message == PInvoke.WM_HOTKEY)
            {
                if (message.wParam.Value == QuitHotKey)
                {
                    PInvoke.PostQuitMessage(0);
                }
                else if (message.wParam.Value == MascotHotKey)
                {
                    var next = MascotCycle[reaction++ % MascotCycle.Length];
                    island.Mascot.Set(next);
                    log!.WriteLine($"{DateTime.Now:HH:mm:ss.fff} mascot {next}");
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

        // In its first few hundred milliseconds the child has no window to close.
        if (settingsProcess is { HasExited: false } && !settingsProcess.CloseMainWindow())
        {
            settingsProcess.Kill();
        }

        log?.Dispose();
        return 0;
    }

    // Kept out of Main so the WinUI assemblies load only in the settings process.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunSettings() => Tomte.Panels.SettingsWindow.Run();

    // The settings window runs in its own process and exits when closed, so none of its memory stays in the shell.
    private static void OpenSettings()
    {
        if (settingsProcess is { HasExited: false })
        {
            settingsProcess.Refresh();
            PInvoke.SetForegroundWindow(new HWND(settingsProcess.MainWindowHandle));
            return;
        }

        settingsProcess?.Dispose();
        settingsProcess = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--settings"));
    }

    private static async Task AutoCycle(IslandWindow island, IslandStateMachine machine, TextWriter log)
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

    /// <summary>Debug stand-ins for the drag and the microphone: lean toward the cursor, and a synthetic level at 20 Hz.</summary>
    private static void StartMascotFeed(IslandWindow island)
    {
        var timer = island.Queue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(50);
        double t = 0;
        timer.Tick += (_, _) =>
        {
            t += 0.05;
            var mascot = island.Mascot;
            if (mascot.State == MascotState.ShelfDrop && PInvoke.GetCursorPos(out var cursor))
            {
                mascot.Lean(cursor.X - island.MascotScreenX);
            }
            else if (mascot.State == MascotState.Rambler)
            {
                mascot.Level = 0.55 + 0.45 * Math.Sin(t * 3.1) * Math.Sin(t * 0.9) + 0.2 * Math.Sin(t * 11);
            }
        };
        timer.Start();
    }

    private static string Memory()
    {
        using var process = Process.GetCurrentProcess();
        process.Refresh();
        return $"ws={process.WorkingSet64 / 1048576.0:F1}MB private={process.PrivateMemorySize64 / 1048576.0:F1}MB";
    }

    /// <summary>Shared with other debug runs; when one holds the file without sharing, this run gets its own.</summary>
    private static StreamWriter OpenLog(string dir)
    {
        try
        {
            return new StreamWriter(new FileStream(Path.Combine(dir, "island-debug.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true };
        }
        catch (IOException)
        {
            return new StreamWriter(Path.Combine(dir, $"island-debug-{Environment.ProcessId}.log"), append: true) { AutoFlush = true };
        }
    }

    /// <summary>
    /// --palette "prompt": the debug driver. Opens the palette on the island, runs the prompt against local
    /// Ollama with the window chip on and an in-memory shelf and scratchpad, logs the stream and the tool
    /// steps, then retracts and quits. External-effect tools are allowed once each, as a user would.
    /// </summary>
    private static async Task PaletteRun(IslandWindow island, IslandStateMachine machine, TextWriter log, string prompt)
    {
        try
        {
            // The island is still hidden, so the foreground is the window the user had in front; Tomte.AI
            // loads here, not in Main, so a normal start never touches it.
            nint foreground = WindowContext.Foreground();
            await Task.Delay(500);
            var config = new ProviderConfig("ollama", ProviderKind.OpenAICompatible, "http://127.0.0.1:11434/v1", "llama3.2:3b");
            using var http = new HttpClient();
            var shelf = new InMemoryShelf();
            shelf.CreateFile("notes.txt", "Milk, eggs, bread.\nCall the dentist on Monday at 10.");
            var scratchpad = new InMemoryScratchpad();
            var (app, title) = WindowContext.Describe(foreground);
            var chips = new List<ContextChip>
            {
                ContextChips.Selection(null),
                ContextChips.Window(app, title),
                ContextChips.Screenshot(app, ct => WindowCapture.CapturePngAsync(foreground, ct)),
                ContextChips.Shelf(shelf),
                ContextChips.Scratchpad(scratchpad),
            };
            chips[1].On = true;
            chips[2].On = Environment.GetCommandLineArgs().Contains("--screenshot");
            log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} palette memory before: {Memory()}");
            var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tomte", "send-log.jsonl");
            var tools = new BuiltInTools(shelf, scratchpad, DesktopEffects.CopyToClipboard, DesktopEffects.OpenUrl);
            var session = new PaletteSession(ProviderSetup.Create(config, http, null), config, new PaletteOptions(), new SendLog(logPath), tools, new PermissionGrants(), chips)
            {
                Prompt = prompt,
                ConfirmTool = (request, _) =>
                {
                    log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} palette confirm {request.Tier}: {request.Summary} -> allow once");
                    return Task.FromResult(PermissionAnswer.AllowOnce);
                },
            };
            int steps = 0;
            session.Changed += () =>
            {
                foreach (var step in session.Answer.OfType<ToolStepSegment>().Skip(steps))
                {
                    steps++;
                    log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} palette tool {step.Tool}: {step.Summary} -> {(step.IsError ? "error: " : "")}{step.Result.ReplaceLineEndings(" | ")}");
                }
            };
            await new PaletteHost(island, machine, log.WriteLine).RunAsync(session);
            log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} palette answer: {session.AnswerText.ReplaceLineEndings(" | ")}");
            log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} palette memory after answer: {Memory()}");
            await Task.Delay(2000);
            await Step(island, machine, log, (IslandState.Hidden, 0, 0, null));
            GC.Collect();
            GC.WaitForPendingFinalizers();
            await Task.Delay(1000);
            log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} palette memory hidden again: {Memory()}");
        }
        catch (Exception e)
        {
            log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} palette run failed: {e}");
        }

        PInvoke.PostQuitMessage(0);
    }

    private static async Task Step(IslandWindow island, IslandStateMachine machine, TextWriter? log, (IslandState State, double W, double H, MascotState? React) next)
    {
        try
        {
            var from = machine.State;
            machine.TransitionTo(next.State);
            island.Mascot.Follow(from, next.State);
            if (next.React is MascotState react)
            {
                island.Mascot.Set(react);
            }

            island.PanelLook = next.State is IslandState.Panels or IslandState.Palette;
            var (monitorW, monitorH) = island.MonitorSize;
            var geometry = IslandGeometry.For(next.State, Math.Min(next.W, 0.6 * monitorW), Math.Min(next.H, 0.5 * monitorH));
            await island.MorphTo(geometry, Spring.PresetFor(from, next.State));
            double rate = island.LastMorphMs > 0 ? island.LastMorphTicks / (island.LastMorphMs / 1000) : 0;
            log?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {from}->{next.State} {geometry.Width}x{geometry.Depth} ticks={island.LastMorphTicks} wall={island.LastMorphMs:F0}ms rate={rate:F1}/s");
        }
        catch (Exception e)
        {
            log?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} step to {next.State} failed: {e}");
        }
    }

    /// <summary>
    /// Temporary wiring for --rambler: Win+Alt+V is registered here until the triggers own every hotkey, and the
    /// provider comes from the command line until settings exist (--provider-id, --provider-kind, --base-url,
    /// --model; the key is read from Credential Manager under the id). Speech to text is local whisper.cpp when
    /// tools/whisper/Get-Whisper.ps1 has run, or the provider's /audio/transcriptions with --stt-provider.
    /// Without a provider the note is the raw transcript.
    /// </summary>
    private static void StartRambler(string[] args, IslandWindow island, IslandStateMachine machine, TextWriter log)
    {
        ProviderConfig? config = Arg(args, "--provider-id") is { } id
            ? new ProviderConfig(id, Enum.Parse<ProviderKind>(Arg(args, "--provider-kind") ?? nameof(ProviderKind.OpenAICompatible)), Arg(args, "--base-url"), Arg(args, "--model"))
            : null;
        // Only a configured provider needs HTTP; local whisper with raw notes never opens a connection.
        var http = config is null ? null : new HttpClient();
        ISpeechToText? speech = null;
        ProviderSend? speechSend = null;
        if (args.Contains("--stt-provider") && config is { Kind: ProviderKind.OpenAICompatible, BaseUrl: { } url })
        {
            speechSend = new ProviderSend(config.Id, Arg(args, "--stt-model") ?? "whisper-1");
            speech = new OpenAITranscriber(http!, url, CredentialStore.Read(config.Id), speechSend.Model);
        }
        else if (WhisperTranscriber.IsInstalled(WhisperTranscriber.DefaultDirectory))
        {
            speech = new WhisperTranscriber(WhisperTranscriber.DefaultDirectory);
        }

        if (speech is null)
        {
            log.WriteLine("rambler: no speech to text. Run tools/whisper/Get-Whisper.ps1, or pass --stt-provider with an OpenAI-compatible provider.");
            return;
        }

        RamblerStructure? structure = null;
        if (config is { DefaultModel: { } model })
        {
            var provider = ProviderSetup.Load(config, http!);
            structure = new RamblerStructure(new ProviderSend(config.Id, model), (transcript, ct) => RamblerNote.StructureAsync(provider, model, transcript, ct));
        }

        var scratchpad = new ScratchpadFile(ScratchpadFile.DefaultPath);
        var sendLog = new SendLog(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tomte", "send-log.jsonl"));
        var session = new Rambler(island, machine, speech, speechSend, structure, scratchpad, sendLog, log.WriteLine);
        // Bounce is raised on the island's thread, where the mascot's level must be set.
        session.Bounce += level => island.Mascot.Level = level;
        RamblerHotkey.Start(island.Queue, session.Begin, session.End, log.WriteLine);
        log.WriteLine($"rambler: speech={speech.GetType().Name} structure={config?.Id ?? "none, raw transcript"} scratchpad={scratchpad.FilePath}");
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>Routes await continuations back to the UI thread's dispatcher queue.</summary>
    private sealed class DispatcherQueueContext(Windows.System.DispatcherQueue queue) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => queue.TryEnqueue(() => d(state));
    }
}
