namespace Tomte.Core;

/// <summary>
/// What the AI tools need from the shelf. The real shelf (SQLite-backed, with drag in and out) is
/// not built yet; it implements this when it lands. Names are unique per shelf.
/// </summary>
public interface IShelf
{
    IReadOnlyList<string> ItemNames { get; }

    /// <summary>The item's text, or null when there is no such item or it is not text.</summary>
    string? ReadText(string name);

    /// <summary>Creates a text file on the shelf. Replaces an item of the same name.</summary>
    void CreateFile(string name, string content);

    /// <summary>Removes the item; no-op when there is none. Used to undo a created file.</summary>
    void Remove(string name);
}

/// <summary>The one persisted quick-capture note, as the AI tools see it.</summary>
public interface IScratchpad
{
    string Text { get; set; }
}

public sealed class InMemoryShelf : IShelf
{
    private readonly Dictionary<string, string> _items = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _order = [];

    public IReadOnlyList<string> ItemNames => _order;

    public string? ReadText(string name) => _items.GetValueOrDefault(name);

    public void CreateFile(string name, string content)
    {
        if (!_items.ContainsKey(name))
            _order.Insert(0, name);
        _items[name] = content;
    }

    public void Remove(string name)
    {
        if (_items.Remove(name))
            _order.RemoveAll(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase));
    }
}

public sealed class InMemoryScratchpad : IScratchpad
{
    public string Text { get; set; } = "";
}
