using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tomte.AI;
using Tomte.AI.Mcp;
using Tomte.AI.Palette;
using Tomte.AI.Skills;
using Tomte.Core;
using Tomte.Core.Clipboard;
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
    private static string settingsFile = SettingsStore.DefaultPath;

    // The one scratchpad note: the rambler, the palette and the Scratchpad panel all use this instance.
    private static ScratchpadNote Scratchpad = null!;

    [STAThread]
    private static unsafe int Main(string[] args)
    {
        if (args.Contains("--settings"))
        {
            // --settings [path]: the shell passes the settings file it uses, so both open the same folder.
            RunSettings(Arg(args, "--settings"));
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

        // A driven yes writes a note and starts a timer: only into a run's own data folder, never the user's.
        if (Arg(args, "--debug-answer") == "yes" && Arg(args, "--debug-settings") is null)
        {
            Console.Error.WriteLine("usage: --debug-answer yes needs --debug-settings <file>, so the run's notes and timer stay beside that file");
            return 2;
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
        settingsFile = store.FilePath;
        var pause = new PauseState();
        // Beside the settings file, so a run with its own settings keeps its timer, note and logs to itself too.
        string data = Path.GetDirectoryName(store.FilePath)!;
        var timer = new IslandTimer(Path.Combine(data, Path.GetFileName(IslandTimer.DefaultPath)));
        Action<string>? logLine = log is null ? null : line => log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} {line}");
        var triggers = new TriggerController(island, store, pause, timer, logLine);
        if (debugTimer is TimeSpan length)
        {
            timer.Run = TimerRun.Starting(DateTimeOffset.Now, length);
        }

        // The note and clipboard history live beside the settings file too. Nothing here opens the database: the note
        // loads when something first reads it, history on the first copy.
        var db = new TomteDb(Path.Combine(data, "tomte.db"));
        Scratchpad = new ScratchpadNote(db, Path.Combine(data, "scratchpad.md"));
        var history = new ClipboardHistory(db);
        var clipboard = new ClipboardWatch(history, captured => ClipboardListener.Start(captured, () => pause.IsPaused(DateTimeOffset.Now), () => store.Current.AppRules, history.Retention.MaxChars, logLine), logLine);
        // Off means no listener at all. Changed arrives on a pool thread and Apply stays there: starting the listener
        // waits for its window and stopping it waits for its thread, neither of which belongs on the island's thread.
        clipboard.Apply(store.Current.KeepClipboardHistory);
        store.Changed += s => clipboard.Apply(s.KeepClipboardHistory);
        // One store of tiles for the Shortcuts panel and for voice; nothing opens the database until a tile is wanted.
        var shortcuts = new ShortcutStore(db, () => store.Current.ScriptShortcuts);
        StartPanels(island, triggers, store, db, shortcuts, clipboard, logLine);

        if (rambler)
        {
            triggers.Rambler = CreateRambler(args, island, triggers, store, timer, shortcuts, log!);
            if (triggers.Rambler is { } hold && int.TryParse(Arg(args, "--debug-hold"), out int holdMs))
            {
                DebugHold(island, hold, holdMs, Arg(args, "--debug-answer"), args.Contains("--dry"), log!);
            }
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

        // Quitting keeps the history; only turning it off clears it. Typing not yet saved is saved now.
        clipboard.Dispose();
        try
        {
            Scratchpad.FlushAsync().Wait(TimeSpan.FromSeconds(6));
        }
        catch (AggregateException e)
        {
            log?.WriteLine($"{DateTime.Now:HH:mm:ss.fff} scratchpad: not saved at quit ({e.InnerException?.GetType().Name})");
        }

        triggers.Dispose();
        log?.Dispose();
        return 0;
    }

    // Kept out of Main so the WinUI assemblies load only in the settings process.
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void RunSettings(string? settingsPath) => Tomte.Panels.SettingsWindow.Run(settingsPath);

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
                var started = Process.Start(new ProcessStartInfo(Environment.ProcessPath!) { ArgumentList = { "--settings", settingsFile } });
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
    /// The panel host with the Shelf and Media panels, the Compact now-playing row and the track-change event.
    /// The event is the one thing that listens to media while the island is hidden, and only when the user
    /// turned it on in settings.
    /// </summary>
    private static void StartPanels(IslandWindow island, TriggerController triggers, SettingsStore store, TomteDb db, ShortcutStore shortcuts, ClipboardWatch clipboard, Action<string>? log)
    {
        var media = new MediaTracker(action => island.Queue.TryEnqueue(() => action()), () => DateTimeOffset.UtcNow, log);
        var panels = new PanelHost(island, triggers.Machine, log) { Resize = triggers.ResizePanels };
        // In the tabs' order. db is tomte.db next to the settings file in use, so a run with its own settings file has its
        // own shelf, tiles, note and history too.
        panels.Register(StartShelf(island, triggers, store, db, log));
        panels.Register(new ShortcutsPanel(island, shortcuts, () => store.Current.ScriptShortcuts, OpenSettings, log));
        panels.Register(new MediaPanel(island, media, () => DateTimeOffset.UtcNow, log));
        panels.Register(new ScratchpadPanel(island, Scratchpad, log));
        // The panel's switch saves the setting off the UI thread; the watch follows the settings file like every other change.
        panels.Register(new ClipboardPanel(island, clipboard, on => Task.Run(() => store.Save(store.Current with { KeepClipboardHistory = on })), log));
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

    /// <summary>
    /// The shelf takes what is dropped on the island and is the Shelf tab. Its database and its folder sit next to
    /// the settings file, and neither is opened before the first drop or the first time the shelf shows.
    /// </summary>
    private static ShelfPanel StartShelf(IslandWindow island, TriggerController triggers, SettingsStore store, TomteDb db, Action<string>? log)
    {
        string dir = Path.GetDirectoryName(store.FilePath)!;
        var shelf = new ShelfStore(db, Path.Combine(dir, "Shelf"), action => island.Queue.TryEnqueue(() => action()), log);
        // Virtual files are written into the shelf folder as the drop reads them; the drop then goes to the shelf.
        triggers.Drops.Receive = shelf.Receive;
        triggers.Dropped += drop => shelf.Accept(drop);
        _ = new ShelfDropSurface(island, triggers.Machine, triggers.Drops);
        return new ShelfPanel(island, triggers, shelf, log);
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
    /// prompt against local Ollama with the window chip on, an in-memory shelf and the real scratchpad note, logs the
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
            // The one note the rambler and the Scratchpad panel use too; the tools read and write it in memory once loaded.
            var scratchpad = Scratchpad;
            await scratchpad.LoadAsync();
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
            var logPath = SendLogPath;
            // Skills come from beside the settings file when the palette opens, never at start, and only those turned on.
            var skills = await Task.Run(() => SkillSet.Load(SkillSet.FolderFor(store.FilePath), store.Current.EnabledSkills));
            log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} palette skills: [{string.Join(", ", skills.Skills.Select(s => s.Name))}] from {skills.Folder}");
            // --dry keeps device tools from changing anything; the grants come from settings.json as they will for the real palette.
            bool dry = Environment.GetCommandLineArgs().Contains("--dry");
            var tools = new BuiltInTools(shelf, scratchpad, DesktopEffects.CopyToClipboard, DesktopEffects.OpenUrl, dry ? (n, i) => BuiltInTools.RunDevice(n, i, dryRun: true) : null);
            // Servers start on the send; disposing at the end of the run stops them, as closing the palette does.
            await using var mcp = McpTools.For(store.Current.McpServers);
            var session = new PaletteSession(ProviderSetup.Create(config, http, null), config, new PaletteOptions(), new SendLog(logPath), tools, new PermissionGrants(store), chips)
            {
                Prompt = prompt,
                Mcp = mcp,
                Skills = skills,
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
            // "/name" runs the palette command of the skill called name, as picking it from the command list does.
            if (session.Commands.FirstOrDefault(c => "/" + c.Skill == prompt) is { } command)
            {
                session.Prompt = command.Text;
            }

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
    /// the provider's /audio/transcriptions with --stt-provider and --stt-model; --debug-transcript "text" or
    /// --debug-wav file.wav stands in for what the microphone heard. Without a provider the note is the raw
    /// transcript; with one the transcript is a voice command the model reads with the voice tools, and --dry keeps
    /// device tools from changing anything. Kept out of Main so Tomte.AI loads only for this switch.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Rambler? CreateRambler(string[] args, IslandWindow island, TriggerController triggers, SettingsStore store, IslandTimer timer, ShortcutStore shortcuts, TextWriter log)
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

        if (Arg(args, "--debug-transcript") is { } said)
        {
            speech = new DebugSpeech(null, said, null);
        }
        else if (Arg(args, "--debug-wav") is { } wav && speech is not null)
        {
            speech = new DebugSpeech(speech, null, ReadWav(wav));
        }

        if (speech is null)
        {
            log.WriteLine("rambler: no speech to text. Run tools/whisper/Get-Whisper.ps1, or pass --stt-provider with an OpenAI-compatible provider.");
            return null;
        }

        // The one note the palette and the Scratchpad panel use too.
        var scratchpad = Scratchpad;
        var sendLog = new SendLog(SendLogPath);
        Func<Task<PaletteSession>>? voice = null;
        if (config is { DefaultModel: not null })
        {
            var provider = ProviderSetup.Load(config, http!);
            // One per store, kept as long as the store. A voice command asks every time, so the grants change nothing for it.
            var grants = new PermissionGrants(store);
            // An in-memory shelf until the shelf store lands. The tiles open by name; that way never runs a script tile.
            var tools = new BuiltInTools(new InMemoryShelf(), scratchpad, DesktopEffects.CopyToClipboard, DesktopEffects.OpenUrl,
                args.Contains("--dry") ? (n, i) => BuiltInTools.RunDevice(n, i, dryRun: true) : null, timer, launchShortcut: shortcuts.LaunchByName);
            voice = async () =>
            {
                // Voice is offered no skill. The turned-on ones are still read, so open_app keeps refusing their scripts.
                var skills = await Task.Run(() => SkillSet.Load(SkillSet.FolderFor(store.FilePath), store.Current.EnabledSkills));
                var command = new PaletteSession(provider, config, new PaletteOptions(), sendLog, tools, grants, []) { Voice = true, Skills = skills };
                // Raised while the palette is up, the event waits in the triggers' queue and shows once the palette hides.
                command.DeviceActionExecuted += e => triggers.Raise(new IslandEvent(e.Line, DateTimeOffset.Now));
                return command;
            };
        }

        var session = new Rambler(island, triggers.Machine, speech, speechSend, voice, scratchpad, sendLog, log.WriteLine);
        // Bounce is raised on the island's thread, where the mascot's level must be set.
        session.Bounce += level => island.Mascot.Level = level;
        session.CommandDone += command =>
        {
            if (VoiceCommand.ReplyToSpeak(command, store.Current.SpokenReplies) is { } reply)
            {
                _ = SpokenReply.SpeakAsync(reply, log.WriteLine);
            }
        };
        log.WriteLine($"rambler: speech={speech.GetType().Name} voice={(voice is null ? "off, raw transcript" : config!.Id)}");
        return session;
    }

    // Beside the settings file, so a run with its own settings file keeps its send log to itself too.
    private static string SendLogPath => Path.Combine(Path.GetDirectoryName(Path.GetFullPath(settingsFile))!, "send-log.jsonl");

    // What a driven run may say yes to with nobody at the keyboard: the app's own data, kept in the run's folder, and
    // reads. A device action only as a dry run (--dry), so a test never changes a setting on the PC, not even for a
    // moment; open_url has no dry run, so it is always refused.
    private static readonly HashSet<string> KeptToTheRun =
    [
        BuiltInTools.SetTimer, BuiltInTools.SaveNote, BuiltInTools.CreateShelfFile, BuiltInTools.ReadShelfItem, BuiltInTools.ListOutputDevices,
    ];

    /// <summary>
    /// --debug-hold ms [--debug-answer yes|no]: one hold of the rambler chord, posted to the island the way the input
    /// thread posts it, so a run needs neither the global hotkey nor injected keys. Each question the palette asks is
    /// answered after 1.5 s, yes only for what the run keeps to itself (see above); without --debug-answer it waits
    /// for Win+Alt+Enter or Esc. After the hold the run waits out the event pill and quits.
    /// </summary>
    private static void DebugHold(IslandWindow island, Rambler rambler, int holdMs, string? answer, bool dry, TextWriter log)
    {
        rambler.Asking += async request =>
        {
            bool yes = answer == "yes" && (KeptToTheRun.Contains(request.Tool) || (dry && BuiltInTools.IsDeviceTool(request.Tool)));
            if (answer is null)
            {
                return;
            }

            await Task.Delay(1500);
            log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} debug: answers {(yes ? "yes" : "no")}");
            // Only to the question it was decided for: by now the user may have answered and another may be up.
            rambler.Answer(yes, to: request);
        };
        rambler.Retracted += async () =>
        {
            await Task.Delay(4500);
            log.WriteLine($"{DateTime.Now:HH:mm:ss.fff} debug: memory hidden again: {Memory()}");
            PInvoke.PostQuitMessage(0);
        };
        _ = Hold();

        async Task Hold()
        {
            await Task.Delay(1000);
            PInvoke.PostMessage(island.Hwnd, InputThread.MsgHotkey, (nuint)HotkeyAction.Rambler + 1, 0);
            await Task.Delay(holdMs);
            PInvoke.PostMessage(island.Hwnd, InputThread.MsgHotkeyReleased, 1, 0);
        }
    }

    /// <summary>16 kHz mono 16-bit PCM, as the speech to text takes it: the samples of the "data" chunk.</summary>
    private static float[] ReadWav(string path)
    {
        var bytes = File.ReadAllBytes(path);
        int at = 12;
        while (at + 8 <= bytes.Length && System.Text.Encoding.ASCII.GetString(bytes, at, 4) != "data")
        {
            at += 8 + BitConverter.ToInt32(bytes, at + 4);
        }

        var pcm = MemoryMarshal.Cast<byte, short>(bytes.AsSpan(at + 8));
        var samples = new float[pcm.Length];
        for (int i = 0; i < pcm.Length; i++)
        {
            samples[i] = pcm[i] / 32768f;
        }

        return samples;
    }

    private static string? Arg(string[] args, string name)
    {
        int i = Array.IndexOf(args, name);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    /// <summary>A known text, or a recorded file through the real speech to text, in place of what the microphone heard.</summary>
    private sealed class DebugSpeech(ISpeechToText? inner, string? text, float[]? samples) : ISpeechToText
    {
        public Task<string> TranscribeAsync(float[] recorded, CancellationToken ct = default) =>
            text is not null ? Task.FromResult(text) : inner!.TranscribeAsync(samples ?? recorded, ct);
    }

    /// <summary>Routes await continuations back to the UI thread's dispatcher queue.</summary>
    private sealed class DispatcherQueueContext(Windows.System.DispatcherQueue queue) : SynchronizationContext
    {
        public override void Post(SendOrPostCallback d, object? state) => queue.TryEnqueue(() => d(state));
    }
}
