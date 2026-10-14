using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Tomte.Core;
using Tomte.Core.Device;
using Tomte.Core.Interaction;
using Tomte.Core.Shortcuts;
using Tomte.Core.Storage;
using Xunit;

namespace Tomte.Tests;

public sealed class ShortcutsTests : IDisposable
{
    // Spaces, an ampersand and a caret: what a careless command line would split or run.
    private readonly string dir = Path.Combine(Path.GetTempPath(), "tomte-tests-" + Guid.NewGuid().ToString("N"), "tools & co ^1");
    private readonly List<ShortcutTarget> started = [];
    private bool scriptsOn;

    public ShortcutsTests() => Directory.CreateDirectory(dir);

    private string DbPath => Path.Combine(dir, "..", "tomte.db");

    public void Dispose() => Directory.Delete(Path.GetDirectoryName(dir)!, recursive: true);

    [Fact]
    public void TargetsAreClassifiedByWhatIsOnDisk()
    {
        Assert.Equal(ShortcutKind.App, ShortcutTarget.Read(Touch("tool.exe")).Kind);
        Assert.Equal(ShortcutKind.File, ShortcutTarget.Read(Touch("notes.txt")).Kind);
        Assert.Equal(ShortcutKind.File, ShortcutTarget.Read(Touch("report.pdf")).Kind);
        Assert.Equal(ShortcutKind.Folder, ShortcutTarget.Read(dir).Kind);
        Assert.Equal(ShortcutKind.Folder, ShortcutTarget.Read(Path.GetPathRoot(dir)!).Kind);

        var web = ShortcutTarget.Read("https://example.com/a b?q=1");
        Assert.Equal(ShortcutKind.Url, web.Kind);
        Assert.Equal("https://example.com/a%20b?q=1", web.Open);
    }

    [Theory]
    [InlineData("build.bat", "cmd.exe")]
    [InlineData("build.cmd", "cmd.exe")]
    [InlineData("build.ps1", "powershell.exe")]
    [InlineData("build.vbs", "wscript.exe")]
    [InlineData("build.vbe", "wscript.exe")]
    [InlineData("build.js", "wscript.exe")]
    [InlineData("build.jse", "wscript.exe")]
    [InlineData("build.wsf", "wscript.exe")]
    public void ScriptFilesAreScriptsRunByAWindowsHost(string name, string host)
    {
        string script = Touch(name);
        var target = ShortcutTarget.Read(script);

        Assert.Equal(ShortcutKind.Script, target.Kind);
        Assert.Equal(Path.Combine(Environment.SystemDirectory, host == "powershell.exe" ? @"WindowsPowerShell\v1.0\powershell.exe" : host), target.Script!.Exe);
        Assert.Contains($"\"{script}\"", target.Script.Arguments);
        Assert.Equal(dir, target.Script.Folder);
        Assert.Contains(target.Script.Exe, target.Script.Text);
    }

    [Fact]
    public void BatchFilesRunThroughCmdWithThePathQuotedInsideTheOuterPair()
    {
        string script = Touch("build.bat");
        // /s strips the first and last quote only, so & and ^ stay inside the path's own quotes.
        Assert.Equal($"/d /s /c \"\"{script}\"\"", ShortcutTarget.Read(script).Script!.Arguments);
    }

    [Fact]
    public void ABatchFileWithAPercentSignIsBlocked()
    {
        var target = ShortcutTarget.Read(Touch("100%done.bat"));
        Assert.Equal(ShortcutKind.Blocked, target.Kind);
        Assert.Null(target.Script);
    }

    [Theory]
    [InlineData("setup.msi")]
    [InlineData("tweak.reg")]
    [InlineData("page.hta")]
    [InlineData("tool.jar")]
    [InlineData("saver.scr")]
    [InlineData("run.py")]
    [InlineData("run.sh")]
    [InlineData("app.pyz")]
    [InlineData("app.pyzw")]
    [InlineData("cached.pyc")]
    [InlineData("cached.pyo")]
    [InlineData("app.appinstaller")]
    [InlineData("app.msix")]
    [InlineData("old.com")]
    [InlineData("search.searchConnector-ms")]
    [InlineData("pack.themepack")]
    public void FilesThatInstallOrRunSomethingAreBlocked(string name)
    {
        var target = ShortcutTarget.Read(Touch(name));
        Assert.Equal(ShortcutKind.Blocked, target.Kind);
        Assert.NotNull(target.Problem);
    }

    [Fact]
    public void WindowsOwnDangerousListBlocksTypesTomteDoesNotName() =>
        // .wsh is on Windows' list (checked on 26200) and on neither of Tomte's.
        Assert.Equal(ShortcutKind.Blocked, ShortcutTarget.Read(Touch("settings.wsh")).Kind);

