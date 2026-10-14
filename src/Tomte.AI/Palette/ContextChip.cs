using Tomte.AI.Files;
using Tomte.Core;

namespace Tomte.AI.Palette;

public enum ContextKind { Selection, Window, Screenshot, ShelfItem, Scratchpad }

/// <summary>
/// One opt-in context attachment. Nothing is read until the chip is on and the user sends; an
/// off chip never runs its reader. <see cref="Available"/> is false when there is nothing to
/// attach (no selection, empty shelf) so the renderer can grey the chip out. A chip with an
/// <c>arm</c> step runs it the first time the user turns it on (the Ctrl+C selection fallback),
/// so no input is injected and no clipboard replaced until the user has acted.
/// </summary>
public sealed class ContextChip(
    ContextKind kind,
    string label,
    string attachedName,
    Func<CancellationToken, Task<IReadOnlyList<ContentPart>>> read,
    bool available = true,
    bool on = false,
    Func<(string Label, bool Available)>? arm = null)
{
    private Func<(string Label, bool Available)>? _arm = arm;
    private bool _on = on && available;

    public ContextKind Kind { get; } = kind;

    public string Label { get; private set; } = label;

    /// <summary>What the send log records for this chip, for example a file name.</summary>
    public string AttachedName { get; } = attachedName;

    public bool Available { get; private set; } = available;

    public bool On
    {
        get => _on;
        set
        {
            if (value && _arm is { } arm)
            {
                (Label, Available) = arm();
                _arm = null;
            }

            _on = value && Available;
        }
    }

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
    /// <summary>
    /// The UIA selection is read when the palette opens (focus moves once the user clicks in), so
    /// <paramref name="text"/> is already here. When UIA could not tell (<paramref name="fallback"/>
    /// given), the chip offers itself and runs the Ctrl+C fallback only when the user turns it on.
    /// Every chip starts off, this one included.
    ///
    /// </summary>
    public static ContextChip Selection(string? text, Func<string?>? fallback = null)
    {
        bool has = !string.IsNullOrWhiteSpace(text);
        string LabelFor() => has ? $"Selection · \"{ContextAssembly.Trim(text!)}\"" : "Selection · none";
        return new(ContextKind.Selection,
            !has && fallback is not null ? "Selection · read on use" : LabelFor(),
            "selection",
            _ => Task.FromResult<IReadOnlyList<ContentPart>>([ContextAssembly.Block("selection", text ?? "")]),
            available: has || fallback is not null,
            on: false,
            arm: has || fallback is null ? null : () =>
            {
                text = fallback();
                has = !string.IsNullOrWhiteSpace(text);
                return (LabelFor(), has);
            });
    }

    public static ContextChip Window(string app, string title) => new(ContextKind.Window,
        $"Window · {app} · {ContextAssembly.Trim(title, 40)}",
        $"window:{app}",
        _ => Task.FromResult<IReadOnlyList<ContentPart>>([ContextAssembly.Block("active_window", title, $"app=\"{app}\"")]),
        available: title.Length > 0 || app.Length > 0);

    /// <summary>The screenshot is taken at send time; <paramref name="capture"/> returns PNG bytes.</summary>
    public static ContextChip Screenshot(string app, Func<CancellationToken, Task<byte[]>> capture) => new(ContextKind.Screenshot,
        "Screenshot of window",
        $"screenshot:{app}",
        async ct => [new ImagePart("image/png", await capture(ct))]);

    /// <summary>
    /// The circled region from circle-to-ask, captured when the lasso was released. Drawing the lasso is the
    /// user's opt-in for this request, so the chip starts on; the size in the label is in DIPs.
    /// </summary>
    public static ContextChip Lasso(byte[] png, double widthDips, double heightDips) => new(ContextKind.Screenshot,
        $"Lasso · {widthDips:F0} × {heightDips:F0}",
        "lasso",
        _ => Task.FromResult<IReadOnlyList<ContentPart>>([new ImagePart("image/png", png)]),
        on: true);

    /// <summary>
    /// The newest shelf item, and the shelf: with this chip on, the model may read the other items with read_shelf_item,
    /// and their names come along. One chip is enough until the shelf has a picker. The session reads the file at send.
    /// </summary>
    public static ContextChip Shelf(IShelf shelf)
    {
        var name = shelf.ItemNames.Count > 0 ? shelf.ItemNames[0] : null;
        return new(ContextKind.ShelfItem,
            name is null ? "Shelf · empty" : $"Shelf · {name}",
            // The other items' names go to the provider too, so the send log names them.
            name is null ? "shelf" : $"shelf:{name}" + (shelf.ItemNames.Count > 1 ? $", names: {string.Join(", ", shelf.ItemNames.Skip(1).Select(PreparedFile.Clean))}" : ""),
            _ => Task.FromResult<IReadOnlyList<ContentPart>>(shelf.ItemNames.Count > 1
                ? [new ShelfFilePart(name!), ContextAssembly.Block("shelf_items", string.Join('\n', shelf.ItemNames.Skip(1).Select(PreparedFile.Clean)))]
                : [new ShelfFilePart(name!)]),
            available: name is not null);
    }

    public static ContextChip Scratchpad(IScratchpad scratchpad) => new(ContextKind.Scratchpad,
        "Scratchpad",
        "scratchpad",
        _ => Task.FromResult<IReadOnlyList<ContentPart>>([ContextAssembly.Block("scratchpad", scratchpad.Text)]));
}
