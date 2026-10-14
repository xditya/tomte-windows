using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.UI.Shell;

namespace Tomte.Core.Shortcuts;

public enum ShortcutKind
{
    App,
    File,
    Folder,
    Url,
    Script,

    /// <summary>Something Tomte never starts from a tile: an installer, a registry file, a link to a link, and the like.</summary>
    Blocked,
}

/// <summary>
/// Exactly what a script tile runs, as the confirmation shows it. The fingerprint covers the command and the
/// script file's bytes, so a tile whose script changed since it was confirmed has to be confirmed again.
/// </summary>
public sealed record ScriptCommand(string Exe, string Arguments, string Folder, string Fingerprint)
{
    public string Text => $"\"{Exe}\" {Arguments}";
}

/// <summary>
/// What a tile's target is right now, read from the disk at the moment of asking: a link that was retargeted
/// reads as what it points at today. <see cref="Open"/> is what gets started (the path, the link itself, or the
/// web address); <see cref="Script"/> is set for scripts only. Reading touches the disk and the shell, so it runs
/// on a worker thread.
/// </summary>
public sealed record ShortcutTarget(ShortcutKind Kind, string Open, bool Missing = false, ScriptCommand? Script = null, string? Problem = null)
{
    // Script types and the Windows host that runs each, with the script's path and any link arguments after it.
    private static readonly Dictionary<string, Func<string, string, (string Exe, string Arguments)>> Hosts = new(StringComparer.OrdinalIgnoreCase)
    {
        // /d skips cmd's AutoRun commands; /s /c strips the outer pair of quotes, so the quoted path keeps & and ^ literal.
        [".bat"] = (script, tail) => (InSystem32("cmd.exe"), $"/d /s /c \"\"{script}\"{tail}\""),
        [".cmd"] = (script, tail) => (InSystem32("cmd.exe"), $"/d /s /c \"\"{script}\"{tail}\""),
        // The execution policy is the user's (or their organisation's) and is not overridden.
        [".ps1"] = (script, tail) => (InSystem32(@"WindowsPowerShell\v1.0\powershell.exe"), $"-File \"{script}\"{tail}"),
        [".vbs"] = WindowsScriptHost,
        [".vbe"] = WindowsScriptHost,
        [".js"] = WindowsScriptHost,
        [".jse"] = WindowsScriptHost,
        [".wsf"] = WindowsScriptHost,
    };

    // A link to one of these with arguments runs a command line, so it is a script. With no arguments it just opens the
    // program (a console, Explorer) and is an app.
    // a named list; a program not on it that runs whatever its arguments say still passes as an app.
    private static readonly HashSet<string> CommandRunners = new(StringComparer.OrdinalIgnoreCase)
    {
        "cmd", "powershell", "pwsh", "wscript", "cscript", "mshta", "rundll32", "regsvr32", "wsl", "bash", "python",
        "pythonw", "py", "pyw", "node", "java", "javaw", "msiexec", "explorer", "conhost", "forfiles", "schtasks", "reg",
        "wmic", "certutil", "msbuild", "installutil", "cmstp",
    };

    // Types that install or run something when opened. Windows 11 does not list the first row as dangerous (checked on
    // 26200); the second row it does, and is here so the answer does not depend on the PC.
    private static readonly HashSet<string> AlsoBlocked = new(StringComparer.OrdinalIgnoreCase)
    {
        ".appinstaller", ".appx", ".appxbundle", ".msix", ".msixbundle", ".application", ".jar", ".py", ".pyw", ".sh", ".xll", ".diagcab", ".search-ms", ".settingcontent-ms",
        ".msi", ".msp", ".reg", ".hta", ".scr", ".com", ".pif", ".cpl", ".msc", ".inf", ".chm",
    };

