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

    /// <summary>Script tiles may run, each once the user confirmed its command. Off until the user turns it on in the settings window.</summary>
    public bool ScriptShortcuts { get; init; }

    /// <summary>Reveal on every monitor whose top is a screen edge; false means the primary monitor only.</summary>
    public bool AllMonitors { get; init; } = true;

    /// <summary>
    /// An item leaves the shelf once a drag out of it was taken by the target: a reference's entry only, never its file;
    /// the shelf's own item only when the target copied its bytes; see ShelfFormats.LeaveAfterDrag. Off, items stay, except
    /// references the target moved away.
    /// </summary>
    public bool RemoveAfterDragOut { get; init; } = true;

    /// <summary>Keep what is copied in clipboard history. Off by default; off, nothing listens to the clipboard.</summary>
    public bool KeepClipboardHistory { get; init; }

    /// <summary>The ten-second "push your cursor up here" pill has been shown; replaying the tour clears it.</summary>
    public bool FirstRunHintShown { get; init; }

    /// <summary>"Not now" on the search's Get Everything card: the "Search every drive" row is not offered again. Settings keeps its button.</summary>
    public bool EverythingOfferDismissed { get; init; }

    /// <summary>The engine the search box's Open in browser row searches with. Google unless the user picks another.</summary>
    public Search.WebSearchEngine WebSearch { get; init; }

    /// <summary>
    /// "Attach to the top edge": draw the island as the notch joined to the screen edge instead of the floating
    /// pill. Read from the file only; the settings window does not show it yet.
    /// </summary>
    public bool AttachToTopEdge { get; init; }

    /// <summary>
    /// Where the user dragged the island along the top edge: DIPs from the middle of the edge to its centre, negative to
    /// the left. 0 is the middle, where "Reset position" and a double click on the tab strip outside its labels put it back.
    /// </summary>
    public double IslandOffset { get; init; }

    /// <summary>AI tools the user answered "always allow" for, by tool name; the AI layer reads and writes it, the settings window lists and removes.</summary>
    public IReadOnlyList<string> AlwaysAllowedTools { get; init; } = [];

    /// <summary>
    /// Keep finished AI conversations and drop zone results in tomte.db for the History tab. On by default; turning it
    /// off deletes them.
    /// </summary>
    public bool KeepAiHistory { get; init; } = true;

    /// <summary>
    /// Seconds a closed AI view (an answer, AI search, a drop zone result, a transcript) can be brought back with Undo: 0
    /// is off, else 5 or 10. A request still running goes on that long. See <see cref="Triggers.UndoClose.Time"/>.
    /// </summary>
    public int UndoSeconds { get; init; } = 10;

    /// <summary>Read the reply to a voice command aloud with Windows speech. Off unless the user turns it on.</summary>
    public bool SpokenReplies { get; init; }

    /// <summary>What Enter picks on the card the words show on after a hold. Insert unless the user changes it; Ask falls back to Insert with no provider.</summary>
    public Rambler.VoiceChoice VoiceEnter { get; init; }

    /// <summary>MCP servers whose tools the AI palette may use; see <see cref="McpServerSettings"/>.</summary>
    public IReadOnlyList<McpServerSettings> McpServers { get; init; } = [];

    /// <summary>
    /// Show the pill for a few seconds when another track starts playing. Off by default; on, Tomte
    /// listens for media changes while the island is hidden.
    /// </summary>
    public bool MediaTrackChangeEvent { get; init; }

    /// <summary>
    /// While something plays, the island stays as the Compact pill showing it instead of hiding. Off by default; on,
    /// Tomte listens for media changes while the island is hidden.
    /// </summary>
    public bool KeepWhilePlaying { get; init; }

    /// <summary>Skills the user turned on, by folder name. A skill not listed is off, so a new one starts off.</summary>
    public IReadOnlyList<string> EnabledSkills { get; init; } = [];

    /// <summary>The skill, by name, behind the custom drop zone while dragging. None by default; it must be on and register a drop zone.</summary>
    public string? DropZoneSkill { get; init; }

    /// <summary>The apps the AI's UI Automation tools may use; the tools themselves are off unless a run turns them on.</summary>
    public UiAutomationSettings UiAutomation { get; init; } = new();

    /// <summary>The AI provider the palette uses. Null keeps the AI layer off. Its key is in Credential Manager, never here.</summary>
    public ProviderConfig? Provider { get; init; }

    /// <summary>
    /// Prices by model id that replace or add to the built-in table, for a model the table lacks or a price that changed.
    /// Read from the file only; the settings window does not show them.
    /// </summary>
    public IReadOnlyDictionary<string, ModelPrice> ModelPrices { get; init; } = new Dictionary<string, ModelPrice>();

    /// <summary>
    /// The one accent colour, as 0xRRGGBB: the selection, the AI, the recording dot. One of <see cref="AccentColors.Choices"/>;
    /// read it through <see cref="AccentColors.Held"/>, since a hand-edited file can hold anything. Whoever draws with it
    /// follows <see cref="SettingsStore.Changed"/>, as for every other setting.
    /// </summary>
    public uint AccentColor { get; init; } = AccentColors.Default;

    /// <summary>The island's look: after the taskbar's theme by default, or always dark or light.</summary>
    public ThemeChoice Theme { get; init; }
}

