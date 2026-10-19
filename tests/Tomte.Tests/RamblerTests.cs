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
    public void RmsMapsToNineMirroredBarsBetweenRestAndFull()
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

        float[] weights = [.35f, .55f, .75f, .9f, 1f, .9f, .75f, .55f, .35f];
        for (double t = 0; t < 3; t += 0.05)
        {
            Waveform.Heights(1, t, loud);
            for (int i = 0; i < Waveform.Bars; i++)
                Assert.InRange(loud[i], Waveform.MinHeight + 0.55f * Waveform.Swing * weights[i] - 0.001f, Waveform.MinHeight + Waveform.Swing * weights[i] + 0.001f);
        }

        // Bars with the same weight move on different periods, so they are not one block.
        Waveform.Heights(1, 0.3, loud);
        Assert.NotEqual(loud[2], loud[6]);
        Assert.Equal(20, Waveform.MinHeight + Waveform.Swing);
    }

    [Fact]
    public void TheBarsFillInOneASecond()
    {
        Assert.Equal(1, Waveform.Reached(0));
        Assert.Equal(1, Waveform.Reached(0.99));
        Assert.Equal(7, Waveform.Reached(6.2)); // in the seventh second
        Assert.Equal(7, Waveform.Reached(7));
        Assert.Equal(Waveform.Bars, Waveform.Reached(8.5));
        Assert.Equal(Waveform.Bars, Waveform.Reached(600));
        Assert.Equal(1, Waveform.Reached(-1));
    }

    [Fact]
    public async Task TranscriptionPostsAWavAndReadsTheText()
    {
        // openai-transcription.json is hand-built from the documented response shape, not recorded (no key).
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
    public void NotesAppendUnderTimeHeadings()
    {
        var text = RamblerNotes.Append("", "### First\n- one\n", new DateTime(2026, 10, 14, 9, 5, 0));
        text += "typed by hand"; // the user's own text, with no blank line after it
        text = RamblerNotes.Append(text, "  second  ", new DateTime(2026, 10, 14, 9, 7, 0));

        Assert.Equal("## 2026-10-14 09:05\n\n### First\n- one\n\ntyped by hand\n\n## 2026-10-14 09:07\n\nsecond\n\n", text);
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

public class VoiceCardTests
{
    [Fact]
    public void TheTakeIsLoudnessBarsWithTheSilenceThatEndsItFlat()
    {
        // Two seconds of speech, then one of silence.
        float[] samples = [.. Enumerable.Range(0, 32000).Select(i => 0.05f * MathF.Sin(i / 5f)), .. new float[16000]];
        var take = Waveform.Take(samples, 12);
        Assert.Equal(12, take.Length);
        Assert.All(take[..8], bar => Assert.False(bar.Silent));
        Assert.All(take[8..], bar => Assert.Equal((Waveform.TakeMin, true), bar));
        Assert.All(take, bar => Assert.InRange(bar.Height, Waveform.TakeMin, Waveform.TakeHeight));
        Assert.True(take[0].Height > Waveform.TakeMin);

        // About six and a half bars a second, eight at least, and never more than the band holds.
        Assert.Equal(46, Waveform.TakeBars(7, 580));
        Assert.Equal(8, Waveform.TakeBars(0.5, 580));
        Assert.Equal(97, Waveform.TakeBars(60, 580));
    }

    [Fact]
    public void TheCardStacksItsPartsAndSplitsItsActionsByShare()
    {
        var layout = new VoiceCardLayout(620, mascot: false, words: 58);
        Assert.Equal(VoiceCardLayout.Side, layout.Mic.X);
        Assert.Equal(layout.Wave.Bottom + VoiceCardLayout.Gap, layout.Text.Y);
        var actions = layout.Actions;
        Assert.Equal(3, actions.Count);
        Assert.All(actions, a => Assert.Equal(VoiceCardLayout.ActionHeight, a.Height));
        Assert.Equal(VoiceCardLayout.InsertShare, actions[0].Width / actions[1].Width, 3);
        Assert.Equal(actions[1].Width, actions[2].Width, 3);
        Assert.Equal(620 - VoiceCardLayout.Side, actions[2].Right, 3);
        Assert.Equal(layout.Text.Bottom + VoiceCardLayout.Gap, actions[0].Y);

        // A reason line takes room under the row; the card grows by it.
        var off = new VoiceCardLayout(620, mascot: false, words: 58, line: "Ask Tomte needs an AI provider.");
        Assert.Equal(layout.Height + (VoiceCardLayout.Gap / 2) + VoiceCardLayout.LineHeight, off.Height, 3);

        // The hold chord at the left, E and Esc at the right end.
        var foot = layout.Footer(("Win Alt V", "hold to talk again"), [("E", "edit words"), ("Esc", "discard")]);
        Assert.Equal(["Win Alt V", "E", "Esc"], foot.Select(f => f.Keys));
        Assert.Equal(VoiceCardLayout.Side, foot[0].Cap.X);
        Assert.Equal(620 - VoiceCardLayout.Side, foot[^1].Word.Slot.Right, 3);
        Assert.Equal(["Esc"], layout.Footer(null, [("Esc", "discard")]).Select(f => f.Keys));

        // The privacy line is on the right, the lock before it.
        var (lockAt, where) = layout.Where(VoiceCardLayout.PrivacyLine(null));
        Assert.True(lockAt.Right < where.Slot.X && where.Slot.Right <= 620 - VoiceCardLayout.Side + 0.01f);
    }

    [Theory]
    [InlineData(null, "Transcribed on this PC · nothing sent")]
    [InlineData("OpenAI", "Transcribed by OpenAI · audio sent")]
    public void ThePrivacyLineSaysWhereTheWordsWereMade(string? provider, string line) => Assert.Equal(line, VoiceCardLayout.PrivacyLine(provider));

    [Theory]
    [InlineData("Standup notes - Notepad", "Standup notes")]
    [InlineData("Re: lunch - Inbox - Outlook", "Re: lunch - Inbox")]
    [InlineData("Untitled", "Untitled")]
    [InlineData("   ", null)]
    public void InsertNamesTheWindowItTypesInto(string title, string? target) => Assert.Equal(target, VoiceCardLayout.Target(title));

    [Theory]
    [InlineData(7, "0:07")]
    [InlineData(65, "1:05")]
    public void TheTakesLengthIsMinutesAndSeconds(int seconds, string shown) => Assert.Equal(shown, VoiceCardLayout.Length(TimeSpan.FromSeconds(seconds)));
}
