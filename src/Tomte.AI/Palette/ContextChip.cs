using Tomte.Core;

namespace Tomte.AI.Palette;

public enum ContextKind { Selection, Window, Screenshot, ShelfItem, Scratchpad }

/// <summary>
/// One opt-in context attachment. Nothing is read until the chip is on and the user sends; an
/// off chip never runs its reader. <see cref="Available"/> is false when there is nothing to
/// attach (no selection, empty shelf) so the renderer can grey the chip out.
/// </summary>
public sealed class ContextChip(ContextKind kind, string label, string attachedName, Func<CancellationToken, Task<IReadOnlyList<ContentPart>>> read, bool available = true, bool on = false)
{
    public ContextKind Kind { get; } = kind;

    public string Label { get; } = label;

    /// <summary>What the send log records for this chip, for example a file name.</summary>
    public string AttachedName { get; } = attachedName;

    public bool Available { get; } = available;

    public bool On { get; set; } = on && available;

    internal Task<IReadOnlyList<ContentPart>> ReadAsync(CancellationToken ct) => read(ct);
}

public static class ContextAssembly
{
    /// <summary>Parts for the chips that are on, in chip order, then the prompt. Off chips are never read.</summary>
    public static async Task<(List<ContentPart> Parts, List<string> Attached)> BuildAsync(IEnumerable<ContextChip> chips, string prompt, CancellationToken ct)
    {
        var parts = new List<ContentPart>();
        var attached = new List<string>();
        foreach (var chip in chips.Where(c => c.On && c.Available))
        {
            parts.AddRange(await chip.ReadAsync(ct));
            attached.Add(chip.AttachedName);
        }
        parts.Add(new TextPart(prompt));
        return (parts, attached);
    }

    /// <summary>Tagged block so the model can tell attached context from the question.</summary>
    public static TextPart Block(string tag, string text, string? attribute = null) =>
        new($"<{tag}{(attribute is null ? "" : " " + attribute)}>\n{text}\n</{tag}>");

    public static string Trim(string text, int max = 28)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length > max ? line[..max] + "..." : line;
    }
}

/// <summary>The chips the palette offers, built from what is in front of the user when it opens.</summary>
public static class ContextChips
{
    /// <summary>The selection is read when the palette opens (focus moves once the user clicks in), so the text is already here.</summary>
    public static ContextChip Selection(string? text)
    {
        bool has = !string.IsNullOrWhiteSpace(text);
        return new(ContextKind.Selection,
            has ? $"Selection · \"{ContextAssembly.Trim(text!)}\"" : "Selection · none",
            "selection",
            _ => Task.FromResult<IReadOnlyList<ContentPart>>([ContextAssembly.Block("selection", text ?? "")]),
            available: has,
            on: has);
    }

    public static ContextChip Window(string app, string title) => new(ContextKind.Window,
        $"Window · {app} — {ContextAssembly.Trim(title, 40)}",
        $"window:{app}",
        _ => Task.FromResult<IReadOnlyList<ContentPart>>([ContextAssembly.Block("active_window", title, $"app=\"{app}\"")]),
        available: title.Length > 0 || app.Length > 0);

    /// <summary>The screenshot is taken at send time; <paramref name="capture"/> returns PNG bytes.</summary>
    public static ContextChip Screenshot(string app, Func<CancellationToken, Task<byte[]>> capture) => new(ContextKind.Screenshot,
        "Screenshot of window",
        $"screenshot:{app}",
        async ct => [new ImagePart("image/png", await capture(ct))]);

    /// <summary>The newest shelf item. One chip is enough until the shelf has a picker.</summary>
    public static ContextChip Shelf(IShelf shelf)
    {
        var name = shelf.ItemNames.Count > 0 ? shelf.ItemNames[0] : null;
        return new(ContextKind.ShelfItem,
            name is null ? "Shelf · empty" : $"Shelf · {name}",
            name is null ? "shelf" : $"shelf:{name}",
            _ => Task.FromResult<IReadOnlyList<ContentPart>>(
                [ContextAssembly.Block("shelf_item", shelf.ReadText(name!) ?? "(not a text item)", $"name=\"{name}\"")]),
            available: name is not null);
    }

    public static ContextChip Scratchpad(IScratchpad scratchpad) => new(ContextKind.Scratchpad,
        "Scratchpad",
        "scratchpad",
        _ => Task.FromResult<IReadOnlyList<ContentPart>>([ContextAssembly.Block("scratchpad", scratchpad.Text)]));
}
