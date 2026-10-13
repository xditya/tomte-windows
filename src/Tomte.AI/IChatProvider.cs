using System.Text.Json;

namespace Tomte.AI;

/// <summary>What a provider's API accepts. Context size is per model, see <see cref="ModelInfo"/>.</summary>
[Flags]
public enum ProviderCapabilities
{
    None = 0,
    Streaming = 1,
    Vision = 2,
    ToolUse = 4,

    /// <summary>PDFs go to the API as-is. Without it the caller extracts the text locally first.</summary>
    Pdf = 8,
}

/// <summary>One model from the provider's list. <see cref="ContextTokens"/> is null when the API does not say.</summary>
public sealed record ModelInfo(string Id, int? ContextTokens = null);

/// <summary>
/// One chat provider. Instances hold no threads, timers or open connections: nothing happens
/// until a method is called, and every call is a single HTTP request.
/// </summary>
public interface IChatProvider
{
    ProviderCapabilities Capabilities { get; }

    /// <summary>Models from the API, or an empty list when the endpoint has none (the user types the id).</summary>
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken ct = default);

    /// <summary>Streams one model turn. Tool calls arrive whole, after their arguments finish streaming.</summary>
    IAsyncEnumerable<ChatEvent> StreamAsync(ChatRequest request, CancellationToken ct = default);
}

public enum ChatRole { User, Assistant }

public sealed record ChatMessage(ChatRole Role, IReadOnlyList<ContentPart> Parts)
{
    public static ChatMessage User(string text) => new(ChatRole.User, [new TextPart(text)]);
}

public abstract record ContentPart;
public sealed record TextPart(string Text) : ContentPart;
public sealed record ImagePart(string MediaType, byte[] Data) : ContentPart;
public sealed record PdfPart(byte[] Data) : ContentPart;

/// <summary>A tool call the model made. Send it back unchanged in the assistant turn.</summary>
public sealed record ToolCallPart(string Id, string Name, JsonElement Input, string? Signature = null) : ContentPart;

public sealed record ToolResultPart(string CallId, string Name, string Content, bool IsError = false) : ContentPart;

/// <summary>
/// A provider block the caller must echo back verbatim in the assistant turn (Anthropic thinking
/// blocks before a tool call). Other adapters drop it.
/// </summary>
public sealed record OpaquePart(JsonElement Raw) : ContentPart;

public sealed record ToolDefinition(string Name, string Description, JsonElement InputSchema);

public sealed record ChatRequest(
    string Model,
    IReadOnlyList<ChatMessage> Messages,
    string? System = null,
    IReadOnlyList<ToolDefinition>? Tools = null,
    int MaxTokens = 4096);

public abstract record ChatEvent;
public sealed record TextDelta(string Text) : ChatEvent;
public sealed record ToolCallEvent(ToolCallPart Call) : ChatEvent;
public sealed record OpaqueEvent(OpaquePart Part) : ChatEvent;

/// <summary>Last event of a turn. <see cref="StopReason"/> is the provider's own string.</summary>
public sealed record TurnDone(string? StopReason, int InputTokens, int OutputTokens) : ChatEvent;

/// <summary>The provider answered with an error. The message never contains the key.</summary>
public sealed class ProviderException(string message, int? status = null) : Exception(message)
{
    public int? Status { get; } = status;
}
