using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Tomte.Core;
using Tomte.Core.Clipboard;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Memory;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Shell;

/// <summary>
/// Clipboard history's listener: a thread of its own with a message-only window registered for the
/// clipboard update event, started only while history is on. Opening the clipboard can wait on
/// another app and reading a format can wait for its owner to render it, so this runs neither on the
/// island's thread (frames) nor on the input thread (hook callbacks have 1 ms). Each update is read
/// here and the text, or an image's bytes, handed off; decoding and storing are the watch's worker's
/// job. The window is also the owner of what Tomte puts back on the clipboard, which is how the next
/// update knows it is Tomte's own.
/// </summary>
internal sealed unsafe class ClipboardListener : IClipboardListener
{
    private const uint MsgPut = PInvoke.WM_APP + 1;
    private const uint CF_UNICODETEXT = 13, CF_DIB = 8;
    private static readonly HWND MessageOnlyParent = new(-3); // HWND_MESSAGE, which CsWin32 does not emit
    private static readonly long Ticks = Stopwatch.Frequency / 1_000_000;

    private readonly Action<ClipboardRead, DateTimeOffset> captured;
    private readonly Func<bool> paused;
    private readonly Func<IReadOnlyList<AppRule>> rules;
    private readonly ClipboardRetention limits;
    private readonly Action<string>? log;
    private readonly ManualResetEventSlim ready = new(false);
    private readonly Lock gate = new();
    private readonly Thread thread;
    private readonly Source source;
    private HWND window;
    private (uint Format, byte[] Bytes)[]? toPut;
    private uint lastSequence;

    private ClipboardListener(Action<ClipboardRead, DateTimeOffset> captured, Func<bool> paused, Func<IReadOnlyList<AppRule>> rules, ClipboardRetention limits, Action<string>? log)
    {
        (this.captured, this.paused, this.rules, this.limits, this.log) = (captured, paused, rules, limits, log);
        source = new Source(this);
        thread = new Thread(Loop) { IsBackground = true, Name = "tomte-clipboard" };
    }

    /// <summary>Starts the thread and returns once the window listens.</summary>
    public static ClipboardListener Start(Action<ClipboardRead, DateTimeOffset> captured, Func<bool> paused, Func<IReadOnlyList<AppRule>> rules, ClipboardRetention limits, Action<string>? log)
    {
        var listener = new ClipboardListener(captured, paused, rules, limits, log);
        listener.thread.Start();
        listener.ready.Wait();
        return listener;
    }

    // The text's own UTF-16 and its end mark, unpaired surrogates and all.
    public void Put(string text) => Post([(CF_UNICODETEXT, MemoryMarshal.AsBytes((text + "\0").AsSpan()).ToArray())]);

    // The PNG first, for the apps that read it and the alpha it keeps; the bitmap for every other app.
    public void Put(byte[] png, byte[] dib) => Post([(PInvoke.RegisterClipboardFormat(ClipboardReader.PngFormat), png), (CF_DIB, dib)]);

    /// <summary>Stops listening and ends the thread. Waits a moment for it, not for an app that hangs on a render.</summary>
    public void Dispose()
    {
        if (window == HWND.Null)
        {
            return;
        }

        PInvoke.PostMessage(window, PInvoke.WM_CLOSE, 0, 0);
        thread.Join(500);
    }

    // Without a window there is no thread to post to; posting to no window would land on the caller's own queue.
    private void Post((uint Format, byte[] Bytes)[] formats)
    {
        if (window == HWND.Null)
        {
            return;
        }

        lock (gate)
        {
            toPut = formats;
        }

        PInvoke.PostMessage(window, MsgPut, 0, 0);
    }

