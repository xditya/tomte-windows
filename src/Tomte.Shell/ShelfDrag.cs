using System.Diagnostics;
using Tomte.Core;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Com;
using Windows.Win32.System.Memory;
using Windows.Win32.System.Ole;
using Windows.Win32.System.SystemServices;
using Windows.Win32.UI.Shell;

namespace Tomte.Shell;

/// <summary>
/// Drags shelf items out of the island. Files and folders go as CF_HDROP; a drag of nothing but content (text, links,
/// images that came without a file) goes as FileGroupDescriptorW with FileContents, which every target tested copies
/// during the drop. The island never activates: the press that starts the drag went to a WS_EX_NOACTIVATE window.
/// DoDragDrop runs on the island's thread and pumps its messages while the drag lasts.
/// </summary>
internal static unsafe class ShelfDrag
{
    private const int DragImageDips = 64;
    private static readonly Guid ClsidDragDropHelper = new("4657278A-411B-11D2-839A-00C04FD918D0");

    // The previous drag's shell data object, released when the next drag starts: a target may still read it after
    // DoDragDrop returns.
    private static ShelfDataObject? previous;

    /// <summary>Runs the drag and returns when it is over. Missing items stay behind; what the target took may leave the shelf.</summary>
    public static void Run(IslandWindow island, TriggerController triggers, ShelfStore shelf, IReadOnlyList<ShelfItem> items, Action<string>? log)
    {
        var present = items.Where(i => !i.Missing).ToList();
        if (present.Count == 0)
        {
            return;
        }

        var effects = ShelfFormats.Effects(present);
        long started = Stopwatch.GetTimestamp();
        previous?.Dispose();
        var data = previous = new ShelfDataObject(present);
        bool image = SetImage(data.Inner, present[0].Path, (int)Math.Round(DragImageDips * island.Scale));
        var dataObject = OleObjects.Wrap<IDataObject>(data);
        var source = OleObjects.Wrap<IDropSource>(new DropSource());
        log?.Invoke($"drag out: {present.Count} items as {(data.VirtualFiles ? "virtual files" : "CF_HDROP")}, effects {effects}, image {image}, ready in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}ms");

        if (log is not null)
        {
            // Says whether the island's queue, which carries the compositor tick, runs while DoDragDrop holds the thread.
            island.Queue.TryEnqueue(() => log($"drag out: the island's queue ran {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}ms after the drag was ready"));
        }

        triggers.DraggingOut(true);
        triggers.Drops.Refusing = true;
        DROPEFFECT effect = default;
        HRESULT result;
        try
        {
            result = PInvoke.DoDragDrop(dataObject, source, (DROPEFFECT)effects, &effect);
        }
        finally
        {
            triggers.Drops.Refusing = false;
            triggers.DraggingOut(false);
            source->Release();
            dataObject->Release();
        }

        var performed = data.Performed();
        log?.Invoke($"drag out: DoDragDrop 0x{result.Value:X8} effect {effect} performed {performed} after {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F0}ms");
        // Whether the files are still there is asked on the shelf's worker, not here: a network path can take seconds.
        _ = shelf.AfterDragOut(present, (ShelfEffect)effect, performed);
    }

    /// <summary>The item's thumbnail, or its icon when no thumbnail is cached, as a 32-bit bitmap; default when the shell has neither.</summary>
    public static HBITMAP Bitmap(string path, int px)
    {
        IShellItemImageFactory* factory;
        fixed (char* p = path)
        fixed (Guid* iid = &IShellItemImageFactory.IID_Guid)
        {
            if (PInvoke.SHCreateItemFromParsingName(p, null, iid, (void**)&factory).Failed)
            {
                return default;
            }
        }

        try
        {
            HBITMAP bitmap;
            var size = new SIZE(px, px);
            // A thumbnail the shell has to make can take a second (a video); the icon then stands in.
            return factory->GetImage(size, SIIGBF.SIIGBF_RESIZETOFIT | SIIGBF.SIIGBF_INCACHEONLY, &bitmap).Succeeded
                || factory->GetImage(size, SIIGBF.SIIGBF_ICONONLY, &bitmap).Succeeded ? bitmap : default;
        }
        finally
        {
            factory->Release();
        }
    }

