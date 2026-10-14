using System.Diagnostics;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tomte.Core;
using Tomte.Core.Storage;
using Tomte.Core.Text;
using Tomte.Shell;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.System.ProcessStatus;
using Windows.Win32.System.WinRT;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace TextCaptures;

/// <summary>
/// The island's text boxes driven offscreen, as the island drives them but with no window shown: a message-only window
/// stands for the island's, keys go straight into the text services as the window procedure would pass them, and
/// nothing reaches system input or the clipboard. Checks the palette's Enter and Shift+Enter, the note binding with a
/// rambler note landing mid-typing, one paint per turn, the caret and the release on hide, then measures memory before
/// first use, while typing and after hide. Exits 2 when a check fails.
/// Usage: TextCaptures --typing &lt;output folder&gt; [--no-measure]
/// </summary>
internal static unsafe class Typing
{
    private static readonly List<string> Report = [];
    private static int fails;
    private static Compositor compositor = null!;
    private static ContainerVisual root = null!;
    private static HWND window;

    /// <summary>
    /// What a text box needs before the first one, as the shell has it: per-monitor DPI awareness from the manifest
    /// (which changes how the process sees DPI, not the display), OLE on this STA thread for the island's drop target,
    /// and a window for the services, here a message-only one.
    /// </summary>
    public static void Prepare()
    {
        if (!window.IsNull)
        {
            return;
        }

        PInvoke.SetProcessDpiAwarenessContext(new Windows.Win32.UI.HiDpi.DPI_AWARENESS_CONTEXT(-4));
        PInvoke.OleInitialize(null).ThrowOnFailure();
        window = MessageWindow();
    }

    /// <summary>
    /// A text box for a capture: the services sized for <paramref name="box"/> at the scale, holding the text, and with
    /// the keys and the caret at <paramref name="caret"/> when one is given.
    /// </summary>
    public static TextServices Captured(string text, float fontDips, RectangleF box, double scale, int? caret)
    {
        Prepare();
        var services = new TextServices(window, multiline: true, fontDips);
        services.Resize(PixelGrid.Cover(box.Width, scale), PixelGrid.Cover(box.Height, scale), EditorScale.Zoom(scale, TextServices.SystemDpi));
        services.Activate();
        services.Text = text;
        if (caret is int at)
        {
            services.Focus();
            services.Selection = (at, at);
        }

        services.Send(PInvoke.EM_REQUESTRESIZE);
        return services;
    }

    public static int Run(string dir, bool measure)
    {
        Directory.CreateDirectory(dir);
        Prepare();
        PInvoke.CreateDispatcherQueueController(new DispatcherQueueOptions
        {
            dwSize = (uint)sizeof(DispatcherQueueOptions),
            threadType = DISPATCHERQUEUE_THREAD_TYPE.DQTYPE_THREAD_CURRENT,
            apartmentType = DISPATCHERQUEUE_THREAD_APARTMENTTYPE.DQTAT_COM_STA,
        }, out var controller).ThrowOnFailure();
        var queue = controller.DispatcherQueue;
        compositor = new Compositor();
        root = compositor.CreateContainerVisual();
        Log($"machine {Environment.MachineName}, {Environment.OSVersion}, system DPI {TextServices.SystemDpi}, caret blink {PInvoke.GetCaretBlinkTime()} ms");
        Log(Memory("start (OLE, compositor, message-only window)"));

        // What the shell has before a text box is first used: the first layout and the text device.
        TextLayout.Of(new TextRun("Tomte 00:00", new TextStyle(13, 600), 100)).Dispose();
        var d2d = TextDraw.CreateDevice(out ID3D11Device* d3d);
        var graphics = PixelSurfaces.CreateGraphicsDevice(compositor, d2d);
        GC.Collect();
        Log(Memory("before first use (text layout and text device made, msftedit not loaded)"));
        Check("msftedit.dll not loaded before first use", !Msftedit.Loaded);

        // Memory first, while the process has never had a text box.
        if (measure)
        {
            Measure(queue, graphics);
        }

        Prompt(queue, graphics);
        Note(queue, graphics, dir);
        Paints(queue, graphics);
        Undo(queue, graphics);
        Pump();
        Check("msftedit.dll freed after the boxes are disposed", !Msftedit.Loaded);

        graphics.Dispose();
        d2d->Release();
        d3d->Release();
        Log($"typing: {fails} checks failed");
        File.WriteAllLines(Path.Combine(dir, "typing-check.txt"), Report);
        return fails == 0 ? 0 : 2;
    }

