using System.ComponentModel;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using Yav.Core.Ports;
using Yav.Platform.Native;

namespace Yav.Platform.Processes;

/// <summary>How a new process gets into its job object. Either way it is in the job before it runs.</summary>
internal enum JobPlacement
{
    /// <summary>Windows creates the process inside the job.</summary>
    AtCreation,

    /// <summary>
    /// The process is created suspended, put into the job, and only then allowed to run. This is the way for a
    /// packaged program, which Windows refuses to create inside a job it is given.
    /// </summary>
    BeforeItRuns,
}

/// <summary>
/// A child process that is inside its own job object before it runs, with only its three pipe handles inherited.
/// </summary>
internal sealed class NativeProcess : IRunningProcess
{
    private const int PipeBufferSize = 64 * 1024;

    private readonly JobObject _job;
    private readonly SafeProcessHandle _process;
    private readonly AnonymousPipeServerStream _stdin;
    private readonly AnonymousPipeServerStream _stdout;
    private readonly AnonymousPipeServerStream _stderr;
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly HandleWaiter _waiter;
    private readonly RegisteredWaitHandle _registration;
    private readonly Lock _gate = new();
    private bool _inputClosed;
    private bool _disposed;

    private NativeProcess(
        JobObject job,
        SafeProcessHandle process,
        int processId,
        AnonymousPipeServerStream stdin,
        AnonymousPipeServerStream stdout,
        AnonymousPipeServerStream stderr)
    {
        _job = job;
        _process = process;
        ProcessId = processId;
        _stdin = stdin;
        _stdout = stdout;
        _stderr = stderr;
        _waiter = new HandleWaiter(process.DangerousGetHandle());
        _registration = ThreadPool.RegisterWaitForSingleObject(_waiter, OnExited, null, Timeout.Infinite, executeOnlyOnce: true);
    }

    public int ProcessId { get; }

    public Stream StandardInput => _stdin;

    public Stream StandardOutput => _stdout;

    public Stream StandardError => _stderr;

    public bool HasExited => _exited.Task.IsCompleted;

    public int? ExitCode => _exited.Task.IsCompletedSuccessfully ? _exited.Task.Result : null;

    public static NativeProcess Start(ProcessSpec spec, string resolvedExecutable) =>
        Start(spec, resolvedExecutable, JobPlacement.AtCreation);

    /// <summary>
    /// Starts the process in the given placement, or before it runs when Windows refuses to create it inside its job.
    /// Tests name <see cref="JobPlacement.BeforeItRuns"/> to exercise that way with any program.
    /// </summary>
    internal static NativeProcess Start(ProcessSpec spec, string resolvedExecutable, JobPlacement placement)
    {
        if (!Directory.Exists(spec.WorkingDirectory))
        {
            throw new DirectoryNotFoundException($"The working directory does not exist: {spec.WorkingDirectory}");
        }

        string application;
        string commandLine;
        if (ExecutableResolver.IsBatchFile(resolvedExecutable))
        {
            application = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            commandLine = "\"" + application + "\" " + CommandLine.BuildForBatch(resolvedExecutable, spec.Arguments);
        }
        else
        {
            application = resolvedExecutable;
            commandLine = CommandLine.Build(resolvedExecutable, spec.Arguments);
        }

        if (commandLine.Length > 32_000)
        {
            throw new ArgumentException("The command line is longer than Windows allows. Pass large input through standard input or a file.");
        }

        var stdin = new AnonymousPipeServerStream(PipeDirection.Out, HandleInheritability.Inheritable, PipeBufferSize);
        var stdout = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable, PipeBufferSize);
        var stderr = new AnonymousPipeServerStream(PipeDirection.In, HandleInheritability.Inheritable, PipeBufferSize);
        JobObject? job = null;

