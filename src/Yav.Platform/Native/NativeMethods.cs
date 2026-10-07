using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Yav.Platform.Native;

internal static partial class NativeMethods
{
    internal const uint CREATE_SUSPENDED = 0x00000004;
    internal const uint CREATE_NEW_PROCESS_GROUP = 0x00000200;
    internal const uint CREATE_UNICODE_ENVIRONMENT = 0x00000400;
    internal const uint EXTENDED_STARTUPINFO_PRESENT = 0x00080000;
    internal const uint CREATE_NO_WINDOW = 0x08000000;

    internal const int STARTF_USESTDHANDLES = 0x00000100;

    internal const int PROC_THREAD_ATTRIBUTE_HANDLE_LIST = 0x00020002;
    internal const int PROC_THREAD_ATTRIBUTE_JOB_LIST = 0x0002000D;
    internal const int PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY = 0x00020012;
    internal const uint PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_DISABLE_PROCESS_TREE = 0x00000002;

    internal const int ERROR_ACCESS_DENIED = 5;

    internal const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x00002000;
    internal const int JobObjectBasicAccountingInformation = 1;
    internal const int JobObjectExtendedLimitInformation = 9;

    internal const uint INFINITE = 0xFFFFFFFF;
    internal const uint WAIT_OBJECT_0 = 0;
    internal const uint WAIT_TIMEOUT = 0x102;

    internal const int STD_INPUT_HANDLE = -10;
    internal const int STD_OUTPUT_HANDLE = -11;
    internal const int STD_ERROR_HANDLE = -12;

    internal const uint ENABLE_PROCESSED_OUTPUT = 0x0001;
    internal const uint ENABLE_VIRTUAL_TERMINAL_PROCESSING = 0x0004;

    internal const uint CRED_TYPE_GENERIC = 1;
    internal const uint CRED_PERSIST_LOCAL_MACHINE = 2;
    internal const int ERROR_NOT_FOUND = 1168;
    internal const int ERROR_NO_SUCH_LOGON_SESSION = 1312;

