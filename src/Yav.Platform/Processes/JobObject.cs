using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Yav.Platform.Native;

namespace Yav.Platform.Processes;

/// <summary>
/// Groups a process and everything it starts so the whole group can be ended together.
/// A job object is process grouping, not a filesystem sandbox: it limits nothing the processes may read or write.
/// </summary>
internal sealed class JobObject : IDisposable
{
    private readonly SafeJobHandle _handle;

    public JobObject()
    {
        var raw = NativeMethods.CreateJobObject(0, null);
        if (raw == 0)
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not create a job object.");
        }

        _handle = new SafeJobHandle(raw);

        // When YAV exits or crashes the handle closes and Windows ends every process still in the job,
        // so no agent or test process outlives the shell that started it.
        var info = new NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            BasicLimitInformation = new NativeMethods.JOBOBJECT_BASIC_LIMIT_INFORMATION
            {
                LimitFlags = NativeMethods.JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
            },
        };

        var size = Marshal.SizeOf<NativeMethods.JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, buffer, fDeleteOld: false);
            if (!NativeMethods.SetInformationJobObject(raw, NativeMethods.JobObjectExtendedLimitInformation, buffer, (uint)size))
            {
                var error = Marshal.GetLastPInvokeError();
                _handle.Dispose();
                throw new Win32Exception(error, "Could not configure the job object.");
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public nint DangerousHandle => _handle.DangerousGetHandle();

    public bool IsClosed => _handle.IsClosed;

    /// <summary>Number of processes currently running in the job, or null when it cannot be read.</summary>
    public int? ActiveProcesses
    {
        get
        {
            if (_handle.IsClosed)
            {
                return 0;
            }

            var size = Marshal.SizeOf<NativeMethods.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                if (!NativeMethods.QueryInformationJobObject(_handle.DangerousGetHandle(), NativeMethods.JobObjectBasicAccountingInformation, buffer, (uint)size, 0))
                {
                    return null;
                }

                var info = Marshal.PtrToStructure<NativeMethods.JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(buffer);
                return (int)info.ActiveProcesses;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
    }

    /// <summary>Puts a process into the job. Everything it starts from then on belongs to the job as well.</summary>
    public void Assign(SafeProcessHandle process)
    {
        if (!NativeMethods.AssignProcessToJobObject(_handle.DangerousGetHandle(), process.DangerousGetHandle()))
        {
            throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not assign the process to its job object.");
        }
    }

    /// <summary>Ends every process in the job. Processes outside the job are not affected.</summary>
    public void Terminate(uint exitCode = 1)
    {
        if (!_handle.IsClosed)
        {
            NativeMethods.TerminateJobObject(_handle.DangerousGetHandle(), exitCode);
        }
    }

    public void Dispose() => _handle.Dispose();
}
