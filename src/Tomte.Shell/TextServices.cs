using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Tomte.Core;
using Tomte.Core.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Direct2D;
using Windows.Win32.Graphics.Direct2D.Common;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.Controls.RichEdit;
using Windows.Win32.UI.Input.Ime;
using Windows.Win32.UI.Input.KeyboardAndMouse;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Shell;

/// <summary>
/// One windowless editor, plain text unless made <see cref="Rich"/>: the system's text services (msftedit.dll)
/// behind a host of our own, drawn with TxDrawD2D. The services take keys, IME and TSF through the window that has
/// keyboard focus, which is the one the host names. The host never takes focus, the foreground or the cursor on its
/// own; it reports what the services ask for (what to repaint, where the caret is, what changed) and the caller
/// draws. They lay out in pixels at the process's system DPI, so the caller draws at that DPI and gives any other
/// monitor scale as <see cref="Zoom"/>.
/// The services are single-threaded: create, use and dispose on the island's thread.
/// </summary>
internal sealed unsafe class TextServices : ITextHost2.Interface, IDisposable
{
    private const uint EsAutoVScroll = 0x40, EsAutoHScroll = 0x80;
    private static readonly HRESULT SOk = (HRESULT)0, SFalse = (HRESULT)1, ENotImpl = (HRESULT)unchecked((int)0x80004001);

    private readonly CHARFORMAT2W* charFormat;
    private readonly PARAFORMAT2* paraFormat;
    private readonly HWND window;
    private uint bits;
    private readonly Dictionary<uint, Windows.System.DispatcherQueueTimer> timers = [];
    private ITextServices2* services;
    private nint hostUnknown;
    private HDC memoryDc;
    private RECT client;
    private float zoom = 1;
    private bool acquired, disposed;

    /// <param name="window">The window that gets keyboard focus while the user types here: the island, or a message-only window offscreen.</param>
    public TextServices(HWND window, bool multiline, float fontDips, uint textColor = 0xFFFFFF)
    {
        this.window = window;
        // No beep (Backspace at the start rings otherwise), no drag of the selection (the island has its own drag and
        // drop), and never TXTBIT_ADVANCEDINPUT: it faults inside msftedit on the first focus with a host of our own.
        bits = PInvoke.TXTBIT_D2DDWRITE | PInvoke.TXTBIT_WORDWRAP | PInvoke.TXTBIT_HIDESELECTION | PInvoke.TXTBIT_DISABLEDRAG
            | (multiline ? PInvoke.TXTBIT_MULTILINE : 0);

        charFormat = (CHARFORMAT2W*)NativeMemory.AllocZeroed((nuint)sizeof(CHARFORMAT2W));
        charFormat->Base.cbSize = (uint)sizeof(CHARFORMAT2W);
        charFormat->Base.dwMask = CFM_MASK.CFM_FACE | CFM_MASK.CFM_SIZE | CFM_MASK.CFM_COLOR | CFM_MASK.CFM_BOLD | CFM_MASK.CFM_ITALIC
            | CFM_MASK.CFM_UNDERLINE | CFM_MASK.CFM_STRIKEOUT | CFM_MASK.CFM_OFFSET | CFM_MASK.CFM_CHARSET | CFM_MASK.CFM_WEIGHT;
        charFormat->Base.yHeight = EditorScale.Twips(fontDips);
        // COLORREF is 0x00BBGGRR.
        charFormat->Base.crTextColor = (COLORREF)(((textColor & 0xFF) << 16) | (textColor & 0xFF00) | ((textColor >> 16) & 0xFF));
        charFormat->Base.bCharSet = FONT_CHARSET.DEFAULT_CHARSET;
        charFormat->wWeight = 400;
        TextStyle.Ui.AsSpan().CopyTo(charFormat->Base.szFaceName.AsSpan());

        paraFormat = (PARAFORMAT2*)NativeMemory.AllocZeroed((nuint)sizeof(PARAFORMAT2));
        paraFormat->Base.cbSize = (uint)sizeof(PARAFORMAT2);
        paraFormat->Base.dwMask = PARAFORMAT_MASK.PFM_ALIGNMENT | PARAFORMAT_MASK.PFM_TABSTOPS;
        paraFormat->Base.wAlignment = PARAFORMAT_ALIGNMENT.PFA_LEFT;
        paraFormat->Base.cTabCount = 1;
        paraFormat->Base.rgxTabs[0] = 720;
        memoryDc = PInvoke.CreateCompatibleDC(HDC.Null);

        try
        {
            Msftedit.Acquire();
            acquired = true;
            hostUnknown = Wrappers.GetOrCreateComInterfaceForObject(this, CreateComInterfaceFlags.None);
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(hostUnknown, in Msftedit.IidTextHost2, out nint host));
            void* unknown;
            try
            {
                Msftedit.Create(null, (void*)host, &unknown).ThrowOnFailure();
            }
            finally
            {
                Marshal.Release(host);
            }

            void* s;
            void** vtbl = *(void***)unknown;
            var iid = Msftedit.IidTextServices2;
            var hr = ((delegate* unmanaged[Stdcall]<void*, Guid*, void**, HRESULT>)vtbl[0])(unknown, &iid, &s);
            ((delegate* unmanaged[Stdcall]<void*, uint>)vtbl[2])(unknown);
            hr.ThrowOnFailure();
            services = (ITextServices2*)s;
        }
        catch
        {
            Dispose();
            throw;
        }

