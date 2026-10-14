using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using Tomte.AI;
using Tomte.AI.Mcp;
using Tomte.AI.Palette;
using Tomte.Core;
using Tomte.Core.Device;
using Tomte.Core.Media;
using Tomte.Core.Rambler;
using Tomte.Core.Shortcuts;
using Tomte.Core.Storage;
using Tomte.Core.Triggers;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Shell;

internal static class Program
{
    private const int QuitHotKey = 2;
    private const int MascotHotKey = 3;

    // --debug-mascot: Win+Alt+Shift+M steps the reactions the AI layer and the rambler will set.
    private static readonly MascotState[] MascotCycle = [MascotState.Rambler, MascotState.Thinking, MascotState.Result, MascotState.Error, MascotState.Compact];

    private static Process? settingsProcess;
    private static bool startingSettings;

    [STAThread]
    private static unsafe int Main(string[] args)
    {
        if (args.Contains("--settings"))
        {
            RunSettings();
            return 0;
        }

        bool debugMascot = args.Contains("--debug-mascot");
        int paletteArg = Array.IndexOf(args, "--palette");
        string? palettePrompt = paletteArg >= 0 && paletteArg + 1 < args.Length ? args[paletteArg + 1] : null;
        bool rambler = args.Contains("--rambler");
        // No prompt box renders yet, so circle-to-ask takes its typed question from here.
        string? captureQuestion = Arg(args, "--debug-capture");
        // --debug-timer <minutes>: the only way to start the timer until the island takes input.
        TimeSpan? debugTimer = null;
        if (args.Contains("--debug-timer"))
        {
            if (!TimerRun.TryParseMinutes(Arg(args, "--debug-timer"), out var duration))
            {
                // A windowed app has no console of its own; this reaches a redirected stderr.
                Console.Error.WriteLine(FormattableString.Invariant($"usage: --debug-timer <minutes>, a number from {TimerRun.MinDuration.TotalMinutes:0.###} (one second) to {TimerRun.MaxDuration.TotalMinutes:0} (one day)"));
                return 2;
            }

            debugTimer = duration;
        }

        string? logArg = args.FirstOrDefault(a => a.StartsWith("--debug-log", StringComparison.Ordinal));
        bool debug = debugMascot || rambler || palettePrompt is not null || captureQuestion is not null || debugTimer is not null || logArg is not null;

        using var island = new IslandWindow { ForceReduceMotion = args.Contains("--debug-reduce-motion") };
        SynchronizationContext.SetSynchronizationContext(new DispatcherQueueContext(island.Queue));
        // Stand-ins for the settings until they exist: the off switch and a cap colour as RRGGBB hex.
        island.Mascot.Enabled = !args.Contains("--debug-mascot-off");
        int cap = Array.IndexOf(args, "--debug-cap");
        if (cap >= 0 && cap + 1 < args.Length)
        {
            island.Mascot.CapColor = Convert.ToUInt32(args[cap + 1], 16);
        }

        TextWriter? log = null;
        int reaction = 0;
        if (debug)
        {
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tomte");
            Directory.CreateDirectory(dir);
            // Synchronized: the rambler's capture thread writes here too. --debug-log=path gives this run its own file;
            // the shared default is opened for shared writing.
            log = TextWriter.Synchronized(logArg is { Length: > 12 }
                ? new StreamWriter(new FileStream(logArg["--debug-log=".Length..], FileMode.Append, FileAccess.Write, FileShare.ReadWrite)) { AutoFlush = true }
                : OpenLog(dir));
            island.Log = log.WriteLine;
            log.WriteLine($"{DateTime.Now:O} start args=[{string.Join(' ', args)}] scale={island.Scale}");
            // An exception that escapes a native callback fails fast with no managed trace; this is the only record of it.
            AppDomain.CurrentDomain.FirstChanceException += (_, e) => log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} first chance on thread {Environment.CurrentManagedThreadId}: {e.Exception}");
            const HOT_KEY_MODIFIERS chord = HOT_KEY_MODIFIERS.MOD_WIN | HOT_KEY_MODIFIERS.MOD_ALT | HOT_KEY_MODIFIERS.MOD_SHIFT | HOT_KEY_MODIFIERS.MOD_NOREPEAT;
            if (logArg is not null)
            {
                // Measurement runs need a clean exit so the input-thread timing lands in the log. Thread hotkeys arrive
                // in the loop below with a null window.
                log.WriteLine($"hotkeys: Win+Alt+Shift+Q quit={PInvoke.RegisterHotKey(HWND.Null, QuitHotKey, chord, 'Q')}");
            }

            if (debugMascot)
            {
                log.WriteLine($"hotkeys: Win+Alt+Shift+M mascot={PInvoke.RegisterHotKey(HWND.Null, MascotHotKey, chord, 'M')}");
                StartMascotFeed(island);
            }
        }