/// <summary>The island's look as chosen in Settings; see Theme.SchemeFor.</summary>
public enum ThemeChoice
{
    FollowTaskbar,
    Dark,
    Light,
}

/// <summary>The accent colours the settings window offers, as 0xRRGGBB.</summary>
public static class AccentColors
{
    public const uint Default = 0xFF6B4A;

    /// <summary>Coral, amber, sky and green, in the order the settings window shows them.</summary>
    public static IReadOnlyList<uint> Choices { get; } = [Default, 0xF2B84B, 0x7CC4FF, 0x9BE08F];

    /// <summary>Not a colour: the accent Windows uses, whatever it is now.</summary>
    public const uint Windows = 0x1000000;

    /// <summary>The choice to keep: <paramref name="rgb"/> when it is one of the choices or <see cref="Windows"/>, the default otherwise.</summary>
    public static uint Held(uint rgb) => rgb == Windows || Choices.Contains(rgb) ? rgb : Default;

    /// <summary>The colour to draw with for a held choice, given the accent Windows uses now.</summary>
    public static uint Resolve(uint held, uint system) => Held(held) == Windows ? system & 0xFFFFFF : Held(held);
}

/// <summary>Chords as text ("Win+Alt+I"); see <see cref="Hotkey.TryParse"/>. Empty means no shortcut.</summary>
public sealed record HotkeySettings
{
    public string Summon { get; init; } = "Win+Alt+I";

    public string Palette { get; init; } = "Win+Alt+A";

    public string CircleToAsk { get; init; } = "Win+Alt+C";

    public string Rambler { get; init; } = "Win+Alt+V";

    public string Scratchpad { get; init; } = "Win+Alt+N";

    public string Search { get; init; } = "Win+Alt+Space";

    public string this[HotkeyAction action] => action switch
    {
        HotkeyAction.Summon => Summon,
        HotkeyAction.Palette => Palette,
        HotkeyAction.CircleToAsk => CircleToAsk,
        HotkeyAction.Rambler => Rambler,
        HotkeyAction.Scratchpad => Scratchpad,
        HotkeyAction.Search => Search,
        _ => throw new ArgumentOutOfRangeException(nameof(action)),
    };

    public HotkeySettings With(HotkeyAction action, string chord) => action switch
    {
        HotkeyAction.Summon => this with { Summon = chord },
        HotkeyAction.Palette => this with { Palette = chord },
        HotkeyAction.CircleToAsk => this with { CircleToAsk = chord },
        HotkeyAction.Rambler => this with { Rambler = chord },
        HotkeyAction.Scratchpad => this with { Scratchpad = chord },
        HotkeyAction.Search => this with { Search = chord },
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

    public const int ExpandDwellMinMs = 300, ExpandDwellMaxMs = 3000;

    /// <summary>Resting on the pill this long opens the panels; a click or the wheel opens them at once.</summary>
    public int ExpandDwellMs { get; init; } = 1000;

    /// <summary>The expand dwell as used: held to its range, so a hand-edited file cannot open the panels at once or never.</summary>
    [JsonIgnore]
    public int ExpandDwellHeldMs => Math.Clamp(ExpandDwellMs, ExpandDwellMinMs, ExpandDwellMaxMs);

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

    public static string DefaultPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tomte", "settings.json");

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
