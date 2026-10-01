using System.Text;
using System.Text.Json;
using System.Threading.Channels;
using Yav.Adapters.Protocol;
using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Platform.Processes;

namespace Yav.Adapters;

/// <summary>
/// The compatibility path: one "codex exec" process per turn, with structured events on standard output.
/// This mode cannot ask for approval, cannot be steered and does not report the settings in effect, and
/// YAV says so instead of pretending otherwise. Permissions are never widened to make it work.
/// </summary>
public sealed class CodexExecAdapter : IAgentAdapter
{
    public const string AdapterId = "codex-exec";

    private readonly IProcessRunner _runner;
    private readonly TimeProvider _clock;
    private readonly AdapterOptions _options;
    private readonly Func<CancellationToken, Task<IReadOnlyList<ModelInfo>>>? _modelSource;

    /// <param name="modelSource">Where the model list comes from, since this mode cannot list models itself.</param>
    public CodexExecAdapter(
        IProcessRunner runner,
        TimeProvider clock,
        AdapterOptions options,
        Func<CancellationToken, Task<IReadOnlyList<ModelInfo>>>? modelSource = null)
    {
        _runner = runner;
        _clock = clock;
        _options = options;
        _modelSource = modelSource;
    }

    public string Id => AdapterId;

    public string Provider => "openai";

    public string DisplayName => "Codex (exec, non-interactive)";

    public AdapterCapabilities Capabilities { get; } = new(
        AdapterFeatures.Streaming | AdapterFeatures.Interrupt | AdapterFeatures.ResumeSession | AdapterFeatures.StructuredOutput
        | AdapterFeatures.UsageReporting | AdapterFeatures.ReadOnlyEnforcement,
        [
            "Approvals are not possible in this mode. An action that needs approval ends the turn as Approval Required.",
            "The model and effort in effect are not reported, so they stay Requested / Unverified.",
            "A running turn cannot be steered, and it can only be stopped by ending the process.",
            "Available models cannot be listed in this mode.",
        ]);

    internal TimeProvider Clock => _clock;

    internal IProcessRunner Runner => _runner;

    internal AdapterOptions Options => _options;

    internal string? ResolveExecutable() =>
        string.IsNullOrEmpty(_options.ExecutablePath) ? _runner.Resolve("codex") : File.Exists(_options.ExecutablePath) ? _options.ExecutablePath : null;

    public async Task<AdapterDetection> DetectAsync(CancellationToken cancellationToken)
    {
        const string Note = "OpenAI documents 'codex exec' as the stable interface for non-interactive runs";
        var executable = ResolveExecutable();
        if (executable is null)
        {
            return new AdapterDetection(
                AdapterId, Provider, false, null, null, AdapterMaturity.Stable, Note, false, CodexAppServerAdapter.TestedVersions,
                ["The Codex CLI was not found. Install it from https://developers.openai.com/codex and sign in with 'codex login'."]);
        }

        var problems = new List<string>();
        string? version = null;
        try
        {
            var result = await _runner.RunAsync(
                new ProcessSpec(executable, ["--version"], Path.GetDirectoryName(executable)!, _options.Environment, Label: "codex --version"),
                new CaptureOptions(Timeout: TimeSpan.FromSeconds(20)),
                cancellationToken).ConfigureAwait(false);
            version = CodexAppServerAdapter.ParseVersion(result.StandardOutput);
            if (result.ExitCode != 0 || version is null)
            {
                problems.Add("The Codex CLI did not report its version: " + (result.StandardError + result.StandardOutput).Trim());
            }
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            problems.Add("The Codex CLI could not be started: " + ex.Message);
        }

        return new AdapterDetection(
            AdapterId, Provider, true, executable, version, AdapterMaturity.Stable, Note,
            CodexAppServerAdapter.IsTestedVersion(version),
            CodexAppServerAdapter.TestedVersions, problems);
    }

