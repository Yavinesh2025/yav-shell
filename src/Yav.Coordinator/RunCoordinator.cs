using System.Collections.Concurrent;
using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Core.Templates;
using Yav.Core.Timing;

namespace Yav.Coordinator;

/// <summary>
/// Owns the task pipeline: prepare, implement, freeze, review and test side by side, decide, repair or
/// deliver. It talks to the other modules through the ports in Yav.Core only.
/// </summary>
public sealed partial class RunCoordinator : IAsyncDisposable
{
    private readonly CoordinatorServices _services;
    private readonly CoordinatorOptions _options;
    private readonly TurnDriver _turns;
    private readonly ConcurrentDictionary<string, TaskSessions> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _activeByProject = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RunContext> _activeRuns = new(StringComparer.Ordinal);

    public RunCoordinator(CoordinatorServices services, CoordinatorOptions? options = null)
    {
        _services = services;
        _options = options ?? new CoordinatorOptions();
        _turns = new TurnDriver(services, _options, Transition);
        Catalog = new AdapterCatalog(services.Adapters, services.Trust, services.Clock, _options.SnapshotMaxAge);
    }

    public AdapterCatalog Catalog { get; }

    private TimeProvider Clock => _services.Clock;

    /// <summary>The run that is active for the project, if any. One project has one coding writer at a time.</summary>
    public string? ActiveRunFor(string projectPath) =>
        _activeByProject.TryGetValue(Path.GetFullPath(projectPath), out var runId) ? runId : null;

    /// <summary>
    /// Performs one request from preparation to a candidate that is ready to apply, or to the point where
    /// the user is needed. The project itself is never written here.
    /// </summary>
    public async Task<RunOutcome> RunAsync(
        RunRequest request,
        RunConfiguration configuration,
        IRunObserver observer,
        IApprovalBroker approvals,
        CancellationToken cancellationToken)
    {
        var runId = Ids.NewRunId(Clock);
        var publisher = new RunPublisher(runId, observer, _services.Store, Clock);
        var timing = new TimingRecorder(Clock, runId);
        var project = Path.GetFullPath(request.ProjectPath);

        if (!_activeByProject.TryAdd(project, runId))
        {
            var reason = $"Run {_activeByProject.GetValueOrDefault(project)} is active for this project. One project has one coding writer at a time; queue the request instead.";
            publisher.Note(Stages.Blocked, reason, NoteLevel.Error);
            return new RunOutcome(runId, RunOutcomeKind.Blocked, RunState.Blocked, reason, null, null, null, []);
        }

        RunContext? context = null;
        try
        {
            Preflight preflight;
            var startedAt = Clock.GetUtcNow();
            var startTimestamp = Clock.GetTimestamp();
            using (timing.Start(SpanKind.LocalPreparation, "preflight"))
            {
                publisher.Note(Stages.Prepare, "Checking the project, the agents and the run configuration");
                preflight = await PreflightAsync(request, configuration, cancellationToken, timing: timing).ConfigureAwait(false);
            }

            if (!preflight.CanRun)
            {
                foreach (var problem in preflight.Blocking)
                {
                    publisher.Note(Stages.Blocked, WithRemedy(problem), NoteLevel.Error);
                }

                return new RunOutcome(
                    runId, RunOutcomeKind.Blocked, RunState.Blocked,
                    string.Join(" ", preflight.Blocking.Select(p => p.Message)), null, null, null, [])
                {
                    Problems = preflight.Problems,
                };
            }

            context = CreateRun(runId, request, configuration, preflight, publisher, approvals, timing, startedAt, startTimestamp);
            _activeRuns[runId] = context;
            timing.SpanCompleted += span => SaveSpan(context, span);

            // What was measured before there was a run to keep it with.
            foreach (var earlier in timing.Completed)
            {
                SaveSpan(context, earlier);
            }

            foreach (var problem in preflight.Problems.Where(p => p.Severity != ProblemSeverity.Blocking))
            {
                publisher.Note(Stages.Prepare, WithRemedy(problem), problem.Severity == ProblemSeverity.Warning ? NoteLevel.Warning : NoteLevel.Info);
            }

            var halt = await PrepareWorkspaceAsync(context, preflight, cancellationToken).ConfigureAwait(false)
                ?? await ProduceCandidateAsync(context, cancellationToken).ConfigureAwait(false)
                ?? await CheckAndRepairAsync(context, cancellationToken).ConfigureAwait(false);

            return Finish(context, halt);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return context is null
                ? new RunOutcome(runId, RunOutcomeKind.Interrupted, RunState.Interrupted, "Stopped before the run started.", null, null, null, [])
                : Finish(context, Stopped());
        }
        catch (AgentException ex) when (context is not null)
        {
            return Finish(context, ex.Refused
                ? new Halt(RunState.Blocked, RunOutcomeKind.Blocked, ex.Message, Level: NoteLevel.Error)
                : new Halt(RunState.Failed, RunOutcomeKind.Failed, "The agent could not be used: " + ex.Message, Level: NoteLevel.Error));
        }
        catch (Exception ex) when (context is not null && ex is not OutOfMemoryException)
        {
            return Finish(context, new Halt(RunState.Failed, RunOutcomeKind.Failed, $"{ex.GetType().Name}: {ex.Message}", Level: NoteLevel.Error));
        }
        finally
        {
            if (context is not null)
            {
                SaveUsage(context);
                _activeRuns.TryRemove(runId, out _);
            }

            _activeByProject.TryRemove(new KeyValuePair<string, string>(project, runId));
        }
    }

