using System.Buffers.Binary;
using System.Text;
using Tomte.Core;
using Tomte.Core.Storage;
using Xunit;

namespace Tomte.Tests;

public sealed class ShelfTests : IDisposable
{
    private readonly string dir = Path.Combine(Path.GetTempPath(), "tomte-shelf-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string user;
    private readonly string temp;
    private readonly string usb;

    public ShelfTests()
    {
        user = Directory.CreateDirectory(Path.Combine(dir, "user")).FullName;
        temp = Directory.CreateDirectory(Path.Combine(dir, "temp")).FullName;
        usb = Directory.CreateDirectory(Path.Combine(dir, "usb")).FullName;
    }

    private string ShelfFolder => Path.Combine(dir, "Shelf");

    public void Dispose()
    {
        // Junctions go first and on their own: a recursive delete treats one as a mount point and is refused.
        var links = Directory.EnumerateDirectories(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, AttributesToSkip = 0 })
            .Where(d => new DirectoryInfo(d).LinkTarget is not null).ToList();
        foreach (string link in links)
        {
            Directory.Delete(link);
        }

        Directory.Delete(dir, recursive: true);
    }

    [Fact]
    public async Task ItemsSurviveARestartNewestFirst()
    {
        string report = Write(user, "report.pdf", "pdf");
        var first = Store();
        await first.Accept(new DroppedItems([report], [], null, KeepCopy: false));
        await first.Accept(new DroppedItems([], [Received(first, "img.png", [1, 2, 3])], null, KeepCopy: false));

        var second = Store();
        await second.Load();

        Assert.Equal(["img.png", "report.pdf"], second.ItemNames);
        Assert.Equal([ShelfKind.Content, ShelfKind.Reference], second.Items.Select(i => i.Kind));
        Assert.Equal(report, second.Items[1].Path);
        Assert.Equal([1, 2, 3], File.ReadAllBytes(second.Items[0].Path));
        Assert.All(second.Items, i => Assert.False(i.Missing));
        Assert.Equal(3, second.Items[1].Size);
    }

