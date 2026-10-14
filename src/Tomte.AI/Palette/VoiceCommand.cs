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

    /// <summary>The reply to read aloud: only with spoken replies on, after a finished answer that has text.</summary>
    public static string? ReplyToSpeak(IPaletteSession session, bool spokenReplies) =>
        spokenReplies && session.Status == PaletteStatus.Done && session.AnswerText.Trim() is { Length: > 0 } reply ? reply : null;
}

/// <summary>
/// The one question a voice palette has open. The user's yes or no answers it, and so does anything that ends the
/// session (its cancel answers no). An answer given for one question never lands on a later one. One thread.
/// </summary>
public sealed class VoiceQuestion
{
    private TaskCompletionSource<PermissionAnswer>? _answer;

    /// <summary>What is being asked now, or null.</summary>
    public ToolRequest? Asked { get; private set; }

    /// <summary>Opens the question; <see cref="Asked"/> is set before this returns.</summary>
    public async Task<PermissionAnswer> AskAsync(ToolRequest request, CancellationToken ct)
    {
        var answer = _answer = new TaskCompletionSource<PermissionAnswer>(TaskCreationOptions.RunContinuationsAsynchronously);
        Asked = request;
        using var cancel = ct.Register(() => answer.TrySetResult(PermissionAnswer.Deny));
        try
        {
            return await answer.Task;
        }
        finally
        {
            if (_answer == answer)
                (_answer, Asked) = (null, null);
        }
    }

    /// <summary>
    /// Answers the open question. With <paramref name="to"/>, only while that request is still the one asked. False
    /// when nothing was answered.
    /// </summary>
    public bool Answer(bool allow, ToolRequest? to = null) =>
        _answer is { } open && (to is null || ReferenceEquals(to, Asked)) && open.TrySetResult(allow ? PermissionAnswer.AllowOnce : PermissionAnswer.Deny);
}
