using System.Collections;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.System.Com;
using Windows.Win32.System.Ole;

namespace Tomte.Shell;

/// <summary>
/// The COM objects the shell hands to OLE: the drop target, and a drag's data object and drop source. CsWin32
/// generates each interface's vtable from its Interface, with entry points that turn an exception into a failed
/// HRESULT, so nothing unwinds into OLE. ComWrappers adds IUnknown and keeps the object alive while OLE holds it.
/// </summary>
internal sealed unsafe class OleObjects : ComWrappers
{
    private static readonly OleObjects Instance = new();
    private static readonly ComInterfaceEntry* DropTargetEntry = Entry<IDropTarget>();
    private static readonly ComInterfaceEntry* DataObjectEntry = Entry<IDataObject>();
    private static readonly ComInterfaceEntry* DropSourceEntry = Entry<IDropSource>();

    /// <summary>A new reference to the object as T; the caller releases it.</summary>
    public static T* Wrap<T>(object target)
        where T : unmanaged, IComIID
    {
        nint unknown = Instance.GetOrCreateComInterfaceForObject(target, CreateComInterfaceFlags.None);
        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, in T.Guid, out nint result));
            return (T*)result;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    protected override ComInterfaceEntry* ComputeVtables(object obj, CreateComInterfaceFlags flags, out int count)
    {
        var entry = obj switch
        {
            IDropTarget.Interface => DropTargetEntry,
            IDataObject.Interface => DataObjectEntry,
            IDropSource.Interface => DropSourceEntry,
            _ => null,
        };
        count = entry is null ? 0 : 1;
        return entry;
    }

    protected override object CreateObject(nint externalComObject, CreateObjectFlags flags) => throw new NotSupportedException();

    protected override void ReleaseObjects(IEnumerable objects) => throw new NotSupportedException();

    private static ComInterfaceEntry* Entry<T>()
        where T : unmanaged, IVTable, IComIID
    {
        var entry = (ComInterfaceEntry*)RuntimeHelpers.AllocateTypeAssociatedMemory(typeof(OleObjects), sizeof(ComInterfaceEntry));
        entry->IID = T.Guid;
        entry->Vtable = (nint)T.VTable;
        return entry;
    }
}