        // --debug-settings <path>: a settings file of this run's own, so a test never edits the user's.
        using var store = new SettingsStore(Arg(args, "--debug-settings"));
        var pause = new PauseState();
        // Beside the settings file, so a run with its own settings keeps its timer to itself too.
        var timer = new IslandTimer(Path.Combine(Path.GetDirectoryName(store.FilePath)!, Path.GetFileName(IslandTimer.DefaultPath)));
        Action<string>? logLine = log is null ? null : line => log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
        var triggers = new TriggerController(island, store, pause, timer, logLine);
        if (debugTimer is TimeSpan length)
        {
            timer.Run = TimerRun.Starting(DateTimeOffset.Now, length);
        }

        StartPanels(island, triggers, store, logLine, args.Contains("--debug-tabs"));
        if (rambler)
        {
            triggers.Rambler = CreateRambler(args, island, triggers.Machine, log!);
        }

        if (palettePrompt is not null)
        {
            _ = PaletteRun(island, triggers, store, log!, palettePrompt);
        }

        if (captureQuestion is not null)
        {
            triggers.Captured = (overlay, crop, foreground) => AskAboutCapture(island, triggers, store, log!, captureQuestion, overlay, crop, foreground);
        }

        using var tray = new TrayIcon(pause);
        tray.OpenIsland += triggers.Toggle;
        tray.OpenSettings += OpenSettings;
        tray.Paused += triggers.Hide;
        tray.Quit += () => PInvoke.PostQuitMessage(0);

        MSG message;
        while (PInvoke.GetMessage(&message, HWND.Null, 0, 0).Value > 0)
        {
            if (message.message == PInvoke.WM_HOTKEY && message.hwnd == HWND.Null)
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

        triggers.Dispose();
        log?.Dispose();
        return 0;
    }

    // Kept out of Main so the WinUI assemblies load only in the settings process.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunSettings() => Tomte.Panels.SettingsWindow.Run();

    // The settings window runs in its own process and exits when closed, so none of its memory stays in the shell.
    // Called on the island's thread after the user's own click (the tray menu, a tile), which is when Windows lets
    // Tomte hand the foreground on.
    private static async void OpenSettings()
    {
        if (settingsProcess is { HasExited: false })
        {
            // The never-activated island may set the foreground only while that click is the last input; when
            // Windows refuses, the open settings window flashes in the taskbar instead of coming to the front.
            settingsProcess.Refresh();
            PInvoke.SetForegroundWindow(new HWND(settingsProcess.MainWindowHandle));
            return;
        }

        if (startingSettings)
        {
            return;
        }

        startingSettings = true;
        try
        {
            settingsProcess?.Dispose();
            // Starting a process takes tens of milliseconds, too long for the island's thread. The new process
            // gets the foreground right this one has from the click, so its window opens in front.
            settingsProcess = await Task.Run(() =>
            {
                var started = Process.Start(new ProcessStartInfo(Environment.ProcessPath!, "--settings"));
                if (started is not null)
                {
                    PInvoke.AllowSetForegroundWindow((uint)started.Id);
                }

                return started;
            });
        }
        catch (Win32Exception)
        {
            // The exe could not be started again (moved, blocked): nothing opens.
            settingsProcess = null;
        }
        finally
        {
            startingSettings = false;
        }
    }

