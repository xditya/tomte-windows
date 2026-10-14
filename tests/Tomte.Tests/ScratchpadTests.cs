using Tomte.Core;
using Tomte.Core.Storage;
using Xunit;

namespace Tomte.Tests;

public sealed class ScratchpadTests : IDisposable
{
    private static readonly DateTime At = new(2026, 10, 14, 9, 5, 0);

    private readonly string dir = Path.Combine(Path.GetTempPath(), "tomte-tests-" + Guid.NewGuid().ToString("N"));

    private string OldFile => Path.Combine(dir, "scratchpad.md");

    private string DbPath => Path.Combine(dir, "tomte.db");

    private TomteDb Db => new(DbPath);

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public async Task TheNoteSurvivesARestart()
    {
        var note = await Loaded();
        note.Edit("Call the dentist", 4);
        await note.FlushAsync();

        var restarted = await Loaded();
        Assert.Equal("Call the dentist", restarted.Text);
        // After a restart the caret starts at the end, ready for the next line.
        Assert.Equal("Call the dentist".Length, restarted.Caret);
    }

    [Fact]
    public async Task TheEarlierBuildsFileIsCarriedOverOnce()
    {
        Directory.CreateDirectory(dir);
        File.WriteAllText(OldFile, "## 2026-10-13 18:00\n\nbuy milk\n\n");

        var note = await Loaded();
        Assert.Equal("## 2026-10-13 18:00\n\nbuy milk\n\n", note.Text);
        Assert.False(File.Exists(OldFile));
        Assert.True(File.Exists(OldFile + ".imported"));

        // A file that turns up again later is not read: the database has the note now.
        File.WriteAllText(OldFile, "something else");
        Assert.Equal("## 2026-10-13 18:00\n\nbuy milk\n\n", (await Loaded()).Text);
    }

    [Fact]
    public async Task ASecondImportLeavesTheFirstBackupAlone()
    {
        // A database set aside as damaged has no note, so the old file is imported again; the backup from the first
        // import must survive that.
        Directory.CreateDirectory(dir);
        File.WriteAllText(OldFile + ".imported", "the first import's copy");
        File.WriteAllText(OldFile, "found again later");

        var note = await Loaded();

        Assert.Equal("found again later", note.Text);
        Assert.Equal("the first import's copy", File.ReadAllText(OldFile + ".imported"));
        Assert.Equal("found again later", File.ReadAllText(OldFile));
        // And it is not imported a third time from the file that stayed.
        File.WriteAllText(OldFile, "changed by hand");
        Assert.Equal("found again later", (await Loaded()).Text);
    }