        // TSF (input methods, the emoji panel, dictation) through the services' own switch.
        Send(PInvoke.EM_SETEDITSTYLE, PInvoke.SES_USECTF, (nint)PInvoke.SES_USECTF);
        Send(PInvoke.EM_SETEVENTMASK, 0, (nint)(PInvoke.ENM_CHANGE | PInvoke.ENM_SELCHANGE | PInvoke.ENM_REQUESTRESIZE | PInvoke.ENM_STARTCOMPOSITION | PInvoke.ENM_ENDCOMPOSITION));
    }

    /// <summary>The services' own DPI: the process's system DPI. Their units are pixels at it.</summary>
    public static uint SystemDpi { get; } = PInvoke.GetDpiForSystem();

    /// <summary>The text's size in DIPs, as the box was made with.</summary>
    public float FontDips => charFormat->Base.yHeight / (float)EditorScale.TwipsPerDip;

    /// <summary>The window that has keyboard focus while the box is typed in.</summary>
    public HWND Window => window;

    /// <summary>
    /// Rich text: parts of the text can look different (the note's Markdown). Plain until set, and set only while the
    /// box is empty. Copy and paste then carry formatting, so a rich box's owner takes those keys.
    /// </summary>
    public bool Rich
    {
        get => (bits & PInvoke.TXTBIT_RICHTEXT) != 0;
        set
        {
            bits = value ? bits | PInvoke.TXTBIT_RICHTEXT : bits & ~PInvoke.TXTBIT_RICHTEXT;
            services->OnTxPropertyBitsChange(PInvoke.TXTBIT_RICHTEXT, bits & PInvoke.TXTBIT_RICHTEXT);
        }
    }

    /// <summary>An input method is composing in the box: the composition has a look of its own until it ends.</summary>
    public bool Composing { get; private set; }

    /// <summary>A composition was committed or cancelled.</summary>
    public event Action? Composed;

    /// <summary>The services' text object model, for styling parts of the text. The caller releases it before the box goes.</summary>
    public ITextDocument* Document()
    {
        ITextDocument* document;
        var iid = ITextDocument.IID_Guid;
        services->QueryInterface(&iid, (void**)&document).ThrowOnFailure();
        return document;
    }

    /// <summary>The services invalidated this rectangle of the client area, in pixels.</summary>
    public event Action<PixelRect>? Invalidated;

    /// <summary>The text changed (EN_CHANGE).</summary>
    public event Action? Changed;

    /// <summary>The selection or the caret moved (EN_SELCHANGE).</summary>
    public event Action? SelectionChanged;

    /// <summary>The text's own height changed (EN_REQUESTRESIZE): a box that grows with its text lays out again.</summary>
    public event Action? Resized;

    /// <summary>The caret was created, moved, shown or hidden.</summary>
    public event Action? CaretChanged;

    /// <summary>A notification handler threw; the services must never see the exception.</summary>
    public event Action<Exception>? Failed;

    /// <summary>Something for the log that is not a failure of ours.</summary>
    public event Action<string>? Log;

    /// <summary>The caret's rectangle in client pixels, as the services reported it, and whether they show it.</summary>
    public (int X, int Y, int Height, bool Shown) Caret { get; private set; }

    /// <summary>The height the content asks for (EN_REQUESTRESIZE), in pixels; 0 until the first.</summary>
    public int ContentHeight { get; private set; }

    /// <summary>Client-pixel points to screen pixels and back, for input method windows; identity when not set.</summary>
    public Func<System.Drawing.Point, bool, System.Drawing.Point>? MapToScreen { get; set; }

    public bool Active { get; private set; }

    public RECT Client => client;

    /// <summary>The monitor's scale over the system DPI's, as the services get it. They zoom the text by it, not a paragraph's indents.</summary>
    public float Zoom => zoom;

    /// <summary>The client size in pixels and the zoom for the monitor's scale; the services lay out again.</summary>
    public void Resize(int width, int height, float zoom)
    {
        bool changed = width != client.right || height != client.bottom || zoom != this.zoom;
        client = new RECT { right = width, bottom = height };
        this.zoom = zoom;
        if (changed && services is not null)
        {
            services->OnTxPropertyBitsChange(PInvoke.TXTBIT_EXTENTCHANGE | PInvoke.TXTBIT_CLIENTRECTCHANGE, PInvoke.TXTBIT_EXTENTCHANGE | PInvoke.TXTBIT_CLIENTRECTCHANGE);
            Send(PInvoke.EM_REQUESTRESIZE);
        }
    }

    public LRESULT Send(uint msg, nuint wParam = 0, nint lParam = 0)
    {
        TrySend(msg, wParam, lParam, out var result);
        return result;
    }

    /// <summary>A window message for the services; false when they did not handle it and the window's default should.</summary>
    public bool TrySend(uint msg, nuint wParam, nint lParam, out LRESULT result)
    {
        // While an input method composes it takes every key, which then arrives as VK_PROCESSKEY: any other key means
        // no composition is going on, even if the services never said it ended.
        if (Composing && msg == PInvoke.WM_KEYDOWN && wParam != (nuint)VIRTUAL_KEY.VK_PROCESSKEY)
        {
            Composing = false;
        }

        // Called through the vtable (slot 3) for the HRESULT: S_FALSE means the services left the message alone.
        LRESULT r = default;
        void** vtbl = *(void***)services;
        var hr = ((delegate* unmanaged[Stdcall]<void*, uint, WPARAM, LPARAM, LRESULT*, HRESULT>)vtbl[3])(services, msg, new WPARAM(wParam), new LPARAM(lParam), &r);
        result = r;
        return hr == SOk;
    }

    /// <summary>The text with every line break as '\n', one character for one, so indices match the services'.</summary>
    public string Text
    {
        get
        {
            BSTR text = default;
            services->TxGetText(&text);
            try
            {
                // Rich text always ends in a paragraph mark of the services' own, after the last line typed.
                string raw = text.ToString() ?? "";
                return EditorText.Lines(Rich && raw.EndsWith('\r') ? raw[..^1] : raw);
            }
            finally
            {
                PInvoke.SysFreeString(text);
            }
        }

        set
        {
            fixed (char* p = ToServices(value))
            {
                services->TxSetText(p);
            }
        }
    }

    /// <summary>The selection, start before end; the caret is at <see cref="SelectionEnd"/> unless the user selected backwards.</summary>
    public (int Start, int End) Selection
    {
        get
        {
            CHARRANGE range;
            Send(PInvoke.EM_EXGETSEL, 0, (nint)(&range));
            return (range.cpMin, range.cpMax);
        }

        set => Send(PInvoke.EM_SETSEL, (nuint)value.Start, value.End);
    }

    /// <summary>
    /// Replace part of the text without the user doing it (a rambler note, an AI write): no undo step, and the
    /// selection stays on the characters it was on.
    /// </summary>
    public void Replace(Splice change)
    {
        var (start, end) = Selection;
        Selection = (change.Start, change.End);
        fixed (char* p = ToServices(change.Text))
        {
            Send(PInvoke.EM_REPLACESEL, 0, (nint)p);
        }

        Selection = (change.Map(start), change.Map(end));
    }

    /// <summary>In-place activate for the client rectangle, once it has a size: the services draw and take messages.</summary>
    public void Activate()
    {
        if (!Active)
        {
            var rc = client;
            services->OnTxInPlaceActivate(&rc);
            Active = true;
        }
    }

    /// <summary>Give the services focus: the caret and the selection show, TSF attaches.</summary>
    public void Focus()
    {
        Activate();
        Send(PInvoke.WM_SETFOCUS);
    }

    /// <summary>Take focus away: the caret and the selection hide, and no timer of theirs is left.</summary>
    public void Blur()
    {
        if (Active)
        {
            Send(PInvoke.WM_KILLFOCUS);
        }

        // A composition never outlasts the focus.
        Composing = false;
        StopTimers();
    }

    /// <summary>
    /// Draw into a device context between BeginDraw and EndDraw, the client area's top-left at pixel
    /// <paramref name="origin"/>, only <paramref name="update"/> (client pixels) when given. The context is set to the
    /// services' DPI, so their units land on device pixels. The caret is not drawn: the caller shows it as a sprite.
    /// </summary>
    public HRESULT Draw(ID2D1DeviceContext* dc, System.Drawing.Point origin, PixelRect? update)
    {
        float dpi = SystemDpi;
        dc->SetDpi(dpi, dpi);
        var move = new D2D_MATRIX_3X2_F { m11 = 1, m22 = 1, dx = EditorScale.Translation(origin.X, SystemDpi), dy = EditorScale.Translation(origin.Y, SystemDpi) };
        dc->SetTransform(&move);
        dc->SetTextAntialiasMode(D2D1_TEXT_ANTIALIAS_MODE.D2D1_TEXT_ANTIALIAS_MODE_GRAYSCALE);
        var area = update ?? new PixelRect(0, 0, client.right, client.bottom);
        float k = 96f / dpi;
        var clip = new D2D_RECT_F { left = area.X * k, top = area.Y * k, right = (area.X + area.Width) * k, bottom = (area.Y + area.Height) * k };
        dc->PushAxisAlignedClip(&clip, D2D1_ANTIALIAS_MODE.D2D1_ANTIALIAS_MODE_ALIASED);
        var clear = default(D2D1_COLOR_F);
        dc->Clear(&clear);
        var bounds = new RECTL { right = client.right, bottom = client.bottom };
        var rect = new RECT { left = area.X, top = area.Y, right = area.X + area.Width, bottom = area.Y + area.Height };
        HRESULT hr = SOk;
        try
        {
            services->TxDrawD2D((ID2D1RenderTarget*)dc, &bounds, update is null ? null : &rect, (int)(Active ? TXTVIEW.TXTVIEW_ACTIVE : TXTVIEW.TXTVIEW_INACTIVE));
        }
        catch (COMException e)
        {
            hr = (HRESULT)e.HResult;
        }

        dc->PopAxisAlignedClip();
        return hr;
    }

    /// <summary>Deactivates and releases the services; msftedit.dll is freed when no editor is left.</summary>
    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        StopTimers();
        if (services is not null)
        {
            if (Active)
            {
                services->OnTxInPlaceDeactivate();
                Active = false;
            }

            // Deactivate and release, not ShutdownTextServices: with this host the release after a shutdown faults
            // inside msftedit, while the release alone frees the services and the host and lets the module go.

            uint left = services->Release();
            services = null;
            if (acquired)
            {
                acquired = false;
                // Something still holds the services (TSF, or work msftedit deferred): it would call into the module
                // after it was freed, so the module stays for the rest of the process.
                if (!Msftedit.Release(left))
                {
                    Log?.Invoke($"msftedit.dll kept loaded: {left} references to a text box's services remain after its release");
                }
            }
        }

        if (acquired)
        {
            acquired = false;
            Msftedit.Release(0);
        }

        if (hostUnknown != 0)
        {
            Marshal.Release(hostUnknown);
            hostUnknown = 0;
        }

        if (!memoryDc.IsNull)
        {
            PInvoke.DeleteDC(memoryDc);
            memoryDc = default;
        }

        NativeMemory.Free(charFormat);
        NativeMemory.Free(paraFormat);
    }

    /// <summary>Timers the services asked for and not yet killed; none while the editor is blurred or gone.</summary>
    public int Timers => timers.Count;

    // One line break, one character, as the note binding counts them; RichEdit's own break is CR.
    private static string ToServices(string text) => EditorText.Lines(text).Replace('\n', '\r');

    private void StopTimers()
    {
        foreach (var timer in timers.Values)
        {
            timer.Stop();
        }

        timers.Clear();
    }

    private void Notify(Action? handler)
    {
        try
        {
            handler?.Invoke();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            Failed?.Invoke(e);
        }
    }

    // ---- ITextHost: what the services ask of the window they live in.
    public HDC TxGetDC() => memoryDc;

    public int TxReleaseDC(HDC hdc) => 1;

    public BOOL TxShowScrollBar(int fnBar, BOOL fShow) => true;

    public BOOL TxEnableScrollBar(SCROLLBAR_CONSTANTS fuSBFlags, int fuArrowflags) => true;

    public BOOL TxSetScrollRange(int fnBar, int nMinPos, int nMaxPos, BOOL fRedraw) => true;

    public BOOL TxSetScrollPos(int fnBar, int nPos, BOOL fRedraw) => true;

    public void TxInvalidateRect(RECT* prc, BOOL fMode)
    {
        var r = prc is null ? client : *prc;
        Notify(() => Invalidated?.Invoke(new PixelRect(r.left, r.top, r.right - r.left, r.bottom - r.top)));
    }

    public void TxViewChange(BOOL fUpdate)
    {
    }

    public BOOL TxCreateCaret(HBITMAP hbmp, int xWidth, int yHeight)
    {
        Caret = Caret with { Height = yHeight };
        Notify(CaretChanged);
        return true;
    }

    public BOOL TxShowCaret(BOOL fShow)
    {
        Caret = Caret with { Shown = fShow };
        Notify(CaretChanged);
        return true;
    }

    public BOOL TxSetCaretPos(int x, int y)
    {
        Caret = Caret with { X = x, Y = y };
        Notify(CaretChanged);
        return true;
    }

    public BOOL TxSetTimer(uint idTimer, uint uTimeout)
    {
        if (!timers.TryGetValue(idTimer, out var timer))
        {
            if (Windows.System.DispatcherQueue.GetForCurrentThread() is not { } queue)
            {
                return false;
            }

            timer = queue.CreateTimer();
            timer.Tick += (_, _) =>
            {
                if (services is not null)
                {
                    Send(PInvoke.WM_TIMER, idTimer);
                }
            };
            timers[idTimer] = timer;
        }

        timer.Interval = TimeSpan.FromMilliseconds(uTimeout);
        timer.Start();
        return true;
    }

    public void TxKillTimer(uint idTimer)
    {
        if (timers.Remove(idTimer, out var timer))
        {
            timer.Stop();
        }
    }

    public void TxScrollWindowEx(int dx, int dy, RECT* lprcScroll, RECT* lprcClip, HRGN hrgnUpdate, RECT* lprcUpdate, SCROLL_WINDOW_FLAGS fuScroll) =>
        Notify(() => Invalidated?.Invoke(new PixelRect(0, 0, client.right, client.bottom)));

    // A drag to select keeps the mouse while the button is down.
    public void TxSetCapture(BOOL fCapture)
    {
        if (fCapture)
        {
            PInvoke.SetCapture(window);
        }
        else
        {
            PInvoke.ReleaseCapture();
        }
    }

    // Focus is the island's to give: it follows the hotkeys and clicks, not the services.
    public void TxSetFocus()
    {
    }

    public void TxSetCursor(HCURSOR hcur, BOOL fText)
    {
        if (!hcur.IsNull)
        {
            PInvoke.SetCursor(hcur);
        }
    }

    public BOOL TxScreenToClient(System.Drawing.Point* lppt)
    {
        if (MapToScreen is { } map)
        {
            *lppt = map(*lppt, false);
        }

        return true;
    }

    public BOOL TxClientToScreen(System.Drawing.Point* lppt)
    {
        if (MapToScreen is { } map)
        {
            *lppt = map(*lppt, true);
        }

        return true;
    }

    public HRESULT TxActivate(int* plOldState)
    {
        *plOldState = 0;
        return SOk;
    }

    public HRESULT TxDeactivate(int lNewState) => SOk;

    public HRESULT TxGetClientRect(RECT* prc)
    {
        *prc = client;
        return SOk;
    }

    public HRESULT TxGetViewInset(RECT* prc)
    {
        *prc = default;
        return SOk;
    }

    public HRESULT TxGetCharFormat(CHARFORMATW** ppCF)
    {
        *ppCF = (CHARFORMATW*)charFormat;
        return SOk;
    }

    public HRESULT TxGetParaFormat(PARAFORMAT** ppPF)
    {
        *ppPF = (PARAFORMAT*)paraFormat;
        return SOk;
    }

    public COLORREF TxGetSysColor(SYS_COLOR_INDEX nIndex) => nIndex switch
    {
        // A selection blue that reads on the island's near-black, with the text kept white.
        SYS_COLOR_INDEX.COLOR_HIGHLIGHT => (COLORREF)0x00D77800,
        SYS_COLOR_INDEX.COLOR_HIGHLIGHTTEXT or SYS_COLOR_INDEX.COLOR_WINDOWTEXT => (COLORREF)0x00FFFFFF,
        SYS_COLOR_INDEX.COLOR_WINDOW => (COLORREF)0x00121010,
        _ => (COLORREF)0x00808080,
    };

    public HRESULT TxGetBackStyle(TXTBACKSTYLE* pstyle)
    {
        *pstyle = TXTBACKSTYLE.TXTBACK_TRANSPARENT;
        return SOk;
    }

    public HRESULT TxGetMaxLength(uint* plength)
    {
        *plength = 1 << 20;
        return SOk;
    }

    public HRESULT TxGetScrollBars(uint* pdwScrollBar)
    {
        *pdwScrollBar = (bits & PInvoke.TXTBIT_MULTILINE) != 0 ? EsAutoVScroll : EsAutoHScroll;
        return SOk;
    }

    public HRESULT TxGetPasswordChar(sbyte* pch) => SFalse;

    public HRESULT TxGetAcceleratorPos(int* pcp)
    {
        *pcp = -1;
        return SOk;
    }

    // The services work the zoom out as the client size over this extent.
    public HRESULT TxGetExtent(Windows.Win32.Foundation.SIZE* lpExtent)
    {
        if (EditorScale.IsNone(zoom))
        {
            return ENotImpl;
        }

        lpExtent->cx = EditorScale.Extent(client.right, SystemDpi, zoom);
        lpExtent->cy = EditorScale.Extent(client.bottom, SystemDpi, zoom);
        return SOk;
    }

    public HRESULT OnTxCharFormatChange(CHARFORMATW* pCF) => SOk;

    public HRESULT OnTxParaFormatChange(PARAFORMAT* pPF) => SOk;

    public HRESULT TxGetPropertyBits(uint dwMask, uint* pdwBits)
    {
        *pdwBits = bits & dwMask;
        return SOk;
    }

    public HRESULT TxNotify(uint iNotify, void* pv)
    {
        switch (iNotify)
        {
            case PInvoke.EN_CHANGE:
                Notify(Changed);
                break;
            case PInvoke.EN_SELCHANGE:
                Notify(SelectionChanged);
                break;
            case PInvoke.EN_STARTCOMPOSITION:
                Composing = true;
                break;
            case PInvoke.EN_ENDCOMPOSITION:
                Composing = false;
                Notify(Composed);
                break;
            case PInvoke.EN_REQUESTRESIZE:
                var r = ((REQRESIZE*)pv)->rc;
                if (r.bottom - r.top != ContentHeight)
                {
                    ContentHeight = r.bottom - r.top;
                    Notify(Resized);
                }

                break;
        }

        return SOk;
    }

    public HIMC TxImmGetContext() => PInvoke.ImmGetContext(window);

    public void TxImmReleaseContext(HIMC himc) => PInvoke.ImmReleaseContext(window, himc);

    public HRESULT TxGetSelectionBarWidth(int* lSelBarWidth)
    {
        *lSelBarWidth = 0;
        return SOk;
    }

    // ---- ITextHost2
    public BOOL TxIsDoubleClickPending() => false;

    public HRESULT TxGetWindow(HWND* phwnd)
    {
        *phwnd = window;
        return SOk;
    }

    // The foreground follows the hotkeys and clicks the island handles, never a request from the services.
    public HRESULT TxSetForegroundWindow() => SOk;

    public HPALETTE TxGetPalette() => default;

    public HRESULT TxGetEastAsianFlags(int* pFlags)
    {
        *pFlags = 0;
        return SOk;
    }

    public HCURSOR TxSetCursor2(HCURSOR hcur, BOOL bText)
    {
        TxSetCursor(hcur, bText);
        return default;
    }

    public void TxFreeTextServicesNotification()
    {
    }

    public HRESULT TxGetEditStyle(uint dwItem, uint* pdwData)
    {
        *pdwData = 0;
        return SOk;
    }

    public HRESULT TxGetWindowStyles(uint* pdwStyle, uint* pdwExStyle)
    {
        *pdwStyle = 0;
        *pdwExStyle = 0;
        return SOk;
    }

    public HRESULT TxShowDropCaret(BOOL fShow, HDC hdc, RECT* prc) => SOk;

    public HRESULT TxDestroyCaret()
    {
        Caret = Caret with { Shown = false };
        Notify(CaretChanged);
        return SOk;
    }

    public HRESULT TxGetHorzExtent(int* plHorzExtent)
    {
        *plHorzExtent = 0;
        return SOk;
    }

    private static readonly HostWrappers Wrappers = new();

    /// <summary>Hands a host to the services as a COM object answering ITextHost and ITextHost2 with one vtable.</summary>
    private sealed class HostWrappers : ComWrappers
    {
        private static ComInterfaceEntry* entries;

        protected override ComInterfaceEntry* ComputeVtables(object obj, CreateComInterfaceFlags flags, out int count)
        {
            if (entries is null)
            {
                var vtbl = (ITextHost2.Vtbl*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(HostWrappers), sizeof(ITextHost2.Vtbl));
                GetIUnknownImpl(out nint qi, out nint addRef, out nint release);
                vtbl->QueryInterface_1 = (delegate* unmanaged[Stdcall]<ITextHost2*, Guid*, void**, HRESULT>)qi;
                vtbl->AddRef_2 = (delegate* unmanaged[Stdcall]<ITextHost2*, uint>)addRef;
                vtbl->Release_3 = (delegate* unmanaged[Stdcall]<ITextHost2*, uint>)release;
                ITextHost2.PopulateVTable(vtbl);
                var e = (ComInterfaceEntry*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(HostWrappers), 2 * sizeof(ComInterfaceEntry));
                e[0] = new ComInterfaceEntry { IID = Msftedit.IidTextHost2, Vtable = (nint)vtbl };
                e[1] = new ComInterfaceEntry { IID = Msftedit.IidTextHost, Vtable = (nint)vtbl };
                entries = e;
            }

            count = 2;
            return entries;
        }

        protected override object CreateObject(nint externalComObject, CreateObjectFlags flags) => throw new NotSupportedException();

        protected override void ReleaseObjects(IEnumerable objects) => throw new NotSupportedException();
    }
}

