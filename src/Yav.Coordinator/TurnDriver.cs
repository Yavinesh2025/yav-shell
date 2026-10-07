using System.Text.Json;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Runs;
using Yav.Core.Timing;

namespace Yav.Coordinator;

internal sealed record TurnResult(
    TurnOutcome Outcome,
    string? FinalMessage,
    JsonElement? StructuredOutput,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<PermissionDenial> Denials,
    // The agent's process or connection ended before the turn did.
    bool SessionLost,
    // Why YAV did not run or did not finish the turn: the provider did not honor the run profile.
    string? PolicyViolation,
    bool StoppedByUser,
    // The turn was never sent, so it cost nothing.
    bool NotStarted,
    // The agent needed an approval nobody could give.
    bool ApprovalRequired);

/// <summary>
/// Runs one turn of one agent conversation: forwards what happens, answers approval requests, enforces the
/// run profile and stops the turn in the order the provider supports: interrupt first, then a bounded
/// shutdown, then ending the agent's own process tree.
/// </summary>
internal sealed class TurnDriver
{
    /// <summary>What is recorded for an answer that did not reach the agent, whatever the answer was.</summary>
    private const string NotDelivered = "NotDelivered";

    private readonly CoordinatorServices _services;
    private readonly CoordinatorOptions _options;
    private readonly Action<RunContext, RunState, string?> _transition;

    public TurnDriver(CoordinatorServices services, CoordinatorOptions options, Action<RunContext, RunState, string?> transition)
    {
        _services = services;
        _options = options;
        _transition = transition;
    }

    private sealed class PendingApproval(ApprovalRequest request, CancellationTokenSource cancel, DateTimeOffset requestedAt)
    {
        public ApprovalRequest Request { get; } = request;

        public CancellationTokenSource Cancel { get; } = cancel;

        public DateTimeOffset RequestedAt { get; } = requestedAt;

        public Task Task { get; set; } = Task.CompletedTask;
    }

    private sealed class TurnState
    {
        public Lock Gate { get; } = new();

        public Dictionary<string, PendingApproval> Pending { get; } = new(StringComparer.Ordinal);

        public List<Task> ApprovalTasks { get; } = [];

        public TurnCompleted? Completed { get; set; }

        public bool SessionLost { get; set; }

        public string? LostReason { get; set; }

        public string? Violation { get; set; }

        public bool Stopping { get; set; }

        public bool ApprovalRequired { get; set; }

        /// <summary>When the turn began, by the clock the run is measured with.</summary>
        public DateTimeOffset Began { get; init; }

        /// <summary>The tools of the agent that are running, by the item they were reported with.</summary>
        public Dictionary<string, TimingRecorder.SpanScope> Tools { get; } = new(StringComparer.Ordinal);
    }

