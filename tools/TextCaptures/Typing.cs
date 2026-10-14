using System.Diagnostics;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using Tomte.Core;
using Tomte.Core.Storage;
using Tomte.Core.Text;
using Tomte.Shell;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct3D11;
using Windows.Win32.UI.Controls.RichEdit;
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
    /// the keys and the caret at <paramref name="caret"/> when one is given, its Markdown styled as the note's is when
    /// <paramref name="markdown"/>.
    /// </summary>
    public static TextServices Captured(string text, float fontDips, RectangleF box, double scale, int? caret, bool markdown = false)
    {
        Prepare();
        var services = new TextServices(window, multiline: true, fontDips) { Rich = markdown };
        services.Resize(PixelGrid.Cover(box.Width, scale), PixelGrid.Cover(box.Height, scale), EditorScale.Zoom(scale, TextServices.SystemDpi));
        services.Activate();
        services.Text = text;
        if (markdown)
        {
            // As the note styles itself; the looks stay in the services after the styling is let go.
            using var style = new NoteStyle(services);
            style.Apply(services.Text, 0, services.Text.Length);
        }

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
        Styled(queue, graphics, dir);
        if (measure)
        {
            StyleTimes(queue, graphics, dir);
        }

        Paints(queue, graphics);
        Undo(queue, graphics);
        Pump();
        Kept(queue, graphics, dir);
        Pump();
        Check("msftedit.dll freed after the boxes are disposed", !Msftedit.Loaded);

        // Last, because it keeps the module for the rest of the process by design.
        Msftedit.Acquire();
        Check("a box whose services are still held keeps msftedit.dll loaded", !Msftedit.Release(servicesLeft: 1) && Msftedit.Loaded);

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
        using var binding = new NoteEditor(box, note, queue, new StringBuilder());
        box.Focus();
        box.Services.Selection = (4, 4);
        var keys = new Keys(box);
        keys.Type(", eggs");
        // The box's change reaches the note on the next turn, never inside the services' own notification.
        Pump();
        Check("typing goes to the note on the next turn", note.Text == "Milk, eggs", Esc(note.Text));

        // The rambler appends on a worker; its Changed is queued to this thread, and the next key comes before that runs.
        note.AppendAsync("Call the dentist", new DateTime(2026, 10, 14, 9, 30, 0)).GetAwaiter().GetResult();
        keys.Type("!");
        Pump();
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
        // A note with Windows line breaks: the box and the binding count each break as one character.
        note.Text = "one\r\ntwo";
        Pump();
        note.Text = "one\r\ntwo\r\nthree";
        Pump();
        Check("CR LF in the note: the box shows one break per line and follows a change to it", box.Services.Text == "one\ntwo\nthree", Esc(box.Services.Text));
        box.Services.Selection = (3, 3);
        keys.Type("!");
        Pump();
        Check("a key after it lands where the caret is", note.Text == "one!\ntwo\nthree", Esc(note.Text));
        note.FlushAsync().GetAwaiter().GetResult();
        Log($"note in the store: \"{Esc(note.Text)}\"");
    }

    // The note's Markdown styled in place: every construct looks as it should, the text stays what was typed, the caret
    // and the selection stay, an edit restyles its lines, undo only undoes text, and the box's own shortcuts do nothing.
    private static void Styled(Windows.System.DispatcherQueue queue, CompositionGraphicsDevice graphics, string dir)
    {
        const string text = "# Plan\nSome **bold**, *it*, ~~old~~ and `code`.\n- [x] done item\n- [ ] open item\n"
            + "1. [link](https://x.y) and https://example.com\n```\nfenced\n```\nafter";
        var note = new ScratchpadNote(new TomteDb(Path.Combine(dir, "note-db", "styled.db")));
        note.LoadAsync().GetAwaiter().GetResult();
        note.Text = text;
        using var box = Box(queue, graphics, new RectangleF(0, 0, 468, 220), 1.5);
        using var binding = new NoteEditor(box, note, queue, new StringBuilder());
        box.Focus();
        Pump();
        var s = box.Services;
        int At(string what) => text.IndexOf(what, StringComparison.Ordinal);
        Check("styling leaves the text as typed", s.Text == text, Esc(s.Text));
        var hash = Look(s, 0);
        var heading = Look(s, At("Plan"));
        Check("a heading is larger and semibold, its # dimmed", heading.Size == 18 && heading.Weight == 600 && heading.Fore == White && hash.Fore == Dim && hash.Size == 18, $"{heading} / {hash}");
        Check("**bold** is bold with its markers dimmed", Look(s, At("bold")).Weight == 700 && Look(s, At("**bold")).Fore == Dim, $"{Look(s, At("bold"))}");
        Check("*italic* is italic", Look(s, At("it*")).Italic && !Look(s, At("*it")).Italic, $"{Look(s, At("it*"))}");
        Check("~~strike~~ is struck", Look(s, At("old~~")).Struck && Look(s, At("~~old")).Fore == Dim, $"{Look(s, At("old~~"))}");
        var code = Look(s, At("code`"));
        Check("`code` is monospace on a faint background", code.Font == "Consolas" && code.Back == CodeBack && code.Fore == CodeText, $"{code}");
        var done = Look(s, At("done"));
        var box1 = Look(s, At("- [x]"));
        Check("a checked task is dimmed and struck, its box dimmed only", done.Struck && done.Fore == Dim && box1.Fore == Dim && !box1.Struck, $"{done} / {box1}");
        var open = Look(s, At("open"));
        Check("an open task is plain, a list item hangs", !open.Struck && open.Fore == White && open.Hang > 10, $"{open}");
        var link = Look(s, At("link"));
        var address = Look(s, At("x.y"));
        var bare = Look(s, At("example.com"));
        Check("a link's text is a link, its address dimmed; a bare address is a link", link.Fore == LinkColor && address.Fore == Dim && bare.Fore == LinkColor, $"{link} / {address} / {bare}");
        var fenced = Look(s, At("fenced"));
        var fence = Look(s, At("```"));
        Check("a fenced block is monospace, its fences dimmed", fenced.Font == "Consolas" && fence.Font == "Consolas" && fence.Fore == Dim, $"{fenced} / {fence}");
        var plain = Look(s, At("after"));
        Check("plain text is the box's own look", plain is { Font: TextStyle.Ui, Size: 13, Weight: 400, Italic: false, Struck: false, Fore: White, Hang: 0 }, $"{plain}");

        // An edit restyles its lines as it is typed, a key and a paint at a time, and leaves the caret after it.
        var keys = new Keys(box);
        s.Selection = (text.Length, text.Length);
        foreach (char c in " **x**")
        {
            keys.Type(c.ToString());
            Pump();
        }

        Check("typing **x** makes it bold at once and the caret stays at the end", Look(s, s.Text.Length - 3).Weight == 700 && s.Selection == (s.Text.Length, s.Text.Length) && note.Text == s.Text, $"{Look(s, s.Text.Length - 3)} caret {s.Selection}");

        // Undo undoes text only: the styling between the keys is no step of its own, and the typing is one step, as
        // in a box with nothing styled.
        keys.Press(VIRTUAL_KEY.VK_Z, ctrl: true);
        Pump();
        Check("one Ctrl+Z takes the typed run out, and its look with it", s.Text == text && Look(s, At("after")) == plain, Esc(s.Text[^12..]));
        s.Selection = (0, 0);
        keys.Press(VIRTUAL_KEY.VK_DELETE);
        Pump();
        keys.Press(VIRTUAL_KEY.VK_DELETE);
        Pump();
        Check("taking the # away makes the heading plain", Look(s, 0) is { Size: 13, Weight: 400 } && s.Text.StartsWith("Plan\n", StringComparison.Ordinal), $"{Look(s, 0)}");
        keys.Press(VIRTUAL_KEY.VK_Z, ctrl: true);
        keys.Press(VIRTUAL_KEY.VK_Z, ctrl: true);
        Pump();
        Check("Ctrl+Z puts the # back, a heading again", s.Text == text && Look(s, At("Plan")) == heading, $"\"{Esc(s.Text[..8])}\" {Look(s, At("Plan"))}");

        // The box's own formatting shortcuts do nothing in the note; a rich box without the note's keys takes them. The
        // selection keeps its place.
        VIRTUAL_KEY[] shortcuts = [VIRTUAL_KEY.VK_E, VIRTUAL_KEY.VK_R, VIRTUAL_KEY.VK_2, VIRTUAL_KEY.VK_OEM_PLUS, VIRTUAL_KEY.VK_B];
        using (var other = Box(queue, graphics, new RectangleF(0, 0, 468, 220), 1.5))
        {
            other.Services.Rich = true;
            other.Services.Text = "after";
            other.Focus();
            var took = new List<VIRTUAL_KEY>();
            foreach (var vk in shortcuts)
            {
                string before = Format(other.Services, 0);
                other.Services.Selection = (0, 5);
                new Keys(other).Press(vk, ctrl: true);
                if (Format(other.Services, 0) != before)
                {
                    took.Add(vk);
                }
            }

            Log($"a rich box without the note's keys formats on Ctrl with: {string.Join(", ", took)}");
        }

        int after = At("after");
        string unformatted = Format(s, after);
        foreach (var vk in shortcuts)
        {
            s.Selection = (after, after + 5);
            keys.Press(vk, ctrl: true);
        }

        Pump();
        Check("in the note those shortcuts change nothing", Format(s, after) == unformatted && s.Text == text && s.Selection == (after, after + 5), $"{Format(s, after)} {s.Selection}");

        // A fence opened above turns what follows into code; closing it again turns it back.
        s.Selection = (At("after"), At("after"));
        keys.Type("```\n");
        Pump();
        Check("a new fence makes the lines after it code", Look(s, s.Text.IndexOf("after", StringComparison.Ordinal)).Font == "Consolas", $"{Look(s, s.Text.IndexOf("after", StringComparison.Ordinal))}");
        keys.Press(VIRTUAL_KEY.VK_Z, ctrl: true);
        Pump();
        Check("undoing it makes them plain again", s.Text == text && Look(s, At("after")) == plain, $"{Look(s, At("after"))}");

        // Nothing is restyled while an input method composes. Offscreen there is no input method: the services start a
        // composition on WM_IME_STARTCOMPOSITION, take no typed characters then and never end it, so the text changes
        // as a rambler note would change it, and the next key the input method did not take ends the composition.
        box.Key(PInvoke.WM_IME_STARTCOMPOSITION, 0, 0, out _);
        Log($"WM_IME_STARTCOMPOSITION offscreen: the services report a composition: {s.Composing}");
        s.Replace(new Splice(s.Text.Length, 0, " **y**"));
        Pump();
        bool unstyled = Look(s, s.Text.Length - 3).Weight != 700;
        box.Key(PInvoke.WM_IME_ENDCOMPOSITION, 0, 0, out _);
        keys.Press(VIRTUAL_KEY.VK_END);
        Pump();
        Check("nothing is restyled while composing, a key after it restyles", s.Text.EndsWith(" **y**", StringComparison.Ordinal) && unstyled && !s.Composing && Look(s, s.Text.Length - 3).Weight == 700,
            $"\"{Esc(s.Text[^8..])}\" unstyled while composing {unstyled}, composing {s.Composing}, {Look(s, s.Text.Length - 3)}");

        // Rich text set through the services' own calls is read as RTF when it starts with {\rtf; the note's never is,
        // and a change made for it is no undo step either.
        using (var other = Box(queue, graphics, new RectangleF(0, 0, 468, 220), 1.5))
        {
            other.Services.Rich = true;
            other.Services.Text = "{\\rtf1 hi}";
            other.Focus();
            other.Services.Selection = (other.Services.Text.Length, other.Services.Text.Length);
            new Keys(other).Type(" typed");
            other.Services.Replace(new Splice(other.Services.Text.Length, 0, "{\\rtf1 there}"));
            Check("text that looks like RTF stays as typed, set whole or changed for the note", other.Services.Text == "{\\rtf1 hi} typed{\\rtf1 there}", Esc(other.Services.Text));
            new Keys(other).Press(VIRTUAL_KEY.VK_Z, ctrl: true);
            Check("Ctrl+Z after a change made for a rich note never takes it out", other.Services.Text == "{\\rtf1 hi} typed{\\rtf1 there}", Esc(other.Services.Text));
            new Keys(other).Press(VIRTUAL_KEY.VK_A, ctrl: true);
            Log($"Ctrl+A in a rich box selects {other.Services.Selection} of {other.Services.Text.Length} characters: its last paragraph mark too");
        }

        // The services zoom the text for the monitor's scale but not the indents: on a 100% monitor they are set again.
        box.Place(box.Box, 1);
        Pump();
        float zoomed = Look(s, At("open")).Hang;
        Check("at another scale a list item's hang follows the zoom", MathF.Abs(zoomed - open.Hang * s.Zoom) < 0.2f, $"{zoomed} at zoom {s.Zoom:F3}, {open.Hang} at 1");
    }

    // How long styling takes on the island's thread, in a note of 2,000 lines of Markdown: opening it, a key in its
    // middle, and a fence typed there, which restyles everything after it.
    private static void StyleTimes(Windows.System.DispatcherQueue queue, CompositionGraphicsDevice graphics, string dir)
    {
        var lines = Enumerable.Range(0, 2000).Select(i => (i % 5) switch
        {
            0 => $"## Section {i}",
            1 => $"- item {i} with **bold** and `code`",
            2 => $"Plain line {i} with *italic* and a [link](https://example.com/{i})",
            3 => $"1. [ ] task {i}",
            _ => "",
        });
        // A box bound to a note of this text, opened and painted, with each paint's styling timed.
        (EditorView, NoteEditor, List<double>) Opened(string text, string name)
        {
            var note = new ScratchpadNote(new TomteDb(Path.Combine(dir, "note-db", name)));
            note.LoadAsync().GetAwaiter().GetResult();
            note.Text = text;
            var box = Box(queue, graphics, new RectangleF(0, 0, 468, 182), 1.5);
            var times = new List<double>();
            var binding = new NoteEditor(box, note, queue, new StringBuilder());
            var restyle = box.BeforePaint!;
            box.BeforePaint = () =>
            {
                long t = Stopwatch.GetTimestamp();
                restyle();
                times.Add(Stopwatch.GetElapsedTime(t).TotalMilliseconds);
            };
            box.Focus();
            Pump();
            Log($"styling: a note of {text.Count(c => c == '\n') + 1:N0} lines ({text.Length:N0} characters) styled on open in {times.Max():F1} ms");
            return (box, binding, times);
        }

        var (smallBox, small, _) = Opened(string.Join("\n", lines.Take(100)), "times-100.db");
        small.Dispose();
        smallBox.Dispose();
        string text = string.Join("\n", lines);
        GC.Collect();
        Log(Memory("before the 2,000-line note"));
        var (box, binding, times) = Opened(text, "times.db");
        using var disposeBox = box;
        using var disposeBinding = binding;
        Log(Memory("the 2,000-line note open and styled"));
        int middle = text.IndexOf("Plain line 1002", StringComparison.Ordinal) + "Plain line 1002".Length;
        box.Services.Selection = (middle, middle);
        var keys = new Keys(box);
        times.Clear();
        string typed = " and a **few** more words with `code` in them, typed one key at a time.";
        foreach (char c in typed)
        {
            keys.Type(c.ToString());
            Pump();
        }

        var each = times.Where(t => t > 0).Order().ToList();
        Log($"styling: per key in the middle of it, {each.Count} keys: median {each[each.Count / 2]:F3} ms, p95 {each[(int)(each.Count * 0.95)]:F3} ms, max {each[^1]:F3} ms");
        Check("styling after a key in a 2,000-line note takes under 2 ms (median)", each[each.Count / 2] < 2, $"{each[each.Count / 2]:F3} ms");
        times.Clear();
        keys.Type("\n```");
        Pump();
        Log($"styling: a fence typed in the middle, which restyles the 1,000 lines after it: {times.Max():F1} ms");
        Log(Memory("after typing in it"));
        binding.Dispose();
        box.Dispose();
        Pump();
        GC.Collect();
        GC.WaitForPendingFinalizers();
        Rest(2000);
        Log(Memory("after hide (the note's box disposed, 2 s later, after a GC)"));
    }

    // COLORREF, as the note's styling sets them.
    private const int White = 0xFFFFFF, Dim = 0xA3A3A3, LinkColor = 0xFFA86F, CodeText = 0xDED6D6, CodeBack = 0x2C2A2A;

    private readonly record struct CharLook(string Font, float Size, int Weight, bool Italic, bool Struck, int Fore, int Back, float Hang);

    // What a formatting shortcut can change at a place: its look, and its line's alignment and spacing, its subscript.
    private static string Format(TextServices services, int at)
    {
        var document = services.Document();
        ITextRange* range;
        document->Range(at, at + 1, &range);
        ITextFont* font;
        ITextPara* para;
        range->GetFont(&font);
        range->GetPara(&para);
        int align, sub;
        float spacing;
        para->GetAlignment(&align);
        para->GetLineSpacing(&spacing);
        font->GetSubscript(&sub);
        para->Release();
        font->Release();
        range->Release();
        document->Release();
        return $"{Look(services, at)}, alignment {align}, spacing {spacing}, subscript {sub}";
    }

    // How the character at a place in a box looks, sizes in DIPs, and how far its line's wrapped lines hang.
    private static CharLook Look(TextServices services, int at)
    {
        var document = services.Document();
        ITextRange* range;
        document->Range(at, at + 1, &range);
        ITextFont* font;
        ITextPara* para;
        range->GetFont(&font);
        range->GetPara(&para);
        BSTR name;
        float size, hang;
        int weight, italic, struck, fore, back;
        font->GetName(&name);
        font->GetSize(&size);
        font->GetWeight(&weight);
        font->GetItalic(&italic);
        font->GetStrikeThrough(&struck);
        font->GetForeColor(&fore);
        font->GetBackColor(&back);
        para->GetLeftIndent(&hang);
        var look = new CharLook(name.ToString() ?? "", MathF.Round(size / 0.75f, 1), weight, italic != 0, struck != 0, fore, back, MathF.Round(hang / 0.75f, 1));
        PInvoke.SysFreeString(name);
        para->Release();
        font->Release();
        range->Release();
        document->Release();
        return look;
    }

    // Typed before the note could be read: kept, and in once it opens, Backspace included.
    private static void Kept(Windows.System.DispatcherQueue queue, CompositionGraphicsDevice graphics, string dir)
    {
        var note = new ScratchpadNote(new TomteDb(Path.Combine(dir, "note-db", "kept.db")));
        var kept = new StringBuilder();
        using var box = Box(queue, graphics, new RectangleF(0, 0, 468, 220), 1.5);
        using (var early = new NoteEditor(box, note, queue, kept))
        {
            box.Focus();
            new Keys(box).Type("buy milkk\b");
            Check("before the note is read nothing reaches the box, the keys are kept", box.Services.Text == "" && kept.ToString() == "buy milk", $"box \"{Esc(box.Services.Text)}\", kept \"{kept}\"");
        }

        // The panel shows again later and the note can be read now.
        note.LoadAsync().GetAwaiter().GetResult();
        using var later = new NoteEditor(box, note, queue, kept);
        Pump();
        Check("what was kept goes in once the note opens", note.Text == "buy milk" && kept.Length == 0, Esc(note.Text));
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
