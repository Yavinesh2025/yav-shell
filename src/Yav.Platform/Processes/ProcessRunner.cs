using System.Diagnostics;
using System.Text;
using Yav.Core.Ports;

namespace Yav.Platform.Processes;

public sealed class ExecutableNotFoundException(string command)
    : FileNotFoundException($"'{command}' was not found on PATH. Executables are never taken from the project directory unless the path is written explicitly.")
{
    public string Command { get; } = command;
}

/// <summary>Starts processes with resolved executable paths and structured arguments.</summary>
public sealed class ProcessRunner : IProcessRunner
{
    private static readonly UTF8Encoding Utf8NoBom = new(encoderShouldEmitUTF8Identifier: false);

    public string? Resolve(string command, string? workingDirectory = null) =>
        ExecutableResolver.Resolve(command, workingDirectory);

    public IRunningProcess Start(ProcessSpec spec)
    {
        if (spec.Io != ProcessIo.Piped)
        {
            throw new ArgumentException("Start creates piped processes. Use RunForegroundAsync for a process that uses the console.", nameof(spec));
        }

        var executable = ExecutableResolver.Resolve(spec.FileName, spec.WorkingDirectory)
            ?? throw new ExecutableNotFoundException(spec.FileName);
        return NativeProcess.Start(spec, executable);
    }

    public async Task<ProcessResult> RunAsync(ProcessSpec spec, CaptureOptions options, CancellationToken cancellationToken)
    {
        var started = Stopwatch.GetTimestamp();
        var process = Start(spec);
        await using (process.ConfigureAwait(false))
        {
            var output = new BoundedTextBuffer(options.MaxCapturedCharacters);
            var error = new BoundedTextBuffer(options.MaxCapturedCharacters);
            StreamWriter? log = null;
            var logGate = new Lock();

            try
            {
                if (options.LogPath is not null)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(options.LogPath))!);
                    log = new StreamWriter(options.LogPath, append: false, Utf8NoBom) { AutoFlush = false };
                }

                void Record(BoundedTextBuffer buffer, string line)
                {
                    lock (logGate)
                    {
                        buffer.AppendLine(line);
                        log?.WriteLine(line);
                    }

                    options.OnOutputLine?.Invoke(line);
                }

                var readOutput = PumpAsync(process.StandardOutput, line => Record(output, line));
                var readError = PumpAsync(process.StandardError, line => Record(error, line));
                var writeInput = WriteInputAsync(process, options.StandardInput);

                var timedOut = false;
                var cancelled = false;
                using var timeout = new CancellationTokenSource();
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
                if (options.Timeout is { } limit)
                {
                    timeout.CancelAfter(limit);
                }

                try
                {
                    await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    timedOut = timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested;
                    cancelled = cancellationToken.IsCancellationRequested;
                    process.Terminate();
                    try
                    {
                        await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None).ConfigureAwait(false);
                    }
                    catch (TimeoutException)
                    {
                    }
                }

                // A process that was ended may have left the pipes open in a grandchild; do not wait forever for them.
                var pumps = Task.WhenAll(readOutput, readError, writeInput);
                try
                {
                    await pumps.WaitAsync(timedOut || cancelled ? TimeSpan.FromSeconds(2) : TimeSpan.FromSeconds(30), CancellationToken.None).ConfigureAwait(false);
                }
                catch (TimeoutException)
                {
                }

                string standardOutput;
                string standardError;
                bool truncated;
                lock (logGate)
                {
                    log?.Flush();
                    standardOutput = output.ToString();
                    standardError = error.ToString();
                    truncated = output.Truncated || error.Truncated;
                }

                return new ProcessResult(
                    ExitCode: process.ExitCode ?? -1,
                    StandardOutput: standardOutput,
                    StandardError: standardError,
                    TimedOut: timedOut,
                    Cancelled: cancelled,
                    OutputTruncated: truncated,
                    Duration: Stopwatch.GetElapsedTime(started));
            }
            finally
            {
                if (log is not null)
                {
                    lock (logGate)
                    {
                        log.Dispose();
                    }
                }
            }
        }
    }

    public async Task<int> RunForegroundAsync(ProcessSpec spec, CancellationToken cancellationToken)
    {
        var executable = ExecutableResolver.Resolve(spec.FileName, spec.WorkingDirectory)
            ?? throw new ExecutableNotFoundException(spec.FileName);

        var start = new ProcessStartInfo
        {
            UseShellExecute = false,
            CreateNoWindow = false,
            WorkingDirectory = spec.WorkingDirectory,
        };

        if (ExecutableResolver.IsBatchFile(executable))
        {
            start.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            start.Arguments = CommandLine.BuildForBatch(executable, spec.Arguments);
        }
        else
        {
            start.FileName = executable;
            foreach (var argument in spec.Arguments)
            {
                start.ArgumentList.Add(argument);
            }
        }

        if (spec.Environment is not null)
        {
            foreach (var (key, value) in spec.Environment)
            {
                if (value is null)
                {
                    start.Environment.Remove(key);
                }
                else
                {
                    start.Environment[key] = value;
                }
            }
        }

        using var process = Process.Start(start) ?? throw new InvalidOperationException($"Could not start '{spec.FileName}'.");
        try
        {
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The foreground process belongs to the user. It is left running; only the wait is abandoned.
            throw;
        }

        return process.ExitCode;
    }

    public static bool IsProcessAlive(int processId, string? expectedName)
    {
        if (processId <= 0)
        {
            return false;
        }

        try
        {
            using var process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                return false;
            }

            // Process identifiers are reused, so the name has to match as well.
            return expectedName is null || string.Equals(process.ProcessName, expectedName, StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private static async Task PumpAsync(Stream stream, Action<string> onLine)
    {
        var decoder = new OutputDecoder(onLine);
        var buffer = new byte[32 * 1024];
        try
        {
            while (true)
            {
                var read = await stream.ReadAsync(buffer).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                decoder.Append(buffer.AsSpan(0, read));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
            // The pipe closed because the process ended or was terminated.
        }

        decoder.Complete();
    }

    private static async Task WriteInputAsync(IRunningProcess process, string? input)
    {
        try
        {
            if (!string.IsNullOrEmpty(input))
            {
                var bytes = Utf8NoBom.GetBytes(input);
                await process.StandardInput.WriteAsync(bytes).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
            // The process exited without reading its input.
        }
        finally
        {
            // Many tools wait for end of input before they start working.
            process.CloseInput();
        }
    }
}
