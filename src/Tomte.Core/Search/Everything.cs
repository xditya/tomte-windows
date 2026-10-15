using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.DataExchange;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Core.Search;

/// <summary>
/// Whole-disk file search through Everything (voidtools), over the IPC its SDK documents: a WM_COPYDATA query to
/// Everything's hidden window, answered by a WM_COPYDATA to a window of ours. No DLL is loaded and nothing is bundled;
/// without Everything running there is no window, and the caller uses the Windows index. Each search makes a
/// message-only window on the calling thread, waits for the answer there and destroys the window, so nothing of it
/// stays between searches. Blocks for as long as Everything takes: worker threads only.
/// </summary>
public static unsafe class Everything
{
    /// <summary>The class of Everything's IPC window. A named instance (-instance name) adds "_(name)".</summary>
    public const string WindowClass = "EVERYTHING_TASKBAR_NOTIFICATION";

    // EVERYTHING_IPC_COPYDATA_QUERY2W, the full path and name of each item, newest change first.
    private const uint Query2 = 18, RequestName = 1, RequestPath = 2, RequestFullPath = 4, SortNewestFirst = 14, FolderFlag = 1;

    // The dwData Everything puts on its answer, which says it is ours: "TOMT".
    private const uint ReplyId = 0x544F4D54;
    private const int ListHeader = 20, ItemSize = 8;
    private const string ReplyClass = "TomteEverythingReply";
    private static readonly HWND MessageOnlyParent = new(-3);
    private static readonly Lock Gate = new();

    // Kept for the life of the process: the window class holds a pointer to it.
    private static readonly WNDPROC Procedure = HandleMessage;
    private static bool registered;

    [ThreadStatic]
    private static byte[]? reply;

    /// <summary>The Everything instance to use: null for the usual one. A named instance is for testing against an index of a scratch folder.</summary>
    public static string? Instance { get; set; }

    /// <summary>Everything is running and answers queries: its IPC window is there.</summary>
    public static bool Running => !Window().IsNull;

    public static string ClassFor(string? instance) => instance is null ? WindowClass : $"{WindowClass}_({instance})";

    /// <summary>
    /// The query as Everything's IPC takes it (EVERYTHING_IPC_QUERY2 then the search as UTF-16 with its terminator):
    /// answer to <paramref name="replyWindow"/> with <see cref="ReplyId"/>, no search flags, from the first result,
    /// at most <paramref name="max"/>, the full path and name of each, newest change first.
    /// </summary>
    public static byte[] Query(uint replyWindow, string search, int max)
    {
        var query = new byte[28 + ((search.Length + 1) * 2)];
        var fields = new[] { replyWindow, ReplyId, 0u, 0u, (uint)max, RequestFullPath, SortNewestFirst };
        for (int i = 0; i < fields.Length; i++)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(query.AsSpan(i * 4), fields[i]);
        }