        try
        {
            job = new JobObject();
            var launch = new Launch(
                application,
                commandLine,
                CommandLine.BuildEnvironmentBlock(spec.Environment),
                spec.WorkingDirectory,
                [stdin.ClientSafePipeHandle.DangerousGetHandle(), stdout.ClientSafePipeHandle.DangerousGetHandle(), stderr.ClientSafePipeHandle.DangerousGetHandle()],
                job);

            var created = TryCreate(launch, placement, out var information, out var error);
            if (!created && placement == JobPlacement.AtCreation && IsJobListRefused(error))
            {
                // A packaged program (Microsoft Store, MSIX), named by its path or by its app execution alias, is put
                // into a job of its package while Windows creates it, and Windows then refuses the job it was given.
                // That job would refuse the process later as well, so the process goes into a new one.
                job.Dispose();
                job = null;
                job = new JobObject();
                launch = launch with { Job = job };
                placement = JobPlacement.BeforeItRuns;
                created = TryCreate(launch, placement, out information, out error);
            }

            if (!created)
            {
                throw new Win32Exception(error, $"Could not start '{Path.GetFileName(resolvedExecutable)}': {new Win32Exception(error).Message}");
            }

            var processHandle = new SafeProcessHandle(information.hProcess, ownsHandle: true);
            try
            {
                if (placement == JobPlacement.BeforeItRuns)
                {
                    LetRunInsideJob(job, processHandle, information.hThread, resolvedExecutable);
                }
            }
            catch
            {
                processHandle.Dispose();
                throw;
            }
            finally
            {
                NativeMethods.CloseHandle(information.hThread);
            }

            // The child owns its ends now. Closing ours lets a read end when the child exits.
            stdin.DisposeLocalCopyOfClientHandle();
            stdout.DisposeLocalCopyOfClientHandle();
            stderr.DisposeLocalCopyOfClientHandle();

            var result = new NativeProcess(job, processHandle, information.dwProcessId, stdin, stdout, stderr);
            job = null;
            return result;
        }
        catch
        {
            stdin.Dispose();
            stdout.Dispose();
            stderr.Dispose();
            throw;
        }
        finally
        {
            // Closing a job that still holds the process ends it, because the job ends its processes when it closes.
            job?.Dispose();
        }
    }

    /// <summary>One call to CreateProcess. Returns false with the Windows error code when Windows refuses it.</summary>
    private static unsafe bool TryCreate(Launch launch, JobPlacement placement, out NativeMethods.PROCESS_INFORMATION information, out int error)
    {
        nint size = 0;
        NativeMethods.InitializeProcThreadAttributeList(0, 2, 0, ref size);
        var attributeList = Marshal.AllocHGlobal(size);
        var attributeListInitialized = false;

        // The attribute list points at these values, so they stay in place until the list is deleted.
        var jobHandle = launch.Job.DangerousHandle;
        var desktopAppPolicy = NativeMethods.PROCESS_CREATION_DESKTOP_APP_BREAKAWAY_DISABLE_PROCESS_TREE;
        try
        {
            if (!NativeMethods.InitializeProcThreadAttributeList(attributeList, 2, 0, ref size))
            {
                throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not prepare the process attributes.");
            }

            attributeListInitialized = true;
            fixed (nint* pipes = launch.Pipes)
            {
                // Only these three handles are inherited, even though other inheritable handles may exist in this process.
                if (!NativeMethods.UpdateProcThreadAttribute(attributeList, 0, NativeMethods.PROC_THREAD_ATTRIBUTE_HANDLE_LIST, (nint)pipes, nint.Size * launch.Pipes.Length, 0, 0))
                {
                    throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not restrict the inherited handles.");
                }

                if (placement == JobPlacement.AtCreation)
                {
                    // The process is created inside the job, so nothing it starts can escape the job.
                    if (!NativeMethods.UpdateProcThreadAttribute(attributeList, 0, NativeMethods.PROC_THREAD_ATTRIBUTE_JOB_LIST, (nint)(&jobHandle), nint.Size, 0, 0))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not assign the process to its job object.");
                    }
                }
                else
                {
                    // By default Windows starts the programs a packaged program runs outside its package, and outside
                    // every job with it, YAV's included. Kept inside the package they stay in the job, and run with the
                    // package's identity like the packaged program itself.
                    if (!NativeMethods.UpdateProcThreadAttribute(attributeList, 0, NativeMethods.PROC_THREAD_ATTRIBUTE_DESKTOP_APP_POLICY, (nint)(&desktopAppPolicy), sizeof(uint), 0, 0))
                    {
                        throw new Win32Exception(Marshal.GetLastPInvokeError(), "Could not keep the programs it starts in its job object.");
                    }
                }

                var startup = new NativeMethods.STARTUPINFOEX
                {
                    StartupInfo = new NativeMethods.STARTUPINFO
                    {
                        cb = Marshal.SizeOf<NativeMethods.STARTUPINFOEX>(),
                        dwFlags = NativeMethods.STARTF_USESTDHANDLES,
                        hStdInput = launch.Pipes[0],
                        hStdOutput = launch.Pipes[1],
                        hStdError = launch.Pipes[2],
                    },
                    lpAttributeList = attributeList,
                };

                // CreateProcess may modify the command line buffer, so each call gets its own writable copy.
                var commandBuffer = new char[launch.Command.Length + 1];
                launch.Command.CopyTo(0, commandBuffer, 0, launch.Command.Length);

                var flags = NativeMethods.EXTENDED_STARTUPINFO_PRESENT
                    | NativeMethods.CREATE_UNICODE_ENVIRONMENT
                    | NativeMethods.CREATE_NO_WINDOW
                    | NativeMethods.CREATE_NEW_PROCESS_GROUP;
                if (placement == JobPlacement.BeforeItRuns)
                {
                    // It must not run a single instruction before it is in the job.
                    flags |= NativeMethods.CREATE_SUSPENDED;
                }

                fixed (char* commandPointer = commandBuffer)
                fixed (char* environmentPointer = launch.Environment)
                {
                    var created = NativeMethods.CreateProcess(
                        launch.Application,
                        commandPointer,
                        0,
                        0,
                        bInheritHandles: true,
                        flags,
                        environmentPointer,
                        launch.WorkingDirectory,
                        ref startup,
                        out information);
                    error = created ? 0 : Marshal.GetLastPInvokeError();
                    return created;
                }
            }
        }
        finally
        {
            if (attributeListInitialized)
            {
                NativeMethods.DeleteProcThreadAttributeList(attributeList);
            }

            Marshal.FreeHGlobal(attributeList);
        }
    }

    /// <summary>Puts a process that was created suspended into its job, and only then lets it run.</summary>
    private static void LetRunInsideJob(JobObject job, SafeProcessHandle process, nint thread, string resolvedExecutable)
    {
        try
        {
            job.Assign(process);
        }
        catch
        {
            // Outside the job nothing would end it together with YAV. It has not run yet, so nothing is lost.
            NativeMethods.TerminateProcess(process.DangerousGetHandle(), 1);
            throw;
        }

        if (NativeMethods.ResumeThread(thread) == uint.MaxValue)
        {
            var error = Marshal.GetLastPInvokeError();
            throw new Win32Exception(error, $"Could not start '{Path.GetFileName(resolvedExecutable)}': {new Win32Exception(error).Message}");
        }
    }

    // How Windows refuses the job for a packaged program: through an app execution alias CreateProcess fails with
    // ERROR_ACCESS_DENIED, and for the path of the packaged program with 0xC0070005, the same error as an NTSTATUS.
    private static bool IsJobListRefused(int error) =>
        error is NativeMethods.ERROR_ACCESS_DENIED or unchecked((int)0xC0070005);

    public async Task<int> WaitForExitAsync(CancellationToken cancellationToken) =>
        await _exited.Task.WaitAsync(cancellationToken).ConfigureAwait(false);

    public void CloseInput()
    {
        lock (_gate)
        {
            if (_inputClosed)
            {
                return;
            }

            _inputClosed = true;
        }

        try
        {
            _stdin.Dispose();
        }
        catch (IOException)
        {
            // The child already closed its end.
        }
    }

    public async Task<bool> ShutdownAsync(TimeSpan grace)
    {
        if (HasExited)
        {
            return true;
        }

        CloseInput();
        try
        {
            await _exited.Task.WaitAsync(grace).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            Terminate();
            try
            {
                await _exited.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                // Termination was requested; the handle is released on dispose either way.
            }

            return false;
        }
    }

    public void Terminate() => _job.Terminate();

    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        // Ending the job also ends anything the child left running in the background.
        _job.Terminate();
        if (!HasExited)
        {
            try
            {
                await _exited.Task.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }
        }

        _registration.Unregister(null);
        _waiter.Dispose();
        CloseInput();
        _stdout.Dispose();
        _stderr.Dispose();
        _process.Dispose();
        _job.Dispose();
    }

    private void OnExited(object? state, bool timedOut)
    {
        if (_process.IsClosed)
        {
            _exited.TrySetResult(-1);
            return;
        }

        _exited.TrySetResult(NativeMethods.GetExitCodeProcess(_process.DangerousGetHandle(), out var code) ? unchecked((int)code) : -1);
    }

    /// <summary>What each attempt to create the process is given.</summary>
    private sealed record Launch(string Application, string Command, char[] Environment, string WorkingDirectory, nint[] Pipes, JobObject Job);

    private sealed class HandleWaiter : WaitHandle
    {
        public HandleWaiter(nint handle)
        {
            SafeWaitHandle = new SafeWaitHandle(handle, ownsHandle: false);
        }
    }
}
