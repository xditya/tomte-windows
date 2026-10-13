using System.Text.Json.Nodes;
using Tomte.AI;
using Tomte.Core.Rambler;
using Xunit;

namespace Tomte.Tests;

public class RamblerTests
{
    private const ushort V = 'V', LeftWin = 0x5B, LeftAlt = 0xA4, Shift = 0x10;

    private static (HoldDetector Detector, List<string> Log) Detector()
    {
        var log = new List<string>();
        var detector = new HoldDetector(on => log.Add(on ? "arm" : "disarm"));
        detector.Started += () => log.Add("start");
        detector.Ended += keep => log.Add(keep ? "keep" : "cancel");
        return (detector, log);
    }

    [Fact]
    public void HoldArmsRawInputOnPressAndEndsOnTheFirstChordKeyUp()
    {
        var (detector, log) = Detector();
        detector.Press(1000);
        detector.KeyUp(Shift, 1100); // not part of the chord
        detector.Press(1200); // auto-repeat while held
        Assert.True(detector.Held);
        detector.KeyUp(LeftAlt, 2000);
        detector.KeyUp(V, 2010); // the rest of the release, already disarmed
        detector.KeyUp(LeftWin, 2020);

        Assert.Equal(["arm", "start", "disarm", "keep"], log);
        Assert.False(detector.Held);
    }

    [Fact]
    public void TapIsCancelledAndKeyUpsWithoutAPressDoNothing()
    {
        var (detector, log) = Detector();
        detector.KeyUp(V, 0);
        detector.Press(1000);
        detector.KeyUp(V, 1299);
        detector.Press(2000);
        detector.KeyUp(LeftWin, 2300);

        Assert.Equal(["arm", "start", "disarm", "cancel", "arm", "start", "disarm", "keep"], log);
    }

    [Fact]
    public void RmsMapsToSevenMirroredBarsBetweenRestAndFull()
    {
        Assert.Equal(0.5f, Waveform.Rms([0.5f, -0.5f, 0.5f, -0.5f]), 5);
        Assert.Equal(0, Waveform.Rms([]));
        Assert.Equal(0, Waveform.Level(0));
        Assert.Equal(0, Waveform.Level(0.001f)); // -60 dBFS, a quiet room
        Assert.Equal(1, Waveform.Level(1f));
        Assert.InRange(Waveform.Level(0.03f), 0.4, 0.6); // about -30 dBFS, normal speech

        Span<float> quiet = stackalloc float[Waveform.Bars];
        Span<float> loud = stackalloc float[Waveform.Bars];
        Waveform.Heights(0, 0.4, quiet);
        foreach (var h in quiet)
            Assert.Equal(Waveform.MinHeight, h);

        float[] weights = [.35f, .62f, .85f, 1f, .85f, .62f, .35f];
        for (double t = 0; t < 3; t += 0.05)
        {
            Waveform.Heights(1, t, loud);
            for (int i = 0; i < Waveform.Bars; i++)
                Assert.InRange(loud[i], Waveform.MinHeight + 0.55f * Waveform.Swing * weights[i] - 0.001f, Waveform.MinHeight + Waveform.Swing * weights[i] + 0.001f);
        }

        // Bars with the same weight move on different periods, so they are not one block.
        Waveform.Heights(1, 0.3, loud);
        Assert.NotEqual(loud[2], loud[4]);
    }

    [Fact]
    public async Task NoteIsStructuredFromARecordedReply()
    {
        const string transcript = "Okay so um tomorrow I need to call the dentist before ten, and uh send John the fishing plan, oh and buy milk. The team meeting moved to Thursday at three.";
        using var http = RecordedHandler.Client(out var handler, "openai-ollama-rambler-note.sse");
        var provider = new OpenAICompatibleProvider(http, "http://127.0.0.1:11434/v1", null);

        var note = await RamblerNote.StructureAsync(provider, "llama3.2:3b", transcript);

        Assert.Equal("### Tomorrow's Tasks\n- Call dentist before 10:00 AM\n- Send fishing plan to John\n- Buy milk\n- Team meeting now on Thursday at 3:00 PM", note);
        var body = JsonNode.Parse(handler.Body!)!;
        Assert.Equal(RamblerNote.Prompt, body["messages"]![0]!["content"]!.GetValue<string>());
        Assert.Equal(transcript, body["messages"]![1]!["content"]!.GetValue<string>());
    }

    [Fact]
    public void ParseDropsAFenceAndFallsBackToTheTranscript()
    {
        Assert.Equal("### Plan\n- [ ] Call Ana", RamblerNote.Parse("```markdown\n### Plan\n- [ ] Call Ana\n```\n", "x"));
        Assert.Equal("call Ana", RamblerNote.Parse("  \n", " call Ana "));
        Assert.Equal("call Ana", RamblerNote.Parse("```\n```", "call Ana"));
    }

    [Fact]
    public async Task TranscriptionPostsAWavAndReadsTheText()
    {
        using var http = RecordedHandler.Client(out var handler, "openai-transcription.json");
        var stt = new OpenAITranscriber(http, "https://api.openai.com/v1/", "sk-test-SECRET");

        var text = await stt.TranscribeAsync(new float[1600]);

        Assert.Equal("Okay so um tomorrow I need to call the dentist before ten.", text);
        Assert.Equal("https://api.openai.com/v1/audio/transcriptions", handler.Request!.RequestUri!.ToString());
        Assert.Equal("Bearer", handler.Request.Headers.Authorization!.Scheme);
        Assert.Contains("name=model", handler.Body);
        Assert.Contains("whisper-1", handler.Body);
        Assert.Contains("RIFF", handler.Body);

        var wav = OpenAITranscriber.Wav([0f, 1f, -1f], 16000);
        Assert.Equal(44 + 6, wav.Length);
        Assert.Equal(16000, BitConverter.ToInt32(wav, 24));
        Assert.Equal(short.MaxValue, BitConverter.ToInt16(wav, 46));
        Assert.Equal(-short.MaxValue, BitConverter.ToInt16(wav, 48));
    }

    [Fact]
    public void ScratchpadFileAppendsNotesUnderTimeHeadings()
    {
        var dir = Path.Combine(Path.GetTempPath(), "tomte-test-" + Guid.NewGuid());
        var file = new ScratchpadFile(Path.Combine(dir, "scratchpad.md"));
        try
        {
            file.Append("### First\n- one\n", new DateTime(2026, 10, 14, 9, 5, 0));
            file.Append("  second  ", new DateTime(2026, 10, 14, 9, 7, 0));

            Assert.Equal("## 2026-10-14 09:05\n\n### First\n- one\n\n## 2026-10-14 09:07\n\nsecond\n\n", File.ReadAllText(file.FilePath));
        }
        finally
        {
            Directory.Delete(dir, true);
        }
    }

    [WhisperFact]
    public async Task WhisperBindingRunsOnSilence()
    {
        // Proves the hand-written binding and the by-value parameter passing; silence gives an empty or bracketed text.
        var text = await new WhisperTranscriber(WhisperTranscriber.DefaultDirectory).TranscribeAsync(new float[16000]);
        Assert.DoesNotContain("dentist", text);
    }
}

/// <summary>Runs only when tools/whisper/Get-Whisper.ps1 has put whisper.cpp and the model in place.</summary>
public sealed class WhisperFactAttribute : FactAttribute
{
    public WhisperFactAttribute()
    {
        if (!WhisperTranscriber.IsInstalled(WhisperTranscriber.DefaultDirectory))
            Skip = $"whisper.cpp is not in {WhisperTranscriber.DefaultDirectory} (run tools/whisper/Get-Whisper.ps1).";
    }
}