    public async Task<TurnResult> RunAsync(RunContext context, SessionSlot slot, TurnRequest request, CancellationToken stop)
    {
        var session = slot.Session;
        var role = slot.Role.Role;
        var state = new TurnState { Began = context.Timing.Clock.GetUtcNow() };

        // What the session reported when it was opened is compared with the run profile before anything is
        // sent, so a configuration that was not honored costs nothing.
        while (session.Events.TryRead(out var early))
        {
            if (early is not TurnCompleted)
            {
                Handle(context, slot, early, state);
            }
        }

        if (state.Violation is null && !HasConfirmations(context, role) && session.Effective is { } reported)
        {
            // A conversation kept from an earlier run of the task: its settings are recorded for this run too.
            Verify(context, slot, reported, state);
        }

        if (state.Violation is null && !state.SessionLost && session is IReportsBeforeTurn asked)
        {
            // An agent that says what it will work with before it is given the prompt is asked first.
            stop.ThrowIfCancellationRequested();
            if (await asked.PrepareTurnAsync(request, stop).ConfigureAwait(false) is { } early)
            {
                var verification = SettingsVerifier.VerifyBeforeTurn(slot.Role, context.Profile.Policy, early, _services.Clock.GetUtcNow());
                if (verification.Violations.Count > 0)
                {
                    // Recorded only when the turn is not started: one that begins reports everything, this included.
                    Record(context, role, verification.Confirmations);
                    state.Violation = string.Join(" ", verification.Violations);
                }
            }
        }

        if (state.Violation is not null || state.SessionLost)
        {
            slot.Lost = state.SessionLost;
            return Result(state, stoppedByUser: false, notStarted: true, forced: false);
        }

        stop.ThrowIfCancellationRequested();
        await session.StartTurnAsync(request, stop).ConfigureAwait(false);
        context.Usage[role].Turns++;

        var stoppedByUser = false;
        var forced = false;
        CancellationTokenSource? grace = null;
        var readToken = stop;
        try
        {
            while (true)
            {
                AgentEvent next;
                try
                {
                    if (!await session.Events.WaitToReadAsync(readToken).ConfigureAwait(false))
                    {
                        state.SessionLost = true;
                        state.LostReason ??= "The agent's event stream ended.";
                        break;
                    }

                    if (!session.Events.TryRead(out next!))
                    {
                        continue;
                    }
                }
                catch (OperationCanceledException)
                {
                    if (grace is null)
                    {
                        // The user asked to stop. The provider is asked first; the turn usually ends by itself.
                        stoppedByUser = true;
                        grace = await BeginStopAsync(context, slot, state).ConfigureAwait(false);
                        readToken = grace.Token;
                        continue;
                    }

                    forced = true;
                    break;
                }

                Handle(context, slot, next, state);
                if (state.Completed is not null || state.SessionLost)
                {
                    break;
                }

                if (state.Violation is not null && grace is null)
                {
                    grace = await BeginStopAsync(context, slot, state).ConfigureAwait(false);
                    readToken = grace.Token;
                }
            }
        }
        finally
        {
            grace?.Dispose();

            // A tool that had not ended when the turn ended is measured up to here.
            foreach (var tool in state.Tools.Values)
            {
                tool.Dispose();
            }

            state.Tools.Clear();
            await SettleApprovalsAsync(context, state).ConfigureAwait(false);
        }

        if (forced || state.SessionLost)
        {
            // The conversation is not used again; whatever is left of its processes is ended.
            slot.Lost = true;
            await ShutdownAsync(session).ConfigureAwait(false);
        }

        if (!HasConfirmations(context, role))
        {
            RecordUnreported(context, slot);
        }

        return Result(state, stoppedByUser, notStarted: false, forced);
    }

    private static TurnResult Result(TurnState state, bool stoppedByUser, bool notStarted, bool forced)
    {
        var completed = state.Completed;
        var outcome = completed?.Outcome
            ?? (stoppedByUser || state.Violation is not null || notStarted ? TurnOutcome.Interrupted : TurnOutcome.Failed);

        return new TurnResult(
            Outcome: outcome,
            FinalMessage: completed?.FinalMessage,
            StructuredOutput: completed?.StructuredOutput,
            ErrorCode: completed?.ErrorCode,
            ErrorMessage: completed?.ErrorMessage ?? (forced ? "The agent did not end the turn after it was asked to stop." : state.LostReason),
            Denials: completed?.Denials ?? [],
            SessionLost: state.SessionLost && completed is null,
            PolicyViolation: state.Violation,
            StoppedByUser: stoppedByUser,
            NotStarted: notStarted,
            ApprovalRequired: state.ApprovalRequired || completed?.Outcome == TurnOutcome.ApprovalRequired);
    }

    private async Task<CancellationTokenSource> BeginStopAsync(RunContext context, SessionSlot slot, TurnState state)
    {
        state.Stopping = true;
        CancelPending(state);
        try
        {
            using var timeout = new CancellationTokenSource(_options.InterruptGrace);
            await slot.Session.InterruptAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AgentException or OperationCanceledException or InvalidOperationException or IOException)
        {
            context.Publisher.Note(Stages.For(slot.Role.Role), "The agent could not be asked to stop: " + ex.Message, NoteLevel.Warning);
        }

        // The grace period is real time: it bounds how long the user waits after asking to stop.
        return new CancellationTokenSource(_options.InterruptGrace);
    }

