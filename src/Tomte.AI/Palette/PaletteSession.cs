using System.Text;
using System.Text.Json;
using Tomte.AI.Mcp;
using Tomte.Core;
using Tomte.Core.Device;

namespace Tomte.AI.Palette;

public enum PaletteStatus { Idle, Sending, Done, Failed, Cancelled }

public abstract class AnswerSegment;

/// <summary>Streamed model text. Grows in place while the answer arrives.</summary>
public sealed class TextSegment : AnswerSegment
{
    internal readonly StringBuilder Builder = new();

    public string Text => Builder.ToString();
}

/// <summary>One tool call: what was asked, what happened, and an undo for app-local writes and device settings.</summary>
public sealed class ToolStepSegment(string tool, string summary, string result, bool isError, Func<ActionResult>? undo) : AnswerSegment
{
    public string Tool { get; } = tool;

    public string Summary { get; } = summary;

    public string Result { get; } = result;

    public bool IsError { get; } = isError;

    /// <summary>One-shot: the second call reports there is nothing left to undo.</summary>
    public Func<ActionResult>? Undo { get; } = undo;
}

/// <summary>A device action that ran: the one line the island shows in Compact for 4 s, and its undo where the action offers one.</summary>
public sealed record DeviceEvent(string Line, Func<ActionResult>? Undo);

/// <summary>The palette's knobs. Lives here until the settings store merges; then Settings owns these values.</summary>
public sealed record PaletteOptions
{
    /// <summary>Null means no cap.</summary>
    public decimal? DailyCapUsd { get; init; } = 2m;

    public IReadOnlyDictionary<string, ModelPrice> Prices { get; init; } = new Dictionary<string, ModelPrice>();

    /// <summary>Off by default: the send log then records only names, times and token counts.</summary>
    public bool LogContent { get; init; }

    public int MaxToolSteps { get; init; } = 8;

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(120);

    public int MaxTokens { get; init; } = 4096;
}

/// <summary>What the renderer binds. Every change raises <see cref="Changed"/> on the sending thread's context.</summary>
public interface IPaletteSession
{
    string Prompt { get; set; }

    IReadOnlyList<ContextChip> Chips { get; }

    IReadOnlyList<AnswerSegment> Answer { get; }

    string AnswerText { get; }

    PaletteStatus Status { get; }

    /// <summary>Which provider and model are being called while a request is in flight, or "Nothing sent": the user always sees where data goes.</summary>
    string ProviderIndicator { get; }

    string? Error { get; }

    int ToolSteps { get; }

    decimal SpentTodayUsd { get; }

    decimal? DailyCapUsd { get; }

    event Action? Changed;

    Task SendAsync();

    void Cancel();
}

/// <summary>
/// One open palette: prompt, chips, the streamed answer and the agent loop. Created when the
/// palette opens and dropped when it closes; it owns no timers or threads beyond the in-flight
/// request (whose timeout is the only timer, and it dies with the request).
/// </summary>
public sealed class PaletteSession : IPaletteSession
{
    private const string SystemPrompt =
        "You are Tomte, a quiet helper on the user's Windows PC. Answer briefly and plainly. " +
        "Context the user attached arrives in tagged blocks before the question; answer from it. " +
        "Use a tool only when the user asks for its effect. Never invent shelf items.";

    private readonly IChatProvider _provider;
    private readonly ProviderConfig _config;
    private readonly PaletteOptions _options;
    private readonly SendLog _log;
    private readonly BuiltInTools _tools;
    private readonly PermissionGrants _grants;
    private readonly List<AnswerSegment> _answer = [];
    private readonly string _model;
    private CancellationTokenSource? _userCancel;
    private string _indicator = "Nothing sent";

    public PaletteSession(IChatProvider provider, ProviderConfig config, PaletteOptions options, SendLog log, BuiltInTools tools, PermissionGrants grants, IReadOnlyList<ContextChip> chips)
    {
        _provider = provider;
        _config = config;
        _options = options;
        _log = log;
        _tools = tools;
        _grants = grants;
        Chips = chips;
        _model = config.DefaultModel ?? throw new ArgumentException("The provider has no default model.", nameof(config));
        SpentTodayUsd = log.SpentToday(config.Id, Now());
    }

    /// <summary>
    /// Asked before an external-effect or destructive tool runs. The renderer shows the prompt; the
    /// default denies, so nothing with an effect outside the app runs without a user action.
    /// </summary>
    public Func<ToolRequest, CancellationToken, Task<PermissionAnswer>> ConfirmTool { get; set; } =
        (_, _) => Task.FromResult(PermissionAnswer.Deny);

