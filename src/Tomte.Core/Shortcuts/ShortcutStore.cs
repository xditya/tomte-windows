using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Tomte.Core.Device;
using Tomte.Core.Storage;

namespace Tomte.Core.Shortcuts;

/// <summary>One tile as stored: its name, its target, and the fingerprint of the script command the user confirmed, if any.</summary>
public sealed record Shortcut(long Id, string Name, string Target, string? Confirmed);

public enum ShortcutOutcome
{
    Opened,
    Failed,
    Missing,
    Blocked,

    /// <summary>A script tile while script shortcuts are off in Settings.</summary>
    ScriptsOff,

    /// <summary>A script tile the user has not confirmed yet.</summary>
    NeedsConfirm,

    /// <summary>A script tile whose command or script changed since the user confirmed it.</summary>
    Changed,
}

public readonly record struct ShortcutLaunch(ShortcutOutcome Outcome, string Message);

/// <summary>
/// The shortcut tiles in tomte.db, and starting them. Every method opens the database or reads the disk and
/// blocks (milliseconds as a rule, seconds behind a stuck lock), so call them from a worker thread only. Nothing
/// here runs until a caller asks: the panel loads its tiles when it first shows.
/// A script tile runs only when script shortcuts are on (<paramref name="scriptsOn"/>, the Settings switch) and
/// the user confirmed that tile's exact command, and only from a click on the tile: <see cref="LaunchByName"/>,
/// which voice and the AI layer reach, never runs one. Adding, confirming and the switch belong to the
/// settings window; no AI tool calls them.
/// </summary>
/// <param name="start">Starts what a tile opens; tests replace it with a recorder.</param>
public sealed class ShortcutStore(TomteDb db, Func<bool> scriptsOn, Func<ShortcutTarget, ActionResult>? start = null)
{
    private const string Feature = "shortcuts";

    // The whole history, oldest first. Never edit or reorder a step that has merged; add at the end.
    private static readonly string[] Schema =
    [
        "CREATE TABLE shortcuts_tiles(id INTEGER PRIMARY KEY, name TEXT NOT NULL, target TEXT NOT NULL, confirmed TEXT)",
    ];

    private readonly Func<ShortcutTarget, ActionResult> start = start ?? Start;

    /// <summary>The tiles in the order they were added.</summary>
    public IReadOnlyList<Shortcut> Load()
    {
        using var connection = db.Open(Feature, Schema);
        using var command = Command(connection, "SELECT id, name, target, confirmed FROM shortcuts_tiles ORDER BY id");
        using var reader = command.ExecuteReader();
        var tiles = new List<Shortcut>();
        while (reader.Read())
        {
            tiles.Add(new(reader.GetInt64(0), reader.GetString(1), reader.GetString(2), reader.IsDBNull(3) ? null : reader.GetString(3)));
        }

        return tiles;
    }

    /// <summary>
    /// Adds a tile at the end. Null when it was added; otherwise why not, in words for the user. A script can be
    /// added; it stays off until the switch is on and the user confirms it.
    /// </summary>
    public string? Add(string target)
    {
        target = target.Trim();
        var read = ShortcutTarget.Read(target);
        if (read.Kind == ShortcutKind.Blocked)
        {
            return read.Problem;
        }

        if (read.Missing)
        {
            return $"There is nothing at {target}.";
        }

        // A web address is stored the way it opens, so the tile and the settings list show what will load.
        string stored = read.Kind == ShortcutKind.Url && ShortcutTarget.WebAddress(target) is { } uri ? uri.AbsoluteUri : target;
        using var connection = db.Open(Feature, Schema);
        using var command = Command(connection, "INSERT INTO shortcuts_tiles(name, target) VALUES ($name, $target)", ("$name", ShortcutTarget.NameFor(stored)), ("$target", stored));
        command.ExecuteNonQuery();
        return null;
    }

    public void Remove(long id)
    {
        using var connection = db.Open(Feature, Schema);
        using var command = Command(connection, "DELETE FROM shortcuts_tiles WHERE id = $id", ("$id", id));
        command.ExecuteNonQuery();
    }

