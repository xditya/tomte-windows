using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Tomte.Core;
using Windows.Win32;
using Windows.Win32.System.JobObjects;

namespace Tomte.AI.Mcp;

/// <summary>
/// One stdio server as a child process that Tomte starts and owns. The process sits in a job object
/// that Windows ends when Tomte's handle to it closes, so the server and everything it started stop
/// with Tomte however Tomte ends: quit, crash or killed. Tomte itself is never in such a job.
/// A program (.exe, .com; uvx is one) is started directly with its arguments as typed. A .cmd or
/// .bat (npx is one) can only run through cmd.exe, so those go through it with the arguments quoted
/// to arrive unchanged. A batch file that hands its arguments on with CALL makes cmd read them a
/// second time, which the quoting does not cover; npx.cmd does not. The child gets Tomte's whole
/// environment plus the server's own variables, which only its environment block holds, and runs in
/// the user's profile folder.
/// </summary>
internal sealed class McpProcess : IAsyncDisposable
{
    // How long a server has to exit after its input closes, before it is ended.
    private static readonly TimeSpan Shutdown = TimeSpan.FromSeconds(1);

    // How much of the end of stderr is kept for an error message.
    private const int ErrorTail = 200;

    private readonly Process _process;
    private readonly SafeHandle _job;
    private readonly StringBuilder _stderr = new();
    private readonly Task _errors;

    private McpProcess(Process process, SafeHandle job)
    {
        _process = process;
        _job = job;
        _errors = ReadErrorsAsync();
    }

    /// <summary>The server's stdin.</summary>
    public Stream Input => _process.StandardInput.BaseStream;

    /// <summary>The server's stdout.</summary>
    public Stream Output => _process.StandardOutput.BaseStream;

    /// <param name="environment">The server's variables with their values; set in the child's environment block only.</param>
    public static McpProcess Start(McpServerSettings server, IReadOnlyDictionary<string, string> environment)
    {
        string path = Find(server.Command)
            ?? throw new FileNotFoundException($"The command \"{server.Command}\" was not found. Give its full path, or check that its folder is on PATH.");
        var info = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardErrorEncoding = Encoding.UTF8,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        };
        foreach (var (name, value) in environment)
        {
            // The block separates variables with a NUL, so one inside a value would start another variable.
            if (value.Contains('\0'))
                throw new ArgumentException($"The value of {name} holds a character that cannot be passed to a program.");
            info.Environment[name] = value;
        }

        if (Path.GetExtension(path).ToLowerInvariant() is ".cmd" or ".bat")
        {
            info.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            info.Arguments = BatchCommandLine(path, server.Arguments);
        }
        else
        {
            info.FileName = path;
            foreach (string argument in server.Arguments)
                info.ArgumentList.Add(argument);
        }

        var job = CreateJob();
        Process process;
        try
        {
            process = Process.Start(info) ?? throw new InvalidOperationException("The process did not start.");
        }
        catch
        {
            job.Dispose();
            throw;
        }

        var owned = new McpProcess(process, job);

        // the process runs for a moment before it is in the job, so a child it starts in that
        // moment is outside the job. Kill() ends the tree as well, which covers a normal stop. Create the
        // process suspended with PROC_THREAD_ATTRIBUTE_JOB_LIST if a server ever escapes a killed Tomte.
        if (!PInvoke.AssignProcessToJobObject(job, process.SafeHandle))
        {
            var error = new Win32Exception();

            // A process that has already exited cannot be assigned and needs no job; why it exited is on its stderr.
            if (!process.HasExited)
            {
                owned.Kill();
                throw error;
            }
        }