/// <summary>
/// msftedit.dll, loaded on the first editor and freed when the last one goes. The interface ids come from its own
/// exports: the SDK header only declares them.
/// </summary>
internal static unsafe class Msftedit
{
    private static HMODULE module;
    private static int users;
    private static delegate* unmanaged[Stdcall]<void*, void*, void**, HRESULT> create;

    public static Guid IidTextHost, IidTextHost2, IidTextServices2;

    /// <summary>Whether the module is in the process now.</summary>
    public static bool Loaded
    {
        get
        {
            fixed (char* name = "msftedit.dll")
            {
                return !PInvoke.GetModuleHandle(name).IsNull;
            }
        }
    }

    public static void Acquire()
    {
        if (users++ > 0)
        {
            return;
        }

        fixed (char* name = "msftedit.dll")
        {
            module = PInvoke.LoadLibrary(name);
        }

        if (module.IsNull)
        {
            users = 0;
            throw new InvalidOperationException("msftedit.dll did not load");
        }

        IidTextHost = *(Guid*)Proc("IID_ITextHost");
        IidTextHost2 = *(Guid*)Proc("IID_ITextHost2");
        IidTextServices2 = *(Guid*)Proc("IID_ITextServices2");
        create = (delegate* unmanaged[Stdcall]<void*, void*, void**, HRESULT>)Proc("CreateTextServices");
    }

    public static HRESULT Create(void* outer, void* host, void** unknown) => create(outer, host, unknown);

    /// <summary>
    /// One editor is gone. <paramref name="servicesLeft"/> is the count its services' last Release returned: above 0,
    /// something still holds them and will call into the module, so the module is never freed (false then).
    /// </summary>
    public static bool Release(uint servicesLeft)
    {
        if (servicesLeft > 0)
        {
            return false;
        }

        if (users == 0 || --users > 0)
        {
            return true;
        }

        PInvoke.FreeLibrary(module);
        module = default;
        create = null;
        return true;
    }

    private static void* Proc(string name)
    {
        fixed (byte* ascii = System.Text.Encoding.ASCII.GetBytes(name + "\0"))
        {
            var p = (void*)(nint)PInvoke.GetProcAddress(module, new PCSTR(ascii));
            return p is null ? throw new InvalidOperationException(name + " is not exported by msftedit.dll") : p;
        }
    }
}