    /// <summary>The user saw this command in full and confirmed it for this tile. Only the settings window calls this.</summary>
    public void Confirm(long id, ScriptCommand command)
    {
        using var connection = db.Open(Feature, Schema);
        using var update = Command(connection, "UPDATE shortcuts_tiles SET confirmed = $fingerprint WHERE id = $id", ("$fingerprint", command.Fingerprint), ("$id", id));
        update.ExecuteNonQuery();
    }

    /// <summary>
    /// Why this tile would not start now, or null when a click would start it. The panel and the settings window
    /// flag a tile with it; <see cref="Launch"/> asks the same question again at the moment of the click.
    /// </summary>
    public static ShortcutOutcome? Gate(Shortcut tile, ShortcutTarget target, bool scriptsOn) =>
        target.Missing ? ShortcutOutcome.Missing
        : target.Kind == ShortcutKind.Blocked ? ShortcutOutcome.Blocked
        : target.Kind != ShortcutKind.Script ? null
        : !scriptsOn ? ShortcutOutcome.ScriptsOff
        : tile.Confirmed is null ? ShortcutOutcome.NeedsConfirm
        : tile.Confirmed != target.Script!.Fingerprint ? ShortcutOutcome.Changed
        : null;

    /// <summary>
    /// A click on the tile. The row and the target are read again now, so a confirmation, a removal or a changed
    /// script since the panel loaded counts.
    /// </summary>
    public ShortcutLaunch Launch(long id)
    {
        var tile = Load().FirstOrDefault(t => t.Id == id);
        return tile is null ? new(ShortcutOutcome.Failed, "That shortcut was removed.") : Launch(tile, scripts: true);
    }

    /// <summary>
    /// Starts the tile with this name, case and outer spaces ignored, as a click would, except that a script tile
    /// never runs this way. True when something was opened. For "open the shortcut called X".
    /// </summary>
    public bool LaunchByName(string name)
    {
        var tile = Load().FirstOrDefault(t => string.Equals(t.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
        return tile is not null && Launch(tile, scripts: false).Outcome == ShortcutOutcome.Opened;
    }

    private ShortcutLaunch Launch(Shortcut tile, bool scripts)
    {
        var target = ShortcutTarget.Read(tile.Target);
        if (!scripts && target.Kind == ShortcutKind.Script)
        {
            return new(ShortcutOutcome.Blocked, $"{tile.Name} is a script; scripts run only from a click on their tile.");
        }

        switch (Gate(tile, target, scriptsOn()))
        {
            case ShortcutOutcome.Missing:
                return new(ShortcutOutcome.Missing, $"{tile.Name} has moved or been deleted.");
            case ShortcutOutcome.Blocked:
                return new(ShortcutOutcome.Blocked, target.Problem!);
            case ShortcutOutcome.ScriptsOff:
                return new(ShortcutOutcome.ScriptsOff, "Script shortcuts are off. Turn them on in Settings, under Shortcuts.");
            case ShortcutOutcome.NeedsConfirm:
                return new(ShortcutOutcome.NeedsConfirm, $"Confirm {tile.Name} in Settings, under Shortcuts, before it runs.");
            case ShortcutOutcome.Changed:
                return new(ShortcutOutcome.Changed, $"{tile.Name} changed since you confirmed it. Confirm it again in Settings.");
        }

        var result = start(target);
        return new(result.Ok ? ShortcutOutcome.Opened : ShortcutOutcome.Failed, result.Message);
    }

    // Apps open or come to the front through the app launcher; everything else goes through the same launcher's shell
    // start. A script runs as its shown command and nothing else.
    private static ActionResult Start(ShortcutTarget target) => target.Kind switch
    {
        ShortcutKind.App => Apps.OpenApp(target.Open),
        ShortcutKind.Script => Apps.Launch(
            new ProcessStartInfo(target.Script!.Exe, target.Script.Arguments) { UseShellExecute = true, WorkingDirectory = target.Script.Folder },
            Path.GetFileName(target.Open)),
        _ => Apps.Launch(new ProcessStartInfo(target.Open) { UseShellExecute = true }, target.Open),
    };

    private static SqliteCommand Command(SqliteConnection connection, string sql, params ReadOnlySpan<(string Name, object Value)> parameters)
    {
        var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }
}
