using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32;

namespace Tomte.Core.Search;

/// <summary>Where file search comes from now.</summary>
public enum FileSource
{
    /// <summary>Everything is running: every drive, instantly.</summary>
    Everything,

    /// <summary>Everything is installed with its service, so Tomte can start it without asking for admin rights.</summary>
    EverythingStopped,

    /// <summary>Everything is installed without its service: starting it may ask for admin rights, so Tomte leaves it to the user.</summary>
    EverythingManual,

    /// <summary>Everything is not installed: the Windows Search index.</summary>
    Index,
}

/// <summary>
/// Getting Everything (voidtools) for whole-disk search: finding where it is installed, starting it in the background,
/// and installing it with winget when the user asks. Tomte never bundles it. Reads the registry and the disk, and the
/// install waits for winget: worker threads only.
/// </summary>
public static class EverythingSetup
{
    public const string PackageId = "voidtools.Everything";
    public const string DownloadPage = "https://www.voidtools.com/downloads/";

    // winget's "already installed" and "no newer version" answers, which leave Everything there as asked.
    private const int AlreadyInstalled = unchecked((int)0x8A150061), NoUpgrade = unchecked((int)0x8A15002B);

    private const string Run = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const int Disabled = 4;

    // 1.4 and the 1.5 alpha, which runs as its own instance with names of its own.
    private static readonly string[] Names = ["Everything", "Everything (1.5a)"];
    private static readonly (string Folder, string Exe)[] Folders = [("Everything", "Everything.exe"), ("Everything 1.5a", "Everything64.exe"), ("Everything 1.5a", "Everything.exe")];

    /// <summary>
    /// The winget command line: the exact package from winget's own source, silent, the agreements accepted (the user
    /// agreed on Tomte's card), and the machine-wide package, whose MSI installs and starts the Everything service and
    /// starts Everything at sign-in, so later runs need no admin rights.
    /// </summary>
    public static readonly IReadOnlyList<string> WingetArguments =
        ["install", "--id", PackageId, "-e", "--source", "winget", "--silent", "--scope", "machine", "--accept-package-agreements", "--accept-source-agreements"];

    public static FileSource Source() =>
        Everything.Running ? FileSource.Everything
        : Exe() is null ? FileSource.Index
        : ServiceUsable ? FileSource.EverythingStopped
        : FileSource.EverythingManual;

    /// <summary>The "Search every drive" row shows while search uses the Windows index and the user has not said "Not now" to it.</summary>
    public static bool Offers(FileSource source, bool dismissed) => source == FileSource.Index && !dismissed;

    /// <summary>One line for Settings and the log: which file search is in use.</summary>
    public static string Describe(FileSource source) => source switch
    {
        FileSource.Everything => "Everything is running: search covers every drive.",
        FileSource.EverythingStopped => "Everything is installed. Tomte starts it when you search, to cover every drive.",
        FileSource.EverythingManual => "Everything is installed but not running. Start it to search every drive; until then search uses the Windows Search index.",
        _ => "The Windows Search index: only the folders Windows indexes. Get Everything (free, from voidtools) to search every drive.",
    };

    /// <summary>
    /// The Everything service (or the 1.5 alpha's) is installed and not disabled; with it Everything runs as a standard
    /// user, so Tomte may start it with no admin prompt.
    /// </summary>
    public static bool ServiceUsable => Names.Any(name => ServiceStarts(Service(name, "Start") as int?));

    /// <summary>A service's Start value that lets it run: any but disabled (4). Null is no service.</summary>
    public static bool ServiceStarts(int? start) => start is int value && value != Disabled;

