using System.Buffers.Binary;
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
    private static readonly ClipboardRetention Limits = new();
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

        // The app's copy can land after the fallback has given up on it: for a second after the end it is still Tomte's.
        Assert.Equal("skipped: Tomte's own write", Read(copied).Outcome);
        var lapsed = DateTime.UtcNow + OwnClipboardUse.Grace + TimeSpan.FromSeconds(2);
        while (OwnClipboardUse.Active)
        {
            Assert.True(DateTime.UtcNow < lapsed, "the grace never ended");
            Thread.Sleep(20);
        }

        Assert.Equal(Secret, Read(copied).Text);
    }

    [Fact]
    public void PausedOpensNothing()
    {
        var clipboard = new FakeClipboard { Text = Secret };
        var read = ClipboardReader.Read(clipboard, paused: true, [], Limits);
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
        var read = ClipboardReader.Read(clipboard, paused: false, [new AppRule("keepass", mode)], Limits);
        Assert.Equal("skipped: an app with a deny rule", read.Outcome);
        Assert.False(clipboard.TextTouched);
    }

    [Fact]
    public void AnAllowRuleChangesNothing()
    {
        var clipboard = new FakeClipboard { Text = "kept", OwnerExe = "keepass.exe" };
        Assert.Equal("kept", ClipboardReader.Read(clipboard, false, [new AppRule("keepass", AppRuleMode.Allow)], Limits).Text);
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
        Assert.Equal("skipped: no text or image", Read(new FakeClipboard()).Outcome);
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

        Assert.True(await watch.PickAsync(row.Id));

        Assert.Equal([item], listeners[0].PutTexts);
        Assert.DoesNotContain(log, line => line.Contains(Secret, StringComparison.Ordinal));

        // An item that is gone puts nothing back, and says so, so the panel shows no "Copied".
        Assert.False(await watch.PickAsync(row.Id + 1000));
        Assert.Single(listeners[0].PutTexts);
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

    [Fact]
    public void AnImageIsReadOnlyWhenThereIsNoText()
    {
        var image = new FakeClipboard { ImageBytes = [1, 2, 3], ImageIsPng = true };
        var read = Read(image);
        Assert.Null(read.Text);
        Assert.Equal([1, 2, 3], read.Image!.ToArray());
        Assert.True(read.Image.Png);
        Assert.Equal("kept an image of 3 bytes (PNG)", read.Outcome);
        Assert.Equal(0, image.OpenNow);

        // Text wins: a spreadsheet's cells come with a picture of them.
        var both = new FakeClipboard { Text = "A1\tB1", ImageBytes = [1, 2, 3] };
        Assert.Equal("A1\tB1", Read(both).Text);
        Assert.False(both.ImageTouched);
    }

    [Fact]
    public void AnImageOverTheSizeIsNeverRead()
    {
        var limits = new ClipboardRetention(MaxImageReadBytes: 100);
        var huge = new FakeClipboard { ImageBytes = new byte[101] };
        Assert.Equal("skipped: an image of 101 bytes, over the 100 kept", ClipboardReader.Read(huge, false, [], limits).Outcome);
        Assert.False(huge.ImageTouched);
        Assert.NotNull(ClipboardReader.Read(new FakeClipboard { ImageBytes = new byte[100] }, false, [], limits).Image);

        // A full 5K screen as a 32-bit bitmap is read; what is kept is a PNG of at most 20 MB.
        Assert.True(Limits.MaxImageReadBytes >= 40 + 5120 * 2880 * 4);
        Assert.Equal(20 << 20, Limits.MaxImageBytes);
    }

    [Fact]
    public void ImagesFollowTheSameRules()
    {
        FakeClipboard Copied(string? format = null, uint? number = null, int owner = 0, string? foreground = null)
        {
            var clipboard = new FakeClipboard { ImageBytes = [1, 2, 3], ImageIsPng = true, OwnerProcessId = owner, ForegroundExe = foreground };
            if (format is not null)
            {
                clipboard.Formats[format] = number;
            }

            return clipboard;
        }

        FakeClipboard[] refused =
        [
            Copied(ClipboardReader.ExcludeFormat),
            Copied(ClipboardReader.ViewerIgnoreFormat),
            Copied(ClipboardReader.HistoryFormat, 0),
            Copied(ClipboardReader.CloudFormat, 0),
            Copied(owner: Environment.ProcessId),
        ];
        foreach (var clipboard in refused)
        {
            Assert.Null(Read(clipboard).Image);
            Assert.False(clipboard.ImageTouched);
        }

        var denied = Copied(foreground: @"C:\Program Files\KeePass\KeePass.exe");
        Assert.Equal("skipped: an app with a deny rule", ClipboardReader.Read(denied, false, [new AppRule("keepass", AppRuleMode.Deny)], Limits).Outcome);
        Assert.False(denied.ImageTouched);

        var paused = Copied();
        Assert.Equal("skipped: paused", ClipboardReader.Read(paused, paused: true, [], Limits).Outcome);
        Assert.Equal(0, paused.Opens);
    }

    [Theory]
    [InlineData(24)]
    [InlineData(32)]
    public void ACopiedBitmapIsKeptAsAPngAndPutBackPixelForPixel(int bits)
    {
        var kept = ClipboardImages.Keep(new CopiedImage(Dib(5, 3, bits, Colour), png: false), Limits.MaxImageReadBytes)!;
        Assert.Equal((5, 3), (kept.Width, kept.Height));
        Assert.Equal("\u0089PNG", Encoding.Latin1.GetString(kept.Png, 0, 4));

        // A plain header, a positive height (bottom row first), 32 bits a pixel, no compression.
        var dib = ClipboardImages.Dib(kept.Png);
        Assert.Equal(40 + 5 * 3 * 4, dib.Length);
        int Field(int at) => BinaryPrimitives.ReadInt32LittleEndian(dib.AsSpan(at));
        Assert.Equal((40, 5, 3, 32, 0), (Field(0), Field(4), Field(8), (int)BinaryPrimitives.ReadInt16LittleEndian(dib.AsSpan(14)), Field(16)));
        for (int y = 0; y < 3; y++)
        {
            for (int x = 0; x < 5; x++)
            {
                Assert.Equal((Colour(x, y), (byte)255), DibPixel(dib, 5, 3, x, y));
            }
        }

        // A PNG that came as one is kept as it came, less what the clipboard's memory held after its end.
        Assert.Equal(kept.Png, ClipboardImages.Keep(new CopiedImage(kept.Png, png: true), Limits.MaxImageReadBytes)!.Png);
        Assert.Equal(kept.Png, ClipboardImages.Keep(new CopiedImage([.. kept.Png, 7, 7, 7], png: true), Limits.MaxImageReadBytes)!.Png);
    }

    [Fact]
    public void TheThumbnailIsCutFromTheMiddleAndDrawnAtTheSlotsSize()
    {
        // Green at both ends, red then blue between: a centred 3:2 cut shows red on its left and blue on its right.
        static (byte, byte, byte) Bands(int x, int y) => x < 50 || x >= 350 ? ((byte)0, (byte)255, (byte)0) : x < 200 ? ((byte)0, (byte)0, (byte)255) : ((byte)255, (byte)0, (byte)0);
        var kept = ClipboardImages.Keep(new CopiedImage(Dib(400, 100, 24, Bands), png: false), Limits.MaxImageReadBytes)!;
        Assert.Equal(ClipboardImages.ThumbWidth * ClipboardImages.ThumbHeight * 4, ClipboardImages.Thumbnail(kept.Thumb, ClipboardImages.ThumbWidth, ClipboardImages.ThumbHeight).Length);

        foreach (double scale in (double[])[1, 1.25, 1.5])
        {
            int width = (int)Math.Round(ClipboardPanelLayout.ThumbWidth * scale), height = (int)Math.Round(ClipboardPanelLayout.ThumbHeight * scale);
            var pixels = ClipboardImages.Thumbnail(kept.Thumb, width, height);
            Assert.Equal(width * height * 4, pixels.Length);
            int middle = height / 2 * width * 4;
            Assert.Equal([0, 0, 255, 255], pixels[middle..(middle + 4)]);
            Assert.Equal([255, 0, 0, 255], pixels[(middle + (width - 1) * 4)..(middle + width * 4)]);
        }
    }

    [Fact]
    public void AnImageWhoseBitmapIsOverTheSizeOrThatDoesNotDecodeIsNotKept()
    {
        var copied = new CopiedImage(Dib(64, 64, 24, Colour), png: false);
        Assert.Null(ClipboardImages.Keep(copied, 64 * 64 * 4 - 1));
        Assert.NotNull(ClipboardImages.Keep(copied, 64 * 64 * 4));

        Assert.ThrowsAny<Exception>(() => ClipboardImages.Keep(new CopiedImage([1, 2, 3], png: true), Limits.MaxImageReadBytes));
        Assert.Throws<ArgumentException>(() => ClipboardImages.BmpHeader([40, 0, 0]));

        // Only the codec the format names reads it: a bitmap file put in the PNG format is not taken as one.
        var bitmap = Dib(4, 4, 24, Colour);
        Assert.ThrowsAny<Exception>(() => ClipboardImages.Keep(new CopiedImage([.. ClipboardImages.BmpHeader(bitmap), .. bitmap], png: true), Limits.MaxImageReadBytes));
    }

    [Fact]
    public void AHeaderClaimingAHugeBitmapIsNotKept()
    {
        // 0x7FFFFFFF squared times 4 does not fit a long; the size must still count as over.
        var header = new byte[40];
        BinaryPrimitives.WriteInt32LittleEndian(header, 40);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(4), 0x7FFFFFFF);
        BinaryPrimitives.WriteInt32LittleEndian(header.AsSpan(8), 0x7FFFFFFF);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(12), 1);
        BinaryPrimitives.WriteInt16LittleEndian(header.AsSpan(14), 24);

        // Windows' bitmap codec refuses this header itself; either way nothing is kept and memory is not the reason.
        KeptImage? kept = null;
        var thrown = Record.Exception(() => kept = ClipboardImages.Keep(new CopiedImage(header, png: false), Limits.MaxImageReadBytes));
        Assert.Null(kept);
        Assert.False(thrown is OutOfMemoryException);

        // The size check itself, at that claim and at the edge.
        Assert.False(ClipboardImages.BitmapFits(0x7FFFFFFF, 0x7FFFFFFF, Limits.MaxImageReadBytes));
        Assert.False(ClipboardImages.BitmapFits(uint.MaxValue, uint.MaxValue, long.MaxValue));
        Assert.True(ClipboardImages.BitmapFits(5120, 2880, Limits.MaxImageReadBytes));
        Assert.True(ClipboardImages.BitmapFits(4, 4, 64));
        Assert.False(ClipboardImages.BitmapFits(4, 4, 63));
    }

    [Fact]
    public void TheBmpHeaderSaysWhereThePixelsStart()
    {
        static int Offset(byte[] dib) => BinaryPrimitives.ReadInt32LittleEndian(ClipboardImages.BmpHeader(dib).AsSpan(10));

        // After the 14-byte file header and the 40-byte one: BI_BITFIELDS's three masks, an 8-bit bitmap's colours.
        Assert.Equal(14 + 40 + 12, Offset(Dib(2, 2, 32, Colour)));
        Assert.Equal(14 + 40, Offset(Dib(2, 2, 24, Colour)));
        var eight = new byte[40 + 256 * 4 + 8];
        BinaryPrimitives.WriteInt32LittleEndian(eight, 40);
        BinaryPrimitives.WriteInt16LittleEndian(eight.AsSpan(14), 8);
        Assert.Equal(14 + 40 + 256 * 4, Offset(eight));
        BinaryPrimitives.WriteInt32LittleEndian(eight.AsSpan(32), 2);
        Assert.Equal(14 + 40 + 2 * 4, Offset(eight));
    }

    [Fact]
    public void HistoryKeepsTheNewestImagesUpToTheirOwnCount()
    {
        var history = new ClipboardHistory(new TomteDb(DbPath), new ClipboardRetention(MaxItems: 2, MaxImages: 2));
        for (int i = 1; i <= 3; i++)
        {
            history.Add($"text {i}", Now.AddMinutes(i));
            history.AddImage(new KeptImage(Encoding.UTF8.GetBytes($"png {i} {Secret}"), 10 * i, 5, [(byte)i]), Now.AddMinutes(i).AddSeconds(30));
        }

        var rows = history.Rows(Now.AddMinutes(4));
        Assert.Equal(["Image · 30 x 5", "text 3", "Image · 20 x 5", "text 2"], rows.Select(r => r.Display));
        Assert.True(rows[0].IsImage);
        Assert.False(rows[1].IsImage);
        Assert.Equal([3], rows[0].Thumb!);
        Assert.Equal($"png 3 {Secret}", Encoding.UTF8.GetString(history.Image(rows[0].Id)!));
        Assert.Null(history.Image(rows[1].Id));
        Assert.False(FileHolds($"png 1 {Secret}"));
    }

    [Fact]
    public void TextKeptBeforeImagesStaysWhenTheTableGrowsTheirColumns()
    {
        // A database from a build that kept text only: the first schema step alone.
        using (var connection = new TomteDb(DbPath).Open("clipboard", ["CREATE TABLE clipboard_entries(id INTEGER PRIMARY KEY, text TEXT NOT NULL, copied_at INTEGER NOT NULL)"]))
        using (var insert = connection.CreateCommand())
        {
            insert.CommandText = $"INSERT INTO clipboard_entries(text, copied_at) VALUES ('from before', {Now.ToUnixTimeMilliseconds()})";
            insert.ExecuteNonQuery();
        }

        var history = new ClipboardHistory(new TomteDb(DbPath));
        history.AddImage(new KeptImage([1], 4, 3, [2]), Now.AddSeconds(1));
        Assert.Equal(["Image · 4 x 3", "from before"], history.Rows(Now.AddSeconds(2)).Select(r => r.Display));
    }

    [Fact]
    public void TheSameImageCopiedAgainMovesUp()
    {
        var history = new ClipboardHistory(new TomteDb(DbPath));
        history.AddImage(new KeptImage([1, 1], 1, 1, [9]), Now);
        history.AddImage(new KeptImage([2, 2], 2, 2, [9]), Now.AddSeconds(1));
        long first = history.Rows(Now).Single(r => r.Display == "Image · 1 x 1").Id;

        history.AddImage(new KeptImage([1, 1], 1, 1, [9]), Now.AddSeconds(2));

        var rows = history.Rows(Now.AddSeconds(3));
        Assert.Equal(["Image · 1 x 1", "Image · 2 x 2"], rows.Select(r => r.Display));
        Assert.Equal(first, rows[0].Id);
    }

    [Fact]
    public async Task CopiesThatLandWhileTheWorkerIsBusyAreDecodedOnceTheLatestWins()
    {
        var listeners = new List<FakeListener>();
        var watch = Watch(listeners);
        watch.Apply(true);
        Assert.Empty(await watch.RowsAsync(Now));
        var replaced = new CopiedImage(Dib(5, 3, 24, Colour), png: false);
        var stored = new CopiedImage(Dib(7, 2, 24, Colour), png: false);

        // Another connection holds the write lock, so the text copy waits on the worker and the images queue behind it.
        using (var holder = new TomteDb(DbPath).Open("holder", []))
        {
            using (var begin = holder.CreateCommand())
            {
                begin.CommandText = "BEGIN EXCLUSIVE";
                begin.ExecuteNonQuery();
            }

            listeners[0].Copy("first");
            listeners[0].Copy(replaced);
            listeners[0].Copy(stored);
            // The copy that was replaced is freed at once, while the worker is still held.
            Assert.Throws<ObjectDisposedException>(() => replaced.ToArray());
        }

        Assert.Equal(["Image · 7 x 2", "first"], (await watch.RowsAsync(Now.AddMinutes(1))).Select(r => r.Display));
        Assert.Single(log, line => line.StartsWith("clipboard: store an image took", StringComparison.Ordinal));
        // And the stored one once its PNG was made.
        Assert.Throws<ObjectDisposedException>(() => stored.ToArray());
    }

    [Fact]
    public async Task TheSameBitmapCopiedTwiceIsOneRow()
    {
        var listeners = new List<FakeListener>();
        var watch = Watch(listeners);
        watch.Apply(true);
        listeners[0].Copy(new CopiedImage(Dib(5, 3, 24, Colour), png: false));
        await watch.RowsAsync(Now);
        listeners[0].Copy(new CopiedImage(Dib(5, 3, 24, Colour), png: false));
        Assert.Single(await watch.RowsAsync(Now));
        Assert.Equal(2, log.Count(line => line.StartsWith("clipboard: store an image took", StringComparison.Ordinal)));
    }

    [Fact]
    public async Task ACopiedImageWhosePngIsOverTheSizeIsNotKept()
    {
        var listeners = new List<FakeListener>();
        var watch = Watch(listeners, new ClipboardRetention(MaxImageBytes: 50));
        watch.Apply(true);
        listeners[0].Copy(new CopiedImage(Dib(64, 64, 24, (x, y) => ((byte)(x * 7), (byte)(y * 13), (byte)(x * y))), png: false));

        Assert.Empty(await watch.RowsAsync(Now));
        Assert.Contains(log, line => line.StartsWith("clipboard: skipped: an image of", StringComparison.Ordinal) && line.EndsWith("as a PNG, over the 50 kept", StringComparison.Ordinal));
    }

    [Fact]
    public void HistoryRefusesAnImageOverTheSize()
    {
        var history = new ClipboardHistory(new TomteDb(DbPath), new ClipboardRetention(MaxImageBytes: 8));
        history.AddImage(new KeptImage(Encoding.UTF8.GetBytes("12345678"), 1, 1, []), Now);
        history.AddImage(new KeptImage(Encoding.UTF8.GetBytes("123456789"), 2, 2, []), Now);
        Assert.Equal(["Image · 1 x 1"], history.Rows(Now).Select(r => r.Display));
    }

    [Fact]
    public async Task ACopiedImageIsStoredOffTheListenerAndOffDeletesIt()
    {
        var listeners = new List<FakeListener>();
        var watch = Watch(listeners);
        watch.Apply(true);
        listeners[0].Copy(new CopiedImage(Dib(5, 3, 24, Colour), png: false));
        await watch.RowsAsync(Now);
        listeners[0].Copy(new CopiedImage([1, 2, 3], png: true));

        var row = Assert.Single(await watch.RowsAsync(Now));
        Assert.Equal("Image · 5 x 3", row.Display);
        Assert.Contains(log, line => line.StartsWith("clipboard: an image could not be read", StringComparison.Ordinal));
        var png = new ClipboardHistory(new TomteDb(DbPath)).Image(row.Id)!;
        Assert.True(FileHolds(png) && FileHolds(row.Thumb!));

        watch.Apply(false);
        Assert.Empty(await watch.RowsAsync(Now));
        Assert.False(FileHolds(png) || FileHolds(row.Thumb!));
        Assert.False(File.Exists(DbPath + "-wal"));
    }

    [Fact]
    public async Task APickPutsTheImageBackAsAPngAndABitmap()
    {
        var listeners = new List<FakeListener>();
        var watch = Watch(listeners);
        watch.Apply(true);
        listeners[0].Copy(new CopiedImage(Dib(5, 3, 24, Colour), png: false));
        var row = Assert.Single(await watch.RowsAsync(Now));

        await watch.PickAsync(row.Id);

        var (png, dib) = Assert.Single(listeners[0].PutImages);
        Assert.Empty(listeners[0].PutTexts);
        Assert.Equal(new ClipboardHistory(new TomteDb(DbPath)).Image(row.Id), png);
        Assert.Equal((Colour(4, 2), (byte)255), DibPixel(dib, 5, 3, 4, 2));
        Assert.Contains($"clipboard: picked an image of {png.Length} bytes", log);
    }

    private static (byte B, byte G, byte R) Colour(int x, int y) => ((byte)(x * 50), (byte)(y * 100), (byte)(200 - x * 30));

    /// <summary>
    /// A packed bitmap as apps put it on the clipboard, bottom row first: 24 bits a pixel with rows padded to four
    /// bytes, or 32 with BI_BITFIELDS and its three masks after the header.
    /// </summary>
    private static byte[] Dib(int width, int height, int bits, Func<int, int, (byte B, byte G, byte R)> colour)
    {
        int stride = (width * bits / 8 + 3) & ~3, masks = bits == 32 ? 12 : 0;
        var dib = new byte[40 + masks + stride * height];
        var header = dib.AsSpan();
        BinaryPrimitives.WriteInt32LittleEndian(header, 40);
        BinaryPrimitives.WriteInt32LittleEndian(header[4..], width);
        BinaryPrimitives.WriteInt32LittleEndian(header[8..], height);
        BinaryPrimitives.WriteInt16LittleEndian(header[12..], 1);
        BinaryPrimitives.WriteInt16LittleEndian(header[14..], (short)bits);
        if (bits == 32)
        {
            BinaryPrimitives.WriteInt32LittleEndian(header[16..], 3);
            BinaryPrimitives.WriteUInt32LittleEndian(header[40..], 0x00FF0000);
            BinaryPrimitives.WriteUInt32LittleEndian(header[44..], 0x0000FF00);
            BinaryPrimitives.WriteUInt32LittleEndian(header[48..], 0x000000FF);
        }

        for (int y = 0; y < height; y++)
        {
            for (int x = 0; x < width; x++)
            {
                int at = 40 + masks + (height - 1 - y) * stride + x * bits / 8;
                (dib[at], dib[at + 1], dib[at + 2]) = colour(x, y);
            }
        }

        return dib;
    }

    // A pixel of a 32-bit bottom-up bitmap with a plain header, counted from the top row.
    private static ((byte B, byte G, byte R), byte A) DibPixel(byte[] dib, int width, int height, int x, int y)
    {
        int at = 40 + ((height - 1 - y) * width + x) * 4;
        return ((dib[at], dib[at + 1], dib[at + 2]), dib[at + 3]);
    }

    private static ClipboardRead Read(FakeClipboard clipboard) => ClipboardReader.Read(clipboard, paused: false, [], Limits);

    private ClipboardWatch Watch(List<FakeListener> listeners, ClipboardRetention? retention = null) =>
        new(new ClipboardHistory(new TomteDb(DbPath), retention), captured =>
        {
            var listener = new FakeListener { Captured = captured };
            listeners.Add(listener);
            return listener;
        }, log.Add);

    private bool FileHolds(string text) => FileHolds(Encoding.UTF8.GetBytes(text));

    private bool FileHolds(byte[] bytes) => File.ReadAllBytes(DbPath).AsSpan().IndexOf(bytes) >= 0;

    private sealed class FakeListener : IClipboardListener
    {
        public Action<ClipboardRead, DateTimeOffset>? Captured { get; init; }

        public List<string> PutTexts { get; } = [];

        public List<(byte[] Png, byte[] Dib)> PutImages { get; } = [];

        public bool Disposed { get; private set; }

        public void Copy(string text) => Captured!(new(text, ""), DateTimeOffset.Now);

        public void Copy(CopiedImage image) => Captured!(new(null, "", image), DateTimeOffset.Now);

        public void Put(string text) => PutTexts.Add(text);

        public void Put(byte[] png, byte[] dib) => PutImages.Add((png, dib));

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

        /// <summary>What the image format holds, and whether it is "PNG" rather than a bitmap.</summary>
        public byte[]? ImageBytes { get; init; }

        public bool ImageIsPng { get; init; }

        public bool ImageTouched { get; private set; }

        public (bool Png, long Bytes)? Image
        {
            get
            {
                Held();
                return ImageBytes is null ? null : (ImageIsPng, ImageBytes.Length);
            }
        }

        public CopiedImage ReadImage(bool png)
        {
            Held();
            Assert.Equal(ImageIsPng, png);
            ImageTouched = true;
            return new CopiedImage(ImageBytes!, png);
        }

        private void Held() => Assert.Equal(1, OpenNow);
    }
}
