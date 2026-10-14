using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Tomte.Core;

/// <summary>
/// Everything the user can change, with the starting values. Thresholds are in raw mouse counts,
/// milliseconds and DIPs. Start with Windows is not here: its state lives with Windows (the
/// package's StartupTask), and a copy here could disagree with it.
/// </summary>
public sealed record TomteSettings
{
    public HotkeySettings Hotkeys { get; init; } = new();

    public RevealSettings Reveal { get; init; } = new();

    /// <summary>Executable rules, matched by file name without the extension; see <see cref="AppRules"/>.</summary>
    public IReadOnlyList<AppRule> AppRules { get; init; } = [];

    /// <summary>Show Compact with ambient content when no window covers the reveal zone.</summary>
    public bool DesktopPeek { get; init; } = true;

    /// <summary>Reveal on every monitor whose top is a screen edge; false means the primary monitor only.</summary>
    public bool AllMonitors { get; init; } = true;

    /// <summary>The ten-second "push your cursor up here" pill has been shown; replaying the tour clears it.</summary>
    public bool FirstRunHintShown { get; init; }

    /// <summary>
    /// "Attach to the top edge": draw the island as the notch joined to the screen edge instead of the floating
    /// pill. Read from the file only; the settings window does not show it yet.
    /// </summary>
    public bool AttachToTopEdge { get; init; }

    /// <summary>AI tools the user answered "always allow" for, by tool name; the AI layer reads and writes it, the settings window lists and removes.</summary>
    public IReadOnlyList<string> AlwaysAllowedTools { get; init; } = [];

    /// <summary>MCP servers whose tools the AI palette may use; see <see cref="McpServerSettings"/>.</summary>
    public IReadOnlyList<McpServerSettings> McpServers { get; init; } = [];

    /// <summary>
    /// Show the pill for a few seconds when another track starts playing. Off by default; on, Tomte
    /// listens for media changes while the island is hidden.
    /// </summary>
    public bool MediaTrackChangeEvent { get; init; }
}

/// <summary>Chords as text ("Win+Alt+I"); see <see cref="Hotkey.TryParse"/>. Empty means no shortcut.</summary>
public sealed record HotkeySettings
{
    public string Summon { get; init; } = "Win+Alt+I";

    public string Palette { get; init; } = "Win+Alt+A";

    public string CircleToAsk { get; init; } = "Win+Alt+C";

    public string Rambler { get; init; } = "Win+Alt+V";

    public string Scratchpad { get; init; } = "Win+Alt+N";

    public string this[HotkeyAction action] => action switch
    {
        HotkeyAction.Summon => Summon,
        HotkeyAction.Palette => Palette,
        HotkeyAction.CircleToAsk => CircleToAsk,
        HotkeyAction.Rambler => Rambler,
        HotkeyAction.Scratchpad => Scratchpad,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public HotkeySettings With(HotkeyAction action, string chord) => action switch
    {
        HotkeyAction.Summon => this with { Summon = chord },
        HotkeyAction.Palette => this with { Palette = chord },
        HotkeyAction.CircleToAsk => this with { CircleToAsk = chord },
        HotkeyAction.Rambler => this with { Rambler = chord },
        HotkeyAction.Scratchpad => this with { Scratchpad = chord },
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };
}

public sealed record RevealSettings
{
    public bool EdgePush { get; init; } = true;

    /// <summary>Width of the reveal zone in the middle of each monitor's top edge, as a fraction of that edge.</summary>
    public double ZoneFraction { get; init; } = 0.30;

    /// <summary>Upward raw counts within <see cref="PushWindowMs"/> that count as a push, for a mouse.</summary>
    public int PushCounts { get; init; } = 60;

    /// <summary>The same for a precision touchpad, whose input arrives with no device handle and its own scaling.</summary>
    public int TouchpadPushCounts { get; init; } = 60;

    public int PushWindowMs { get; init; } = 400;

    /// <summary>Resting in the zone this long also reveals. 0 turns dwell off.</summary>
    public int DwellMs { get; init; } = 350;

    public bool DragReveal { get; init; } = true;

    /// <summary>A drag within this many DIPs of the top edge, inside the zone, shows the drop surface.</summary>
    public int DragApproachDip { get; init; } = 48;

