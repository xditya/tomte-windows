using System.Runtime.InteropServices;
using System.Text;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Security.Credentials;

namespace Tomte.AI;

/// <summary>
/// API keys as generic credentials in Windows Credential Manager, scoped to the current user on
/// this PC. Keys live only here: never in settings, logs or exception messages.
/// </summary>
public static class CredentialStore
{
    private const string Prefix = "Tomte/ai/";

    public static unsafe void Save(string providerId, string secret)
    {
        var blob = Encoding.UTF8.GetBytes(secret);
        fixed (char* target = Prefix + providerId)
        fixed (char* user = "Tomte")
        fixed (byte* data = blob)
        {
            var credential = new CREDENTIALW
            {
                Type = CRED_TYPE.CRED_TYPE_GENERIC,
                TargetName = target,
                UserName = user,
                CredentialBlob = data,
                CredentialBlobSize = (uint)blob.Length,
                Persist = CRED_PERSIST.CRED_PERSIST_LOCAL_MACHINE,
            };
            try
            {
                if (!PInvoke.CredWrite(&credential, 0))
                    throw new InvalidOperationException($"CredWrite failed: {Marshal.GetLastPInvokeErrorMessage()}");
            }
            finally
            {
                Array.Clear(blob);
            }
        }
    }

    /// <summary>The stored key, or null when there is none.</summary>
    public static unsafe string? Read(string providerId)
    {
        CREDENTIALW* credential;
        fixed (char* target = Prefix + providerId)
        {
            if (!PInvoke.CredRead(target, CRED_TYPE.CRED_TYPE_GENERIC, 0, &credential))
                return Marshal.GetLastPInvokeError() == (int)WIN32_ERROR.ERROR_NOT_FOUND
                    ? null
                    : throw new InvalidOperationException($"CredRead failed: {Marshal.GetLastPInvokeErrorMessage()}");
        }
        try
        {
            return Encoding.UTF8.GetString(credential->CredentialBlob, (int)credential->CredentialBlobSize);
        }
        finally
        {
            PInvoke.CredFree(credential);
        }
    }

    public static unsafe void Delete(string providerId)
    {
        fixed (char* target = Prefix + providerId)
        {
            if (!PInvoke.CredDelete(target, CRED_TYPE.CRED_TYPE_GENERIC, 0)
                && Marshal.GetLastPInvokeError() != (int)WIN32_ERROR.ERROR_NOT_FOUND)
                throw new InvalidOperationException($"CredDelete failed: {Marshal.GetLastPInvokeErrorMessage()}");
        }
    }
}