    [Fact]
    public void ALinkIsWhatItPointsAt()
    {
        string bat = Touch("build.bat");
        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");

        Assert.Equal(ShortcutKind.Script, ShortcutTarget.Read(Link("bat.lnk", bat)).Kind);
        Assert.Equal(ShortcutKind.Script, ShortcutTarget.Read(Link("cmd-args.lnk", cmd, "/c echo hi")).Kind);
        Assert.Equal(ShortcutKind.App, ShortcutTarget.Read(Link("cmd.lnk", cmd)).Kind);
        Assert.Equal(ShortcutKind.App, ShortcutTarget.Read(Link("tool.lnk", Touch("tool.exe"))).Kind);
        Assert.Equal(ShortcutKind.Folder, ShortcutTarget.Read(Link("folder.lnk", dir)).Kind);
        Assert.Equal(ShortcutKind.File, ShortcutTarget.Read(Link("notes.lnk", Touch("notes.txt"))).Kind);
        Assert.Equal(ShortcutKind.Blocked, ShortcutTarget.Read(Link("setup.lnk", Touch("setup.msi"))).Kind);

        // The link starts, not its target, so the link's own arguments and folder apply; a script runs as its command.
        string notesLink = Path.Combine(dir, "notes.lnk");
        Assert.Equal(notesLink, ShortcutTarget.Read(notesLink).Open);
        var script = ShortcutTarget.Read(Link("bat-args.lnk", bat, "release")).Script!;
        Assert.Equal($"/d /s /c \"\"{bat}\" release\"", script.Arguments);
        var command = ShortcutTarget.Read(Path.Combine(dir, "cmd-args.lnk")).Script!;
        Assert.Equal(cmd, command.Exe, ignoreCase: true);
        Assert.Equal("/c echo hi", command.Arguments);
    }

    [Fact]
    public void InternetShortcutsOpenOnlyWebAddresses()
    {
        Assert.Equal(new ShortcutTarget(ShortcutKind.Url, "https://example.com/"), ShortcutTarget.Read(Write("site.url", "[InternetShortcut]\r\nURL=https://example.com/\r\n")));
        Assert.Equal(ShortcutKind.Blocked, ShortcutTarget.Read(Write("local.url", "[InternetShortcut]\r\nURL=file:///C:/Windows/System32/cmd.exe\r\n")).Kind);
        Assert.Equal(ShortcutKind.Blocked, ShortcutTarget.Read(Write("page.url", "[InternetShortcut]\r\nURL=ms-settings:display\r\n")).Kind);
    }