    [Fact]
    public async Task ReferencesStayWhereTheyAreAndCopiesGoIntoTheShelfFolder()
    {
        string mine = Write(user, "mine.txt", "mine");
        string stick = Write(usb, "stick.txt", "stick");
        string kept = Write(user, "kept.txt", "kept");
        var shelf = Store();

        await shelf.Accept(new DroppedItems([mine, stick], [], null, KeepCopy: false));
        await shelf.Accept(new DroppedItems([kept], [], null, KeepCopy: true));

        var byName = shelf.Items.ToDictionary(i => i.Name);
        Assert.Equal((ShelfKind.Reference, mine), (byName["mine.txt"].Kind, byName["mine.txt"].Path));
        Assert.Equal(ShelfKind.Copy, byName["stick.txt"].Kind);
        Assert.Equal(ShelfKind.Copy, byName["kept.txt"].Kind);
        Assert.StartsWith(ShelfFolder, byName["stick.txt"].Path, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("kept", File.ReadAllText(byName["kept.txt"].Path));
        // The user's own files are where they were.
        Assert.True(File.Exists(mine) && File.Exists(stick) && File.Exists(kept));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ATemporaryFileIsTakenInBeforeTheDropReturns(bool link)
    {
        string attachment = Write(temp, "attachment.docx", "body");
        string folder = Directory.CreateDirectory(Path.Combine(temp, "Extracted")).FullName;
        Write(folder, "inside.txt", "inside");
        var shelf = Store(link: link);

        var queued = shelf.Accept(new DroppedItems([attachment, folder], [], null, KeepCopy: false));
        // The source cleans its temporary files up the moment the drop returns.
        Directory.Delete(temp, recursive: true);
        await queued;

        var byName = shelf.Items.ToDictionary(i => i.Name);
        Assert.Equal(ShelfKind.Copy, byName["attachment.docx"].Kind);
        Assert.Equal("body", File.ReadAllText(byName["attachment.docx"].Path));
        Assert.Equal("inside", File.ReadAllText(Path.Combine(byName["Extracted"].Path, "inside.txt")));
        Assert.True(byName["Extracted"].IsFolder);
    }

    [Theory]
    [InlineData(@"C:\Users\a\AppData\Local\Temp\x.txt", false, DriveType.Fixed, DropDecision.CopyNow)]
    [InlineData(@"C:\Users\a\AppData\Local\Temp2\x.txt", false, DriveType.Fixed, DropDecision.Reference)]
    [InlineData(@"C:\Users\a\Documents\x.txt", false, DriveType.Fixed, DropDecision.Reference)]
    [InlineData(@"C:\Users\a\Documents\x.txt", true, DriveType.Fixed, DropDecision.CopyLater)]
    [InlineData(@"E:\photos\x.jpg", false, DriveType.Removable, DropDecision.CopyLater)]
    [InlineData(@"F:\disc\x.jpg", false, DriveType.CDRom, DropDecision.CopyLater)]
    [InlineData(@"\\server\share\x.txt", false, DriveType.Network, DropDecision.Reference)]
    public void TemporaryFoldersRemovableDrivesAndCtrlAreCopied(string path, bool keepCopy, DriveType drive, DropDecision expected) =>
        Assert.Equal(expected, ShelfStore.Decide(path, keepCopy, [@"C:\Users\a\AppData\Local\Temp\"], _ => drive));

    [Fact]
    public async Task AMovedOrDeletedFileIsFlaggedWhenChecked()
    {
        string path = Write(user, "notes.txt", "notes");
        var shelf = Store();
        await shelf.Accept(new DroppedItems([path], [], null, KeepCopy: false));
        Assert.False(shelf.Items[0].Missing);

        File.Move(path, path + ".moved");
        await shelf.Check();
        Assert.True(shelf.Items[0].Missing);
        Assert.Null(shelf.Items[0].Size);

        // Still flagged after a restart, and back once the file is.
        var restarted = Store();
        await restarted.Load();
        Assert.True(restarted.Items[0].Missing);
        File.Move(path + ".moved", path);
        await restarted.Check();
        Assert.False(restarted.Items[0].Missing);
    }

    [Fact]
    public async Task ATemporaryFileOnTheShelfVolumeBecomesAHardLink()
    {
        string attachment = Write(temp, "big.bin", "first");
        var shelf = Store();
        await shelf.Accept(new DroppedItems([attachment], [], null, KeepCopy: false));
        string copy = shelf.Items.Single().Path;

        // One file under two names: a write through the source shows in the copy, and the copy outlives the source.
        File.AppendAllText(attachment, " second");
        Assert.Equal("first second", File.ReadAllText(copy));
        File.Delete(attachment);
        Assert.Equal("first second", File.ReadAllText(copy));
    }

    [Fact]
    public async Task ALargeTemporaryFileIsCopiedOffTheDropFromAHandleOpenedInIt()
    {
        var bytes = Enumerable.Range(0, 5000).Select(i => (byte)i).ToArray();
        string attachment = Path.Combine(temp, "large.bin");
        File.WriteAllBytes(attachment, bytes);
        var shelf = Store(link: false, inDrop: 1000);

        var queued = shelf.Accept(new DroppedItems([attachment], [], null, KeepCopy: false));
        // The source deletes its file as the drop returns; the handle opened in the drop keeps the data readable.
        File.Delete(attachment);
        await queued;

        var item = shelf.Items.Single();
        Assert.Equal(ShelfKind.Copy, item.Kind);
        Assert.Equal(bytes, File.ReadAllBytes(item.Path));
    }

    [Fact]
    public async Task ALargeTemporaryFolderIsCopiedAfterTheDrop()
    {
        string folder = Directory.CreateDirectory(Path.Combine(temp, "Large")).FullName;
        Write(folder, "a.txt", new string('a', 3000));
        var shelf = Store(link: false, inDrop: 1000);

        await shelf.Accept(new DroppedItems([folder], [], null, KeepCopy: false));

        var item = shelf.Items.Single();
        Assert.True(item.IsFolder);
        Assert.Equal(3000, new FileInfo(Path.Combine(item.Path, "a.txt")).Length);
    }

    [Fact]
    public async Task ACopiedFolderSkipsLinksAndKeepsEverythingElse()
    {
        string folder = Directory.CreateDirectory(Path.Combine(user, "project")).FullName;
        Write(folder, "top.txt", "top");
        Write(Directory.CreateDirectory(Path.Combine(folder, "sub")).FullName, "deep.txt", "deep");
        string outside = Directory.CreateDirectory(Path.Combine(dir, "outside")).FullName;
        Write(outside, "elsewhere.txt", "not copied");
        Junction(Path.Combine(folder, "link"), outside);
        var shelf = Store();

        await shelf.Accept(new DroppedItems([folder], [], null, KeepCopy: true));

        string copy = shelf.Items.Single().Path;
        Assert.Equal("top", File.ReadAllText(Path.Combine(copy, "top.txt")));
        Assert.Equal("deep", File.ReadAllText(Path.Combine(copy, "sub", "deep.txt")));
        Assert.False(Directory.Exists(Path.Combine(copy, "link")));
    }

    [Fact]
    public async Task ARemovedCopyKeepsItsFilesUntilTheNextProcess()
    {
        string mine = Write(user, "mine.txt", "mine");
        var shelf = Store();
        await shelf.Accept(new DroppedItems([mine], [Received(shelf, "drag.txt", "dragged out"u8.ToArray()), Received(shelf, "held.txt", "held"u8.ToArray())], null, KeepCopy: false));
        var dragged = shelf.Items.Single(i => i.Name == "drag.txt");
        var held = shelf.Items.Single(i => i.Name == "held.txt");
        string stray = Directory.CreateDirectory(Path.Combine(ShelfFolder, "left-over")).FullName;
        string strayWithAnId = Directory.CreateDirectory(Path.Combine(ShelfFolder, "0123456789ab")).FullName;

        await shelf.Remove([dragged, shelf.Items.Single(i => i.Name == "mine.txt")]);
        // A browser that was given this path by a drag may read it later in this session.
        Assert.True(File.Exists(dragged.Path));
        Assert.True(Directory.Exists(stray));

        var next = Store();
        await next.Load();
        Assert.False(Directory.Exists(Path.GetDirectoryName(dragged.Path)));
        // Only a removed item's own folder goes: a folder no row names may be the user's only copy of something.
        Assert.True(Directory.Exists(stray));
        Assert.True(Directory.Exists(strayWithAnId));
        Assert.True(File.Exists(held.Path));
        Assert.True(File.Exists(mine));
        Assert.Equal(["held.txt"], next.ItemNames);
    }

    [Fact]
    public async Task ASetAsideDatabaseLeavesTheCopiesWhereTheyAre()
    {
        string attachment = Write(temp, "only-copy.docx", "body");
        var shelf = Store();
        await shelf.Accept(new DroppedItems([attachment], [Received(shelf, "img.png", [1, 2])], null, KeepCopy: false));
        await shelf.Remove([shelf.Items.Single(i => i.Name == "img.png")]);
        var slots = Directory.GetDirectories(ShelfFolder);

        // A damaged file set aside and an empty one in its place: no row says anything about these folders now.
        foreach (string file in Directory.GetFiles(dir, "tomte.db*"))
        {
            File.Delete(file);
        }

        var fresh = Store();
        await fresh.Load();
        Assert.Empty(fresh.Items);
        Assert.All(slots, slot => Assert.True(Directory.Exists(slot)));
    }

    [Fact]
    public async Task TheSweepLeavesALinkedFolderAlone()
    {
        var shelf = Store();
        await shelf.Add([new ShelfEntry(ShelfKind.Content, "note.txt", Content: "note"u8.ToArray())]);
        var item = shelf.Items.Single();
        await shelf.Remove([item]);
        string slot = Path.GetDirectoryName(item.Path)!;
        Directory.Delete(slot, recursive: true);
        string outside = Directory.CreateDirectory(Path.Combine(dir, "outside")).FullName;
        string kept = Write(outside, "note.txt", "the user's");
        Junction(slot, outside);

        await Store().Load();
        Assert.True(File.Exists(kept));
    }

    [Fact]
    public async Task TheSweepDoesNothingWhenTheShelfFolderIsALink()
    {
        string real = Directory.CreateDirectory(Path.Combine(dir, "real-shelf")).FullName;
        Junction(ShelfFolder, real);
        var shelf = Store();
        await shelf.Add([new ShelfEntry(ShelfKind.Content, "note.txt", Content: "note"u8.ToArray())]);
        var item = shelf.Items.Single();
        await shelf.Remove([item]);

        await Store().Load();
        Assert.True(File.Exists(item.Path));
    }

    [Fact]
    public async Task AFolderTwoItemsNameStaysUntilBothAreRemoved()
    {
        var shelf = Store();
        await shelf.Add([new ShelfEntry(ShelfKind.Content, "note.txt", Content: "note"u8.ToArray())]);
        var first = shelf.Items.Single();
        // Shown in Explorer and Ctrl-dropped back: the copy keeps the shelf path, so two items name one folder.
        await shelf.Accept(new DroppedItems([first.Path], [], null, KeepCopy: true));
        var second = shelf.Items.Single(i => i.Id != first.Id);
        Assert.Equal(first.Path, second.Path);

        await shelf.Remove([first]);
        var next = Store();
        await next.Load();
        Assert.Equal("note", File.ReadAllText(second.Path));
        Assert.Equal([second.Name], next.ItemNames);

        await next.Remove(next.Items);
        await Store().Load();
        Assert.False(Directory.Exists(Path.GetDirectoryName(second.Path)));
    }

    [Fact]
    public async Task ARemovedItemWhoseFolderIsInUseIsSweptByALaterProcess()
    {
        var shelf = Store();
        await shelf.Add([new ShelfEntry(ShelfKind.Content, "open.txt", Content: "open"u8.ToArray())]);
        var item = shelf.Items.Single();
        await shelf.Remove([item]);

        using (File.OpenRead(item.Path))
        {
            await Store().Load();
            Assert.True(File.Exists(item.Path));
        }

        // The row was kept, so the process after that one deletes the folder.
        await Store().Load();
        Assert.False(Directory.Exists(Path.GetDirectoryName(item.Path)));
    }

    [Fact]
    public void TheSweepTakesOnlyTheShelfsOwnFolders()
    {
        string folder = Path.Combine(dir, "Shelf");
        Assert.Equal(Path.Combine(folder, "0123456789ab"), ShelfStore.OwnSlot(folder, Path.Combine(folder, "0123456789ab", "a.txt")));
        Assert.Null(ShelfStore.OwnSlot(folder, Path.Combine(folder, "0123456789a", "a.txt")));
        Assert.Null(ShelfStore.OwnSlot(folder, Path.Combine(folder, "0123456789az", "a.txt")));
        Assert.Null(ShelfStore.OwnSlot(folder, Path.Combine(folder, "Pictures", "a.txt")));
        Assert.Null(ShelfStore.OwnSlot(folder, Path.Combine(folder, "0123456789ab", "inner", "a.txt")));
        Assert.Null(ShelfStore.OwnSlot(folder, Path.Combine(dir, "0123456789ab", "a.txt")));
        Assert.Null(ShelfStore.OwnSlot(folder, Path.Combine(folder, "a.txt")));
    }

    [Fact]
    public async Task AReferenceTheTargetMovedAwayLeavesTheShelfOnTheWorker()
    {
        string moved = Write(user, "moved.txt", "m");
        string stayed = Write(user, "stayed.txt", "s");
        var shelf = Store();
        await shelf.Accept(new DroppedItems([moved, stayed], [], null, KeepCopy: false));
        File.Move(moved, Path.Combine(temp, "moved.txt"));

        await shelf.ForgetMovedAway(shelf.Items, ShelfEffect.None, ShelfEffect.Move);
        Assert.Equal(["stayed.txt"], shelf.ItemNames);
        await shelf.ForgetMovedAway(shelf.Items, ShelfEffect.Copy, ShelfEffect.None);
        Assert.Equal(["stayed.txt"], shelf.ItemNames);
    }

    [Fact]
    public void AReceivedVirtualFileGoesStraightToDiskAndIsRefusedPastTheCapOrAFailedRead()
    {
        var shelf = Store(maxReceived: 1000);
        string? path = shelf.Receive("CON.txt", Reader(new byte[1000]));
        Assert.Equal("_CON.txt", Path.GetFileName(path));
        Assert.Equal(1000, new FileInfo(path!).Length);

        // One byte over the cap is refused and leaves nothing behind; so is a stream that never ends, and a transfer
        // that breaks partway.
        int before = Directory.GetDirectories(ShelfFolder).Length;
        Assert.Null(shelf.Receive("big.bin", Reader(new byte[1001])));
        Assert.Null(shelf.Receive("endless.bin", buffer => buffer.Length));
        int reads = 0;
        Assert.Null(shelf.Receive("broken.bin", buffer => ++reads == 1 ? 100 : -1));
        Assert.Equal(before, Directory.GetDirectories(ShelfFolder).Length);
    }

    [Fact]
    public async Task FoldersMadeInThisProcessSurviveItsFirstLoad()
    {
        // The first drop of a session can copy a temporary file in before the shelf has loaded.
        string attachment = Write(temp, "first.txt", "first");
        var shelf = Store();
        await shelf.Accept(new DroppedItems([attachment], [], null, KeepCopy: false));
        Assert.True(File.Exists(shelf.Items.Single().Path));
    }

    [Fact]
    public async Task NamesStayUniqueAndCreateFileReplaces()
    {
        string a = Write(user, "a.txt", "one");
        string b = Write(Directory.CreateDirectory(Path.Combine(user, "other")).FullName, "A.txt", "two");
        var shelf = Store();
        await shelf.Accept(new DroppedItems([a, b], [], null, KeepCopy: false));
        Assert.Equal(["A (2).txt", "a.txt"], shelf.ItemNames);

        IShelf tools = shelf;
        tools.CreateFile("summary.md", "first");
        tools.CreateFile("Summary.md", "second");
        await shelf.Load();
        Assert.Equal("second", tools.ReadText("summary.md"));
        Assert.Single(shelf.Items, i => i.Name.Equals("summary.md", StringComparison.OrdinalIgnoreCase));
        Assert.Equal("one", tools.ReadText("a.txt"));

        tools.Remove("summary.md");
        await shelf.Load();
        Assert.Null(tools.ReadText("summary.md"));
        Assert.Equal(2, shelf.Items.Count);
    }

    [Fact]
    public async Task ReadTextOnlyReadsText()
    {
        var shelf = Store();
        await shelf.Accept(new DroppedItems([], [Received(shelf, "bin.dat", [0x50, 0x00, 0x4B]), Received(shelf, "utf16.txt", [0xFF, 0xFE, (byte)'h', 0, (byte)'i', 0])], "plain text\nmore", KeepCopy: false));
        Assert.Null(shelf.ReadText("bin.dat"));
        Assert.Equal("hi", shelf.ReadText("utf16.txt"));
        Assert.Equal("plain text\nmore", shelf.ReadText("plain text.txt"));
        Assert.Null(shelf.ReadText("nothing.txt"));
    }

    [Fact]
    public void DroppedTextBecomesAFile()
    {
        var (link, bytes) = ShelfFormats.FromText("  https://github.com/owner/repo  ");
        Assert.Equal("github.com.url", link);
        Assert.Equal("[InternetShortcut]\r\nURL=https://github.com/owner/repo\r\n", Encoding.ASCII.GetString(bytes));

        Assert.Equal("see https___example.com.txt", ShelfFormats.FromText("see https://example.com").Name);
        Assert.Equal("Text.txt", ShelfFormats.FromText("\n\n  ").Name);
        Assert.Equal("a_b c.txt", ShelfFormats.FromText("a/b c\nsecond line").Name);
    }

    [Fact]
    public void DropFilesParseAndBuild()
    {
        string[] paths = [@"C:\Users\a\report.pdf", @"\\server\share\folder"];
        Assert.Equal(paths, ShelfFormats.ParseDropFiles(ShelfFormats.DropFiles(paths)));

        // The ANSI form, as an old source writes it.
        var ansi = new byte[20 + 12];
        BinaryPrimitives.WriteUInt32LittleEndian(ansi, 20);
        Encoding.ASCII.GetBytes(@"C:\a.txt").CopyTo(ansi, 20);
        Assert.Equal([@"C:\a.txt"], ShelfFormats.ParseDropFiles(ansi));

        // Written by another process: an offset past the end or a list without its end are survived.
        var bad = ShelfFormats.DropFiles(paths);
        BinaryPrimitives.WriteUInt32LittleEndian(bad, 4000);
        Assert.Empty(ShelfFormats.ParseDropFiles(bad));
        Assert.Equal([@"C:\Users\a\report.pdf", @"\\server\share\fol"], ShelfFormats.ParseDropFiles(ShelfFormats.DropFiles(paths).AsSpan(..^10)));
    }

    [Fact]
    public void TheFileGroupDescriptorLayout()
    {
        var bytes = ShelfFormats.Descriptor([("notes.txt", 20), ("big.bin", 5_000_000_000)]);

        Assert.Equal(4 + 2 * 592, bytes.Length);
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(bytes));
        // FD_UNICODE | FD_PROGRESSUI | FD_FILESIZE, then the size as high and low halves, then the wide name.
        Assert.Equal(0x80004040u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4)));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4 + 592 + 64)));
        Assert.Equal(705_032_704u, BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(4 + 592 + 68)));
        Assert.Equal("notes.txt\0", Encoding.Unicode.GetString(bytes, 4 + 72, 20));

        Assert.Equal(
            [new VirtualFile("notes.txt", 20, false), new VirtualFile("big.bin", 5_000_000_000, false)],
            ShelfFormats.ParseDescriptor(bytes));
    }

    [Fact]
    public void AVirtualFileIsReadToTheEndWhateverItsDescriptorSays()
    {
        // As Chrome sends an image: no FD_FILESIZE (flags 0x8000), size 0; and one that declares too little.
        var descriptor = ShelfFormats.Descriptor([("img.png", 0), ("short.txt", 5)]);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(4), 0x8000);
        var files = ShelfFormats.ParseDescriptor(descriptor);
        Assert.Equal([new VirtualFile("img.png", null, false), new VirtualFile("short.txt", 5, false)], files);

        var content = Enumerable.Range(0, 1538).Select(i => (byte)i).ToArray();
        var into = new MemoryStream();
        // A stream that hands out 100 bytes at a time, like a pipe from the source process.
        Assert.True(ShelfFormats.CopyCapped(Reader(content, 100), into, cap: 1538));
        Assert.Equal(content, into.ToArray());
        Assert.False(ShelfFormats.CopyCapped(Reader(content, 100), new MemoryStream(), cap: 1537));

        // An HGLOBAL block can be larger than its file; the declared size wins only when it fits.
        Assert.Equal(5, ShelfFormats.ContentLength(5, 4096));
        Assert.Equal(1538, ShelfFormats.ContentLength(null, 1538));
        Assert.Equal(4096, ShelfFormats.ContentLength(10_000, 4096));
        Assert.Equal(4096, ShelfFormats.ContentLength(0, 4096));
    }

    [Fact]
    public void ADescriptorIsReadOnlyAsFarAsItReaches()
    {
        var descriptor = ShelfFormats.Descriptor([("a.txt", 1), ("b.txt", 2)]);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor, 1000);
        Assert.Equal(2, ShelfFormats.ParseDescriptor(descriptor).Count);
        Assert.Empty(ShelfFormats.ParseDescriptor(descriptor.AsSpan(0, 3)));

        // A folder entry: FD_ATTRIBUTES and FILE_ATTRIBUTE_DIRECTORY.
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(4), 0x80000004);
        BinaryPrimitives.WriteUInt32LittleEndian(descriptor.AsSpan(4 + 36), 0x10);
        Assert.True(ShelfFormats.ParseDescriptor(descriptor)[0].IsFolder);
    }

    [Fact]
    public void WhatADragOutOffers()
    {
        var reference = Item(ShelfKind.Reference);
        var copy = Item(ShelfKind.Copy);
        var content = Item(ShelfKind.Content);

        // The target reads the keys: Ctrl copies and Shift moves, as in any Windows drag.
        Assert.Equal(ShelfEffect.Copy | ShelfEffect.Move, ShelfFormats.Effects([reference, reference]));
        Assert.Equal(ShelfEffect.Copy, ShelfFormats.Effects([reference, copy]));
        Assert.Equal(ShelfEffect.Copy, ShelfFormats.Effects([content]));

        Assert.True(ShelfFormats.AsVirtualFiles([content, content]));
        Assert.False(ShelfFormats.AsVirtualFiles([content, reference]));
        Assert.False(ShelfFormats.AsVirtualFiles([copy]));
    }

    [Fact]
    public void OnlyAReferenceTheTargetMovedAwayLeavesTheShelf()
    {
        var moved = Item(ShelfKind.Reference, @"C:\gone.txt");
        var stayed = Item(ShelfKind.Reference, @"C:\here.txt");
        var copy = Item(ShelfKind.Copy, @"C:\shelf\copy.txt");
        bool Exists(string p) => p != @"C:\gone.txt" && p != @"C:\shelf\copy.txt";

        // Explorer's same-volume move: DoDragDrop says none, "Performed DropEffect" says move.
        Assert.Equal([moved], ShelfFormats.MovedAway([moved, stayed, copy], ShelfEffect.None, ShelfEffect.Move, Exists));
        Assert.Equal([moved], ShelfFormats.MovedAway([moved, stayed], ShelfEffect.Move, ShelfEffect.None, Exists));
        Assert.Empty(ShelfFormats.MovedAway([moved, stayed], ShelfEffect.Copy, ShelfEffect.Copy, Exists));
    }

    [Fact]
    public void ItemActions()
    {
        var present = Item(ShelfKind.Reference, @"C:\a\one.txt");
        var missing = Item(ShelfKind.Reference, @"C:\a\two.txt") with { Missing = true };

        Assert.All(Enum.GetValues<ShelfAction>(), a => Assert.True(ShelfStore.Allows(a, present)));
        Assert.Equal([ShelfAction.CopyPath, ShelfAction.Remove], Enum.GetValues<ShelfAction>().Where(a => ShelfStore.Allows(a, missing)));
        Assert.Equal("C:\\a\\one.txt" + Environment.NewLine + "C:\\a\\two.txt", ShelfStore.PathsText([present, missing]));
    }

    [Theory]
    [InlineData("report.pdf", "report.pdf")]
    [InlineData("a:b*c?.txt", "a_b_c_.txt")]
    [InlineData("  ends with dots...  ", "ends with dots")]
    [InlineData("", "Item")]
    [InlineData("CON", "_CON")]
    [InlineData("nul.txt", "_nul.txt")]
    [InlineData("COM1.tar.gz", "_COM1.tar.gz")]
    [InlineData("lpt9", "_lpt9")]
    [InlineData("CONSOLE.txt", "CONSOLE.txt")]
    [InlineData("COM10.txt", "COM10.txt")]
    public void SafeNames(string name, string expected) => Assert.Equal(expected, ShelfStore.SafeName(name));

    [Fact]
    public void TheDragImageHelperWaitsForTheShapeToStop()
    {
        var gate = new DragImageGate();
        Assert.True(gate.Begin(shapeMoving: false));
        Assert.False(gate.Over(shapeMoving: false));
        Assert.True(gate.End());

        // Entered during the reveal: the helper comes in on the first DragOver after the shape stops, once.
        Assert.False(gate.Begin(shapeMoving: true));
        Assert.False(gate.Over(shapeMoving: true));
        Assert.True(gate.Over(shapeMoving: false));
        Assert.False(gate.Over(shapeMoving: false));
        Assert.True(gate.Entered);
        Assert.True(gate.End());

        // Left before the shape stopped: the helper never entered and hears nothing.
        Assert.False(gate.Begin(shapeMoving: true));
        Assert.False(gate.End());
        Assert.False(gate.Over(shapeMoving: false));
    }

    [Fact]
    public void UniqueNames()
    {
        var taken = new HashSet<string>(["a.txt", "A (2).txt", "b"], StringComparer.OrdinalIgnoreCase);
        Assert.Equal("a (3).txt", ShelfStore.Unique("a.txt", taken));
        Assert.Equal("b (2)", ShelfStore.Unique("b", taken));
        Assert.Equal("c.txt", ShelfStore.Unique("c.txt", taken));
    }

    private static readonly System.Numerics.Vector2 Body = new(560, 260 - PanelSpace.TabStripHeight);

    [Fact]
    public void TheGridFitsTheShelfPanelClearOfItsCorners()
    {
        Assert.Equal(2, ShelfLayout.Rows(Body));
        for (int slot = 0; slot < 8; slot++)
        {
            var tile = ShelfLayout.Tile(slot, Body);
            Assert.True(tile.Left >= ShelfLayout.PadX && tile.Right <= Body.X - ShelfLayout.PadX + 0.01f && tile.Bottom <= Body.Y - ShelfLayout.PadY + 0.01f, $"tile {slot} at {tile}");
            // The panel's bottom corners have a radius of 22; a tile corner must sit inside the rounded shape.
            Assert.True(InsideRoundedBottom(tile.Left, tile.Bottom, Body, 22) && InsideRoundedBottom(tile.Right, tile.Bottom, Body, 22), $"tile {slot}");
            foreach (var action in Enum.GetValues<ShelfAction>())
            {
                Assert.True(tile.Contains(ShelfLayout.ActionButton(action, tile)), $"{action} on tile {slot}");
            }
        }
    }

    [Fact]
    public void TheDropTargetLeavesTheMascotItsColumnAndClearsTheCorners()
    {
        var g = IslandGeometry.ShelfDrop;
        var r = ShelfLayout.DropTarget(g.Width, g.Depth);
        Assert.True(r.Left >= -g.Width / 2 + 8 + 26 + 8);
        // The floating pill rounds all four corners with the state's corner, 20 here.
        foreach (var (x, y) in new[] { (r.Left, r.Top), (r.Right, r.Top), (r.Left, r.Bottom), (r.Right, r.Bottom) })
        {
            double cx = Math.Clamp(x, -g.Width / 2 + g.Corner, g.Width / 2 - g.Corner), cy = Math.Clamp(y, g.Corner, g.Depth - g.Corner);
            Assert.True(Math.Sqrt((x - cx) * (x - cx) + (y - cy) * (y - cy)) <= g.Corner, $"corner ({x}, {y})");
        }
    }

    [Fact]
    public void APressFindsItsTileAndOnlyASelectedTileHasButtons()
    {
        var sixth = ShelfLayout.Tile(5, Body);
        var centre = new System.Numerics.Vector2(sixth.X + sixth.Width / 2, sixth.Y + sixth.Height - 10);
        Assert.Equal((5, (ShelfAction?)null), ShelfLayout.At(centre, Body, 0, 6, _ => false));
        Assert.Equal((9, (ShelfAction?)null), ShelfLayout.At(centre, Body, 4, 12, _ => false));
        Assert.Equal((-1, (ShelfAction?)null), ShelfLayout.At(centre, Body, 0, 5, _ => false));
        Assert.Equal((-1, (ShelfAction?)null), ShelfLayout.At(new(5, 5), Body, 0, 6, _ => true));

        var share = ShelfLayout.ActionButton(ShelfAction.Share, sixth);
        var onShare = new System.Numerics.Vector2(share.X + 11, share.Y + 11);
        Assert.Equal((5, (ShelfAction?)ShelfAction.Share), ShelfLayout.At(onShare, Body, 0, 6, i => i == 5));
        Assert.Equal((5, (ShelfAction?)null), ShelfLayout.At(onShare, Body, 0, 6, _ => false));
    }

    [Fact]
    public void TheWheelScrollsARowANotchWithinTheItems()
    {
        // 18 items are five rows; two show.
        Assert.Equal(4, ShelfLayout.Scroll(0, -1, 18, Body));
        Assert.Equal(12, ShelfLayout.Scroll(0, -10, 18, Body));
        Assert.Equal(0, ShelfLayout.Scroll(4, 3, 18, Body));
        Assert.Equal(0, ShelfLayout.Scroll(0, -1, 8, Body));
        // Items removed while scrolled down pull the page back.
        Assert.Equal(0, ShelfLayout.Scroll(12, 0, 5, Body));
    }

    [Theory]
    [InlineData(1.0)]
    [InlineData(1.25)]
    [InlineData(1.5)]
    public void APressInWindowPixelsLandsOnTheSameTileAtEveryScale(double scale)
    {
        // A press arrives in window pixels; the panel host maps it into island space and then into the body, as for
        // Media. The island's DIP space starts one pixel in plus half the outline's width and carries the scale.
        var size = PanelSize.Shelf;
        float originX = 1 + (float)(IslandGeometry.Panel(size.W, size.H).OuterWidth / 2 * scale);
        for (int slot = 0; slot < 8; slot++)
        {
            var tile = ShelfLayout.Tile(slot, Body);
            foreach (var (x, y) in new[] { (tile.Left + 1, tile.Top + 1), (tile.Right - 1, tile.Bottom - 1) })
            {
                int px = (int)Math.Round(originX + (x - size.W / 2) * scale), py = (int)Math.Round(1 + (y + PanelSpace.TabStripHeight) * scale);
                var back = PanelSpace.ToBody(PanelSpace.FromPixels(px, py, originX, 1, scale), size.W);
                Assert.Equal(slot, ShelfLayout.At(back, Body, 0, 8, _ => false).Index);
            }
        }
    }

    [Fact]
    public void TheStringsThatWaitForTheRenderer()
    {
        Assert.Equal("1 item · references by default", ShelfLayout.Head(1));
        Assert.Equal("3 items · references by default", ShelfLayout.Head(3));
        Assert.Equal("4 KB", ShelfLayout.Size(4000));
        Assert.Equal("1 KB", ShelfLayout.Size(20));
        Assert.Equal("2.4 MB", ShelfLayout.Size(2_516_582));
        Assert.Equal("5.0 GB", ShelfLayout.Size(5L << 30));
        var item = Item(ShelfKind.Reference, @"C:\a\Q3-report.pdf") with { Size = 2_516_582 };
        Assert.Equal("2.4 MB", ShelfLayout.Meta(item));
        Assert.Equal("File moved or deleted", ShelfLayout.Meta(item with { Missing = true }));
        Assert.Equal("Folder", ShelfLayout.Meta(item with { IsFolder = true, Size = null }));
        Assert.Equal(0xC0392Bu, ShelfLayout.KindColor(item));
        Assert.Equal("Reveal in Explorer", ShelfLayout.Name(ShelfAction.Reveal));
    }

    private static bool InsideRoundedBottom(float x, float y, System.Numerics.Vector2 body, float radius)
    {
        // Body coordinates; the panel's bottom corners are the body's.
        float cx = Math.Clamp(x, radius, body.X - radius), cy = Math.Min(y, body.Y - radius);
        return (x - cx) * (x - cx) + (y - cy) * (y - cy) <= radius * radius;
    }

    private static ShelfItem Item(ShelfKind kind, string path = @"C:\x.txt") => new(1, Path.GetFileName(path), path, kind, DateTimeOffset.UnixEpoch);

    private static string Write(string folder, string name, string text)
    {
        string path = Path.Combine(folder, name);
        File.WriteAllText(path, text);
        return path;
    }

    private static ReadChunk Reader(byte[] content, int chunk = 4096)
    {
        int at = 0;
        return buffer =>
        {
            int n = Math.Min(chunk, Math.Min(buffer.Length, content.Length - at));
            content.AsSpan(at, n).CopyTo(buffer);
            at += n;
            return n;
        };
    }

    // A virtual file as a drop hands it to Accept: already written into the shelf folder.
    private static (string, string) Received(ShelfStore shelf, string name, byte[] content) => (name, shelf.Receive(name, Reader(content))!);

    // A directory junction, which needs no privilege to make.
    private static void Junction(string link, string target)
    {
        using var mklink = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"") { CreateNoWindow = true, UseShellExecute = false, RedirectStandardOutput = true })!;
        mklink.StandardOutput.ReadToEnd();
        mklink.WaitForExit();
        Assert.True(Directory.Exists(link), $"no junction at {link}");
    }

    private ShelfStore Store(bool link = true, long inDrop = ShelfStore.InDropCopyBytesDefault, long maxReceived = ShelfStore.MaxReceivedBytesDefault) =>
        new(new TomteDb(Path.Combine(dir, "tomte.db")), ShelfFolder, action => action())
        {
            TempRoots = [temp],
            DriveTypeOf = p => p.StartsWith(usb, StringComparison.OrdinalIgnoreCase) ? DriveType.Removable : DriveType.Fixed,
            LinkTemporaryFiles = link,
            InDropCopyBytes = inDrop,
            MaxReceivedBytes = maxReceived,
        };
}
