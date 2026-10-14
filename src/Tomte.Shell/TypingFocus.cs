using System.Numerics;
using Tomte.Core;
using Windows.UI.Composition;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Shell;

/// <summary>
/// Who has the keys. The island never activates on its own (WS_EX_NOACTIVATE and MA_NOACTIVATE stay): only a typing
/// hotkey or a click into a text box makes it the foreground window, which Windows allows because that input was
/// the last one and went to this process. The window that was in front is remembered, and a hide or Esc hands the
/// foreground back to it. When the user moves the foreground elsewhere themselves, nothing is handed back.
/// The boxes are made here, so each one draws on the text device and is known for clicks and the cursor.
/// Island thread only.
/// </summary>
internal sealed class TypingFocus
{
    private const nuint MkLButton = 1;
    private readonly IslandWindow island;
    private readonly IslandStateMachine machine;
    private readonly TextRenderer text;
    private readonly Action<string>? log;
    private readonly List<EditorView> editors = [];
    private EditorView? focused, dragging;
    private HWND previous;
    private bool handBackQueued;
    private long lastPress;
    private Vector2 lastPressAt;

    public TypingFocus(IslandWindow island, IslandStateMachine machine, TextRenderer text, Action<string>? log)
    {
        this.island = island;
        this.machine = machine;
        this.text = text;
        this.log = log;
        island.Typing = OnMessage;
        island.Pressed += OnPressed;
        island.LayoutChanged += () =>
        {
            foreach (var editor in editors)
            {
                editor.Place(editor.Box, island.Scale);
            }
        };
        // Leaving the typing states ends typing at once, before the panel is released after its fade.
        machine.Changed += (_, to) =>
        {
            if (to is not (IslandState.Panels or IslandState.Palette))
            {
                Release();
            }
        };
    }

    /// <summary>Esc or Alt+F4 while a box has the keys: the island hides, which hands the foreground back.</summary>
    public Action? Escape { get; set; }

    /// <summary>A box has the keys, or no longer: the island must not retract under someone typing.</summary>
    public Action<bool>? Typing { get; set; }

    /// <summary>The user moved the foreground to another window while a box had the keys.</summary>
    public event Action? Lost;

    public EditorView? Focused => focused;

    /// <summary>A text box in <paramref name="host"/>, drawn on the text device. Dispose it with its view.</summary>
    public EditorView Create(ContainerVisual host, bool multiline, float fontDips)
    {
        var editor = new EditorView(island.Compositor, island.Queue, host, island.Dips, island.Hwnd, multiline, fontDips)
        {
            IslandToScreen = island.IslandToScreen,
            ScreenToIsland = p => island.ScreenToIsland(p.X, p.Y),
        };
        text.Hold();
        void Ready() => editor.Use(text.Device?.Graphics);
        text.DeviceReady += Ready;
        editor.Use(text.Device?.Graphics);
        editor.Failed += e => log?.Invoke($"typing: a box was not drawn: {e.Message}");
        editor.Services.Failed += e => log?.Invoke($"typing: a change was not taken: {e}");
        editor.Services.Log += line => log?.Invoke("typing: " + line);
        editor.Disposing += () =>
        {
            text.DeviceReady -= Ready;
            text.Let();
            editors.Remove(editor);
            if (dragging == editor)
            {
                dragging = null;
            }

            if (focused == editor)
            {
                Release();
            }
        };
        editors.Add(editor);
        return editor;
    }

    /// <summary>A typing hotkey or a click into the box: the island takes the foreground and the box the keys. False when Windows refused.</summary>
    public bool Take(EditorView editor)
    {
        handBackQueued = false;
        if (focused != editor)
        {
            focused?.Blur();
        }

        // Set first, so the input method messages that come with the activation reach the box.
        focused = editor;
        var front = PInvoke.GetForegroundWindow();
        if (front != island.Hwnd)
        {
            if (!PInvoke.SetForegroundWindow(island.Hwnd))
            {
                focused = null;
                log?.Invoke("typing: Windows did not give the island the foreground");
                return false;
            }

            previous = front;
        }

        PInvoke.SetFocus(island.Hwnd);
        editor.Focus();
        Typing?.Invoke(true);
        // Esc now reaches the window as a key, where an input method can still use it to cancel a composition.
        InputThread.SetEscape(false);
        log?.Invoke("typing: the island has the keys");
        return true;
    }