    /// <summary>The system drag image, from the first item's picture. The helper owns the bitmap once it took it.</summary>
    private static bool SetImage(IDataObject* inner, string path, int px)
    {
        var bitmap = Bitmap(path, px);
        if (bitmap.IsNull)
        {
            return false;
        }

        BITMAP info;
        PInvoke.GetObject(bitmap, sizeof(BITMAP), &info);
        var image = new SHDRAGIMAGE
        {
            sizeDragImage = new SIZE(info.bmWidth, info.bmHeight),
            ptOffset = new System.Drawing.Point(info.bmWidth / 2, info.bmHeight / 2),
            hbmpDragImage = bitmap,
            crColorKey = new COLORREF(0xFFFFFFFF),
        };
        IDragSourceHelper* helper;
        bool taken = false;
        fixed (Guid* clsid = &ClsidDragDropHelper)
        fixed (Guid* iid = &IDragSourceHelper.IID_Guid)
        {
            if (PInvoke.CoCreateInstance(clsid, null, CLSCTX.CLSCTX_INPROC_SERVER, iid, (void**)&helper).Succeeded)
            {
                taken = helper->InitializeFromBitmap(&image, inner).Succeeded;
                helper->Release();
            }
        }

        if (!taken)
        {
            PInvoke.DeleteObject(bitmap);
        }

        return taken;
    }

    private sealed class DropSource : IDropSource.Interface
    {
        public HRESULT QueryContinueDrag(BOOL escapePressed, MODIFIERKEYS_FLAGS keys) =>
            escapePressed ? HRESULT.DRAGDROP_S_CANCEL : (keys & MODIFIERKEYS_FLAGS.MK_LBUTTON) == 0 ? HRESULT.DRAGDROP_S_DROP : HRESULT.S_OK;

        public HRESULT GiveFeedback(DROPEFFECT effect) => HRESULT.DRAGDROP_S_USEDEFAULTCURSORS;
    }
}

/// <summary>
/// The data object of one drag. It renders its own formats when a target asks and hands everything else, the drag
/// image and the effects targets write back included, to the shell's own data object inside it. Targets probe it on
/// every DragOver, so a probe for a format it does not offer costs a comparison and a call, no allocation.
/// </summary>
internal sealed unsafe class ShelfDataObject : IDataObject.Interface, IDisposable
{
    private const ushort CfHDrop = 15;
    private static readonly ushort CfDescriptor = (ushort)PInvoke.RegisterClipboardFormat("FileGroupDescriptorW");
    private static readonly ushort CfContents = (ushort)PInvoke.RegisterClipboardFormat("FileContents");
    private static readonly ushort CfPerformed = (ushort)PInvoke.RegisterClipboardFormat("Performed DropEffect");
    private readonly IReadOnlyList<ShelfItem> items;
    private readonly FORMATETC[] offered;

    public ShelfDataObject(IReadOnlyList<ShelfItem> items)
    {
        this.items = items;
        VirtualFiles = ShelfFormats.AsVirtualFiles(items);
        IDataObject* shell;
        fixed (Guid* iid = &IDataObject.IID_Guid)
        {
            PInvoke.SHCreateDataObject(null, 0, null, null, iid, (void**)&shell).ThrowOnFailure();
        }

        Inner = shell;
        offered = VirtualFiles
            ? [Format(CfDescriptor, -1, TYMED.TYMED_HGLOBAL), Format(CfContents, -1, TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL)]
            : [Format(CfHDrop, -1, TYMED.TYMED_HGLOBAL)];
    }

    public IDataObject* Inner { get; private set; }

    public bool VirtualFiles { get; }

    public void Dispose()
    {
        if (Inner is not null)
        {
            Inner->Release();
            Inner = null;
        }
    }

    /// <summary>What the target wrote back as "Performed DropEffect"; Explorer's same-volume move says so only here.</summary>
    public ShelfEffect Performed()
    {
        var f = Format(CfPerformed, -1, TYMED.TYMED_HGLOBAL);
        STGMEDIUM medium;
        if (Inner->GetData(&f, &medium).Failed)
        {
            return ShelfEffect.None;
        }

        try
        {
            var p = (uint*)PInvoke.GlobalLock(medium.u.hGlobal);
            var value = p is null ? ShelfEffect.None : (ShelfEffect)(*p);
            PInvoke.GlobalUnlock(medium.u.hGlobal);
            return value;
        }
        finally
        {
            PInvoke.ReleaseStgMedium(&medium);
        }
    }

