using System.Diagnostics;
using System.Runtime.InteropServices;
using Tomte.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Com;
using Windows.Win32.System.Ole;
using Windows.Win32.System.SystemServices;
using Windows.Win32.UI.Shell;

namespace Tomte.Shell;

/// <summary>
/// The island's OLE drop target, registered once while hidden so DragEnter arrives the moment the surface is under a
/// real drag. It takes files (CF_HDROP), virtual files (FileGroupDescriptorW with FileContents, what browsers and mail
/// clients drag) and text, in that order of preference. Everything a drop carries is read inside Drop, because the
/// source may let go of it as soon as Drop returns; what to do with it happens in the Dropped handler and on the
/// shelf's worker, never here. The drag image is the system's, through IDropTargetHelper.
/// </summary>
internal sealed unsafe class DropTarget : IDropTarget.Interface, IDisposable
{
    private const ushort CfUnicodeText = 13, CfHDrop = 15;
    private static readonly Guid ClsidDragDropHelper = new("4657278A-411B-11D2-839A-00C04FD918D0");
    private readonly HWND hwnd;
    private readonly Action<string>? log;
    private readonly ushort cfDescriptor = (ushort)PInvoke.RegisterClipboardFormat("FileGroupDescriptorW");
    private readonly ushort cfContents = (ushort)PInvoke.RegisterClipboardFormat("FileContents");
    private IDropTargetHelper* helper;
    private DROPEFFECT allowed;
    private ushort offer;

    public DropTarget(HWND hwnd, Action<string>? log)
    {
        this.hwnd = hwnd;
        this.log = log;
        PInvoke.OleInitialize(null).ThrowOnFailure();
        var target = OleObjects.Wrap<IDropTarget>(this);
        // OLE keeps its own reference until RevokeDragDrop.
        PInvoke.RegisterDragDrop(hwnd, target).ThrowOnFailure();
        target->Release();
    }

    public event Action? Entered;

    public event Action? Left;

    /// <summary>The cursor's screen x on every DragOver, for the mascot's lean.</summary>
    public event Action<int>? Over;

    /// <summary>Raised inside Drop, on the island's thread, with everything already read.</summary>
    public event Action<DroppedItems>? Dropped;

    /// <summary>A drag out of the island is running: its items are already on the shelf.</summary>
    public bool Refusing { get; set; }

    public void Dispose()
    {
        PInvoke.RevokeDragDrop(hwnd);
        if (helper is not null)
        {
            helper->Release();
            helper = null;
        }
    }

    public HRESULT DragEnter(IDataObject* data, MODIFIERKEYS_FLAGS keys, POINTL pt, DROPEFFECT* effect)
    {
        long started = Stopwatch.GetTimestamp();
        allowed = *effect;
        offer = Refusing ? (ushort)0 : Has(data, CfHDrop) ? CfHDrop : Has(data, cfDescriptor) ? cfDescriptor : Has(data, CfUnicodeText) ? CfUnicodeText : (ushort)0;
        *effect = Effect(keys);
        long helped = Stopwatch.GetTimestamp();
        var h = Helper();
        if (h is not null)
        {
            h->DragEnter(hwnd, data, (System.Drawing.Point*)&pt, *effect);
        }

        log?.Invoke($"drop target: enter, offer {offer}, effect {*effect}, formats {Stopwatch.GetElapsedTime(started, helped).TotalMilliseconds:F2}ms, drag image helper {Stopwatch.GetElapsedTime(helped).TotalMilliseconds:F2}ms");
        Entered?.Invoke();
        return HRESULT.S_OK;
    }

    public HRESULT DragOver(MODIFIERKEYS_FLAGS keys, POINTL pt, DROPEFFECT* effect)
    {
        *effect = Effect(keys);
        if (helper is not null)
        {
            helper->DragOver((System.Drawing.Point*)&pt, *effect);
        }

        Over?.Invoke(pt.x);
        return HRESULT.S_OK;
    }

    public HRESULT DragLeave()
    {
        if (helper is not null)
        {
            helper->DragLeave();
        }

        Left?.Invoke();
        return HRESULT.S_OK;
    }

