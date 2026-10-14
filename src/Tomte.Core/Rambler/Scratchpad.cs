namespace Tomte.Core.Rambler;

public static class RamblerNotes
{
    /// <summary>The note's text with a rambler note added at the end under a time heading.</summary>
    public static string Append(string text, string note, DateTime at)
    {
        if (text.Length > 0 && !text.EndsWith("\n\n", StringComparison.Ordinal))
            text = text.TrimEnd('\r', '\n') + "\n\n";
        return text + $"## {at:yyyy-MM-dd HH:mm}\n\n{note.Trim()}\n\n";
    }
}
