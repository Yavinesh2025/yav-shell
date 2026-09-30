using System.Collections.Concurrent;
using System.Text.Json;
using System.Threading.Channels;
using Yav.Adapters.Protocol;
using Yav.Core;
using Yav.Core.Agents;

namespace Yav.Adapters;

/// <summary>One Codex thread. Events are translated from the app server's notifications as they arrive.</summary>
internal sealed class CodexAppServerSession : IAgentSession
{
    /// <summary>How many files an approval request names before it only counts the rest.</summary>
    private const int NamedFiles = 20;

    private readonly CodexAppServerAdapter _adapter;
    private readonly CodexServer _server;
    private readonly JsonRpcConnection _connection;
    private readonly SessionRequest _request;

    // The warnings for all conversations this one was given, by their number, so that each is given once.
    private readonly HashSet<long> _warned = [];
    private readonly SessionEvents _events = new();
    private readonly ConcurrentDictionary<string, PendingApproval> _approvals = new(StringComparer.Ordinal);

    // The files of each change Codex announced in the running turn, by item. A request to approve a change
    // names only the item, so this is where the user learns what would be changed.
    private readonly ConcurrentDictionary<string, IReadOnlyList<AnnouncedChange>> _announcedChanges = new(StringComparer.Ordinal);

    // The sub-agents the model started in this conversation, with their nicknames, so that each is announced once.
    private readonly ConcurrentDictionary<string, string?> _subAgents = new(StringComparer.Ordinal);

    // The sub-agents whose requests for approval were refused, so that the user is told once for each.
    private readonly ConcurrentDictionary<string, byte> _subAgentsAsked = new(StringComparer.Ordinal);
    private readonly Lock _gate = new();
    private EffectiveSettings _effective;
    private string? _activeTurn;
    private bool _turnRequested;
    private bool _structuredRequested;
    private string? _finalAnswer;
    private string? _lastUnphased;
    private string? _lastMessage;
    private bool _closed;

    // Why the conversation is not used any more: Codex reported that someone other than the user decides its
    // requests for access, or what it reported leaves that or the limits of its sandbox unknown. The turn that
    // ran then is stopped once.
    private string? _refusal;
    private bool _refusedTurnStopped;

    // The turn Codex was asked to stop. It is asked once, whoever asks: the user, the run, or a refusal.
    private string? _interruptedTurn;

    // The number under which the process noted the account this conversation reports, so that an older report
    // never replaces a newer one.
    private long _accountVersion;

    private sealed record PendingApproval(string RawId, ApprovalKind Kind, JsonElement? RequestedPermissions, string Method);

    /// <param name="Kind">What Codex calls the change. Null when it does not say.</param>
    private sealed record AnnouncedChange(string Path, string? Kind, string? MovedTo);

    /// <param name="accountVersion">The number under which the process noted the account that <paramref name="effective"/> names.</param>
    public CodexAppServerSession(
        CodexAppServerAdapter adapter, CodexServer server, SessionRequest request, string threadId, EffectiveSettings effective, long accountVersion)
    {
        _adapter = adapter;
        _server = server;
        _connection = server.Connection;
        _request = request;
        SessionId = threadId;
        _effective = effective;
        _accountVersion = accountVersion;
    }

    /// <summary>The number under which the process noted the account the conversation reports now.</summary>
    internal long AccountVersion
    {
        get
        {
            lock (_gate)
            {
                return _accountVersion;
            }
        }
    }

    public string AdapterId => CodexAppServerAdapter.AdapterId;

    public AgentRole Role => _request.Role;

    public string? SessionId { get; }

    /// <summary>What Codex reported last as in effect: when the conversation was opened, or when its settings changed.</summary>
    public EffectiveSettings? Effective => Volatile.Read(ref _effective);

    public ChannelReader<AgentEvent> Events => _events.Reader;

    private DateTimeOffset Now => _adapter.Clock.GetUtcNow();

    private bool Closed
    {
        get
        {
            lock (_gate)
            {
                return _closed;
            }
        }
    }

