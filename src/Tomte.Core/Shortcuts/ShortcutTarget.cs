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
/// Exactly what a script tile runs, as the confirmation shows it. The fingerprint covers the command, the working
/// folder and, when the command runs a script file, that file's bytes (<see cref="HashedFile"/>), so a tile whose
/// script changed since it was confirmed has to be confirmed again. A link that passes arguments to a program has
/// no hashed file: only its command line is fingerprinted, not any file the arguments name.
/// </summary>
public sealed record ScriptCommand(string Exe, string Arguments, string Folder, string Fingerprint, string? HashedFile)
{
    public string Text => $"\"{Exe}\" {Arguments}";
}

/// <summary>
/// What a tile's target is right now, read from the disk at the moment of asking: a link that was retargeted
/// reads as what it points at today. <see cref="Open"/> is what gets started (the path, the link itself, or the
/// web address); <see cref="Script"/> is set for scripts only. <see cref="Icon"/> is the local path the shell may
/// read the tile's icon from, or null for none (a web page takes the browser's icon). Reading touches the disk and
/// the shell, so it runs on a worker thread.
/// </summary>
public sealed record ShortcutTarget(ShortcutKind Kind, string Open, bool Missing = false, ScriptCommand? Script = null, string? Problem = null, string? Icon = null)
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

    // Types that install or run something when opened. Windows 11 does not list the first row as dangerous (checked on
    // 26200); the second row it does, and is here so the answer does not depend on the PC.
    private static readonly HashSet<string> AlsoBlocked = new(StringComparer.OrdinalIgnoreCase)
    {
        ".appinstaller", ".appx", ".appxbundle", ".msix", ".msixbundle", ".application", ".jar", ".py", ".pyw", ".sh", ".xll", ".diagcab", ".search-ms", ".searchConnector-ms", ".settingcontent-ms", ".themepack",
        ".msi", ".msp", ".reg", ".hta", ".scr", ".com", ".pif", ".cpl", ".msc", ".inf", ".chm",
    };

    /// <summary>
    /// Null when the text can be a tile's target; otherwise why not, in words for the user. Only http and https
    /// web addresses and full local paths qualify. Any other link (mailto:, ms-settings:, file:, a custom
    /// scheme) hands its text to whatever program claimed the scheme, which is a command line by another name.
    /// A network path or a mapped network drive makes Windows connect to that server, offering the user's
    /// sign-in, each time the panel reads the tile's icon, and a share that is offline stalls for many seconds.
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

        if (!Path.IsPathFullyQualified(target))
        {
            return @"Use a full path, such as C:\Tools\app.exe.";
        }

        return OnNetworkDrive(target) ? "Tomte does not open shortcuts on a network drive." : null;
    }

    /// <summary>The web address as Tomte opens it, or null when the text is not an http or https address.</summary>
    public static Uri? WebAddress(string text) =>
        Uri.TryCreate(text, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https" && uri.Host.Length > 0 ? uri : null;

    /// <summary>
    /// The text as a tile stores it: a web address as it opens, a path as Windows reads it. Windows drops trailing
    /// dots and spaces from a file name, so "a.bat." and "a.bat " are a.bat; reading the text as typed would take
    /// a batch file for a file with no type.
    /// </summary>
    public static string Normalize(string target) =>
        Refuse(target) is not null ? target
        : WebAddress(target) is { } uri ? uri.AbsoluteUri
        : Path.GetFullPath(target);

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

        string path = Path.GetFullPath(target);
        try
        {
            if (Directory.Exists(path))
            {
                return new(ShortcutKind.Folder, path, Icon: path);
            }

            if (!File.Exists(path))
            {
                return new(Guess(path), path, Missing: true);
            }

            return Path.GetExtension(path).ToLowerInvariant() switch
            {
                ".lnk" => ReadLink(path),
                ".url" => ReadInternetShortcut(path),
                _ => ByType(path, path, "", null, path),
            };
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or COMException)
        {
            return new(ShortcutKind.Blocked, path, Problem: $"Tomte could not read {path}: {e.Message}");
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

    private static bool OnNetworkDrive(string path)
    {
        try
        {
            return new DriveInfo(Path.GetPathRoot(path)!).DriveType == DriveType.Network;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static bool OnNetwork(string path) => path.StartsWith(@"\\", StringComparison.Ordinal) || (Path.IsPathFullyQualified(path) && OnNetworkDrive(path));

    // For a target that is gone: what it looked like, so the tile keeps its place in the right style.
    private static ShortcutKind Guess(string path) =>
        Hosts.ContainsKey(Path.GetExtension(path)) ? ShortcutKind.Script
        : Path.GetExtension(path).Equals(".exe", StringComparison.OrdinalIgnoreCase) ? ShortcutKind.App
        : ShortcutKind.File;

    /// <summary>A file that exists, by its type. <paramref name="open"/> is what starts it: the file, or the link pointing at it.</summary>
    private static ShortcutTarget ByType(string file, string open, string arguments, string? folder, string? icon)
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
            return new(ShortcutKind.Script, open, Script: Command(exe, args, folder ?? Path.GetDirectoryName(file)!, file), Icon: icon);
        }

        if (ext.Equals(".exe", StringComparison.OrdinalIgnoreCase))
        {
            // Arguments make a link a command line, whatever the program: pcalua, control and mmc run what they name.
            return arguments.Length > 0
                ? new(ShortcutKind.Script, open, Script: Command(file, arguments, folder ?? Path.GetDirectoryName(file)!, null), Icon: icon)
                : new(ShortcutKind.App, open, Icon: icon);
        }

        if (ext.Equals(".lnk", StringComparison.OrdinalIgnoreCase) || ext.Equals(".url", StringComparison.OrdinalIgnoreCase))
        {
            return new(ShortcutKind.Blocked, open, Problem: "Tomte does not follow a shortcut to another shortcut.");
        }

        if (AlsoBlocked.Contains(ext) || (ext.Length > 1 && PInvoke.AssocIsDangerous(ext)))
        {
            return new(ShortcutKind.Blocked, open, Problem: $"Tomte does not open {ext} files from a tile: opening one installs or runs something.");
        }

        return new(ShortcutKind.File, open, Icon: icon);
    }

    /// <summary>A .lnk is what it points at. The link itself is what starts, unless it is a script, which runs as the command shown.</summary>
    private static ShortcutTarget ReadLink(string link)
    {
        var (raw, arguments, folder, iconFile) = Link(link);
        // A link's own icon is read from wherever it says; one on a network path is not, and the target's is used.
        bool remoteIcon = iconFile.Length > 0 && OnNetwork(Environment.ExpandEnvironmentVariables(iconFile));
        if (raw.Length == 0)
        {
            // No file behind it: an app from the Start menu, This PC, a Control Panel page. The shell opens those.
            return new(ShortcutKind.App, link, Icon: remoteIcon ? null : link);
        }

        if (Refuse(raw) is { } why)
        {
            return new(ShortcutKind.Blocked, link, Problem: $"This shortcut points at {raw}. {why}");
        }

        string path = Path.GetFullPath(raw);
        string icon = remoteIcon ? path : link;
        if (Directory.Exists(path))
        {
            return new(ShortcutKind.Folder, link, Icon: icon);
        }

        if (!File.Exists(path))
        {
            return new(Guess(path), link, Missing: true);
        }

        return ByType(path, link, arguments, folder.Length > 0 ? folder : null, icon);
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
        return new(exe, arguments, folder, fingerprint, script);
    }

    /// <summary>Target path, arguments, working folder and icon file of a .lnk, read without resolving (no search, no network, no UI).</summary>
    private static unsafe (string Path, string Arguments, string Folder, string Icon) Link(string file)
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
            string folder = new(buffer);
            buffer[0] = '\0';
            link.GetIconLocation(new PWSTR(buffer), Capacity, out _);
            return (path, arguments, folder, new string(buffer));
        }
        finally
        {
            Marshal.FinalReleaseComObject(link);
        }
    }
}
