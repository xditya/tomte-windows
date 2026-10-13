namespace Tomte.Core.Rambler;

/// <summary>
/// Where a rambler note lands. The scratchpad panel is not built yet; when it is, it implements
/// this and <see cref="ScratchpadFile"/> goes away. An MCP integration is the other target.
/// </summary>
public interface INoteTarget
{
    void Append(string note, DateTime at);
}

/// <summary>One markdown file, each note under a time heading, oldest first.</summary>
public sealed class ScratchpadFile(string path) : INoteTarget
{
    public static string DefaultPath { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tomte", "scratchpad.md");

    public string FilePath { get; } = path;

    public void Append(string note, DateTime at)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.AppendAllText(FilePath, $"## {at:yyyy-MM-dd HH:mm}\n\n{note.Trim()}\n\n");
    }
}