        Encoding.Unicode.GetBytes(search, query.AsSpan(28));
        return query;
    }

    /// <summary>
    /// The hits in Everything's answer (EVERYTHING_IPC_LIST2): each item's flags and where its data is, the data being,
    /// for each requested text, its length in characters, the characters and a terminator. Reads the name and the path
    /// when Everything sent those instead of the full path. Anything that runs past the end of the answer is left out.
    /// </summary>
    public static IReadOnlyList<FileHit> Parse(ReadOnlySpan<byte> list)
    {
        var hits = new List<FileHit>();
        if (list.Length < ListHeader)
        {
            return hits;
        }

        uint count = BinaryPrimitives.ReadUInt32LittleEndian(list[4..]);
        uint requested = BinaryPrimitives.ReadUInt32LittleEndian(list[12..]);
        for (long i = 0; i < count && ListHeader + ((i + 1) * ItemSize) <= list.Length; i++)
        {
            var item = list[(int)(ListHeader + (i * ItemSize))..];
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(item);
            int at = (int)Math.Min(int.MaxValue, BinaryPrimitives.ReadUInt32LittleEndian(item[4..]));
            string? name = null, path = null, full = null;
            bool ok = (requested & RequestName) == 0 || Text(list, ref at, out name);
            ok = ok && ((requested & RequestPath) == 0 || Text(list, ref at, out path));
            ok = ok && ((requested & RequestFullPath) == 0 || Text(list, ref at, out full));
            string? found = full ?? (name is null ? null : path is { Length: > 0 } ? Path.Combine(path, name) : name);
            if (ok && found is { Length: > 0 })
            {
                hits.Add(new(found, (flags & FolderFlag) != 0));
            }
        }

        return hits;
    }

    /// <summary>
    /// Asks Everything for <paramref name="search"/> and waits for the answer, up to two seconds, or until
    /// <paramref name="cancel"/>. Null when Everything is not running, refused the query or did not answer in time.
    /// </summary>
    public static IReadOnlyList<FileHit>? Search(string search, int max, CancellationToken cancel)
    {
        var everything = Window();
        if (everything.IsNull || search.Length == 0)
        {
            return null;
        }

        var instance = new HINSTANCE(Marshal.GetHINSTANCE(typeof(Everything).Module));
        Register(instance);
        HWND window;
        fixed (char* className = ReplyClass)
        {
            window = PInvoke.CreateWindowEx(0, className, null, 0, 0, 0, 0, 0, MessageOnlyParent, HMENU.Null, instance, null);
        }

        if (window.IsNull)
        {
            throw new Win32Exception();
        }

        try
        {
            reply = null;
            var query = Query((uint)(nint)window.Value, search, max);
            fixed (byte* data = query)
            {
                var message = new COPYDATASTRUCT { dwData = Query2, cbData = (uint)query.Length, lpData = data };
                nuint accepted;
                // Not SMTO_BLOCK: Everything may answer while this waits, and the answer is a message sent to this thread.
                if (PInvoke.SendMessageTimeout(everything, PInvoke.WM_COPYDATA, (nuint)(nint)window.Value, (nint)(&message), SEND_MESSAGE_TIMEOUT_FLAGS.SMTO_ABORTIFHUNG, 2000, &accepted) == 0 || accepted == 0)
                {
                    return null;
                }
            }

            return Wait(TimeSpan.FromSeconds(2), cancel) is { } answer ? Parse(answer) : null;
        }
        finally
        {
            PInvoke.DestroyWindow(window);
            reply = null;
        }
    }

    private static HWND Window() => PInvoke.FindWindow(ClassFor(Instance), null);

    // The answer is a sent message, which this thread takes while it waits for input or for the cancel.
    private static byte[]? Wait(TimeSpan limit, CancellationToken cancel)
    {
        long end = Environment.TickCount64 + (long)limit.TotalMilliseconds;
        var handle = new HANDLE(cancel.WaitHandle.SafeWaitHandle.DangerousGetHandle());
        while (reply is null && !cancel.IsCancellationRequested)
        {
            long left = end - Environment.TickCount64;
            if (left <= 0)
            {
                return null;
            }

            PInvoke.MsgWaitForMultipleObjectsEx(new ReadOnlySpan<HANDLE>(in handle), (uint)left, QUEUE_STATUS_FLAGS.QS_ALLINPUT, MSG_WAIT_FOR_MULTIPLE_OBJECTS_EX_FLAGS.MWMO_INPUTAVAILABLE);
            while (reply is null && PInvoke.PeekMessage(out var message, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
            {
                PInvoke.DispatchMessage(&message);
            }
        }

        return reply;
    }

    private static void Register(HINSTANCE instance)
    {
        lock (Gate)
        {
            if (registered)
            {
                return;
            }

            fixed (char* className = ReplyClass)
            {
                var windowClass = new WNDCLASSEXW
                {
                    cbSize = (uint)Marshal.SizeOf<WNDCLASSEXW>(),
                    lpfnWndProc = Procedure,
                    hInstance = instance,
                    lpszClassName = className,
                };
                if (PInvoke.RegisterClassEx(in windowClass) == 0)
                {
                    throw new Win32Exception();
                }
            }

            registered = true;
        }
    }

    private static LRESULT HandleMessage(HWND hwnd, uint message, WPARAM wParam, LPARAM lParam)
    {
        if (message == PInvoke.WM_COPYDATA && (nint)lParam != 0)
        {
            var data = (COPYDATASTRUCT*)(nint)lParam;
            if (data->dwData == ReplyId)
            {
                reply = data->lpData is null ? [] : new ReadOnlySpan<byte>(data->lpData, (int)data->cbData).ToArray();
                return new LRESULT(1);
            }
        }

        return PInvoke.DefWindowProc(hwnd, message, wParam, lParam);
    }

    // One text field at offset at: its length in characters, the characters, a terminator. False when it runs past the end.
    private static bool Text(ReadOnlySpan<byte> list, ref int at, out string? text)
    {
        text = null;
        if (at < 0 || at > list.Length - 4)
        {
            return false;
        }

        long chars = BinaryPrimitives.ReadUInt32LittleEndian(list[at..]);
        long end = at + 4 + ((chars + 1) * 2);
        if (end > list.Length)
        {
            return false;
        }

        text = Encoding.Unicode.GetString(list.Slice(at + 4, (int)chars * 2));
        at = (int)end;
        return true;
    }
}