    private static EditorView Box(Windows.System.DispatcherQueue queue, CompositionGraphicsDevice graphics, RectangleF box, double scale, bool multiline = true)
    {
        var editor = new EditorView(compositor, queue, root, root, window, multiline, 13);
        editor.Use(graphics);
        editor.Place(box, scale);
        return editor;
    }

    // The palette's prompt: what the island window does with each key while the prompt has the keys.
    private static void Prompt(Windows.System.DispatcherQueue queue, CompositionGraphicsDevice graphics)
    {
        using var prompt = Box(queue, graphics, new RectangleF(0, 0, 580, 18), 1.5);
        var sent = new List<string>();
        prompt.SendOnEnter(sent.Add);
        prompt.Focus();
        var keys = new Keys(prompt);
        keys.Type("hello");
        keys.Press(VIRTUAL_KEY.VK_RETURN, shift: true);
        keys.Type("world");
        Check("Shift+Enter is a new line in the prompt", prompt.Services.Text == "hello\nworld" && sent.Count == 0, Esc(prompt.Services.Text));
        keys.Press(VIRTUAL_KEY.VK_RETURN);
        Check("Enter sends the prompt once and adds nothing", sent is ["hello\nworld"] && prompt.Services.Text == "hello\nworld", $"sent [{string.Join("|", sent.Select(Esc))}] text \"{Esc(prompt.Services.Text)}\"");
        keys.Press(VIRTUAL_KEY.VK_RETURN, ctrl: true);
        Check("Ctrl+Enter sends too and adds no line feed", sent.Count == 2 && prompt.Services.Text == "hello\nworld", Esc(prompt.Services.Text));
        Pump();
        var caret = prompt.Services.Caret;
        Check("the caret is shown while focused", caret.Shown && caret.Height > 0, $"caret {caret}");
        Check("the caret sprite is the only caret: shown in the box", prompt.Visual.Children.First().IsVisible);
        prompt.Blur();
        Pump();
        Check("blurred: the caret sprite is hidden and the services hold no timer", !prompt.Visual.Children.First().IsVisible && prompt.Services.Timers == 0);
    }

    // The scratchpad: a rambler note saved on a worker while the user types never goes missing.
    private static void Note(Windows.System.DispatcherQueue queue, CompositionGraphicsDevice graphics, string dir)
    {
        string data = Path.Combine(dir, "note-db");
        if (Directory.Exists(data))
        {
            Directory.Delete(data, recursive: true);
        }

        Directory.CreateDirectory(data);
        var note = new ScratchpadNote(new TomteDb(Path.Combine(data, "tomte.db")));
        note.LoadAsync().GetAwaiter().GetResult();
        note.Text = "Milk";
        using var box = Box(queue, graphics, new RectangleF(0, 0, 468, 220), 1.25);
        using var binding = new NoteEditor(box, note, queue);
        box.Focus();
        box.Services.Selection = (4, 4);
        var keys = new Keys(box);
        keys.Type(", eggs");
        Check("typing goes to the note", note.Text == "Milk, eggs", Esc(note.Text));

        // The rambler appends on a worker; its Changed is queued to this thread, and the next key comes before that runs.
        note.AppendAsync("Call the dentist", new DateTime(2026, 10, 14, 9, 30, 0)).GetAwaiter().GetResult();
        keys.Type("!");
        Check("a rambler note that landed before the key is kept, the key goes where the caret was",
            note.Text.StartsWith("Milk, eggs!", StringComparison.Ordinal) && note.Text.Contains("Call the dentist") && box.Services.Text == note.Text, Esc(note.Text));

        // The race: the note changes after the box took it in but before the box's text goes back to it.
        var bound = box.BeforeKey!;
        box.BeforeKey = (m, w, l) =>
        {
            bool swallowed = bound(m, w, l);
            if (m == PInvoke.WM_CHAR)
            {
                note.AppendAsync("Buy stamps", new DateTime(2026, 10, 14, 9, 31, 0)).GetAwaiter().GetResult();
            }

            return swallowed;
        };
        keys.Type("?");
        box.BeforeKey = bound;
        Pump();
        Check("a rambler note that lands between the read and the write is kept as well",
            note.Text.Contains("Buy stamps") && note.Text.Contains("Call the dentist") && note.Text.StartsWith("Milk, eggs!?", StringComparison.Ordinal) && box.Services.Text == note.Text,
            Esc(note.Text));
        Check("the caret stays after what the user typed", box.Services.Selection.End == "Milk, eggs!?".Length, $"caret {box.Services.Selection}");

        // A note saved meanwhile by the AI tool replaces the text; the box shows it after the queued change runs.
        note.Text = "Rewritten by the tool";
        Pump();
        Check("a whole-text write reaches the box", box.Services.Text == "Rewritten by the tool", Esc(box.Services.Text));
        note.FlushAsync().GetAwaiter().GetResult();
        Log($"note in the store: \"{Esc(note.Text)}\"");
    }

