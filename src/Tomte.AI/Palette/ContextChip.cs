using Tomte.AI.Files;
using Tomte.Core;

namespace Tomte.AI.Palette;

public enum ContextKind { Selection, Window, Screenshot, ShelfItem, Scratchpad, Region }

/// <summary>
/// One opt-in context attachment. Nothing is read until the chip is on and the user sends; an
/// off chip never runs its reader. <see cref="Available"/> is false when there is nothing to
/// attach (no selection, empty shelf), and the palette does not show the chip then. A chip with an
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
    Func<bool>? arm = null,
    Func<string>? tip = null)
{
    private Func<bool>? _arm = arm;
    private bool _on = on && available;

    public ContextKind Kind { get; } = kind;

    /// <summary>The chip's short name, as it shows.</summary>
    public string Label { get; } = label;

    /// <summary>What turning the chip on sends, in plain words from the chip's own data, for its tooltip.</summary>
    public string Tip => tip?.Invoke() ?? Label;

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
                Available = arm();
                _arm = null;
            }

            _on = value && Available;
        }
    }

    /// <summary>The chip as the palette draws it.</summary>
    public ChipSpec Spec => new(Label, Kind switch
    {
        ContextKind.Selection => ChipIcon.Selection,
        ContextKind.Window => ChipIcon.Window,
        ContextKind.Screenshot => ChipIcon.Screenshot,
        ContextKind.ShelfItem => ChipIcon.Shelf,
        ContextKind.Scratchpad => ChipIcon.Note,
        _ => ChipIcon.Region,
    }, On && Available);

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

/// <summary>
/// The chips the palette offers, built from what is in front of the user when it opens. Each has a short name and a
/// tooltip that says what it would send.
/// </summary>
public static class ContextChips
{
    /// <summary>
    /// The hotkey palette's chips: the circled region first and on when circle-to-ask opened it, then the selection,
    /// the window, its screenshot, the shelf and the scratchpad, each off until the user turns it on.
    /// </summary>
    public static List<ContextChip> For(ContextChip? lasso, string? selection, string app, string title, Func<CancellationToken, Task<byte[]>> screenshot, IShelf shelf, IScratchpad scratchpad) =>
        [.. lasso is null ? [] : new[] { lasso }, Selection(selection, app: app), Window(app, title), Screenshot(app, screenshot), Shelf(shelf), Scratchpad(scratchpad)];

    /// <summary>
    /// The UIA selection is read when the palette opens (focus moves once the user clicks in), so
    /// <paramref name="text"/> is already here. When UIA could not tell (<paramref name="fallback"/>
    /// given), the chip offers itself and runs the Ctrl+C fallback only when the user turns it on.
    /// Every chip starts off, this one included.
    /// <paramref name="app"/> is the app the text is in.
    /// </summary>
    public static ContextChip Selection(string? text, Func<string?>? fallback = null, string app = "")
    {
        bool has = !string.IsNullOrWhiteSpace(text), armed = !has && fallback is not null;
        return new(ContextKind.Selection,
            "Selection",
            "selection",
            _ => Task.FromResult<IReadOnlyList<ContentPart>>([ContextAssembly.Block("selection", text ?? "")]),
            available: has || armed,
            on: false,
            arm: !armed ? null : () =>
            {
                text = fallback!();
                (has, armed) = (!string.IsNullOrWhiteSpace(text), false);
                return has;
            },
            tip: () => has ? $"Selected text{From(app)}, {Count(text!.Length, "character")}"
                : armed ? $"Copies the selected text{From(app)} when you turn this on, then puts the clipboard back"
                : "No text is selected");
    }

    public static ContextChip Window(string app, string title) => new(ContextKind.Window,
        "Window",
        $"window:{app}",
        _ => Task.FromResult<IReadOnlyList<ContentPart>>([ContextAssembly.Block("active_window", title, $"app=\"{app}\"")]),
        available: title.Length > 0 || app.Length > 0,
        tip: () => title.Length > 0 ? $"Window title{From(app)}: {ContextAssembly.Trim(title, 80)}" : $"The name of the app in front: {app}");

    /// <summary>The screenshot is taken at send time; <paramref name="capture"/> returns PNG bytes. With no app there is no window to take.</summary>
    public static ContextChip Screenshot(string app, Func<CancellationToken, Task<byte[]>> capture) => new(ContextKind.Screenshot,
        "Screenshot",
        $"screenshot:{app}",
        async ct => [new ImagePart("image/png", await capture(ct))],
        available: app.Length > 0,
        tip: () => $"Screenshot of the {app} window, sent as an image");

    /// <summary>
    /// The circled region from circle-to-ask, captured when the lasso was released. Drawing the lasso is the
    /// user's opt-in for this request, so the chip starts on; the size in the tooltip is in DIPs.
    /// </summary>
    public static ContextChip Lasso(byte[] png, double widthDips, double heightDips) => new(ContextKind.Region,
        "Region",
        "lasso",
        _ => Task.FromResult<IReadOnlyList<ContentPart>>([new ImagePart("image/png", png)]),
        on: true,
        tip: () => $"The region you circled, {widthDips:F0} × {heightDips:F0}, sent as an image");

    /// <summary>
    /// The newest shelf item, and the shelf: with this chip on, the model may read the other items with read_shelf_item,
    /// and their names come along. One chip is enough until the shelf has a picker. The session reads the file at send.
    /// </summary>
    public static ContextChip Shelf(IShelf shelf)
    {
        var name = shelf.ItemNames.Count > 0 ? shelf.ItemNames[0] : null;
        int count = shelf.ItemNames.Count;
        return new(ContextKind.ShelfItem,
            "Shelf",
            // The other items' names go to the provider too, so the send log names them.
            name is null ? "shelf" : $"shelf:{name}" + (count > 1 ? $", names: {string.Join(", ", shelf.ItemNames.Skip(1).Select(PreparedFile.Clean))}" : ""),
            _ => Task.FromResult<IReadOnlyList<ContentPart>>(count > 1
                ? [new ShelfFilePart(name!), ContextAssembly.Block("shelf_items", string.Join('\n', shelf.ItemNames.Skip(1).Select(PreparedFile.Clean)))]
                : [new ShelfFilePart(name!)]),
            available: name is not null,
            // The tooltip says that the other items' names go along too.
            tip: () => name is null ? "The shelf is empty" : $"Shelf: {name}" + count switch { 1 => "", 2 => " and the name of 1 other", var n => $" and the names of {n - 1} others" });
    }

    public static ContextChip Scratchpad(IScratchpad scratchpad) => new(ContextKind.Scratchpad,
        "Note",
        "scratchpad",
        _ => Task.FromResult<IReadOnlyList<ContentPart>>([ContextAssembly.Block("scratchpad", scratchpad.Text)]),
        // Counted when the tooltip shows: the note may still be loading when the palette opens.
        tip: () => string.IsNullOrWhiteSpace(scratchpad.Text) ? "The Scratchpad note, which is empty"
            : $"The Scratchpad note, {Count(scratchpad.Text.ReplaceLineEndings("\n").TrimEnd('\n').Split('\n').Length, "line")}");

    private static string From(string app) => app.Length > 0 ? $" from {app}" : "";

    private static string Count(int n, string what) => $"{n} {what}{(n == 1 ? "" : "s")}";
}
