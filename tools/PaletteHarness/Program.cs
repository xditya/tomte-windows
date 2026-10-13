// Runs one palette prompt end to end against local Ollama (ollama serve, llama3.2:3b) and prints
// what the island would show: the provider indicator, the streamed answer, tool calls and their
// permission prompts. The shelf holds one seeded item, notes.txt, so a tool round trip can happen.
//   PaletteHarness "prompt" [--chip window|screenshot|shelf|scratchpad|selection]... [--yes] [--model id] [--cap usd]
// --yes answers every permission prompt with "allow once". --chip selection reads the selection of
// the foreground window, which from a console is the console itself; use it from a script that
// launches the harness while another window is in front.
using System.Diagnostics;
using Tomte.AI;
using Tomte.AI.Palette;
using Tomte.Core;

string? prompt = null, model = "llama3.2:3b";
decimal? cap = 2m;
bool yes = false;
var chipsWanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--chip": chipsWanted.Add(args[++i]); break;
        case "--yes": yes = true; break;
        case "--model": model = args[++i]; break;
        case "--cap": cap = decimal.Parse(args[++i], System.Globalization.CultureInfo.InvariantCulture); break;
        default: prompt = args[i]; break;
    }
}

if (prompt is null)
{
    Console.WriteLine("usage: PaletteHarness \"prompt\" [--chip window|screenshot|shelf|scratchpad|selection]... [--yes] [--model id] [--cap usd]");
    return 1;
}

var config = new ProviderConfig("ollama", ProviderKind.OpenAICompatible, "http://127.0.0.1:11434/v1", model);
using var http = new HttpClient();
var provider = ProviderSetup.Create(config, http, null);
var shelf = new InMemoryShelf();
shelf.CreateFile("notes.txt", "Milk, eggs, bread.\nCall the dentist on Monday at 10.\nRenew the library card.");
var scratchpad = new InMemoryScratchpad { Text = "Ideas:\n- a quieter alarm" };
var tools = new BuiltInTools(shelf, scratchpad, DesktopEffects.CopyToClipboard, DesktopEffects.OpenUrl);
var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tomte", "send-log.jsonl");

nint foreground = WindowContext.Foreground();
var (app, title) = WindowContext.Describe(foreground);
var chips = new List<ContextChip>
{
    ContextChips.Selection(chipsWanted.Contains("selection") ? SelectionReader.Read(foreground) : null),
    ContextChips.Window(app, title),
    ContextChips.Screenshot(app, ct => WindowCapture.CapturePngAsync(foreground, ct)),
    ContextChips.Shelf(shelf),
    ContextChips.Scratchpad(scratchpad),
};
foreach (var chip in chips)
{
    chip.On = chipsWanted.Contains(chip.Kind.ToString()) || (chip.Kind == ContextKind.Selection && chip.On);
    Console.WriteLine($"chip  {(chip.On ? "[x]" : "[ ]")} {chip.Label}{(chip.Available ? "" : " (unavailable)")}");
}

var session = new PaletteSession(provider, config, new PaletteOptions { DailyCapUsd = cap }, new SendLog(logPath), tools, new PermissionGrants(), chips)
{
    Prompt = prompt,
    ConfirmTool = (request, _) =>
    {
        Console.WriteLine();
        Console.WriteLine($"ask   {request.Tier}: {request.Summary}  [y]es / [n]o{(request.OfferAlwaysAllow ? " / [a]lways" : "")}");
        if (yes)
        {
            Console.WriteLine("      (auto: yes)");
            return Task.FromResult(PermissionAnswer.AllowOnce);
        }

        return Task.FromResult(Console.ReadLine()?.Trim().ToLowerInvariant() switch
        {
            "y" or "yes" => PermissionAnswer.AllowOnce,
            "a" or "always" => PermissionAnswer.AlwaysAllow,
            _ => PermissionAnswer.Deny,
        });
    },
};

int printed = 0, steps = 0;
string indicator = "";
var clock = Stopwatch.StartNew();
session.Changed += () =>
{
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
Console.WriteLine($"scratchpad now: {scratchpad.Text.ReplaceLineEndings(" | ")}");
Console.WriteLine($"shelf now: {string.Join(", ", shelf.ItemNames)}");
Console.WriteLine($"send log: {logPath}");
return session.Status == PaletteStatus.Done ? 0 : 2;
