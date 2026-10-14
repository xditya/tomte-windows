// Runs one palette prompt end to end against local Ollama (ollama serve, llama3.2:3b) and prints
// what the island would show: the provider indicator, the streamed answer, tool calls, their
// permission prompts and the Compact event line after each device action. The shelf holds one
// seeded item, notes.txt, so a tool round trip can happen.
//   PaletteHarness "prompt" [--chip window|screenshot|shelf|scratchpad|selection]... [--yes] [--dry] [--undo] [--automation] [--model id] [--cap usd]
// --yes answers "allow once" to every permission prompt for Tomte's own tools. It never approves a
// tool of an MCP server or a UI Automation request: that prompt is answered "no", because nobody
// read it. Run without --yes to answer such a prompt yourself.
// --dry runs device tools in dry-run mode, so they change nothing on the PC. It does not cover
// open_url and copy_to_clipboard, which still act when allowed, or MCP tools, which have no dry run.
// An MCP tool runs without a prompt only when the user set it to Read in Settings; always-allow
// grants are kept in memory here and start empty, so they cover only answers given in this run.
// --undo runs the undo of every device action at the end, so a run that changed dark mode or the
// volume leaves them as they were. --chip selection reads the selection of the foreground window,
// which from a console is the console itself; use it from a script that launches the harness while
// another window is in front.
// MCP servers come from the user's settings (Settings > MCP servers); they start on the send
// and stop when the harness exits.
// --automation offers the UI Automation tools (list a window's controls, click, type, select), with the allow and
// deny lists from the user's settings. Every list and every action asks; type y or n (--yes answers them no). The
// window in front is this console, so name the window in the prompt ("In Calculator, ...").
// --file path (repeatable) puts that file on the shelf instead of notes.txt and turns the shelf chip
// on, so the last file given is read, sized and sent as the palette would; --chip shelf attaches the
// seeded shelf. --vision marks the model as one that reads images, --context n gives its context size
// (local endpoints do not say). A send too large for the model asks truncate, chunk or cancel on stdin,
// or takes --size truncate|chunk|cancel. --provider anthropic|gemini sends to that cloud provider instead,
// with the key saved in Credential Manager under that name and the model given with --model.
using System.Diagnostics;
using Tomte.AI;
using Tomte.AI.Automation;
using Tomte.AI.Files;
using Tomte.AI.Mcp;
using Tomte.AI.Palette;
using Tomte.Core;

string? prompt = null, model = "llama3.2:3b";
decimal? cap = 2m;
bool yes = false, dry = false, undoAll = false, vision = false, automationOn = false;
int? context = null;
string? size = null;
var files = new List<string>();
var kind = ProviderKind.OpenAICompatible;
var chipsWanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--chip": chipsWanted.Add(args[++i]); break;
        case "--yes": yes = true; break;
        case "--dry": dry = true; break;
        case "--undo": undoAll = true; break;
        case "--automation": automationOn = true; break;
        case "--model": model = args[++i]; break;
        case "--cap": cap = decimal.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--file": files.Add(Path.GetFullPath(args[++i])); break;
        case "--vision": vision = true; break;
        case "--context": context = int.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        case "--size": size = args[++i]; break;
        case "--provider": kind = Enum.Parse<ProviderKind>(args[++i], ignoreCase: true); break;
        default: prompt = args[i]; break;
    }
}

if (prompt is null)
{
    Console.WriteLine("usage: PaletteHarness \"prompt\" [--chip window|screenshot|shelf|scratchpad|selection]... [--yes] [--dry] [--undo] [--automation] [--model id] [--cap usd] [--file path]... [--vision] [--context tokens] [--size truncate|chunk|cancel] [--provider anthropic|gemini]");
    return 1;
}

var config = kind == ProviderKind.OpenAICompatible
    ? new ProviderConfig("ollama", kind, "http://127.0.0.1:11434/v1", model)
    : new ProviderConfig(kind.ToString().ToLowerInvariant(), kind, null, model);