    /// <summary>
    /// Everything.exe as installed, looked up in this order: the machine-wide sign-in entry, the service's program, the
    /// usual Program Files folders (1.4, then the 1.5 alpha), then the user's own sign-in entry, which anyone could set
    /// without admin rights. Null when none names a file that is there.
    /// </summary>
    public static string? Exe() => Candidates(
        name => Registry.GetValue(@"HKEY_LOCAL_MACHINE\" + Run, name, null) as string,
        name => Service(name, "ImagePath") as string,
        name => Registry.GetValue(@"HKEY_CURRENT_USER\" + Run, name, null) as string,
        [Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86)])
        .FirstOrDefault(File.Exists);

    /// <summary>The places <see cref="Exe"/> looks, in its order, from these readers of the sign-in entries and the services' programs.</summary>
    public static IEnumerable<string> Candidates(Func<string, string?> machineRun, Func<string, string?> service, Func<string, string?> userRun, IReadOnlyList<string> programFiles) =>
        Names.Select(n => ExeFrom(machineRun(n)))
            .Concat(Names.Select(n => ExeFrom(service(n))))
            .Concat(programFiles.Where(f => f.Length > 0).SelectMany(f => Folders.Select(folder => Path.Combine(f, folder.Folder, folder.Exe))))
            .Concat(Names.Select(n => ExeFrom(userRun(n))))
            .OfType<string>();

    private static object? Service(string name, string value) =>
        Registry.GetValue(@"HKEY_LOCAL_MACHINE\SYSTEM\CurrentControlSet\Services\" + name, value, null);

    /// <summary>The program a command line starts: the quoted first part, or everything up to ".exe". Null when it names no .exe.</summary>
    public static string? ExeFrom(string? commandLine)
    {
        string text = commandLine?.Trim() ?? "";
        if (text.StartsWith('"'))
        {
            int close = text.IndexOf('"', 1);
            text = close > 0 ? text[1..close] : "";
        }
        else
        {
            int exe = text.IndexOf(".exe", StringComparison.OrdinalIgnoreCase);
            text = exe > 0 ? text[..(exe + 4)] : "";
        }

        return text.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? Environment.ExpandEnvironmentVariables(text) : null;
    }

    /// <summary>
    /// Starts Everything in the background, with no window. Through the shell, so it inherits none of Tomte's handles
    /// (a redirected log would otherwise stay open for as long as Everything runs). False when it could not be started.
    /// </summary>
    public static bool Start(string exe, string? instance = null)
    {
        var start = new ProcessStartInfo(exe) { UseShellExecute = true, WorkingDirectory = Path.GetDirectoryName(exe)!, ArgumentList = { "-startup" } };
        if (instance is not null)
        {
            start.ArgumentList.Add("-instance");
            start.ArgumentList.Add(instance);
        }

        try
        {
            using var started = Process.Start(start);
            return started is not null;
        }
        catch (Win32Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Installs Everything with winget, then starts it and waits for it to answer. Null when it is running; otherwise what
    /// went wrong, in words for the user. Windows asks for permission once, for the machine-wide install.
    /// </summary>
    public static async Task<string?> InstallAsync(Action<string>? log, CancellationToken cancel)
    {
        int code;
        try
        {
            code = await Winget(log, cancel);
        }
        catch (Win32Exception)
        {
            return "winget is not on this PC, so Tomte cannot install Everything.";
        }

        log?.Invoke($"everything: winget exited with 0x{code:X8}");
        if (code is not (0 or AlreadyInstalled or NoUpgrade))
        {
            return $"winget could not install Everything (code 0x{code:X8}).";
        }

        if (Everything.Running)
        {
            return null;
        }

        if (Exe() is not { } exe || !Start(exe))
        {
            return "Everything is installed, but Tomte could not start it.";
        }

        // polls for the IPC window for up to 20 s, once, after an install; Everything broadcasts
        // EVERYTHING_IPC_CREATED, which only a top-level window of ours would hear.
        for (int waited = 0; waited < 20_000 && !Everything.Running; waited += 250)
        {
            await Task.Delay(250, cancel);
        }

        return Everything.Running ? null : "Everything is installed and starting. Search uses it as soon as it is ready.";
    }

    private static async Task<int> Winget(Action<string>? log, CancellationToken cancel)
    {
        var start = new ProcessStartInfo(WingetExe() ?? throw new Win32Exception(2)) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (string argument in WingetArguments)
        {
            start.ArgumentList.Add(argument);
        }

        log?.Invoke($"everything: winget {string.Join(' ', WingetArguments)}");
        using var winget = Process.Start(start) ?? throw new Win32Exception();
        // Read to the end, or a full pipe would stop winget; only the last line is kept for the log.
        var output = winget.StandardOutput.ReadToEndAsync(cancel);
        var errors = winget.StandardError.ReadToEndAsync(cancel);
        using var limit = CancellationTokenSource.CreateLinkedTokenSource(cancel);
        limit.CancelAfter(TimeSpan.FromMinutes(10));
        await winget.WaitForExitAsync(limit.Token);
        string last = (await output).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
        log?.Invoke($"everything: winget said \"{last}\"{((await errors).Length > 0 ? " (and wrote errors)" : "")}");
        return winget.ExitCode;
    }

    /// <summary>
    /// winget by its full path, never a name looked up on PATH: the App Installer's alias in the user's WindowsApps folder,
    /// else what App Paths names. Null when there is none.
    /// </summary>
    public static string? WingetExe()
    {
        const string AppPaths = @"Software\Microsoft\Windows\CurrentVersion\App Paths\winget.exe";
        string?[] candidates =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WindowsApps", "winget.exe"),
            Registry.GetValue(@"HKEY_CURRENT_USER\" + AppPaths, null, null) as string,
            Registry.GetValue(@"HKEY_LOCAL_MACHINE\" + AppPaths, null, null) as string,
        ];
        return candidates.FirstOrDefault(c => c is { Length: > 0 } && Path.IsPathFullyQualified(c) && File.Exists(c));
    }
}