    /// <summary>The user's MCP servers. Their tools are listed (and stdio servers started) on the first send; the owner disposes it when the palette closes.</summary>
    public McpTools? Mcp { get; init; }

    /// <summary>Clock for the daily cap; tests set it.</summary>
    public Func<DateTimeOffset> Now { get; set; } = () => DateTimeOffset.Now;

    public string Prompt { get; set; } = "";

    public IReadOnlyList<ContextChip> Chips { get; }

    public IReadOnlyList<AnswerSegment> Answer => _answer;

    public string AnswerText => string.Concat(_answer.OfType<TextSegment>().Select(s => s.Text));

    public PaletteStatus Status { get; private set; }

    public string ProviderIndicator => _indicator;

    public string? Error { get; private set; }

    public int ToolSteps { get; private set; }

    public decimal SpentTodayUsd { get; private set; }

    public decimal? DailyCapUsd => _options.DailyCapUsd;

    public event Action? Changed;

    /// <summary>Raised after a device action ran (not for reads), on the sending thread's context. The shell shows it as a Compact event.</summary>
    public event Action<DeviceEvent>? DeviceActionExecuted;

    public async Task SendAsync()
    {
        if (Status == PaletteStatus.Sending || string.IsNullOrWhiteSpace(Prompt))
            return;

        _answer.Clear();
        ToolSteps = 0;
        Error = null;
        if (_options.DailyCapUsd is { } cap && SpentTodayUsd >= cap)
        {
            Finish(PaletteStatus.Failed, $"Daily cap of ${cap:0.00} reached for {_config.Id}. Raise it in settings or wait until tomorrow.");
            return;
        }

        Status = PaletteStatus.Sending;
        _userCancel = new CancellationTokenSource();
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(_userCancel.Token);
        timeout.CancelAfter(_options.Timeout);
        var ct = timeout.Token;
        bool sent = false;
        int inputTokens = 0, outputTokens = 0;
        List<string> attached = [];
        Changed?.Invoke();
        try
        {
            List<ContentPart> parts;
            (parts, attached) = await ContextAssembly.BuildAsync(Chips, Prompt, ct);
            IReadOnlyList<ToolDefinition> tools = Mcp is null ? BuiltInTools.Definitions : [.. BuiltInTools.Definitions, .. await Mcp.ListAsync(ct)];
            var messages = new List<ChatMessage> { new(ChatRole.User, parts) };
            while (true)
            {
                _indicator = $"Sending to {_config.Id} · {_model}";
                sent = true;
                Changed?.Invoke();
                var (assistant, calls, usage) = await StreamTurnAsync(messages, tools, ct);
                inputTokens += usage.InputTokens;
                outputTokens += usage.OutputTokens;
                messages.Add(new ChatMessage(ChatRole.Assistant, assistant));
                if (calls.Count == 0)
                    break;

                var results = new List<ContentPart>();
                bool capped = false;
                foreach (var call in calls)
                {
                    if (ToolSteps >= _options.MaxToolSteps)
                    {
                        Note($"Stopped after {_options.MaxToolSteps} tool steps.");
                        capped = true;
                        break;
                    }

                    ToolSteps++;
                    results.Add(await RunToolAsync(call, ct));
                }

                if (capped)
                    break;
                messages.Add(new ChatMessage(ChatRole.User, results));
            }

            Finish(PaletteStatus.Done, null);
        }
        catch (OperationCanceledException)
        {
            Finish(_userCancel.IsCancellationRequested ? PaletteStatus.Cancelled : PaletteStatus.Failed,
                _userCancel.IsCancellationRequested ? null : $"Timed out after {_options.Timeout.TotalSeconds:0} s.");
        }
        // A failed chip read (capture, UIA) or provider call ends the send; it must never unwind the island's thread.
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Finish(PaletteStatus.Failed, e.Message);
        }
        finally
        {
            _userCancel.Dispose();
            _userCancel = null;
            if (sent)
            {
                var cost = SendLog.Estimate(_options.Prices, _model, inputTokens, outputTokens);
                _log.Append(new SendLogEntry(Now(), _config.Id, _model, inputTokens, outputTokens, cost, attached,
                    _options.LogContent ? $"{Prompt}\n---\n{AnswerText}" : null));
                SpentTodayUsd += cost;
                _indicator = $"Sent to {_config.Id} · {_model}";
                Changed?.Invoke();
            }
        }
    }

    public void Cancel() => _userCancel?.Cancel();

    private async Task<(List<ContentPart> Assistant, List<ToolCallPart> Calls, TurnDone Usage)> StreamTurnAsync(List<ChatMessage> messages, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
    {
        // A copy: the list keeps growing, and a request must not change after it was sent.
        var request = new ChatRequest(_model, [.. messages], SystemPrompt, tools, _options.MaxTokens);
        var assistant = new List<ContentPart>();
        var calls = new List<ToolCallPart>();
        TurnDone usage = new(null, 0, 0);
        TextSegment? segment = null;
        int placeholder = -1;

        // Deltas accumulate in the segment; the assistant turn gets the text as one part when the segment closes.
        void CloseText()
        {
            if (segment is not null)
                assistant[placeholder] = new TextPart(segment.Text);
            segment = null;
        }

        await foreach (var e in _provider.StreamAsync(request, ct))
        {
            switch (e)
            {
                case TextDelta delta:
                    if (segment is null)
                    {
                        segment = new TextSegment();
                        _answer.Add(segment);
                        placeholder = assistant.Count;
                        assistant.Add(new TextPart(""));
                    }

                    segment.Builder.Append(delta.Text);
                    break;
                case ToolCallEvent call:
                    CloseText();
                    assistant.Add(call.Call);
                    calls.Add(call.Call);
                    break;
                case OpaqueEvent opaque:
                    CloseText();
                    assistant.Add(opaque.Part);
                    break;
                case TurnDone done:
                    usage = done;
                    break;
            }

            Changed?.Invoke();
        }

        CloseText();
        return (assistant, calls, usage);
    }

    private async Task<ToolResultPart> RunToolAsync(ToolCallPart call, CancellationToken ct)
    {
        bool mcp = Mcp?.Owns(call.Name) == true;
        // MCP servers check their own arguments; the only shape rule here is the one every tool shares.
        string? invalid = mcp
            ? call.Input.ValueKind == JsonValueKind.Object ? null : "Arguments must be a JSON object."
            : BuiltInTools.Validate(call.Name, call.Input);
        if (invalid is not null)
            return Step(call, call.Name, invalid, isError: true);

        var summary = mcp ? Mcp!.Describe(call.Name, call.Input) : BuiltInTools.Describe(call.Name, call.Input);
        var tier = mcp ? Mcp!.TierOf(call.Name) : BuiltInTools.Tiers[call.Name];
        // An MCP tool is allowed as what it was when the user said so: its key changes when the tool or its server does.
        string grant = mcp ? Mcp!.KeyOf(call.Name) : call.Name;
        if (_grants.NeedsConfirmation(grant, tier))
        {
            var answer = await ConfirmTool(new ToolRequest(call.Name, summary, tier, PermissionGrants.CanAlwaysAllow(tier)), ct);
            // The grant is a settings.json write that can wait on the settings process; the pool takes it, not the island's thread.
            if (answer == PermissionAnswer.AlwaysAllow && PermissionGrants.CanAlwaysAllow(tier))
                await Task.Run(() => _grants.AlwaysAllow(grant));
            if (answer == PermissionAnswer.Deny)
                return Step(call, summary, "The user did not allow this.", isError: true);
        }

        if (mcp)
        {
            var mcpResult = await Mcp!.CallAsync(call, ct);
            return Step(call, summary, mcpResult.Content, mcpResult.IsError);
        }

        // Device actions block on WinRT, COM and WMI; the pool runs them so the island's thread keeps animating.
        // Every other tool stays on the calling thread: the clipboard's owner window belongs to the thread that first used it.
        var (result, undo) = BuiltInTools.IsDeviceTool(call.Name) ? await Task.Run(() => _tools.Run(call), ct) : _tools.Run(call);
        if (!result.IsError && tier != ActionTier.Read && BuiltInTools.IsDeviceTool(call.Name))
            DeviceActionExecuted?.Invoke(new DeviceEvent(result.Content, undo));
        return Step(call, summary, result.Content, result.IsError, undo);
    }

    private ToolResultPart Step(ToolCallPart call, string summary, string result, bool isError, Func<ActionResult>? undo = null)
    {
        _answer.Add(new ToolStepSegment(call.Name, summary, result, isError, undo));
        Changed?.Invoke();
        return new ToolResultPart(call.Id, call.Name, result, isError);
    }

    private void Note(string text)
    {
        var segment = new TextSegment();
        segment.Builder.Append(text);
        _answer.Add(segment);
    }

    private void Finish(PaletteStatus status, string? error)
    {
        Status = status;
        Error = error;
        Changed?.Invoke();
    }
}