    /// <summary>
    /// Adds a request to the implementation turn that is running, because the user chose to. What was
    /// added is a requirement of the task from then on: both models are given it, and nothing that was
    /// checked before counts for it. False when the agent cannot take input during a turn or is not
    /// implementing; nothing is recorded then.
    /// </summary>
    public async Task<bool> SteerAsync(string runId, string text, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(text) || !_activeRuns.TryGetValue(runId, out var context))
        {
            return false;
        }

        await context.RequirementsChange.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (context.State is not (RunState.Implementing or RunState.Repairing)
                || !_sessions.TryGetValue(context.TaskId, out var sessions)
                || sessions.Implementer is not { Lost: false } slot)
            {
                return false;
            }

            var adapter = _services.Adapters.GetValueOrDefault(slot.Role.AdapterId);
            if (adapter is null || !adapter.Capabilities.Has(AdapterFeatures.Steering))
            {
                return false;
            }

            if (!await slot.Session.SteerAsync(text, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }

            var requirement = context.AddRequirement(text, Clock.GetUtcNow());
            _services.Store.SaveRequirement(context.TaskId, requirement);
            _services.Store.UpdateRun(runId, run => run with { AcceptanceVersion = requirement.Version });
            context.Publisher.Note(
                Stages.CodeA,
                $"Requirement {requirement.Version} was added to the running turn. The candidate is reviewed and tested against all {requirement.Version} requirements; "
                + "what was checked before does not count for it.",
                NoteLevel.Warning);
            return true;
        }
        finally
        {
            context.RequirementsChange.Release();
        }
    }

    private static Halt Stopped() =>
        new(RunState.Interrupted, RunOutcomeKind.Interrupted, "Stopped by you. The isolated workspace and the conversations are kept; continue with /resume.");

    private static string WithRemedy(ProfileProblem problem) =>
        problem.Remedy is null ? problem.Message : problem.Message + " " + problem.Remedy;

    private RunContext CreateRun(
        string runId,
        RunRequest request,
        RunConfiguration configuration,
        Preflight preflight,
        RunPublisher publisher,
        IApprovalBroker approvals,
        TimingRecorder timing,
        DateTimeOffset startedAt,
        long startTimestamp)
    {
        var store = _services.Store;
        var profile = preflight.Resolution.Profile!;
        var now = Clock.GetUtcNow();

        var existing = request.TaskId is null ? null : store.FindTask(request.TaskId);
        if (existing is not null && !string.Equals(Path.GetFullPath(existing.ProjectPath), preflight.ProjectPath, StringComparison.OrdinalIgnoreCase))
        {
            // A task belongs to one project. A request for another project starts a task of its own.
            existing = null;
        }

        TaskContext task;
        List<Requirement> requirements;
        var sequence = 1;
        if (existing is null)
        {
            task = new TaskContext(Ids.NewId("t"), preflight.ProjectPath, now, null, null, null, null, null);
            store.SaveTask(task);
            requirements = [];
        }
        else
        {
            task = existing;
            requirements = [.. store.GetRequirements(task.TaskId)];
            var earlier = store.ListRuns(preflight.ProjectPath, 50).Where(r => r.TaskId == task.TaskId).ToList();
            sequence = earlier.Count == 0 ? 1 : earlier.Max(r => r.Sequence) + 1;
            foreach (var run in earlier.Where(r => r.State == RunState.ReadyToApply))
            {
                // The earlier candidate lives on as part of the new one; it can no longer be applied by itself.
                store.Transition(run.RunId, RunState.ReadyToApply, RunState.Blocked, $"Superseded by follow-up run {runId}, which continues from its changes.");
            }
        }

        var requirement = new Requirement(requirements.Count + 1, request.Text, now, request.Attachments);
        store.SaveRequirement(task.TaskId, requirement);
        requirements.Add(requirement);

        var profileHash = profile.ComputeHash();
        store.CreateRun(
            new RunRecord(
                runId, task.TaskId, sequence, request.MechanicalEdit is null ? RunKind.CodingTask : RunKind.MechanicalEdit, preflight.ProjectPath,
                request.Text, requirements.Count, profileHash, RunState.Preparing, RunDisposition.Pending, null, 0, null, now, now,
                _options.OwnerProcessId, _services.YavVersion),
            profile);

        publisher.EnablePersistence();
        var context = new RunContext
        {
            RunId = runId,
            TaskId = task.TaskId,
            Request = request,
            Configuration = configuration,
            Profile = profile,
            ProfileHash = profileHash,
            Project = preflight.Configuration.Effective,
            GateConfigurationHash = preflight.Configuration.TrustedHash,
            Publisher = publisher,
            Approvals = approvals,
            Timing = timing,
            Requirements = requirements,
            IsFollowUp = existing is not null,
            LocalEdit = request.MechanicalEdit is not null,
            StartTimestamp = startTimestamp,
            StartedAt = startedAt,
        };

        foreach (var role in new[] { AgentRole.Implementer, AgentRole.Reviewer })
        {
            var roleProfile = profile.For(role);
            var stored = existing is null ? null : store.FindSession(task.TaskId, role);
            var continues = stored is not null && string.Equals(stored.AdapterId, roleProfile.AdapterId, StringComparison.OrdinalIgnoreCase);
            context.Usage[role] = new RoleUsage(
                roleProfile.AdapterId,
                roleProfile.AccountRouteLabel,
                new SessionUsageTracker(continues ? stored!.CumulativeTokens : null, continues ? stored!.CumulativeCostUsd : null, sessionIsNew: !continues));
        }

        publisher.Publish(new RunStarted(runId, now, task.TaskId, request.Text, profile, profileHash, context.IsFollowUp));
        return context;
    }

    /// <summary>Model A implements, or the explicit local edit is performed. Null means a candidate can now be frozen.</summary>
    private async Task<Halt?> ProduceCandidateAsync(RunContext context, CancellationToken stop)
    {
        if (context.Request.MechanicalEdit is { } edit)
        {
            return await PerformMechanicalEditAsync(context, edit, stop).ConfigureAwait(false);
        }

        var workspace = context.RequireWorkspace();
        var references = context.Profile.Policy.Optimization
            ? StartingReferences.Find(context.Request.Text, workspace.AgentDirectory, Clock.GetUtcNow())
            : [];
        var prompt = PromptBuilder.ImplementationRequest(context.Requirements, references, context.Project.RequiredGates.ToList()) + AttachmentNote(context);
        context.Publisher.Note(
            Stages.CodeA,
            $"{context.Profile.Implementer.ModelDisplayName ?? context.Profile.Implementer.ModelId} is implementing the task"
            + (context.Profile.Implementer.RequestedEffort.Length > 0 ? $" (effort {context.Profile.Implementer.RequestedEffort})" : string.Empty));
        return await ImplementAsync(context, prompt, SpanKind.Implementation, quotesOutput: false, stop).ConfigureAwait(false);
    }

    private static string AttachmentNote(RunContext context)
    {
        var attachments = context.Requirements[^1].Attachments;
        if (attachments.Count == 0)
        {
            return string.Empty;
        }

        return "\n\n## Attached files\nThe user attached these files as reference data:\n" + string.Join("\n", attachments.Select(a => "- " + a));
    }

    private async Task<Halt?> PerformMechanicalEditAsync(RunContext context, MechanicalEditRequest edit, CancellationToken stop)
    {
        var workspace = context.RequireWorkspace();
        Transition(context, RunState.Implementing, "local edit");
        using var span = context.Timing.Start(SpanKind.LocalCommand, "mechanical edit");

        // The path is the one the user wrote: it starts at the directory that was opened, which need not be
        // the root of what the workspace holds, and it does not leave that directory.
        var located = WorkspacePaths.Locate(edit.Path, workspace);
        var preview = located is null
            ? new MechanicalEditPreview(false, $"'{edit.Path}' is not a path inside the project.", 0, [], string.Empty)
            : await _services.Workspaces.PreviewMechanicalEditAsync(workspace, edit with { Path = located }, stop).ConfigureAwait(false);
        if (!preview.Valid || located is null)
        {
            // The file is named the way the user named it, not by its place in the workspace.
            var problem = located is null ? preview.Problem : preview.Problem?.Replace($"'{located}'", $"'{edit.Path}'", StringComparison.Ordinal);
            return new Halt(
                RunState.Blocked, RunOutcomeKind.Blocked,
                $"The edit was not performed: {problem} An ambiguous change is a task for Model A; send it as a request instead.");
        }

        await _services.Workspaces.ApplyMechanicalEditAsync(workspace, edit with { Path = located }, stop).ConfigureAwait(false);
        context.FirstActionAt ??= Clock.GetUtcNow();
        context.ImplementerSummary = null;
        var lines = (preview.MatchLines.Count == 1 ? "line " : "lines ") + string.Join(", ", preview.MatchLines);
        context.Publisher.Note(
            Stages.Local,
            $"Replaced {preview.MatchCount} occurrence(s) in {edit.Path.Replace('\\', '/')} ({lines}) locally. "
            + "Model A was not called; review and checks follow as for any change.");
        return null;
    }

    /// <summary>One turn of Model A. Null means the turn completed and the workspace can be frozen.</summary>
    /// <param name="quotesOutput">The prompt quotes what an agent or a tool wrote, which nobody checked.</param>
    private async Task<Halt?> ImplementAsync(RunContext context, string prompt, SpanKind kind, bool quotesOutput, CancellationToken stop)
    {
        context.WorkState = kind == SpanKind.Repair ? RunState.Repairing : RunState.Implementing;
        Transition(context, context.WorkState, null);

        if (LimitReached(context) is { } limit)
        {
            return new Halt(RunState.Blocked, RunOutcomeKind.Blocked, limit);
        }

        var slot = await OpenSessionAsync(context, AgentRole.Implementer, stop).ConfigureAwait(false);
        TurnResult result;
        using (context.Timing.Start(kind, kind == SpanKind.Repair ? $"repair {context.RepairCyclesUsed}" : "implementation"))
        {
            result = await _turns.RunAsync(context, slot, new TurnRequest(prompt, null, [], QuotesOutput: quotesOutput), stop).ConfigureAwait(false);
        }

        SaveSession(context, slot);
        var halt = Interpret(context, AgentRole.Implementer, result);
        if (halt is null)
        {
            context.ImplementerSummary = result.FinalMessage;
        }

        return halt;
    }

    /// <summary>Turns the way a turn ended into the state the run stops in, or null when the turn completed.</summary>
    private Halt? Interpret(RunContext context, AgentRole role, TurnResult result)
    {
        var name = role == AgentRole.Implementer ? "Model A" : "Model B";
        // An agent that ended as well is dealt with further down: what it had done by then is not known.
        if (result.PolicyViolation is { } violation && !result.SessionLost)
        {
            var spent = result.NotStarted ? " No request was sent, so no usage was spent on it." : " The turn was stopped.";
            return new Halt(RunState.Blocked, RunOutcomeKind.Blocked, violation + spent, Level: NoteLevel.Error);
        }

        if (result.StoppedByUser)
        {
            return Stopped();
        }

        if (result.ApprovalRequired)
        {
            List<ApprovalRequest> unanswered;
            lock (context.UnansweredApprovals)
            {
                unanswered = [.. context.UnansweredApprovals];
            }

            var what = unanswered.Count > 0 ? string.Join("; ", unanswered.Select(a => a.Command ?? a.Title)) : result.ErrorMessage ?? "an action outside its permissions";
            return new Halt(
                RunState.Blocked, RunOutcomeKind.ApprovalRequired,
                $"Approval Required: {name} asked to run {what}. Nobody could answer in this mode, so access was not granted. "
                + "Run the task interactively to decide.");
        }

        if (result.SessionLost)
        {
            return new Halt(
                RunState.NeedsReconciliation, RunOutcomeKind.NeedsReconciliation,
                $"{name}: {result.ErrorMessage ?? "the agent ended before the turn did."} What it had already changed is unknown until the workspace is checked. "
                + "Nothing is sent again automatically; use /resume to check the workspace.",
                Level: NoteLevel.Error);
        }

        switch (result.Outcome)
        {
            case TurnOutcome.Completed:
                return null;

            case TurnOutcome.RateLimited:
            case TurnOutcome.UsageLimitReached:
            {
                var resets = context.RateLimits?.Primary?.ResetsAt;
                var when = resets is null ? string.Empty : $" The provider reports a reset at {resets.Value.ToLocalTime():HH:mm}.";
                var what = result.Outcome == TurnOutcome.UsageLimitReached ? "the usage limit of the account was reached" : "the provider's rate limit was reached";
                return new Halt(
                    RunState.RateLimited, RunOutcomeKind.RateLimited,
                    $"{name}: {what}.{when} Nothing was bought or switched to another route. Continue with /resume when the limit allows.");
            }

            case TurnOutcome.Interrupted:
                return new Halt(RunState.Interrupted, RunOutcomeKind.Interrupted, $"{name}: the turn was interrupted. Continue with /resume.");

            default:
                return new Halt(
                    RunState.Failed, RunOutcomeKind.Failed,
                    $"{name}: {result.ErrorMessage ?? "the turn failed without a message."}",
                    Level: NoteLevel.Error);
        }
    }

    /// <summary>The first limit the run has reached, or null. Limits stop YAV from starting turns; a turn in flight is not cut off.</summary>
    private string? LimitReached(RunContext context)
    {
        var limits = context.Configuration.Limits;
        if (limits.MaxElapsed is { } elapsedLimit)
        {
            var elapsed = Clock.GetElapsedTime(context.StartTimestamp);
            if (elapsed >= elapsedLimit)
            {
                return $"The elapsed-time limit of {Describe(elapsedLimit)} was reached after {Describe(elapsed)}. No further turn is started; work so far is kept. "
                    + "Change the limit with /limits.";
            }
        }

        if (limits.MaxRunTokens is { } tokenLimit)
        {
            long used = 0;
            var known = false;
            foreach (var usage in context.Usage.Values)
            {
                if (usage.Tracker.RunTokens.Total is { } total)
                {
                    used += total;
                    known = true;
                }
            }

            if (known && used >= tokenLimit)
            {
                return $"The token limit of {tokenLimit:N0} was reached: {used:N0} tokens were reported so far. No further turn is started. "
                    + "Usage that the provider reports late is not included. Change the limit with /limits.";
            }
        }

        if (limits.StopAtRateLimitPercent is { } percent && MostUsed(context) is { } most && most.UsedPercent >= percent)
        {
            var which = most.Name is null ? "the rate-limit window" : $"the rate-limit window '{most.Name}'";
            return $"The provider reports {most.UsedPercent}% of {which} used, which reaches your limit of {percent}%. No further turn is started. "
                + "Change the limit with /limits.";
        }

        return null;
    }

    /// <summary>The window that is used most, of every limit a provider reported in this run, whichever it reported last.</summary>
    private static (int UsedPercent, string? Name)? MostUsed(RunContext context)
    {
        List<RateLimitSnapshot> known;
        lock (context.RateLimitsByName)
        {
            known = [.. context.RateLimitsByName.Values];
        }

        (int UsedPercent, string? Name)? most = null;
        foreach (var snapshot in known)
        {
            foreach (var window in new[] { snapshot.Primary, snapshot.Secondary })
            {
                if (window?.UsedPercent is { } used && (most is null || used > most.Value.UsedPercent))
                {
                    most = (used, snapshot.LimitName);
                }
            }
        }

        return most;
    }

    private static string Describe(TimeSpan time) =>
        time.TotalHours >= 1 ? $"{(int)time.TotalHours}h {time.Minutes}m" : time.TotalMinutes >= 1 ? $"{(int)time.TotalMinutes}m {time.Seconds}s" : $"{time.TotalSeconds:0.#}s";

    private async Task<SessionSlot> OpenSessionAsync(RunContext context, AgentRole role, CancellationToken stop)
    {
        var workspace = context.RequireWorkspace();
        var roleProfile = context.Profile.For(role);
        var sessions = _sessions.GetOrAdd(context.TaskId, _ => new TaskSessions());
        var current = sessions.For(role);
        if (current is not null && current.Matches(roleProfile, workspace.AgentDirectory))
        {
            return current;
        }

        if (current is not null)
        {
            // The settings changed since the conversation was opened, or it ended. It is never reused with other settings.
            sessions.Set(role, null);
            await CloseAsync(current).ConfigureAwait(false);
        }

        var adapter = _services.Adapters.GetValueOrDefault(roleProfile.AdapterId)
            ?? throw new AgentException($"The adapter '{roleProfile.AdapterId}' is not available.");
        var request = new SessionRequest(
            role,
            roleProfile.ModelId,
            roleProfile.RequestedEffort,
            workspace.AgentDirectory,
            roleProfile.Sandbox,
            roleProfile.Approvals,
            RuntimeTemplates.For(role).Text,
            context.Profile.ProjectTrusted,
            roleProfile.ServiceTier,
            // The complete diff and the output of checks live here, outside the source tree.
            [workspace.EvidencePath]);

        IAgentSession? session = null;
        var resumed = false;
        var stored = context.IsFollowUp ? _services.Store.FindSession(context.TaskId, role) : null;
        using (context.Timing.Start(SpanKind.AgentInitialization, role == AgentRole.Implementer ? "Model A session" : "Model B session"))
        {
            if (stored is not null
                && string.Equals(stored.AdapterId, adapter.Id, StringComparison.OrdinalIgnoreCase)
                && adapter.Capabilities.Has(AdapterFeatures.ResumeSession))
            {
                try
                {
                    session = await adapter.ResumeSessionAsync(stored.SessionId, request, stop).ConfigureAwait(false);
                    resumed = true;
                }
                catch (AgentException ex) when (!ex.Refused)
                {
                    context.Publisher.Note(
                        Stages.For(role),
                        $"The earlier conversation {stored.SessionId} could not be resumed ({ex.Message}). A new one is started; it does not know the earlier turns.",
                        NoteLevel.Warning);
                }
            }

            session ??= await adapter.StartSessionAsync(request, stop).ConfigureAwait(false);
        }

        if (!resumed)
        {
            // Nothing of an earlier conversation is counted for this one.
            context.Usage[role].Tracker = new SessionUsageTracker(null, null, sessionIsNew: true);
        }

        var slot = new SessionSlot(session, roleProfile, workspace.AgentDirectory, resumed);
        context.Usage[role].SessionId = session.SessionId;
        sessions.Set(role, slot);
        return slot;
    }

    private void SaveSession(RunContext context, SessionSlot slot)
    {
        var role = slot.Role.Role;
        var usage = context.Usage[role];
        var sessionId = slot.Session.SessionId ?? usage.SessionId;
        if (string.IsNullOrEmpty(sessionId))
        {
            return;
        }

        usage.SessionId = sessionId;
        var cumulative = usage.Tracker.CumulativeForStorage;
        _services.Store.SaveSession(new SessionRecord(
            context.TaskId, role, slot.Role.AdapterId, sessionId, usage.Model ?? slot.Role.ModelId, cumulative?.Tokens, cumulative?.Cost, Clock.GetUtcNow()));

        var task = _services.Store.FindTask(context.TaskId);
        if (task is not null)
        {
            _services.Store.SaveTask(role == AgentRole.Implementer
                ? task with { ImplementerSessionId = sessionId, ImplementerAdapterId = slot.Role.AdapterId }
                : task with { ReviewerSessionId = sessionId, ReviewerAdapterId = slot.Role.AdapterId });
        }
    }

    private void SaveUsage(RunContext context)
    {
        foreach (var (role, usage) in context.Usage)
        {
            if (usage.Turns == 0)
            {
                continue;
            }

            try
            {
                var latest = usage.Tracker.Latest;
                _services.Store.SaveUsage(new UsageRecord(
                    context.RunId,
                    role,
                    usage.AdapterId,
                    usage.SessionId,
                    usage.Model,
                    usage.Tracker.RunTokens,
                    usage.Tracker.BaselineKnown,
                    usage.Tracker.RunCostUsd,
                    usage.Tracker.RunCostUsd is null ? ValueProvenance.Unavailable : latest?.CostProvenance ?? ValueProvenance.Reported,
                    usage.Turns,
                    usage.Retries,
                    usage.BillingRoute,
                    latest?.Source ?? "not reported by the agent",
                    latest?.ObservedAt ?? Clock.GetUtcNow()));
            }
            catch (Exception ex) when (ex is not OutOfMemoryException)
            {
                context.Publisher.Note(Stages.Local, "Usage could not be recorded: " + ex.Message, NoteLevel.Warning);
            }
        }
    }

    private void SaveSpan(RunContext context, TimingSpan span)
    {
        try
        {
            _services.Store.SaveSpan(span);
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _ = context;
            _ = ex;
        }
    }

    private void Transition(RunContext context, RunState to, string? reason)
    {
        lock (context.StateGate)
        {
            var from = context.State;
            if (from == to)
            {
                return;
            }

            _services.Store.Transition(context.RunId, from, to, reason);
            context.State = to;
            context.Publisher.Publish(new StateChanged(context.RunId, Clock.GetUtcNow(), from, to, reason));
        }
    }

    private RunOutcome Finish(RunContext context, Halt halt)
    {
        try
        {
            Transition(context, halt.State, halt.Reason);
        }
        catch (InvalidRunTransitionException ex)
        {
            context.Publisher.Note(Stages.Local, ex.Message, NoteLevel.Warning);
        }

        var disposition = halt.Disposition ?? RunDisposition.Pending;
        if (halt.Disposition is not null)
        {
            _services.Store.UpdateRun(context.RunId, run => run with { Disposition = disposition });
        }

        var (stage, message, level) = halt.State switch
        {
            RunState.ReadyToApply => (Stages.Ready, "Changes available for inspection (/diff) and application (/apply)", NoteLevel.Success),
            RunState.Completed when disposition == RunDisposition.NoChanges => (Stages.Done, "Answered. No source was changed, so there is nothing to apply.", NoteLevel.Success),
            RunState.Completed => (Stages.Done, halt.Reason ?? "Completed", NoteLevel.Success),
            _ => (RunPublisher.StageFor(halt.State), halt.Reason ?? RunStateMachine.Display(halt.State), halt.Level),
        };
        context.Publisher.Note(stage, message, level);

        List<ApprovalRequest> unanswered;
        lock (context.UnansweredApprovals)
        {
            unanswered = [.. context.UnansweredApprovals];
        }

        var finalMessage = halt.FinalMessage ?? context.ImplementerSummary;
        context.Publisher.Publish(new RunFinished(context.RunId, Clock.GetUtcNow(), context.State, disposition, halt.Reason, finalMessage));
        return new RunOutcome(context.RunId, halt.Kind, context.State, halt.Reason, context.Candidate, halt.Decision, finalMessage, unanswered)
        {
            TaskId = context.TaskId,
        };
    }

    private async Task CloseAsync(SessionSlot slot)
    {
        try
        {
            await slot.Session.ShutdownAsync(_options.ShutdownGrace, CancellationToken.None).ConfigureAwait(false);
            await slot.Session.DisposeAsync().ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is AgentException or InvalidOperationException or IOException or OperationCanceledException)
        {
            // Its processes belong to a job object and end with it.
            _ = ex;
        }
    }

    /// <summary>Ends the conversations of a task, for example when an unrelated task starts.</summary>
    public async Task CloseTaskAsync(string taskId)
    {
        if (_sessions.TryRemove(taskId, out var sessions))
        {
            foreach (var slot in new[] { sessions.Implementer, sessions.Reviewer })
            {
                if (slot is not null)
                {
                    await CloseAsync(slot).ConfigureAwait(false);
                }
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var taskId in _sessions.Keys.ToArray())
        {
            await CloseTaskAsync(taskId).ConfigureAwait(false);
        }
    }
}