    [Theory]
    [InlineData("mailto:someone@example.com")]
    [InlineData("ms-settings:display")]
    [InlineData("file:///C:/Windows/System32/cmd.exe")]
    [InlineData("javascript:alert(1)")]
    [InlineData("shell:startup")]
    [InlineData("steam://run/440")]
    [InlineData("ftp://example.com/file")]
    [InlineData("http://")]
    [InlineData(@"\\server\share\notes.txt")]
    [InlineData(@"\\?\C:\Windows\notepad.exe")]
    [InlineData("//server/share")]
    [InlineData(@"\/server\share\notes.txt")]
    [InlineData(@"/\server\share\notes.txt")]
    [InlineData("notes.txt")]
    [InlineData(@"C:notes.txt")]
    [InlineData(@"C:\notes.txt:hidden")]
    [InlineData("C:\\a\"b")]
    [InlineData("")]
    [InlineData(@"shell:AppsFolder\")]
    [InlineData(@"shell:AppsFolder\..\Startup")]
    [InlineData(@"shell:AppsFolderX\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    public void TargetsThatAreNotAFullLocalPathOrAWebAddressAreRefused(string target)
    {
        Assert.NotNull(ShortcutTarget.Refuse(target));
        Assert.Equal(ShortcutKind.Blocked, ShortcutTarget.Read(target).Kind);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://localhost:8080/app")]
    [InlineData(@"C:\Tools\app.exe")]
    [InlineData(@"shell:AppsFolder\Microsoft.WindowsCalculator_8wekyb3d8bbwe!App")]
    [InlineData(@"shell:appsfolder\{1AC14E77-02E7-4E5D-B744-2EB1AE5198B7}\charmap.exe")]
    public void FullPathsAndWebAddressesPass(string target) => Assert.Null(ShortcutTarget.Refuse(target));

    [Fact]
    public void AMissingTargetIsFlaggedAndNeverThrows()
    {
        string gone = Path.Combine(dir, "gone.exe");
        var target = ShortcutTarget.Read(gone);
        Assert.True(target.Missing);
        Assert.Equal(ShortcutKind.App, target.Kind);

        string link = Link("dangling.lnk", Touch("soon-gone.txt"));
        File.Delete(Path.Combine(dir, "soon-gone.txt"));
        Assert.True(ShortcutTarget.Read(link).Missing);
    }

    [Fact]
    public void TheFingerprintFollowsTheScriptsBytesAndTheLinksArguments()
    {
        string script = Write("build.ps1", "Write-Host one");
        string before = ShortcutTarget.Read(script).Script!.Fingerprint;
        Assert.Equal(before, ShortcutTarget.Read(script).Script!.Fingerprint);

        File.WriteAllText(script, "Write-Host two");
        Assert.NotEqual(before, ShortcutTarget.Read(script).Script!.Fingerprint);

        string cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        string first = ShortcutTarget.Read(Link("c.lnk", cmd, "/c echo one")).Script!.Fingerprint;
        Assert.NotEqual(first, ShortcutTarget.Read(Link("c.lnk", cmd, "/c echo two")).Script!.Fingerprint);
    }

    [Fact]
    public void NamesComeFromTheTarget()
    {
        Assert.Equal("Code", ShortcutTarget.NameFor(@"C:\Apps\Code.exe"));
        Assert.Equal("Visual Studio Code", ShortcutTarget.NameFor(@"C:\Start\Visual Studio Code.lnk"));
        Assert.Equal("build.ps1", ShortcutTarget.NameFor(@"C:\src\build.ps1"));
        Assert.Equal("Downloads", ShortcutTarget.NameFor(@"C:\Users\me\Downloads\"));
        Assert.Equal("D:", ShortcutTarget.NameFor(@"D:\"));
        Assert.Equal("figma.com", ShortcutTarget.NameFor("https://www.figma.com/file/x"));
    }

    [Fact]
    public void TilesSurviveARestartInTheOrderTheyWereAdded()
    {
        var store = Store();
        Assert.Null(store.Add(Touch("tool.exe")));
        Assert.Null(store.Add(" https://example.com/docs "));
        Assert.Null(store.Add(dir));

        // A new database object over the same file is what the next start of the app sees.
        var again = new ShortcutStore(new TomteDb(DbPath), () => scriptsOn, Record).Load();
        Assert.Equal(["tool", "example.com", "tools & co ^1"], again.Select(t => t.Name));
        Assert.Equal("https://example.com/docs", again[1].Target);
        Assert.All(again, t => Assert.Null(t.Confirmed));

        store.Remove(again[0].Id);
        Assert.Equal(["example.com", "tools & co ^1"], Store().Load().Select(t => t.Name));
    }

    [Fact]
    public void AddRefusesWhatCouldNotOpen()
    {
        var store = Store();
        Assert.NotNull(store.Add("mailto:someone@example.com"));
        Assert.NotNull(store.Add(@"\\server\share\x.txt"));
        Assert.NotNull(store.Add(Path.Combine(dir, "nothing.txt")));
        Assert.NotNull(store.Add(Touch("setup.msi")));
        Assert.Empty(store.Load());
    }

    [Fact]
    public void WebAndFileTilesStartWhatTheyPointAt()
    {
        var store = Store();
        store.Add("https://example.com/");
        store.Add(Touch("notes.txt"));
        store.Add(dir);
        foreach (var tile in store.Load())
        {
            Assert.Equal(ShortcutOutcome.Opened, store.Launch(tile.Id).Outcome);
        }

        // The recorder stands in for the browser and Explorer: nothing opened on this PC.
        Assert.Equal(
            [(ShortcutKind.Url, "https://example.com/"), (ShortcutKind.File, Path.Combine(dir, "notes.txt")), (ShortcutKind.Folder, dir)],
            started.Select(t => (t.Kind, t.Open)));
    }

    [Fact]
    public void AScriptRunsOnlyWhenScriptsAreOnAndThisCommandWasConfirmed()
    {
        var store = Store();
        string script = Write("build.ps1", "Write-Host one");
        store.Add(script);
        long id = store.Load().Single().Id;

        Assert.Equal(ShortcutOutcome.ScriptsOff, store.Launch(id).Outcome);
        scriptsOn = true;
        Assert.Equal(ShortcutOutcome.NeedsConfirm, store.Launch(id).Outcome);
        Assert.Empty(started);

        var shown = ShortcutTarget.Read(script).Script!;
        store.Confirm(id, shown);
        Assert.Equal(ShortcutOutcome.Opened, store.Launch(id).Outcome);
        Assert.Equal(shown, Assert.Single(started).Script);

        // Turning the switch off again stops a confirmed script too.
        scriptsOn = false;
        Assert.Equal(ShortcutOutcome.ScriptsOff, store.Launch(id).Outcome);
        Assert.Single(started);
    }

    [Fact]
    public void AScriptThatChangedSinceItWasConfirmedNeedsConfirmingAgain()
    {
        scriptsOn = true;
        var store = Store();
        string script = Write("build.ps1", "Write-Host one");
        store.Add(script);
        long id = store.Load().Single().Id;
        store.Confirm(id, ShortcutTarget.Read(script).Script!);

        File.WriteAllText(script, "Remove-Item -Recurse C:\\Important");
        Assert.Equal(ShortcutOutcome.Changed, store.Launch(id).Outcome);
        Assert.Empty(started);

        store.Confirm(id, ShortcutTarget.Read(script).Script!);
        Assert.Equal(ShortcutOutcome.Opened, store.Launch(id).Outcome);
    }

    [Fact]
    public void ALinkThatBecomesAScriptIsGatedLikeOne()
    {
        scriptsOn = true;
        var store = Store();
        string link = Link("tool.lnk", Touch("notes.txt"));
        store.Add(link);
        long id = store.Load().Single().Id;
        Assert.Equal(ShortcutOutcome.Opened, store.Launch(id).Outcome);

        // Retargeted after it was added: now it runs a command line, which nobody confirmed.
        Link("tool.lnk", Path.Combine(Environment.SystemDirectory, "cmd.exe"), "/c del /q *");
        Assert.Equal(ShortcutOutcome.NeedsConfirm, store.Launch(id).Outcome);
        Link("tool.lnk", Touch("setup.msi"));
        Assert.Equal(ShortcutOutcome.Blocked, store.Launch(id).Outcome);
        Assert.Single(started);
    }

    [Theory]
    [InlineData("build.bat")]
    [InlineData("build.cmd")]
    [InlineData("build.ps1")]
    [InlineData("build.vbs")]
    [InlineData("build.js")]
    [InlineData("build.wsf")]
    public void AFileTileOnAScriptIsGatedAsAScript(string name)
    {
        var store = Store();
        store.Add(Touch(name));
        Assert.Equal(ShortcutOutcome.ScriptsOff, store.Launch(store.Load().Single().Id).Outcome);
        scriptsOn = true;
        Assert.Equal(ShortcutOutcome.NeedsConfirm, store.Launch(store.Load().Single().Id).Outcome);
        Assert.Empty(started);
    }

    [Fact]
    public void AMissingTargetIsReportedNotStarted()
    {
        var store = Store();
        string file = Touch("notes.txt");
        store.Add(file);
        File.Delete(file);
        var tile = store.Load().Single();

        Assert.Equal(ShortcutOutcome.Missing, ShortcutStore.Gate(tile, ShortcutTarget.Read(tile.Target), scriptsOn));
        Assert.Equal(ShortcutOutcome.Missing, store.Launch(tile.Id).Outcome);
        Assert.Equal(ShortcutOutcome.Failed, store.Launch(tile.Id + 100).Outcome);
        Assert.Empty(started);
    }

    [Fact]
    public void TrailingDotsAndSpacesReadAsTheFileWindowsOpens()
    {
        // Windows drops them from a file name, so these all name the files without them.
        string bat = Touch("a.bat");
        foreach (string padded in new[] { bat + ".", bat + " ", bat + ". .", bat + "..." })
        {
            var target = ShortcutTarget.Read(padded);
            Assert.Equal(ShortcutKind.Script, target.Kind);
            Assert.Equal(bat, target.Open);
            Assert.Contains($"\"{bat}\"", target.Script!.Arguments);
        }

        Assert.Equal(ShortcutKind.Script, ShortcutTarget.Read(Link("x.lnk", bat) + ".").Kind);
        Assert.Equal(ShortcutKind.Url, ShortcutTarget.Read(Write("x.url", "[InternetShortcut]\r\nURL=https://example.com/\r\n") + ".").Kind);
        Assert.Equal(ShortcutKind.Blocked, ShortcutTarget.Read(Write("y.url", "[InternetShortcut]\r\nURL=file:///C:/x.bat\r\n") + " ").Kind);
        Assert.Equal(ShortcutKind.Blocked, ShortcutTarget.Read(Touch("setup.msi") + ".").Kind);

        // Stored the way Windows reads it, and gated as the script it is.
        var store = Store();
        Assert.Null(store.Add(bat + "."));
        var tile = Assert.Single(store.Load());
        Assert.Equal(bat, tile.Target);
        Assert.Equal("a.bat", tile.Name);
        Assert.Equal(ShortcutOutcome.ScriptsOff, store.Launch(tile.Id).Outcome);
        Assert.Empty(started);
    }

    [Fact]
    public void ARowWithATrailingDotIsGatedAtTheClick()
    {
        var store = Store();
        Assert.Empty(store.Load());
        using (var db = new TomteDb(DbPath).Open("shortcuts", ["unused: the table exists, so no step runs"]))
        using (var insert = db.CreateCommand())
        {
            insert.CommandText = "INSERT INTO shortcuts_tiles(name, target) VALUES ('a', $bat), ('b', $msi)";
            insert.Parameters.AddWithValue("$bat", Touch("a.bat") + ".");
            insert.Parameters.AddWithValue("$msi", Touch("setup.msi") + " ");
            insert.ExecuteNonQuery();
        }

        var tiles = store.Load();
        Assert.Equal(ShortcutOutcome.ScriptsOff, store.Launch(tiles[0].Id).Outcome);
        Assert.Equal(ShortcutOutcome.Blocked, store.Launch(tiles[1].Id).Outcome);
        Assert.Empty(started);
    }

    [Fact]
    public void ALinkThatPassesArgumentsToAnyProgramIsAScript()
    {
        // Not only shells: these programs run, open or install what their arguments name.
        string bat = Touch("a.bat");
        string system = Environment.SystemDirectory, windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        foreach (var (program, arguments) in new[] { (Path.Combine(system, "pcalua.exe"), $"-a \"{bat}\""), (Path.Combine(system, "control.exe"), "x.cpl"), (Path.Combine(system, "mmc.exe"), "x.msc"), (Path.Combine(windows, "hh.exe"), "x.chm") })
        {
            Assert.True(File.Exists(program), program);
            var target = ShortcutTarget.Read(Link("runner.lnk", program, arguments));
            Assert.Equal(ShortcutKind.Script, target.Kind);
            Assert.Equal(arguments, target.Script!.Arguments);
            // Only the command line is fingerprinted: no file is hashed for these.
            Assert.Null(target.Script.HashedFile);
        }

        Assert.Equal(ShortcutKind.Script, ShortcutTarget.Read(Link("profile.lnk", Touch("browser.exe"), "--profile-directory=Work")).Kind);
        Assert.Equal(bat, ShortcutTarget.Read(Link("bat.lnk", bat)).Script!.HashedFile);
        Assert.Equal(bat, ShortcutTarget.Read(bat).Script!.HashedFile);
    }

    [Fact]
    public void IconsAreReadOnlyFromLocalPathsAndNeverForBlockedOrWebTiles()
    {
        string notes = Touch("notes.txt");
        Assert.Equal(notes, ShortcutTarget.Read(notes).Icon);
        Assert.Equal(dir, ShortcutTarget.Read(dir).Icon);
        string link = Link("notes.lnk", notes);
        Assert.Equal(link, ShortcutTarget.Read(link).Icon);

        Assert.Null(ShortcutTarget.Read(Touch("setup.msi")).Icon);
        Assert.Null(ShortcutTarget.Read("https://example.com/").Icon);
        Assert.Null(ShortcutTarget.Read(Write("site.url", "[InternetShortcut]\r\nURL=https://example.com/\r\nIconFile=\\\\server\\share\\site.ico\r\n")).Icon);
        Assert.Null(ShortcutTarget.Read(Path.Combine(dir, "gone.txt")).Icon);

        // A link whose icon lives on a network path shows its target's icon instead.
        string remote = Link("remote-icon.lnk", notes, icon: @"\\server\share\x.ico,0");
        Assert.Equal(notes, ShortcutTarget.Read(remote).Icon);
        // One backslash and one slash name the same server.
        string mixed = Link("mixed-icon.lnk", notes, icon: @"\/server\share\x.ico,0");
        Assert.Equal(notes, ShortcutTarget.Read(mixed).Icon);
    }

    [Fact]
    public void ALinkThatStartsInANetworkFolderIsBlocked()
    {
        // The shell would start the app with that folder current, which connects to the server.
        string tool = Touch("tool.exe");
        foreach (string folder in new[] { @"\\server\share", @"\/server\share" })
        {
            var target = ShortcutTarget.Read(Link("remote-folder.lnk", tool, folder: folder));
            Assert.Equal(ShortcutKind.Blocked, target.Kind);
            Assert.Null(target.Icon);
        }

        Assert.Equal(ShortcutKind.App, ShortcutTarget.Read(Link("local-folder.lnk", tool, folder: dir)).Kind);
    }

    [Fact]
    public void AShortNameIsReadAsTheLongNameItStandsFor()
    {
        // "tool.wsfx" is a plain file; its short name, where the volume makes one, ends in .WSF, a script type. On a
        // volume with short names turned off the short path is the long one and this only checks the plain read.
        string file = Touch("tool.wsfx");
        dynamic fso = Activator.CreateInstance(Type.GetTypeFromProgID("Scripting.FileSystemObject")!)!;
        string shortPath = fso.GetFile(file).ShortPath;
        var target = ShortcutTarget.Read(shortPath);
        Assert.Equal(ShortcutKind.File, target.Kind);
        Assert.Equal(file, target.Open, ignoreCase: true);
    }

    [Fact]
    public void TheSettingsFileLivesInTheUsersLocalAppData() =>
        // Nothing in the environment moves the script switch or the confirmed scripts somewhere else.
        Assert.Equal(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Tomte", "settings.json"), SettingsStore.DefaultPath);

    [Fact]
    public void WhatIsStoredIsCheckedAgainAtTheClick()
    {
        // Rows written behind the store's back (an edited file, an older build): refused when clicked, not started.
        var store = Store();
        Assert.Empty(store.Load());
        using (var db = new TomteDb(DbPath).Open("shortcuts", ["unused: the table exists, so no step runs"]))
        using (var insert = db.CreateCommand())
        {
            insert.CommandText = "INSERT INTO shortcuts_tiles(name, target) VALUES ('mail', 'mailto:someone@example.com'), ('share', $share), ('settings', 'ms-settings:display')";
            insert.Parameters.AddWithValue("$share", @"\\server\share\tool.exe");
            insert.ExecuteNonQuery();
        }

        Assert.All(store.Load(), tile => Assert.Equal(ShortcutOutcome.Blocked, store.Launch(tile.Id).Outcome));
        Assert.Equal(3, store.Load().Count);
        Assert.Empty(started);
    }

    [Fact]
    public void LaunchByNameOpensTheNamedTileButNeverAScript()
    {
        scriptsOn = true;
        var store = Store();
        store.Add(Touch("notes.txt"));
        string script = Touch("build.bat");
        store.Add(script);
        var bat = store.Load().Single(t => t.Name == "build.bat");
        store.Confirm(bat.Id, ShortcutTarget.Read(script).Script!);

        Assert.True(store.LaunchByName("  NOTES.txt "));
        Assert.False(store.LaunchByName("nothing"));
        Assert.False(store.LaunchByName("build.bat"));
        Assert.Equal(ShortcutKind.File, Assert.Single(started).Kind);
    }

    [Fact]
    public void TheAILayerCannotReachShortcutsOrTheScriptSwitch()
    {
        // Every type and member Tomte.AI uses from another assembly is listed in its metadata.
        using var pe = new PEReader(File.OpenRead(typeof(Tomte.AI.Palette.BuiltInTools).Assembly.Location));
        var metadata = pe.GetMetadataReader();
        var namespaces = metadata.TypeReferences.Select(h => metadata.GetString(metadata.GetTypeReference(h).Namespace)).ToHashSet();
        var members = metadata.MemberReferences.Select(h => metadata.GetString(metadata.GetMemberReference(h).Name)).ToHashSet();

        Assert.Contains("Tomte.Core.Device", namespaces);
        Assert.DoesNotContain("Tomte.Core.Shortcuts", namespaces);
        Assert.DoesNotContain($"set_{nameof(TomteSettings.ScriptShortcuts)}", members);
        Assert.DoesNotContain(BuiltInTools(), name => name.Contains("shortcut") || name.Contains("script"));

        static IEnumerable<string> BuiltInTools() => Tomte.AI.Palette.BuiltInTools.Definitions.Select(d => d.Name);
    }

    [Theory]
    [InlineData(34)]
    [InlineData(43)]
    [InlineData(51)]
    public void TheShellDrawsIconsAtTheTilesPixelSize(int size)
    {
        // 34 DIP at 100%, 125% and 150%.
        foreach (string path in new[] { Path.Combine(Environment.SystemDirectory, "notepad.exe"), dir, Touch("notes.txt"), Touch("build.ps1") })
        {
            var icon = ShellIcon.For(path, size);
            Assert.NotNull(icon);
            Assert.Equal((size, size), (icon.Width, icon.Height));
            Assert.Equal(size * size * 4, icon.Pixels.Length);

            // Premultiplied: no colour above its alpha. An icon has see-through corners and a solid middle.
            var alpha = Enumerable.Range(0, size * size).Select(i => icon.Pixels[i * 4 + 3]).ToArray();
            Assert.All(Enumerable.Range(0, icon.Pixels.Length), i => Assert.True(icon.Pixels[i] <= icon.Pixels[i | 3]));
            Assert.Contains(alpha, a => a == 0);
            Assert.Contains(alpha, a => a == 255);
        }
    }

    [Fact]
    public void AMissingPathHasNoIconAndAWebTileTakesTheBrowsers()
    {
        Assert.Null(ShellIcon.For(Path.Combine(dir, "gone.exe"), 34));
        // A PC with no browser set for https gets a neutral tile instead.
        Assert.True(ShellIcon.ForWeb(43) is null or { Width: 43, Height: 43 });
    }

    [Fact]
    public void TheGridHoldsTwoRowsOfFourAtThePanelSizeAndKeepsClearOfTheCorners()
    {
        var (width, height) = PanelSize.Shortcuts;
        var body = new System.Numerics.Vector2((float)width, (float)height - PanelSpace.TabStripHeight);
        Assert.Equal(2, ShortcutGrid.Rows(body));

        Assert.Equal(new System.Drawing.RectangleF(14, 39, 117, 80), ShortcutGrid.Cell(0, body));
        Assert.Equal(new System.Drawing.RectangleF(139, 127, 117, 80), ShortcutGrid.Cell(5, body));
        // The rounded corners reach 22 DIP in from every edge of the island; the last row ends well above them.
        float corner = (float)IslandGeometry.Panel(width, height).Corner;
        Assert.True(ShortcutGrid.Cell(7, body).Bottom <= body.Y - corner);
        Assert.True(ShortcutGrid.Cell(7, body).Right <= body.X - ShortcutGrid.Inset);

        // Three tiles and the add tile: a press finds its cell, and nothing in a gap, past the add tile or in the head.
        var targets = ShortcutGrid.Targets(Tiles(3), 0, body);
        Assert.Equal("add", HitId(targets, new(ShortcutGrid.Cell(3, body).X + 50, 60)));
        Assert.Equal("tile:2", HitId(targets, new(ShortcutGrid.Cell(1, body).X + 50, 60)));
        Assert.Null(HitId(targets, new(135, 60)));
        Assert.Null(HitId(targets, new(150, 150)));
        Assert.Null(HitId(targets, new(10, 10)));
    }

    [Fact]
    public void TilesAreOneArrowGroupNamedByTheirTargetAndCellsOutOfViewHaveNoHeight()
    {
        var body = new System.Numerics.Vector2(520, 300 - PanelSpace.TabStripHeight);
        var targets = ShortcutGrid.Targets(Tiles(9), 0, body);
        Assert.Equal(10, targets.Count);
        Assert.All(targets, t => Assert.Equal((TargetKind.Tile, 1), (t.Kind, t.Group)));
        Assert.Equal(@"Tile 1 · C:\Tools\tile1.exe", targets[0].Tip);
        Assert.Equal(("add", "Add a shortcut in Settings"), (targets[^1].Id, targets[^1].Tip));

        // Two rows show; the third (tile 9 and the add tile) keeps its place below with no height.
        Assert.Equal(ShortcutGrid.Cell(4, body), targets[4].Bounds);
        Assert.Equal(0, targets[8].Bounds.Height);
        Assert.Equal(ShortcutGrid.Cell(0, body).Y + 2 * (ShortcutGrid.TileHeight + ShortcutGrid.Gap), targets[8].Bounds.Y);
        Assert.Null(HitId(targets, new(targets[8].Bounds.X + 10, targets[8].Bounds.Y)));
        // Down from the second row finds it; focus there scrolls one row, and then the first row has no height.
        Assert.Equal(8, Target.Arrow(targets, 4, new(0, 1)));
        Assert.Equal(1, ShortcutGrid.FirstRowFor(8, 0, 9, body));
        Assert.Equal(0, ShortcutGrid.FirstRowFor(0, 1, 9, body));
        Assert.Equal(1, ShortcutGrid.FirstRowFor(5, 1, 9, body));
        var scrolled = ShortcutGrid.Targets(Tiles(9), 1, body);
        Assert.Equal(ShortcutGrid.Cell(4, body), scrolled[8].Bounds);
        Assert.Equal(0, scrolled[0].Bounds.Height);
    }

    private static Shortcut[] Tiles(int count) => [.. Enumerable.Range(1, count).Select(i => new Shortcut(i, $"Tile {i}", $@"C:\Tools\tile{i}.exe", null))];

    private static string? HitId(IReadOnlyList<Target> targets, System.Numerics.Vector2 at) =>
        Target.Hit([(targets, p => p)], at) is { } hit ? targets[hit.Index].Id : null;

    [Fact]
    public void TheWheelScrollsByRowsUntilTheAddTileIsInTheBottomRow()
    {
        var body = new System.Numerics.Vector2(520, 300 - PanelSpace.TabStripHeight);
        // Seven tiles and the add tile fill the two rows: nothing to scroll.
        Assert.Equal(0, ShortcutGrid.MaxFirstRow(7, body));
        Assert.Equal(0, ShortcutGrid.FirstRow(5, 7, body));
        Assert.Equal((0, 8), ShortcutGrid.Visible(0, 7, body));
        Assert.Equal((0, 4), ShortcutGrid.Visible(0, 3, body));

        // A dozen tiles and the add tile take four rows; the first row runs 0 to 2.
        Assert.Equal(2, ShortcutGrid.MaxFirstRow(12, body));
        Assert.Equal(0, ShortcutGrid.FirstRow(-3, 12, body));
        Assert.Equal(2, ShortcutGrid.FirstRow(9, 12, body));
        Assert.Equal((4, 8), ShortcutGrid.Visible(1, 12, body));
        Assert.Equal((8, 5), ShortcutGrid.Visible(2, 12, body));

        // Tiles removed while scrolled down: the first row comes back to what there is.
        Assert.Equal(0, ShortcutGrid.FirstRow(2, 4, body));
        Assert.Equal(0, ShortcutGrid.MaxFirstRow(0, body));
    }

    [Theory]
    [InlineData(1.0, 34)]
    [InlineData(1.25, 43)]
    [InlineData(1.5, 51)]
    public void IconsSitOnWholePixels(double scale, int pixels)
    {
        Assert.Equal(pixels, ShortcutGrid.IconPixels(scale));
        // The body starts under the 42 DIP tab strip, 52.5 pixels down at 125%; the icons snap to the island's pixels.
        const float top = PanelSpace.TabStripHeight;
        var body = new System.Numerics.Vector2(520, 258);
        for (int i = 0; i < 8; i++)
        {
            var icon = ShortcutGrid.Icon(ShortcutGrid.Cell(i, body), scale, top);
            Assert.Equal(pixels, icon.Width * scale, 3);
            Assert.Equal(Math.Round(icon.X * scale), icon.X * scale, 3);
            Assert.Equal(Math.Round((top + icon.Y) * scale), (top + icon.Y) * scale, 3);
        }
    }

    [Fact]
    public void AStartAppReadsAsWhatItsEntryStarts()
    {
        const string app = @"shell:AppsFolder\Vendor.App_0000000000000!App";
        var byShell = new ShortcutTarget(ShortcutKind.App, app, Icon: app);
        // Opened as Start opens it, by its ID: a packaged app, a shell object such as File Explorer, a program with
        // arguments (Task Manager, a browser web app), a game through its launcher's scheme, a page, a folder, and
        // Windows' own consoles.
        string exe = Touch("tool.exe"), bat = Touch("build.bat");
        Assert.Equal(byShell, ShortcutTarget.ReadApp(app, ("", "")));
        Assert.Equal(byShell, ShortcutTarget.ReadApp(app, ("::{52205FD8-5DFB-447D-801A-D0B52F2E83E1}", "")));
        Assert.Equal(byShell, ShortcutTarget.ReadApp(app, (exe, "--profile 2")));
        Assert.Equal(byShell, ShortcutTarget.ReadApp(app, ("steam://rungameid/431960", "")));
        Assert.Equal(byShell, ShortcutTarget.ReadApp(app, ("https://example.com/", "")));
        Assert.Equal(byShell, ShortcutTarget.ReadApp(app, (dir, "")));
        Assert.Equal(byShell, ShortcutTarget.ReadApp(app, (Path.Combine(Environment.SystemDirectory, "services.msc"), "")));
        Assert.Null(ShortcutStore.Gate(new Shortcut(1, "tool", app, null), ShortcutTarget.ReadApp(app, (exe, "--profile 2")), scriptsOn: false));

        // A desktop app with no arguments is its exe, read now: open or focus it, as an .exe tile does.
        Assert.Equal(new ShortcutTarget(ShortcutKind.App, exe, Icon: exe), ShortcutTarget.ReadApp(app, (exe, "")));
        Assert.True(ShortcutTarget.ReadApp(app, (Path.Combine(dir, "gone.exe"), "")).Missing);

        // Any other file behind an entry reads as it would if browsed to: anyone running as the user can write a
        // Start menu link, so a script stays gated and an installer, a console elsewhere or a .hta stays refused.
        var script = ShortcutTarget.ReadApp(app, (bat, "release"));
        Assert.Equal(ShortcutKind.Script, script.Kind);
        Assert.Equal($"/d /s /c \"\"{bat}\" release\"", script.Script!.Arguments);
        Assert.Equal(ShortcutOutcome.ScriptsOff, ShortcutStore.Gate(new Shortcut(1, "build", app, null), script, scriptsOn: false));
        Assert.Equal(ShortcutKind.Script, ShortcutTarget.ReadApp(app, (Touch("tidy.ps1"), "")).Kind);
        Assert.Equal(ShortcutKind.Blocked, ShortcutTarget.ReadApp(app, (Touch("setup.msi"), "")).Kind);
        Assert.Equal(ShortcutKind.Blocked, ShortcutTarget.ReadApp(app, (Touch("tweak.reg"), "/s")).Kind);
        Assert.Equal(ShortcutKind.Blocked, ShortcutTarget.ReadApp(app, (Touch("page.hta"), "")).Kind);
        Assert.Equal(ShortcutKind.Blocked, ShortcutTarget.ReadApp(app, (Touch("console.msc"), "")).Kind);
        Assert.Equal(ShortcutKind.File, ShortcutTarget.ReadApp(app, (Touch("notes.txt"), "")).Kind);
        // A path no tile may hold is refused, not opened by ID: a hidden stream, or one past the length limit.
        Assert.Equal(ShortcutKind.Blocked, ShortcutTarget.ReadApp(app, (@"C:\x\a.txt:b.bat", "")).Kind);
        Assert.Equal(ShortcutKind.Blocked, ShortcutTarget.ReadApp(app, (@"C:\" + new string('a', 2100) + ".exe", "")).Kind);

        // A network path stays refused; an app that is gone is missing, never thrown.
        Assert.Equal(ShortcutKind.Blocked, ShortcutTarget.ReadApp(app, (@"\\server\share\app.exe", "")).Kind);
        Assert.Equal(ShortcutKind.Blocked, ShortcutTarget.ReadApp(app, (@"\\server\share\app.exe", "-x")).Kind);
        Assert.Equal(new ShortcutTarget(ShortcutKind.App, app, Missing: true), ShortcutTarget.ReadApp(app, null));
        Assert.Equal(new ShortcutTarget(ShortcutKind.App, app, Missing: true), ShortcutTarget.Read(app));
        // The shell resolves only the folder's own entries: This PC or a path behind the prefix is no app.
        Assert.True(ShortcutTarget.Read(@"shell:AppsFolder\::{20D04FE0-3AEA-1069-A2D8-08002B30309D}").Missing);
        Assert.True(ShortcutTarget.Read(@"shell:AppsFolder\shell:Downloads").Missing);
    }

    [Fact]
    public void AStartAppIsStoredByItsIdAndStartedByTheLauncher()
    {
        var store = Store();
        Assert.Equal("There is nothing at shell:AppsFolder\\Vendor.Gone_0000000000000!App.", store.Add(@"shell:AppsFolder\Vendor.Gone_0000000000000!App", "Gone"));

        // Read from the shell, not launched: the recorder takes the start. A Start list with no app has nothing to add.
        var app = AppList.Load().FirstOrDefault(a => ShortcutTarget.Read(a.Target) is { Kind: ShortcutKind.App, Missing: false });
        if (app is null)
        {
            return;
        }

        Assert.Null(store.Add(app.Target, app.Name));
        var tile = Assert.Single(store.Load());
        Assert.Equal((app.Name, app.Target), (tile.Name, tile.Target));

        Assert.Equal(ShortcutOutcome.Opened, store.Launch(tile.Id).Outcome);
        Assert.Equal(ShortcutTarget.Read(app.Target), Assert.Single(started));
        Assert.True(store.LaunchByName(app.Name.ToUpperInvariant()));
        Assert.True(Apps.OpenApp(app.Target, dryRun: true).Ok);
    }

    [Fact]
    public void OpenAppTakesOnlyAnInstalledAppsId()
    {
        Assert.Equal("That is not an installed app.", Apps.OpenApp(@"shell:AppsFolder\Vendor.Gone_0000000000000!App", dryRun: true).Message);
        Assert.False(Apps.OpenApp(@"shell:AppsFolder\::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", dryRun: true).Ok);
        Assert.NotNull(Apps.ValidateAppTarget(@"shell:AppsFolder\..\Startup"));
        Assert.NotNull(Apps.ValidateAppTarget(@"shell:AppsFolder\"));
        // An ID may hold a scheme of its own, as a Steam game's does.
        Assert.Null(Apps.ValidateAppTarget(@"shell:AppsFolder\steam://rungameid/431960"));
    }

    [Fact]
    public void TheAppListIsStartsAppsSortedByName()
    {
        var apps = AppList.Load();
        Assert.NotEmpty(apps);
        Assert.All(apps, a => Assert.Null(ShortcutTarget.Refuse(a.Target)));
        Assert.All(apps.Zip(apps.Skip(1)), pair => Assert.True(StringComparer.CurrentCultureIgnoreCase.Compare(pair.First.Name, pair.Second.Name) <= 0));
    }

    [Fact]
    public void ShellItemsBecomeAppsWithANameAndAnId()
    {
        var apps = AppList.Sorted([(" Zed ", "Zed.App"), ("beta", "B!App"), ("", "Nameless!App"), ("Quoted", "Bad\"Id"), ("Alpha", "B!App"), ("alpha", "A!App")]);

        Assert.Equal(["alpha", "Alpha", "beta", "Zed"], apps.Select(a => a.Name));
        Assert.Equal(["A!App", "B!App", "B!App", "Zed.App"], apps.Select(a => a.Id));
        Assert.Equal(@"shell:AppsFolder\A!App", apps[0].Target);
    }

    [Fact]
    public void TheSearchBoxKeepsAppsWhoseNameHoldsTheText()
    {
        var apps = AppList.Sorted([("Calculator", "C!App"), ("Visual Studio Code", "Code"), ("Code Composer", "CCS"), ("Clock", "K!App")]);

        Assert.Equal(["Code Composer", "Visual Studio Code"], AppList.Filter(apps, "code").Select(a => a.Name));
        Assert.Equal(["Calculator"], AppList.Filter(apps, "  CALC ").Select(a => a.Name));
        Assert.Equal(4, AppList.Filter(apps, " ").Count());
        Assert.Empty(AppList.Filter(apps, "xyz"));
    }

    private ShortcutStore Store() => new(new TomteDb(DbPath), () => scriptsOn, Record);

    private ActionResult Record(ShortcutTarget target)
    {
        started.Add(target);
        return ActionResult.Done("recorded");
    }

    private string Touch(string name) => Write(name, "");

    private string Write(string name, string content)
    {
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private string Link(string name, string target, string arguments = "", string? icon = null, string? folder = null)
    {
        string path = Path.Combine(dir, name);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.Arguments = arguments;
        if (icon is not null)
        {
            link.IconLocation = icon;
        }

        if (folder is not null)
        {
            link.WorkingDirectory = folder;
        }

        link.Save();
        return path;
    }
}
