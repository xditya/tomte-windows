using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.System.Ole;

namespace Tomte.Shell;

/// <summary>
/// The island's OLE drop target: registered once while hidden, so DragEnter arrives the moment the surface is
/// under a real drag. It accepts nothing yet (effect none); the shelf's drop surface replaces the bodies.
/// The COM object is one static vtable of UnmanagedCallersOnly functions over a heap cell, built by hand:
/// the shell runs CsWin32 without marshaling, so the generated IDropTarget is only the vtable layout, and the
/// runtime's source-generated COM wrappers are not used on this hot, reentrant OLE path.
/// </summary>
internal sealed unsafe class DropTarget : IDisposable
{
    private static readonly Guid IidUnknown = new("00000000-0000-0000-C000-000000000046");
    private static readonly Guid IidDropTarget = new("00000122-0000-0000-C000-000000000046");
    private static DropTarget? instance;
    private readonly HWND hwnd;
    private readonly void** cell;
    private readonly void** vtable;

    public DropTarget(HWND hwnd)
    {
        this.hwnd = hwnd;
        instance = this;
        PInvoke.OleInitialize(null).ThrowOnFailure();
        vtable = (void**)NativeMemory.Alloc((nuint)(7 * sizeof(void*)));
        vtable[0] = (delegate* unmanaged[Stdcall]<void*, Guid*, void**, int>)&QueryInterface;
        vtable[1] = (delegate* unmanaged[Stdcall]<void*, uint>)&AddRef;
        vtable[2] = (delegate* unmanaged[Stdcall]<void*, uint>)&Release;
        vtable[3] = (delegate* unmanaged[Stdcall]<void*, void*, uint, long, uint*, int>)&DragEnter;
        vtable[4] = (delegate* unmanaged[Stdcall]<void*, uint, long, uint*, int>)&DragOver;
        vtable[5] = (delegate* unmanaged[Stdcall]<void*, int>)&DragLeave;
        vtable[6] = (delegate* unmanaged[Stdcall]<void*, void*, uint, long, uint*, int>)&Drop;
        cell = (void**)NativeMemory.Alloc((nuint)sizeof(void*));
        cell[0] = vtable;
        PInvoke.RegisterDragDrop(hwnd, (IDropTarget*)cell).ThrowOnFailure();
    }

    public event Action? Entered;

    public event Action? Left;

    public void Dispose()
    {
        PInvoke.RevokeDragDrop(hwnd);
        instance = null;
        // The cell stays allocated: OLE may still hold the pointer until the window goes.
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int QueryInterface(void* self, Guid* iid, void** result)
    {
        if (*iid == IidUnknown || *iid == IidDropTarget)
        {
            *result = self;
            return 0;
        }

        *result = null;
        return unchecked((int)0x80004002); // E_NOINTERFACE
    }

    // Lifetime is the process's; the object is never freed while OLE can call it.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint AddRef(void* self) => 1;

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static uint Release(void* self) => 1;

    // POINTL (two ints) arrives by value in one register on x64, hence the long.
    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DragEnter(void* self, void* dataObject, uint keyState, long point, uint* effect)
    {
        *effect = 0;
        Raise(instance?.Entered);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DragOver(void* self, uint keyState, long point, uint* effect)
    {
        *effect = 0;
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int DragLeave(void* self)
    {
        Raise(instance?.Left);
        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static int Drop(void* self, void* dataObject, uint keyState, long point, uint* effect)
    {
        *effect = 0;
        Raise(instance?.Left);
        return 0;
    }

    /// <summary>An exception must not unwind into OLE.</summary>
    private static void Raise(Action? handler)
    {
        try
        {
            handler?.Invoke();
        }
        catch (Exception)
        {
        }
    }
}
