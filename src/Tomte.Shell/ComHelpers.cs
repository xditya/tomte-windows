using System.Runtime.InteropServices;

namespace Windows.Win32;

/// <summary>CsWin32 builds a COM interface's vtable but leaves IUnknown to whoever owns the objects: here, ComWrappers.</summary>
internal static unsafe partial class ComHelpers
{
    static partial void PopulateIUnknownImpl<TComInterface>(System.Com.IUnknown.Vtbl* vtable)
        where TComInterface : unmanaged
    {
        ComWrappers.GetIUnknownImpl(out nint queryInterface, out nint addRef, out nint release);
        vtable->QueryInterface_1 = (delegate* unmanaged[Stdcall]<System.Com.IUnknown*, Guid*, void**, Foundation.HRESULT>)queryInterface;
        vtable->AddRef_2 = (delegate* unmanaged[Stdcall]<System.Com.IUnknown*, uint>)addRef;
        vtable->Release_3 = (delegate* unmanaged[Stdcall]<System.Com.IUnknown*, uint>)release;
    }
}
