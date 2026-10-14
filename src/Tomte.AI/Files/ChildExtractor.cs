using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Encodings.Web;
using System.Text.Json;
using Windows.Win32;
using Windows.Win32.System.JobObjects;

namespace Tomte.AI.Files;

/// <summary>How to start the reader process: a program and its arguments. The request then goes in on its stdin.</summary>
public sealed record ExtractorCommand(string FileName, IReadOnlyList<string> Arguments)
{
    /// <summary>This program again with --extract, which the shell and the palette harness both answer.</summary>
    public static ExtractorCommand ThisProgram { get; } = new(Environment.ProcessPath ?? "", ["--extract"]);
}

/// <summary>
/// Reads a PDF or a Word file in a short-lived child process. A parser fed a hostile file can be made to unpack a
/// small file to gigabytes or to spin inside one page, and neither can be stopped from inside: the child runs in a job
/// object with a memory limit that kills it on close, and the parent kills it at the time limit. The parent never
/// loads PdfPig. The child starts, reads its request from stdin only once it is inside the job, writes the text to
/// stdout as JSON and exits; its exit code says how it went.
/// </summary>
public static class ChildExtractor
{
    public const int Ok = 0;
    public const int Unreadable = 2;
    public const int TooLarge = 3;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private sealed record Request(string Path, FileKind Kind, int MaxChars, long MaxBytes, long MaxDocxXmlBytes, int MaxDocxEntries);

    private sealed record Reply(string? Text, bool Cut, int Pages, int PagesRead, bool Encrypted, string? Error);

    public static async Task<Extracted> ReadAsync(string path, FileKind kind, FileLimits limits, CancellationToken ct) =>
        (await RunAsync(path, kind, limits, ct)).Text;

    /// <summary>The text, and the most memory the child committed, which the job keeps after it has gone.</summary>
    internal static async Task<(Extracted Text, long PeakBytes)> RunAsync(string path, FileKind kind, FileLimits limits, CancellationToken ct)
    {
        var info = new ProcessStartInfo(limits.Extractor.FileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
        };
        foreach (var argument in limits.Extractor.Arguments)
            info.ArgumentList.Add(argument);
        // The job's limit stops the process; the runtime only knows about it if told, and then throws a clean
        // out-of-memory exception at three quarters of it instead of failing inside the collector.
        info.Environment["DOTNET_GCHeapHardLimit"] = (limits.ChildMemoryBytes / 4 * 3).ToString("X", CultureInfo.InvariantCulture);

        using var job = CreateJob(limits.ChildMemoryBytes);
        using var process = Process.Start(info) ?? throw new FileReadException("could not be read: the reader did not start.");
        try
        {
            if (!PInvoke.AssignProcessToJobObject(job, process.SafeHandle) && !process.HasExited)
                throw new Win32Exception();

            // Nothing is read before the limits hold: the request goes in only now that the child is inside the job.
            await JsonSerializer.SerializeAsync(process.StandardInput.BaseStream,
                new Request(path, kind, limits.MaxChars, limits.MaxBytes, limits.MaxDocxXmlBytes, limits.MaxDocxEntries), Json, ct);
            process.StandardInput.Close();

            using var time = CancellationTokenSource.CreateLinkedTokenSource(ct);
            time.CancelAfter(limits.TimeLimit);
            // Escaped control characters take six bytes; nothing the child may send is longer than this.
            var output = ReadAllAsync(process.StandardOutput.BaseStream, limits.MaxChars * 6L + 65_536, time.Token);
            try
            {
                await process.WaitForExitAsync(time.Token);
                byte[] bytes = await output;
                long peak = PeakBytes(job);
                return process.ExitCode switch
                {
                    Ok when JsonSerializer.Deserialize<Reply>(bytes, Json) is { Text: { } text } r => (new Extracted(text, r.Cut, r.Pages, r.PagesRead, r.Encrypted), peak),
                    Unreadable when JsonSerializer.Deserialize<Reply>(bytes, Json) is { Error: { } error } => throw new FileReadException(error),
                    // An exit at the memory limit can be the runtime's own fail-fast rather than the child's code.
                    _ when process.ExitCode == TooLarge || peak >= limits.ChildMemoryBytes / 10 * 9 => throw TooMuchMemory(limits),
                    _ => throw new FileReadException("could not be read: it is damaged or not supported."),
                };
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new FileReadException(string.Create(CultureInfo.InvariantCulture, $"took longer than {limits.TimeLimit.TotalSeconds:0} s to read, so it was not sent."));
            }
            catch (JsonException)
            {
                throw new FileReadException("could not be read: it is damaged or not supported.");
            }
        }
        finally
        {
            // Closing the job kills the child as well; this covers a child that left the job by exiting first.
            if (!process.HasExited)
                process.Kill();
        }
    }

