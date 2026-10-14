using Tomte.AI.Skills;
using Tomte.Core;

namespace Tomte.AI.Palette;

/// <summary>Where a rambler transcript goes once it is text.</summary>
public static class VoiceCommand
{
    /// <summary>
    /// Without a session (the AI layer off, or no provider) the transcript is the raw note, which
    /// <paramref name="keep"/> puts on the scratchpad as the rambler has always done. With one, <paramref name="run"/>
    /// shows the palette while the model picks a command or a note and the user confirms a command. Whenever nothing
    /// ran the raw words are kept too, so a model that answers a dictated note in text, or a send that failed, never
    /// loses what was said; only a cancel or the user's no keeps nothing. True when the raw words were kept.
    /// </summary>
    public static async Task<bool> RouteAsync(string transcript, Func<string, Task> keep, PaletteSession? session, Func<PaletteSession, Task> run)
    {
        if (session is not null)
        {
            session.Prompt = transcript;
            await run(session);
            if (session.Status == PaletteStatus.Cancelled
                || session.Answer.OfType<ToolStepSegment>().Any(s => !s.IsError || s.Result == PaletteSession.NotAllowed))
                return false;
        }

        await keep(transcript);
        return true;
    }

    /// <summary>
    /// The session for one transcript, made from <paramref name="configured"/>, the provider set up at the moment the
    /// command starts, so one set up after Tomte started is used from the next hold on. Null while there is none: the
    /// transcript is then the raw note, and no skill is read. Voice is offered no skill; the turned-on ones are read,
    /// off the island's thread, only so open_app keeps refusing the files in their folder.
    /// </summary>
    public static async Task<PaletteSession?> StartAsync((IChatProvider Provider, ProviderConfig Config, PaletteOptions Options)? configured, Func<SkillSet> skills, SendLog log, BuiltInTools tools, PermissionGrants grants, AiHistory? history = null)
    {
        if (configured is not { } now)
            return null;
        var loaded = await Task.Run(skills);
        return new PaletteSession(now.Provider, now.Config, now.Options, log, tools, grants, []) { Voice = true, Skills = loaded, History = history };
    }

    /// <summary>The reply to read aloud: only with spoken replies on, after a finished answer that has text.</summary>
    public static string? ReplyToSpeak(IPaletteSession session, bool spokenReplies) =>
        spokenReplies && session.Status == PaletteStatus.Done && session.AnswerText.Trim() is { Length: > 0 } reply ? reply : null;
}
