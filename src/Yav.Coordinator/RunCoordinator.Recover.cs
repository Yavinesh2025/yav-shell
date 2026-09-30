using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Runs;
using Yav.Core.Templates;
using Yav.Core.Timing;

namespace Yav.Coordinator;

public enum RecheckScope
{
    /// <summary>Review and required checks.</summary>
    All,
    Review,
    Tests,
}

public sealed partial class RunCoordinator
{
    private sealed record Rehydrated(RunContext? Context, string? Problem);

    /// <summary>
    /// Looks for what a crash left behind: applies that were interrupted, and runs whose process is gone.
    /// Interrupted applies are finished or rolled back. Runs are only examined and marked; nothing is sent
    /// to an agent.
    /// </summary>
    public async Task<ReconciliationReport> ReconcileAsync(CancellationToken cancellationToken)
    {
        var journals = await _services.Workspaces.ReconcileJournalsAsync(cancellationToken).ConfigureAwait(false);
        var alive = _options.ProcessIsAlive ?? (_ => true);
        var before = _services.Store.ListRuns(null, 1000).ToDictionary(r => r.RunId, r => r.State, StringComparer.Ordinal);
        var orphaned = _services.Store.MarkOrphanedRuns(pid => pid == _options.OwnerProcessId ? true : alive(pid));

        var findings = new List<ReconciliationFinding>();
        foreach (var run in orphaned)
        {
            var finding = await ExamineAsync(run, cancellationToken).ConfigureAwait(false);
            findings.Add(finding with { StateBefore = before.GetValueOrDefault(run.RunId, run.State) });
        }

        return new ReconciliationReport(findings, journals);
    }

    /// <summary>What can be said about a run that stopped without finishing, from the workspace and the provider.</summary>
    public async Task<ReconciliationFinding> ExamineAsync(RunRecord run, CancellationToken cancellationToken)
    {
        var observations = new List<string>();
        var located = await LocateAsync(run, cancellationToken).ConfigureAwait(false);
        if (located.Workspace is not { } workspace)
        {
            observations.Add("The isolated workspace no longer exists.");
            return new ReconciliationFinding(run.RunId, run.ProjectPath, run.RequestText, run.State, ReconciliationAdvice.DiscardOnly, observations);
        }

        var fingerprint = await _services.Workspaces.FingerprintAsync(workspace, cancellationToken).ConfigureAwait(false);
        var candidate = located.Candidate;
        var unchecked_ = candidate is null
            ? !string.Equals(fingerprint, workspace.BaselineFingerprint, StringComparison.Ordinal)
            : !string.Equals(fingerprint, candidate.Fingerprint, StringComparison.Ordinal);

        if (candidate is null)
        {
            observations.Add(unchecked_
                ? "The workspace contains changes that were never frozen as a candidate."
                : "The workspace is unchanged: nothing had been written when the run stopped.");
        }
        else
        {
            observations.Add(unchecked_
                ? $"The workspace changed after candidate {candidate.ShortFingerprint} was frozen."
                : $"The workspace still holds exactly candidate {candidate.ShortFingerprint}.");
        }

        var probe = await ProbeImplementerAsync(located.Task!, observations, cancellationToken).ConfigureAwait(false);
        var advice = probe switch
        {
            SessionProbeState.Active => ReconciliationAdvice.WaitForProvider,
            _ when candidate is not null && !unchecked_ => ReconciliationAdvice.Recheck,
            SessionProbeState.LastTurnCompleted when unchecked_ => ReconciliationAdvice.CheckWorkspace,
            _ when unchecked_ && probe is SessionProbeState.Unsupported or SessionProbeState.NotFound => ReconciliationAdvice.CheckWorkspace,
            _ => ReconciliationAdvice.ContinueImplementation,
        };

        return new ReconciliationFinding(run.RunId, run.ProjectPath, run.RequestText, run.State, advice, observations);
    }

    private async Task<SessionProbeState> ProbeImplementerAsync(TaskContext task, List<string> observations, CancellationToken cancellationToken)
    {
        if (task.ImplementerSessionId is null || task.ImplementerAdapterId is null
            || !_services.Adapters.TryGetValue(task.ImplementerAdapterId, out var adapter))
        {
            observations.Add("No conversation of Model A is recorded for the task.");
            return SessionProbeState.NotFound;
        }

        try
        {
            var probe = await adapter.ProbeSessionAsync(task.ImplementerSessionId, cancellationToken).ConfigureAwait(false);
            observations.Add(probe.State switch
            {
                SessionProbeState.Active => "The provider reports that Model A's turn is still running.",
                SessionProbeState.LastTurnCompleted => "The provider reports that Model A's last turn completed.",
                SessionProbeState.LastTurnInterrupted => "The provider reports that Model A's last turn was interrupted.",
                SessionProbeState.LastTurnFailed => "The provider reports that Model A's last turn failed" + (probe.Detail is null ? "." : ": " + probe.Detail),
                SessionProbeState.NotFound => "The provider no longer knows Model A's conversation.",
                SessionProbeState.Unsupported => "This agent cannot be asked what happened to a conversation without sending a turn.",
                _ => "Model A's conversation is idle.",
            });
            return probe.State;
        }
        catch (AgentException ex)
        {
            observations.Add("The provider could not be asked about Model A's conversation: " + ex.Message);
            return SessionProbeState.NotFound;
        }
    }

