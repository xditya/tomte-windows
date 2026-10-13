using System.Text;
using Tomte.Core;

namespace Tomte.AI.Palette;

public enum PaletteStatus { Idle, Sending, Done, Failed, Cancelled }

public abstract class AnswerSegment;

/// <summary>Streamed model text. Grows in place while the answer arrives.</summary>
public sealed class TextSegment : AnswerSegment
{
    internal readonly StringBuilder Builder = new();

    public string Text => Builder.ToString();
}

/// <summary>One tool call: what was asked, what happened, and an undo for app-local writes.</summary>
public sealed class ToolStepSegment(string tool, string summary, string result, bool isError, Action? undo) : AnswerSegment
{
    public string Tool { get; } = tool;

    public string Summary { get; } = summary;

    public string Result { get; } = result;

    public bool IsError { get; } = isError;

    public Action? Undo { get; } = undo;
}

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

    /// <summary>The visible send: which provider and model are being called, or "Nothing sent".</summary>
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
            var messages = new List<ChatMessage> { new(ChatRole.User, parts) };
            while (true)
            {
                _indicator = $"Sending to {_config.Id} · {_model}";
                sent = true;
                Changed?.Invoke();
                var (assistant, calls, usage) = await StreamTurnAsync(messages, ct);
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
        catch (Exception e) when (e is ProviderException or HttpRequestException or IOException or InvalidOperationException)
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

    private async Task<(List<ContentPart> Assistant, List<ToolCallPart> Calls, TurnDone Usage)> StreamTurnAsync(List<ChatMessage> messages, CancellationToken ct)
    {
        // A copy: the list keeps growing, and a request must not change after it was sent.
        var request = new ChatRequest(_model, [.. messages], SystemPrompt, BuiltInTools.Definitions, _options.MaxTokens);
        var assistant = new List<ContentPart>();
        var calls = new List<ToolCallPart>();
        TurnDone usage = new(null, 0, 0);
        TextSegment? segment = null;
        StringBuilder? text = null;
        await foreach (var e in _provider.StreamAsync(request, ct))
        {
            switch (e)
            {
                case TextDelta delta:
                    if (segment is null)
                    {
                        segment = new TextSegment();
                        _answer.Add(segment);
                        text = new StringBuilder();
                        assistant.Add(new TextPart(""));
                    }

                    segment.Builder.Append(delta.Text);
                    text!.Append(delta.Text);
                    // The assistant turn echoes the text as one part; replace the placeholder with the full text so far.
                    assistant[^1] = new TextPart(text.ToString());
                    break;
                case ToolCallEvent call:
                    assistant.Add(call.Call);
                    calls.Add(call.Call);
                    segment = null;
                    break;
                case OpaqueEvent opaque:
                    assistant.Add(opaque.Part);
                    segment = null;
                    break;
                case TurnDone done:
                    usage = done;
                    break;
            }

            Changed?.Invoke();
        }

        return (assistant, calls, usage);
    }

    private async Task<ToolResultPart> RunToolAsync(ToolCallPart call, CancellationToken ct)
    {
        if (BuiltInTools.Validate(call.Name, call.Input) is { } invalid)
            return Step(call, call.Name, invalid, isError: true);

        var summary = BuiltInTools.Describe(call.Name, call.Input);
        var tier = BuiltInTools.Tiers[call.Name];
        if (_grants.NeedsConfirmation(call.Name, tier))
        {
            var answer = await ConfirmTool(new ToolRequest(call.Name, summary, tier, PermissionGrants.CanAlwaysAllow(tier)), ct);
            if (answer == PermissionAnswer.AlwaysAllow && PermissionGrants.CanAlwaysAllow(tier))
                _grants.AlwaysAllow(call.Name);
            if (answer == PermissionAnswer.Deny)
                return Step(call, summary, "The user did not allow this.", isError: true);
        }

        var (result, undo) = _tools.Run(call);
        return Step(call, summary, result.Content, result.IsError, undo);
    }

    private ToolResultPart Step(ToolCallPart call, string summary, string result, bool isError, Action? undo = null)
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
