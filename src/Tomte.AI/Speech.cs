using System.Net.Http.Headers;
using System.Runtime.InteropServices;

namespace Tomte.AI;

/// <summary>Speech to text for the rambler. Input is 16 kHz mono float samples.</summary>
public interface ISpeechToText
{
    Task<string> TranscribeAsync(float[] samples, CancellationToken ct = default);
}

/// <summary>
/// The OpenAI-compatible <c>/audio/transcriptions</c> endpoint: OpenAI (whisper-1, gpt-4o-mini-transcribe),
/// Groq, LocalAI and other servers that copy it. Anthropic's Messages API takes no audio, and the Gemini
/// adapter has no audio part yet, so this adapter is the provider path.
/// </summary>
public sealed class OpenAITranscriber(HttpClient http, string baseUrl, string? key, string model = "whisper-1") : ISpeechToText
{
    public async Task<string> TranscribeAsync(float[] samples, CancellationToken ct = default)
    {
        var audio = new ByteArrayContent(Wav(samples, 16000));
        audio.Headers.ContentType = new MediaTypeHeaderValue("audio/wav");
        var request = new HttpRequestMessage(HttpMethod.Post, $"{baseUrl.TrimEnd('/')}/audio/transcriptions")
        {
            Content = new MultipartFormDataContent
            {
                { new StringContent(model), "model" },
                { new StringContent("json"), "response_format" },
                { audio, "file", "rambler.wav" },
            },
        };
        if (!string.IsNullOrEmpty(key))
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);

        // This send is not in the send log yet: the log comes with the palette work, and Rambler.End records
        // each rambler send there once it lands. The user's hold is the visible action until then.
        using var doc = await ProviderHttp.GetJsonAsync(http, request, key, ct);
        return doc.RootElement.TryGetProperty("text", out var text)
            ? text.GetString()?.Trim() ?? ""
            : throw new ProviderException("The transcription answer has no text.");
    }

    /// <summary>16-bit PCM mono WAV: a quarter of the float size, and every endpoint reads it.</summary>
    internal static byte[] Wav(float[] samples, int rate)
    {
        var bytes = new byte[44 + 2 * samples.Length];
        var w = new BinaryWriter(new MemoryStream(bytes));
        w.Write("RIFF"u8);
        w.Write(36 + 2 * samples.Length);
        w.Write("WAVEfmt "u8);
        w.Write(16);
        w.Write((short)1); // PCM
        w.Write((short)1); // mono
        w.Write(rate);
        w.Write(rate * 2);
        w.Write((short)2);
        w.Write((short)16);
        w.Write("data"u8);
        w.Write(2 * samples.Length);
        foreach (var s in samples)
            w.Write((short)Math.Round(Math.Clamp(s, -1f, 1f) * short.MaxValue));
        return bytes;
    }
}

/// <summary>
/// Local whisper.cpp. The DLLs and the model come from tools/whisper/Get-Whisper.ps1 into
/// <see cref="DefaultDirectory"/>; nothing is bundled. The model is loaded per recording and freed
/// after it, so the 150 MB or so it takes exists only while a transcript is being made.
/// </summary>
public sealed partial class WhisperTranscriber(string directory) : ISpeechToText
{
    public const string ModelFile = "ggml-base.en.bin";

    private static string? loadedFrom;

    public static string DefaultDirectory { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tomte", "whisper");

    public static bool IsInstalled(string directory) =>
        File.Exists(Path.Combine(directory, "whisper.dll")) && File.Exists(Path.Combine(directory, ModelFile));

    // whisper_full is CPU-bound for about as long as the recording; run it off the caller's thread.
    public Task<string> TranscribeAsync(float[] samples, CancellationToken ct = default) =>
        Task.Run(() => Transcribe(samples), ct);

    private unsafe string Transcribe(float[] samples)
    {
        Load(directory);
        var contextParams = whisper_context_default_params_by_ref();
        nint context = whisper_init_from_file_with_params(Path.Combine(directory, ModelFile), contextParams);
        whisper_free_context_params(contextParams);
        if (context == 0)
            throw new InvalidOperationException($"whisper.cpp could not load {ModelFile} from {directory}.");
        try
        {
            var fullParams = whisper_full_default_params_by_ref(0); // greedy
            try
            {
                fixed (float* data = samples)
                {
                    if (whisper_full(context, fullParams, data, samples.Length) != 0)
                        throw new InvalidOperationException("whisper.cpp failed to transcribe the recording.");
                }
            }
            finally
            {
                whisper_free_params(fullParams);
            }

            var text = new System.Text.StringBuilder();
            int segments = whisper_full_n_segments(context);
            for (int i = 0; i < segments; i++)
                text.Append(Marshal.PtrToStringUTF8(whisper_full_get_segment_text(context, i)));
            return text.ToString().Trim();
        }
        finally
        {
            whisper_free(context);
        }
    }

    /// <summary>
    /// Points the "whisper" and "ggml" imports at <paramref name="dir"/> and registers ggml's CPU
    /// backend from there; ggml would otherwise only look next to Tomte's exe.
    /// </summary>
    private static void Load(string dir)
    {
        if (loadedFrom is not null)
            return;
        NativeLibrary.SetDllImportResolver(typeof(WhisperTranscriber).Assembly, (name, _, _) =>
            name is "whisper" or "ggml" ? NativeLibrary.Load(Path.Combine(loadedFrom!, name + ".dll")) : 0);
        loadedFrom = dir;
        ggml_backend_load_all_from_path(dir);
    }

    // Hand-written imports: CsWin32 generates only Win32 metadata, and whisper.cpp ships no metadata of its own.
    // whisper_init_from_file_with_params and whisper_full take their parameter structs by value. On x64 Windows a
    // struct larger than 8 bytes goes by value as a pointer to a caller-owned copy, so the *_by_ref copies are passed
    // as that pointer and the struct layout never has to be mirrored here. This holds on win-x64 only, which is
    // the only runtime Tomte ships.
    [LibraryImport("ggml", StringMarshalling = StringMarshalling.Utf8)]
    private static partial void ggml_backend_load_all_from_path(string dir);

    [LibraryImport("whisper")]
    private static partial nint whisper_context_default_params_by_ref();

    [LibraryImport("whisper", StringMarshalling = StringMarshalling.Utf8)]
    private static partial nint whisper_init_from_file_with_params(string path, nint byValueParams);

    [LibraryImport("whisper")]
    private static partial void whisper_free_context_params(nint p);

    [LibraryImport("whisper")]
    private static partial nint whisper_full_default_params_by_ref(int strategy);

    [LibraryImport("whisper")]
    private static unsafe partial int whisper_full(nint context, nint byValueParams, float* samples, int count);

    [LibraryImport("whisper")]
    private static partial void whisper_free_params(nint p);

    [LibraryImport("whisper")]
    private static partial int whisper_full_n_segments(nint context);

    [LibraryImport("whisper")]
    private static partial nint whisper_full_get_segment_text(nint context, int segment);

    [LibraryImport("whisper")]
    private static partial void whisper_free(nint context);
}