    private void Loop()
    {
        try
        {
            // The predefined static class: every message this window cares about is posted, so the loop takes it first.
            fixed (char* className = "STATIC")
            {
                window = PInvoke.CreateWindowEx(0, className, null, 0, 0, 0, 0, 0, MessageOnlyParent, HMENU.Null, HINSTANCE.Null, null);
            }

            if (window == HWND.Null || !PInvoke.AddClipboardFormatListener(window))
            {
                log?.Invoke($"clipboard: listener could not start ({new Win32Exception().NativeErrorCode})");
            }

            lastSequence = PInvoke.GetClipboardSequenceNumber();
        }
        finally
        {
            // Start waits for this; it must come even if the window could not be made.
            ready.Set();
        }

        // No window, nothing to listen with: the thread ends here rather than wait for a message that cannot come.
        if (window == HWND.Null)
        {
            return;
        }

        MSG message;
        while (PInvoke.GetMessage(&message, HWND.Null, 0, 0).Value > 0)
        {
            try
            {
                switch (message.message)
                {
                    case PInvoke.WM_CLIPBOARDUPDATE:
                        OnUpdate();
                        continue;
                    case MsgPut:
                        PutNow();
                        continue;
                    case PInvoke.WM_CLOSE:
                        PInvoke.RemoveClipboardFormatListener(window);
                        PInvoke.DestroyWindow(window);
                        return;
                }
            }
            catch (Exception e)
            {
                // The type only, so no clipboard text can reach the log through an exception's message.
                log?.Invoke($"clipboard: update failed ({e.GetType().Name})");
                continue;
            }

            PInvoke.TranslateMessage(&message);
            PInvoke.DispatchMessage(&message);
        }
    }

    private void OnUpdate()
    {
        // Several updates can be queued for one change, and a write in several steps sends one per step.
        uint sequence = PInvoke.GetClipboardSequenceNumber();
        if (sequence == lastSequence)
        {
            return;
        }

        lastSequence = sequence;
        long start = Stopwatch.GetTimestamp();
        var read = ClipboardReader.Read(source, paused(), rules(), limits);
        long us = (Stopwatch.GetTimestamp() - start) / Ticks;
        if (read.Text is not null || read.Image is not null)
        {
            captured(read, DateTimeOffset.Now);
        }

        log?.Invoke($"clipboard: {read.Outcome} in {us} us");
    }

    /// <summary>A picked item back on the clipboard in its formats, owned by this window.</summary>
    private void PutNow()
    {
        (uint Format, byte[] Bytes)[]? formats;
        lock (gate)
        {
            (formats, toPut) = (toPut, null);
        }

        if (formats is null || !source.Open())
        {
            log?.Invoke("clipboard: could not put the item back, the clipboard stayed busy");
            return;
        }

        try
        {
            PInvoke.EmptyClipboard();
            foreach (var (format, bytes) in formats)
            {
                var memory = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)bytes.Length);
                if (memory.IsNull)
                {
                    break;
                }

                bytes.CopyTo(new Span<byte>(PInvoke.GlobalLock(memory), bytes.Length));
                PInvoke.GlobalUnlock(memory);
                // On success the clipboard owns the memory; otherwise it is still ours to free.
                if (PInvoke.SetClipboardData(format, (HANDLE)memory.Value).IsNull)
                {
                    PInvoke.GlobalFree(memory);
                }
            }
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    /// <summary>The Win32 clipboard as the reader asks for it, on the listener's thread.</summary>
    private sealed class Source(ClipboardListener owner) : IClipboardSource
    {
        private readonly Dictionary<string, uint> formats = [];

        public int OwnerProcessId => (int)Pid(PInvoke.GetClipboardOwner());

        public string? OwnerExe => Exe(PInvoke.GetClipboardOwner());

        public string? ForegroundExe => Exe(PInvoke.GetForegroundWindow());

        public int TextLength
        {
            get
            {
                if (!PInvoke.IsClipboardFormatAvailable(CF_UNICODETEXT))
                {
                    return -1;
                }

                var data = PInvoke.GetClipboardData(CF_UNICODETEXT);
                return data.IsNull ? -1 : (int)Math.Min(int.MaxValue, PInvoke.GlobalSize((HGLOBAL)data.Value) / 2);
            }
        }

        public bool TryOpen() => PInvoke.OpenClipboard(owner.window);