    /// <summary>The child's side: one request from stdin, the reply on stdout, the exit code for the parent.</summary>
    public static int Run()
    {
        using var stdout = Console.OpenStandardOutput();
        try
        {
            Request request;
            using (var stdin = Console.OpenStandardInput())
                request = JsonSerializer.Deserialize<Request>(stdin, Json) ?? throw new FileReadException("could not be read: no request.");
            var limits = new FileLimits { MaxChars = request.MaxChars, MaxBytes = request.MaxBytes, MaxDocxXmlBytes = request.MaxDocxXmlBytes, MaxDocxEntries = request.MaxDocxEntries };
            Extracted text;
            using (var file = FileAttachments.Open(request.Path))
                text = request.Kind == FileKind.Pdf ? PdfExtraction.Read(file, limits, CancellationToken.None) : TextExtraction.ReadDocx(file, limits, CancellationToken.None);
            JsonSerializer.Serialize(stdout, new Reply(text.Text, text.Cut, text.Pages, text.PagesRead, text.Encrypted, null), Json);
            return Ok;
        }
        catch (OutOfMemoryException)
        {
            return TooLarge;
        }
        catch (FileReadException e)
        {
            JsonSerializer.Serialize(stdout, new Reply(null, false, 0, 0, false, e.Message), Json);
            return Unreadable;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            JsonSerializer.Serialize(stdout, new Reply(null, false, 0, 0, false, "could not be opened. It may be in use or not readable."), Json);
            return Unreadable;
        }
        catch (Exception)
        {
            JsonSerializer.Serialize(stdout, new Reply(null, false, 0, 0, false, "could not be read: it is damaged or not supported."), Json);
            return Unreadable;
        }
    }

    private static FileReadException TooMuchMemory(FileLimits limits) =>
        new($"needs more than {limits.ChildMemoryBytes >> 20} MB to read, which can mean a damaged or hostile file, so it was not read.");

    private static async Task<byte[]> ReadAllAsync(Stream stream, long cap, CancellationToken ct)
    {
        var all = new MemoryStream();
        var buffer = new byte[64 * 1024];
        int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (all.Length + n > cap)
                throw new FileReadException("could not be read: the reader sent more than it may.");
            all.Write(buffer, 0, n);
        }

        return all.ToArray();
    }

    private static SafeHandle CreateJob(long memoryBytes)
    {
        var job = PInvoke.CreateJobObject(null, null);
        var limits = default(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
        // No error dialog for a child that crashes: it ends, and the parent says the file could not be read.
        limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            | JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_PROCESS_MEMORY | JOB_OBJECT_LIMIT.JOB_OBJECT_LIMIT_DIE_ON_UNHANDLED_EXCEPTION;
        limits.ProcessMemoryLimit = (nuint)memoryBytes;
        if (job.IsInvalid || !PInvoke.SetInformationJobObject(job, JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, MemoryMarshal.AsBytes(new ReadOnlySpan<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>(in limits))))
        {
            int error = Marshal.GetLastPInvokeError();
            job.Dispose();
            throw new Win32Exception(error);
        }

        return job;
    }

    private static unsafe long PeakBytes(SafeHandle job)
    {
        var info = default(JOBOBJECT_EXTENDED_LIMIT_INFORMATION);
        bool added = false;
        try
        {
            job.DangerousAddRef(ref added);
            return PInvoke.QueryInformationJobObject((Windows.Win32.Foundation.HANDLE)job.DangerousGetHandle(), JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation, &info, (uint)sizeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION), null)
                ? (long)info.PeakProcessMemoryUsed : 0;
        }
        finally
        {
            if (added)
                job.DangerousRelease();
        }
    }
}
