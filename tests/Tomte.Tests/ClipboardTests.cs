using System.Text;
using Tomte.Core;
using Tomte.Core.Clipboard;
using Tomte.Core.Storage;
using Xunit;

namespace Tomte.Tests;

/// <summary>The reader runs against fake clipboard data; nothing here touches the real clipboard.</summary>
public sealed class ClipboardTests : IDisposable
{
    private const string Secret = "hunter2-correct-horse";
    private const int Max = 65536;
    private static readonly DateTimeOffset Now = new(2026, 10, 14, 12, 0, 0, TimeSpan.Zero);

    private readonly string dir = Path.Combine(Path.GetTempPath(), "tomte-tests-" + Guid.NewGuid().ToString("N"));
    private readonly List<string> log = [];

    private string DbPath => Path.Combine(dir, "tomte.db");

    public void Dispose()
    {
        if (Directory.Exists(dir))
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void PlainTextIsKept()
    {
        var read = Read(new FakeClipboard { Text = "Meet at 10, not 9" });
        Assert.Equal("Meet at 10, not 9", read.Text);
        Assert.Equal("kept 17 characters", read.Outcome);
    }

    [Theory]
    [InlineData(ClipboardReader.ExcludeFormat, null)]
    [InlineData(ClipboardReader.ViewerIgnoreFormat, null)]
    [InlineData(ClipboardReader.HistoryFormat, 0u)]
    [InlineData(ClipboardReader.CloudFormat, 0u)]
    [InlineData(ClipboardReader.HistoryFormat, 7u)]
    public void ContentThatAsksNotToBeMonitoredIsNeverRead(string format, uint? number)
    {
        var clipboard = new FakeClipboard { Text = Secret };
        clipboard.Formats[format] = number;

        var read = Read(clipboard);

        Assert.Null(read.Text);
        Assert.False(clipboard.TextTouched);
        Assert.Contains(format, read.Outcome);
        Assert.DoesNotContain(Secret, read.Outcome);
        Assert.Equal(0, clipboard.OpenNow);
    }

    [Fact]
    public void AnExplicitOneLetsTheItemIn()
    {
        var clipboard = new FakeClipboard { Text = "fine to keep" };
        clipboard.Formats[ClipboardReader.HistoryFormat] = 1u;
        clipboard.Formats[ClipboardReader.CloudFormat] = 1u;

        Assert.Equal("fine to keep", Read(clipboard).Text);
    }

    [Fact]
    public void TomtesOwnWritesAreNotKept()
    {
        var own = new FakeClipboard { Text = Secret, OwnerProcessId = Environment.ProcessId };
        Assert.Equal("skipped: Tomte's own write", Read(own).Outcome);
        Assert.False(own.TextTouched);

        // The selection fallback's Ctrl+C is written by another app, but on Tomte's behalf.
        var copied = new FakeClipboard { Text = Secret };
        OwnClipboardUse.Begin();
        try
        {
            Assert.Equal("skipped: Tomte's own write", Read(copied).Outcome);
        }
        finally
        {
            OwnClipboardUse.End();
        }

        Assert.False(copied.TextTouched);
        Assert.Equal(Secret, Read(copied).Text);
    }

    [Fact]
    public void PausedOpensNothing()
    {
        var clipboard = new FakeClipboard { Text = Secret };
        var read = ClipboardReader.Read(clipboard, paused: true, [], Max);
        Assert.Equal("skipped: paused", read.Outcome);
        Assert.Equal(0, clipboard.Opens);
    }

    [Theory]
    [InlineData(AppRuleMode.Deny, "KeePass.exe", null)]
    [InlineData(AppRuleMode.Deny, null, @"C:\Program Files\KeePass\KeePass.exe")]
    [InlineData(AppRuleMode.DenyWithHotkeys, "keepass", null)]
    public void AnAppWithADenyRuleIsNotWatched(AppRuleMode mode, string? owner, string? foreground)
    {
        var clipboard = new FakeClipboard { Text = Secret, OwnerExe = owner, ForegroundExe = foreground };
        var read = ClipboardReader.Read(clipboard, paused: false, [new AppRule("keepass", mode)], Max);
        Assert.Equal("skipped: an app with a deny rule", read.Outcome);
        Assert.False(clipboard.TextTouched);
    }

    [Fact]
    public void AnAllowRuleChangesNothing()
    {
        var clipboard = new FakeClipboard { Text = "kept", OwnerExe = "keepass.exe" };
        Assert.Equal("kept", ClipboardReader.Read(clipboard, false, [new AppRule("keepass", AppRuleMode.Allow)], Max).Text);
    }

    [Fact]
    public void ABusyClipboardIsTriedForAQuarterSecondAtMost()
    {
        var soon = new FakeClipboard { Text = "after a wait", BusyFor = 3 };
        Assert.Equal("after a wait", Read(soon).Text);
        Assert.Equal(3, soon.Waits.Count);

        var stuck = new FakeClipboard { Text = Secret, BusyFor = int.MaxValue };
        Assert.Equal("skipped: the clipboard stayed busy", Read(stuck).Outcome);
        Assert.Equal(250, stuck.Waits.Sum());
        Assert.False(stuck.TextTouched);
    }

    [Fact]
    public void TooLongOrBlankIsNotKept()
    {
        var huge = new FakeClipboard { Text = new string('x', Max + 10) };
        Assert.Equal($"skipped: {Max + 11} characters, over the {Max} kept", Read(huge).Outcome);
        Assert.False(huge.TextTouched);

        Assert.Equal("skipped: blank text", Read(new FakeClipboard { Text = " \r\n\t" }).Outcome);
        Assert.Equal("skipped: no text", Read(new FakeClipboard()).Outcome);
    }

    [Fact]
    public void HistoryKeepsTheNewestItemsUpToTheCount()
    {
        var history = new ClipboardHistory(new TomteDb(DbPath), new ClipboardRetention(MaxItems: 3));
        for (int i = 1; i <= 5; i++)
        {
            history.Add($"item {i}", Now.AddMinutes(i));
        }

        Assert.Equal(["item 5", "item 4", "item 3"], history.Rows(Now.AddMinutes(6)).Select(r => r.Display));
    }

    [Fact]
    public void HistoryDropsItemsOlderThanTheAge()
    {
        var history = new ClipboardHistory(new TomteDb(DbPath));
        history.Add("old " + Secret, Now.AddDays(-8));
        history.Add("today", Now);

        Assert.Equal(["today"], history.Rows(Now).Select(r => r.Display));
        Assert.False(FileHolds(Secret));
    }

    [Fact]
    public void HistoryRefusesAnItemOverTheSize()
    {
        var history = new ClipboardHistory(new TomteDb(DbPath), new ClipboardRetention(MaxChars: 10));
        history.Add("exactly 10", Now);
        history.Add("eleven char", Now);
        Assert.Equal(["exactly 10"], history.Rows(Now).Select(r => r.Display));
    }

    [Fact]
    public void TheSameTextCopiedAgainMovesUp()
    {
        var history = new ClipboardHistory(new TomteDb(DbPath));
        history.Add("a", Now);
        history.Add("b", Now.AddSeconds(1));
        history.Add("a", Now.AddSeconds(2));
        Assert.Equal(["a", "b"], history.Rows(Now.AddSeconds(3)).Select(r => r.Display));
    }

    [Fact]
    public void ClearedTextIsGoneFromTheDatabaseFile()
    {
        var history = new ClipboardHistory(new TomteDb(DbPath));
        string small = "one " + Secret;
        // Larger than a page, so it also sits in overflow pages.
        string large = string.Concat(Enumerable.Repeat("big-" + Secret + "-", 600));
        history.Add(small, Now);
        history.Add(large, Now.AddSeconds(1));
        history.Add("stays", Now.AddSeconds(2));
        Assert.True(FileHolds(small) && FileHolds("big-" + Secret));

        history.Remove(history.Rows(Now).Single(r => r.Display == small).Id);
        Assert.False(FileHolds(small));
        Assert.True(FileHolds("big-" + Secret));

        history.Clear();
        Assert.False(FileHolds(Secret));
        Assert.Empty(history.Rows(Now));
        // The log is folded back and deleted when the last connection closes, so no copy waits there either.
        Assert.False(File.Exists(DbPath + "-wal"));
    }

    [Fact]
    public void RowsShowOneShortLineAndPickReturnsTheWholeItem()
    {
        var history = new ClipboardHistory(new TomteDb(DbPath));
        string item = "first line\r\n\r\n   second\tline " + new string('z', 500);
        history.Add(item, Now);

        var row = Assert.Single(history.Rows(Now.AddMinutes(9)));
        Assert.StartsWith("first line second line zzz", row.Display);
        Assert.Equal(ClipboardRow.DisplayChars, row.Display.Length);
        Assert.EndsWith("\u2026", row.Display);
        Assert.Equal("9 min", row.Ago(Now.AddMinutes(9)));
        Assert.Equal(item, history.Text(row.Id));
    }

    [Fact]
    public async Task OffMeansNothingIsStartedOrOpened()
    {
        int started = 0;
        var watch = new ClipboardWatch(new ClipboardHistory(new TomteDb(DbPath)), _ =>
        {
            started++;
            return new FakeListener();
        });

        watch.Apply(false);
        watch.Apply(false);
        await Task.Delay(50);

        Assert.Equal(0, started);
        Assert.False(watch.On);
        Assert.False(Directory.Exists(dir));
    }

    [Fact]
    public async Task OnListensOnceAndOffStopsAndClears()
    {
        var listeners = new List<FakeListener>();
        var watch = Watch(listeners);

        watch.Apply(true);
        watch.Apply(true);
        var listener = Assert.Single(listeners);
        listener.Copy(Secret);
        Assert.Equal([Secret], (await watch.RowsAsync(Now)).Select(r => r.Display));

        watch.Apply(false);
        Assert.True(listener.Disposed);
        Assert.Empty(await watch.RowsAsync(Now));
        Assert.False(FileHolds(Secret));
        Assert.DoesNotContain(log, line => line.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public async Task TheSwitchCanBeFlippedFromAnyThreadAtOnce()
    {
        // The shell applies settings changes on the pool thread they arrive on, so flips can overlap.
        var listeners = new List<FakeListener>();
        var watch = new ClipboardWatch(new ClipboardHistory(new TomteDb(DbPath)), captured =>
        {
            var listener = new FakeListener { Captured = captured };
            lock (listeners)
            {
                listeners.Add(listener);
            }

            return listener;
        });

        await Task.WhenAll(Enumerable.Range(0, 200).Select(i => Task.Run(() => watch.Apply(i % 2 == 0))));
        watch.Apply(true);

        Assert.True(watch.On);
        // Every listener but the one now running was stopped; none was started twice over another.
        Assert.Single(listeners, l => !l.Disposed);
        listeners.Single(l => !l.Disposed).Copy("after the flips");
        Assert.Equal(["after the flips"], (await watch.RowsAsync(Now)).Select(r => r.Display));
    }

    [Fact]
    public async Task HistoryLeftBehindWhileOffIsClearedWhenItIsShown()
    {
        // Stored by a run with history on; turned off since while Tomte was not running.
        new ClipboardHistory(new TomteDb(DbPath)).Add(Secret, Now);

        Assert.Empty(await Watch([]).RowsAsync(Now));
        Assert.False(FileHolds(Secret));
    }

    [Fact]
    public async Task ACopyThatLandsAfterOffIsDropped()
    {
        var listeners = new List<FakeListener>();
        var watch = Watch(listeners);
        watch.Apply(true);
        var first = listeners[0];

        watch.Apply(false);
        first.Copy("too late");
        watch.Apply(true);
        first.Copy("from the old listener");

        Assert.Empty(await watch.RowsAsync(Now));
        listeners[1].Copy("from the new one");
        Assert.Equal(["from the new one"], (await watch.RowsAsync(Now)).Select(r => r.Display));
    }

    [Fact]
    public async Task APickPutsTheWholeItemBackThroughTheListener()
    {
        var listeners = new List<FakeListener>();
        var watch = Watch(listeners);
        watch.Apply(true);
        string item = Secret + new string('.', 1000);
        listeners[0].Copy(item);
        var row = Assert.Single(await watch.RowsAsync(Now));

        await watch.PickAsync(row.Id);

        Assert.Equal([item], listeners[0].PutTexts);
        Assert.DoesNotContain(log, line => line.Contains(Secret, StringComparison.Ordinal));
    }

    [Fact]
    public void QuittingKeepsTheHistory()
    {
        var listeners = new List<FakeListener>();
        var watch = Watch(listeners);
        watch.Apply(true);
        listeners[0].Copy("keep me");

        watch.Dispose();

        Assert.True(listeners[0].Disposed);
        Assert.Equal(["keep me"], new ClipboardHistory(new TomteDb(DbPath)).Rows(DateTimeOffset.Now).Select(r => r.Display));
    }

    private static ClipboardRead Read(FakeClipboard clipboard) => ClipboardReader.Read(clipboard, paused: false, [], Max);

    private ClipboardWatch Watch(List<FakeListener> listeners) =>
        new(new ClipboardHistory(new TomteDb(DbPath)), captured =>
        {
            var listener = new FakeListener { Captured = captured };
            listeners.Add(listener);
            return listener;
        }, log.Add);

    private bool FileHolds(string text) =>
        File.ReadAllBytes(DbPath).AsSpan().IndexOf(Encoding.UTF8.GetBytes(text)) >= 0;

    private sealed class FakeListener : IClipboardListener
    {
        public Action<string, DateTimeOffset>? Captured { get; init; }

        public List<string> PutTexts { get; } = [];

        public bool Disposed { get; private set; }

        public void Copy(string text) => Captured!(text, DateTimeOffset.Now);

        public void Put(string text) => PutTexts.Add(text);

        public void Dispose() => Disposed = true;
    }

    /// <summary>Clipboard data for the reader. Fails the test if anything is read while the clipboard is closed.</summary>
    private sealed class FakeClipboard : IClipboardSource
    {
        public Dictionary<string, uint?> Formats { get; } = [];

        public string? Text { get; init; }

        public int BusyFor { get; init; }

        public int OwnerProcessId { get; init; }

        public string? OwnerExe { get; init; }

        public string? ForegroundExe { get; init; }

        public int Opens { get; private set; }

        public int OpenNow { get; private set; }

        public List<int> Waits { get; } = [];

        public bool TextTouched { get; private set; }

        public int TextLength
        {
            get
            {
                Held();
                return Text is null ? -1 : Text.Length + 1;
            }
        }

        public bool TryOpen()
        {
            Opens++;
            if (Opens <= BusyFor)
            {
                return false;
            }

            OpenNow++;
            return true;
        }

        public void Wait(int milliseconds) => Waits.Add(milliseconds);

        public void Close() => OpenNow--;

        public bool Has(string format)
        {
            Held();
            return Formats.ContainsKey(format);
        }

        public uint? Number(string format)
        {
            Held();
            return Formats.TryGetValue(format, out var value) ? value ?? 0 : null;
        }

        public string ReadText()
        {
            Held();
            TextTouched = true;
            return Text!;
        }

        private void Held() => Assert.Equal(1, OpenNow);
    }
}
