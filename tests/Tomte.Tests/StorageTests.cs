using System.Diagnostics;
using System.Globalization;
using Microsoft.Data.Sqlite;
using Tomte.Core.Storage;
using Xunit;

namespace Tomte.Tests;

public sealed class StorageTests : IDisposable
{
    // The second step fails if it runs twice, and takes long enough that the others arrive while it holds the write lock.
    internal static readonly string[] Slow =
    [
        "CREATE TABLE slow_rows(n INTEGER PRIMARY KEY)",
        "WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n + 1 FROM numbers WHERE n < 200000) INSERT INTO slow_rows SELECT n FROM numbers",
    ];

    private const string Garbage = "This is a text file that somebody saved over the database. It is not one.";

    private static readonly string[] Shelf =
    [
        "CREATE TABLE shelf_items(id INTEGER PRIMARY KEY, name TEXT NOT NULL)",
        "INSERT INTO shelf_items(name) VALUES ('seed')",
    ];

    private static readonly string[] Shortcuts = ["CREATE TABLE shortcuts_tiles(id INTEGER PRIMARY KEY, target TEXT NOT NULL)"];

    private readonly string dir = Path.Combine(Path.GetTempPath(), "tomte-tests-" + Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(dir, "tomte.db");

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void NothingExistsUntilTheFirstOpen()
    {
        var db = new TomteDb(DbPath);
        Assert.False(Directory.Exists(dir));

        db.Open("shelf", Shelf).Dispose();
        Assert.True(File.Exists(DbPath));
    }

    [Fact]
    public void ADisposedConnectionLeavesOneFileAndNoHandle()
    {
        new TomteDb(DbPath).Open("shelf", Shelf).Dispose();

        Assert.Equal(["tomte.db"], Directory.GetFiles(dir).Select(Path.GetFileName));
        // Fails with a sharing violation while any handle is left.
        using var alone = new FileStream(DbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public void EveryConnectionGetsTheSameSettings()
    {
        var db = new TomteDb(DbPath);
        for (int i = 0; i < 2; i++)
        {
            using var connection = db.Open("shelf", Shelf);
            Assert.Equal("wal", Scalar(connection, "PRAGMA journal_mode"));
            Assert.Equal("1", Scalar(connection, "PRAGMA synchronous"));
            Assert.Equal("1", Scalar(connection, "PRAGMA foreign_keys"));
            Assert.Equal("5000", Scalar(connection, "PRAGMA busy_timeout"));
        }
    }

    [Fact]
    public void MigratingAgainRunsNothing()
    {
        var db = new TomteDb(DbPath);
        db.Open("shelf", Shelf).Dispose();

        using var again = db.Open("shelf", Shelf);
        Assert.Equal("1", Scalar(again, "SELECT count(*) FROM shelf_items"));
        Assert.Equal("shelf=2", Versions(again));
    }

    [Fact]
    public void FeaturesMigrateIndependently()
    {
        var db = new TomteDb(DbPath);
        db.Open("shelf", Shelf).Dispose();

        using var connection = db.Open("shortcuts", Shortcuts);
        Assert.Equal("shelf=2,shortcuts=1", Versions(connection));
        Assert.Equal("1", Scalar(connection, "SELECT count(*) FROM shelf_items"));
        Assert.Equal("0", Scalar(connection, "SELECT count(*) FROM shortcuts_tiles"));
    }

    [Fact]
    public void AStepAddedLaterRunsAlone()
    {
        var db = new TomteDb(DbPath);
        db.Open("shelf", Shelf).Dispose();
        db.Open("shortcuts", Shortcuts).Dispose();

        using var connection = db.Open("shelf", [.. Shelf, "ALTER TABLE shelf_items ADD COLUMN pinned INTEGER NOT NULL DEFAULT 0"]);
        Assert.Equal("1", Scalar(connection, "SELECT count(*) FROM shelf_items WHERE pinned = 0"));
        Assert.Equal("shelf=3,shortcuts=1", Versions(connection));
    }

    [Fact]
    public void AFailingStepLeavesNothingBehind()
    {
        var db = new TomteDb(DbPath);
        Assert.Throws<SqliteException>(() => db.Open("shelf", [.. Shelf, "CREATE TABLE shelf_tags(id INTEGER PRIMARY KEY)", "NOT SQL"]));

        using var connection = db.Open("shortcuts", Shortcuts);
        Assert.Equal("shortcuts=1", Versions(connection));
        Assert.Equal("0", Scalar(connection, "SELECT count(*) FROM sqlite_schema WHERE name LIKE 'shelf%'"));
    }

    [Fact]
    public void ADatabaseAheadOfTheCodeIsLeftAlone()
    {
        var db = new TomteDb(DbPath);
        db.Open("shelf", [.. Shelf, "CREATE TABLE shelf_tags(id INTEGER PRIMARY KEY)"]).Dispose();

        using var older = db.Open("shelf", Shelf);
        Assert.Equal("shelf=3", Versions(older));
    }

    [Fact]
    public void OpeningDoesNotWaitForAWriter()
    {
        var db = new TomteDb(DbPath);
        using var writer = db.Open("shelf", Shelf);
        using var uncommitted = writer.BeginTransaction();
        Execute(writer, "INSERT INTO shelf_items(name) VALUES ('not committed')");

        // Needing the write lock here would throw after the busy wait.
        using var reader = db.Open("shelf", Shelf);
        Assert.Equal("seed", Scalar(reader, "SELECT group_concat(name) FROM shelf_items"));
    }

    [Fact]
    public async Task ConnectionsMigratingAtOnceRunEachStepOnce()
    {
        // The file exists already, so nothing but the migration itself holds the eight apart.
        new TomteDb(DbPath).Open("shelf", Shelf).Dispose();

        await AtOnce(8, () => new TomteDb(DbPath).Open("slow", Slow).Dispose());

        using var connection = new TomteDb(DbPath).Open("slow", Slow);
        Assert.Equal("200000", Scalar(connection, "SELECT count(*) FROM slow_rows"));
    }

    [Fact]
    public void ProcessesMigratingAtOnceRunEachStepOnce()
    {
        // Each child is this test assembly run as a program (see SecondProcess); all of them wait for the same signal.
        string signal = "tomte-tests-" + Guid.NewGuid().ToString("N");
        using var go = new EventWaitHandle(false, EventResetMode.ManualReset, signal);
        var children = Enumerable.Range(0, 4).Select(_ => Process.Start(new ProcessStartInfo("dotnet", [typeof(StorageTests).Assembly.Location, DbPath, signal])
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        })!).ToArray();
        try
        {
            Assert.All(children, child => Assert.Equal("ready", child.StandardOutput.ReadLine()));
            go.Set();
            Assert.All(children, child =>
            {
                Assert.True(child.WaitForExit(TimeSpan.FromSeconds(60)), "still running");
                Assert.True(child.ExitCode == 0, child.StandardError.ReadToEnd());
            });
        }
        finally
        {
            foreach (var child in children)
            {
                if (!child.HasExited)
                {
                    child.Kill();
                }

                child.Dispose();
            }
        }

        using var connection = new TomteDb(DbPath).Open("slow", Slow);
        Assert.Equal("200000", Scalar(connection, "SELECT count(*) FROM slow_rows"));
        Assert.Equal("slow=2", Versions(connection));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ADamagedFileIsKeptAsideAndTheDatabaseStartsEmpty(bool wasADatabase)
    {
        var db = new TomteDb(DbPath);
        if (wasADatabase)
        {
            using (var connection = db.Open("shelf", Shelf))
            {
                Execute(connection, "INSERT INTO shelf_items(name) VALUES ('the user''s file')");
            }

            // The schema's own page starts right after the 100-byte file header.
            using var file = new FileStream(DbPath, FileMode.Open, FileAccess.Write);
            file.Position = 100;
            file.Write(Enumerable.Repeat((byte)0xFF, 400).ToArray());
        }
        else
        {
            Directory.CreateDirectory(dir);
            File.WriteAllText(DbPath, Garbage);
        }

        byte[] damaged = File.ReadAllBytes(DbPath);

        using (var connection = db.Open("shelf", Shelf))
        {
            Assert.Equal("seed", Scalar(connection, "SELECT group_concat(name) FROM shelf_items"));
        }

        Assert.Equal(damaged, File.ReadAllBytes(Assert.Single(Directory.GetFiles(dir, "tomte.db.bad-*"))));
    }

    [Fact]
    public async Task ConnectionsMeetingADamagedFileKeepOneCopy()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(DbPath, Garbage);

        await AtOnce(8, () => new TomteDb(DbPath).Open("shelf", Shelf).Dispose());

        Assert.Equal(Garbage, File.ReadAllText(Assert.Single(Directory.GetFiles(dir, "tomte.db.bad-*"))));
        using var connection = new TomteDb(DbPath).Open("shelf", Shelf);
        Assert.Equal("1", Scalar(connection, "SELECT count(*) FROM shelf_items"));
    }

    [Fact]
    public void AnErrorThatIsNotDamageSetsNothingAside()
    {
        // A folder where the file should be: SQLite cannot open it, and that says nothing about the data.
        Directory.CreateDirectory(DbPath);

        Assert.Throws<SqliteException>(() => new TomteDb(DbPath).Open("shelf", Shelf));
        Assert.Equal([DbPath], Directory.GetFileSystemEntries(dir));
    }

    private static Task AtOnce(int count, Action work)
    {
        var start = new Barrier(count);
        return Task.WhenAll(Enumerable.Range(0, count).Select(_ => Task.Factory.StartNew(
            () =>
            {
                start.SignalAndWait();
                work();
            },
            TaskCreationOptions.LongRunning)));
    }

    private static string Versions(SqliteConnection connection) =>
        Scalar(connection, "SELECT group_concat(feature || '=' || version) FROM (SELECT feature, version FROM schema_versions ORDER BY feature)");

    private static string Scalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToString(command.ExecuteScalar(), CultureInfo.InvariantCulture) ?? "";
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}

/// <summary>
/// The test assembly run as a program: `dotnet Tomte.Tests.dll (database path) (signal name)` is one more
/// process that migrates when the signal is set. The test runner never calls this.
/// </summary>
internal static class SecondProcess
{
    private static void Main(string[] args)
    {
        using var go = EventWaitHandle.OpenExisting(args[1]);
        Console.WriteLine("ready");
        go.WaitOne();
        new TomteDb(args[0]).Open("slow", StorageTests.Slow).Dispose();
    }
}
