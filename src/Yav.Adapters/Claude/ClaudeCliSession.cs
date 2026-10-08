using System.Buffers;
using System.Collections.Concurrent;
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
/// One Claude Code conversation. The process stays open between turns; it is started on the first turn and
/// started again with the same session identifier when it has ended.
/// </summary>
internal sealed class ClaudeCliSession : IAgentSession, IReportsBeforeTurn
{
    private const string Redacted = "[redacted]";

    private const string SettingsSource = "get_settings answer";

    private const string SettingsNotAnswered = "get_settings (not answered)";

    private const string AccountSource = "initialize answer";

    /// <summary>How many lines of what a tool is given are listed in a question, and how long one of them may be.</summary>
    private const int ListedLines = 400;

    private const int ListedCharacters = 2000;

    /// <summary>The tool a result is handed back with when a schema was given for it.</summary>
    private const string ResultTool = "StructuredOutput";

    /// <summary>What is reported for a model that is sent no effort, whatever was asked for.</summary>
    private const string NoEffort = "none";

    // Tools that can neither change files nor run code.
    private static readonly HashSet<string> ReadOnlyTools = new(StringComparer.Ordinal)
    {
        "Read", "Glob", "Grep", "LS", "NotebookRead", "TodoWrite", "TodoRead", "EndConversation", ResultTool,
    };

    private static readonly HashSet<string> FileTools = new(StringComparer.Ordinal) { "Write", "Edit", "MultiEdit", "NotebookEdit" };

    private static readonly HashSet<string> CommandTools = new(StringComparer.Ordinal) { "Bash", "PowerShell" };

    // Settings files of the workspace. In a review, the workspace holds what Model A left there, and settings
    // can carry hooks, which are programs.
    private static readonly HashSet<string> WorkspaceSettings = new(StringComparer.OrdinalIgnoreCase) { "projectSettings", "localSettings" };

    private readonly ClaudeCliAdapter _adapter;
    private readonly SessionRequest _request;
    private readonly SessionEvents _events = new();
    private readonly ConcurrentDictionary<string, PendingApproval> _approvals = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, PendingTool> _tools = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, TaskCompletionSource<Answer>> _answers = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private readonly Lock _gate = new();
    private readonly bool _resumeExisting;
    private IRunningProcess? _process;
    private Task _reader = Task.CompletedTask;
    private DiagnosticTail _diagnostics = new();
    private string? _instructionsFile;
    private string? _processSchema;
    private string? _secret;
    private Applied? _applied;
    private AccountSaid? _account;
    private IRunningProcess? _askedOf;
    private bool _settingsNotAnswered;
    private bool _accountNotAnswered;
    private bool _sessionExists;
    private bool _turnRunning;

    /// <summary>The process whose reader has ended: nothing it says is read any more, and it reports no end of a turn after that.</summary>
    private IRunningProcess? _readerEnded;
    private bool _interruptRequested;
    private string? _lastText;
    private int _controlCounter;
    private bool _disposed;

    private sealed record PendingApproval(string RequestId, JsonElement Input);

    /// <param name="Path">The file a tool that changes files was given.</param>
    /// <param name="Summary">What a tool that is neither a command nor a change to a file was given.</param>
    /// <param name="Where">What such a tool was pointed at.</param>
    private sealed record PendingTool(string Name, string? Command, string? Path, string Summary = "", string? Where = null);

    /// <summary>What came back for a control request. Ended: nothing came back, because the process ended.</summary>
    private sealed record Answer(JsonElement? Payload, string? Error, bool Ended = false);

    /// <summary>What Claude Code says it works with.</summary>
    /// <param name="Effort">What it sends with the next request. Null when its answer does not name a level.</param>
    /// <param name="SettingsSources">The settings files it loaded.</param>
    private sealed record Applied(string? Effort, IReadOnlyList<string> SettingsSources);

    public ClaudeCliSession(ClaudeCliAdapter adapter, SessionRequest request, string? resumeId)
    {
        _adapter = adapter;
        _request = request;
        _resumeExisting = resumeId is not null;
        _sessionExists = resumeId is not null;
        SessionId = resumeId ?? Guid.NewGuid().ToString();
    }

    public string AdapterId => ClaudeCliAdapter.AdapterId;

    public AgentRole Role => _request.Role;

    public string? SessionId { get; private set; }

    public EffectiveSettings? Effective { get; private set; }

    public ChannelReader<AgentEvent> Events => _events.Reader;

    private DateTimeOffset Now => _adapter.Clock.GetUtcNow();

