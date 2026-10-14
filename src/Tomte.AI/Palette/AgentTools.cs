using System.Globalization;
using System.Text.Json;
using Tomte.Core;
using Tomte.Core.Device;
using Tomte.Core.Rambler;

namespace Tomte.AI.Palette;

/// <summary>
/// The tools the model sees: the five built-in ones, the timer, one per device action, and for voice the note
/// step and the shortcut tiles. Definitions, argument validation, permission tiers and execution live here.
/// Clipboard, URL opening, the device actions and the shortcut launcher are injected so tests and the harness
/// can fake or dry-run them; without a timer, set_timer reports that it is unavailable.
/// </summary>
public sealed class BuiltInTools(
    IShelf shelf,
    IScratchpad scratchpad,
    Action<string> copyToClipboard,
    Action<string> openUrl,
    Func<string, JsonElement, ActionResult>? device = null,
    IslandTimer? timer = null,
    Func<string, bool>? launchShortcut = null)
{
    public const string ReadShelfItem = "read_shelf_item";
    public const string WriteScratchpad = "write_scratchpad";
    public const string CreateShelfFile = "create_shelf_file";
    public const string OpenUrl = "open_url";
    public const string CopyToClipboard = "copy_to_clipboard";
    public const string SetDarkMode = "set_dark_mode";
    public const string SetNightLight = "set_night_light";
    public const string SetBrightness = "set_brightness";
    public const string SetWifi = "set_wifi";
    public const string SetBluetooth = "set_bluetooth";
    public const string SetVolume = "set_volume";
    public const string SetMute = "set_mute";
    public const string SetMicMute = "set_mic_mute";
    public const string ListOutputDevices = "list_output_devices";
    public const string SetOutputDevice = "set_output_device";
    public const string OpenApp = "open_app";
    public const string OpenSettingsPage = "open_settings_page";
    public const string LockPc = "lock_pc";
    public const string SleepPc = "sleep_pc";
    public const string RestartPc = "restart_pc";
    public const string ShutDownPc = "shut_down_pc";
    public const string SetTimer = "set_timer";
    public const string SaveNote = "save_note";
    public const string OpenShortcut = "open_shortcut";

    private sealed record Param(string Name, string Type, bool Required = true);

    // Action is the Tomte.Core.Device entry ("Group.Method") the tool runs; its tier comes from there.
    private sealed record Tool(string Name, string Description, Param[] Params, string? Action = null);

    private static readonly Tool[] Table =
    [
        new(ReadShelfItem, "Read the text of an item on the user's shelf.", [new("name", "string")]),
        new(WriteScratchpad, "Add text to the user's scratchpad note. Appends unless mode is \"replace\".", [new("text", "string"), new("mode", "string", false)]),
        new(CreateShelfFile, "Create a text file on the user's shelf.", [new("name", "string"), new("content", "string")]),
        new(OpenUrl, "Open an http or https URL in the user's default browser.", [new("url", "string")]),
        new(CopyToClipboard, "Put text on the user's clipboard.", [new("text", "string")]),
        new(SetDarkMode, "Turn Windows dark mode on or off.", [new("on", "boolean")], "Display.SetDarkMode"),
        new(SetNightLight, "Open the Night light setting so the user can turn it on or off.", [new("on", "boolean")], "Display.SetNightLight"),
        new(SetBrightness, "Set the built-in display's brightness, 0 to 100.", [new("percent", "integer")], "Display.SetBrightness"),
        new(SetWifi, "Turn Wi-Fi on or off.", [new("on", "boolean")], "Radios.SetWifi"),
        new(SetBluetooth, "Turn Bluetooth on or off.", [new("on", "boolean")], "Radios.SetBluetooth"),
        new(SetVolume, "Set the speaker volume, 0 to 100.", [new("percent", "integer")], "Audio.SetVolume"),
        new(SetMute, "Mute or unmute the speakers.", [new("muted", "boolean")], "Audio.SetMute"),
        new(SetMicMute, "Mute or unmute the microphone.", [new("muted", "boolean")], "Audio.SetMicMute"),
        new(ListOutputDevices, "List the audio output devices.", [], "Audio.ListOutputDevices"),
        new(SetOutputDevice, "Switch audio output to the device whose name contains the given text.", [new("name", "string")], "Audio.SetOutputDevice"),
        new(OpenApp, "Open an app by name, or bring it to the front if it is already running.", [new("name", "string")], "Apps.OpenApp"),
        new(OpenSettingsPage, "Open a Windows Settings page by its ms-settings: URI, for example ms-settings:display.", [new("uri", "string")], "Apps.OpenSettingsPage"),
        new(LockPc, "Lock the screen. Everything keeps running; the user signs back in. Not sleep, not shut down.", [], "Power.Lock"),
        new(SleepPc, "Put the PC to sleep (low power, resumes where it was). Not lock, not shut down.", [], "Power.Sleep"),
        new(RestartPc, "Restart the PC (everything closes, then Windows starts again).", [], "Power.Restart"),
        new(ShutDownPc, "Shut down the PC completely (everything closes, power off). Not lock, not sleep.", [], "Power.ShutDown"),
        new(SetTimer, "Start a countdown timer that shows on the island and announces when it ends. Replaces a running timer. minutes is the length in minutes: 1 for one minute, 0.5 for thirty seconds.", [new("minutes", "number")]),
        new(SaveNote, "Save what the user dictated as a note on their scratchpad. note is markdown: a short title line starting with \"### \", " +
            "then one \"- [ ] \" line per thing to do, or \"- \" bullets. Keep names, times and numbers exactly as spoken, drop filler words, add nothing.",
            [new("note", "string")]),
        new(OpenShortcut, "Open one of the user's shortcut tiles by its name.", [new("name", "string")]),
    ];

    /// <summary>Every tool in the table, as Settings lists them.</summary>
    public static readonly IReadOnlyList<ToolDefinition> All = [.. Table.Select(t => new ToolDefinition(t.Name, t.Description, Schema(t.Params)))];

    // Typed chat keeps the clipboard and free scratchpad writes. A transcript gets the note step and the shortcut
    // tiles instead, so a dictated sentence has one obvious place to go.
    private static readonly HashSet<string> ChatOnly = [WriteScratchpad, CopyToClipboard];
    private static readonly HashSet<string> VoiceOnly = [SaveNote, OpenShortcut];

    /// <summary>What the typed palette offers the model.</summary>
    public static readonly IReadOnlyList<ToolDefinition> Definitions = [.. All.Where(d => !VoiceOnly.Contains(d.Name))];

    private static readonly HashSet<string> DeviceTools = [.. Table.Where(t => t.Action is not null).Select(t => t.Name)];

    // The clipboard belongs to the user, not the app: replacing it is an external effect.
    private static readonly Dictionary<string, ActionTier> BuiltInTiers = new()
    {
        [ReadShelfItem] = ActionTier.Read,
        [WriteScratchpad] = ActionTier.AppLocalWrite,
        [CreateShelfFile] = ActionTier.AppLocalWrite,
        [OpenUrl] = ActionTier.ExternalEffect,
        [CopyToClipboard] = ActionTier.ExternalEffect,
        [SetTimer] = ActionTier.AppLocalWrite,
        [SaveNote] = ActionTier.AppLocalWrite,
        [OpenShortcut] = ActionTier.ExternalEffect,
    };

    public static readonly IReadOnlyDictionary<string, ActionTier> Tiers =
        Table.ToDictionary(t => t.Name, t => t.Action is null ? BuiltInTiers[t.Name] : DeviceActionTiers.All[t.Action]);

    public static bool IsDeviceTool(string name) => DeviceTools.Contains(name);

    /// <summary>Device actions block on WinRT, COM and WMI, and the shortcut launcher reads the database: these run on the pool.</summary>
    public static bool RunsOffThread(string name) => IsDeviceTool(name) || name == OpenShortcut;

    /// <summary>
    /// What a voice transcript may reach: the device actions, the power ones included, opening a URL, the timer, the
    /// shelf, the note step and, once a launcher is wired, the shortcut tiles. Never an MCP server's tools.
    /// </summary>
    public IReadOnlyList<ToolDefinition> VoiceDefinitions =>
        [.. All.Where(d => !ChatOnly.Contains(d.Name) && (d.Name != OpenShortcut || launchShortcut is not null))];

    // Kept with no caller yet: the scheduled-job runner will filter a job's tool list with it.
    /// <summary>A scheduled job may only carry tools that never need a prompt while unattended; destructive ones never run there.</summary>
    public static bool AllowedInScheduledJobs(string name) => Tiers.TryGetValue(name, out var tier) && tier != ActionTier.Destructive;

    /// <summary>Null when the call can run; otherwise a message for the model.</summary>
    public static string? Validate(string name, JsonElement input)
    {
        var entry = Table.FirstOrDefault(t => t.Name == name);
        if (entry is null)
            return $"There is no tool named {name}.";
        if (input.ValueKind != JsonValueKind.Object)
            return "Arguments must be a JSON object.";
        foreach (var p in entry.Params)
        {
            if (!input.TryGetProperty(p.Name, out var value))
            {
                if (p.Required)
                    return $"{name} needs {Expected(p)} argument \"{p.Name}\".";
                continue;
            }

            bool ok = p.Type switch
            {
                "boolean" => Bool(value) is not null,
                "integer" => Int(value) is not null,
                "number" => Number(value) is not null,
                _ => value.ValueKind == JsonValueKind.String && (!p.Required || value.GetString()!.Length > 0),
            };
            if (!ok)
                return $"{name}: \"{p.Name}\" must be {Expected(p)}.";
        }

        return name switch
        {
            OpenUrl when !IsWebUrl(Arg(input, "url")) => "open_url only opens http and https URLs.",
            CreateShelfFile when !IsFileName(Arg(input, "name")) => "name must be a plain file name with no path separators or device names.",
            WriteScratchpad when input.TryGetProperty("mode", out var mode) && mode.GetString() is not ("append" or "replace") => "mode must be \"append\" or \"replace\".",
            SetBrightness or SetVolume when Int(input.GetProperty("percent")) is < 0 or > 100 => "percent must be between 0 and 100.",
            SetTimer when Duration(input) is null =>
                FormattableString.Invariant($"minutes must be between {TimerRun.MinDuration.TotalMinutes:0.###} (one second) and {TimerRun.MaxDuration.TotalMinutes:0} (one day)."),
            // With nothing left inside its fence, the note says nothing; the transcript is kept as the raw note instead.
            SaveNote when NoteText(input).Length == 0 => "note is empty.",
            OpenApp => Apps.ValidateAppTarget(Arg(input, "name")),
            OpenSettingsPage when !Apps.IsSettingsUri(Arg(input, "uri")) => "A settings page looks like ms-settings:display.",
            _ => null,
        };
    }

    /// <summary>One line for the confirmation prompt and the answer's step list.</summary>
    public static string Describe(string name, JsonElement input) => name switch
    {
        ReadShelfItem => $"Read shelf item \"{Arg(input, "name")}\"",
        WriteScratchpad => $"{(Arg(input, "mode") == "replace" ? "Replace the scratchpad with" : "Add to the scratchpad")} {Arg(input, "text").Length} characters",
        CreateShelfFile => $"Create shelf file \"{Arg(input, "name")}\" ({Arg(input, "content").Length} characters)",
        OpenUrl => $"Open {Arg(input, "url")}",
        CopyToClipboard => $"Copy to the clipboard: \"{Preview(Arg(input, "text"))}\"",
        SetDarkMode => $"Turn dark mode {OnOff(input)}",
        SetNightLight => $"Open Night light settings to turn it {OnOff(input)}",
        SetBrightness => $"Set brightness to {Int(input, "percent")}%",
        SetWifi => $"Turn Wi-Fi {OnOff(input)}",
        SetBluetooth => $"Turn Bluetooth {OnOff(input)}",
        SetVolume => $"Set volume to {Int(input, "percent")}%",
        SetMute => $"{(Bool(input, "muted") ? "Mute" : "Unmute")} the speakers",
        SetMicMute => $"{(Bool(input, "muted") ? "Mute" : "Unmute")} the microphone",
        ListOutputDevices => "List the audio output devices",
        SetOutputDevice => $"Switch audio output to \"{Arg(input, "name")}\"",
        OpenApp => $"Open or focus {Arg(input, "name")}",
        OpenSettingsPage => $"Open {Arg(input, "uri")}",
        LockPc => "Lock the PC",
        SleepPc => "Put the PC to sleep",
        RestartPc => "Restart the PC",
        ShutDownPc => "Shut down the PC",
        SetTimer => Duration(input)?.TotalMinutes is { } minutes ? FormattableString.Invariant($"Set a timer for {minutes:0.##} minute{(minutes == 1 ? "" : "s")}") : "Set a timer",
        SaveNote => $"Save a note to the scratchpad: \"{Preview(NoteText(input))}\"",
        OpenShortcut => $"Open the shortcut \"{Arg(input, "name")}\"",
        _ => name,
    };

    /// <summary>The device half: a validated tool call to its Tomte.Core.Device action. Blocks on WinRT and COM, so callers run it off the UI thread.</summary>
    public static ActionResult RunDevice(string name, JsonElement input, bool dryRun) => name switch
    {
        SetDarkMode => Display.SetDarkMode(Bool(input, "on"), dryRun),
        SetNightLight => Display.SetNightLight(Bool(input, "on"), dryRun),
        SetBrightness => Display.SetBrightness(Int(input, "percent"), dryRun),
        SetWifi => Radios.SetWifi(Bool(input, "on"), dryRun),
        SetBluetooth => Radios.SetBluetooth(Bool(input, "on"), dryRun),
        SetVolume => Audio.SetVolume(Int(input, "percent"), dryRun),
        SetMute => Audio.SetMute(Bool(input, "muted"), dryRun),
        SetMicMute => Audio.SetMicMute(Bool(input, "muted"), dryRun),
        ListOutputDevices => Audio.ListOutputDevices(),
        SetOutputDevice => Audio.SetOutputDevice(Arg(input, "name"), dryRun),
        OpenApp => Apps.OpenApp(Arg(input, "name"), dryRun),
        OpenSettingsPage => Apps.OpenSettingsPage(Arg(input, "uri"), dryRun),
        LockPc => Power.Lock(dryRun),
        SleepPc => Power.Sleep(dryRun),
        RestartPc => Power.Restart(dryRun),
        ShutDownPc => Power.ShutDown(dryRun),
        _ => ActionResult.Fail($"{name} is not a device tool."),
    };

    /// <summary>Runs a validated call. The undo, when there is one, puts the app's own data or the device setting back.</summary>
    public (ToolResultPart Result, Func<ActionResult>? Undo) Run(ToolCallPart call)
    {
        if (Validate(call.Name, call.Input) is { } error)
            return (new ToolResultPart(call.Id, call.Name, error, IsError: true), null);
        try
        {
            if (IsDeviceTool(call.Name))
            {
                var result = (device ?? ((n, i) => RunDevice(n, i, dryRun: false)))(call.Name, call.Input);
                return (new ToolResultPart(call.Id, call.Name, result.Message, !result.Ok), result.UndoAvailable ? result.Undo : null);
            }

            return call.Name switch
            {
                ReadShelfItem => ReadItem(call),
                WriteScratchpad => Write(call),
                CreateShelfFile => Create(call),
                OpenUrl => Open(call),
                SetTimer => StartTimer(call),
                SaveNote => Note(call),
                OpenShortcut => Shortcut(call),
                _ => Copy(call),
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return (new ToolResultPart(call.Id, call.Name, $"{call.Name} failed: {e.Message}", IsError: true), null);
        }
    }

    private (ToolResultPart, Func<ActionResult>?) ReadItem(ToolCallPart call)
    {
        var name = Arg(call.Input, "name");
        var text = shelf.ReadText(name);
        if (text is not null)
            return (new ToolResultPart(call.Id, call.Name, text), null);
        var items = shelf.ItemNames.Count == 0 ? "The shelf is empty." : $"Items on the shelf: {string.Join(", ", shelf.ItemNames)}.";
        return (new ToolResultPart(call.Id, call.Name, $"No readable shelf item named \"{name}\". {items}", IsError: true), null);
    }

    private (ToolResultPart, Func<ActionResult>?) Write(ToolCallPart call)
    {
        var before = scratchpad.Text;
        var text = Arg(call.Input, "text");
        scratchpad.Text = Arg(call.Input, "mode") == "replace" || before.Length == 0 ? text : before.TrimEnd('\n', '\r') + "\n" + text;
        return (new ToolResultPart(call.Id, call.Name, "Written to the scratchpad."), () =>
        {
            scratchpad.Text = before;
            return ActionResult.Done("Scratchpad put back.");
        }
        );
    }

    private (ToolResultPart, Func<ActionResult>?) Create(ToolCallPart call)
    {
        var name = Arg(call.Input, "name");
        var previous = shelf.ReadText(name);
        shelf.CreateFile(name, Arg(call.Input, "content"));
        return (new ToolResultPart(call.Id, call.Name, $"Created \"{name}\" on the shelf."),
            () =>
            {
                if (previous is null)
                    shelf.Remove(name);
                else
                    shelf.CreateFile(name, previous);
                return ActionResult.Done(previous is null ? $"Removed \"{name}\" from the shelf." : $"Put the previous \"{name}\" back.");
            }
        );
    }

    private (ToolResultPart, Func<ActionResult>?) Open(ToolCallPart call)
    {
        openUrl(Arg(call.Input, "url"));
        return (new ToolResultPart(call.Id, call.Name, "Opened in the browser."), null);
    }

    /// <summary>The undo puts back what ran before: no timer, or the one this call replaced.</summary>
    private (ToolResultPart, Func<ActionResult>?) StartTimer(ToolCallPart call)
    {
        if (timer is null)
            return (new ToolResultPart(call.Id, call.Name, "The timer is not available here.", IsError: true), null);
        var previous = timer.Run;
        var run = TimerRun.Starting(DateTimeOffset.Now, Duration(call.Input)!.Value);
        timer.Run = run;
        return (new ToolResultPart(call.Id, call.Name, $"Timer set; it ends at {run.EndsAt.LocalDateTime:HH:mm:ss}."), () =>
        {
            timer.Run = previous;
            return ActionResult.Done(previous is null ? "Timer cancelled." : "The previous timer is back.");
        }
        );
    }

    /// <summary>
    /// The rambler's note: added at the end of the scratchpad under a time heading. The undo takes off only the note,
    /// and only while it is still the end of the scratchpad, so an edit made since is never lost.
    /// </summary>
    private (ToolResultPart, Func<ActionResult>?) Note(ToolCallPart call)
    {
        var before = scratchpad.Text;
        scratchpad.Text = RamblerNotes.Append(before, NoteText(call.Input), DateTime.Now);
        // Appending keeps the text up to its trailing line breaks; everything after that is the note's.
        int kept = before.TrimEnd('\r', '\n').Length;
        string added = scratchpad.Text[kept..];
        return (new ToolResultPart(call.Id, call.Name, "Note saved to the scratchpad."), () =>
        {
            var now = scratchpad.Text;
            if (!now.EndsWith(added, StringComparison.Ordinal))
                return ActionResult.Fail("The scratchpad changed after the note, so the note stays.");
            scratchpad.Text = now[..^added.Length] + before[kept..];
            return ActionResult.Done("Note taken off the scratchpad.");
        }
        );
    }

    private (ToolResultPart, Func<ActionResult>?) Shortcut(ToolCallPart call)
    {
        var name = Arg(call.Input, "name");
        if (launchShortcut is null)
            return (new ToolResultPart(call.Id, call.Name, "Shortcuts are not available here.", IsError: true), null);
        return launchShortcut(name)
            ? (new ToolResultPart(call.Id, call.Name, $"Opened {name}."), null)
            : (new ToolResultPart(call.Id, call.Name, $"No shortcut named \"{name}\" could be opened.", IsError: true), null);
    }

    private (ToolResultPart, Func<ActionResult>?) Copy(ToolCallPart call)
    {
        copyToClipboard(Arg(call.Input, "text"));
        return (new ToolResultPart(call.Id, call.Name, "Copied to the clipboard."), null);
    }

    private static string Arg(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    // Small local models often send "true" or "30" as strings; those are taken too.
    private static bool? Bool(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        JsonValueKind.String when bool.TryParse(v.GetString(), out var b) => b,
        _ => null,
    };

    private static int? Int(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetInt32(out var i) => i,
        JsonValueKind.String when int.TryParse(v.GetString(), out var i) => i,
        _ => null,
    };

    // llama3.2:3b escapes the note's line breaks twice and once wrote terminal colour codes into one. A dictated
    // note never means a backslash and an n, or a control character other than a line break or a tab. A model may
    // also wrap the note in a code fence, as it did when the rambler asked for the note on its own.
    private static string NoteText(JsonElement input)
    {
        var note = string.Concat(Arg(input, "note").Replace("\\n", "\n", StringComparison.Ordinal).Where(c => !char.IsControl(c) || c is '\n' or '\t')).Trim();
        if (note.StartsWith("```", StringComparison.Ordinal))
        {
            int firstLine = note.IndexOf('\n');
            note = firstLine < 0 ? "" : note[(firstLine + 1)..].TrimEnd();
            if (note.EndsWith("```", StringComparison.Ordinal))
                note = note[..^3];
            note = note.Trim();
        }

        return note;
    }

    private static TimeSpan? Duration(JsonElement input) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty("minutes", out var v)
            && TimerRun.TryParseMinutes(v.ValueKind == JsonValueKind.String ? v.GetString() : v.GetRawText(), out var duration)
            ? duration
            : null;

    private static double? Number(JsonElement v) => v.ValueKind switch
    {
        JsonValueKind.Number when v.TryGetDouble(out var d) => d,
        JsonValueKind.String when double.TryParse(v.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d) => d,
        _ => null,
    };

    private static bool Bool(JsonElement input, string name) => input.TryGetProperty(name, out var v) && Bool(v) == true;

    private static int Int(JsonElement input, string name) => input.TryGetProperty(name, out var v) ? Int(v) ?? 0 : 0;

    private static string OnOff(JsonElement input) => Bool(input, "on") ? "on" : "off";

    private static string Expected(Param p) => p.Type switch
    {
        "boolean" => "a true or false",
        "integer" => "a whole-number",
        "number" => "a number",
        _ => "a non-empty string",
    };

    private static string Preview(string text) =>
        (text.Length > 40 ? text[..40] + "..." : text).ReplaceLineEndings(" ");

    private static bool IsWebUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    // CON, NUL, COM1 and the like are device names on Windows even with an extension (CON.txt).
    private static readonly HashSet<string> ReservedNames = new(
        ["CON", "PRN", "AUX", "NUL", .. Enumerable.Range(1, 9).SelectMany(i => new[] { $"COM{i}", $"LPT{i}" })],
        StringComparer.OrdinalIgnoreCase);

    private static bool IsFileName(string name) =>
        name.Trim() == name
        && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
        && name is not ("." or "..")
        && !ReservedNames.Contains(name.Split('.')[0]);

    // Plain JSON Schema: type object, typed properties, required list. Anthropic, Gemini and
    // OpenAI-compatible endpoints all take this shape; Gemini drops the schema of a no-argument tool.
    private static JsonElement Schema(Param[] parameters)
    {
        var properties = string.Join(",", parameters.Select(p => $"\"{p.Name}\":{{\"type\":\"{p.Type}\"}}"));
        var required = string.Join(",", parameters.Where(p => p.Required).Select(p => $"\"{p.Name}\""));
        return JsonDocument.Parse($"{{\"type\":\"object\",\"properties\":{{{properties}}},\"required\":[{required}]}}").RootElement;
    }
}
