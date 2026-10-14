using Microsoft.Data.Sqlite;

namespace Tomte.Core.Storage;

/// <summary>
/// tomte.db, the one SQLite file, next to settings.json. A feature keeps its own tables in it and
/// reaches them only through <see cref="Open"/>, passing its name and its own list of schema steps,
/// so features share the file and nothing else. Creating this object touches nothing: the folder,
/// the file and the native library load on the first Open. A connection is for one piece of work
/// and is then disposed. There is no pool, so no handle outlives the work and an idle Tomte holds
/// nothing open.
/// </summary>
public sealed class TomteDb(string? path = null)
{
    // How long a statement waits for another connection's lock before it throws.
    private const int BusySeconds = 5;

    public static string DefaultPath { get; } = Path.Combine(Path.GetDirectoryName(SettingsStore.DefaultPath)!, "tomte.db");

    // Absolute on purpose: in a packaged app the provider hands SQLite the package's own data folder, and SQLite would
    // resolve a relative name against that instead of the current folder.
    public string FilePath { get; } = Path.GetFullPath(path ?? DefaultPath);

    /// <summary>
    /// Opens the database for one piece of work and first brings the feature's tables up to date:
    /// <paramref name="steps"/> is that feature's whole schema history in order, each entry one or
    /// more SQL statements. Steps this file has not seen run in one transaction, the rest are
    /// skipped. Only ever add to the end of the list. Prefix table names with the feature name.
    /// A step cannot switch foreign keys off (that setting is fixed while a transaction is open).
    /// Dispose the connection when the work is done; never keep one in a field.
    /// A file that is not a database, or whose schema is damaged, is renamed to tomte.db.bad-(time)
    /// and an empty one takes its place, so everything stored so far is gone from the app.
    /// </summary>
    /// <exception cref="SqliteException">Locked for longer than the wait, disk full, access denied, or a step that is not valid SQL.</exception>
    /// <exception cref="IOException">A damaged file could not be renamed because something else kept it open.</exception>
    public SqliteConnection Open(string feature, IReadOnlyList<string> steps)
    {
        if (TryConnect(feature, steps) is { } db)
        {
            return db;
        }

        // One process at a time sets a damaged file aside, and it looks again first: another may have replaced it already.
        // the name is per logon session; a job run started in another session would not share it.
        using var gate = new Mutex(false, @"Local\Tomte.Db.SetAside");
        try
        {
            gate.WaitOne();
        }
        catch (AbandonedMutexException)
        {
            // The last owner died holding it; it is ours now.
        }

        try
        {
            if (TryConnect(feature, steps) is { } replaced)
            {
                return replaced;
            }

            SetAside();
            return Connect(feature, steps);
        }
        finally
        {
            gate.ReleaseMutex();
        }
    }

    // only damage the open itself runs into is caught (the header, the schema, a feature's migration). A bad page
    // deep in a table throws from the feature's own query every time; PRAGMA quick_check on a process's first open would
    // find it, at a few milliseconds per megabyte.
    private SqliteConnection? TryConnect(string feature, IReadOnlyList<string> steps)
    {
        try
        {
            return Connect(feature, steps);
        }
        catch (SqliteException e) when (e.SqliteErrorCode is SQLitePCL.raw.SQLITE_NOTADB or SQLitePCL.raw.SQLITE_CORRUPT)
        {
            return null;
        }
    }

    private SqliteConnection Connect(string feature, IReadOnlyList<string> steps)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var db = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = FilePath,
            ForeignKeys = true,
            Pooling = false,
            DefaultTimeout = BusySeconds,
        }.ToString());
        try
        {
            db.Open();
            // SQLite's own wait backs off from 1 ms; without it the provider retries a locked statement every 150 ms.
            SQLitePCL.raw.sqlite3_busy_timeout(db.Handle, BusySeconds * 1000);
            // Write-ahead log: the shell, the settings window and a job run read while one of them writes, and the last
            // connection to close folds the log back into the file. NORMAL: no flush per commit; a power cut can drop the
            // commits made since the log was last folded back, but cannot damage the file.
            Run(db, """
                PRAGMA journal_mode = WAL;
                PRAGMA synchronous = NORMAL;
                CREATE TABLE IF NOT EXISTS schema_versions(feature TEXT PRIMARY KEY, version INTEGER NOT NULL) WITHOUT ROWID;
                """);
            Migrate(db, feature, steps);
            return db;
        }
        catch
        {
            db.Dispose();
            throw;
        }
    }

    private static void Migrate(SqliteConnection db, string feature, IReadOnlyList<string> steps)
    {
        // The usual path: one read, no write lock.
        if (Applied(db, feature) >= steps.Count)
        {
            return;
        }

        // BEGIN IMMEDIATE takes the write lock, so a second connection or process waits here and then finds the work done.
        using var transaction = db.BeginTransaction();
        int applied = Applied(db, feature);
        if (applied >= steps.Count)
        {
            return;
        }

        for (; applied < steps.Count; applied++)
        {
            Run(db, steps[applied]);
        }

        Run(db, "INSERT OR REPLACE INTO schema_versions(feature, version) VALUES ($feature, $version)", ("$feature", feature), ("$version", applied));
        transaction.Commit();
    }

    private static int Applied(SqliteConnection db, string feature)
    {
        using var command = Command(db, "SELECT version FROM schema_versions WHERE feature = $feature", ("$feature", feature));
        return Convert.ToInt32(command.ExecuteScalar() ?? 0);
    }

    private static void Run(SqliteConnection db, string sql, params ReadOnlySpan<(string Name, object Value)> parameters)
    {
        using var command = Command(db, sql, parameters);
        command.ExecuteNonQuery();
    }

    // CreateCommand enlists in the connection's open transaction and takes its timeout.
    private static SqliteCommand Command(SqliteConnection db, string sql, params ReadOnlySpan<(string Name, object Value)> parameters)
    {
        var command = db.CreateCommand();
        command.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        return command;
    }

    private void SetAside()
    {
        string bad = $"{FilePath}.bad-{DateTime.Now:yyyyMMdd-HHmmss-fff}";
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(FilePath, bad);
                return;
            }
            catch (IOException) when (attempt < 20)
            {
                // A connection that is failing on the same file right now still holds it and lets go within milliseconds.
                // one second of retries; after that the IOException reaches the caller and the next Open tries again.
                Thread.Sleep(50);
            }
        }
    }
}