    public async Task<AuthStatus> GetAuthStatusAsync(CancellationToken cancellationToken)
    {
        var now = _clock.GetUtcNow();
        const string Source = "codex login status";
        var executable = ResolveExecutable();
        if (executable is null)
        {
            return new AuthStatus(false, AccountRouteKind.NotAuthenticated, "Codex is not installed", null, null, BillingKind.Unknown, RoutePolicy.Allowed, null, Source, now);
        }

        var result = await _runner.RunAsync(
            new ProcessSpec(executable, ["login", "status"], Path.GetDirectoryName(executable)!, _options.Environment, Label: "codex login status"),
            new CaptureOptions(Timeout: TimeSpan.FromSeconds(30)),
            cancellationToken).ConfigureAwait(false);
        var text = (result.StandardOutput + result.StandardError).Trim();

        if (result.ExitCode != 0 || text.Contains("Not logged in", StringComparison.OrdinalIgnoreCase))
        {
            return new AuthStatus(
                false, AccountRouteKind.NotAuthenticated, "Not signed in", null, null, BillingKind.Unknown, RoutePolicy.Allowed,
                "Sign in with 'codex login' or /login codex.", Source, now);
        }

        if (text.Contains("API key", StringComparison.OrdinalIgnoreCase))
        {
            return new AuthStatus(
                true, AccountRouteKind.ApiKey, "OpenAI API key", null, "openai", BillingKind.PayPerToken, RoutePolicy.RequiresAcknowledgement,
                "Usage is billed per token to the API account, separately from any ChatGPT plan.", Source, now);
        }

        if (text.Contains("ChatGPT", StringComparison.OrdinalIgnoreCase))
        {
            return new AuthStatus(
                true, AccountRouteKind.Subscription, "ChatGPT plan", null, "openai", BillingKind.IncludedInSubscription,
                RoutePolicy.RequiresAcknowledgement,
                "Usage counts against the limits of your ChatGPT plan. The plan type is not reported in this mode.", Source, now);
        }

        return new AuthStatus(
            true, AccountRouteKind.Unknown, "Signed in (route not recognized)", null, null, BillingKind.Unknown, RoutePolicy.RequiresAcknowledgement,
            "Codex reported a sign-in state this version of YAV does not recognize.", Source, now);
    }