    /// <summary>
    /// Continues a run that stopped before it was ready. What the workspace and the provider say decides
    /// what happens: unchecked changes are checked, and only an unfinished turn is continued, in the same
    /// conversation. A request is never sent again blindly.
    /// </summary>
    public async Task<RunOutcome> ResumeAsync(
        string runId,
        RunConfiguration configuration,
        IRunObserver observer,
        IApprovalBroker approvals,
        CancellationToken cancellationToken)
    {
        var rehydrated = await RehydrateAsync(runId, configuration, observer, approvals, cancellationToken).ConfigureAwait(false);
        if (rehydrated.Context is not { } context)
        {
            return NotPossible(runId, rehydrated.Problem!);
        }

        if (context.State is RunState.Completed or RunState.ReadyToApply || RunStateMachine.IsActive(context.State))
        {
            return NotPossible(runId, context.State switch
            {
                RunState.Completed => $"Run {runId} is completed; there is nothing to resume.",
                RunState.ReadyToApply => $"Run {runId} is ready to apply; there is nothing to resume. Use /apply, or send a follow-up.",
                _ => $"Run {runId} is recorded as {RunStateMachine.Display(context.State)} by a process that is still running.",
            });
        }

        return await RunResumedAsync(context, cancellationToken, async stop =>
        {
            var run = _services.Store.FindRun(runId)!;
            var finding = await ExamineAsync(run, stop).ConfigureAwait(false);
            foreach (var observation in finding.Observations)
            {
                context.Publisher.Note(Stages.Prepare, observation);
            }

            switch (finding.Advice)
            {
                case ReconciliationAdvice.WaitForProvider:
                    return new Halt(
                        context.State, RunOutcomeKind.NeedsReconciliation,
                        "The provider still reports a running turn for this task. It is not started a second time. Try /resume again when it has ended.");

                case ReconciliationAdvice.DiscardOnly:
                    return new Halt(context.State, RunOutcomeKind.Blocked, "The isolated workspace no longer exists. The run can only be discarded.");

                case ReconciliationAdvice.ContinueImplementation:
                {
                    context.Publisher.Note(Stages.CodeA, "Model A's turn did not finish. It is asked to continue in the same conversation.");
                    var prompt = PromptBuilder.ContinuationRequest(context.Requirements, context.Project.RequiredGates.ToList());
                    // It quotes what the user asked for and nothing else, also when the turn was a repair.
                    var kind = context.RepairCyclesUsed > 0 ? SpanKind.Repair : SpanKind.Implementation;
                    if (await ImplementAsync(context, prompt, kind, quotesOutput: false, stop).ConfigureAwait(false) is { } halt)
                    {
                        return halt;
                    }

                    break;
                }

                default:
                    context.Publisher.Note(Stages.Check, "Nothing is sent to Model A. What is in the workspace is checked.");
                    break;
            }

            return await CheckAndRepairAsync(context, stop).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>Runs the review, the required checks or both again for the current state of a run's workspace.</summary>
    public async Task<RunOutcome> RecheckAsync(
        string runId,
        RecheckScope scope,
        RunConfiguration configuration,
        IRunObserver observer,
        IApprovalBroker approvals,
        CancellationToken cancellationToken)
    {
        var rehydrated = await RehydrateAsync(runId, configuration, observer, approvals, cancellationToken).ConfigureAwait(false);
        if (rehydrated.Context is not { } context)
        {
            return NotPossible(runId, rehydrated.Problem!);
        }

        if (context.State == RunState.Completed || RunStateMachine.IsActive(context.State))
        {
            return NotPossible(runId, $"Run {runId} is {RunStateMachine.Display(context.State)}; its candidate cannot be checked again.");
        }

        if (scope is RecheckScope.All or RecheckScope.Review)
        {
            // Asked for explicitly, so an existing review is not reused.
            context.LastReview = null;
        }

        context.ForceGates = scope is RecheckScope.All or RecheckScope.Tests;
        return await RunResumedAsync(context, cancellationToken, stop => CheckAndRepairAsync(context, stop)).ConfigureAwait(false);
    }

    /// <summary>Runs the rest of a stored run with the same bookkeeping as a new one.</summary>
    private async Task<RunOutcome> RunResumedAsync(RunContext context, CancellationToken cancellationToken, Func<CancellationToken, Task<Halt>> work)
    {
        var project = Path.GetFullPath(context.Request.ProjectPath);
        if (!_activeByProject.TryAdd(project, context.RunId))
        {
            return NotPossible(
                context.RunId,
                $"Run {_activeByProject.GetValueOrDefault(project)} is active for this project. One project has one coding writer at a time.");
        }

        _activeRuns[context.RunId] = context;
        context.Timing.SpanCompleted += span => SaveSpan(context, span);
        try
        {
            _services.Store.UpdateRun(context.RunId, run => run with { OwnerProcessId = _options.OwnerProcessId });
            return Finish(context, await work(cancellationToken).ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            return Finish(context, Stopped());
        }
        catch (AgentException ex)
        {
            return Finish(context, ex.Refused
                ? new Halt(RunState.Blocked, RunOutcomeKind.Blocked, ex.Message, Level: NoteLevel.Error)
                : new Halt(RunState.Failed, RunOutcomeKind.Failed, "The agent could not be used: " + ex.Message, Level: NoteLevel.Error));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            return Finish(context, new Halt(RunState.Failed, RunOutcomeKind.Failed, $"{ex.GetType().Name}: {ex.Message}", Level: NoteLevel.Error));
        }
        finally
        {
            SaveUsage(context);
            _activeRuns.TryRemove(context.RunId, out _);
            _activeByProject.TryRemove(new KeyValuePair<string, string>(project, context.RunId));
        }
    }

    /// <summary>Builds the working state of a stored run from what was recorded. The run's profile is the one it started with.</summary>
    private async Task<Rehydrated> RehydrateAsync(
        string runId,
        RunConfiguration configuration,
        IRunObserver observer,
        IApprovalBroker approvals,
        CancellationToken cancellationToken)
    {
        var store = _services.Store;
        var run = store.FindRun(runId);
        if (run is null)
        {
            return new Rehydrated(null, $"Run {runId} does not exist.");
        }

        if (_activeRuns.ContainsKey(runId))
        {
            return new Rehydrated(null, $"Run {runId} is running.");
        }

        var located = await LocateAsync(run, cancellationToken).ConfigureAwait(false);
        if (located.Workspace is null || located.Profile is null)
        {
            return new Rehydrated(null, located.Problem ?? $"Run {runId} cannot be continued.");
        }

        var requirements = store.GetRequirements(run.TaskId);
        if (requirements.Count != run.AcceptanceVersion)
        {
            return new Rehydrated(
                null,
                $"Run {runId} was superseded: the task has {requirements.Count} requirement(s) and this run was started for {run.AcceptanceVersion}. Continue with the newest run of the task.");
        }

        SaveWaiting(run, "until it was continued");
        var state = _services.Validation.LoadConfiguration(run.ProjectPath);
        var publisher = new RunPublisher(runId, observer, store, Clock);
        publisher.EnablePersistence();

        var context = new RunContext
        {
            RunId = runId,
            TaskId = run.TaskId,
            Request = new RunRequest(run.ProjectPath, run.RequestText) { TaskId = run.TaskId, Attachments = requirements[^1].Attachments },
            // Limits are the user's current ones; models, effort and policy stay those the run started with.
            Configuration = configuration with { Policy = located.Profile.Policy },
            Profile = located.Profile,
            ProfileHash = run.ProfileHash,
            Project = state.Effective,
            GateConfigurationHash = state.TrustedHash,
            Publisher = publisher,
            Approvals = approvals,
            Timing = new TimingRecorder(Clock, runId),
            Requirements = requirements,
            IsFollowUp = true,
            LocalEdit = run.Kind == RunKind.MechanicalEdit,
            StartTimestamp = Clock.GetTimestamp(),
            StartedAt = Clock.GetUtcNow(),
            State = run.State,
            Workspace = located.Workspace,
            RepairCyclesUsed = run.RepairCyclesUsed,
            Candidate = located.Candidate,
        };

        context.CandidateSequence = store.GetCandidates(runId).Count;
        context.LastReview = located.Candidate is null ? null : store.GetLatestReview(runId, located.Candidate.CandidateId);
        context.Confirmations.AddRange(store.GetConfirmations(runId));
        context.WorkState = run.RepairCyclesUsed > 0 ? RunState.Repairing : RunState.Implementing;

        foreach (var role in new[] { AgentRole.Implementer, AgentRole.Reviewer })
        {
            var roleProfile = located.Profile.For(role);
            var stored = store.FindSession(run.TaskId, role);
            var continues = stored is not null && string.Equals(stored.AdapterId, roleProfile.AdapterId, StringComparison.OrdinalIgnoreCase);
            context.Usage[role] = new RoleUsage(
                roleProfile.AdapterId,
                roleProfile.AccountRouteLabel,
                new SessionUsageTracker(continues ? stored!.CumulativeTokens : null, continues ? stored!.CumulativeCostUsd : null, sessionIsNew: !continues))
            {
                SessionId = continues ? stored!.SessionId : null,
                Model = continues ? stored!.Model : null,
            };
        }

        return new Rehydrated(context, null);
    }
}