    public HRESULT GetData(FORMATETC* format, STGMEDIUM* medium)
    {
        if (!Offers(format))
        {
            return Inner->GetData(format, medium);
        }

        *medium = default;
        if (format->cfFormat == CfContents)
        {
            int index = Math.Max(0, format->lindex);
            if (index >= items.Count)
            {
                return HRESULT.DV_E_LINDEX;
            }

            if ((format->tymed & (uint)TYMED.TYMED_ISTREAM) != 0)
            {
                IStream* stream;
                fixed (char* path = items[index].Path)
                {
                    var hr = PInvoke.SHCreateStreamOnFileEx(path, (uint)(STGM.STGM_READ | STGM.STGM_SHARE_DENY_NONE), 0, false, null, &stream);
                    if (hr.Failed)
                    {
                        return hr;
                    }
                }

                medium->tymed = TYMED.TYMED_ISTREAM;
                medium->u.pstm = stream;
                return HRESULT.S_OK;
            }

            return Global(File.ReadAllBytes(items[index].Path), medium);
        }

        return Global(format->cfFormat == CfHDrop
            ? ShelfFormats.DropFiles([.. items.Select(i => i.Path)])
            : ShelfFormats.Descriptor([.. items.Select(i => (i.Name, i.Size ?? 0))]), medium);
    }

    public HRESULT GetDataHere(FORMATETC* format, STGMEDIUM* medium) =>
        Offers(format) ? HRESULT.E_NOTIMPL : Inner->GetDataHere(format, medium);

    public HRESULT QueryGetData(FORMATETC* format) => Offers(format) ? HRESULT.S_OK : Inner->QueryGetData(format);

    public HRESULT GetCanonicalFormatEtc(FORMATETC* formatIn, FORMATETC* formatOut)
    {
        *formatOut = *formatIn;
        formatOut->ptd = null;
        return HRESULT.DATA_S_SAMEFORMATETC;
    }

    public HRESULT SetData(FORMATETC* format, STGMEDIUM* medium, BOOL release) => Inner->SetData(format, medium, release);

    public HRESULT EnumFormatEtc(uint direction, IEnumFORMATETC** result)
    {
        if (direction != (uint)DATADIR.DATADIR_GET)
        {
            return Inner->EnumFormatEtc(direction, result);
        }

        // Ours first, then whatever the drag image helper put into the shell's object.
        var all = new List<FORMATETC>(offered);
        IEnumFORMATETC* inner;
        if (Inner->EnumFormatEtc(direction, &inner).Succeeded)
        {
            FORMATETC f;
            uint fetched;
            while (inner->Next(1, &f, &fetched) == HRESULT.S_OK && fetched == 1)
            {
                all.Add(f);
            }

            inner->Release();
        }

        fixed (FORMATETC* formats = all.ToArray())
        {
            return PInvoke.SHCreateStdEnumFmtEtc((uint)all.Count, formats, result);
        }
    }

    public HRESULT DAdvise(FORMATETC* format, uint advf, IAdviseSink* sink, uint* connection) => HRESULT.OLE_E_ADVISENOTSUPPORTED;

    public HRESULT DUnadvise(uint connection) => HRESULT.OLE_E_ADVISENOTSUPPORTED;

    public HRESULT EnumDAdvise(IEnumSTATDATA** enumAdvise) => HRESULT.OLE_E_ADVISENOTSUPPORTED;

    private static FORMATETC Format(ushort format, int index, TYMED tymed) =>
        new() { cfFormat = format, dwAspect = (uint)DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = (uint)tymed };

    private static HRESULT Global(byte[] bytes, STGMEDIUM* medium)
    {
        var global = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)bytes.Length);
        void* p = PInvoke.GlobalLock(global);
        if (p is null)
        {
            PInvoke.GlobalFree(global);
            return HRESULT.E_OUTOFMEMORY;
        }

        bytes.CopyTo(new Span<byte>(p, bytes.Length));
        PInvoke.GlobalUnlock(global);
        medium->tymed = TYMED.TYMED_HGLOBAL;
        medium->u.hGlobal = global;
        return HRESULT.S_OK;
    }

    private bool Offers(FORMATETC* format)
    {
        foreach (var f in offered)
        {
            if (f.cfFormat == format->cfFormat && (f.tymed & format->tymed) != 0 && format->dwAspect == (uint)DVASPECT.DVASPECT_CONTENT
                && (format->lindex == -1 || (format->cfFormat == CfContents && format->lindex < items.Count)))
            {
                return true;
            }
        }

        return false;
    }
}
