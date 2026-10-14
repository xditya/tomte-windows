using System.ComponentModel;
using System.Diagnostics;
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
/// here and the text handed off; storing it is the watch's worker's job. The window is also the owner
/// of what Tomte puts back on the clipboard, which is how the next update knows it is Tomte's own.
/// </summary>
internal sealed unsafe class ClipboardListener : IClipboardListener
{
    private const uint MsgPut = PInvoke.WM_APP + 1;
    private const uint CF_UNICODETEXT = 13;
    private static readonly HWND MessageOnlyParent = new(-3); // HWND_MESSAGE, which CsWin32 does not emit
    private static readonly long Ticks = Stopwatch.Frequency / 1_000_000;

    private readonly Action<string, DateTimeOffset> captured;
    private readonly Func<bool> paused;
    private readonly Func<IReadOnlyList<AppRule>> rules;
    private readonly int maxChars;
    private readonly Action<string>? log;
    private readonly ManualResetEventSlim ready = new(false);
    private readonly Lock gate = new();
    private readonly Thread thread;
    private readonly Source source;
    private HWND window;
    private string? toPut;
    private uint lastSequence;

    private ClipboardListener(Action<string, DateTimeOffset> captured, Func<bool> paused, Func<IReadOnlyList<AppRule>> rules, int maxChars, Action<string>? log)
    {
        (this.captured, this.paused, this.rules, this.maxChars, this.log) = (captured, paused, rules, maxChars, log);
        source = new Source(this);
        thread = new Thread(Loop) { IsBackground = true, Name = "tomte-clipboard" };
    }

    /// <summary>Starts the thread and returns once the window listens.</summary>
    public static ClipboardListener Start(Action<string, DateTimeOffset> captured, Func<bool> paused, Func<IReadOnlyList<AppRule>> rules, int maxChars, Action<string>? log)
    {
        var listener = new ClipboardListener(captured, paused, rules, maxChars, log);
        listener.thread.Start();
        listener.ready.Wait();
        return listener;
    }

    public void Put(string text)
    {
        lock (gate)
        {
            toPut = text;
        }

        PInvoke.PostMessage(window, MsgPut, 0, 0);
    }

    /// <summary>Stops listening and ends the thread. Waits a moment for it, not for an app that hangs on a render.</summary>
    public void Dispose()
    {
        PInvoke.PostMessage(window, PInvoke.WM_CLOSE, 0, 0);
        thread.Join(500);
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
        var read = ClipboardReader.Read(source, paused(), rules(), maxChars);
        long us = (Stopwatch.GetTimestamp() - start) / Ticks;
        if (read.Text is { } text)
        {
            captured(text, DateTimeOffset.Now);
        }

        log?.Invoke($"clipboard: {read.Outcome} in {us} us");
    }

    /// <summary>A picked item back on the clipboard as plain text, owned by this window.</summary>
    private void PutNow()
    {
        string? text;
        lock (gate)
        {
            (text, toPut) = (toPut, null);
        }

        if (text is null || !source.Open())
        {
            log?.Invoke("clipboard: could not put the item back, the clipboard stayed busy");
            return;
        }

        try
        {
            PInvoke.EmptyClipboard();
            nuint bytes = (nuint)(text.Length + 1) * 2;
            var memory = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, bytes);
            if (memory.IsNull)
            {
                return;
            }

            char* p = (char*)PInvoke.GlobalLock(memory);
            text.CopyTo(new Span<char>(p, text.Length));
            p[text.Length] = '\0';
            PInvoke.GlobalUnlock(memory);
            // On success the clipboard owns the memory; otherwise it is still ours to free.
            if (PInvoke.SetClipboardData(CF_UNICODETEXT, (HANDLE)memory.Value).IsNull)
            {
                PInvoke.GlobalFree(memory);
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
