using Tomte.Core;

namespace Tomte.AI.Files;

/// <summary>
/// What sizing a send needs to know about the model: its context, whether it reads images, whether its provider takes
/// PDFs, and each provider's documented rules for counting and for the largest image and PDF it takes. Token counts
/// here are estimates made on this PC; nothing is sent to count them.
/// </summary>
public sealed record ModelFacts(ProviderKind Kind, string Model, int ContextTokens, bool Vision, bool NativePdf)
{
    /// <summary>
    /// The model list's context size and vision flag when it gives them. Otherwise: Claude models take at least
    /// 200,000 tokens and every current one reads images; Gemini models take about a million and read images; an
    /// OpenAI-compatible endpoint (often a small local model) is taken to have 8,192 tokens and no vision, since its
    /// API does not say.
    /// </summary>
    public static ModelFacts For(ProviderConfig config, ProviderCapabilities capabilities, ModelInfo? listed) => new(
        config.Kind,
        config.DefaultModel ?? "",
        listed?.ContextTokens ?? config.Kind switch { ProviderKind.Anthropic => 200_000, ProviderKind.Gemini => 1_048_576, _ => 8_192 },
        capabilities.HasFlag(ProviderCapabilities.Vision) && (listed?.Vision ?? config.Kind != ProviderKind.OpenAICompatible),
        capabilities.HasFlag(ProviderCapabilities.Pdf));

    /// <summary>
    /// Characters per token for Latin text. Google documents about 4 for Gemini and OpenAI about 4 for English.
    /// Anthropic publishes no ratio, and its newer tokenizers count up to a third more tokens than older ones, so 3.
    /// Every other character (Chinese, Japanese, emoji, accented letters) counts as a token of its own.
    /// </summary>
    public double CharsPerToken => Kind == ProviderKind.Anthropic ? 3.0 : 4.0;

    public int TextTokens(string text)
    {
        int ascii = 0;
        foreach (char c in text)
        {
            if (c < 0x80)
                ascii++;
        }

        return (int)Math.Ceiling(ascii / CharsPerToken) + (text.Length - ascii);
    }

    /// <summary>
    /// The largest image worth sending. Anthropic: 2,576 pixels on the long edge and about 4,784 tokens at 750 pixels a
    /// token (older models scale down to 1,568 on their side). Gemini: tiles of 768 pixels, with pages scaled to 3,072
    /// at most. OpenAI: fits a 2,048 square, then the short side to 768.
    /// </summary>
    public ImageBox ImageBox => Kind switch
    {
        ProviderKind.Anthropic => new(2576, 2576, 4784L * 750),
        ProviderKind.Gemini => new(3072, 3072, long.MaxValue),
        _ => new(2048, 768, long.MaxValue),
    };

    /// <summary>Anthropic: width times height over 750. Gemini: 258 for a small image, else 258 per tile. OpenAI: 85 plus 170 per 512-pixel tile.</summary>
    public int ImageTokens(int width, int height)
    {
        switch (Kind)
        {
            case ProviderKind.Anthropic:
                return (int)Math.Min(4784, Math.Ceiling(width * (double)height / 750));
            case ProviderKind.Gemini when width <= 384 && height <= 384:
                return 258;
            case ProviderKind.Gemini:
                // Google's rule: a crop unit of the short side over 1.5, and a tile per unit in each direction.
                double unit = Math.Max(1, Math.Floor(Math.Min(width, height) / 1.5));
                return (int)(Math.Ceiling(width / unit) * Math.Ceiling(height / unit)) * 258;
            default:
                return 85 + 170 * (int)(Math.Ceiling(width / 512.0) * Math.Ceiling(height / 512.0));
        }
    }

    /// <summary>
    /// The largest PDF sent as a PDF: 4 MB, well inside Anthropic's 32 MB and Gemini's 20 MB request. The request body is
    /// built in memory, where the file, its base64 and the JSON around it come to about nine times its size, so 4 MB
    /// keeps a send within the app's memory budget. A larger PDF goes as its extracted text.
    /// </summary>
    public long MaxPdfBytes => 4L << 20;

    /// <summary>Anthropic takes 600 pages (100 on 200,000-token models), Gemini 1,000.</summary>
    public int MaxPdfPages => Kind == ProviderKind.Anthropic ? (ContextTokens > 200_000 ? 600 : 100) : 1000;

    /// <summary>
    /// A PDF sent as a PDF. Anthropic sends each page's text and an image of the page, about 1,600 tokens; Gemini
    /// counts 258 tokens a page and does not charge for the text it reads from the file.
    /// </summary>
    public int PdfTokens(int pages, int textTokens) => Kind == ProviderKind.Anthropic ? textTokens + pages * 1600 : pages * 258;

    /// <summary>Room left for the answer: the reply cap, but never more than a quarter of a small model's context.</summary>
    public int InputBudget(int maxTokens) => ContextTokens - Math.Min(maxTokens, ContextTokens / 4);
}

/// <summary>
/// The estimate the palette shows before a send that carries files: tokens, cost and what the model and today's cap
/// allow. <see cref="Line"/> is the sentence to show; <see cref="Files"/> has one line per file (name, kind, whether it
/// goes as a PDF or image or as text read on this PC, size).
/// </summary>
public sealed record TokenEstimate(int Tokens, int Limit, decimal CostUsd, IReadOnlyList<string> Files, string Line)
{
    public bool Fits => Tokens <= Limit;
}

public enum SizeChoice { Truncate, Chunk, Cancel }

/// <summary>
/// What the palette asks when a send is larger than the model takes or today's cap allows. <see cref="Truncate"/> and
/// <see cref="Chunk"/> are the button texts, null when that choice cannot work (nothing to cut, or more parts or money
/// than allowed); <see cref="Cancel"/> is always there.
/// </summary>
public sealed record SizeQuestion(string Reason, string? Truncate, string? Chunk, string Cancel);