    /// <summary>
    /// The panel host with the Media panel, the Compact now-playing row and the track-change event. The
    /// event is the one thing that listens to media while the island is hidden, and only when the user
    /// turned it on in settings.
    /// </summary>
    private static void StartPanels(IslandWindow island, TriggerController triggers, SettingsStore store, Action<string>? log, bool debugTabs)
    {
        var media = new MediaTracker(action => island.Queue.TryEnqueue(() => action()), () => DateTimeOffset.UtcNow, log);
        var panels = new PanelHost(island, triggers.Machine, log) { Resize = triggers.ResizePanels };
        panels.Register(new MediaPanel(island, media, () => DateTimeOffset.UtcNow, log));
        // tomte.db next to the settings file in use, so a run with its own settings file has its own tiles too.
        var shortcuts = new ShortcutStore(new TomteDb(Path.Combine(Path.GetDirectoryName(store.FilePath)!, "tomte.db")), () => store.Current.ScriptShortcuts);
        panels.Register(new ShortcutsPanel(island, shortcuts, () => store.Current.ScriptShortcuts, OpenSettings, log));
        if (debugTabs)
        {
            // --debug-tabs: an empty Shelf-sized tab to switch to, until a second panel exists.
            panels.Register(new BlankPanel());
            panels.Select(PanelId.Media);
        }

        triggers.Panels = panels;
        // The row lives on the state changes and ContentReady it listens to.
        _ = new NowPlayingRow(island, triggers, media, log);

        bool announcing = false;
        media.TrackChanged += playing =>
        {
            if (announcing)
            {
                log?.Invoke($"media: track changed, raising the event ({playing.App})");
                triggers.Raise(new IslandEvent(playing.Title, DateTimeOffset.Now) { Source = IslandEventSource.TrackChange });
            }
        };
        Apply(store.Current);
        // Changed arrives on a pool thread.
        store.Changed += settings => island.Queue.TryEnqueue(() => Apply(settings));

        void Apply(TomteSettings settings)
        {
            if (settings.MediaTrackChangeEvent == announcing)
            {
                return;
            }

            announcing = settings.MediaTrackChangeEvent;
            // No positions and no art: a hidden island needs only the title to know the track changed.
            if (announcing)
            {
                media.Watch(timeline: false, art: false);
            }
            else
            {
                media.Unwatch(timeline: false, art: false);
            }
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
    /// Circle-to-ask after the lasso: the circled region as a chip with the question. Kept out of Main so
    /// Tomte.AI loads only on the first capture.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task AskAboutCapture(IslandWindow island, TriggerController triggers, SettingsStore store, TextWriter log, string question, CaptureOverlay overlay, PixelRect crop, nint foreground)
    {
        long released = Stopwatch.GetTimestamp();
        var png = await WindowCapture.CaptureMonitorRegionPngAsync(overlay.Monitor, crop, CancellationToken.None);
        var (w, h) = overlay.Lasso.BoxDips;
        var chip = ContextChips.Lasso(png, w, h);
        File.WriteAllBytes(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tomte", "capture-last.png"), png);
        log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} capture chip \"{chip.Label}\" px={crop} points={overlay.Lasso.Points.Count} png={png.Length}B in {Stopwatch.GetElapsedTime(released).TotalMilliseconds:F1}ms");
        await PaletteRun(island, triggers, store, log, question, chip, foreground);
    }

    /// <summary>
    /// --palette "prompt" [--screenshot] [--dry]: the debug driver. Opens the palette on the island, runs the
    /// prompt against local Ollama with the window chip on and an in-memory shelf and scratchpad, logs the
    /// stream and the tool steps, retracts, raises each device action as an island event and undoes it, then
    /// quits. Confirmations are allowed once each, as a user would; --dry keeps device tools from changing anything.
    /// Destructive requests (lock, sleep, restart, shut down) are denied without --dry: nobody is there to answer.
    /// A request from an MCP server's tool is always denied here, with --dry too: the dry run is Tomte's own, and
    /// a server's tool would really run. Such a tool runs in a driven run only when the user set it to Read or
    /// answered "always allow" for it before.
    /// Circle-to-ask passes its lasso chip and the foreground it saw before the overlay took focus.
    /// </summary>
    private static async Task PaletteRun(IslandWindow island, TriggerController triggers, SettingsStore store, TextWriter log, string prompt, ContextChip? lasso = null, nint foreground = 0)
    {
        try
        {
            // The island is still hidden, so the foreground is the window the user had in front; Tomte.AI
            // loads here, not in Main, so a normal start never touches it.
            if (lasso is null)
            {
                foreground = WindowContext.Foreground();
                await Task.Delay(500);
            }

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
            if (lasso is not null)
            {
                chips.Insert(0, lasso);
            }

            log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} palette memory before: {Memory()}");
            var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tomte", "send-log.jsonl");
            // --dry keeps device tools from changing anything; the grants come from settings.json as they will for the real palette.
            bool dry = Environment.GetCommandLineArgs().Contains("--dry");
            var tools = new BuiltInTools(shelf, scratchpad, DesktopEffects.CopyToClipboard, DesktopEffects.OpenUrl, dry ? (n, i) => BuiltInTools.RunDevice(n, i, dryRun: true) : null);
            // Servers start on the send; disposing at the end of the run stops them, as closing the palette does.
            await using var mcp = McpTools.For(store.Current.McpServers);
            var session = new PaletteSession(ProviderSetup.Create(config, http, null), config, new PaletteOptions(), new SendLog(logPath), tools, new PermissionGrants(store), chips)
            {
                Prompt = prompt,
                Mcp = mcp,
                ConfirmTool = (request, _) =>
                {
                    // Nobody is at the keyboard in a driven run: refuse what has no undo and no bound, and
                    // never approve a tool of an outside server on the user's behalf.
                    bool outside = mcp?.Owns(request.Tool) == true;
                    bool deny = outside || (!dry && (request.Tier == ActionTier.Destructive || request.Tool == BuiltInTools.OpenApp));
                    string answer = outside ? "deny, an MCP tool is not approved with nobody at the keyboard" : deny ? "deny, needs --dry" : "allow once";
                    log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} palette confirm {request.Tier}: {request.Summary} -> {answer}");
                    return Task.FromResult(deny ? PermissionAnswer.Deny : PermissionAnswer.AllowOnce);
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
            var host = new PaletteHost(island, triggers.Machine, log.WriteLine);
            await host.RunAsync(session);
            log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} palette answer: {session.AnswerText.ReplaceLineEndings(" | ")}");
            log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} palette memory after answer: {Memory()}");
            await Task.Delay(2000);
            triggers.Hide();
            // The controller owns every reveal, so a device event goes to it like any other: the pause, the suppression
            // check, the Focus queue and the 4 s Compact hold are its to apply.
            foreach (var e in host.Events)
            {
                triggers.Raise(new IslandEvent(e.Line, DateTimeOffset.Now));
                log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} palette event raised: {e.Line}{(e.Undo is null ? "" : " [undo]")}");
            }

