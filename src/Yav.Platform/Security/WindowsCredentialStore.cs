using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Yav.Core.Ports;
using Yav.Platform.Native;

namespace Yav.Platform.Security;

/// <summary>
/// Stores secrets in the Windows Credential Manager for the current user. The secret is protected by
/// Windows with the user's logon credentials and never appears in YAV settings, logs or transcripts.
/// Any program running as the same user can read it; that is a boundary of the operating system.
/// </summary>
public sealed class WindowsCredentialStore : ICredentialStore
{
    private const string Prefix = "YavShell/";
    private const int MaxSecretBytes = 2560;

    private readonly string _prefix;

    /// <param name="scope">Keeps the secrets of one data directory apart from those of another. Null for the default one.</param>
    public WindowsCredentialStore(string? scope = null)
    {
        _prefix = scope is null ? Prefix : Prefix + Checked(scope, "scope") + "/";
    }

    public bool IsAvailable => OperatingSystem.IsWindows();

    public bool Exists(string name)
    {
        if (!NativeMethods.CredRead(Target(name), NativeMethods.CRED_TYPE_GENERIC, 0, out var pointer))
        {
            return false;
        }

        NativeMethods.CredFree(pointer);
        return true;
    }

    public string? Read(string name)
    {
        if (!NativeMethods.CredRead(Target(name), NativeMethods.CRED_TYPE_GENERIC, 0, out var pointer))
        {
            var error = Marshal.GetLastPInvokeError();
            if (error is NativeMethods.ERROR_NOT_FOUND or NativeMethods.ERROR_NO_SUCH_LOGON_SESSION)
            {
                return null;
            }

            throw new Win32Exception(error, "Could not read from the Windows Credential Manager.");
        }

        try
        {
            var credential = Marshal.PtrToStructure<NativeMethods.CREDENTIAL>(pointer);
            if (credential.CredentialBlob == 0 || credential.CredentialBlobSize == 0)
            {
                return string.Empty;
            }

            var bytes = new byte[credential.CredentialBlobSize];
            Marshal.Copy(credential.CredentialBlob, bytes, 0, bytes.Length);
            try
            {
                return Encoding.UTF8.GetString(bytes);
            }
            finally
            {
                Array.Clear(bytes);
            }
        }
        finally
        {
            NativeMethods.CredFree(pointer);
        }
    }

    public void Write(string name, string secret, string comment)
    {
        ArgumentException.ThrowIfNullOrEmpty(secret);
        var bytes = Encoding.UTF8.GetBytes(secret);
        if (bytes.Length > MaxSecretBytes)
        {
            Array.Clear(bytes);
            throw new ArgumentException($"The secret is longer than the {MaxSecretBytes} bytes the Credential Manager accepts.");
        }

        var target = Marshal.StringToCoTaskMemUni(Target(name));
        var commentPointer = Marshal.StringToCoTaskMemUni(comment);
        var user = Marshal.StringToCoTaskMemUni(Environment.UserName);
        var blob = Marshal.AllocCoTaskMem(bytes.Length);
        try
        {
            Marshal.Copy(bytes, 0, blob, bytes.Length);
            var credential = new NativeMethods.CREDENTIAL
            {
                Type = NativeMethods.CRED_TYPE_GENERIC,
                TargetName = target,
                Comment = commentPointer,
                CredentialBlobSize = (uint)bytes.Length,
                CredentialBlob = blob,
                Persist = NativeMethods.CRED_PERSIST_LOCAL_MACHINE,
                UserName = user,
            };

            if (!NativeMethods.CredWrite(ref credential, 0))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not write to the Windows Credential Manager.");
            }
        }
        finally
        {
            // Overwrite the unmanaged copy before releasing it.
            for (var i = 0; i < bytes.Length; i++)
            {
                Marshal.WriteByte(blob, i, 0);
            }

            Array.Clear(bytes);
            Marshal.FreeCoTaskMem(blob);
            Marshal.FreeCoTaskMem(target);
            Marshal.FreeCoTaskMem(commentPointer);
            Marshal.FreeCoTaskMem(user);
        }
    }

    public bool Delete(string name)
    {
        if (NativeMethods.CredDelete(Target(name), NativeMethods.CRED_TYPE_GENERIC, 0))
        {
            return true;
        }

        var error = Marshal.GetLastPInvokeError();
        if (error == NativeMethods.ERROR_NOT_FOUND)
        {
            return false;
        }

        throw new Win32Exception(error, "Could not delete from the Windows Credential Manager.");
    }

    private string Target(string name) => _prefix + Checked(name, "name");

    private static string Checked(string value, string what)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, what);
        if (!char.IsAsciiLetterOrDigit(value[0]) || value.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '-' or '_' or '.')))
        {
            throw new ArgumentException($"'{value}' is not a valid credential {what}.", what);
        }

        return value;
    }
}