    public HRESULT Drop(IDataObject* data, MODIFIERKEYS_FLAGS keys, POINTL pt, DROPEFFECT* effect)
    {
        *effect = Effect(keys);
        if (helper is not null)
        {
            helper->Drop(data, (System.Drawing.Point*)&pt, *effect);
        }

        if (*effect != DROPEFFECT.DROPEFFECT_NONE)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                var drop = Read(data, (keys & MODIFIERKEYS_FLAGS.MK_CONTROL) != 0);
                log?.Invoke($"drop: {drop.Paths.Count} paths, {drop.Files.Count} virtual files ({drop.Files.Sum(f => (long)f.Bytes.Length)} bytes), text {drop.Text?.Length ?? 0} chars, read in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}ms");
                Dropped?.Invoke(drop);
            }
            catch (Exception e)
            {
                log?.Invoke($"drop: could not read it: {e}");
                *effect = DROPEFFECT.DROPEFFECT_NONE;
            }
        }

        Left?.Invoke();
        return HRESULT.S_OK;
    }

    /// <summary>
    /// A file stays where it is, which is a link to it; Ctrl keeps a copy. Everything else is copied in. Move is never
    /// returned: a source that sees it deletes its original.
    /// </summary>
    private DROPEFFECT Effect(MODIFIERKEYS_FLAGS keys)
    {
        if (offer == 0)
        {
            return DROPEFFECT.DROPEFFECT_NONE;
        }

        if (offer == CfHDrop && (keys & MODIFIERKEYS_FLAGS.MK_CONTROL) == 0 && allowed.HasFlag(DROPEFFECT.DROPEFFECT_LINK))
        {
            return DROPEFFECT.DROPEFFECT_LINK;
        }

        return allowed.HasFlag(DROPEFFECT.DROPEFFECT_COPY) ? DROPEFFECT.DROPEFFECT_COPY : DROPEFFECT.DROPEFFECT_NONE;
    }

    /// <summary>Made on the first drag, not at start: the shell's drag image code stays unloaded until then.</summary>
    private IDropTargetHelper* Helper()
    {
        if (helper is null)
        {
            void* created;
            fixed (Guid* clsid = &ClsidDragDropHelper)
            fixed (Guid* iid = &IDropTargetHelper.IID_Guid)
            {
                if (PInvoke.CoCreateInstance(clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, iid, &created).Succeeded)
                {
                    helper = (IDropTargetHelper*)created;
                }
            }
        }

        return helper;
    }

    private DroppedItems Read(IDataObject* data, bool keepCopy)
    {
        if (offer == CfHDrop)
        {
            return new DroppedItems(ShelfFormats.ParseDropFiles(Global(data, CfHDrop)), [], null, keepCopy);
        }

        if (offer == cfDescriptor)
        {
            var files = new List<(string, byte[])>();
            var list = ShelfFormats.ParseDescriptor(Global(data, cfDescriptor));
            for (int i = 0; i < list.Count; i++)
            {
                // a virtual folder's entries arrive flattened and the folder entry itself is skipped.
                if (list[i].IsFolder)
                {
                    continue;
                }

                try
                {
                    files.Add((list[i].Name, Contents(data, i, list[i].DeclaredSize)));
                }
                catch (Exception e) when (e is COMException or ArgumentException or InvalidOperationException)
                {
                    log?.Invoke($"drop: could not read {list[i].Name}: {e.Message}");
                }
            }

            return new DroppedItems([], files, null, keepCopy);
        }

        var text = MemoryMarshal.Cast<byte, char>(Global(data, CfUnicodeText));
        int end = text.IndexOf('\0');
        return new DroppedItems([], [], new string(end < 0 ? text : text[..end]), keepCopy);
    }

    private static bool Has(IDataObject* data, ushort format)
    {
        var f = Format(format, -1, TYMED.TYMED_HGLOBAL);
        return data->QueryGetData(&f) == HRESULT.S_OK;
    }

    private static FORMATETC Format(ushort format, int index, TYMED tymed) =>
        new() { cfFormat = format, dwAspect = (uint)DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = (uint)tymed };

    /// <summary>A format's whole HGLOBAL, copied out.</summary>
    private static byte[] Global(IDataObject* data, ushort format)
    {
        var f = Format(format, -1, TYMED.TYMED_HGLOBAL);
        STGMEDIUM medium;
        data->GetData(&f, &medium).ThrowOnFailure();
        try
        {
            return medium.tymed == TYMED.TYMED_HGLOBAL ? Copy(medium.u.hGlobal, null) : [];
        }
        finally
        {
            PInvoke.ReleaseStgMedium(&medium);
        }
    }

    /// <summary>One virtual file, read to the end of its stream whatever its descriptor declared.</summary>
    private byte[] Contents(IDataObject* data, int index, long? declared)
    {
        var f = Format(cfContents, index, TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL);
        STGMEDIUM medium;
        data->GetData(&f, &medium).ThrowOnFailure();
        try
        {
            if (medium.tymed == TYMED.TYMED_ISTREAM)
            {
                nint stream = (nint)medium.u.pstm;
                return ShelfFormats.ReadToEnd(buffer => Read((IStream*)stream, buffer));
            }

            return medium.tymed == TYMED.TYMED_HGLOBAL ? Copy(medium.u.hGlobal, declared) : [];
        }
        finally
        {
            PInvoke.ReleaseStgMedium(&medium);
        }
    }

    private static int Read(IStream* stream, Span<byte> buffer)
    {
        uint got = 0;
        fixed (byte* p = buffer)
        {
            // S_FALSE with fewer bytes is the end; a failure ends it too.
            return stream->Read(p, (uint)buffer.Length, &got).Failed ? 0 : (int)got;
        }
    }

    private static byte[] Copy(HGLOBAL global, long? declared)
    {
        int size = (int)PInvoke.GlobalSize(global);
        void* p = PInvoke.GlobalLock(global);
        if (p is null)
        {
            return [];
        }

        try
        {
            return new ReadOnlySpan<byte>(p, ShelfFormats.ContentLength(declared, size)).ToArray();
        }
        finally
        {
            PInvoke.GlobalUnlock(global);
        }
    }
}