            if (host.Events.Count > 0)
            {
                // Long enough for the hold and the retract; then the undos run so a debug run leaves the PC as it was.
                await Task.Delay(4500);
                foreach (var e in host.Events)
                {
                    if (e.Undo is { } undo)
                    {
                        log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} palette undo: {undo().Message}");
                    }
                }
            }
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

    /// <summary>
    /// --rambler: the hold-to-talk session Win+Alt+V drives. The provider comes from the command line until
    /// settings carry it (--provider-id, --provider-kind, --base-url, --model; the key is read from Credential
    /// Manager under the id). Speech to text is local whisper.cpp when tools/whisper/Get-Whisper.ps1 has run, or
    /// the provider's /audio/transcriptions with --stt-provider and --stt-model. Without a provider the note is
    /// the raw transcript. Kept out of Main so Tomte.AI loads only for this switch.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Rambler? CreateRambler(string[] args, IslandWindow island, IslandStateMachine machine, TextWriter log)
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
            return null;
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
        log.WriteLine($"rambler: speech={speech.GetType().Name} structure={config?.Id ?? "none, raw transcript"} scratchpad={scratchpad.FilePath}");
        return session;
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private sealed class BlankPanel : IPanel
    {
        public PanelId Id => PanelId.Shelf;

        public void Shown(Windows.UI.Composition.ContainerVisual body, System.Numerics.Vector2 size)
        {
        }

        public void Hidden()
        {
        }

        public void Press(System.Numerics.Vector2 point)
        {
        }

        public void Wheel(System.Numerics.Vector2 point, int delta)
        {
        }
    }

    /// <summary>Routes await continuations back to the UI thread's dispatcher queue.</summary>
    private sealed class DispatcherQueueContext(Windows.System.DispatcherQueue queue) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => queue.TryEnqueue(() => d(state));
    }
}
