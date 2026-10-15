namespace Tomte.Core.Rambler;

/// <summary>What the words do once the voice key is released: typed into the app, asked of Tomte, or added to the note.</summary>
public enum VoiceChoice { Insert, Ask, Note }

/// <summary>The rules of the card the words show on after a hold, before anything is typed, sent or saved.</summary>
public static class VoiceChoices
{
    /// <summary>The highlighted choice, which Enter picks: the one in settings, but Insert while Ask has no provider to go to.</summary>
    public static VoiceChoice Default(VoiceChoice setting, bool canAsk) =>
        setting == VoiceChoice.Ask && !canAsk ? VoiceChoice.Insert : setting;

    /// <summary>
    /// What a key on the card picks: Enter (<paramref name="alt"/> null) the highlighted choice, Alt+I, Alt+A and Alt+N
    /// their own. Null for Ask while there is no provider: it is disabled, and the card stays.
    /// </summary>
    public static VoiceChoice? Pick(VoiceChoice? alt, VoiceChoice setting, bool canAsk) =>
        (alt ?? Default(setting, canAsk)) is var picked && (picked != VoiceChoice.Ask || canAsk) ? picked : null;

    public static string Label(VoiceChoice choice) => choice switch
    {
        VoiceChoice.Insert => "Insert",
        VoiceChoice.Ask => "Ask Tomte",
        _ => "Add to note",
    };

    public static string Key(VoiceChoice choice) => choice switch
    {
        VoiceChoice.Insert => "Alt+I",
        VoiceChoice.Ask => "Alt+A",
        _ => "Alt+N",
    };

    public const string NoProvider = "Ask Tomte needs an AI provider.";
}
