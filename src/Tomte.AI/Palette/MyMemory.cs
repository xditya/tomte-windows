using System.Net;
using System.Text;
using System.Text.Json;

namespace Tomte.AI.Palette;

/// <summary>
/// Translation with no key through MyMemory's free API by Translated. It takes up to 500 bytes a request and about
/// 5,000 characters a day from one connection, and its terms ask for no more than a paragraph a request, so the text
/// goes one paragraph at a time, a long paragraph in pieces cut at a sentence or a space. MyMemory keeps what it is
/// sent, which the card and Settings say.
/// </summary>
public static class MyMemory
{
    public const string Name = "MyMemory";

    /// <summary>The most one request takes, in UTF-8 bytes, and the free characters a day for one connection.</summary>
    public const int MaxBytes = 500, DailyChars = 5000;

    /// <summary>
    /// What came back: the translation, or why there is none. <see cref="SameLanguage"/>: the text is already in the
    /// language asked for, which MyMemory answers with an error.
    /// </summary>
    public sealed record Answer(string? Text, string? Error, bool SameLanguage = false);

    public const string QuotaUsed = "MyMemory's free limit of 5,000 characters a day for this connection is used up.";

    public static Uri Url(string piece, string language) =>
        new($"https://api.mymemory.translated.net/get?q={Uri.EscapeDataString(piece)}&langpair=autodetect%7C{Uri.EscapeDataString(language)}");

    /// <summary>
    /// The text in order as pieces to send and the blank lines and spaces between them, which stay as they are. Each
    /// piece is a paragraph, or part of one, of at most <see cref="MaxBytes"/>.
    /// </summary>
    public static IEnumerable<(string Text, bool Send)> Pieces(string text)
    {
        int i = 0;
        while (i < text.Length)
        {
            int gap = i;
            while (gap < text.Length && char.IsWhiteSpace(text[gap]))
                gap++;
            if (gap > i)
            {
                yield return (text[i..gap], false);
                i = gap;
                continue;
            }

            // A paragraph runs to the next blank line.
            int end = text.IndexOf("\n\n", i, StringComparison.Ordinal) is var blank and >= 0 ? blank : text.Length;
            while (end > i && char.IsWhiteSpace(text[end - 1]))
                end--;
            int cut = Fit(text, i, end);
            yield return (text[i..cut], true);
            i = cut;
        }
    }

    // Where a piece from start ends: the paragraph's end when it fits, else after the last sentence end or space that
    // fits, else at the byte limit itself in a script without spaces.
    private static int Fit(string text, int start, int end)
    {
        int at = start, bytes = 0;
        foreach (var rune in text.AsSpan(start, end - start).EnumerateRunes())
        {
            if (bytes + rune.Utf8SequenceLength > MaxBytes)
                break;
            bytes += rune.Utf8SequenceLength;
            at += rune.Utf16SequenceLength;
        }

        if (at == end)
            return end;
        int space = -1;
        for (int j = at; j > start; j--)
        {
            if (!char.IsWhiteSpace(text[j]))
                continue;
            if (".!?".Contains(text[j - 1]))
                return j;
            if (space < 0)
                space = j;
        }

        return space > start ? space : at;
    }

    /// <summary>One answer from the API, which says what went wrong in its body, mostly with HTTP 200.</summary>
    public static Answer Parse(string body, HttpStatusCode status = HttpStatusCode.OK)
    {
        try
        {
            using var json = JsonDocument.Parse(body);
            var root = json.RootElement;
            string code = root.TryGetProperty("responseStatus", out var s) ? s.ValueKind == JsonValueKind.Number ? s.GetRawText() : s.GetString() ?? "" : "";
            string details = root.TryGetProperty("responseDetails", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString()! : "";
            string? translated = root.TryGetProperty("responseData", out var data) && data.ValueKind == JsonValueKind.Object
                && data.TryGetProperty("translatedText", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
            if (root.TryGetProperty("quotaFinished", out var q) && q.ValueKind == JsonValueKind.True || code == "429" || status == HttpStatusCode.TooManyRequests
                || details.Contains("ALL AVAILABLE FREE TRANSLATIONS", StringComparison.OrdinalIgnoreCase))
                return new(null, QuotaUsed);
            if (details.Contains("TWO DISTINCT LANGUAGES", StringComparison.OrdinalIgnoreCase))
                return new(null, null, SameLanguage: true);
            if (code != "200" || string.IsNullOrWhiteSpace(translated))
                return new(null, details.Length > 0 ? $"MyMemory said: {Sentence(details)}" : "MyMemory brought nothing back.");
            // It can answer with HTML character references, such as &#39; for an apostrophe.
            return new(WebUtility.HtmlDecode(translated), null);
        }
        catch (JsonException)
        {
            return new(null, status == HttpStatusCode.OK ? "MyMemory brought back something that is not a translation." : $"MyMemory answered with an error (HTTP {(int)status}).");
        }
    }

    /// <summary>
    /// The text, translated into <paramref name="language"/> a piece at a time, one request after another; the first
    /// error ends it. A piece already in that language stays as it is; when every piece is, the answer says so.
    /// </summary>
    public static async Task<Answer> TranslateAsync(HttpClient http, string text, string language, CancellationToken ct)
    {
        var result = new StringBuilder();
        bool changed = false;
        foreach (var (piece, send) in Pieces(text))
        {
            if (!send)
            {
                result.Append(piece);
                continue;
            }

            Answer answer;
            try
            {
                using var response = await http.GetAsync(Url(piece, language), ct);
                answer = Parse(await response.Content.ReadAsStringAsync(ct), response.StatusCode);
            }
            catch (HttpRequestException)
            {
                return new(null, "MyMemory could not be reached. Check the connection.");
            }
            catch (TaskCanceledException) when (!ct.IsCancellationRequested)
            {
                return new(null, "MyMemory did not answer in time.");
            }

            if (answer.Error is not null)
                return answer;
            changed |= !answer.SameLanguage;
            result.Append(answer.SameLanguage ? piece : answer.Text);
        }

        return changed ? new(result.ToString(), null) : new(null, null, SameLanguage: true);
    }

    // "QUERY LENGTH LIMIT EXCEEDED. MAX ALLOWED QUERY : 500 CHARS" as a sentence.
    private static string Sentence(string shouted)
    {
        string lower = shouted.Trim().ToLowerInvariant();
        return lower.Length == 0 ? lower : char.ToUpperInvariant(lower[0]) + lower[1..];
    }
}