using var http = new HttpClient();
var provider = kind == ProviderKind.OpenAICompatible ? ProviderSetup.Create(config, http, null) : ProviderSetup.Load(config, http);
var seeded = new InMemoryShelf();
seeded.CreateFile("notes.txt", "Milk, eggs, bread.\nCall the dentist on Monday at 10.\nRenew the library card.");
IShelf shelf = files.Count == 0 ? seeded : new DiskShelf(files);
var scratchpad = new InMemoryScratchpad { Text = "Ideas:\n- a quieter alarm" };
var tools = new BuiltInTools(shelf, scratchpad, DesktopEffects.CopyToClipboard, DesktopEffects.OpenUrl,
    dry ? (name, input) => BuiltInTools.RunDevice(name, input, dryRun: true) : null);
var settings = SettingsStore.Load(SettingsStore.DefaultPath);
await using var mcp = McpTools.For(settings.McpServers);
var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tomte", "send-log.jsonl");

nint foreground = WindowContext.Foreground();
var (app, title) = WindowContext.Describe(foreground);
// No desktop watch here: the lock screen and sign-in are refused by their apps, which are on the fixed list.
using var automation = AutomationTools.For(automationOn, settings.UiAutomation, settings.AppRules, foreground, () => false);
var chips = new List<ContextChip>
{
    chipsWanted.Contains("selection") ? SelectionReader.Chip(foreground) : ContextChips.Selection(null),
    ContextChips.Window(app, title),
    ContextChips.Screenshot(app, ct => WindowCapture.CapturePngAsync(foreground, ct)),
    ContextChips.Shelf(shelf),
    ContextChips.Scratchpad(scratchpad),
};
foreach (var chip in chips)
{
    chip.On = chipsWanted.Contains(chip.Kind.ToString()) || (chip.Kind == ContextKind.Selection && chip.On)
        || (chip.Kind == ContextKind.ShelfItem && (chipsWanted.Contains("shelf") || files.Count > 0));
    Console.WriteLine($"chip  {(chip.On ? "[x]" : "[ ]")} {chip.Label}{(chip.Available ? "" : " (unavailable)")}");
}

// Grants stay in memory here: the harness must not write the user's settings.json.
var session = new PaletteSession(provider, config, new PaletteOptions { DailyCapUsd = cap }, new SendLog(logPath), tools, new PermissionGrants(), chips)
{
    Prompt = prompt,
    Mcp = mcp,
    Automation = automation,
    Model = vision || context is not null ? new ModelInfo(model, context, vision ? true : null) : null,
    ChooseSize = (question, _) =>
    {
        Console.WriteLine($"\nsize  {question.Reason}");
        Console.WriteLine($"      [t] {question.Truncate ?? "(cannot truncate)"}\n      [c] {question.Chunk ?? "(cannot chunk)"}\n      [n] {question.Cancel}");
        var answer = size ?? Console.ReadLine()?.Trim().Trim('\uFEFF').ToLowerInvariant();
        Console.WriteLine($"      ({(size is null ? "stdin" : "--size")}: {answer ?? "<closed>"})");
        return Task.FromResult(answer switch { "t" or "truncate" => SizeChoice.Truncate, "c" or "chunk" => SizeChoice.Chunk, _ => SizeChoice.Cancel });
    },
    ConfirmTool = (request, _) =>
    {
        Console.WriteLine();
        Console.WriteLine($"ask   {request.Tier}: {request.Summary}  [y]es / [n]o{(request.OfferAlwaysAllow ? " / [a]lways" : "")}");
        if (yes)
        {
            // As in the shell's driven run: --yes stands in for a person at Tomte's own prompts only.
            // A server's tool would really run, --dry or not, so it is not approved on the user's behalf.
            bool outside = mcp?.Owns(request.Tool) == true || AutomationTools.Owns(request.Tool);
            Console.WriteLine(outside ? "      (auto: no, --yes does not approve an MCP or UI Automation tool; run without --yes to answer it)" : "      (auto: yes)");
            return Task.FromResult(outside ? PermissionAnswer.Deny : PermissionAnswer.AllowOnce);
        }

        // A PowerShell pipe prefixes the line with a byte order mark; Trim does not count it as whitespace.
        var answer = Console.ReadLine()?.Trim().Trim('﻿').ToLowerInvariant();
        Console.WriteLine($"      (stdin: {answer ?? "<closed>"})");
        return Task.FromResult(answer switch
        {
            "y" or "yes" => PermissionAnswer.AllowOnce,
            "a" or "always" => PermissionAnswer.AlwaysAllow,
            _ => PermissionAnswer.Deny,
        });
    },
};

