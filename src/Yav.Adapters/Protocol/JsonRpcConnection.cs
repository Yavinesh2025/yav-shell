using System.Buffers;
using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Yav.Core.Ports;
using Yav.Platform.Processes;

namespace Yav.Adapters.Protocol;

/// <summary>
/// One JSON-RPC conversation over a child process's standard input and output, in the form the Codex app
/// server uses: one JSON object per line and no "jsonrpc" field. Standard output carries only protocol
/// messages; standard error is drained separately and never parsed.
/// </summary>
internal sealed class JsonRpcConnection : IAsyncDisposable
{
    private readonly IRunningProcess _process;
    private readonly TimeSpan _requestTimeout;
    private readonly DiagnosticTail _diagnostics;
    private readonly ConcurrentDictionary<long, Pending> _pending = new();
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly CancellationTokenSource _closing = new();
    private readonly Task _reader;
    private readonly Task _errorReader;
    private long _nextId;
    private int _disposed;

    // Why the connection ended, once it has: a request made afterwards fails at once instead of waiting.
    private volatile string? _ended;

    /// <param name="OnAnswer">Called by the reader with the result, before the message that follows it is handled.</param>
    private sealed record Pending(TaskCompletionSource<JsonElement> Waiter, Action<JsonElement>? OnAnswer);

    public JsonRpcConnection(IRunningProcess process, TimeSpan requestTimeout, DiagnosticTail diagnostics)
    {
        _process = process;
        _requestTimeout = requestTimeout;
        _diagnostics = diagnostics;
        _reader = Task.Run(ReadLoopAsync);
        _errorReader = Task.Run(ReadErrorsAsync);
    }

    /// <summary>Called for each notification, in the order they arrived. The element is only valid during the call.</summary>
    public Func<string, JsonElement, ValueTask>? OnNotification { get; set; }

    /// <summary>Called for each request the agent sends. The first argument is the raw JSON of the request id.</summary>
    public Func<string, string, JsonElement, ValueTask>? OnServerRequest { get; set; }

    /// <summary>Called when a line could not be understood.</summary>
    public Func<string, ValueTask>? OnProtocolProblem { get; set; }

    /// <summary>Called once when the agent's output ended. The argument is the exit code, when known.</summary>
    public Func<int?, string, ValueTask>? OnClosed { get; set; }

    public bool IsAlive => !_reader.IsCompleted && !_process.HasExited;

    public int ProcessId => _process.ProcessId;

    public string DiagnosticTail => _diagnostics.Read();