    /// <summary>
    /// Null when the text can be a tile's target; otherwise why not, in words for the user. Only http and https
    /// web addresses and full local paths qualify. Any other link (mailto:, ms-settings:, file:, a custom
    /// scheme) hands its text to whatever program claimed the scheme, which is a command line by another name.
    /// A network path makes Windows connect to that server, offering the user's sign-in, each time the
    /// panel reads the tile's icon, and a share that is offline stalls for many seconds.
    /// </summary>
    public static string? Refuse(string target)
    {
        if (string.IsNullOrWhiteSpace(target))
        {
            return "Choose an app, a file, a folder or a web address.";
        }

        if (target.Length > 2048)
        {
            return "That is too long for a shortcut.";
        }

        if (target.IndexOfAny(['"', '\r', '\n', '\0']) >= 0)
        {
            return "A shortcut cannot hold quotes or line breaks.";
        }

        int colon = target.IndexOf(':');
        if (colon >= 0 && colon != 1)
        {
            return WebAddress(target) is null ? "Only http and https web addresses open from a tile." : null;
        }

        // A second colon names a hidden stream inside a file, not a file.
        if (target.Length > 2 && target.IndexOf(':', 2) >= 0)
        {
            return "A path holds a colon only after the drive letter.";
        }

        if (target.StartsWith(@"\\", StringComparison.Ordinal) || target.StartsWith("//", StringComparison.Ordinal))
        {
            return "Tomte does not open shortcuts on a network path.";
        }

        return Path.IsPathFullyQualified(target) ? null : @"Use a full path, such as C:\Tools\app.exe.";
    }

    /// <summary>The web address as Tomte opens it, or null when the text is not an http or https address.</summary>
    public static Uri? WebAddress(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0 ? uri : null;

    /// <summary>What the target is now. Never throws: a target that cannot be read is Missing or Blocked.</summary>
    public static ShortcutTarget Read(string target)
    {
        if (Refuse(target) is { } why)
        {
            return new(ShortcutKind.Blocked, target, Problem: why);
        }

        if (WebAddress(target) is { } uri)
        {
            return new(ShortcutKind.Url, uri.AbsoluteUri);
        }

        try
        {
            if (Directory.Exists(target))
            {
                return new(ShortcutKind.Folder, target);
            }

            if (!File.Exists(target))
            {
                return new(Guess(target), target, Missing: true);
            }

            return Path.GetExtension(target).ToLowerInvariant() switch
            {
                ".lnk" => ReadLink(target),
                ".url" => ReadInternetShortcut(target),
                _ => ByType(target, target, "", null),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException)
        {
            return new(ShortcutKind.Blocked, target, Problem: $"Tomte could not read {target}: {e.Message}");
        }
    }

    /// <summary>The tile's name when it is added: the file or folder name, an app without .exe, a web address's host.</summary>
    public static string NameFor(string target)
    {
        if (WebAddress(target) is { } uri)
        {
            return uri.Host.StartsWith("www.", StringComparison.OrdinalIgnoreCase) ? uri.Host[4..] : uri.Host;
        }

        string trimmed = target.TrimEnd('\\', '/');
        string name = Path.GetExtension(trimmed).ToLowerInvariant() is ".exe" or ".lnk" or ".url"
            ? Path.GetFileNameWithoutExtension(trimmed)
            : Path.GetFileName(trimmed);
        // A drive root has no name of its own.
        return name.Length > 0 ? name : trimmed;
    }

    private static Func<string, string, (string, string)> WindowsScriptHost => (script, tail) => (InSystem32("wscript.exe"), $"\"{script}\"{tail}");

    private static string InSystem32(string relative) => Path.Combine(Environment.SystemDirectory, relative);

    // For a target that is gone: what it looked like, so the tile keeps its place in the right style.
    private static ShortcutKind Guess(string path) =>
        Hosts.ContainsKey(Path.GetExtension(path)) ? ShortcutKind.Script
        : Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? ShortcutKind.App
        : ShortcutKind.File;

    /// <summary>A file that exists, by its type. <paramref name="open"/> is what starts it: the file, or the link pointing at it.</summary>
    private static ShortcutTarget ByType(string file, string open, string arguments, string? folder)
    {
        string ext = Path.GetExtension(file);
        if (Hosts.TryGetValue(ext, out var host))
        {
            // cmd expands %name% even inside quotes, so a path holding % could run something else.
            if (ext is ".bat" or ".cmd" && (file.Contains('%') || arguments.Contains('%')))
            {
                return new(ShortcutKind.Blocked, open, Problem: "Tomte does not run a batch file whose path or arguments hold a % sign.");
            }

            var (exe, args) = host(file, arguments.Length > 0 ? " " + arguments : "");
            return new(ShortcutKind.Script, open, Script: Command(exe, args, folder ?? Path.GetDirectoryName(file)!, file));
        }

        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            return arguments.Length > 0 && CommandRunners.Contains(Path.GetFileNameWithoutExtension(file))
                ? new(ShortcutKind.Script, open, Script: Command(file, arguments, folder ?? Path.GetDirectoryName(file)!, null))
                : new(ShortcutKind.App, open);
        }

        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            return new(ShortcutKind.Blocked, open, Problem: "Tomte does not follow a shortcut to another shortcut.");
        }

        if (AlsoBlocked.Contains(ext) || (ext.Length > 1 && PInvoke.AssocIsDangerous(ext)))
        {
            return new(ShortcutKind.Blocked, open, Problem: $"Tomte does not open {ext} files from a tile: opening one installs or runs something.");
        }

        return new(ShortcutKind.File, open);
    }

