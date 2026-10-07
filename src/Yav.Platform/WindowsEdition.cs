using Yav.Platform.Native;

namespace Yav.Platform;

/// <summary>What kind of Windows this is, beyond its version.</summary>
public static class WindowsEdition
{
    /// <summary>
    /// True on Windows Server (a server or a domain controller), false on an edition for a workstation, such as
    /// Windows 11, and null when Windows does not say. Windows Server has builds of the same numbers as Windows 11.
    /// </summary>
    public static unsafe bool? IsServer()
    {
        var info = new NativeMethods.OSVERSIONINFOEXW { dwOSVersionInfoSize = (uint)sizeof(NativeMethods.OSVERSIONINFOEXW) };
        return NativeMethods.RtlGetVersion(&info) == 0 ? info.wProductType != NativeMethods.VER_NT_WORKSTATION : null;
    }
}
