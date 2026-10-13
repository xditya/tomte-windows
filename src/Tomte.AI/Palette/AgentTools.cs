using System.Text.Json;
using Tomte.Core;
using Tomte.Core.Device;

namespace Tomte.AI.Palette;

/// <summary>
/// The five built-in tools: definitions the model sees, argument validation, permission tiers and
/// execution. Clipboard and URL opening are injected so tests and the harness can fake them.
/// </summary>
public sealed class BuiltInTools(IShelf shelf, IScratchpad scratchpad, Action<string> copyToClipboard, Action<string> openUrl)
{
    public const string ReadShelfItem = "read_shelf_item";
    public const string WriteScratchpad = "write_scratchpad";
    public const string CreateShelfFile = "create_shelf_file";
    public const string OpenUrl = "open_url";
    public const string CopyToClipboard = "copy_to_clipboard";

    // Name, description, required string arguments, optional string arguments.
    private static readonly (string Name, string Description, string[] Required, string[] Optional)[] Table =
    [
        (ReadShelfItem, "Read the text of an item on the user's shelf.", ["name"], []),
        (WriteScratchpad, "Add text to the user's scratchpad note. Appends unless mode is \"replace\".", ["text"], ["mode"]),
        (CreateShelfFile, "Create a text file on the user's shelf.", ["name", "content"], []),
        (OpenUrl, "Open an http or https URL in the user's default browser.", ["url"], []),
        (CopyToClipboard, "Put text on the user's clipboard.", ["text"], []),
    ];

    public static readonly IReadOnlyList<ToolDefinition> Definitions = [.. Table.Select(t => new ToolDefinition(t.Name, t.Description, Schema(t.Required, t.Optional)))];

    // The clipboard belongs to the user, not the app: replacing it is an external effect.
    public static readonly IReadOnlyDictionary<string, ActionTier> Tiers = new Dictionary<string, ActionTier>
    {
        [ReadShelfItem] = ActionTier.Read,
        [WriteScratchpad] = ActionTier.AppLocalWrite,
        [CreateShelfFile] = ActionTier.AppLocalWrite,
        [OpenUrl] = ActionTier.ExternalEffect,
        [CopyToClipboard] = ActionTier.ExternalEffect,
    };

    /// <summary>Null when the call can run; otherwise a message for the model.</summary>
    public static string? Validate(string name, JsonElement input)
    {
        var entry = Table.FirstOrDefault(t => t.Name == name);
        if (entry.Name is null)
            return $"There is no tool named {name}.";
        if (input.ValueKind != JsonValueKind.Object)
            return "Arguments must be a JSON object.";
        foreach (var required in entry.Required)
        {
            if (!input.TryGetProperty(required, out var value) || value.ValueKind != JsonValueKind.String || value.GetString()!.Length == 0)
                return $"{name} needs a non-empty string argument \"{required}\".";
        }
        foreach (var optional in entry.Optional)
        {
            if (input.TryGetProperty(optional, out var value) && value.ValueKind != JsonValueKind.String)
                return $"{name}: \"{optional}\" must be a string.";
        }
        return name switch
        {
            OpenUrl when !IsWebUrl(input.GetProperty("url").GetString()!) => "open_url only opens http and https URLs.",
            CreateShelfFile when !IsFileName(input.GetProperty("name").GetString()!) => "name must be a plain file name with no path separators.",
            WriteScratchpad when input.TryGetProperty("mode", out var mode) && mode.GetString() is not ("append" or "replace") => "mode must be \"append\" or \"replace\".",
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
        _ => name,
    };

    /// <summary>Runs a validated call. The undo, when there is one, puts the app's own data back.</summary>
    public (ToolResultPart Result, Action? Undo) Run(ToolCallPart call)
    {
        if (Validate(call.Name, call.Input) is { } error)
            return (new ToolResultPart(call.Id, call.Name, error, IsError: true), null);
        try
        {
            return call.Name switch
            {
                ReadShelfItem => ReadItem(call),
                WriteScratchpad => Write(call),
                CreateShelfFile => Create(call),
                OpenUrl => Open(call),
                _ => Copy(call),
            };
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            return (new ToolResultPart(call.Id, call.Name, $"{call.Name} failed: {e.Message}", IsError: true), null);
        }
    }

    private (ToolResultPart, Action?) ReadItem(ToolCallPart call)
    {
        var name = Arg(call.Input, "name");
        var text = shelf.ReadText(name);
        if (text is not null)
            return (new ToolResultPart(call.Id, call.Name, text), null);
        var items = shelf.ItemNames.Count == 0 ? "The shelf is empty." : $"Items on the shelf: {string.Join(", ", shelf.ItemNames)}.";
        return (new ToolResultPart(call.Id, call.Name, $"No readable shelf item named \"{name}\". {items}", IsError: true), null);
    }

    private (ToolResultPart, Action?) Write(ToolCallPart call)
    {
        var before = scratchpad.Text;
        var text = Arg(call.Input, "text");
        scratchpad.Text = Arg(call.Input, "mode") == "replace" || before.Length == 0 ? text : before.TrimEnd('\n', '\r') + "\n" + text;
        return (new ToolResultPart(call.Id, call.Name, "Written to the scratchpad."), () => scratchpad.Text = before);
    }

    private (ToolResultPart, Action?) Create(ToolCallPart call)
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
            }
        );
    }

    private (ToolResultPart, Action?) Open(ToolCallPart call)
    {
        openUrl(Arg(call.Input, "url"));
        return (new ToolResultPart(call.Id, call.Name, "Opened in the browser."), null);
    }

    private (ToolResultPart, Action?) Copy(ToolCallPart call)
    {
        copyToClipboard(Arg(call.Input, "text"));
        return (new ToolResultPart(call.Id, call.Name, "Copied to the clipboard."), null);
    }

    private static string Arg(JsonElement input, string name) =>
        input.ValueKind == JsonValueKind.Object && input.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString()! : "";

    private static string Preview(string text) =>
        (text.Length > 40 ? text[..40] + "..." : text).ReplaceLineEndings(" ");

    private static bool IsWebUrl(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https";

    private static bool IsFileName(string name) =>
        name.Trim() == name && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && name is not ("." or "..");

    private static JsonElement Schema(string[] required, string[] optional)
    {
        var properties = string.Join(",", required.Concat(optional).Select(p => $"\"{p}\":{{\"type\":\"string\"}}"));
        var requiredList = string.Join(",", required.Select(p => $"\"{p}\""));
        return JsonDocument.Parse($"{{\"type\":\"object\",\"properties\":{{{properties}}},\"required\":[{requiredList}]}}").RootElement;
    }
}