int printed = 0, steps = 0;
string indicator = "";
var events = new List<DeviceEvent>();
var clock = Stopwatch.StartNew();
session.DeviceActionExecuted += e =>
{
    events.Add(e);
    Console.WriteLine($"\n[{clock.ElapsedMilliseconds,6} ms] compact event (4 s): {e.Line}{(e.Undo is null ? "" : "  [undo]")}");
};
TokenEstimate? estimate = null;
session.Changed += () =>
{
    if (session.Estimate is { } now && now != estimate)
    {
        estimate = now;
        Console.WriteLine($"\n[{clock.ElapsedMilliseconds,6} ms] estimate: {now.Line}");
        foreach (var file in now.Files)
            Console.WriteLine($"         file {file}");
    }

    if (session.ProviderIndicator != indicator)
    {
        indicator = session.ProviderIndicator;
        Console.WriteLine($"\n[{clock.ElapsedMilliseconds,6} ms] {indicator}  (status {session.Status})");
    }

    foreach (var step in session.Answer.OfType<ToolStepSegment>().Skip(steps))
    {
        steps++;
        Console.WriteLine($"\n[{clock.ElapsedMilliseconds,6} ms] tool {step.Tool}: {step.Summary}");
        Console.WriteLine($"         -> {(step.IsError ? "error: " : "")}{step.Result.ReplaceLineEndings(" | ")}{(step.Undo is null ? "" : "  (undo available)")}");
    }

    var text = session.AnswerText;
    if (text.Length > printed)
    {
        Console.Write(text[printed..]);
        printed = text.Length;
    }
};

Console.WriteLine($"\nprompt: {prompt}");
await session.SendAsync();
Console.WriteLine();
Console.WriteLine($"\n[{clock.ElapsedMilliseconds,6} ms] status {session.Status}{(session.Error is null ? "" : ": " + session.Error)}; {session.ToolSteps} tool steps; ${session.SpentTodayUsd:0.00} today of {(session.DailyCapUsd is { } c ? $"${c:0.00}" : "no")} cap");
if (undoAll)
{
    foreach (var e in events.Where(e => e.Undo is not null))
    {
        await Task.Delay(1000);
        Console.WriteLine($"[{clock.ElapsedMilliseconds,6} ms] undo \"{e.Line}\" -> {e.Undo!().Message}");
    }
}

Console.WriteLine($"scratchpad now: {scratchpad.Text.ReplaceLineEndings(" | ")}");
Console.WriteLine($"shelf now: {string.Join(", ", shelf.ItemNames)}");
Console.WriteLine($"send log: {logPath}");
return session.Status == PaletteStatus.Done ? 0 : 2;

/// <summary>Files from disk as shelf items, the last one given newest, as the shelf lists a drop.</summary>
internal sealed class DiskShelf(List<string> paths) : IShelf
{
    public IReadOnlyList<string> ItemNames => [.. paths.Select(Path.GetFileName).Reverse()!];

    public string? ReadText(string name) => null;

    public string? PathOf(string name) => paths.LastOrDefault(p => string.Equals(Path.GetFileName(p), name, StringComparison.OrdinalIgnoreCase));

    public void CreateFile(string name, string content) => Console.WriteLine($"(the harness does not write {name} to disk)");

    public void Remove(string name)
    {
    }
}
