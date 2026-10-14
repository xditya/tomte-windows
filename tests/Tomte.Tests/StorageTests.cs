using System.Diagnostics;
using System.Globalization;
using System.Reflection;
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

    // TomteDb stops a private timer inside the provider by name; these say what to do when a new version moves it.
    private const string FactoryName = "Microsoft.Data.Sqlite.SqliteConnectionFactory";
    private const string Remedy = "Update TomteDb.StopProviderTimer and these tests, or the provider ticks every 30 s again while the island is hidden.";
    private const BindingFlags AnyField = BindingFlags.Static | BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;

    private static readonly Assembly Provider = typeof(SqliteConnection).Assembly;

    private static readonly string ProviderVersion =
        "Microsoft.Data.Sqlite " + Provider.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

    // AUTOINCREMENT and UNIQUE make SQLite add objects under its own names (sqlite_sequence, an automatic index).
    private static readonly string[] Shelf =
    [
        "CREATE TABLE shelf_items(id INTEGER PRIMARY KEY AUTOINCREMENT, name TEXT NOT NULL UNIQUE)",
        "INSERT INTO shelf_items(name) VALUES ('seed')",
    ];

    private static readonly string[] Shortcuts = ["CREATE TABLE shortcuts_tiles(id INTEGER PRIMARY KEY, target TEXT NOT NULL)"];

    private readonly string dir = Path.Combine(Path.GetTempPath(), "tomte-tests-" + Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(dir, "tomte.db");

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            // One test leaves a read-only file.
            foreach (string file in Directory.GetFiles(dir))
            {
                File.SetAttributes(file, FileAttributes.Normal);
            }

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
            Assert.Equal("1", Scalar(connection, "PRAGMA secure_delete"));
        }
    }

    [Fact]
    public void DeletedTextIsGoneFromTheFile()
    {
        var db = new TomteDb(DbPath);
        using (var connection = db.Open("shelf", Shelf))
        {
            Execute(connection, "INSERT INTO shelf_items(name) VALUES ('copied-from-a-password-manager')");
        }

        Assert.Contains("copied-from-a-password-manager", File.ReadAllText(DbPath));

        using (var connection = db.Open("shelf", Shelf))
        {
            Execute(connection, "DELETE FROM shelf_items WHERE name LIKE 'copied%'");
        }

        Assert.DoesNotContain("copied-from-a-password-manager", File.ReadAllText(DbPath));
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

    [Theory]
    [InlineData("CREATE TABLE items(id INTEGER PRIMARY KEY)")]
    [InlineData("CREATE TABLE shelf_items(id INTEGER PRIMARY KEY); CREATE INDEX items_by_id ON shelf_items(id)")]
    [InlineData("CREATE TABLE shelf_items(id INTEGER PRIMARY KEY); ALTER TABLE shelf_items RENAME TO items")]
    [InlineData("CREATE TABLE shortcuts_extra(id INTEGER PRIMARY KEY)")]
    public void AStepMayOnlyMakeObjectsWithItsFeaturesPrefix(string step)
    {
        var db = new TomteDb(DbPath);
        db.Open("shortcuts", Shortcuts).Dispose();

        var error = Assert.Throws<InvalidOperationException>(() => db.Open("shelf", [step]));
        Assert.Contains("shelf_...", error.Message);

        using var connection = db.Open("shortcuts", Shortcuts);
        Assert.Equal("shortcuts=1", Versions(connection));
        Assert.Equal("schema_versions,shortcuts_tiles", Scalar(connection, "SELECT group_concat(name) FROM (SELECT name FROM sqlite_schema ORDER BY name)"));
    }

    [Theory]
    [InlineData("Shelf")]
    [InlineData("shelf_tags")]
    [InlineData("shelf items")]
    [InlineData("schema")]
    [InlineData("")]
    [InlineData(null)]
    public void AFeatureNameIsLowercaseLettersAndDigits(string? feature)
    {
        Assert.ThrowsAny<ArgumentException>(() => new TomteDb(DbPath).Open(feature!, Shelf));
        Assert.False(Directory.Exists(dir));
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
    public async Task ProcessesMigratingAtOnceRunEachStepOnce()
    {
        // All four wait for the same signal.
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        string signal = "tomte-tests-" + Guid.NewGuid().ToString("N");
        using var go = new EventWaitHandle(false, EventResetMode.ManualReset, signal);
        var children = Enumerable.Range(0, 4).Select(_ => new Child("migrate", DbPath, signal)).ToArray();
        try
        {
            foreach (var child in children)
            {
                Assert.Equal("ready", await child.Line(limit.Token));
            }

            go.Set();
            foreach (var child in children)
            {
                await child.Finish(limit.Token);
            }
        }
        finally
        {
            foreach (var child in children)
            {
                child.Dispose();
            }
        }

        using var connection = new TomteDb(DbPath).Open("slow", Slow);
        Assert.Equal("200000", Scalar(connection, "SELECT count(*) FROM slow_rows"));
        Assert.Equal("slow=2", Versions(connection));
    }

    [Fact]
    public void TheProviderStillHasTheTimerTomteDbLooksFor()
    {
        var factory = Provider.GetType(FactoryName);
        Assert.True(factory is not null, $"{ProviderVersion} has no {FactoryName}. {Remedy}");
        Assert.True(factory.GetField("Instance", AnyField) is { IsStatic: true }, $"{ProviderVersion} has no static {FactoryName}.Instance. {Remedy}");
        Assert.True(
            factory.GetField("_pruneTimer", AnyField) is { IsStatic: false } field && field.FieldType == typeof(Timer),
            $"{ProviderVersion} has no System.Threading.Timer field {FactoryName}._pruneTimer. {Remedy}");
    }

    [Fact]
    public void TheProviderHasNoTimerTomteDbDoesNotKnowAbout()
    {
        // The pool's own timer only exists for a pooled connection, and TomteDb never makes one.
        string[] known = [FactoryName + "._pruneTimer", "Microsoft.Data.Sqlite.SqliteConnectionPool._pruneTimer"];
        var found = Provider.GetTypes()
            .SelectMany(type => type.GetFields(AnyField | BindingFlags.DeclaredOnly))
            .Where(field => field.FieldType.FullName?.Contains("Timer", StringComparison.Ordinal) == true)
            .Select(field => $"{field.DeclaringType!.FullName}.{field.Name}");

        Assert.True(known.Order().SequenceEqual(found.Order()), $"{ProviderVersion} keeps timers in: {string.Join(", ", found)}. {Remedy}");
    }

    [Fact]
    public void OpeningStopsTheProvidersTimer()
    {
        new TomteDb(DbPath).Open("shelf", Shelf).Dispose();

        var factory = Provider.GetType(FactoryName)!;
        var timer = (Timer)factory.GetField("_pruneTimer", AnyField)!.GetValue(factory.GetField("Instance", AnyField)!.GetValue(null))!;
        // A live timer accepts a change and answers true. A disposed one answers false on .NET 10; the documentation says
        // it throws, so that counts as stopped too.
        bool alive;
        try
        {
            alive = timer.Change(Timeout.Infinite, Timeout.Infinite);
        }
        catch (ObjectDisposedException)
        {
            alive = false;
        }

        Assert.False(alive, $"{ProviderVersion}: the timer is still alive after an open. {Remedy}");
    }

    [Fact]
    public async Task UsingTheDatabaseLeavesNoTimerBehind()
    {
        // Counted in a process of its own: this one runs other tests, and their timers come and go.
        using var limit = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        using var child = new Child("timers", DbPath);
        string[] counts = (await child.Line(limit.Token)).Split(' ');
        await child.Finish(limit.Token);

        Assert.Equal(3, counts.Length);
        Assert.True(counts.Distinct().Count() == 1, $"Active timers before the first open, after one use and after two: {string.Join(", ", counts)}");
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
    public void ALogSqliteLeftBehindGoesWithTheDamagedFile()
    {
        // SQLite folds a log into its file, or deletes it, when the failing connection closes. Read-only is the one
        // cheap way to make it leave the log where it is.
        Directory.CreateDirectory(dir);
        File.WriteAllText(DbPath, Garbage);
        File.WriteAllText(DbPath + "-wal", "commits that were never folded back");
        File.SetAttributes(DbPath + "-wal", FileAttributes.ReadOnly);

        new TomteDb(DbPath).Open("shelf", Shelf).Dispose();

        string bad = Assert.Single(Directory.GetFiles(dir, "tomte.db.bad-*"), file => !file.EndsWith("-wal", StringComparison.Ordinal));
        Assert.Equal("commits that were never folded back", File.ReadAllText(bad + "-wal"));
        Assert.Equal(Garbage, File.ReadAllText(bad));
        Assert.False(File.Exists(DbPath + "-wal"));
    }

    [Fact]
    public void ALogIsNeverSetAsideWithoutItsFile()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(DbPath, Garbage);
        File.WriteAllText(DbPath + "-wal", "commits that were never folded back");
        File.SetAttributes(DbPath + "-wal", FileAttributes.ReadOnly);

        // SQLite can still read the file, but nothing can rename it while this handle is open.
        using (new FileStream(DbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
        {
            Assert.Throws<IOException>(() => new TomteDb(DbPath).Open("shelf", Shelf));
        }

        Assert.Empty(Directory.GetFiles(dir, "*.bad-*"));
        Assert.Equal(Garbage, File.ReadAllText(DbPath));
        Assert.Equal("commits that were never folded back", File.ReadAllText(DbPath + "-wal"));
    }

    [Fact]
    public void DamageInOneFeaturesIndexSetsNothingAside()
    {
        var db = new TomteDb(DbPath);
        db.Open("shortcuts", Shortcuts).Dispose();

        // A full-text index whose own storage was emptied: its self-check reports "malformed" with a longer code (267),
        // not the plain 11 that means the file itself is damaged.
        string[] search =
        [
            "CREATE VIRTUAL TABLE shelf_search USING fts5(body)",
            "INSERT INTO shelf_search(body) VALUES ('one two three')",
            "DELETE FROM shelf_search_data",
            "INSERT INTO shelf_search(shelf_search) VALUES ('integrity-check')",
        ];
        var error = Assert.Throws<SqliteException>(() => db.Open("shelf", search));
        Assert.Equal((11, 267), (error.SqliteErrorCode, error.SqliteExtendedErrorCode));

        Assert.Equal(["tomte.db"], Directory.GetFiles(dir).Select(Path.GetFileName));
        using var connection = db.Open("shortcuts", Shortcuts);
        Assert.Equal("shortcuts=1", Versions(connection));
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

    [Fact]
    public void OnlyTheThreeNewestSetAsideCopiesAreKeptAndNothingElseIsTouched()
    {
        Directory.CreateDirectory(dir);
        foreach (string stamp in new[] { "20200101-000000-000", "20200201-000000-000", "20200301-000000-000" })
        {
            File.WriteAllText($"{DbPath}.bad-{stamp}", stamp);
        }

        File.WriteAllText($"{DbPath}.bad-20200101-000000-000-wal", "the oldest copy's log");
        // These look like copies but are not ones Tomte made: the user's own name, another database, more after the
        // stamp, and a folder below the data folder.
        string below = Path.Combine(dir, "below");
        Directory.CreateDirectory(below);
        string[] others =
        [
            $"{DbPath}.bad-mine",
            Path.Combine(dir, "other.db.bad-20190101-000000-000"),
            $"{DbPath}.bad-20190101-000000-000.txt",
            Path.Combine(below, "tomte.db.bad-20190101-000000-000"),
        ];
        foreach (string file in others)
        {
            File.WriteAllText(file, "keep");
        }

        File.WriteAllText(DbPath, Garbage);

        new TomteDb(DbPath).Open("shelf", Shelf).Dispose();

        string[] kept = Directory.GetFiles(dir, "tomte.db.bad-*").Except(others).ToArray();
        Assert.Equal(3, kept.Length);
        Assert.Contains($"{DbPath}.bad-20200301-000000-000", kept);
        Assert.Contains($"{DbPath}.bad-20200201-000000-000", kept);
        Assert.Equal(Garbage, File.ReadAllText(Assert.Single(kept, file => !file.Contains(".bad-2020", StringComparison.Ordinal))));
        Assert.All(others, file => Assert.Equal("keep", File.ReadAllText(file)));
    }

    [Fact]
    public void TheCopyJustSetAsideStaysWhenOlderCopiesCarryALaterStamp()
    {
        // A clock that was set back, or a move to a time zone further west.
        Directory.CreateDirectory(dir);
        string[] later = ["20990101-000000-000", "20990201-000000-000", "20990301-000000-000"];
        foreach (string stamp in later)
        {
            File.WriteAllText($"{DbPath}.bad-{stamp}", stamp);
        }

        File.WriteAllText(DbPath, Garbage);

        new TomteDb(DbPath).Open("shelf", Shelf).Dispose();

        string[] kept = Directory.GetFiles(dir, "tomte.db.bad-*");
        Assert.Equal(3, kept.Length);
        Assert.Equal(Garbage, File.ReadAllText(Assert.Single(kept, file => !file.Contains(".bad-2099", StringComparison.Ordinal))));
        Assert.False(File.Exists($"{DbPath}.bad-{later[0]}"));
    }

    [Fact]
    public async Task ASetAsideIsToldOnceToWhicheverProcessAsksFirst()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(DbPath, Garbage);
        var db = new TomteDb(DbPath);
        var raised = new List<string>();
        Action<string> onSetAside = path =>
        {
            lock (raised)
            {
                raised.Add(path);
            }
        };
        TomteDb.FileSetAside += onSetAside;
        try
        {
            Assert.False(db.TakeSetAsideNotice());
            db.Open("shelf", Shelf).Dispose();
            // The file is healthy now: nothing more is set aside or told.
            db.Open("shelf", Shelf).Dispose();

            // The settings window and the shell each have their own object for the same file, and two asking at
            // the same moment still make one claim.
            int told = 0;
            await AtOnce(8, () =>
            {
                if (new TomteDb(DbPath).TakeSetAsideNotice())
                {
                    Interlocked.Increment(ref told);
                }
            });
            Assert.Equal(1, told);
            Assert.False(db.TakeSetAsideNotice());
            lock (raised)
            {
                Assert.Equal([db.FilePath], raised.Where(path => path == db.FilePath));
            }
        }
        finally
        {
            TomteDb.FileSetAside -= onSetAside;
        }
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

    /// <summary>This test assembly run as a program (see SecondProcess). Killed on dispose if it is still running.</summary>
    private sealed class Child : IDisposable
    {
        private readonly Process process;
        private readonly Task<string> errors;

        public Child(params string[] arguments)
        {
            process = Process.Start(new ProcessStartInfo("dotnet", [typeof(StorageTests).Assembly.Location, .. arguments])
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
            })!;
            // Read while it runs, so a child that fails loudly cannot block on a full pipe.
            errors = process.StandardError.ReadToEndAsync();
        }

        public async Task<string> Line(CancellationToken limit) =>
            await process.StandardOutput.ReadLineAsync(limit) ?? throw new InvalidOperationException("The child exited: " + await errors);

        public async Task Finish(CancellationToken limit)
        {
            await process.WaitForExitAsync(limit);
            Assert.True(process.ExitCode == 0, await errors);
        }

        public void Dispose()
        {
            if (!process.HasExited)
            {
                process.Kill();
            }

            process.Dispose();
        }
    }
}

/// <summary>
/// The test assembly run as a program, `dotnet Tomte.Tests.dll (what to do) (database path) ...`, for the
/// tests that need a second process. The test runner never calls this.
/// </summary>
internal static class SecondProcess
{
    private static int Main(string[] args)
    {
        switch (args[0])
        {
            case "migrate":
                using (var go = EventWaitHandle.OpenExisting(args[2]))
                {
                    Console.WriteLine("ready");
                    // Bounded, so a child does not outlive a test host that died.
                    if (!go.WaitOne(TimeSpan.FromSeconds(60)))
                    {
                        return 1;
                    }
                }

                new TomteDb(args[1]).Open("slow", StorageTests.Slow).Dispose();
                return 0;
            case "timers":
                // Active timers before the first open, after one use and after two, on one line.
                var db = new TomteDb(args[1]);
                long before = Timer.ActiveCount;
                db.Open("slow", StorageTests.Slow).Dispose();
                long afterOne = Timer.ActiveCount;
                db.Open("slow", StorageTests.Slow).Dispose();
                Console.WriteLine($"{before} {afterOne} {Timer.ActiveCount}");
                return 0;
            default:
                return 2;
        }
    }
}