    /// <summary>No box has the keys any more; on the next turn the foreground goes back to the window that had it, unless another box took the keys meanwhile.</summary>
    public void Release()
    {
        dragging = null;
        if (focused is { } was)
        {
            focused = null;
            was.Blur();
            Typing?.Invoke(false);
        }

        if (!previous.IsNull && !handBackQueued)
        {
            handBackQueued = true;
            island.Queue.TryEnqueue(HandBack);
        }
    }

    private void HandBack()
    {
        if (!handBackQueued || focused is not null)
        {
            return;
        }

        handBackQueued = false;
        if (PInvoke.GetForegroundWindow() == island.Hwnd && PInvoke.IsWindow(previous))
        {
            PInvoke.SetForegroundWindow(previous);
            log?.Invoke("typing: foreground handed back");
        }

        previous = default;
    }

    // A press on a box takes the keys and places the caret; a second one soon after at the same place is a double
    // click, which the window class does not report on its own.
    private void OnPressed(Vector2 at)
    {
        var editor = editors.LastOrDefault(e => e.Contains(at));
        if (editor is null || (focused != editor && !Take(editor)))
        {
            return;
        }

        long now = Environment.TickCount64;
        float slop = PInvoke.GetSystemMetrics(SYSTEM_METRICS_INDEX.SM_CXDOUBLECLK) / (float)island.Scale;
        bool twice = now - lastPress <= PInvoke.GetDoubleClickTime() && Vector2.Distance(at, lastPressAt) <= slop;
        (lastPress, lastPressAt) = twice ? (0, default) : (now, at);
        editor.Mouse(twice ? PInvoke.WM_LBUTTONDBLCLK : PInvoke.WM_LBUTTONDOWN, at, MkLButton);
        dragging = editor;
    }

    private LRESULT? OnMessage(uint message, nuint wParam, nint lParam)
    {
        switch (message)
        {
            case PInvoke.WM_ACTIVATE:
                if ((wParam & 0xFFFF) == PInvoke.WA_INACTIVE && focused is not null)
                {
                    // The user went elsewhere: their choice of window stands, and Esc goes back to being a hotkey for
                    // as long as the island shows. Before Lost, which may hide the island and release Esc after this.
                    previous = default;
                    Release();
                    if (machine.State is IslandState.Panels or IslandState.Palette)
                    {
                        InputThread.SetEscape(true);
                    }

                    Lost?.Invoke();
                }

                return null;
            case PInvoke.WM_SETCURSOR:
                if ((lParam & 0xFFFF) == PInvoke.HTCLIENT && PInvoke.GetCursorPos(out var cursor)
                    && editors.Any(e => e.Contains(island.ScreenToIsland(cursor.X, cursor.Y))))
                {
                    PInvoke.SetCursor(PInvoke.LoadCursor(HINSTANCE.Null, PInvoke.IDC_IBEAM));
                    return new LRESULT(1);
                }

                return null;
            case PInvoke.WM_MOUSEMOVE or PInvoke.WM_LBUTTONUP:
                if (dragging is not { } drag)
                {
                    return null;
                }

                drag.Mouse(message, island.ToIsland((short)(lParam & 0xFFFF), (short)((lParam >> 16) & 0xFFFF)), wParam);
                if (message == PInvoke.WM_LBUTTONUP)
                {
                    dragging = null;
                }

                return new LRESULT(0);
            case PInvoke.WM_CAPTURECHANGED:
                dragging = null;
                return null;
            case PInvoke.WM_SETFOCUS or PInvoke.WM_KILLFOCUS:
                return null;
        }

        if (focused is not { } editor)
        {
            return null;
        }

        var key = (VIRTUAL_KEY)(wParam & 0xFFFF);
        if ((message == PInvoke.WM_KEYDOWN && key == VIRTUAL_KEY.VK_ESCAPE) || (message == PInvoke.WM_SYSKEYDOWN && key == VIRTUAL_KEY.VK_F4))
        {
            // Alt+F4 would otherwise close the island's window, and with it Tomte.
            Escape?.Invoke();
            return new LRESULT(0);
        }

        if (editor.BeforeKey?.Invoke(message, wParam, lParam) == true)
        {
            return new LRESULT(0);
        }

        return editor.Key(message, wParam, lParam, out var result) ? result : null;
    }
}