    // Many changes in one turn paint once, in the next turn.
    private static void Paints(Windows.System.DispatcherQueue queue, CompositionGraphicsDevice graphics)
    {
        using var box = Box(queue, graphics, new RectangleF(0, 0, 468, 220), 1.5);
        box.Focus();
        Pump();
        int before = box.Paints;
        var keys = new Keys(box);
        keys.Type("twenty characters!!!");
        int during = box.Paints - before;
        Pump();
        int after = box.Paints - before;
        Check("twenty keys in one turn paint nothing until the loop turns, then once", during == 0 && after == 1, $"during {during}, after {after}");
    }

    // A change made for the note (no undo step) and the user's own undo.
    private static void Undo(Windows.System.DispatcherQueue queue, CompositionGraphicsDevice graphics)
    {
        using var box = Box(queue, graphics, new RectangleF(0, 0, 468, 220), 1.5);
        box.Focus();
        var keys = new Keys(box);
        keys.Type("abc");
        keys.Press(VIRTUAL_KEY.VK_Z, ctrl: true);
        Check("Ctrl+Z undoes typing", box.Services.Text == "", Esc(box.Services.Text));
        keys.Type("abc");
        box.Services.Replace(new Splice(3, 0, "\n\n09:30 rambler"));
        keys.Press(VIRTUAL_KEY.VK_Z, ctrl: true);
        string afterUndo = box.Services.Text;
        Log($"Ctrl+Z after a change made for the note leaves \"{Esc(afterUndo)}\"");
        Check("Ctrl+Z after a change made for the note never takes the note's change out", afterUndo.Contains("09:30 rambler"), Esc(afterUndo));
    }

    private static void Measure(Windows.System.DispatcherQueue queue, CompositionGraphicsDevice graphics)
    {
        var box = Box(queue, graphics, new RectangleF(0, 0, 492, 228), 1.5);
        box.Focus();
        Pump();
        Log(Memory("first box shown and focused"));
        var keys = new Keys(box);
        var times = new List<double>();
        string line = "The quick brown fox jumps over the lazy dog. ";
        for (int i = 0; i < 1000; i++)
        {
            long t = Stopwatch.GetTimestamp();
            keys.Type(line[i % line.Length].ToString());
            Pump();
            times.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
        }

        Log(Memory("while typing (after 1,000 keys, each painted)"));
        times.Sort();
        Log($"per key, handled and painted: median {times[times.Count / 2]:F3} ms, p95 {times[(int)(times.Count * 0.95)]:F3} ms, max {times[^1]:F3} ms; paints {box.Paints}");
        box.Dispose();
        Pump();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Rest(2000);
        Log(Memory("after hide (box disposed, msftedit freed, 2 s later, after a GC)"));
        Log($"msftedit.dll in the process after hide: {Msftedit.Loaded}");
    }

    private static void Check(string what, bool ok, string detail = "")
    {
        if (!ok)
        {
            fails++;
        }

        Log($"{(ok ? "ok  " : "FAIL")} {what}{(detail.Length > 0 ? ": " + detail : "")}");
    }

    private static void Log(string line)
    {
        Report.Add(line);
        Console.WriteLine(line);
    }

    private static string Esc(string s) => s.Replace("\n", "\\n").Replace("\r", "\\r");

    private static string Memory(string when)
    {
        var counters = new PROCESS_MEMORY_COUNTERS_EX2 { cb = (uint)sizeof(PROCESS_MEMORY_COUNTERS_EX2) };
        PInvoke.K32GetProcessMemoryInfo(PInvoke.GetCurrentProcess(), (PROCESS_MEMORY_COUNTERS*)&counters, counters.cb);
        return $"memory {when}: private working set {counters.PrivateWorkingSetSize / 1048576.0:F1} MB, working set {counters.WorkingSetSize / 1048576.0:F1} MB";
    }

    /// <summary>One turn of the loop: everything queued, the dispatcher's paints included.</summary>
    private static void Pump()
    {
        for (int i = 0; i < 4; i++)
        {
            while (PInvoke.PeekMessage(out MSG msg, HWND.Null, 0, 0, PEEK_MESSAGE_REMOVE_TYPE.PM_REMOVE))
            {
                PInvoke.TranslateMessage(in msg);
                PInvoke.DispatchMessage(in msg);
            }
        }
    }