    private async Task ShutdownAsync(IAgentSession session)
    {
        try
        {
            await session.ShutdownAsync(_options.ShutdownGrace, CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AgentException or OperationCanceledException or InvalidOperationException or IOException)
        {
            // The process tree is owned by a job object; it ends with the session in any case.
            _ = ex;
        }
    }

    private void Handle(RunContext context, SessionSlot slot, AgentEvent agentEvent, TurnState state)
    {
        var role = slot.Role.Role;
        var usage = context.Usage[role];
        context.Publisher.Publish(new AgentActivity(
            context.RunId, _services.Clock.GetUtcNow(), role, WorkspacePaths.ForDisplay(agentEvent, context.Workspace?.RootPath)));

        switch (agentEvent)
        {
            case SessionConfigured configured:
                usage.SessionId = configured.SessionId.Length > 0 ? configured.SessionId : usage.SessionId;
                usage.Model = configured.Effective.Model ?? usage.Model;
                Verify(context, slot, configured.Effective, state);
                break;

            case CommandStarted started:
                context.FirstActionAt ??= _services.Clock.GetUtcNow();
                BeginTool(context, role, started.ItemId, "command", started.At, state);
                break;

            case CommandCompleted completed:
                EndTool(completed.ItemId, completed.At, state);
                break;

            case ToolActivity tool:
                context.FirstActionAt ??= _services.Clock.GetUtcNow();
                if (tool.Status == "started")
                {
                    BeginTool(context, role, tool.ItemId, "tool", tool.At, state);
                }
                else
                {
                    EndTool(tool.ItemId, tool.At, state);
                }

                break;

            case AssistantMessage or AssistantTextDelta or FilesChanged or ReasoningSummary:
                context.FirstActionAt ??= _services.Clock.GetUtcNow();
                break;

            case ApprovalRequested requested:
                BeginApproval(context, slot, requested.Request, state);
                break;

            case ApprovalWithdrawn withdrawn:
                Withdraw(context, slot, withdrawn.ApprovalId, state);
                break;

            case UsageUpdated updated:
                usage.Tracker.Observe(updated.Usage);
                usage.Model = updated.Usage.Model ?? usage.Model;
                usage.SessionId = updated.Usage.SessionId ?? usage.SessionId;
                break;

            case RateLimitUpdated limits:
                context.RateLimits = limits.Snapshot;
                lock (context.RateLimitsByName)
                {
                    context.RateLimitsByName[$"{limits.Snapshot.AdapterId}:{limits.Snapshot.LimitName}"] = limits.Snapshot;
                }

                break;

            case ProviderRetry:
            case AgentError { WillRetry: true }:
                // Both are the provider saying that it tries the same request again.
                usage.Retries++;
                break;

            case ModelRerouted rerouted when context.Profile.Policy.QualityLock:
                state.Violation ??=
                    $"{RoleName(role)}: the provider rerouted the request from '{rerouted.FromModel}' to '{rerouted.ToModel}' ({rerouted.Reason}). "
                    + "A different model than the one you chose is never accepted silently.";
                break;

            case TurnCompleted { ErrorCode: TurnErrorCodes.AgentExited } exited:
                // Reported as the end of the turn, but it is the end of the agent: what it had done is unknown.
                state.SessionLost = true;
                state.LostReason = exited.ErrorMessage;
                break;

            case TurnCompleted completed:
                state.Completed = completed;
                break;

            case SessionEnded ended:
                state.SessionLost = true;
                state.LostReason = ended.Reason
                    ?? (ended.ExitCode is null ? "The agent process ended." : $"The agent process ended with exit code {ended.ExitCode}.");
                break;
        }
    }

    /// <summary>
    /// The time an agent spends in a tool is measured from the moment it reports the start to the moment it
    /// reports the end: the times the adapter read the reports, not when the turn takes them up, which on a busy
    /// machine can be later and both at once. A report with a time from before the turn, left from an earlier turn
    /// or not made on the clock of the run, counts from when it is taken up. What is kept says whose tool it was,
    /// not what it was given.
    /// </summary>
    private static void BeginTool(RunContext context, AgentRole role, string itemId, string what, DateTimeOffset reportedAt, TurnState state)
    {
        if (itemId.Length > 0 && !state.Tools.ContainsKey(itemId))
        {
            state.Tools[itemId] = context.Timing.Start(
                SpanKind.ToolActivity, $"{RoleName(role)}: {what}", startedAt: reportedAt >= state.Began ? reportedAt : null);
        }
    }

    private static void EndTool(string itemId, DateTimeOffset reportedAt, TurnState state)
    {
        if (state.Tools.Remove(itemId, out var tool))
        {
            tool.End(reportedAt);
        }
    }

    private static bool HasConfirmations(RunContext context, AgentRole role)
    {
        lock (context.Confirmations)
        {
            return context.Confirmations.Any(c => c.Role == role);
        }
    }

    private void Verify(RunContext context, SessionSlot slot, EffectiveSettings effective, TurnState state)
    {
        var verification = SettingsVerifier.Verify(slot.Role, context.Profile.Policy, effective, _services.Clock.GetUtcNow(), slot.WorkingDirectory);
        Record(context, slot.Role.Role, verification.Confirmations);
        slot.Verified = true;
        bool said;
        lock (context.NetworkAccessSaid)
        {
            said = effective.Widening is { NetworkAccess: true } && context.NetworkAccessSaid.Add(slot.Role.Role);
        }

        if (said)
        {
            // It stops nothing: YAV asks for no network and forbids none. But nobody should learn it from a table only.
            context.Publisher.Note(
                Stages.For(slot.Role.Role),
                $"{RoleName(slot.Role.Role)}: the agent reports that its sandbox has access to the network. That is set in the agent's own configuration, not by YAV.",
                NoteLevel.Warning);
        }

        if (verification.Violations.Count > 0)
        {
            state.Violation ??= string.Join(" ", verification.Violations);
            return;
        }

        bool outside;
        lock (context.WritableOutsideSaid)
        {
            outside = effective.Widening is { AdditionalWritableRoots.Count: > 0 } && context.WritableOutsideSaid.Add(slot.Role.Role);
        }

        if (outside)
        {
            // With quality lock on this stopped the run. Where it does not, it is said: a table is not where one learns it.
            context.Publisher.Note(
                Stages.For(slot.Role.Role),
                $"{RoleName(slot.Role.Role)}: the agent reports that it may also write to {string.Join("; ", effective.Widening!.AdditionalWritableRoots)}, "
                + "outside the workspace. That is set in the agent's own configuration, not by YAV. What it writes there is not part of the candidate and is not undone.",
                NoteLevel.Warning);
        }
    }

    /// <summary>The agent never said what is in effect, so what was requested is shown as exactly that.</summary>
    private void RecordUnreported(RunContext context, SessionSlot slot)
    {
        var now = _services.Clock.GetUtcNow();
        const string Source = "the agent does not report its settings";
        Record(context, slot.Role.Role,
        [
            new(slot.Role.Role, ProfileSettings.Model, slot.Role.ModelId, null, Yav.Core.VerificationStatus.RequestedUnverified, Source, now),
            new(slot.Role.Role, ProfileSettings.Effort, slot.Role.RequestedEffort, null, Yav.Core.VerificationStatus.RequestedUnverified, Source, now),
        ]);
    }

    private void Record(RunContext context, AgentRole role, IReadOnlyList<Yav.Core.Profiles.ProfileConfirmation> confirmations)
    {
        lock (context.Confirmations)
        {
            context.Confirmations.RemoveAll(c => c.Role == role);
            context.Confirmations.AddRange(confirmations);
        }

        foreach (var confirmation in confirmations)
        {
            _services.Store.SaveConfirmation(context.RunId, confirmation);
            context.Publisher.Publish(new SettingConfirmed(context.RunId, confirmation.ObservedAt, confirmation));
        }
    }

    private void BeginApproval(RunContext context, SessionSlot slot, ApprovalRequest request, TurnState state)
    {
        var role = slot.Role.Role;
        var now = _services.Clock.GetUtcNow();

        if (role == AgentRole.Reviewer || slot.Role.Approvals == ApprovalMode.NeverAsk)
        {
            // The reviewer is never given more access, whoever asks and whatever the reason.
            Answer(context, slot, request, ApprovalDecision.Decline, "policy: the reviewer is read-only", now, state);
            return;
        }

        if (state.Stopping)
        {
            Answer(context, slot, request, ApprovalDecision.Cancel, "policy: the run is stopping", now, state);
            return;
        }

        if (!context.Approvals.CanAsk)
        {
            // Nobody can answer. Access is not granted and the run does not hang.
            lock (context.UnansweredApprovals)
            {
                context.UnansweredApprovals.Add(request);
            }

            state.ApprovalRequired = true;
            Answer(context, slot, request, ApprovalDecision.Cancel, "policy: non-interactive run", now, state);
            return;
        }

        var pending = new PendingApproval(request, new CancellationTokenSource(), now);
        PendingApproval? replaced;
        lock (state.Gate)
        {
            state.Pending.Remove(request.ApprovalId, out replaced);
            state.Pending[request.ApprovalId] = pending;
        }

        // An agent that asks again under a name that is still open means the new request. The old question is taken back.
        replaced?.Cancel.Cancel();

        _transition(context, RunState.AwaitingApproval, request.Title);
        pending.Task = Task.Run(() => AskAsync(context, slot, pending, state));
        lock (state.Gate)
        {
            state.ApprovalTasks.Add(pending.Task);
        }
    }

    private async Task AskAsync(RunContext context, SessionSlot slot, PendingApproval pending, TurnState state)
    {
        var role = slot.Role.Role;
        ApprovalDecision decision;
        var decidedBy = "user";
        using (context.Timing.Start(SpanKind.ApprovalWaiting, Label(pending.Request)))
        {
            try
            {
                decision = await context.Approvals.AskAsync(context.RunId, role, pending.Request, pending.Cancel.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // The agent withdrew the request or the run is stopping. The request no longer exists, so nothing is sent.
                return;
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                decision = ApprovalDecision.Decline;
                decidedBy = "policy: the prompt failed (" + ex.Message + ")";
            }
        }

        bool current;
        bool last;
        lock (state.Gate)
        {
            // An answer to a request that was withdrawn in the meantime is stale and must not reach the agent,
            // also when the agent asked for something else under the same name since.
            current = state.Pending.TryGetValue(pending.Request.ApprovalId, out var open)
                && ReferenceEquals(open, pending)
                && state.Pending.Remove(pending.Request.ApprovalId);
            last = state.Pending.Count == 0;
        }

        if (!current)
        {
            SaveApproval(context, role, pending.Request, "stale", "policy: the request was withdrawn before the answer", pending.RequestedAt);
            return;
        }

        // Waited for here, so that the turn does not end before it is recorded whether the answer arrived.
        var recorded = Answer(context, slot, pending.Request, decision, decidedBy, pending.RequestedAt, state);
        if (last && !state.Stopping)
        {
            _transition(context, context.WorkState, null);
        }

        await recorded.ConfigureAwait(false);
    }

    private void Withdraw(RunContext context, SessionSlot slot, string approvalId, TurnState state)
    {
        PendingApproval? pending;
        bool last;
        lock (state.Gate)
        {
            state.Pending.Remove(approvalId, out pending);
            last = state.Pending.Count == 0;
        }

        if (pending is null)
        {
            return;
        }

        pending.Cancel.Cancel();
        SaveApproval(context, slot.Role.Role, pending.Request, "withdrawn", "agent", pending.RequestedAt);
        if (last && !state.Stopping)
        {
            _transition(context, context.WorkState, null);
        }
    }

    /// <summary>Sends the answer and records it once it is known whether it arrived. The returned task completes then.</summary>
    private Task Answer(
        RunContext context,
        SessionSlot slot,
        ApprovalRequest request,
        ApprovalDecision decision,
        string decidedBy,
        DateTimeOffset requestedAt,
        TurnState state)
    {
        Task sending;
        try
        {
            // Sent without waiting for the reply, so that the event loop keeps reading what the agent says next.
            sending = slot.Session.RespondToApprovalAsync(request.ApprovalId, decision, CancellationToken.None);
        }
        catch (Exception ex) when (ex is AgentException or InvalidOperationException or IOException)
        {
            sending = Task.FromException(ex);
        }

        // Recorded when it is known whether the answer arrived. One that did not arrive allowed nothing, and
        // is not written down as if it had: the agent may have taken its request back in the same moment.
        var recording = sending.ContinueWith(
            sent =>
            {
                try
                {
                    if (sent.IsCompletedSuccessfully)
                    {
                        SaveApproval(context, slot.Role.Role, request, decision.ToString(), decidedBy, requestedAt);
                        return;
                    }

                    var why = sent.IsCanceled ? "sending it was cancelled" : sent.Exception!.GetBaseException().Message;
                    SaveApproval(
                        context, slot.Role.Role, request, NotDelivered,
                        $"{decidedBy} answered {decision}; the answer did not reach the agent: {why}", requestedAt);
                }
                catch (Exception ex) when (ex is not OutOfMemoryException)
                {
                    // What is recorded is a record; it must not end the turn. It is said instead.
                    context.Publisher.Note(Stages.Approval, $"The answer to {RunPublisher.Quoted(request.Title)} could not be recorded: {ex.Message}", NoteLevel.Warning);
                }
            },
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
        lock (state.Gate)
        {
            state.ApprovalTasks.Add(recording);
        }

        return recording;
    }

    private void SaveApproval(RunContext context, AgentRole role, ApprovalRequest request, string decision, string decidedBy, DateTimeOffset requestedAt)
    {
        _services.Store.SaveApproval(new ApprovalRecord(
            context.RunId, request.ApprovalId, role, request.Kind, request.Title, request.Command, decision, decidedBy, requestedAt,
            _services.Clock.GetUtcNow()));
        // The title is what the agent wrote. In a line of YAV it stands in quotes.
        context.Publisher.Note(
            Stages.Approval,
            $"{RunPublisher.Quoted(request.Title)}: {decision} ({decidedBy})",
            decision is "Accept" or "AcceptForSession" ? NoteLevel.Info : NoteLevel.Warning);
    }

    private static void CancelPending(TurnState state)
    {
        List<PendingApproval> pending;
        lock (state.Gate)
        {
            pending = [.. state.Pending.Values];
            state.Pending.Clear();
        }

        foreach (var approval in pending)
        {
            approval.Cancel.Cancel();
        }
    }

    private static async Task SettleApprovalsAsync(RunContext context, TurnState state)
    {
        _ = context;
        CancelPending(state);
        Task[] tasks;
        lock (state.Gate)
        {
            tasks = [.. state.ApprovalTasks];
        }

        try
        {
            // A prompt that ignores cancellation must not keep the run from ending.
            await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is TimeoutException or OperationCanceledException)
        {
            _ = ex;
        }
    }

    /// <summary>What was asked for, on one line and short enough for a timing report.</summary>
    private static string Label(ApprovalRequest request)
    {
        var text = Yav.Core.Text.TerminalSanitizer.CleanSingleLine(request.Command ?? request.Title);
        return text.Length <= 120 ? text : string.Concat(text.AsSpan(0, 119), "…");
    }

    private static string RoleName(AgentRole role) => role == AgentRole.Implementer ? "Model A" : "Model B";
}
