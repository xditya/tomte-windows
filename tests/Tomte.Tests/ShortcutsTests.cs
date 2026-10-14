using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Tomte.Core;
using Tomte.Core.Device;
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
    [InlineData("app.appinstaller")]
    [InlineData("app.msix")]
    [InlineData("old.com")]
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
        Assert.Equal(ShortcutKind.App, ShortcutTarget.Read(Link("tool.lnk", Touch("tool.exe"), "--profile two")).Kind);
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
    [InlineData("notes.txt")]
    [InlineData(@"C:notes.txt")]
    [InlineData(@"C:\notes.txt:hidden")]
    [InlineData("C:\\a\"b")]
    [InlineData("")]
    public void TargetsThatAreNotAFullLocalPathOrAWebAddressAreRefused(string target)
    {
        Assert.NotNull(ShortcutTarget.Refuse(target));
        Assert.Equal(ShortcutKind.Blocked, ShortcutTarget.Read(target).Kind);
    }

    [Theory]
    [InlineData("https://example.com")]
    [InlineData("http://localhost:8080/app")]
    [InlineData(@"C:\Tools\app.exe")]
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
    public void TheGridHoldsTwoRowsOfFourAtTheSpecSizeAndKeepsClearOfTheCorners()
    {
        var (width, height) = PanelSize.Shortcuts;
        var body = new System.Numerics.Vector2((float)width, (float)height - 42);
        Assert.Equal(2, ShortcutGrid.Rows(body));
        Assert.Equal(7, ShortcutGrid.Shown(12, body));
        Assert.Equal(3, ShortcutGrid.Shown(3, body));

        Assert.Equal(new System.Drawing.RectangleF(14, 39, 117, 80), ShortcutGrid.Cell(0, body));
        Assert.Equal(new System.Drawing.RectangleF(139, 127, 117, 80), ShortcutGrid.Cell(5, body));
        // The rounded corners reach 22 DIP in from every edge of the island; the last row ends well above them.
        float corner = (float)IslandGeometry.Panel(width, height).Corner;
        Assert.True(ShortcutGrid.Cell(7, body).Bottom <= body.Y - corner);
        Assert.True(ShortcutGrid.Cell(7, body).Right <= body.X - ShortcutGrid.Inset);

        Assert.Equal(3, ShortcutGrid.At(new(ShortcutGrid.Cell(3, body).X + 50, 60), body, 4));
        Assert.Null(ShortcutGrid.At(new(135, 60), body, 8));
        Assert.Null(ShortcutGrid.At(new(150, 150), body, 5));
        Assert.Null(ShortcutGrid.At(new(10, 10), body, 8));
    }

    [Theory]
    [InlineData(1.0, 34)]
    [InlineData(1.25, 43)]
    [InlineData(1.5, 51)]
    public void IconsSitOnWholePixels(double scale, int pixels)
    {
        Assert.Equal(pixels, ShortcutGrid.IconPixels(scale));
        // The body starts under the 42 DIP tab strip, 52.5 pixels down at 125%; the icons snap to the island's pixels.
        const float top = 42;
        var body = new System.Numerics.Vector2(520, 258);
        for (int i = 0; i < 8; i++)
        {
            var icon = ShortcutGrid.Icon(ShortcutGrid.Cell(i, body), scale, top);
            Assert.Equal(pixels, icon.Width * scale, 3);
            Assert.Equal(Math.Round(icon.X * scale), icon.X * scale, 3);
            Assert.Equal(Math.Round((top + icon.Y) * scale), (top + icon.Y) * scale, 3);
        }
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

    private string Link(string name, string target, string arguments = "")
    {
        string path = Path.Combine(dir, name);
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell")!)!;
        dynamic link = shell.CreateShortcut(path);
        link.TargetPath = target;
        link.Arguments = arguments;
        link.Save();
        return path;
    }
}