    internal async ValueTask PublishAsync(AgentEvent agentEvent)
    {
        if (!Closed)
        {
            await SendAsync(agentEvent).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A warning of Codex that named no conversation. It can reach a conversation twice while the conversation is
    /// registered, as the one who registers it and the reader both hand it over; it is given once.
    /// </summary>
    internal async ValueTask WarnAsync(long number, string text)
    {
        lock (_gate)
        {
            if (!_warned.Add(number))
            {
                return;
            }
        }

        await PublishAsync(new AgentNotice(Now, text, IsWarning: true)).ConfigureAwait(false);
    }

    /// <summary>
    /// Writes to the events of the session, also after it was closed for everybody else. The channel may have
    /// been completed in the meantime, by the one who disposed the session: nobody reads it then.
    /// </summary>
    private async ValueTask SendAsync(AgentEvent agentEvent)
    {
        try
        {
            await _events.PublishAsync(agentEvent).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
        }
    }

    /// <summary>
    /// Answers a request of Codex from where its messages are read. An answer that cannot be delivered is told to
    /// the user; it does not stop the reading, which the other conversations of the process depend on.
    /// </summary>
    private async ValueTask AnswerAsync(string method, Func<Task> answer)
    {
        try
        {
            await answer().ConfigureAwait(false);
        }
        catch (AgentProtocolException ex)
        {
            await PublishAsync(new AgentNotice(Now, $"YAV could not answer the request '{method}' of Codex: {ex.Message}", IsWarning: true)).ConfigureAwait(false);
        }
    }

    public async Task StartTurnAsync(TurnRequest request, CancellationToken cancellationToken)
    {
        lock (_gate)
        {
            if (_refusal is not null)
            {
                throw new AgentProtocolException(_refusal, refused: true);
            }

            if (_closed)
            {
                throw new AgentProtocolException("The session has ended.");
            }

            if (_turnRequested)
            {
                throw new InvalidOperationException("A turn is already running in this session.");
            }

            _turnRequested = true;
            _structuredRequested = request.OutputSchema is not null;
            _finalAnswer = null;
            _lastUnphased = null;
            _lastMessage = null;
        }

        try
        {
            var result = await _connection.RequestAsync(
                "turn/start",
                writer =>
                {
                    writer.WriteString("threadId", SessionId);
                    writer.WritePropertyName("input");
                    writer.WriteStartArray();
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", request.Prompt);
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                    if (_request.Effort.Length > 0)
                    {
                        writer.WriteString("effort", _request.Effort);
                    }

                    if (request.OutputSchema is { } schema)
                    {
                        writer.WritePropertyName("outputSchema");
                        schema.WriteTo(writer);
                    }
                },
                cancellationToken).ConfigureAwait(false);

            var turnId = result.Child("turn")?.Text("id");
            lock (_gate)
            {
                // The notification may have arrived, and even the turn may have ended, before this response.
                if (_turnRequested && _activeTurn is null && turnId is not null)
                {
                    _activeTurn = turnId;
                }
            }

            StopRefusedTurn();
        }
        catch
        {
            lock (_gate)
            {
                _turnRequested = false;
                _activeTurn = null;
            }

            throw;
        }
    }

    public async Task RespondToApprovalAsync(string approvalId, ApprovalDecision decision, CancellationToken cancellationToken)
    {
        if (!_approvals.TryRemove(approvalId, out var pending))
        {
            throw new InvalidOperationException($"There is no open approval request '{approvalId}'.");
        }

        if (pending.Kind == ApprovalKind.Permissions)
        {
            var grant = decision is ApprovalDecision.Accept or ApprovalDecision.AcceptForSession;
            await _connection.RespondAsync(
                pending.RawId,
                writer =>
                {
                    writer.WritePropertyName("permissions");
                    if (grant && pending.RequestedPermissions is { } requested)
                    {
                        requested.WriteTo(writer);
                    }
                    else
                    {
                        // Nothing is granted.
                        writer.WriteStartObject();
                        writer.WriteEndObject();
                    }

                    // Permissions are offered for the turn only, so an answer for the whole conversation grants them
                    // for the turn: access is never granted wider than the user was asked.
                    writer.WriteString("scope", "turn");
                },
                cancellationToken).ConfigureAwait(false);
            return;
        }

        var value = decision switch
        {
            ApprovalDecision.Accept => "accept",
            ApprovalDecision.AcceptForSession => "acceptForSession",
            ApprovalDecision.Cancel => "cancel",
            _ => "decline",
        };
        await _connection.RespondAsync(pending.RawId, writer => writer.WriteString("decision", value), cancellationToken).ConfigureAwait(false);
    }

    public async Task InterruptAsync(CancellationToken cancellationToken)
    {
        string? turn;
        lock (_gate)
        {
            turn = _closed ? null : _activeTurn;
            if (turn is not null && !ClaimInterrupt(turn))
            {
                turn = null;
            }
        }

        if (turn is null)
        {
            return;
        }

        await SendInterruptAsync(turn, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>True when the turn was not asked to stop yet; from now on it counts as asked. Called under the lock.</summary>
    private bool ClaimInterrupt(string turn)
    {
        if (_interruptedTurn == turn)
        {
            return false;
        }

        _interruptedTurn = turn;
        return true;
    }

    private async Task SendInterruptAsync(string turn, CancellationToken cancellationToken)
    {
        try
        {
            await _connection.RequestAsync(
                "turn/interrupt",
                writer =>
                {
                    writer.WriteString("threadId", SessionId);
                    writer.WriteString("turnId", turn);
                },
                cancellationToken,
                TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }
        catch (AgentProtocolException)
        {
            // The turn ended by itself in the meantime, or the agent is gone. Either way nothing is running.
        }
    }

    public async Task<bool> SteerAsync(string text, CancellationToken cancellationToken)
    {
        string? turn;
        lock (_gate)
        {
            turn = _closed ? null : _activeTurn;
        }

        if (turn is null)
        {
            return false;
        }

        try
        {
            await _connection.RequestAsync(
                "turn/steer",
                writer =>
                {
                    writer.WriteString("threadId", SessionId);
                    writer.WriteString("expectedTurnId", turn);
                    writer.WritePropertyName("input");
                    writer.WriteStartArray();
                    writer.WriteStartObject();
                    writer.WriteString("type", "text");
                    writer.WriteString("text", text);
                    writer.WriteEndObject();
                    writer.WriteEndArray();
                },
                cancellationToken).ConfigureAwait(false);
            return true;
        }
        catch (AgentProtocolException)
        {
            return false;
        }
    }

    public async Task ShutdownAsync(TimeSpan grace, CancellationToken cancellationToken)
    {
        // The grace period is measured in real time: it bounds how long the user waits after asking to stop.
        var started = System.Diagnostics.Stopwatch.GetTimestamp();

        // The process is shared with the other sessions of the adapter, so the provider is asked to stop this turn first.
        using (var limit = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken))
        {
            limit.CancelAfter(grace);
            try
            {
                await InterruptAsync(limit.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // The agent did not even answer the request to stop.
            }
        }

        while (System.Diagnostics.Stopwatch.GetElapsedTime(started) < grace)
        {
            lock (_gate)
            {
                if (_activeTurn is null && !_turnRequested)
                {
                    return;
                }
            }

            await Task.Delay(50, cancellationToken).ConfigureAwait(false);
        }

        lock (_gate)
        {
            if (_activeTurn is null && !_turnRequested)
            {
                return;
            }
        }

        // The turn did not end. Ending the agent's own process is what is left; other conversations of this
        // adapter end with it and are resumed in a new process when they are needed again.
        await _adapter.AbandonAsync(_connection).ConfigureAwait(false);
    }

    internal async ValueTask HandleNotificationAsync(string method, JsonElement parameters)
    {
        switch (method)
        {
            case "turn/started":
                lock (_gate)
                {
                    if (_turnRequested && parameters.Child("turn")?.Text("id") is { } id)
                    {
                        _activeTurn = id;
                    }
                }

                StopRefusedTurn();
                await PublishAsync(new TurnStarted(Now, parameters.Child("turn")?.Text("id") ?? string.Empty)).ConfigureAwait(false);
                break;

            case "thread/settings/updated":
                if (parameters.Child("threadSettings") is { } settings)
                {
                    EffectiveSettings now;
                    lock (_gate)
                    {
                        now = CodexAppServerAdapter.ParseSettingsUpdate(settings, method, _effective);
                        _effective = now;
                    }

                    await SettingsChangedAsync(
                        now,
                        CodexAppServerAdapter.RefusalFor(now.ApprovalsReviewer) ?? CodexAppServerAdapter.SandboxRefusal(settings.Child("sandboxPolicy"))).ConfigureAwait(false);
                }

                break;

            case "item/agentMessage/delta":
                await PublishAsync(new AssistantTextDelta(Now, parameters.Text("itemId") ?? string.Empty, parameters.Text("delta") ?? string.Empty)).ConfigureAwait(false);
                break;

            case "item/commandExecution/outputDelta":
                await PublishAsync(new CommandOutputDelta(Now, parameters.Text("itemId") ?? string.Empty, parameters.Text("delta") ?? string.Empty)).ConfigureAwait(false);
                break;

            case "item/reasoning/summaryTextDelta":
                // The complete summary follows with the finished item.
                break;

            case "item/started":
                switch (parameters.Child("item"))
                {
                    case { } started when started.Text("type") == "commandExecution":
                        await PublishAsync(new CommandStarted(Now, started.Text("id") ?? string.Empty, started.Text("command") ?? string.Empty, started.Text("cwd"))).ConfigureAwait(false);
                        break;

                    case { } change when change.Text("type") == "fileChange" && change.Text("id") is { } changeId:
                        _announcedChanges[changeId] = ParseAnnounced(change);
                        break;
                }

                break;

            case "item/fileChange/patchUpdated":
                // The change is still being written; the files named last are the ones that would be changed.
                if (parameters.Text("itemId") is { } patchedId)
                {
                    _announcedChanges[patchedId] = ParseAnnounced(parameters);
                }

                break;

            case "item/completed":
                if (parameters.Child("item") is { } item)
                {
                    await HandleItemAsync(item).ConfigureAwait(false);
                }

                break;

            case "thread/tokenUsage/updated":
                if (parameters.Child("tokenUsage") is { } usage)
                {
                    await PublishAsync(new UsageUpdated(Now, ParseUsage(usage))).ConfigureAwait(false);
                }

                break;

            case "model/rerouted":
                await PublishAsync(new ModelRerouted(
                    Now, parameters.Text("fromModel") ?? string.Empty, parameters.Text("toModel") ?? string.Empty,
                    parameters.Text("reason") ?? "unknown")).ConfigureAwait(false);
                break;

            case "error":
            {
                var error = parameters.Child("error");
                await PublishAsync(new AgentError(
                    Now, error?.Text("message") ?? "The agent reported an error.", ErrorCode(error), parameters.Flag("willRetry") ?? false)).ConfigureAwait(false);
                break;
            }

            case "warning":
                await PublishAsync(new AgentNotice(Now, parameters.Text("message") ?? string.Empty, IsWarning: true)).ConfigureAwait(false);
                break;

            // Each of these says that Codex's own reviewer takes part in deciding about access in this conversation.
            case "item/autoApprovalReview/started":
            case "item/autoApprovalReview/completed":
            case "autoApprovalReview/strictReviewRequired":
            case "guardianWarning":
                await RefuseAsync(AutoReviewRefusal(method, parameters)).ConfigureAwait(false);
                break;

            case "serverRequest/resolved":
            {
                var raw = parameters.Child("requestId")?.GetRawText();
                foreach (var (approvalId, pending) in _approvals)
                {
                    if (pending.RawId == raw && _approvals.TryRemove(approvalId, out _))
                    {
                        await PublishAsync(new ApprovalWithdrawn(Now, approvalId)).ConfigureAwait(false);
                    }
                }

                break;
            }

            case "turn/completed":
                await CompleteTurnAsync(parameters.Child("turn")).ConfigureAwait(false);
                break;

            // Codex unloaded the thread: nothing runs the conversation any more, and nothing more arrives for it.
            // Waiting for it would be waiting without an end, so it ends for YAV; it can be resumed.
            case "thread/closed":
                _server.Forget(this);
                await ConnectionClosedAsync(null, "Codex closed the conversation, so nothing runs it any more. It can be resumed.").ConfigureAwait(false);
                break;
        }
    }

    private async ValueTask HandleItemAsync(JsonElement item)
    {
        var id = item.Text("id") ?? string.Empty;
        switch (item.Text("type"))
        {
            case "agentMessage":
            {
                var text = item.Text("text") ?? string.Empty;
                var phase = item.Text("phase") switch
                {
                    "final_answer" => MessagePhase.FinalAnswer,
                    "commentary" => MessagePhase.Commentary,
                    _ => MessagePhase.Unknown,
                };
                lock (_gate)
                {
                    _lastMessage = text;
                    if (phase == MessagePhase.FinalAnswer)
                    {
                        _finalAnswer = text;
                    }
                    else if (phase == MessagePhase.Unknown)
                    {
                        _lastUnphased = text;
                    }
                }

                await PublishAsync(new AssistantMessage(Now, id, text, phase)).ConfigureAwait(false);
                break;
            }

            case "reasoning":
            {
                var summary = string.Join("\n", item.Items("summary").Select(s => s.GetString()).Where(s => !string.IsNullOrEmpty(s)));
                if (summary.Length > 0)
                {
                    await PublishAsync(new ReasoningSummary(Now, id, summary)).ConfigureAwait(false);
                }

                break;
            }

            case "commandExecution":
                await PublishAsync(new CommandCompleted(
                    Now, id, item.Text("command") ?? string.Empty, (int?)item.Number("exitCode"), item.Number("durationMs"),
                    item.Text("status") ?? "completed", item.Text("aggregatedOutput"))).ConfigureAwait(false);
                break;

            case "fileChange":
                _announcedChanges.TryRemove(id, out _);
                await PublishAsync(new FilesChanged(Now, id, ParseChanges(item), item.Text("status") ?? "completed")).ConfigureAwait(false);
                break;

            case "mcpToolCall":
                await PublishAsync(new ToolActivity(Now, id, $"{item.Text("server")}/{item.Text("tool")}", "MCP tool call", item.Text("status") ?? "completed")).ConfigureAwait(false);
                break;

            case "webSearch":
                await PublishAsync(new ToolActivity(Now, id, "web search", item.Text("query") ?? string.Empty, "completed")).ConfigureAwait(false);
                break;

            case "dynamicToolCall":
                await PublishAsync(new ToolActivity(Now, id, item.Text("tool") ?? "tool", "Tool call", item.Text("status") ?? "completed")).ConfigureAwait(false);
                break;

            case "contextCompaction":
                await PublishAsync(new AgentNotice(Now, "The agent compacted its context.", IsWarning: false)).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>The files of a change, from an item or a notification that carries its changes.</summary>
    private static List<FileChange> ParseChanges(JsonElement holder) => ParseAnnounced(holder).Select(change => new FileChange(
        change.Path,
        change.Kind switch
        {
            "add" => FileChangeKind.Add,
            "delete" => FileChangeKind.Delete,

            // The events have no kind for what YAV does not know; the file is changed in some way.
            _ => change.MovedTo is null ? FileChangeKind.Update : FileChangeKind.Move,
        },
        change.MovedTo)).ToList();

    /// <summary>The files of a change with the kind of each as Codex names it: "add", "delete", "update", or another.</summary>
    private static List<AnnouncedChange> ParseAnnounced(JsonElement holder) => holder.Items("changes").Select(change =>
    {
        var kind = change.Child("kind");
        return new AnnouncedChange(change.Text("path") ?? string.Empty, kind?.Text("type"), kind?.Text("move_path"));
    }).ToList();

    /// <summary>A kind of change YAV knows how to describe.</summary>
    private static bool IsKnownChange(AnnouncedChange change) => change.Kind is "add" or "delete" or "update";

    /// <summary>What a request to approve a change would change, for the user to see before deciding.</summary>
    private List<string> DescribeChanges(string? itemId)
    {
        if (itemId is null || !_announcedChanges.TryGetValue(itemId, out var changes) || changes.Count == 0)
        {
            return ["Codex did not say which files it would change."];
        }

        var lines = changes.Take(NamedFiles).Select(change => change.Kind switch
        {
            "add" => "Add " + change.Path,
            "delete" => "Delete " + change.Path,
            "update" when change.MovedTo is not null => $"Move {change.Path} to {change.MovedTo}",
            "update" => "Update " + change.Path,
            null => $"Change {change.Path} in a way Codex did not name",
            var other => $"Change {change.Path} in the way Codex calls '{other}', which this version of YAV does not know"
                + (change.MovedTo is null ? string.Empty : $" (to {change.MovedTo})"),
        }).ToList();
        if (changes.Count > NamedFiles)
        {
            lines.Add($"and {changes.Count - NamedFiles} more");
        }

        return lines;
    }

    private async ValueTask CompleteTurnAsync(JsonElement? turn)
    {
        string? final;
        bool structuredRequested;
        lock (_gate)
        {
            if (!_turnRequested)
            {
                return;
            }

            // The final answer is the message the provider marked as such; without marks, the last message.
            final = _finalAnswer ?? _lastUnphased ?? _lastMessage;
            structuredRequested = _structuredRequested;
            _turnRequested = false;
            _activeTurn = null;
        }

        _announcedChanges.Clear();
        foreach (var approvalId in _approvals.Keys.ToList())
        {
            if (_approvals.TryRemove(approvalId, out _))
            {
                await PublishAsync(new ApprovalWithdrawn(Now, approvalId)).ConfigureAwait(false);
            }
        }

        var error = turn?.Child("error");
        var code = ErrorCode(error);
        var outcome = turn?.Text("status") switch
        {
            "completed" => TurnOutcome.Completed,
            "interrupted" => TurnOutcome.Interrupted,
            _ => code switch
            {
                "usageLimitExceeded" or "sessionBudgetExceeded" => TurnOutcome.UsageLimitReached,
                "rateLimitExceeded" or "serverOverloaded" => TurnOutcome.RateLimited,
                _ => TurnOutcome.Failed,
            },
        };

        await PublishAsync(new TurnCompleted(
            Now,
            turn?.Text("id") ?? string.Empty,
            outcome,
            final,
            structuredRequested && outcome == TurnOutcome.Completed ? JsonReading.ParseObject(final) : null,
            code,
            error?.Text("message"),
            [])).ConfigureAwait(false);
    }

    private static string? ErrorCode(JsonElement? error)
    {
        if (error?.Child("codexErrorInfo") is not { } info)
        {
            return null;
        }

        if (info.ValueKind == JsonValueKind.String)
        {
            return info.GetString();
        }

        // Some error kinds are objects with a single property that names the kind.
        if (info.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in info.EnumerateObject())
            {
                return property.Name;
            }
        }

        return null;
    }

    private UsageSnapshot ParseUsage(JsonElement usage)
    {
        // Codex counts cached tokens inside the input count and reasoning tokens inside the output count.
        static TokenCounts? Counts(JsonElement? breakdown) => breakdown is { } b
            ? TokenCounts.FromInclusiveCounters(
                b.Number("inputTokens"), b.Number("cachedInputTokens"), b.Number("outputTokens"), b.Number("reasoningOutputTokens"),
                b.Number("cacheWriteInputTokens") is > 0 ? b.Number("cacheWriteInputTokens") : null)
            : null;

        return new UsageSnapshot(
            AdapterId,
            SessionId,
            Effective?.Model,
            UsageScope.CumulativeForSession,
            Counts(usage.Child("total")) ?? TokenCounts.Unavailable,
            Counts(usage.Child("last")),
            ProviderCostUsd: null,
            CostProvenance: ValueProvenance.Unavailable,
            ContextWindow: usage.Number("modelContextWindow"),
            Source: "thread/tokenUsage/updated",
            ObservedAt: Now);
    }

    internal async ValueTask HandleServerRequestAsync(string rawId, string method, JsonElement parameters)
    {
        bool refused;
        lock (_gate)
        {
            refused = _refusal is not null;
        }

        if (refused)
        {
            // Nobody sees this conversation any more, so nobody could answer; kept, it would be waited for forever.
            await CancelAsync(rawId, method).ConfigureAwait(false);
            return;
        }

        var kind = method switch
        {
            "item/commandExecution/requestApproval" => ApprovalKind.CommandExecution,
            "item/fileChange/requestApproval" => ApprovalKind.FileChange,
            "item/permissions/requestApproval" => ApprovalKind.Permissions,
            _ => (ApprovalKind?)null,
        };

        if (kind is null)
        {
            if (method == "mcpServer/elicitation/request")
            {
                // YAV cannot show the form or the page an MCP server asks the user to fill in.
                await AnswerAsync(method, () => _connection.RespondAsync(rawId, writer => writer.WriteString("action", "decline"), CancellationToken.None)).ConfigureAwait(false);
                await PublishAsync(new AgentNotice(Now, ElicitationDeclined(parameters), IsWarning: true)).ConfigureAwait(false);
            }
            else
            {
                await AnswerAsync(method, () => _connection.RespondErrorAsync(rawId, -32601, $"YAV Shell does not handle '{method}'.", CancellationToken.None)).ConfigureAwait(false);
                await PublishAsync(new AgentNotice(
                    Now,
                    $"Codex asked for something this version of YAV does not handle ('{method}'), so it was refused; the turn goes on without it.",
                    IsWarning: true)).ConfigureAwait(false);
            }

            return;
        }

        if (_request.Approvals == ApprovalMode.NeverAsk)
        {
            // The read-only reviewer never gets more access, whatever is asked.
            if (kind == ApprovalKind.Permissions)
            {
                await AnswerAsync(method, () => _connection.RespondAsync(
                    rawId,
                    writer =>
                    {
                        writer.WritePropertyName("permissions");
                        writer.WriteStartObject();
                        writer.WriteEndObject();
                    },
                    CancellationToken.None)).ConfigureAwait(false);
            }
            else
            {
                await AnswerAsync(method, () => _connection.RespondAsync(rawId, writer => writer.WriteString("decision", "decline"), CancellationToken.None)).ConfigureAwait(false);
            }

            await PublishAsync(new AgentNotice(Now, "The agent asked for an approval that this role never gives; it was declined.", IsWarning: true)).ConfigureAwait(false);
            return;
        }

        var approvalId = rawId.Trim('"');
        var command = parameters.Text("command");
        var details = new List<string>();
        if (parameters.Child("networkApprovalContext") is { } network)
        {
            details.Add($"Network access to {network.Text("host")} ({network.Text("protocol")})");
        }

        if (parameters.Text("grantRoot") is { } root)
        {
            details.Add("Write access below " + root);
        }

        if (kind == ApprovalKind.Permissions && parameters.Child("permissions") is { } requested)
        {
            details.Add("Requested permissions: " + requested.GetRawText());
        }

        if (kind == ApprovalKind.FileChange)
        {
            details.AddRange(DescribeChanges(parameters.Text("itemId")));
        }

        if (kind == ApprovalKind.CommandExecution && parameters.Text("kind") == "writeStdin")
        {
            // Shown also where only the command is shown and the title is not.
            details.Add("Codex asks to send input to a command that is already running, not to start a new one.");
        }

        _approvals[approvalId] = new PendingApproval(
            rawId, kind.Value, kind == ApprovalKind.Permissions ? parameters.Child("permissions")?.Clone() : null, method);

        // What YAV cannot describe, or cannot name completely, is never granted for the rest of the conversation,
        // and not by a key pressed by accident.
        var unknown = kind switch
        {
            ApprovalKind.CommandExecution => parameters.Text("kind") is not (null or "command" or "writeStdin"),
            ApprovalKind.FileChange => parameters.Text("itemId") is { } changed
                && _announcedChanges.TryGetValue(changed, out var announced)
                && (!announced.All(IsKnownChange) || announced.Count > NamedFiles),
            _ => false,
        };

        await PublishAsync(new ApprovalRequested(
            Now,
            new ApprovalRequest(
                approvalId,
                kind.Value,
                kind switch
                {
                    ApprovalKind.CommandExecution => CommandTitle(parameters),
                    ApprovalKind.FileChange => "Change files",
                    _ => "Grant additional permissions",
                },
                command,
                parameters.Text("cwd"),
                parameters.Text("reason"),
                details,
                CanAcceptForSession: kind != ApprovalKind.Permissions && !unknown,
                Deliberate: unknown))).ConfigureAwait(false);
    }

    /// <summary>
    /// What a request that comes as a command approval is about. Codex also asks through it to send input to a
    /// command that is already running, and to let a command reach the network; the command can then be missing.
    /// </summary>
    private static string CommandTitle(JsonElement parameters)
    {
        var named = !string.IsNullOrWhiteSpace(parameters.Text("command"));

        // Versions of Codex that send no kind mean a command.
        return (parameters.Text("kind") ?? "command") switch
        {
            "command" when named => "Run a command",
            "command" when parameters.Child("networkApprovalContext")?.Text("host") is { } host => "Allow network access to " + host,
            "command" => "Run a command that Codex did not name",
            "writeStdin" => "Send input to a running command",
            var other => $"Approve a request of the kind '{other}', which this version of YAV does not know",
        };
    }

    /// <summary>What the user is told about a question of an MCP server that was declined, with the server and the question.</summary>
    private static string ElicitationDeclined(JsonElement parameters)
    {
        var question = parameters.Text("message") is { Length: > 0 } message
            ? " (\"" + (message.Length <= 300 ? message : message[..300] + "...") + "\")"
            : string.Empty;
        return $"The MCP server '{parameters.Text("serverName") ?? "unknown"}' asked you something{question}. YAV cannot show such a "
            + "question, so it was declined; the tool that asked goes on without the answer.";
    }

    /// <summary>
    /// The model started a sub-agent. It works in a thread of its own that YAV does not follow, so the user is
    /// told once, whatever the sub-agent does afterwards.
    /// </summary>
    internal async ValueTask SubAgentStartedAsync(string threadId, string? nickname)
    {
        if (_subAgents.TryAdd(threadId, nickname))
        {
            await PublishAsync(new AgentNotice(Now, $"The model started a sub-agent{SubAgentName(threadId)}. YAV neither shows nor counts what it does.", IsWarning: true))
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// A sub-agent of this conversation asked for approval. YAV does not show the requests of sub-agents, so the
    /// request is refused; the user is told once for each sub-agent.
    /// </summary>
    internal async ValueTask SubAgentAskedAsync(string threadId, string method)
    {
        if (_subAgentsAsked.TryAdd(threadId, 0))
        {
            await PublishAsync(new AgentNotice(
                Now,
                $"A sub-agent{SubAgentName(threadId)} asked for approval ('{method}'). YAV does not show what sub-agents ask, so it was refused, "
                + "and so is everything this sub-agent asks later; it goes on without it.",
                IsWarning: true)).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Codex says that its own reviewer takes part in deciding about access for a sub-agent of this conversation.
    /// The sub-agent works in the same workspace, so the conversation is refused as if it were its own reviewer.
    /// </summary>
    internal ValueTask SubAgentReviewedAsync(string threadId, string method, JsonElement parameters) =>
        RefuseAsync(AutoReviewRefusal(method, parameters, $"a sub-agent{SubAgentName(threadId)} of this conversation"));

    private string SubAgentName(string threadId) => _subAgents.TryGetValue(threadId, out var nickname) && !string.IsNullOrEmpty(nickname)
        ? $" ({nickname})"
        : string.Empty;

    /// <summary>
    /// The settings changed while the conversation is open. What is in effect now is reported like the settings
    /// of a new conversation, so that it is verified again; a conversation whose requests for access Codex would
    /// now decide without the user, or whose limits are no longer known, is not used any further.
    /// </summary>
    private async ValueTask SettingsChangedAsync(EffectiveSettings now, string? refusal)
    {
        if (refusal is not null)
        {
            // The settings are reported by the refusal, after the conversation was marked as given up.
            await RefuseAsync(refusal, now).ConfigureAwait(false);
            return;
        }

        await PublishAsync(new SessionConfigured(Now, SessionId ?? string.Empty, now)).ConfigureAwait(false);
    }

    /// <summary>
    /// Codex says that the account of its process changed. The conversation reports what is in effect now, so
    /// that it is compared again with the route that was shown before the run. An older report never replaces a
    /// newer one.
    /// </summary>
    internal async ValueTask AccountChangedAsync(AccountSaid account, long version)
    {
        EffectiveSettings now;
        lock (_gate)
        {
            if (_closed || version <= _accountVersion)
            {
                return;
            }

            _accountVersion = version;
            now = _effective with { Account = account };
            _effective = now;
        }

        await PublishAsync(new SessionConfigured(Now, SessionId ?? string.Empty, now)).ConfigureAwait(false);
    }

    /// <summary>
    /// Why a conversation in which Codex's own reviewer took part in a decision about access is not used, naming
    /// what Codex said. Whatever it decided, it was not the user who decided.
    /// </summary>
    /// <param name="forWhom">Whose decision it was, when it was not this conversation's own: a sub-agent of it.</param>
    internal static string AutoReviewRefusal(string method, JsonElement parameters, string? forWhom = null)
    {
        static string Bounded(string text) => text.Length <= 300 ? text : text[..300] + "...";

        var action = parameters.Child("action") is { } given
            ? given.Text("type") switch
            {
                "command" => $"the command '{Bounded(given.Text("command") ?? string.Empty)}'",
                "execve" => $"the program '{Bounded(given.Text("program") ?? string.Empty)}'",
                "writeStdin" => "input to a running command",
                "applyPatch" => "a change of files",
                "networkAccess" => $"network access to {given.Text("host")}",
                "mcpToolCall" => $"the tool {given.Text("server")}/{given.Text("toolName")}",
                var other => $"an action of the kind '{other}'",
            }
            : null;
        var said = (method, parameters.Child("review")?.Text("status"), action) switch
        {
            ("guardianWarning", _, _) when parameters.Text("message") is { Length: > 0 } message => $": \"{Bounded(message)}\"",
            (_, { } status, { } about) when status != "inProgress" => $", with the outcome '{status}' for {about}",
            (_, _, { } about) => $", about {about}",
            _ => string.Empty,
        };

        var where = forWhom is null ? "in this conversation" : "for " + forWhom;
        return $"Codex reported '{method}' {where}{said}. Its own reviewer decides, or decided, about access here instead of you: "
            + "requests for access would be approved or denied without you, and YAV would not see them. YAV lets only you decide about "
            + "access, so the conversation is not used. Check the setting approvals_reviewer of Codex.";
    }

    /// <summary>
    /// Ends the conversation for YAV and stops its turn through the provider. What Codex did until then is
    /// treated like the work of an agent that ended during a turn: YAV may not have seen all of it. The
    /// conversation counts as given up before anybody hears of it, so that nobody asks Codex to stop the turn a
    /// second time, and what Codex asks afterwards is answered with cancel instead of being kept.
    /// </summary>
    /// <param name="configured">The settings that led to the refusal, reported first when there are any.</param>
    private async ValueTask RefuseAsync(string refusal, EffectiveSettings? configured = null)
    {
        lock (_gate)
        {
            if (_closed || _refusal is not null)
            {
                return;
            }

            _refusal = refusal;
            _closed = true;
        }

        StopRefusedTurn();
        if (configured is not null)
        {
            await SendAsync(new SessionConfigured(Now, SessionId ?? string.Empty, configured)).ConfigureAwait(false);
        }

        await SendAsync(new AgentNotice(Now, refusal, IsWarning: true)).ConfigureAwait(false);
        await SendAsync(new SessionEnded(Now, null, refusal)).ConfigureAwait(false);
        _events.Complete();

        // Nobody can answer what was asked before: the user no longer sees this conversation.
        foreach (var approvalId in _approvals.Keys.ToList())
        {
            if (_approvals.TryRemove(approvalId, out var pending))
            {
                await CancelAsync(pending.RawId, pending.Method).ConfigureAwait(false);
            }
        }
    }

    /// <summary>Answers a request of Codex so that nothing is granted and the turn is stopped.</summary>
    private ValueTask CancelAsync(string rawId, string method) => method switch
    {
        "item/permissions/requestApproval" => AnswerAsync(method, () => _connection.RespondAsync(
            rawId,
            writer =>
            {
                writer.WritePropertyName("permissions");
                writer.WriteStartObject();
                writer.WriteEndObject();
            },
            CancellationToken.None)),
        "item/commandExecution/requestApproval" or "item/fileChange/requestApproval" =>
            AnswerAsync(method, () => _connection.RespondAsync(rawId, writer => writer.WriteString("decision", "cancel"), CancellationToken.None)),
        "mcpServer/elicitation/request" =>
            AnswerAsync(method, () => _connection.RespondAsync(rawId, writer => writer.WriteString("action", "cancel"), CancellationToken.None)),
        _ => AnswerAsync(method, () => _connection.RespondErrorAsync(rawId, -32601, $"YAV Shell does not handle '{method}'.", CancellationToken.None)),
    };

    /// <summary>
    /// Stops the turn of a refused conversation through the provider, once, as soon as the turn is known. It
    /// is not awaited: the answer arrives through the connection's reader, which may be the caller.
    /// </summary>
    private void StopRefusedTurn()
    {
        string turn;
        lock (_gate)
        {
            if (_refusal is null || _activeTurn is null || _refusedTurnStopped)
            {
                return;
            }

            _refusedTurnStopped = true;
            turn = _activeTurn;

            // Somebody asked already; once is enough.
            if (!ClaimInterrupt(turn))
            {
                return;
            }
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await SendInterruptAsync(turn, CancellationToken.None).ConfigureAwait(false);
            }
            catch (ObjectDisposedException)
            {
                // The connection was closed in the meantime, and the turn ended with it.
            }
        });
    }

    internal async ValueTask ConnectionClosedAsync(int? exitCode, string description)
    {
        bool running;
        lock (_gate)
        {
            if (_closed)
            {
                return;
            }

            // Closed first, so that nothing else is published after the end.
            _closed = true;
            running = _turnRequested;
            _turnRequested = false;
            _activeTurn = null;
        }

        if (running)
        {
            await SendAsync(new TurnCompleted(
                Now, string.Empty, TurnOutcome.Failed, null, null, TurnErrorCodes.AgentExited,
                "The agent ended before the turn was finished. " + description, [])).ConfigureAwait(false);
        }

        await SendAsync(new SessionEnded(Now, exitCode, description)).ConfigureAwait(false);
        _events.Complete();
    }

    public ValueTask DisposeAsync()
    {
        _server.Forget(this);
        lock (_gate)
        {
            _closed = true;
        }

        _events.Complete();
        return ValueTask.CompletedTask;
    }
}