    internal const uint SYNCHRONIZE = 0x00100000;
    internal const uint PROCESS_QUERY_LIMITED_INFORMATION = 0x1000;

    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFO
    {
        public int cb;
        public nint lpReserved;
        public nint lpDesktop;
        public nint lpTitle;
        public int dwX;
        public int dwY;
        public int dwXSize;
        public int dwYSize;
        public int dwXCountChars;
        public int dwYCountChars;
        public int dwFillAttribute;
        public int dwFlags;
        public short wShowWindow;
        public short cbReserved2;
        public nint lpReserved2;
        public nint hStdInput;
        public nint hStdOutput;
        public nint hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public nint lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct PROCESS_INFORMATION
    {
        public nint hProcess;
        public nint hThread;
        public int dwProcessId;
        public int dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct CREDENTIAL
    {
        public uint Flags;
        public uint Type;
        public nint TargetName;
        public nint Comment;
        public long LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public nint TargetAlias;
        public nint UserName;
    }

    [LibraryImport("kernel32.dll", EntryPoint = "CreateProcessW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool CreateProcess(
        string? lpApplicationName,
        char* lpCommandLine,
        nint lpProcessAttributes,
        nint lpThreadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool bInheritHandles,
        uint dwCreationFlags,
        char* lpEnvironment,
        string? lpCurrentDirectory,
        ref STARTUPINFOEX lpStartupInfo,
        out PROCESS_INFORMATION lpProcessInformation);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool InitializeProcThreadAttributeList(nint lpAttributeList, int dwAttributeCount, int dwFlags, ref nint lpSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool UpdateProcThreadAttribute(
        nint lpAttributeList,
        uint dwFlags,
        nint attribute,
        nint lpValue,
        nint cbSize,
        nint lpPreviousValue,
        nint lpReturnSize);

    [LibraryImport("kernel32.dll")]
    internal static partial void DeleteProcThreadAttributeList(nint lpAttributeList);

    [LibraryImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint CreateJobObject(nint lpJobAttributes, string? lpName);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetInformationJobObject(nint hJob, int jobObjectInformationClass, nint lpJobObjectInformation, uint cbJobObjectInformationLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool QueryInformationJobObject(nint hJob, int jobObjectInformationClass, nint lpJobObjectInformation, uint cbJobObjectInformationLength, nint lpReturnLength);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateJobObject(nint hJob, uint uExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool AssignProcessToJobObject(nint hJob, nint hProcess);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool TerminateProcess(nint hProcess, uint uExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CloseHandle(nint hObject);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint WaitForSingleObject(nint hHandle, uint dwMilliseconds);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetExitCodeProcess(nint hProcess, out uint lpExitCode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial uint ResumeThread(nint hThread);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint GetStdHandle(int nStdHandle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GetConsoleMode(nint hConsoleHandle, out uint lpMode);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetConsoleMode(nint hConsoleHandle, uint dwMode);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetConsoleOutputCP();

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetConsoleCP();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetConsoleOutputCP(uint wCodePageID);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool SetConsoleCP(uint wCodePageID);

    [LibraryImport("kernel32.dll")]
    internal static partial uint GetOEMCP();

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static partial nint OpenProcess(uint dwDesiredAccess, [MarshalAs(UnmanagedType.Bool)] bool bInheritHandle, int dwProcessId);

    [LibraryImport("kernel32.dll", EntryPoint = "QueryFullProcessImageNameW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static unsafe partial bool QueryFullProcessImageName(nint hProcess, uint dwFlags, char* lpExeName, ref uint lpdwSize);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    internal static unsafe partial uint GetConsoleProcessList(uint* lpdwProcessList, uint dwProcessCount);

    internal const byte VER_NT_WORKSTATION = 1;

    [StructLayout(LayoutKind.Sequential)]
    internal unsafe struct OSVERSIONINFOEXW
    {
        public uint dwOSVersionInfoSize;
        public uint dwMajorVersion;
        public uint dwMinorVersion;
        public uint dwBuildNumber;
        public uint dwPlatformId;
        public fixed ushort szCSDVersion[128];
        public ushort wServicePackMajor;
        public ushort wServicePackMinor;
        public ushort wSuiteMask;
        public byte wProductType;
        public byte wReserved;
    }

    /// <summary>The version of Windows as it is, not as a manifest of the program lets it be seen. 0 is success.</summary>
    [LibraryImport("ntdll.dll")]
    internal static unsafe partial int RtlGetVersion(OSVERSIONINFOEXW* lpVersionInformation);

    internal const uint WM_SETTINGCHANGE = 0x001A;
    internal const uint SMTO_ABORTIFHUNG = 0x0002;
    internal static readonly nint HWND_BROADCAST = 0xFFFF;

    [LibraryImport("user32.dll", EntryPoint = "SendMessageTimeoutW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    internal static partial nint SendMessageTimeout(nint hWnd, uint msg, nuint wParam, string lParam, uint fuFlags, uint uTimeout, out nuint lpdwResult);

    [LibraryImport("advapi32.dll", EntryPoint = "CredReadW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CredRead(string target, uint type, uint reservedFlag, out nint credentialPtr);

    [LibraryImport("advapi32.dll", EntryPoint = "CredWriteW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CredWrite(ref CREDENTIAL userCredential, uint flags);

    [LibraryImport("advapi32.dll", EntryPoint = "CredDeleteW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool CredDelete(string target, uint type, uint flags);

    [LibraryImport("advapi32.dll")]
    internal static partial void CredFree(nint buffer);
}

/// <summary>Owns a job object handle. Closing it terminates every process still in the job.</summary>
internal sealed class SafeJobHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    public SafeJobHandle()
        : base(ownsHandle: true)
    {
    }

    public SafeJobHandle(nint handle)
        : base(ownsHandle: true)
    {
        SetHandle(handle);
    }

    protected override bool ReleaseHandle() => NativeMethods.CloseHandle(handle);
}