    /// <param name="onAnswer">
    /// Called with the result by the reader itself, before it handles the message that follows the answer. What
    /// depends on the order of the answer and later messages is done there; it must be short and must not wait.
    /// </param>
    public async Task<JsonElement> RequestAsync(
        string method, Action<Utf8JsonWriter>? writeParameters, CancellationToken cancellationToken, TimeSpan? timeout = null, Action<JsonElement>? onAnswer = null)
    {
        var id = Interlocked.Increment(ref _nextId);
        var waiter = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pending[id] = new Pending(waiter, onAnswer);
        try
        {
            // The reader fails what is waiting when it ends; a request that came later would wait for nothing.
            if (_ended is { } ended)
            {
                throw new AgentProtocolException(ended);
            }

            await SendAsync(
                writer =>
                {
                    writer.WriteString("method", method);
                    writer.WriteNumber("id", id);
                    if (writeParameters is not null)
                    {
                        writer.WritePropertyName("params");
                        writer.WriteStartObject();
                        writeParameters(writer);
                        writer.WriteEndObject();
                    }
                },
                cancellationToken).ConfigureAwait(false);

            var limit = timeout ?? _requestTimeout;
            try
            {
                // A malformed request gets no answer at all, so every request has a time limit.
                return await waiter.Task.WaitAsync(limit, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                throw new AgentProtocolException(
                    $"The agent did not answer '{method}' within {limit.TotalSeconds:0.#} seconds."
                    + (DiagnosticTail.Length > 0 ? " Last diagnostic output: " + DiagnosticTail : string.Empty));
            }
        }
        finally
        {
            _pending.TryRemove(id, out _);
        }
    }

    public Task NotifyAsync(string method, CancellationToken cancellationToken) =>
        SendAsync(writer => writer.WriteString("method", method), cancellationToken);

    public Task RespondAsync(string rawId, Action<Utf8JsonWriter> writeResult, CancellationToken cancellationToken) =>
        SendAsync(
            writer =>
            {
                writer.WritePropertyName("id");
                writer.WriteRawValue(rawId);
                writer.WritePropertyName("result");
                writer.WriteStartObject();
                writeResult(writer);
                writer.WriteEndObject();
            },
            cancellationToken);

    public Task RespondErrorAsync(string rawId, int code, string message, CancellationToken cancellationToken) =>
        SendAsync(
            writer =>
            {
                writer.WritePropertyName("id");
                writer.WriteRawValue(rawId);
                writer.WritePropertyName("error");
                writer.WriteStartObject();
                writer.WriteNumber("code", code);
                writer.WriteString("message", message);
                writer.WriteEndObject();
            },
            cancellationToken);

    private async Task SendAsync(Action<Utf8JsonWriter> write, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = false }))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        // One line per message, no byte order mark: a mark would make the first line unreadable for the agent.
        buffer.Write("\n"u8);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _process.StandardInput.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await _process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
        {
            throw new AgentProtocolException("The agent process is no longer reading requests. " + DiagnosticTail, inner: ex);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task ReadLoopAsync()
    {
        var reason = "The agent closed its output.";
        try
        {
            var reader = new JsonLineReader(_process.StandardOutput);
            while (await reader.ReadFrameAsync(_closing.Token).ConfigureAwait(false) is { } frame)
            {
                await DispatchAsync(frame).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            reason = "The connection was closed.";
        }
        catch (FrameTooLargeException ex)
        {
            reason = ex.Message;
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            reason = "Reading from the agent failed: " + ex.Message;
        }
        finally
        {
            // Whatever ended the loop, what waits for the agent is told, so that nothing waits without a limit.
            await EndAsync(reason).ConfigureAwait(false);
        }
    }

    private async Task EndAsync(string reason)
    {
        int? exitCode = null;
        try
        {
            exitCode = await _process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Not ended yet, or not known: the reason the output ended is what is said then.
        }

        try
        {
            await _errorReader.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // What the agent wrote to standard error until now is what is told.
        }

        var tail = DiagnosticTail;
        var description = exitCode is null ? reason : $"The agent process ended (exit code {exitCode}).";
        if (tail.Length > 0)
        {
            description += " Last diagnostic output: " + tail;
        }

        _ended = description;
        foreach (var (_, pending) in _pending)
        {
            pending.Waiter.TrySetException(new AgentProtocolException(description));
        }

        if (OnClosed is { } closed)
        {
            try
            {
                await closed(exitCode, description).ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                // Nobody is left to tell; the diagnostic output keeps it for /doctor.
                _diagnostics.Append("YAV: telling the sessions that the agent ended failed: " + ex.Message);
            }
        }
    }

    /// <summary>Tells the sessions about a problem. A problem in telling them is kept in the diagnostic output only.</summary>
    private async ValueTask ReportProblemAsync(string message)
    {
        try
        {
            if (OnProtocolProblem is { } problem)
            {
                await problem(message).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _diagnostics.Append($"YAV: {message} Telling the sessions failed too: {ex.Message}");
        }
    }

    private async ValueTask DispatchAsync(ReadOnlyMemory<byte> frame)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(frame);
        }
        catch (JsonException)
        {
            await ReportProblemAsync("The agent sent a line that is not valid JSON; it was skipped.").ConfigureAwait(false);
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            var hasMethod = root.TryGetProperty("method", out var methodElement) && methodElement.ValueKind == JsonValueKind.String;
            var hasId = root.TryGetProperty("id", out var idElement) && idElement.ValueKind is JsonValueKind.Number or JsonValueKind.String;
            var parameters = root.TryGetProperty("params", out var given) ? given : default;

            if (hasMethod)
            {
                var method = methodElement.GetString()!;
                try
                {
                    if (hasId && OnServerRequest is { } request)
                    {
                        await request(idElement.GetRawText(), method, parameters).ConfigureAwait(false);
                    }
                    else if (!hasId && OnNotification is { } notification)
                    {
                        await notification(method, parameters).ConfigureAwait(false);
                    }
                }
                catch (Exception ex) when (!_closing.IsCancellationRequested)
                {
                    // One message that could not be handled must not end the conversation of every session.
                    await ReportProblemAsync($"YAV could not handle the message '{method}' of the agent: {ex.Message}").ConfigureAwait(false);
                }
            }
            else if (hasId && idElement.ValueKind == JsonValueKind.Number && idElement.TryGetInt64(out var id) && _pending.TryRemove(id, out var pending))
            {
                if (root.TryGetProperty("error", out var error) && error.ValueKind == JsonValueKind.Object)
                {
                    var code = error.TryGetProperty("code", out var codeElement) && codeElement.TryGetInt32(out var parsed) ? parsed : (int?)null;
                    pending.Waiter.TrySetException(new AgentProtocolException(error.Text("message") ?? "The agent reported an error.", code));
                }
                else
                {
                    var result = root.TryGetProperty("result", out var answer) ? answer.Clone() : default;
                    try
                    {
                        pending.OnAnswer?.Invoke(result);
                        pending.Waiter.TrySetResult(result);
                    }
                    catch (Exception ex) when (ex is not OutOfMemoryException)
                    {
                        // The one who asked learns it; the reader goes on.
                        pending.Waiter.TrySetException(ex);
                    }
                }
            }
        }
    }

    private async Task ReadErrorsAsync()
    {
        var decoder = new OutputDecoder(_diagnostics.Append);
        var buffer = new byte[8 * 1024];
        try
        {
            while (true)
            {
                var read = await _process.StandardError.ReadAsync(buffer, _closing.Token).ConfigureAwait(false);
                if (read <= 0)
                {
                    break;
                }

                decoder.Append(buffer.AsSpan(0, read));
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or OperationCanceledException)
        {
        }

        decoder.Complete();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        // Closing the input is the documented way to end the connection; termination is the fallback.
        await _process.ShutdownAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        _closing.Cancel();
        try
        {
            await Task.WhenAll(_reader, _errorReader).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // What the reader ran into was told to the sessions already. Throwing it here would leave the
            // process and the write gate undisposed, and the next connection would inherit the failure.
        }

        await _process.DisposeAsync().ConfigureAwait(false);
        _closing.Dispose();
        _writeGate.Dispose();
    }
}
