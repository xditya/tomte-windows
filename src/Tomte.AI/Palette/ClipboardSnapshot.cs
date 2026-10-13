using System.Diagnostics;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Memory;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.AI.Palette;

/// <summary>One saved clipboard format: bytes for HGLOBAL data, a copied handle for GDI bitmaps and metafiles.</summary>
internal sealed record ClipFormat(uint Id, byte[]? Bytes, HANDLE Gdi, bool IsMetafile);

/// <summary>
/// Win32 clipboard access for the Ctrl+C fallback and the copy tool: snapshot every
/// format, send Ctrl+C, read the copy, put everything back in order. Runs on a thread with a
/// message loop and pumps while waiting, because whoever empties the clipboard next sends
/// WM_DESTROYCLIPBOARD to the owner and blocks until it is handled.
/// </summary>
internal static unsafe class ClipboardSnapshot
{
    private const uint CF_BITMAP = 2, CF_METAFILEPICT = 3, CF_PALETTE = 9, CF_UNICODETEXT = 13, CF_ENHMETAFILE = 14,
        CF_OWNERDISPLAY = 0x80, CF_DSPBITMAP = 0x82, CF_DSPENHMETAFILE = 0x8E;

    private static HWND owner;

    /// <summary>A message-only owner window, created on first use: with a null owner SetClipboardData fails after EmptyClipboard.</summary>
    private static HWND Owner
    {
        get
        {
            if (owner.IsNull)
                owner = PInvoke.CreateWindowEx(0, "STATIC", "tomte-clipboard", 0, 0, 0, 0, 0, HWND.HWND_MESSAGE, null, null, null);
            return owner;
        }
    }

    public static uint Sequence => PInvoke.GetClipboardSequenceNumber();

