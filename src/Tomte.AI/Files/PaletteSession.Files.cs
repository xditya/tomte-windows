using System.Globalization;
using System.Text.Json;
using Tomte.AI.Automation;
using Tomte.AI.Files;

namespace Tomte.AI.Palette;

/// <summary>
/// Files in a palette send: shelf files a chip attached and the ones the model reads with read_shelf_item. A file
/// reaches the provider only with a send the user made: through the shelf chip, or a read while the shelf chip is on
/// (a voice session asks before each read). Files are read when the send needs them, on the pool, and the send is
/// sized before anything leaves.
/// </summary>
public sealed partial class PaletteSession
{
    private readonly List<string> _fileReads = [];
    private readonly List<ContentPart> _readFiles = [];
    private List<List<ContentPart>> _parts = [];
    private int _part;
    private bool _filesInPlay;

    /// <summary>
    /// The model as the provider's list gave it: its context size and whether it reads images. Null uses the provider's
    /// defaults. Read when a send starts and at each file read, so it may be set after the session is made.
    /// </summary>
    public ModelInfo? Model { get; set; }

    public FileLimits FileLimits { get; init; } = new();

    /// <summary>
    /// Asked when a send with files is larger than the model takes in one request or than today's cap allows. The
    /// renderer shows the question's sentences as buttons. The default cancels, so nothing that large is sent without a
    /// choice by the user.
    /// </summary>
    public Func<SizeQuestion, CancellationToken, Task<SizeChoice>> ChooseSize { get; set; } =
        (_, _) => Task.FromResult(SizeChoice.Cancel);

    public TokenEstimate? Estimate { get; private set; }

    internal ModelFacts Facts => ModelFacts.For(_config, _provider.Capabilities, Model);

    // Making a job has its own prompt. A job run has the unattended rule instead of skills and automation, and gets the
    // file paragraph, like any session, once a file is in the send: for a job only through read_shelf_item, which it is
    // offered only when the user approved it for that job.
    private string SystemText => Scheduling?.Prompt
        ?? (Voice ? VoicePrompt : Job is not null ? SystemPrompt + JobPrompt : SystemPrompt + Skills?.Prompt + (Automation is null ? null : AutomationTools.Prompt))
            + (_filesInPlay || _fileReads.Count > 0 ? FileAttachments.Prompt : "");

    /// <summary>
    /// read_shelf_item only once the user attached the shelf; a voice session keeps it and asks before every read, and a
    /// job keeps it when the user approved it for that job, which is the job's tool list by then.
    /// </summary>
    private IReadOnlyList<ToolDefinition> OfferShelf(IReadOnlyList<ToolDefinition> tools) =>
        Voice || Job is not null || Chips.Any(c => c.On && c.Available && c.Kind == ContextKind.ShelfItem) ? tools : [.. tools.Where(t => t.Name != BuiltInTools.ReadShelfItem)];

    /// <summary>
    /// Reads the attached files, refuses an image for a model that does not read images, and sizes the send. Returns
    /// the first request's parts, or null when the send stops here (after <see cref="Finish"/>).
    /// </summary>
    private async Task<List<ContentPart>?> PrepareFilesAsync(List<ContentPart> parts, List<string> attached, IReadOnlyList<ToolDefinition> tools, CancellationToken ct)
    {
        _fileReads.Clear();
        _readFiles.Clear();
        (_parts, _part, _filesInPlay, Estimate) = ([], 0, false, null);
        var facts = Facts;
        if (!facts.Vision && parts.OfType<ImagePart>().Any())
        {
            Finish(PaletteStatus.Failed, $"{_model} does not take images, so nothing was sent. Turn the screenshot off, or pick a model that reads images.");
            return null;
        }

        var (prepared, problem) = await FileAttachments.PrepareAsync(parts, _tools.Shelf, facts, FileLimits, ct);
        if (problem is not null)
        {
            Finish(PaletteStatus.Failed, problem);
            return null;
        }

        _filesInPlay = prepared.OfType<PreparedFilePart>().Any();
        var plan = new SendPlan(prepared, SystemText, tools, facts, _options, SpentTodayUsd, _config.Id);
        Estimate = plan.Estimate;
        Changed?.Invoke();

        // Only file text can be cut or split, so a send without files is never held up by its estimate.
        if (plan.Files.Count == 0 || plan.Estimate.Fits)
        {
            _parts = [plan.Whole()];
        }
        else
        {
            var question = plan.Question!;
            var choice = await ChooseSize(question, ct);
            if (choice == SizeChoice.Truncate && question.Truncate is not null)
                _parts = [plan.Truncated()];
            else if (choice == SizeChoice.Chunk && question.Chunk is not null)
                _parts = plan.Chunks();
            else
            {
                Finish(PaletteStatus.Cancelled, null);
                return null;
            }

            Estimate = plan.Estimate with { Files = [.. plan.Files.Select(f => f.Line)] };
        }

        attached.AddRange(plan.Files.Select(f => "file " + f.Line));
        if (_parts.Count > 1)
            Note(PartLabel(0));
        return _parts[0];
    }

    /// <summary>Starts the next part of a chunked send in <paramref name="messages"/>, with the full time of its own. False when there is none.</summary>
    private bool NextPart(List<ChatMessage> messages, CancellationTokenSource timeout)
    {
        if (++_part >= _parts.Count)
            return false;
        messages.Clear();
        messages.Add(new ChatMessage(ChatRole.User, _parts[_part]));
        Note(PartLabel(_part));
        timeout.CancelAfter(_options.Timeout);
        return true;
    }

    private string PartLabel(int part) => string.Create(CultureInfo.InvariantCulture, $"{(part == 0 ? "" : "\n\n")}Part {part + 1} of {_parts.Count}:\n");

    /// <summary>Images and PDFs a read brought in; they ride in the user turn after the tool results, since a tool result is text.</summary>
    private List<ContentPart> TakeReadFiles()
    {
        var parts = _readFiles.ToList();
        _readFiles.Clear();
        return parts;
    }

    /// <summary>
    /// read_shelf_item through the file readers. Text comes back as the tool result, cut to half of what the model
    /// takes so one read cannot fill the request; an image or a PDF the provider takes as it is follows the results.
    /// </summary>
    // each read is cut on its own, so several large reads in one send can still pass the context; the provider's error then says so.
    private async Task<ToolResultPart> ReadShelfFileAsync(ToolCallPart call, string summary, CancellationToken ct)
    {
        var facts = Facts;
        string name = call.Input.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : "";
        var limits = FileLimits with { MaxChars = (int)Math.Clamp(facts.InputBudget(_options.MaxTokens) / 2 * facts.CharsPerToken, 1000, FileLimits.MaxChars) };
        var (file, problem) = await FileAttachments.PrepareOneAsync(_tools.Shelf, name, facts, limits, ct);
        if (file is null)
            return Step(call, summary, problem!, isError: true);

        _fileReads.Add("read " + file.Line);
        if (!file.Native)
            return Step(call, summary, ((TextPart)file.Parts[0]).Text, isError: false);
        _readFiles.AddRange(file.Parts);
        return Step(call, summary, $"{PreparedFile.Clean(file.Name)} follows after the tool results, as {(file.Kind == "pdf" ? "a PDF document" : "an image")}.", isError: false);
    }
}