    [Fact]
    public void NothingReadsOrWritesBeforeTheFirstLoad()
    {
        var note = new ScratchpadNote(Db, OldFile);
        Assert.Throws<InvalidOperationException>(() => note.Text);
        Assert.Throws<InvalidOperationException>(() => note.Text = "lost");
        Assert.Throws<InvalidOperationException>(() => note.Edit("lost", 0));
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task TypingIsSavedOnceAfterThePause()
    {
        var time = new FakeTime();
        var note = new ScratchpadNote(Db, OldFile, time);
        await note.LoadAsync();

        note.Edit("a", 1);
        note.Edit("ab", 2);
        note.Edit("abc", 3);
        var timer = Assert.Single(time.Timers);
        Assert.Equal(ScratchpadNote.SaveDelay, timer.Due);
        Assert.Equal(2, timer.Changes);
        Assert.Equal("", await Stored());

        timer.Fire();
        await WaitFor(async () => await Stored() == "abc");
        Assert.True(timer.Disposed);
    }

    [Fact]
    public async Task AFlushSavesAtOnce()
    {
        var time = new FakeTime();
        var note = new ScratchpadNote(Db, OldFile, time);
        await note.LoadAsync();
        note.Edit("hide now", 8);

        await note.FlushAsync();

        Assert.Equal("hide now", await Stored());
        Assert.True(Assert.Single(time.Timers).Disposed);
    }

    [Fact]
    public async Task ARamblerNoteLandsWhileThePanelHoldsAnEdit()
    {
        var note = await Loaded();
        note.Edit("typed but not saved", 5);
        int changes = 0;
        note.Changed += () => changes++;

        await note.AppendAsync("spoken", At);

        const string both = "typed but not saved\n\n## 2026-10-14 09:05\n\nspoken\n\n";
        Assert.Equal(both, note.Text);
        Assert.Equal(5, note.Caret);
        Assert.Equal(1, changes);
        Assert.Equal(both, await Stored());
    }

    [Fact]
    public async Task RamblerNotesWaitOutAHeldDatabaseAndAreSavedInOrder()
    {
        var time = new FakeTime();
        var note = new ScratchpadNote(Db, OldFile, time);
        await note.LoadAsync();
        note.Edit("typed", 5);
        await note.FlushAsync();

        using (new FileStream(DbPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            // Neither can be read or written while the file is held; both wait, and one retry is scheduled for them.
            Assert.False(await note.AppendAsync("first", At));
            Assert.False(await note.AppendAsync("second", At.AddMinutes(1)));
            var retry = Assert.Single(time.Timers, t => !t.Disposed);
            Assert.Equal(ScratchpadNote.RetryDelay, retry.Due);

            // A retry while the file is still held fails too, and the next one waits twice as long.
            retry.Fire();
            await WaitFor(() => Task.FromResult(time.Timers.Any(t => !t.Disposed && t.Due == 2 * ScratchpadNote.RetryDelay)));
        }

        time.Timers.Single(t => !t.Disposed && t.Due == 2 * ScratchpadNote.RetryDelay).Fire();
        const string both = "typed\n\n## 2026-10-14 09:05\n\nfirst\n\n## 2026-10-14 09:06\n\nsecond\n\n";
        await WaitFor(async () => await Stored() == both);
        Assert.Equal(both, note.Text);
    }

    [Fact]
    public async Task WhatAnotherProcessAddedIsKeptAfterUnsavedEdits()
    {
        var shell = await Loaded();
        shell.Edit("base", 4);
        await shell.FlushAsync();
        var job = await Loaded();

        shell.Edit("base, typed", 11);
        await job.AppendAsync("job result", At);
        int changes = 0;
        shell.Changed += () => changes++;
        await shell.FlushAsync();

        const string merged = "base, typed\n\n## 2026-10-14 09:05\n\njob result\n\n";
        Assert.Equal(merged, shell.Text);
        Assert.Equal(merged, await Stored());
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task ARewriteByAnotherProcessIsKeptBelowUnsavedEdits()
    {
        var shell = await Loaded();
        shell.Edit("base", 4);
        await shell.FlushAsync();
        var other = await Loaded();
        other.Text = "rewritten elsewhere";
        await other.FlushAsync();

        shell.Edit("base, typed here", 16);
        await shell.FlushAsync();

        Assert.Equal("base, typed here\n\nrewritten elsewhere", shell.Text);
        Assert.Equal(shell.Text, await Stored());
    }

    [Fact]
    public async Task ALoadPicksUpAChangeSavedElsewhere()
    {
        var shell = await Loaded();
        var other = await Loaded();
        other.Text = "from the other process";
        await other.FlushAsync();
        int changes = 0;
        shell.Changed += () => changes++;

        await shell.LoadAsync();

        Assert.Equal("from the other process", shell.Text);
        Assert.Equal(1, changes);
    }

    [Fact]
    public async Task AReplacementMovesTheCaretInsideTheNewText()
    {
        var note = await Loaded();
        note.Edit("hello world", 11);
        int changes = 0;
        note.Changed += () => changes++;

        note.Text = "hi";

        Assert.Equal(2, note.Caret);
        Assert.Equal(1, changes);
    }

    private async Task<ScratchpadNote> Loaded()
    {
        var note = new ScratchpadNote(Db, OldFile);
        await note.LoadAsync();
        return note;
    }

    // What the database holds now, read through an instance of its own as another process would.
    private async Task<string> Stored()
    {
        var reader = new ScratchpadNote(Db);
        await reader.LoadAsync();
        return reader.Text;
    }

    private static async Task WaitFor(Func<Task<bool>> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!await condition())
        {
            Assert.True(DateTime.UtcNow < deadline, "timed out");
            await Task.Delay(10);
        }
    }

    /// <summary>Timers that fire only when the test says so.</summary>
    private sealed class FakeTime : TimeProvider
    {
        private readonly List<FakeTimer> timers = [];

        // A copy: the retry makes its timers on a worker thread while the test looks.
        public List<FakeTimer> Timers
        {
            get
            {
                lock (timers)
                {
                    return [.. timers];
                }
            }
        }

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new FakeTimer(() => callback(state)) { Due = dueTime };
            lock (timers)
            {
                timers.Add(timer);
            }

            return timer;
        }
    }

    private sealed class FakeTimer(Action fire) : ITimer
    {
        public TimeSpan Due { get; set; }

        public int Changes { get; private set; }

        public bool Disposed { get; private set; }

        public void Fire() => fire();

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime;
            Changes++;
            return true;
        }

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