    private static void Rest(int ms)
    {
        var t = Stopwatch.StartNew();
        while (t.ElapsedMilliseconds < ms)
        {
            Pump();
            Thread.Sleep(15);
        }
    }

    private static HWND MessageWindow()
    {
        fixed (char* name = "TomteTypingCheck")
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &WndProc,
                hInstance = (HINSTANCE)(nint)PInvoke.GetModuleHandle((PCWSTR)null),
                lpszClassName = name,
            };
            PInvoke.RegisterClassEx(in wc);
            // HWND_MESSAGE: never shown, never in the z-order.
            return PInvoke.CreateWindowEx(0, name, name, 0, 0, 0, 0, 0, new HWND(-3), HMENU.Null, HINSTANCE.Null, null);
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT WndProc(HWND hwnd, uint msg, WPARAM wParam, LPARAM lParam) => PInvoke.DefWindowProc(hwnd, msg, wParam, lParam);

    /// <summary>
    /// What a window gets for a key (WM_KEYDOWN, the WM_CHAR TranslateMessage makes, WM_KEYUP), handed to the box as the
    /// island's window procedure hands it. Ctrl and Shift also go into this thread's own key-state table, which the
    /// services read; no other thread or process sees it.
    /// </summary>
    private sealed class Keys(EditorView box)
    {
        public void Type(string text)
        {
            foreach (char c in text)
            {
                short scan = char.IsSurrogate(c) ? (short)-1 : PInvoke.VkKeyScan(c);
                if (scan == -1)
                {
                    Send(PInvoke.WM_CHAR, c, 1);
                    continue;
                }

                var vk = (VIRTUAL_KEY)(scan & 0xFF);
                bool shift = (scan & 0x100) != 0;
                State(ctrl: false, shift, down: true);
                Send(PInvoke.WM_KEYDOWN, (nuint)vk, Param(vk, up: false));
                Send(PInvoke.WM_CHAR, c, Param(vk, up: false));
                Send(PInvoke.WM_KEYUP, (nuint)vk, Param(vk, up: true));
                State(ctrl: false, shift, down: false);
            }
        }

        public void Press(VIRTUAL_KEY vk, bool ctrl = false, bool shift = false)
        {
            State(ctrl, shift, down: true);
            Send(PInvoke.WM_KEYDOWN, (nuint)vk, Param(vk, up: false));
            char? c = vk switch
            {
                VIRTUAL_KEY.VK_RETURN => ctrl ? '\n' : '\r',
                VIRTUAL_KEY.VK_BACK => '\b',
                >= VIRTUAL_KEY.VK_A and <= VIRTUAL_KEY.VK_Z when ctrl => (char)(vk - VIRTUAL_KEY.VK_A + 1),
                _ => null,
            };
            if (c is char ch)
            {
                Send(PInvoke.WM_CHAR, ch, Param(vk, up: false));
            }

            Send(PInvoke.WM_KEYUP, (nuint)vk, Param(vk, up: true));
            State(ctrl, shift, down: false);
        }

        private void Send(uint msg, nuint wParam, nint lParam)
        {
            if (box.BeforeKey?.Invoke(msg, wParam, lParam) != true)
            {
                box.Key(msg, wParam, lParam, out _);
            }
        }

        private static nint Param(VIRTUAL_KEY vk, bool up)
        {
            uint scan = PInvoke.MapVirtualKey((uint)vk, MAP_VIRTUAL_KEY_TYPE.MAPVK_VK_TO_VSC);
            uint lp = 1 | (scan << 16);
            return (nint)(up ? lp | (1u << 30) | (1u << 31) : lp);
        }

        private static void State(bool ctrl, bool shift, bool down)
        {
            if (!ctrl && !shift)
            {
                return;
            }

            byte* state = stackalloc byte[256];
            PInvoke.GetKeyboardState(state);
            byte v = down ? (byte)0x80 : (byte)0;
            if (ctrl)
            {
                state[(int)VIRTUAL_KEY.VK_CONTROL] = state[(int)VIRTUAL_KEY.VK_LCONTROL] = v;
            }

            if (shift)
            {
                state[(int)VIRTUAL_KEY.VK_SHIFT] = state[(int)VIRTUAL_KEY.VK_LSHIFT] = v;
            }

            PInvoke.SetKeyboardState(state);
        }
    }
}