        /// <summary>The bounded retry the reader uses, for putting an item back too.</summary>
        public bool Open()
        {
            for (int attempt = 0; attempt < 10; attempt++)
            {
                if (TryOpen())
                {
                    return true;
                }

                Wait(5 << Math.Min(attempt, 3));
            }

            return false;
        }

        // An app emptying the clipboard while this window owns it sends WM_DESTROYCLIPBOARD and waits for the answer,
        // so sent messages are answered while waiting; posted ones stay queued for the loop.
        public void Wait(int milliseconds)
        {
            PInvoke.MsgWaitForMultipleObjects(0, null, false, (uint)milliseconds, QUEUE_STATUS_FLAGS.QS_SENDMESSAGE);
            MSG message;
            PInvoke.PeekMessage(&message, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_NOREMOVE | PEEK_MESSAGE_REMOVE_TYPE.PM_QS_SENDMESSAGE);
        }

        public void Close() => PInvoke.CloseClipboard();

        public bool Has(string format) => PInvoke.IsClipboardFormatAvailable(Id(format));

        public uint? Number(string format)
        {
            uint id = Id(format);
            if (!PInvoke.IsClipboardFormatAvailable(id))
            {
                return null;
            }

            var data = (HGLOBAL)PInvoke.GetClipboardData(id).Value;
            if (data.IsNull || PInvoke.GlobalSize(data) < 4)
            {
                return 0;
            }

            uint* p = (uint*)PInvoke.GlobalLock(data);
            try
            {
                return p == null ? 0 : *p;
            }
            finally
            {
                PInvoke.GlobalUnlock(data);
            }
        }

        public string ReadText()
        {
            var data = (HGLOBAL)PInvoke.GetClipboardData(CF_UNICODETEXT).Value;
            int chars = (int)Math.Min(int.MaxValue, PInvoke.GlobalSize(data) / 2);
            char* p = data.IsNull ? null : (char*)PInvoke.GlobalLock(data);
            if (p == null)
            {
                return "";
            }

            try
            {
                var span = new ReadOnlySpan<char>(p, chars);
                int end = span.IndexOf('\0');
                return new string(end < 0 ? span : span[..end]);
            }
            finally
            {
                PInvoke.GlobalUnlock(data);
            }
        }

        public (bool Png, long Bytes)? Image =>
            Size(Id(ClipboardReader.PngFormat)) is > 0 and var png ? (true, png)
            : Size(CF_DIB) is > 0 and var dib ? (false, dib)
            : null;

        public byte[] ReadImage(bool png)
        {
            var data = (HGLOBAL)PInvoke.GetClipboardData(png ? Id(ClipboardReader.PngFormat) : CF_DIB).Value;
            byte* p = data.IsNull ? null : (byte*)PInvoke.GlobalLock(data);
            if (p == null)
            {
                return [];
            }

            try
            {
                return new ReadOnlySpan<byte>(p, (int)Math.Min(int.MaxValue, PInvoke.GlobalSize(data))).ToArray();
            }
            finally
            {
                PInvoke.GlobalUnlock(data);
            }
        }

        // A format whose handle is empty or not global memory has no size and counts as not there.
        private static long Size(uint format)
        {
            if (!PInvoke.IsClipboardFormatAvailable(format))
            {
                return -1;
            }

            var data = PInvoke.GetClipboardData(format);
            return data.IsNull ? -1 : (long)PInvoke.GlobalSize((HGLOBAL)data.Value);
        }

        private static uint Pid(HWND hwnd)
        {
            uint pid = 0;
            if (!hwnd.IsNull)
            {
                PInvoke.GetWindowThreadProcessId(hwnd, &pid);
            }

            return pid;
        }

        private static string? Exe(HWND hwnd) => Pid(hwnd) is var pid and not 0 ? DesktopWatch.ExeName(pid) : null;

        private uint Id(string format)
        {
            if (!formats.TryGetValue(format, out uint id))
            {
                formats[format] = id = PInvoke.RegisterClipboardFormat(format);
            }

            return id;
        }
    }
}
