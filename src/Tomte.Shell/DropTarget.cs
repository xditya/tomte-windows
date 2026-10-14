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
/// source may let go of it as soon as Drop returns: paths and text into memory, virtual files straight into the shelf
/// folder through <see cref="Receive"/>. What to do with it happens in the Dropped handler and on the shelf's worker.
/// The drag image is the system's, through IDropTargetHelper, let in by a <see cref="DragImageGate"/>.
/// </summary>
internal sealed unsafe class DropTarget : IDropTarget.Interface, IDisposable
{
    private const ushort CfUnicodeText = 13, CfHDrop = 15;
    private static readonly Guid ClsidDragDropHelper = new("4657278A-411B-11D2-839A-00C04FD918D0");
    private readonly HWND hwnd;
    private readonly Func<bool> shapeMoving;
    private readonly Action<string>? log;
    private readonly ushort cfDescriptor = (ushort)PInvoke.RegisterClipboardFormat("FileGroupDescriptorW");
    private readonly ushort cfContents = (ushort)PInvoke.RegisterClipboardFormat("FileContents");
    private readonly DragImageGate gate = new();
    private IDropTargetHelper* helper;

    // The drag's data object, held from DragEnter to the end of the drag so the helper can be let in late.
    private IDataObject* data;
    private DROPEFFECT allowed;
    private ushort offer;

