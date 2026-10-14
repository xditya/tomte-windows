namespace Tomte.AI.Palette;

/// <summary>Where a rambler transcript goes once it is text.</summary>
public static class VoiceCommand
{
    /// <summary>
    /// Without a session (the AI layer off, or no provider) the transcript is the raw note, kept by
    /// <paramref name="keep"/> on the scratchpad as the rambler has always done. With one, <paramref name="run"/> shows the palette while the model picks a command or a
    /// note and the user confirms it. A send that failed before anything ran keeps the raw words too: losing what was
    /// said is worse than an untidy note. A cancel, a refusal or an answer with no tool call keeps nothing.
    /// True when the raw note was written.
    /// </summary>
    public static async Task<bool> RouteAsync(string transcript, Func<string, Task> keep, PaletteSession? session, Func<PaletteSession, Task> run)
    {
        if (session is not null)
        {
            session.Prompt = transcript;
            await run(session);
            if (session.Status != PaletteStatus.Failed || session.Answer.OfType<ToolStepSegment>().Any(s => !s.IsError))
                return false;
        }

        await keep(transcript);
        return true;
    }

    /// <summary>The reply to read aloud: only with spoken replies on, after a finished answer that has text.</summary>
    public static string? ReplyToSpeak(IPaletteSession session, bool spokenReplies) =>
        spokenReplies && session.Status == PaletteStatus.Done && session.AnswerText.Trim() is { Length: > 0 } reply ? reply : null;
}