    /// <summary>A .lnk is what it points at. The link itself is what starts, unless it is a script, which runs as the command shown.</summary>
    private static ShortcutTarget ReadLink(string link)
    {
        var (path, arguments, folder) = Link(link);
        if (path.Length == 0)
        {
            // No file behind it: an app from the Start menu, This PC, a Control Panel page. The shell opens those.
            return new(ShortcutKind.App, link);
        }

        if (path.StartsWith(@"\\", StringComparison.Ordinal))
        {
            return new(ShortcutKind.Blocked, link, Problem: "This shortcut points at a network path, which Tomte does not open.");
        }

        if (Directory.Exists(path))
        {
            return new(ShortcutKind.Folder, link);
        }

        if (!File.Exists(path))
        {
            return new(Guess(path), link, Missing: true);
        }

        return ByType(path, link, arguments, folder.Length > 0 ? folder : null);
    }

    // [InternetShortcut] URL=...: opens like a web tile when the address is http or https, never otherwise.
    private static ShortcutTarget ReadInternetShortcut(string file)
    {
        string? line = File.ReadLines(file).FirstOrDefault(l => l.StartsWith("URL=", StringComparison.OrdinalIgnoreCase));
        return line is not null && WebAddress(line[4..].Trim()) is { } uri
            ? new(ShortcutKind.Url, uri.AbsoluteUri)
            : new(ShortcutKind.Blocked, file, Problem: "This link file opens something other than a web page.");
    }

    private static ScriptCommand Command(string exe, string arguments, string folder, string? script)
    {
        // The script's bytes, so an edited script reads as a different command.
        string content = script is null ? "" : Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(script)));
        string fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes($"{exe}\n{arguments}\n{folder}\n{content}")));
        return new(exe, arguments, folder, fingerprint);
    }

    /// <summary>Target path, arguments and working folder of a .lnk, read without resolving (no search, no network, no UI).</summary>
    private static unsafe (string Path, string Arguments, string Folder) Link(string file)
    {
        var link = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)link).Load(file, STGM.STGM_READ);
            const int Capacity = 2048;
            char* buffer = stackalloc char[Capacity];
            buffer[0] = '\0';
            // An item with no file system path answers S_FALSE and leaves the buffer empty.
            link.GetPath(new PWSTR(buffer), Capacity, null, 0);
            string path = new(buffer);
            buffer[0] = '\0';
            link.GetArguments(new PWSTR(buffer), Capacity);
            string arguments = new string(buffer).Trim();
            buffer[0] = '\0';
            link.GetWorkingDirectory(new PWSTR(buffer), Capacity);
            return (path, arguments, new string(buffer));
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }
}
