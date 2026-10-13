namespace Tomte.Core.Rambler;

/// <summary>
/// The scratchpad as one markdown file under %LOCALAPPDATA%\Tomte, until the Scratchpad panel brings
/// its own store. Read and written whole; nothing is held open.
/// </summary>
public sealed class ScratchpadFile(string path) : IScratchpad
{
    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tomte", "scratchpad.md");

    public string FilePath { get; } = path;

    public string Text
    {
        get => File.Exists(FilePath) ? File.ReadAllText(FilePath) : "";
        set
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, value);
        }
    }
}

public static class RamblerNotes
{
    /// <summary>Adds a rambler note at the end of the scratchpad under a time heading.</summary>
    public static void AppendNote(this IScratchpad scratchpad, string note, DateTime at)
    {
        var text = scratchpad.Text;
        if (text.Length > 0 && !text.EndsWith("\n\n", StringComparison.Ordinal))
            text = text.TrimEnd('\r', '\n') + "\n\n";
        scratchpad.Text = text + $"## {at:yyyy-MM-dd HH:mm}\n\n{note.Trim()}\n\n";
    }
}
