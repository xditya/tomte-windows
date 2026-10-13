using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Tomte.Shell;

internal static unsafe class Program
{
    private static int Main()
    {
        var instance = new HINSTANCE(Marshal.GetHINSTANCE(typeof(Program).Module));

        fixed (char* className = "TomteIsland")
        {
            var windowClass = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = &HandleMessage,
                hInstance = instance,
                lpszClassName = className,
            };
            if (PInvoke.RegisterClassEx(in windowClass) == 0)
            {
                throw new Win32Exception();
            }

            // Created hidden and never shown here: idle means nothing drawn, no space reserved, no focus taken.
            var window = PInvoke.CreateWindowEx(
                WINDOW_EX_STYLE.WS_EX_TOPMOST
                | WINDOW_EX_STYLE.WS_EX_TOOLWINDOW
                | WINDOW_EX_STYLE.WS_EX_NOACTIVATE
                | WINDOW_EX_STYLE.WS_EX_NOREDIRECTIONBITMAP,
                className,
                null,
                WINDOW_STYLE.WS_POPUP,
                0,
                0,
                0,
                0,
                HWND.Null,
                HMENU.Null,
                instance,
                null);
            if (window == HWND.Null)
            {
                throw new Win32Exception();
            }
        }

        MSG message;
        while (PInvoke.GetMessage(&message, HWND.Null, 0, 0).Value > 0)
        {
            PInvoke.TranslateMessage(&message);
            PInvoke.DispatchMessage(&message);
        }

        return 0;
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvStdcall)])]
    private static LRESULT HandleMessage(HWND window, uint message, WPARAM wParam, LPARAM lParam)
    {
        switch (message)
        {
            case PInvoke.WM_CLOSE:
                PInvoke.DestroyWindow(window);
                return new LRESULT(0);
            case PInvoke.WM_DESTROY:
                PInvoke.PostQuitMessage(0);
                return new LRESULT(0);
            default:
                return PInvoke.DefWindowProc(window, message, wParam, lParam);
        }
    }
}
