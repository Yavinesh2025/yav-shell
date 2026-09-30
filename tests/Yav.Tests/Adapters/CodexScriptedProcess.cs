using System.IO.Pipelines;
using System.Text;
using System.Text.Json.Nodes;
using Yav.Core.Ports;

namespace Yav.Tests.Adapters;

/// <summary>
/// Stands for the process of the Codex app server without starting one: the test says what the agent writes and
/// reads what YAV sends it, one line each. Used where a test must decide exactly when something arrives.
/// </summary>
internal sealed class CodexScriptedProcess : IRunningProcess
{
    private readonly Pipe _input = new();
    private readonly Pipe _output = new();
    private readonly Pipe _errors = new();
    private readonly TaskCompletionSource<int> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly StreamReader _heard;
    private readonly Lock _gate = new();
    private bool _outputEnded;

    public CodexScriptedProcess(Stream? output = null)
    {
        StandardInput = _input.Writer.AsStream();
        StandardOutput = output ?? _output.Reader.AsStream();
        StandardError = _errors.Reader.AsStream();
        _heard = new StreamReader(_input.Reader.AsStream(), new UTF8Encoding(false));
    }

    public int ProcessId => 4242;

    public Stream StandardInput { get; }

    public Stream StandardOutput { get; }

    public Stream StandardError { get; }

    public bool HasExited => _exited.Task.IsCompleted;

    public int? ExitCode => HasExited ? _exited.Task.Result : null;

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => _exited.Task.WaitAsync(cancellationToken);

    public void CloseInput() => _input.Writer.Complete();

    public Task<bool> ShutdownAsync(TimeSpan grace)
    {
        CloseInput();
        Exit(0);
        return Task.FromResult(true);
    }

    public void Terminate() => Exit(1);

    public ValueTask DisposeAsync()
    {
        Exit(1);
        return ValueTask.CompletedTask;
    }

    /// <summary>The agent writes one message.</summary>
    public async Task SayAsync(string json) => await _output.Writer.WriteAsync(Encoding.UTF8.GetBytes(json + "\n"));

    /// <summary>The next message YAV sent to the agent.</summary>
    public async Task<JsonObject> HeardAsync()
    {
        var line = await _heard.ReadLineAsync().WaitAsync(TimeSpan.FromSeconds(30))
            ?? throw new InvalidOperationException("YAV closed the input of the agent.");
        return JsonNode.Parse(line)!.AsObject();
    }

    /// <summary>The agent ends: its output closes and it has exited.</summary>
    public void Exit(int code)
    {
        lock (_gate)
        {
            if (!_outputEnded)
            {
                _outputEnded = true;
                _output.Writer.Complete();
                _errors.Writer.Complete();
            }
        }

        _exited.TrySetResult(code);
    }
}

/// <summary>
/// Starts processes as the real runner does, but holds the shutdown of the first <paramref name="held"/> of them
/// until the test lets it go, so that a test decides when a process that was given up actually ends.
/// </summary>
internal sealed class CodexHeldShutdownRunner(IProcessRunner inner, int held) : IProcessRunner
{
    private readonly List<CodexHeldProcess> _started = [];

    public CodexHeldProcess Started(int index)
    {
        lock (_started)
        {
            return _started[index];
        }
    }

    public string? Resolve(string command, string? workingDirectory = null) => inner.Resolve(command, workingDirectory);

    public IRunningProcess Start(ProcessSpec spec)
    {
        lock (_started)
        {
            var process = new CodexHeldProcess(inner.Start(spec));
            if (_started.Count >= held)
            {
                process.Release();
            }

            _started.Add(process);
            return process;
        }
    }

    public Task<ProcessResult> RunAsync(ProcessSpec spec, CaptureOptions options, CancellationToken cancellationToken) =>
        inner.RunAsync(spec, options, cancellationToken);

    public Task<int> RunForegroundAsync(ProcessSpec spec, CancellationToken cancellationToken) => inner.RunForegroundAsync(spec, cancellationToken);
}

/// <summary>A real process whose shutdown waits until the test releases it.</summary>
internal sealed class CodexHeldProcess(IRunningProcess inner) : IRunningProcess
{
    private readonly TaskCompletionSource _asked = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Ends when somebody asked the process to shut down.</summary>
    public Task ShutdownAsked => _asked.Task;

    public void Release() => _released.TrySetResult();

    public int ProcessId => inner.ProcessId;

    public Stream StandardInput => inner.StandardInput;

    public Stream StandardOutput => inner.StandardOutput;

    public Stream StandardError => inner.StandardError;

    public bool HasExited => inner.HasExited;

    public int? ExitCode => inner.ExitCode;

    public Task<int> WaitForExitAsync(CancellationToken cancellationToken) => inner.WaitForExitAsync(cancellationToken);

    public void CloseInput() => inner.CloseInput();

    public async Task<bool> ShutdownAsync(TimeSpan grace)
    {
        _asked.TrySetResult();
        await _released.Task;
        return await inner.ShutdownAsync(grace);
    }

    public void Terminate() => inner.Terminate();

    public ValueTask DisposeAsync()
    {
        _released.TrySetResult();
        return inner.DisposeAsync();
    }
}

/// <summary>An output that fails in a way nobody expects once it is told to.</summary>
internal sealed class CodexBrokenOutput : Stream
{
    private readonly TaskCompletionSource _broken = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Break() => _broken.TrySetResult();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        await _broken.Task.WaitAsync(cancellationToken);
        throw new InvalidOperationException("The output of the agent broke in an unexpected way.");
    }

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
