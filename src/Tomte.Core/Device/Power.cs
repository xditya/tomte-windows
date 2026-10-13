using System.ComponentModel;
using Microsoft.Win32.SafeHandles;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security;
using Windows.Win32.System.Shutdown;

namespace Tomte.Core.Device;

// Destructive tier: always confirmed, never run from a scheduled job. No undo.
// Every function checks dryRun before anything else.
public static class Power
{
    public static ActionResult Lock(bool dryRun = false)
    {
        if (dryRun) return ActionResult.DryRun("lock the PC.");
        return PInvoke.LockWorkStation() ? ActionResult.Done("Locked the PC.") : LastError("Lock");
    }

    public static ActionResult Sleep(bool dryRun = false)
    {
        if (dryRun) return ActionResult.DryRun("put the PC to sleep.");
        return PInvoke.SetSuspendState(false, false, false) ? ActionResult.Done("Put the PC to sleep.") : LastError("Sleep");
    }

    public static ActionResult Restart(bool dryRun = false)
    {
        if (dryRun) return ActionResult.DryRun("restart the PC.");
        return Exit(EXIT_WINDOWS_FLAGS.EWX_REBOOT, "Restart");
    }

    public static ActionResult ShutDown(bool dryRun = false)
    {
        if (dryRun) return ActionResult.DryRun("shut down the PC.");
        return Exit(EXIT_WINDOWS_FLAGS.EWX_SHUTDOWN | EXIT_WINDOWS_FLAGS.EWX_POWEROFF, "Shut down");
    }

    // ExitWindowsEx needs SeShutdownPrivilege enabled on the process token first.
    private static unsafe ActionResult Exit(EXIT_WINDOWS_FLAGS flags, string what)
    {
        if (!PInvoke.OpenProcessToken(new SafeFileHandle(PInvoke.GetCurrentProcess(), false), TOKEN_ACCESS_MASK.TOKEN_ADJUST_PRIVILEGES | TOKEN_ACCESS_MASK.TOKEN_QUERY, out var token))
            return LastError(what);
        using (token)
        {
            var privileges = new TOKEN_PRIVILEGES { PrivilegeCount = 1 };
            if (!PInvoke.LookupPrivilegeValue(null, PInvoke.SE_SHUTDOWN_NAME, out privileges.Privileges[0].Luid))
                return LastError(what);
            privileges.Privileges[0].Attributes = TOKEN_PRIVILEGES_ATTRIBUTES.SE_PRIVILEGE_ENABLED;
            if (!PInvoke.AdjustTokenPrivileges(new HANDLE(token.DangerousGetHandle()), false, &privileges, 0, null, null))
                return LastError(what);
        }
        return PInvoke.ExitWindowsEx(flags, SHUTDOWN_REASON.SHTDN_REASON_FLAG_PLANNED)
            ? ActionResult.Done($"{what} started.")
            : LastError(what);
    }

    private static ActionResult LastError(string what) =>
        ActionResult.Fail($"{what} failed: {new Win32Exception().Message}");
}