        return owned;
    }

    /// <summary>The end of what the server wrote to stderr, which is usually why it did not start. Waits a moment for the last of it.</summary>
    public async Task<string> ErrorTailAsync()
    {
        await Task.WhenAny(_errors, Task.Delay(200));
        lock (_stderr)
            return _stderr.Length > ErrorTail ? _stderr.ToString(_stderr.Length - ErrorTail, ErrorTail) : _stderr.ToString();
    }

    /// <summary>Ends the server and everything it started, now.</summary>
    public void Kill()
    {
        try
        {
            _process.Kill(entireProcessTree: true);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Already gone, or part of the tree could not be ended; the job below ends what is in it.
        }

        _job.Dispose();
        _process.Dispose();
    }

    /// <summary>Stops the server the way the protocol asks: close its input, give it a moment to exit, then end it.</summary>
    public async ValueTask DisposeAsync()
    {
        try
        {
            _process.StandardInput.Close();
            using var patience = new CancellationTokenSource(Shutdown);
            await _process.WaitForExitAsync(patience.Token);
        }
        catch (Exception e) when (e is OperationCanceledException or IOException or InvalidOperationException)
        {
            // Still running after the wait, or already stopped and disposed.
        }

        Kill();
    }

    /// <summary>
    /// The file a command names, found the way a shell finds it: the name as given when it has an
    /// extension, then with each program extension, in the folders on PATH. A command with a folder in
    /// it is looked up from the user's profile folder, where the server runs.
    /// </summary>
    internal static string? Find(string command)
    {
        command = command.Trim().Trim('"');
        if (command.Length == 0)
            return null;
        string[] names = [.. Path.HasExtension(command) ? new[] { command } : [], command + ".com", command + ".exe", command + ".bat", command + ".cmd"];
        string[] folders = command.Contains('\\') || command.Contains('/') || Path.IsPathRooted(command)
            ? [Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)]
            : (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        foreach (string folder in folders)
        {
            foreach (string name in names)
            {
                try
                {
                    string path = Path.GetFullPath(Path.Combine(folder.Trim('"'), name));
                    if (File.Exists(path))
                        return path;
                }
                catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException)
                {
                    // A PATH entry that is not a usable folder name.
                }
            }
        }

        return null;
    }

    /// <summary>
    /// The cmd.exe arguments that run a .cmd or .bat with these arguments unchanged. cmd reads the line
    /// before the script does: it expands %NAME% and gives a meaning to characters such as ampersand,
    /// bar and caret outside quotes. So an argument that is not plainly safe is quoted, a quote inside
    /// it is doubled, and each percent sign is followed by a variable that expands to nothing, so it
    /// never pairs up into a name. A line break cannot be passed at all.
    /// </summary>
    internal static string BatchCommandLine(string script, IEnumerable<string> arguments)
    {
        if (script.Contains('"') || script.Contains('%'))
            throw new ArgumentException("A .cmd or .bat command cannot be run from a folder with a quote or percent sign in its name.");

        // The outer quotes are cmd's own: it removes the first and the last quote of the line after /c.
        var line = new StringBuilder("/e:ON /v:OFF /d /c \"\"").Append(script).Append('"');
        foreach (string argument in arguments)
        {
            if (argument.AsSpan().IndexOfAny('\r', '\n', '\0') >= 0)
                throw new ArgumentException("An argument for a .cmd or .bat command cannot hold a line break.");
            line.Append(' ');
            bool quote = argument.Length == 0 || argument[^1] == '\\'
                || argument.Any(c => char.IsControl(c) || (c < 128 && !char.IsAsciiLetterOrDigit(c) && !"#$*+-./:?@\\_".Contains(c)));
            if (quote)
                line.Append('"');
            int backslashes = 0;
            foreach (char c in argument)
            {
                if (c == '\\')
                {
                    backslashes++;
                }
                else
                {
                    // The program splits the line again by the C rules: backslashes before a quote count double there.
                    if (c == '"')
                        line.Append('\\', backslashes).Append('"');
                    else if (c == '%')
                        line.Append("%%cd:~,");
                    backslashes = 0;
                }

                line.Append(c);
            }

            if (quote)
                line.Append('\\', backslashes).Append('"');
        }

        return line.Append('"').ToString();
    }

    private static SafeHandle CreateJob()
    {
        var job = PInvoke.CreateJobObject(null, null);
        var limits = default(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
        if (job.IsInvalid || !PInvoke.SetInformationJobObject(job, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, MemoryMarshal.AsBytes(new ReadOnlySpan<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(in limits))))
        {
            int error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error);
        }

        return job;
    }

    private async Task ReadErrorsAsync()
    {
        var buffer = new char[1024];
        try
        {
            int read;
            while ((read = await _process.StandardError.ReadAsync(buffer)) > 0)
            {
                lock (_stderr)
                {
                    // Only the end is kept: a server can write without limit, and nobody reads the rest.
                    _stderr.Append(buffer, 0, read);
                    if (_stderr.Length > 4 * ErrorTail)
                        _stderr.Remove(0, _stderr.Length - ErrorTail);
                }
            }
        }
        catch (Exception e) when (e is IOException or ObjectDisposedException or InvalidOperationException)
        {
            // The process is gone and its streams with it.
        }
    }
}