    /// <summary>
    /// Starts Claude Code for the turn and asks it what it will work with. The turn is started afterwards
    /// by whoever asked, when what was said is what was requested. A process that ends before it said it is
    /// not started again for the turn: a second one would be given the prompt without anybody having
    /// compared what it works with. Throws then, with what the process reported.
    /// </summary>
    public async Task<EarlySettings?> PrepareTurnAsync(TurnRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_turnRunning)
            {
                throw new InvalidOperationException("A turn is already running in this session.");
            }
        }

        _askedOf = null;
        await EnsureProcessAsync(request.OutputSchema?.GetRawText()).ConfigureAwait(false);
        Applied? applied;
        AccountSaid? account;
        bool ended;
        try
        {
            (applied, account, ended) = await AskWhatIsInEffectAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (AgentProtocolException)
        {
            // It could not be written to: it ended while it was being asked.
            (applied, account, ended) = (null, null, true);
        }

        if (ended)
        {
            throw new AgentProtocolException(await EndedBeforePromptAsync("before it said what it works with").ConfigureAwait(false));
        }

        _applied = applied;
        _account = account;
        _askedOf = _process;
        return new EarlySettings(
            Model: null,
            Effort: applied?.Effort,
            Source: applied is null ? SettingsNotAnswered : SettingsSource,
            BoundaryProblem: _request.Role == AgentRole.Reviewer ? BoundaryProblem(applied) : null,
            Account: account);
    }

    public async Task StartTurnAsync(TurnRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_turnRunning)
            {
                throw new InvalidOperationException("A turn is already running in this session.");
            }

            _turnRunning = true;
            _interruptRequested = false;
            _lastText = null;
        }

        try
        {
            if (_askedOf is { } asked)
            {
                // Asked for this turn: the prompt is for that process and no other, so it is not started again,
                // whatever happens to it from here on. Another process would not have been asked.
                if (!ReferenceEquals(asked, _process) || asked.HasExited)
                {
                    _askedOf = null;
                    throw new AgentProtocolException(await EndedBeforePromptAsync("after it said what it works with and before it was given the prompt").ConfigureAwait(false));
                }
            }
            else
            {
                await EnsureProcessAsync(request.OutputSchema?.GetRawText()).ConfigureAwait(false);
            }
        }
        catch
        {
            // Nothing was started, so nobody is there who would report the end of this turn.
            lock (_gate)
            {
                _turnRunning = false;
            }

            throw;
        }

        try
        {
            if (_askedOf is null || !ReferenceEquals(_askedOf, _process))
            {
                // Asked before every prompt: what is in effect can differ from what the process was started with.
                (_applied, _account, _) = await AskWhatIsInEffectAsync(cancellationToken).ConfigureAwait(false);
            }

            // What was asked holds for one prompt.
            _askedOf = null;

            await SendAsync(
                writer =>
                {
                    writer.WriteString("type", "user");
                    writer.WritePropertyName("message");
                    writer.WriteStartObject();
                    writer.WriteString("role", "user");
                    writer.WriteString("content", request.Prompt);
                    writer.WriteEndObject();
                    writer.WriteNull("parent_tool_use_id");
                    writer.WriteString("session_id", SessionId);
                    if (_request.Role == AgentRole.Reviewer || request.QuotesOutput)
                    {
                        // What is reviewed is quoted in the prompt, and a repair quotes what was found. Marked like
                        // this, the text is delivered as it is written: Claude Code takes no "@path" in it for a
                        // file to attach and no line for a command. What the user wrote is not marked: with the
                        // mark, Claude Code also leaves out what it attaches to a prompt by itself.
                        writer.WriteBoolean("client_composed", true);
                    }
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AgentProtocolException)
        {
            // The process ended before it took the prompt. Its reader reports why, as the end of this turn. A
            // reader that had ended before the turn began cannot; then it is said here, or the turn would never end.
            bool report;
            lock (_gate)
            {
                report = _turnRunning && _readerEnded is not null && ReferenceEquals(_readerEnded, _process);
                if (report)
                {
                    _turnRunning = false;
                }
            }

            if (report)
            {
                var tail = RedactText(_diagnostics.Read());
                var code = _process?.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
                await _events.PublishAsync(new TurnCompleted(
                    Now, string.Empty, TurnOutcome.Failed, null, null, TurnErrorCodes.AgentExited,
                    $"Claude Code ended (exit code {code}) before it took the prompt." + (tail.Length > 0 ? " It reported: " + tail : string.Empty),
                    [])).ConfigureAwait(false);
            }
        }
        catch
        {
            lock (_gate)
            {
                _turnRunning = false;
            }

            throw;
        }
    }

    /// <summary>
    /// Why no prompt was sent: the process ended, with what it reported. The reader has drained what the
    /// process wrote once it has ended, so it is waited for, a bounded time.
    /// </summary>
    private async Task<string> EndedBeforePromptAsync(string when)
    {
        await _reader.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        var tail = RedactText(_diagnostics.Read());
        var code = _process?.ExitCode?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? "unknown";
        return $"Claude Code ended (exit code {code}) {when}. No prompt was sent."
            + (tail.Length > 0 ? " It reported: " + tail : string.Empty);
    }

    private async Task EnsureProcessAsync(string? schema)
    {
        if (_process is { HasExited: false } && !string.Equals(schema, _processSchema, StringComparison.Ordinal))
        {
            // The schema is an option of the process, so a different schema needs a new process.
            await StopProcessAsync().ConfigureAwait(false);
        }

        if (_process is null || _process.HasExited)
        {
            await StopProcessAsync().ConfigureAwait(false);
            Launch(schema);
        }
    }

    /// <summary>
    /// Why the boundary of a review cannot be confirmed, when that can be said before the review begins.
    /// The tools are named by Claude Code only once it has the prompt; they are looked at then.
    /// </summary>
    private static string? BoundaryProblem(Applied? applied)
    {
        if (applied is null)
        {
            return "Claude Code did not say which settings it loaded, so nothing says that no program of the workspace runs with the review.";
        }

        var planted = applied.SettingsSources.Where(WorkspaceSettings.Contains).ToList();
        return planted.Count == 0
            ? null
            : $"Claude Code loaded settings of the workspace ({string.Join(", ", planted)}), although it was started so that it would not. Settings can carry hooks, which are programs.";
    }

    /// <summary>What Claude Code works with, in its own words: the settings, and the account. Ended: the process ended before it said.</summary>
    private async Task<(Applied? Applied, AccountSaid? Account, bool Ended)> AskWhatIsInEffectAsync(CancellationToken cancellationToken)
    {
        var (applied, ended) = await AskAppliedAsync(cancellationToken).ConfigureAwait(false);
        if (ended)
        {
            return (null, null, true);
        }

        (var account, ended) = await AskAccountAsync(cancellationToken).ConfigureAwait(false);
        return ended ? (null, null, true) : (applied, account, false);
    }

    /// <summary>
    /// The message at the start of a turn does not carry the effort in this mode of Claude Code. Its answer to
    /// "get_settings" does, after everything that can lower it. No model is asked for it.
    /// Nothing is applied when there was no answer, which leaves the effort unreported; ended says that there
    /// was none because the process ended.
    /// </summary>
    private async Task<(Applied? Applied, bool Ended)> AskAppliedAsync(CancellationToken cancellationToken)
    {
        if (_settingsNotAnswered)
        {
            // This process did not say when it was asked. Asking it again would make every turn wait for nothing.
            return (null, false);
        }

        var given = await AskAsync("get_settings", cancellationToken).ConfigureAwait(false);
        if (given.Payload is { } settings && settings.Child("applied") is { ValueKind: JsonValueKind.Object } applied)
        {
            return (new Applied(
                Level(applied),
                settings.Items("sources").Select(source => source.Text("source")).OfType<string>().Where(name => name.Length > 0).ToList()), false);
        }

        if (given.Ended)
        {
            return (null, true);
        }

        await NotAnsweredAsync(given.Error ?? "its answer does not say what is applied").ConfigureAwait(false);
        return (null, false);
    }

    /// <summary>
    /// The account that was shown before the run was read by another process of Claude Code, which was started
    /// in another way: a review, for one, is started without the settings of the user. The conversation says
    /// in its answer to "initialize" what it works with itself. No model is asked for it.
    /// </summary>
    private async Task<(AccountSaid? Account, bool Ended)> AskAccountAsync(CancellationToken cancellationToken)
    {
        if (_accountNotAnswered)
        {
            return (null, false);
        }

        var given = await AskAsync("initialize", cancellationToken).ConfigureAwait(false);
        if (given.Payload?.Child("account") is { ValueKind: JsonValueKind.Object } account)
        {
            return (Account(account), false);
        }

        if (given.Ended)
        {
            return (null, true);
        }

        _accountNotAnswered = true;
        await _events.PublishAsync(new AgentNotice(
            Now,
            $"Claude Code did not say which account the conversation works with ({RedactText(given.Error ?? "its answer names no account")}). "
            + "That it is the route that was shown before the run is not confirmed.",
            IsWarning: true)).ConfigureAwait(false);
        return (null, false);
    }

    /// <summary>
    /// What an answer says about the account. It also names the address and the organization of the user;
    /// neither is read.
    /// </summary>
    private AccountSaid Account(JsonElement account)
    {
        static string? Named(string? text) => text is { Length: > 0 } and not "none" ? text : null;

        // The order is the one in which the route that is shown before a run is read from the sign-in.
        var (route, description) = (Named(account.Text("apiProvider")), Named(account.Text("apiKeySource")), Named(account.Text("subscriptionType"))) switch
        {
            ("gateway", _, _) => (AccountRouteKind.Gateway, "gateway"),
            ({ } provider, _, _) when provider != "firstParty" => (AccountRouteKind.CloudProvider, $"cloud provider ({provider})"),
            (_, { } key, _) => (AccountRouteKind.ApiKey, $"API key ({key})"),
            (_, _, { } plan) => (AccountRouteKind.Subscription, $"subscription ({plan})"),
            _ => ((AccountRouteKind?)null, Named(account.Text("tokenSource")) is { } token ? $"token ({token})" : "an account it says nothing more about"),
        };

        return new AccountSaid(
            route,
            RedactText(description),
            AccountSource,
            _request.Role == AgentRole.Reviewer
                ? "Claude Code runs a review without the settings of the user, so a key or a provider that is set there is not what the review works with."
                : null);
    }

    /// <summary>
    /// Asks Claude Code something it answers without asking a model. No answer in the time the process may
    /// take to start is an answer that says so.
    /// </summary>
    private async Task<Answer> AskAsync(string subtype, CancellationToken cancellationToken)
    {
        var requestId = "yav-" + Interlocked.Increment(ref _controlCounter);
        var answer = new TaskCompletionSource<Answer>(TaskCreationOptions.RunContinuationsAsynchronously);
        _answers[requestId] = answer;
        var patience = _adapter.Options.EffectiveStartupTimeout;
        try
        {
            await SendAsync(
                writer =>
                {
                    writer.WriteString("type", "control_request");
                    writer.WriteString("request_id", requestId);
                    writer.WritePropertyName("request");
                    writer.WriteStartObject();
                    writer.WriteString("subtype", subtype);
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);

            // The first answer of a process takes as long as the process takes to start. Real time: a process is waited for.
            return await answer.Task.WaitAsync(patience, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return new Answer(null, string.Create(System.Globalization.CultureInfo.InvariantCulture, $"no answer within {patience.TotalSeconds:0} seconds"));
        }
        finally
        {
            _answers.TryRemove(requestId, out _);
        }
    }

    /// <summary>The level in an answer. Only an explicit null says that none is sent; anything else that is not a name is not read as one.</summary>
    private static string? Level(JsonElement applied) =>
        !applied.TryGetProperty("effort", out var effort) ? null
        : effort.ValueKind switch
        {
            JsonValueKind.String => effort.GetString(),
            JsonValueKind.Null => NoEffort,
            _ => null,
        };

    private async Task NotAnsweredAsync(string reason)
    {
        _settingsNotAnswered = true;
        await _events.PublishAsync(new AgentNotice(
            Now,
            $"Claude Code did not say which effort is in effect and which settings it loaded ({RedactText(reason)}). Neither is confirmed. "
            + "A version that answers the request get_settings is needed for that.",
            IsWarning: true)).ConfigureAwait(false);
    }

    private void Launch(string? schema)
    {
        var executable = _adapter.ResolveExecutable() ?? throw new AgentProtocolException("Claude Code was not found. Run /doctor for details.");
        var batch = ExecutableResolver.IsBatchFile(executable);
        var reviewer = _request.Role == AgentRole.Reviewer;

        var directory = _adapter.Options.DiagnosticsDirectory ?? Path.Combine(Path.GetTempPath(), "yav-shell");
        Directory.CreateDirectory(directory);
        _instructionsFile ??= Path.Combine(directory, "instructions-" + Guid.NewGuid().ToString("N")[..12] + ".txt");
        File.WriteAllText(_instructionsFile, _request.RoleInstructions, new UTF8Encoding(false));

        var arguments = new List<string>
        {
            "-p",
            "--output-format", "stream-json",
            "--input-format", "stream-json",
            "--verbose",
            "--model", _request.ModelId,

            // Appended: Claude Code's own prompt, tool guidance and safety instructions stay in place.
            "--append-system-prompt-file", _instructionsFile,

            // Settings, hooks and MCP servers that the repository controls are loaded only for a trusted project,
            // and never for a review: what is in the workspace then is what Model A left there.
            "--setting-sources", _request.ProjectTrusted && !reviewer ? "user,project,local" : "user",
        };

        if (_request.Effort.Length > 0)
        {
            arguments.AddRange(["--effort", _request.Effort]);
        }

        if (reviewer)
        {
            // The boundary is what exists, not what is asked for: no tool that writes or runs anything is available.
            arguments.Add("--restricted");
            arguments.AddRange(["--tools", "Read,Glob,Grep"]);
            arguments.AddRange(["--disallowedTools", "mcp__*"]);
            arguments.Add("--strict-mcp-config");
            arguments.AddRange(["--permission-mode", "dontAsk"]);
            arguments.AddRange(["--permission-prompts", "none"]);
        }
        else
        {
            arguments.AddRange(["--permission-mode", "acceptEdits"]);

            // Its questions to the user are shown by Claude Code in a window of its own, which is not there, and would
            // only be declined. Without the tool, a question is part of the answer, which YAV shows.
            arguments.AddRange(["--disallowedTools", "AskUserQuestion"]);
            if (_request.Approvals == ApprovalMode.AskUser)
            {
                arguments.AddRange(["--permission-prompt-tool", "stdio"]);
            }
            else
            {
                arguments.AddRange(["--permission-prompts", "none"]);
            }

            if (!_request.ProjectTrusted)
            {
                arguments.Add("--strict-mcp-config");
            }
        }

        foreach (var readable in _request.AdditionalReadableDirectories)
        {
            arguments.AddRange(["--add-dir", readable]);
        }

        if (_sessionExists)
        {
            arguments.AddRange(["--resume", SessionId!]);
        }
        else
        {
            arguments.AddRange(["--session-id", SessionId!]);
        }

        if (!batch)
        {
            if (schema is not null)
            {
                arguments.AddRange(["--json-schema", schema]);
            }

            if (_request.ServiceTier == "fast")
            {
                arguments.AddRange(["--settings", "{\"fastMode\":true}"]);
            }
        }

        _secret = _adapter.ApiKey();
        _diagnostics = new DiagnosticTail();
        _processSchema = schema;
        _settingsNotAnswered = false;
        _accountNotAnswered = false;
        var process = _adapter.Runner.Start(new ProcessSpec(
            executable, arguments, _request.WorkingDirectory, _adapter.BuildEnvironment(), Label: "claude -p"));
        _process = process;
        var diagnostics = _diagnostics;
        var errors = ClaudeCliAdapter.DrainAsync(process.StandardError, diagnostics);
        _reader = Task.Run(() => ReadLoopAsync(process, errors, diagnostics));

        if (batch && (schema is not null || _request.ServiceTier is not null))
        {
            _events.TryPublish(new AgentNotice(
                Now,
                "Claude Code is installed as a batch launcher, which cannot carry JSON options safely. The structured-output schema and "
                + "provider speed were not passed; the review result is still validated by YAV.",
                IsWarning: true));
        }
    }

    private async Task ReadLoopAsync(IRunningProcess process, Task errors, DiagnosticTail diagnostics)
    {
        try
        {
            var reader = new JsonLineReader(process.StandardOutput);
            while (await reader.ReadFrameAsync(CancellationToken.None).ConfigureAwait(false) is { } frame)
            {
                await DispatchAsync(Redact(frame)).ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or FrameTooLargeException)
        {
            diagnostics.Append(ex.Message);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            // Whatever it was: what follows has to happen, or whoever waits for the end of the turn waits for good.
            diagnostics.Append($"{ex.GetType().Name}: {ex.Message}");
        }

        int? exitCode = null;
        try
        {
            exitCode = await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
            await errors.WaitAsync(TimeSpan.FromSeconds(2)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        bool running;
        bool interrupted;
        lock (_gate)
        {
            running = _turnRunning;
            interrupted = _interruptRequested;
            _turnRunning = false;
            _readerEnded = process;
        }

        foreach (var approvalId in _approvals.Keys.ToList())
        {
            if (_approvals.TryRemove(approvalId, out _))
            {
                await _events.PublishAsync(new ApprovalWithdrawn(Now, approvalId)).ConfigureAwait(false);
            }
        }

        // Nobody is left to answer what was asked.
        foreach (var answer in _answers.Values)
        {
            answer.TrySetResult(new Answer(null, null, Ended: true));
        }

        if (running)
        {
            var tail = RedactText(diagnostics.Read());
            var message = $"Claude Code ended (exit code {exitCode?.ToString() ?? "unknown"}) before the turn was finished."
                + (tail.Length > 0 ? " It reported: " + tail : string.Empty);
            await _events.PublishAsync(new TurnCompleted(
                Now, string.Empty, interrupted ? TurnOutcome.Interrupted : TurnOutcome.Failed, _lastText, null,
                interrupted ? null : TurnErrorCodes.AgentExited, interrupted ? null : message, [])).ConfigureAwait(false);
        }
    }

    private ReadOnlyMemory<byte> Redact(ReadOnlyMemory<byte> frame)
    {
        if (_secret is null)
        {
            return frame;
        }

        var secret = Encoding.UTF8.GetBytes(_secret);
        if (frame.Span.IndexOf(secret) < 0)
        {
            return frame;
        }

        // The key is removed before the line is parsed, so it cannot reach any event, log or screen.
        return Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(frame.Span).Replace(_secret, Redacted, StringComparison.Ordinal));
    }

    private string RedactText(string text) => _secret is null ? text : text.Replace(_secret, Redacted, StringComparison.Ordinal);

    private async ValueTask DispatchAsync(ReadOnlyMemory<byte> frame)
    {
        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(frame);
        }
        catch (JsonException)
        {
            await _events.PublishAsync(new AgentNotice(Now, "The agent sent a line that is not valid JSON; it was skipped.", true)).ConfigureAwait(false);
            return;
        }

        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return;
            }

            switch (root.Text("type"))
            {
                case "system":
                    await HandleSystemAsync(root).ConfigureAwait(false);
                    break;

                case "assistant":
                    if (root.Child("parent_tool_use_id") is null && root.Child("message") is { } assistant)
                    {
                        await HandleAssistantAsync(assistant).ConfigureAwait(false);
                    }

                    break;

                case "user":
                    if (root.Child("parent_tool_use_id") is null && root.Child("message") is { } user)
                    {
                        await HandleToolResultsAsync(user).ConfigureAwait(false);
                    }

                    break;

                case "result":
                    await HandleResultAsync(root).ConfigureAwait(false);
                    break;

                case "rate_limit_event":
                    if (root.Child("rate_limit_info") is { } limit)
                    {
                        await _events.PublishAsync(new RateLimitUpdated(Now, ParseRateLimit(limit))).ConfigureAwait(false);
                    }

                    break;

                case "control_request":
                    await HandleControlRequestAsync(root).ConfigureAwait(false);
                    break;

                case "control_response":
                    if (root.Child("response") is { } response && response.Text("request_id") is { } answered
                        && _answers.TryGetValue(answered, out var waiting))
                    {
                        waiting.TrySetResult(response.Text("subtype") == "success"
                            ? new Answer(response.Child("response")?.Clone(), null)
                            : new Answer(null, response.Text("error") ?? "the request was refused"));
                    }

                    break;

                case "control_cancel_request":
                {
                    var requestId = root.Text("request_id");
                    foreach (var (approvalId, pending) in _approvals)
                    {
                        if (pending.RequestId == requestId && _approvals.TryRemove(approvalId, out _))
                        {
                            await _events.PublishAsync(new ApprovalWithdrawn(Now, approvalId)).ConfigureAwait(false);
                        }
                    }

                    break;
                }

                // keep_alive and message kinds this version does not know are ignored.
            }
        }
    }

    private async ValueTask HandleSystemAsync(JsonElement message)
    {
        switch (message.Text("subtype"))
        {
            case "init":
            {
                _sessionExists = true;
                SessionId = message.Text("session_id") ?? SessionId;
                var tools = message.Items("tools").Select(t => t.GetString() ?? string.Empty).Where(t => t.Length > 0).ToList();
                var applied = _applied;
                var problem = _request.Role == AgentRole.Reviewer ? BoundaryProblem(applied) : null;

                // No tool that changes anything. For a review also: no settings of the workspace, and that has to
                // have been said. What Claude Code did not say is not confirmed.
                var readOnly = tools.Count > 0 && tools.All(ReadOnlyTools.Contains) && problem is null;
                if (problem is not null && applied is not null)
                {
                    await _events.PublishAsync(new AgentNotice(Now, problem + " The read-only boundary is not confirmed.", IsWarning: true)).ConfigureAwait(false);
                }
                Effective = new EffectiveSettings(
                    Model: message.Text("model"),
                    Effort: applied?.Effort,
                    // Claude Code applies no operating-system sandbox here; what it can do follows from the tools it has.
                    Sandbox: readOnly ? "read-only" : null,
                    ApprovalPolicy: message.Text("permissionMode"),
                    ServiceTier: message.Text("fast_mode_state") == "on" ? "fast" : null,
                    WorkingDirectory: message.Text("cwd"),
                    CredentialSource: message.Text("apiKeySource"),
                    AgentVersion: message.Text("claude_code_version"),
                    InstructionSources: [],
                    Tools: tools,
                    Source: "init message",
                    EffortSource: applied is null ? SettingsNotAnswered : SettingsSource,
                    Account: _account);
                await _events.PublishAsync(new SessionConfigured(Now, SessionId ?? string.Empty, Effective)).ConfigureAwait(false);
                await _events.PublishAsync(new TurnStarted(Now, string.Empty)).ConfigureAwait(false);
                break;
            }

            case "api_retry":
                await _events.PublishAsync(new ProviderRetry(
                    Now, (int)(message.Number("attempt") ?? 0), (int?)message.Number("max_retries"), message.Number("retry_delay_ms"),
                    message.Text("error") ?? "unknown")).ConfigureAwait(false);
                break;

            case "notification":
                if (message.Text("text") is { Length: > 0 } text)
                {
                    await _events.PublishAsync(new AgentNotice(Now, text, IsWarning: message.Text("priority") is "high" or "immediate")).ConfigureAwait(false);
                }

                break;

            case "permission_denied":
                await _events.PublishAsync(new AgentNotice(
                    Now, $"The tool {message.Text("tool_name")} was denied: {message.Text("message") ?? message.Text("decision_reason") ?? "not permitted"}",
                    IsWarning: true)).ConfigureAwait(false);
                break;

            case "status":
                if (message.Text("status") == "compacting")
                {
                    await _events.PublishAsync(new AgentNotice(Now, "The agent is compacting its context.", IsWarning: false)).ConfigureAwait(false);
                }

                break;
        }
    }

    private async ValueTask HandleAssistantAsync(JsonElement message)
    {
        var messageId = message.Text("id") ?? string.Empty;
        foreach (var block in message.Items("content"))
        {
            switch (block.Text("type"))
            {
                case "text":
                {
                    var text = block.Text("text") ?? string.Empty;
                    if (text.Length > 0)
                    {
                        lock (_gate)
                        {
                            _lastText = text;
                        }

                        await _events.PublishAsync(new AssistantMessage(Now, messageId, text, MessagePhase.Unknown)).ConfigureAwait(false);
                    }

                    break;
                }

                case "tool_use":
                {
                    var id = block.Text("id") ?? string.Empty;
                    var name = block.Text("name") ?? "tool";
                    var input = block.Child("input");
                    if (CommandTools.Contains(name))
                    {
                        var command = input?.Text("command") ?? string.Empty;
                        _tools[id] = new PendingTool(name, command, null);
                        await _events.PublishAsync(new CommandStarted(Now, id, command, _request.WorkingDirectory)).ConfigureAwait(false);
                    }
                    else if (FileTools.Contains(name))
                    {
                        _tools[id] = new PendingTool(name, null, input?.Text("file_path") ?? input?.Text("notebook_path"));
                    }
                    else if (name != ResultTool)
                    {
                        var (what, where) = Given(input);
                        _tools[id] = new PendingTool(name, null, null, what, where);
                        await _events.PublishAsync(new ToolActivity(Now, id, name, what, "started", where)).ConfigureAwait(false);
                    }

                    // The tool a structured result is handed back with does no work. The result is part of how the turn ends.
                    break;
                }

                // "thinking" blocks are the model's reasoning. They are neither shown nor stored.
            }
        }
    }

    /// <summary>
    /// What a tool was given: what it looks for, and what it was pointed at. The path is passed on as the agent
    /// gave it; it is shortened for display by who knows the workspace.
    /// </summary>
    private static (string What, string? Where) Given(JsonElement? input)
    {
        if (input is not { ValueKind: JsonValueKind.Object } value)
        {
            return (string.Empty, null);
        }

        var what = new[] { "pattern", "query", "url", "description" }.Select(name => value.Text(name)).FirstOrDefault(text => !string.IsNullOrEmpty(text));
        var where = new[] { "file_path", "notebook_path", "path" }.Select(name => value.Text(name)).FirstOrDefault(text => !string.IsNullOrEmpty(text));
        return (what ?? string.Empty, where);
    }

    private static string Describe(JsonElement? input)
    {
        if (input is not { ValueKind: JsonValueKind.Object } value)
        {
            return string.Empty;
        }

        foreach (var name in new[] { "file_path", "path", "pattern", "query", "url", "description" })
        {
            if (value.Text(name) is { Length: > 0 } text)
            {
                return text;
            }
        }

        return string.Empty;
    }

    private async ValueTask HandleToolResultsAsync(JsonElement message)
    {
        foreach (var block in message.Items("content"))
        {
            if (block.Text("type") != "tool_result" || block.Text("tool_use_id") is not { } id || !_tools.TryRemove(id, out var tool))
            {
                continue;
            }

            var failed = block.Flag("is_error") == true;
            var text = ReadContent(block);
            if (tool.Command is not null)
            {
                await _events.PublishAsync(new CommandCompleted(Now, id, tool.Command, null, null, failed ? "failed" : "completed", text)).ConfigureAwait(false);
            }
            else if (tool.Path is not null)
            {
                await _events.PublishAsync(new FilesChanged(
                    Now, id, [new FileChange(tool.Path, FileChangeKind.Update, null)], failed ? "failed" : "completed")).ConfigureAwait(false);
            }
            else
            {
                await _events.PublishAsync(new ToolActivity(Now, id, tool.Name, tool.Summary, failed ? "failed" : "completed", tool.Where)).ConfigureAwait(false);
            }
        }
    }

    private static string ReadContent(JsonElement block)
    {
        if (block.Child("content") is not { } content)
        {
            return string.Empty;
        }

        if (content.ValueKind == JsonValueKind.String)
        {
            return content.GetString() ?? string.Empty;
        }

        if (content.ValueKind == JsonValueKind.Array)
        {
            return string.Join("\n", content.EnumerateArray().Select(part => part.Text("text")).Where(t => !string.IsNullOrEmpty(t)));
        }

        return string.Empty;
    }

    private async ValueTask HandleResultAsync(JsonElement result)
    {
        bool interrupted;
        string? lastText;
        lock (_gate)
        {
            if (!_turnRunning)
            {
                return;
            }

            _turnRunning = false;
            interrupted = _interruptRequested;
            lastText = _lastText;
        }

        _tools.Clear();
        await PublishUsageAsync(result).ConfigureAwait(false);

        var subtype = result.Text("subtype") ?? "success";
        var isError = result.Flag("is_error") == true;
        var text = result.Text("result");
        var errors = string.Join("; ", result.Items("errors").Select(e => e.GetString()).Where(e => !string.IsNullOrEmpty(e)));
        var status = result.Number("api_error_status");
        var reason = result.Text("terminal_reason");

        TurnOutcome outcome;
        string? message = null;
        if (subtype == "success" && !isError)
        {
            outcome = TurnOutcome.Completed;
        }
        else if (interrupted || reason is "aborted_streaming" or "aborted_tools")
        {
            outcome = TurnOutcome.Interrupted;
        }
        else
        {
            message = subtype == "success" ? text : errors.Length > 0 ? errors : subtype;
            outcome = Classify(subtype, status, message ?? string.Empty);
        }

        var denials = result.Items("permission_denials")
            .Select(d => new PermissionDenial(d.Text("tool_name") ?? "tool", Describe(d.Child("tool_input"))))
            .ToList();

        await _events.PublishAsync(new TurnCompleted(
            Now,
            string.Empty,
            outcome,
            outcome == TurnOutcome.Completed ? text ?? lastText : lastText,
            outcome == TurnOutcome.Completed && result.Child("structured_output") is { ValueKind: JsonValueKind.Object } structured ? structured.Clone() : null,
            outcome == TurnOutcome.Completed ? null : subtype,
            message,
            denials)).ConfigureAwait(false);
    }

    private static TurnOutcome Classify(string subtype, long? status, string message)
    {
        if (subtype is "error_max_budget_usd")
        {
            return TurnOutcome.UsageLimitReached;
        }

        // "You've hit your session limit", "... weekly limit", "... Opus limit" is how Claude Code words a limit of the account.
        if (message.Contains("usage limit", StringComparison.OrdinalIgnoreCase) || message.Contains("credit", StringComparison.OrdinalIgnoreCase)
            || (message.Contains("hit your", StringComparison.OrdinalIgnoreCase) && message.Contains("limit", StringComparison.OrdinalIgnoreCase)))
        {
            return TurnOutcome.UsageLimitReached;
        }

        if (status is 429 or 529
            || message.Contains("429", StringComparison.Ordinal)
            || message.Contains("rate limit", StringComparison.OrdinalIgnoreCase)
            || message.Contains("overloaded", StringComparison.OrdinalIgnoreCase))
        {
            return TurnOutcome.RateLimited;
        }

        return TurnOutcome.Failed;
    }

    private async ValueTask PublishUsageAsync(JsonElement result)
    {
        if (result.Child("modelUsage") is not { ValueKind: JsonValueKind.Object } usage)
        {
            return;
        }

        // Claude counts cache reads and cache writes apart from input; thinking tokens are inside the output count.
        long? input = null, cacheRead = null, cacheWrite = null, output = null, thinking = null, window = null;
        string? model = null;
        foreach (var entry in usage.EnumerateObject())
        {
            model ??= entry.Name;
            input = Add(input, entry.Value.Number("inputTokens"));
            cacheRead = Add(cacheRead, entry.Value.Number("cacheReadInputTokens"));
            cacheWrite = Add(cacheWrite, entry.Value.Number("cacheCreationInputTokens"));
            output = Add(output, entry.Value.Number("outputTokens"));
            thinking = Add(thinking, entry.Value.Number("thinkingTokens"));
            window ??= entry.Value.Number("contextWindow");
        }

        var cost = result.Money("total_cost_usd");
        await _events.PublishAsync(new UsageUpdated(Now, new UsageSnapshot(
            AdapterId, SessionId, model, UsageScope.CumulativeForSession,
            TokenCounts.FromExclusiveCounters(input, cacheRead, cacheWrite, output, thinking),
            null,
            cost,
            // Claude Code calls this figure an estimate, not a billing statement.
            cost is null ? ValueProvenance.Unavailable : ValueProvenance.Estimated,
            window,
            "result message",
            Now))).ConfigureAwait(false);

        static long? Add(long? sum, long? value) => value is null ? sum : (sum ?? 0) + value.Value;
    }

    private RateLimitSnapshot ParseRateLimit(JsonElement info)
    {
        var resets = info.Number("resetsAt");
        // A fraction of the window. It is above 1 when more than the window allows was used, which happens.
        var utilization = info.Child("utilization") is { ValueKind: JsonValueKind.Number } value && value.TryGetDouble(out var fraction)
            ? (int?)Math.Round(fraction * 100)
            : null;
        var window = info.Text("rateLimitType") switch
        {
            "five_hour" => (int?)300,
            var type when type is not null && type.StartsWith("seven_day", StringComparison.Ordinal) => 10_080,
            _ => null,
        };
        return new RateLimitSnapshot(
            AdapterId, info.Text("rateLimitType"), null,
            new RateLimitWindow(utilization, window, resets is null ? null : DateTimeOffset.FromUnixTimeSeconds(resets.Value)),
            null, null, null, null,
            info.Text("status") == "rejected" ? true : null,
            "rate_limit_event", Now);
    }

    private async ValueTask HandleControlRequestAsync(JsonElement message)
    {
        var requestId = message.Text("request_id") ?? string.Empty;
        var request = message.Child("request");
        if (request?.Text("subtype") != "can_use_tool")
        {
            await RespondErrorAsync(requestId, "YAV Shell does not handle this request.").ConfigureAwait(false);
            return;
        }

        var tool = request.Value.Text("tool_name") ?? "tool";
        var input = request.Value.Child("input")?.Clone() ?? default;
        if (_request.Approvals == ApprovalMode.NeverAsk)
        {
            await RespondDenyAsync(requestId, "This role is never given additional access.", interrupt: false).ConfigureAwait(false);
            return;
        }

        if (request.Value.Flag("requires_user_interaction") == true)
        {
            // What the tool asks of the user is shown by Claude Code in a window of its own, which is not there.
            // Allowing it would answer something nobody saw.
            await _events.PublishAsync(new AgentNotice(
                Now,
                $"The agent wanted to use {request.Value.Text("display_name") ?? tool}, which asks the user something in a way YAV cannot show. It was declined.",
                IsWarning: true)).ConfigureAwait(false);
            await RespondDenyAsync(requestId, "This needs an answer of the user that cannot be given here. Go on without it.", interrupt: false).ConfigureAwait(false);
            return;
        }

        // Named by the request and not by the tool use: a request that was taken back and asked again for the
        // same use of a tool is another request, and an answer to the first must not answer it.
        var approvalId = requestId.Length > 0 ? requestId : "claude-" + Guid.NewGuid().ToString("N");
        _approvals[approvalId] = new PendingApproval(requestId, input);
        var command = CommandTools.Contains(tool) ? input.Text("command") : null;
        var details = new List<string>();
        if (request.Value.Text("blocked_path") is { } blocked)
        {
            details.Add("Path: " + blocked);
        }

        var (given, complete) = Listed(input, command is null ? null : "command");
        details.AddRange(given);

        await _events.PublishAsync(new ApprovalRequested(
            Now,
            new ApprovalRequest(
                approvalId,
                CommandTools.Contains(tool) ? ApprovalKind.CommandExecution : FileTools.Contains(tool) ? ApprovalKind.FileChange : ApprovalKind.ToolUse,
                request.Value.Text("title") ?? $"Use {request.Value.Text("display_name") ?? tool}",
                command,
                _request.WorkingDirectory,
                request.Value.Text("decision_reason"),
                details,
                // A lasting allow rule would be wider than the single action that was asked about.
                CanAcceptForSession: false,

                // What could not be listed completely is not something to allow in passing either.
                Deliberate: request.Value.Flag("default_to_no") == true || !complete))).ConfigureAwait(false);
    }

    /// <summary>
    /// Everything a tool is given: a line for each field, and a line for each line of a text. All of it is
    /// what is decided about; what a tool sends away is in a field nobody would think of looking for.
    /// </summary>
    /// <param name="shownAsCommand">The field that is shown as the command of a shell, and therefore not listed again.</param>
    /// <returns>The lines, and whether they are all of it.</returns>
    private static (List<string> Lines, bool Complete) Listed(JsonElement input, string? shownAsCommand)
    {
        var lines = new List<string>();
        if (input.ValueKind != JsonValueKind.Object)
        {
            return (lines, true);
        }

        foreach (var field in input.EnumerateObject())
        {
            if (field.Name == shownAsCommand)
            {
                continue;
            }

            // A shell is given a description of its command by the agent. It is not what the command does.
            var name = shownAsCommand is not null && field.Name == "description" ? "description, in the words of the agent" : field.Name;
            if (field.Value.ValueKind != JsonValueKind.String)
            {
                lines.Add($"{name}: {Compact(field.Value)}");
                continue;
            }

            var text = (field.Value.GetString() ?? string.Empty).ReplaceLineEndings("\n").Split('\n');
            if (text.Length == 1)
            {
                lines.Add($"{name}: {text[0]}");
                continue;
            }

            lines.Add(name + ":");
            lines.AddRange(text.Select(line => "  " + line));
        }

        var complete = true;
        for (var i = 0; i < lines.Count; i++)
        {
            if (lines[i].Length > ListedCharacters)
            {
                complete = false;
                lines[i] = string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"{lines[i][..ListedCharacters]} ({lines[i].Length - ListedCharacters} more characters are not shown)");
            }
        }

        if (lines.Count > ListedLines)
        {
            complete = false;
            var more = lines.Count - ListedLines;
            lines.RemoveRange(ListedLines, more);
            lines.Add(string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{more} more lines of what the tool is given are not listed."));
        }

        return (lines, complete);
    }

    /// <summary>A value that is not a text, written on one line. Characters that cannot be seen are written as JSON writes them.</summary>
    private static string Compact(JsonElement value)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            value.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(buffer.GetBuffer(), 0, (int)buffer.Length);
    }

    public async Task RespondToApprovalAsync(string approvalId, ApprovalDecision decision, CancellationToken cancellationToken)
    {
        if (!_approvals.TryRemove(approvalId, out var pending))
        {
            throw new InvalidOperationException($"There is no open approval request '{approvalId}'.");
        }

        if (decision is ApprovalDecision.Accept or ApprovalDecision.AcceptForSession)
        {
            await SendAsync(
                writer =>
                {
                    writer.WriteString("type", "control_response");
                    writer.WritePropertyName("response");
                    writer.WriteStartObject();
                    writer.WriteString("subtype", "success");
                    writer.WriteString("request_id", pending.RequestId);
                    writer.WritePropertyName("response");
                    writer.WriteStartObject();
                    writer.WriteString("behavior", "allow");
                    writer.WritePropertyName("updatedInput");
                    if (pending.Input.ValueKind == JsonValueKind.Object)
                    {
                        pending.Input.WriteTo(writer);
                    }
                    else
                    {
                        writer.WriteStartObject();
                        writer.WriteEndObject();
                    }

                    writer.WriteEndObject();
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        if (decision == ApprovalDecision.Cancel)
        {
            lock (_gate)
            {
                _interruptRequested = true;
            }
        }

        await RespondDenyAsync(pending.RequestId, "The user declined this action.", decision == ApprovalDecision.Cancel).ConfigureAwait(false);
    }

    private Task RespondDenyAsync(string requestId, string message, bool interrupt) => SendAsync(
        writer =>
        {
            writer.WriteString("type", "control_response");
            writer.WritePropertyName("response");
            writer.WriteStartObject();
            writer.WriteString("subtype", "success");
            writer.WriteString("request_id", requestId);
            writer.WritePropertyName("response");
            writer.WriteStartObject();
            writer.WriteString("behavior", "deny");
            writer.WriteString("message", message);
            writer.WriteBoolean("interrupt", interrupt);
            writer.WriteEndObject();
            writer.WriteEndObject();
        },
        CancellationToken.None);

    private Task RespondErrorAsync(string requestId, string message) => SendAsync(
        writer =>
        {
            writer.WriteString("type", "control_response");
            writer.WritePropertyName("response");
            writer.WriteStartObject();
            writer.WriteString("subtype", "error");
            writer.WriteString("request_id", requestId);
            writer.WriteString("error", message);
            writer.WriteEndObject();
        },
        CancellationToken.None);

    public async Task InterruptAsync(CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (!_turnRunning || _process is null || _process.HasExited)
            {
                return;
            }

            _interruptRequested = true;
        }

        try
        {
            await SendAsync(
                writer =>
                {
                    writer.WriteString("type", "control_request");
                    writer.WriteString("request_id", "yav-" + Interlocked.Increment(ref _controlCounter));
                    writer.WritePropertyName("request");
                    writer.WriteStartObject();
                    writer.WriteString("subtype", "interrupt");
                    writer.WriteEndObject();
                },
                cancellationToken).ConfigureAwait(false);
        }
        catch (AgentProtocolException)
        {
            // The process is gone, so nothing is running.
        }
    }

    public Task<bool> SteerAsync(string text, CancellationToken cancellationToken) => Task.FromResult(false);

    public async Task ShutdownAsync(TimeSpan grace, CancellationToken cancellationToken)
    {
        await InterruptAsync(cancellationToken).ConfigureAwait(false);
        var started = _adapter.Clock.GetTimestamp();
        while (_adapter.Clock.GetElapsedTime(started) < grace)
        {
            lock (_gate)
            {
                if (!_turnRunning)
                {
                    break;
                }
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        await StopProcessAsync().ConfigureAwait(false);
    }

    private async Task SendAsync(Action<Utf8JsonWriter> write, CancellationToken cancellationToken)
    {
        var process = _process ?? throw new AgentProtocolException("Claude Code is not running.");
        var buffer = new ArrayBufferWriter<byte>(512);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            write(writer);
            writer.WriteEndObject();
        }

        buffer.Write("\n"u8);
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await process.StandardInput.WriteAsync(buffer.WrittenMemory, cancellationToken).ConfigureAwait(false);
            await process.StandardInput.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException or NotSupportedException)
        {
            throw new AgentProtocolException("Claude Code is no longer reading input.", inner: ex);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private async Task StopProcessAsync()
    {
        var process = _process;
        _process = null;
        if (process is null)
        {
            return;
        }

        // Closing the input ends the session; the process tree is ended when that does not happen in time.
        await process.ShutdownAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        try
        {
            await _reader.WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
        }

        await process.DisposeAsync().ConfigureAwait(false);
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

        await StopProcessAsync().ConfigureAwait(false);
        if (_instructionsFile is not null)
        {
            try
            {
                File.Delete(_instructionsFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
            }
        }

        _events.Complete();
        _writeGate.Dispose();
    }
}
