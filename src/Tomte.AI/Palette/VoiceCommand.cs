using Tomte.AI.Skills;
using Tomte.Core;
using Tomte.Core.Rambler;

namespace Tomte.AI.Palette;

/// <summary>Where a rambler transcript goes once it is text.</summary>
public static class VoiceCommand
{
    /// <summary>
    /// What a choice on the rambler's card does with the words. Insert hands them to <paramref name="insert"/>, which types
    /// them or says why it cannot; Add to note keeps them; Ask alone makes a session with <paramref name="start"/> and routes
    /// the words through it as <see cref="RouteAsync"/> does, so nothing reaches a provider before the user picks Ask.
    /// Returns why Insert could not type, if it could not, and the session Ask ran, if it made one.
    /// </summary>
    public static async Task<(string? Refused, PaletteSession? Asked)> PickedAsync(VoiceChoice choice, string transcript, Func<string, string?> insert, Func<string, Task> keep, Func<Task<PaletteSession?>> start, Func<PaletteSession, Task> run)
    {
        switch (choice)
        {
            case VoiceChoice.Insert:
                return (insert(transcript), null);
            case VoiceChoice.Note:
                await keep(transcript);
                return (null, null);
            default:
                var session = await start();
                await RouteAsync(transcript, keep, session, run);
                return (null, session);
        }
    }

    /// <summary>
    /// Without a session (the AI layer off, or no provider) the transcript is the raw note, which
    /// <paramref name="keep"/> puts on the scratchpad as the rambler has always done. With one, <paramref name="run"/>
    /// shows the palette while the model picks a command or a note and the user confirms a command. The raw words are
    /// kept only when nothing could be sent: no session, a palette taken down before the send, or a send that failed.
    /// A finished answer, in text or with tools, is not also a note (the AI history has the question and the answer),
    /// and a cancel keeps nothing. True when the raw words were kept.
    /// </summary>
    public static async Task<bool> RouteAsync(string transcript, Func<string, Task> keep, PaletteSession? session, Func<PaletteSession, Task> run)
    {
        if (session is not null)
        {
            session.Prompt = transcript;
            await run(session);
            if (session.Status is PaletteStatus.Done or PaletteStatus.Cancelled)
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
