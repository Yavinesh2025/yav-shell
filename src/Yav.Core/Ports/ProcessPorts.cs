namespace Yav.Core.Ports;

public enum ProcessIo
{
    /// <summary>Standard input, output and error are pipes owned by YAV.</summary>
    Piped,

    /// <summary>The child uses the real console. Used only for the explicit foreground shell and /exec.</summary>
    InheritConsole,
}

/// <summary>
/// Describes a process to start. Arguments are passed as a structured list and are never assembled into a
/// shell command line by string interpolation. Large prompts travel over standard input or a file.
/// </summary>
public sealed record ProcessSpec(
    string FileName,
    IReadOnlyList<string> Arguments,
    string WorkingDirectory,
    // Overrides applied on top of the inherited environment. A null value removes the variable.
    IReadOnlyDictionary<string, string?>? Environment = null,
    ProcessIo Io = ProcessIo.Piped,
    // Text used in diagnostics. Never contains secrets.
    string? Label = null);

public sealed record ProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut,
    bool Cancelled,
    bool OutputTruncated,
    TimeSpan Duration)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut && !Cancelled;
}

public sealed record CaptureOptions(
    TimeSpan? Timeout = null,
    // Upper bound for each captured stream held in memory. Bytes beyond it are counted and, when LogPath is set, still written to the log.
    int MaxCapturedCharacters = 4 * 1024 * 1024,
    // Optional file that receives the complete, untruncated output.
    string? LogPath = null,
    string? StandardInput = null,
    Action<string>? OnOutputLine = null);

public interface IRunningProcess : IAsyncDisposable
{
    int ProcessId { get; }

    Stream StandardInput { get; }

    Stream StandardOutput { get; }

    Stream StandardError { get; }

    bool HasExited { get; }

    int? ExitCode { get; }

    Task<int> WaitForExitAsync(CancellationToken cancellationToken);

    /// <summary>Closes standard input, which most tools treat as a request to finish.</summary>
    void CloseInput();

    /// <summary>
    /// Closes input and waits for a normal exit. When the grace period ends the whole YAV-owned process
    /// tree is terminated through its job object. Returns true when the process exited on its own.
    /// </summary>
    Task<bool> ShutdownAsync(TimeSpan grace);

    /// <summary>Terminates this process and every process it started. Unrelated processes are never affected.</summary>
    void Terminate();
}

public interface IProcessRunner
{
    /// <summary>Finds an executable on PATH using PATHEXT. Returns the full path or null.</summary>
    string? Resolve(string command, string? workingDirectory = null);

    IRunningProcess Start(ProcessSpec spec);

    Task<ProcessResult> RunAsync(ProcessSpec spec, CaptureOptions options, CancellationToken cancellationToken);

    /// <summary>Runs a process attached to the real console and waits for it. Returns its exit code.</summary>
    Task<int> RunForegroundAsync(ProcessSpec spec, CancellationToken cancellationToken);
}

/// <summary>Stores secrets in the operating system's credential store. Secrets never enter settings, logs or transcripts.</summary>
public interface ICredentialStore
{
    bool IsAvailable { get; }

    bool Exists(string name);

    string? Read(string name);

    void Write(string name, string secret, string comment);

    bool Delete(string name);
}