    /// <summary>With no OLE DragEnter after this long, the drop surface retracts.</summary>
    public int DragEnterTimeoutMs { get; init; } = 600;

    public int RetractMarginDip { get; init; } = 24;

    public int RetractDelayMs { get; init; } = 400;
}

/// <summary>
/// Reads and writes settings.json and reports changes, including ones written by the settings
/// window, which runs in its own process. Changed is raised on a thread-pool thread for changes
/// from outside, and on the caller's thread for <see cref="Save"/>.
/// </summary>
public sealed class SettingsStore : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        // Keeps "Win+Alt+I" readable in the file; it is never embedded in HTML.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Converters = { new JsonStringEnumConverter() },
    };

    private readonly FileSystemWatcher watcher;
    private readonly Lock gate = new();
    private readonly Lock reloading = new();
    private string lastText;
    private bool disposed;

    public SettingsStore(string? path = null)
    {
        FilePath = path ?? DefaultPath;
        string directory = Path.GetDirectoryName(FilePath)!;
        Directory.CreateDirectory(directory);
        Current = Load(FilePath);
        lastText = Serialize(Current);
        watcher = new FileSystemWatcher(directory, Path.GetFileName(FilePath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        watcher.Changed += (_, _) => Reload();
        watcher.Created += (_, _) => Reload();
        watcher.Renamed += (_, _) => Reload();
        watcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// In %LOCALAPPDATA%\Tomte, or in the folder TOMTE_DATA_DIR names: a test run's own settings and database,
    /// which the settings window it opens inherits with the environment. tomte.db lives next to this file.
    /// </summary>
    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetEnvironmentVariable("TOMTE_DATA_DIR") is { Length: > 0 } dir ? dir : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tomte"),
        "settings.json");

    public string FilePath { get; }

    public TomteSettings Current { get; private set; }

    public event Action<TomteSettings>? Changed;

    /// <summary>A missing file gives the defaults. A file that does not parse is kept as settings.json.bad and the defaults are used.</summary>
    public static TomteSettings Load(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<TomteSettings>(File.ReadAllText(path), Json) ?? new();
        }
        catch (FileNotFoundException)
        {
            return new();
        }
        catch (JsonException)
        {
            File.Copy(path, path + ".bad", overwrite: true);
            return new();
        }
    }

    public static string Serialize(TomteSettings settings) => JsonSerializer.Serialize(settings, Json);

    /// <summary>Writes through a temporary file and a rename, so a reader never sees half a file.</summary>
    public void Save(TomteSettings settings)
    {
        string text = Serialize(settings);
        // One temporary file per process: the shell and the settings window both save.
        string temp = FilePath + "." + Environment.ProcessId + ".tmp";
        File.WriteAllText(temp, text);
        Retry(() => File.Move(temp, FilePath, overwrite: true));
        Publish(settings, text);
    }

    /// <summary>
    /// Stops watching. A reload the watcher started before that may still be reading the file; Dispose
    /// waits for it, so once it returns nothing holds the file and Changed is not raised again.
    /// </summary>
    public void Dispose()
    {
        watcher.Dispose();
        lock (reloading)
        {
            disposed = true;
        }
    }

    private void Reload()
    {
        // One reload at a time; the watcher raises several events for one write.
        lock (reloading)
        {
            if (disposed)
            {
                return;
            }

            try
            {
                string text = "";
                Retry(() => text = File.ReadAllText(FilePath));
                var settings = JsonSerializer.Deserialize<TomteSettings>(text, Json) ?? new();
                Publish(settings, Serialize(settings));
            }
            catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException)
            {
                // A write still in progress, or a hand edit that does not parse: keep the current settings.
                // The next write raises another event.
            }
        }
    }

    // The other process holds the file for a moment during its own replace or reload; the shell and
    // the settings window write and watch the same file, so a few short retries cover that window.
    private static void Retry(Action io)
    {
        for (int attempt = 1; ; attempt++)
        {
            try
            {
                io();
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException && attempt < 5)
            {
                Thread.Sleep(20 * attempt);
            }
        }
    }

    private void Publish(TomteSettings settings, string text)
    {
        lock (gate)
        {
            if (text == lastText)
            {
                return;
            }

            lastText = text;
            Current = settings;
        }

        Changed?.Invoke(settings);
    }
}