    public async Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken)
    {
        if (_modelSource is null)
        {
            return [];
        }

        try
        {
            return await _modelSource(cancellationToken).ConfigureAwait(false);
        }
        catch (AgentProtocolException)
        {
            return [];
        }
    }

    public Task<RateLimitSnapshot?> GetRateLimitsAsync(CancellationToken cancellationToken) => Task.FromResult<RateLimitSnapshot?>(null);

    public Task<IAgentSession> StartSessionAsync(SessionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<IAgentSession>(new CodexExecSession(this, request, null));

    public Task<IAgentSession> ResumeSessionAsync(string sessionId, SessionRequest request, CancellationToken cancellationToken) =>
        Task.FromResult<IAgentSession>(new CodexExecSession(this, request, sessionId));

    public Task<SessionProbe> ProbeSessionAsync(string sessionId, CancellationToken cancellationToken) =>
        Task.FromResult(new SessionProbe(SessionProbeState.Unsupported, "A session cannot be examined in this mode without sending a turn."));

    public LoginFlow? GetLoginFlow()
    {
        var executable = ResolveExecutable();
        return executable is null
            ? null
            : new LoginFlow(executable, ["login"], "Codex's own sign-in", ["The sign-in runs in Codex itself. YAV does not see or store the credentials."]);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

internal sealed class CodexExecSession : IAgentSession
{
    private readonly CodexExecAdapter _adapter;
    private readonly SessionRequest _request;
    private readonly SessionEvents _events = new();
    private readonly Lock _gate = new();
    private IRunningProcess? _process;
    private Task _turn = Task.CompletedTask;
    private bool _interrupted;
    private bool _running;
    private bool _disposed;

    public CodexExecSession(CodexExecAdapter adapter, SessionRequest request, string? sessionId)
    {
        _adapter = adapter;
        _request = request;
        SessionId = sessionId;
        // Nothing here was said by the agent, so nothing is filled in: also not the directory YAV asked it to work in.
        Effective = new EffectiveSettings(
            null, null, null, null, null, null, null, null, [], [],
            "codex exec does not report the settings in effect");
    }

    public string AdapterId => CodexExecAdapter.AdapterId;

    public AgentRole Role => _request.Role;

    public string? SessionId { get; private set; }

    public EffectiveSettings? Effective { get; }

    public ChannelReader<AgentEvent> Events => _events.Reader;

    private DateTimeOffset Now => _adapter.Clock.GetUtcNow();

    public Task StartTurnAsync(TurnRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_running)
            {
                throw new InvalidOperationException("A turn is already running in this session.");
            }

            _running = true;
            _interrupted = false;
        }

        var executable = _adapter.ResolveExecutable();
        if (executable is null)
        {
            lock (_gate)
            {
                _running = false;
            }

            throw new AgentProtocolException("The Codex CLI was not found. Run /doctor for details.");
        }

        string? schemaFile = null;
        try
        {
            var arguments = new List<string> { "exec" };
            if (SessionId is not null)
            {
                // Always an explicit identifier, never "the most recent session".
                arguments.Add("resume");
                arguments.Add(SessionId);
            }

            arguments.Add("--json");
            arguments.Add("--skip-git-repo-check");
            var sandbox = _request.Sandbox == SandboxLevel.ReadOnly ? "read-only" : "workspace-write";
            if (SessionId is null)
            {
                arguments.AddRange(["-s", sandbox]);
                arguments.AddRange(["-C", _request.WorkingDirectory]);
            }
            else
            {
                // "exec resume" has no sandbox option of its own; the configuration override keeps the boundary explicit.
                arguments.AddRange(["-c", "sandbox_mode=" + sandbox]);
            }

            arguments.AddRange(["-m", _request.ModelId]);
            if (_request.Effort.Length > 0)
            {
                arguments.AddRange(["-c", "model_reasoning_effort=" + _request.Effort]);
            }

            if (_request.ServiceTier is not null)
            {
                arguments.AddRange(["-c", "service_tier=" + _request.ServiceTier]);
            }

            var prompt = request.Prompt;
            if (ExecutableResolver.IsBatchFile(executable))
            {
                // A batch launcher cannot carry text with quotes or line breaks safely, so the role instructions
                // travel in front of the task on standard input instead.
                prompt = "<role-instructions>\n" + _request.RoleInstructions + "\n</role-instructions>\n\n" + request.Prompt;
            }
            else
            {
                arguments.AddRange(["-c", "developer_instructions=" + JsonSerializer.Serialize(_request.RoleInstructions, ExecJson.Default.String)]);
            }

            if (request.OutputSchema is { } schema)
            {
                var directory = _adapter.Options.DiagnosticsDirectory ?? Path.Combine(Path.GetTempPath(), "yav-shell");
                Directory.CreateDirectory(directory);
                schemaFile = Path.Combine(directory, "schema-" + Guid.NewGuid().ToString("N")[..12] + ".json");
                File.WriteAllText(schemaFile, schema.GetRawText(), new UTF8Encoding(false));
                arguments.AddRange(["--output-schema", schemaFile]);
            }

            // "-" makes Codex read the prompt from standard input.
            arguments.Add("-");

            var process = _adapter.Runner.Start(new ProcessSpec(
                executable, arguments, _request.WorkingDirectory, _adapter.Options.Environment, Label: "codex exec"));
            lock (_gate)
            {
                _process = process;
            }

            var schemaPath = schemaFile;
            schemaFile = null;
            _turn = Task.Run(() => RunAsync(process, prompt, request.OutputSchema is not null, schemaPath), CancellationToken.None);
            return Task.CompletedTask;
        }
        catch
        {
            lock (_gate)
            {
                _running = false;
            }

            if (schemaFile is not null && File.Exists(schemaFile))
            {
                File.Delete(schemaFile);
            }

            throw;
        }
    }

    private async Task RunAsync(IRunningProcess process, string prompt, bool structured, string? schemaFile)
    {
        var diagnostics = new DiagnosticTail();
        string? finalMessage = null;
        string? failure = null;
        var completed = false;

        try
        {
            var errors = Task.Run(async () =>
            {
                var decoder = new OutputDecoder(diagnostics.Append);
                var buffer = new byte[8 * 1024];
                try
                {
                    int read;
                    while ((read = await process.StandardError.ReadAsync(buffer).ConfigureAwait(false)) > 0)
                    {
                        decoder.Append(buffer.AsSpan(0, read));
                    }
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException)
                {
                }

                decoder.Complete();
            });

            try
            {
                await process.StandardInput.WriteAsync(new UTF8Encoding(false).GetBytes(prompt)).ConfigureAwait(false);
                await process.StandardInput.FlushAsync().ConfigureAwait(false);
            }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException)
            {
            }

            // Codex reads the prompt until the end of its input.
            process.CloseInput();

            var reader = new JsonLineReader(process.StandardOutput);
            while (await reader.ReadFrameAsync(CancellationToken.None).ConfigureAwait(false) is { } frame)
            {
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(frame);
                }
                catch (JsonException)
                {
                    await _events.PublishAsync(new AgentNotice(Now, "The agent sent a line that is not valid JSON; it was skipped.", true)).ConfigureAwait(false);
                    continue;
                }

                using (document)
                {
                    var root = document.RootElement;
                    switch (root.Text("type"))
                    {
                        case "thread.started":
                            SessionId = root.Text("thread_id") ?? SessionId;
                            await _events.PublishAsync(new SessionConfigured(Now, SessionId ?? string.Empty, Effective!)).ConfigureAwait(false);
                            break;

                        case "turn.started":
                            await _events.PublishAsync(new TurnStarted(Now, string.Empty)).ConfigureAwait(false);
                            break;

                        case "item.started":
                            if (root.Child("item") is { } started && started.Text("type") == "command_execution")
                            {
                                await _events.PublishAsync(new CommandStarted(Now, started.Text("id") ?? string.Empty, started.Text("command") ?? string.Empty, _request.WorkingDirectory)).ConfigureAwait(false);
                            }

                            break;

                        case "item.completed":
                            if (root.Child("item") is { } item)
                            {
                                finalMessage = await HandleItemAsync(item).ConfigureAwait(false) ?? finalMessage;
                            }

                            break;

                        case "turn.completed":
                            completed = true;
                            if (root.Child("usage") is { } usage)
                            {
                                // The totals are those of the whole session, also after a resume.
                                await _events.PublishAsync(new UsageUpdated(Now, new UsageSnapshot(
                                    AdapterId, SessionId, null, UsageScope.CumulativeForSession,
                                    TokenCounts.FromInclusiveCounters(
                                        usage.Number("input_tokens"), usage.Number("cached_input_tokens"), usage.Number("output_tokens"),
                                        usage.Number("reasoning_output_tokens"),
                                        usage.Number("cache_write_input_tokens") is > 0 ? usage.Number("cache_write_input_tokens") : null),
                                    null, null, ValueProvenance.Unavailable, null, "turn.completed", Now))).ConfigureAwait(false);
                            }

                            break;

                        case "turn.failed":
                            failure = root.Child("error")?.Text("message") ?? "The turn failed.";
                            break;

                        case "error":
                            await _events.PublishAsync(new AgentError(Now, root.Text("message") ?? "The agent reported an error.", null, WillRetry: true)).ConfigureAwait(false);
                            break;
                    }
                }
            }

            var exitCode = await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            try
            {
                await errors.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
            }

            bool interrupted;
            lock (_gate)
            {
                interrupted = _interrupted;
            }

            TurnOutcome outcome;
            string? message = null;
            if (interrupted)
            {
                outcome = TurnOutcome.Interrupted;
            }
            else if (failure is not null)
            {
                outcome = Classify(failure);
                message = failure;
            }
            else if (completed && exitCode == 0)
            {
                outcome = TurnOutcome.Completed;
            }
            else
            {
                outcome = TurnOutcome.Failed;
                var tail = diagnostics.Read();
                message = $"The agent ended with exit code {exitCode} before the turn was finished."
                    + (tail.Length > 0 ? " Last diagnostic output: " + tail : string.Empty);
            }

            await _events.PublishAsync(new TurnCompleted(
                Now, string.Empty, outcome, finalMessage,
                structured && outcome == TurnOutcome.Completed ? JsonReading.ParseObject(finalMessage) : null,
                null, message, [])).ConfigureAwait(false);
        }
        finally
        {
            if (schemaFile is not null)
            {
                try
                {
                    File.Delete(schemaFile);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                {
                }
            }

            await process.DisposeAsync().ConfigureAwait(false);
            lock (_gate)
            {
                _process = null;
                _running = false;
            }
        }
    }

    private async Task<string?> HandleItemAsync(JsonElement item)
    {
        var id = item.Text("id") ?? string.Empty;
        switch (item.Text("type"))
        {
            case "agent_message":
            {
                var text = item.Text("text") ?? string.Empty;
                await _events.PublishAsync(new AssistantMessage(Now, id, text, MessagePhase.Unknown)).ConfigureAwait(false);
                return text;
            }

            case "reasoning":
                if (item.Text("text") is { Length: > 0 } summary)
                {
                    await _events.PublishAsync(new ReasoningSummary(Now, id, summary)).ConfigureAwait(false);
                }

                break;

            case "command_execution":
                await _events.PublishAsync(new CommandCompleted(
                    Now, id, item.Text("command") ?? string.Empty, (int?)item.Number("exit_code"), null,
                    item.Text("status") ?? "completed", item.Text("aggregated_output"))).ConfigureAwait(false);
                break;

            case "file_change":
            {
                var changes = item.Items("changes").Select(change => new FileChange(
                    change.Text("path") ?? string.Empty,
                    change.Text("kind") switch
                    {
                        "add" => FileChangeKind.Add,
                        "delete" => FileChangeKind.Delete,
                        _ => FileChangeKind.Update,
                    },
                    null)).ToList();
                await _events.PublishAsync(new FilesChanged(Now, id, changes, item.Text("status") ?? "completed")).ConfigureAwait(false);
                break;
            }

            case "mcp_tool_call":
                await _events.PublishAsync(new ToolActivity(Now, id, $"{item.Text("server")}/{item.Text("tool")}", "MCP tool call", item.Text("status") ?? "completed")).ConfigureAwait(false);
                break;

            case "web_search":
                await _events.PublishAsync(new ToolActivity(Now, id, "web search", item.Text("query") ?? string.Empty, "completed")).ConfigureAwait(false);
                break;

            case "error":
                await _events.PublishAsync(new AgentNotice(Now, item.Text("message") ?? string.Empty, IsWarning: true)).ConfigureAwait(false);
                break;
        }

        return null;
    }

    private static TurnOutcome Classify(string message)
    {
        if (message.Contains("approval", StringComparison.OrdinalIgnoreCase) && message.Contains("not supported", StringComparison.OrdinalIgnoreCase))
        {
            return TurnOutcome.ApprovalRequired;
        }

        if (message.Contains("usage limit", StringComparison.OrdinalIgnoreCase) || message.Contains("out of credits", StringComparison.OrdinalIgnoreCase))
        {
            return TurnOutcome.UsageLimitReached;
        }

        if (message.Contains("rate limit", StringComparison.OrdinalIgnoreCase) || message.Contains("429", StringComparison.Ordinal)
            || message.Contains("overloaded", StringComparison.OrdinalIgnoreCase))
        {
            return TurnOutcome.RateLimited;
        }

        return TurnOutcome.Failed;
    }

    public Task RespondToApprovalAsync(string approvalId, ApprovalDecision decision, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Approvals are not possible with the non-interactive Codex adapter.");

    public Task InterruptAsync(CancellationToken cancellationToken)
    {
        IRunningProcess? process;
        lock (_gate)
        {
            process = _process;
            if (process is not null)
            {
                _interrupted = true;
            }
        }

        // This mode has no request to stop a turn, so the YAV-owned process tree is ended.
        process?.Terminate();
        return Task.CompletedTask;
    }

    public Task<bool> SteerAsync(string text, CancellationToken cancellationToken) => Task.FromResult(false);

    public async Task ShutdownAsync(TimeSpan grace, CancellationToken cancellationToken)
    {
        await InterruptAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await _turn.WaitAsync(grace + TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }
    }

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

        await InterruptAsync(CancellationToken.None).ConfigureAwait(false);
        try
        {
            await _turn.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        _events.Complete();
    }
}

[System.Text.Json.Serialization.JsonSerializable(typeof(string))]
internal sealed partial class ExecJson : System.Text.Json.Serialization.JsonSerializerContext;