    public DropTarget(HWND hwnd, Func<bool> shapeMoving, Action<string>? log)
    {
        this.hwnd = hwnd;
        this.shapeMoving = shapeMoving;
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

    /// <summary>The drag over the island now carries something the island takes.</summary>
    public bool Accepting => offer != 0;

    /// <summary>Writes one virtual file into the shelf as it is read and returns its path, or null when it is refused.</summary>
    public Func<string, ReadChunk, string?>? Receive { get; set; }

    public void Dispose()
    {
        PInvoke.RevokeDragDrop(hwnd);
        EndDrag();
        if (helper is not null)
        {
            helper->Release();
            helper = null;
        }
    }

    public HRESULT DragEnter(IDataObject* dataObject, MODIFIERKEYS_FLAGS keys, POINTL pt, DROPEFFECT* effect)
    {
        long started = Stopwatch.GetTimestamp();
        EndDrag();
        allowed = *effect;
        offer = Refusing ? (ushort)0 : Has(dataObject, CfHDrop) ? CfHDrop : Has(dataObject, cfDescriptor) ? cfDescriptor : Has(dataObject, CfUnicodeText) ? CfUnicodeText : (ushort)0;
        *effect = Effect(keys);
        dataObject->AddRef();
        data = dataObject;
        bool now = gate.Begin(shapeMoving());
        if (now)
        {
            EnterHelper(pt, *effect);
        }

        log?.Invoke($"drop target: enter, offer {offer}, effect {*effect}, drag image helper {(now ? "now" : "after the shape stops")}, in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F2}ms");
        Entered?.Invoke();
        return HRESULT.S_OK;
    }

    public HRESULT DragOver(MODIFIERKEYS_FLAGS keys, POINTL pt, DROPEFFECT* effect)
    {
        *effect = Effect(keys);
        if (gate.Over(shapeMoving()))
        {
            EnterHelper(pt, *effect);
        }
        else if (gate.Entered && helper is not null)
        {
            helper->DragOver((System.Drawing.Point*)&pt, *effect);
        }

        Over?.Invoke(pt.x);
        return HRESULT.S_OK;
    }

    public HRESULT DragLeave()
    {
        if (gate.End() && helper is not null)
        {
            helper->DragLeave();
        }

        EndDrag();
        Left?.Invoke();
        return HRESULT.S_OK;
    }

    public HRESULT Drop(IDataObject* dataObject, MODIFIERKEYS_FLAGS keys, POINTL pt, DROPEFFECT* effect)
    {
        // The effect the source hears is decided after the read: a source told "copy" for a drop that kept nothing
        // would be wrong.
        var final = Effect(keys);
        if (final != DROPEFFECT.DROPEFFECT_NONE)
        {
            long started = Stopwatch.GetTimestamp();
            try
            {
                var drop = Read(dataObject, (keys & MODIFIERKEYS_FLAGS.MK_CONTROL) != 0);
                log?.Invoke($"drop: {drop.Paths.Count} paths, {drop.Files.Count} virtual files, text {drop.Text?.Length ?? 0} chars, read in {Stopwatch.GetElapsedTime(started).TotalMilliseconds:F1}ms");
                if (drop.Paths.Count + drop.Files.Count == 0 && string.IsNullOrEmpty(drop.Text))
                {
                    final = DROPEFFECT.DROPEFFECT_NONE;
                }
                else
                {
                    Dropped?.Invoke(drop);
                }
            }
            catch (Exception e)
            {
                log?.Invoke($"drop: could not read it: {e}");
                final = DROPEFFECT.DROPEFFECT_NONE;
            }
        }

        *effect = final;
        if (gate.End() && helper is not null)
        {
            helper->Drop(dataObject, (System.Drawing.Point*)&pt, final);
        }

        EndDrag();
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

    /// <summary>The helper's DragEnter, with the data object held since the drag entered. Its result does not matter.</summary>
    private void EnterHelper(POINTL pt, DROPEFFECT effect)
    {
        if (data is null)
        {
            return;
        }

        var h = Helper();
        if (h is not null)
        {
            h->DragEnter(hwnd, data, (System.Drawing.Point*)&pt, effect);
        }
    }

    private void EndDrag()
    {
        gate.End();
        if (data is not null)
        {
            data->Release();
            data = null;
        }
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

    private DroppedItems Read(IDataObject* dataObject, bool keepCopy)
    {
        if (offer == CfHDrop)
        {
            return new DroppedItems(ShelfFormats.ParseDropFiles(Global(dataObject, CfHDrop)), [], null, keepCopy);
        }

        if (offer == cfDescriptor)
        {
            var files = new List<(string, string)>();
            var list = ShelfFormats.ParseDescriptor(Global(dataObject, cfDescriptor));
            for (int i = 0; i < list.Count; i++)
            {
                // a virtual folder's entries arrive flattened and the folder entry itself is skipped.
                if (list[i].IsFolder)
                {
                    continue;
                }

                try
                {
                    if (Contents(dataObject, i, list[i]) is { } path)
                    {
                        files.Add((list[i].Name, path));
                    }
                }
                catch (Exception e) when (e is COMException or ArgumentException or InvalidOperationException)
                {
                    log?.Invoke($"drop: could not read {list[i].Name}: {e.Message}");
                }
            }

            return new DroppedItems([], files, null, keepCopy);
        }

        var text = MemoryMarshal.Cast<byte, char>(Global(dataObject, CfUnicodeText));
        int end = text.IndexOf('\0');
        return new DroppedItems([], [], new string(end < 0 ? text : text[..end]), keepCopy);
    }

    private static bool Has(IDataObject* dataObject, ushort format)
    {
        var f = Format(format, -1, TYMED.TYMED_HGLOBAL);
        return dataObject->QueryGetData(&f) == HRESULT.S_OK;
    }

    private static FORMATETC Format(ushort format, int index, TYMED tymed) =>
        new() { cfFormat = format, dwAspect = (uint)DVASPECT.DVASPECT_CONTENT, lindex = index, tymed = (uint)tymed };

    /// <summary>A format's whole HGLOBAL, copied out. Used for the descriptor, the path list and text, which are small.</summary>
    private static byte[] Global(IDataObject* dataObject, ushort format)
    {
        var f = Format(format, -1, TYMED.TYMED_HGLOBAL);
        STGMEDIUM medium;
        dataObject->GetData(&f, &medium).ThrowOnFailure();
        try
        {
            if (medium.tymed != TYMED.TYMED_HGLOBAL)
            {
                return [];
            }

            int size = (int)PInvoke.GlobalSize(medium.u.hGlobal);
            void* p = PInvoke.GlobalLock(medium.u.hGlobal);
            if (p is null)
            {
                return [];
            }

            try
            {
                return new ReadOnlySpan<byte>(p, size).ToArray();
            }
            finally
            {
                PInvoke.GlobalUnlock(medium.u.hGlobal);
            }
        }
        finally
        {
            PInvoke.ReleaseStgMedium(&medium);
        }
    }

    /// <summary>
    /// One virtual file into the shelf: a stream is read to its end whatever the descriptor declared, an HGLOBAL as far
    /// as its declared size when that fits. Null when refused or when no receiver is set.
    /// </summary>
    private string? Contents(IDataObject* dataObject, int index, VirtualFile file)
    {
        if (Receive is not { } receive)
        {
            return null;
        }

        var f = Format(cfContents, index, TYMED.TYMED_ISTREAM | TYMED.TYMED_HGLOBAL);
        STGMEDIUM medium;
        dataObject->GetData(&f, &medium).ThrowOnFailure();
        try
        {
            if (medium.tymed == TYMED.TYMED_ISTREAM)
            {
                nint stream = (nint)medium.u.pstm;
                return receive(file.Name, buffer => Read((IStream*)stream, buffer));
            }

            if (medium.tymed != TYMED.TYMED_HGLOBAL)
            {
                return null;
            }

            int size = (int)PInvoke.GlobalSize(medium.u.hGlobal);
            nint p = (nint)PInvoke.GlobalLock(medium.u.hGlobal);
            if (p == 0)
            {
                return null;
            }

            try
            {
                int length = ShelfFormats.ContentLength(file.DeclaredSize, size), offset = 0;
                return receive(file.Name, buffer =>
                {
                    int n = Math.Min(buffer.Length, length - offset);
                    new ReadOnlySpan<byte>((byte*)p + offset, n).CopyTo(buffer);
                    offset += n;
                    return n;
                });
            }
            finally
            {
                PInvoke.GlobalUnlock(medium.u.hGlobal);
            }
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
}