    public static void SetText(string text)
    {
        Open();
        try
        {
            PInvoke.EmptyClipboard();
            PInvoke.SetClipboardData(CF_UNICODETEXT, Alloc(Encoding.Unicode.GetBytes(text + "\0")));
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    /// <summary>The selection copied by Ctrl+C in the foreground window, or null when nothing was copied within the timeout.</summary>
    public static (string? Text, string? ChromiumMime)? CopySelection(int timeoutMs = 250)
    {
        // A held Win or Alt from the hotkey would turn Ctrl+C into another chord.
        var wait = Stopwatch.StartNew();
        while (ModifierDown() && wait.ElapsedMilliseconds < 2000)
            Pump(5);

        var before = Snapshot();
        uint s0 = Sequence;
        SendChord(VIRTUAL_KEY.VK_CONTROL, VIRTUAL_KEY.VK_C);
        var t = Stopwatch.StartNew();
        // polls the sequence number every 2 ms for at most the timeout; a clipboard listener window is the upgrade.
        while (Sequence == s0 && t.ElapsedMilliseconds < timeoutMs)
            Pump(2);
        if (Sequence == s0)
        {
            Discard(before);
            return null;
        }

        // Some apps write their formats one call at a time.
        Pump(20);
        var copied = ReadCopy();
        Restore(before);
        return copied;
    }

    private static (string? Text, string? ChromiumMime) ReadCopy()
    {
        Open();
        try
        {
            string? text = Bytes(PInvoke.GetClipboardData(CF_UNICODETEXT)) is { } t ? Encoding.Unicode.GetString(t).Split('\0')[0] : null;
            // VS Code marks a whole-line copy made with nothing selected inside this format.
            string? mime = Bytes(PInvoke.GetClipboardData(PInvoke.RegisterClipboardFormat("Chromium Web Custom MIME Data Format"))) is { } m
                ? Encoding.Unicode.GetString(m) + Encoding.UTF8.GetString(m)
                : null;
            return (text, mime);
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    private static List<ClipFormat> Snapshot()
    {
        Open();
        try
        {
            var ids = new List<uint>();
            for (uint f = PInvoke.EnumClipboardFormats(0); f != 0; f = PInvoke.EnumClipboardFormats(f))
                ids.Add(f);
            var list = new List<ClipFormat>();
            foreach (var id in ids)
            {
                var h = PInvoke.GetClipboardData(id);
                if (h.IsNull)
                    continue;
                switch (id)
                {
                    case CF_BITMAP or CF_DSPBITMAP:
                        list.Add(new(id, null, (HANDLE)PInvoke.CopyImage(h, GDI_IMAGE_TYPE.IMAGE_BITMAP, 0, 0, 0).Value, false));
                        continue;
                    case CF_ENHMETAFILE or CF_DSPENHMETAFILE:
                        list.Add(new(id, null, (HANDLE)PInvoke.CopyEnhMetaFile((HENHMETAFILE)h.Value, null).Value, true));
                        continue;
                    // Handle-in-struct and private handle formats cannot be copied; Windows re-synthesises CF_METAFILEPICT from CF_ENHMETAFILE.
                    case CF_METAFILEPICT or CF_PALETTE or CF_OWNERDISPLAY or (>= 0x300 and <= 0x3FF):
                        continue;
                }

                if (Bytes(h) is { } bytes)
                    list.Add(new(id, bytes, HANDLE.Null, false));
            }

            return list;
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    /// <summary>Puts every saved format back in its original order. GDI handles pass to the clipboard, so a snapshot restores once.</summary>
    private static void Restore(List<ClipFormat> snapshot)
    {
        Open();
        try
        {
            PInvoke.EmptyClipboard();
            foreach (var f in snapshot)
                PInvoke.SetClipboardData(f.Id, f.Bytes is null ? f.Gdi : Alloc(f.Bytes));
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    private static void Discard(List<ClipFormat> snapshot)
    {
        foreach (var f in snapshot.Where(f => !f.Gdi.IsNull))
        {
            if (f.IsMetafile)
                PInvoke.DeleteEnhMetaFile((HENHMETAFILE)f.Gdi.Value);
            else
                PInvoke.DeleteObject((HGDIOBJ)f.Gdi.Value);
        }
    }

    private static byte[]? Bytes(HANDLE h)
    {
        if (h.IsNull)
            return null;
        var g = (HGLOBAL)h.Value;
        nuint size = PInvoke.GlobalSize(g);
        void* p = size == 0 ? null : PInvoke.GlobalLock(g);
        if (p == null)
            return null;
        try
        {
            return new ReadOnlySpan<byte>(p, checked((int)size)).ToArray();
        }
        finally
        {
            PInvoke.GlobalUnlock(g);
        }
    }

    private static HANDLE Alloc(byte[] bytes)
    {
        var g = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)Math.Max(1, bytes.Length));
        void* p = PInvoke.GlobalLock(g);
        bytes.CopyTo(new Span<byte>(p, bytes.Length));
        PInvoke.GlobalUnlock(g);
        return (HANDLE)g.Value;
    }

    /// <summary>Clipboard history and other monitors open the clipboard right after every change; retry instead of failing.</summary>
    private static void Open()
    {
        for (int i = 0; i < 200; i++)
        {
            if (PInvoke.OpenClipboard(Owner))
                return;
            Pump(5);
        }

        throw new InvalidOperationException("The clipboard stayed open in another app for a second.");
    }

    /// <summary>Waits while dispatching messages, so a pending WM_DESTROYCLIPBOARD never deadlocks the other app.</summary>
    private static void Pump(int ms)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            while (PInvoke.PeekMessage(out var m, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
            {
                PInvoke.TranslateMessage(in m);
                PInvoke.DispatchMessage(in m);
            }

            int left = ms - (int)sw.ElapsedMilliseconds;
            if (left <= 0)
                return;
            PInvoke.MsgWaitForMultipleObjects(null, false, (uint)left, QUEUE_STATUS_FLAGS.QS_ALLINPUT);
        }
    }

    private static bool ModifierDown() =>
        Down(VIRTUAL_KEY.VK_LWIN) || Down(VIRTUAL_KEY.VK_RWIN) || Down(VIRTUAL_KEY.VK_MENU) || Down(VIRTUAL_KEY.VK_SHIFT) || Down(VIRTUAL_KEY.VK_CONTROL);

    private static bool Down(VIRTUAL_KEY vk) => (PInvoke.GetAsyncKeyState((int)vk) & 0x8000) != 0;

    private static void SendChord(VIRTUAL_KEY modifier, VIRTUAL_KEY key)
    {
        ReadOnlySpan<INPUT> inputs = [Key(modifier, false), Key(key, false), Key(key, true), Key(modifier, true)];
        PInvoke.SendInput(inputs, sizeof(INPUT));
    }

    private static INPUT Key(VIRTUAL_KEY vk, bool up) => new()
    {
        type = INPUT_TYPE.INPUT_KEYBOARD,
        Anonymous = { ki = new KEYBDINPUT { wVk = vk, dwFlags = up ? KEYBD_EVENT_FLAGS.KEYEVENTF_KEYUP : 0 } },
    };
}
