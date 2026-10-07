using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Core.Timing;

namespace Yav.Coordinator;

public sealed partial class RunCoordinator
{
    /// <summary>
    /// Writes an accepted candidate into the project. The evidence and the project's files are verified
    /// again first; a file the user edited in the meantime is never overwritten.
    /// </summary>
    public async Task<DeliveryOutcome> ApplyAsync(string runId, IRunObserver observer, CancellationToken cancellationToken)
    {
        var store = _services.Store;
        var run = store.FindRun(runId);
        if (run is null)
        {
            return Refused($"Run {runId} does not exist.", RunState.Failed, RunDisposition.Pending);
        }

        var publisher = new RunPublisher(runId, observer, store, Clock);
        publisher.EnablePersistence();

        if (run.State != RunState.ReadyToApply)
        {
            return Refused(
                run.State == RunState.Completed && run.Disposition == RunDisposition.Applied
                    ? $"Run {runId} was already applied."
                    : $"Run {runId} is {RunStateMachine.Display(run.State)}. Only a candidate that passed review and the required checks can be applied.",
                run.State, run.Disposition);
        }

        var timing = new TimingRecorder(Clock, runId);
        timing.SpanCompleted += span => _services.Store.SaveSpan(span);
        SaveWaiting(run, "until /apply");

        IsolatedWorkspace workspace;
        Candidate candidate;
        RunProfile profile;
        AcceptanceDecision decision;
        using (timing.Start(SpanKind.Apply, "evidence"))
        {
            var located = await LocateAsync(run, cancellationToken).ConfigureAwait(false);
            if (located.Problem is not null)
            {
                return Refused(located.Problem, run.State, run.Disposition);
            }

            (workspace, candidate, profile) = (located.Workspace!, located.Candidate!, located.Profile!);

            // Nothing is taken on trust from the moment the run became ready: the evidence is evaluated again.
            decision = await EvaluateStoredAsync(run, profile, workspace, candidate, cancellationToken).ConfigureAwait(false);
        }

        if (!decision.Accepted)
        {
            var reason = "The evidence no longer covers this candidate, so it was not applied. "
                + string.Join(" | ", decision.Issues.Select(i => i.Message).Take(4)) + " Check it again with /review and /test.";
            store.Transition(runId, RunState.ReadyToApply, RunState.Blocked, reason);
            publisher.Publish(new StateChanged(runId, Clock.GetUtcNow(), RunState.ReadyToApply, RunState.Blocked, reason));
            publisher.Note(Stages.Blocked, reason, NoteLevel.Error);
            return new DeliveryOutcome(false, reason, RunState.Blocked, run.Disposition, [], null, decision);
        }

        // A run that did not require checks may have owed that to the acceptance of the review alone. The evidence
        // cannot tell, because the policy of the run says only that checks were not required. When that acceptance was
        // withdrawn after the run started and was not given again, what it allowed is not applied. The run stays
        // ready, so that accepting the review alone once more makes it applicable again.
        if (WithdrawnAfterTheRun(run, profile))
        {
            var message = $"Run {runId} was accepted on the review of Model B alone, and you withdrew that acceptance for this project after the run started, "
                + "so nothing was written. Run the task again to have it checked, or accept the review alone for the project once more and apply again.";
            publisher.Note(Stages.Apply, message, NoteLevel.Warning);
            return new DeliveryOutcome(false, message, run.State, run.Disposition, [], null, decision);
        }

        ApplyResult result;
        ApplyPreflight preflight;
        using (timing.Start(SpanKind.Apply, "apply"))
        {
            preflight = await _services.Workspaces.PreflightApplyAsync(workspace, candidate, cancellationToken).ConfigureAwait(false);
            if (!preflight.CanApply)
            {
                var mergeable = preflight.Conflicts.All(c => c.Kind == ApplyConflictKind.ConcurrentEdit);
                var message = $"{preflight.Conflicts.Count} file(s) in the project no longer match what the task started from. Nothing was written. "
                    + (mergeable
                        ? "Your edits can be combined with the candidate in isolation and checked again: /apply --merge."
                        : "Resolve them in the project, then apply again.");
                foreach (var conflict in preflight.Conflicts)
                {
                    publisher.Note(Stages.Apply, $"{conflict.Path}: {conflict.Detail}", NoteLevel.Warning);
                }

                publisher.Note(Stages.Apply, message, NoteLevel.Warning);
                return new DeliveryOutcome(false, message, run.State, run.Disposition, preflight.Conflicts, null, decision);
            }

            publisher.Note(Stages.Apply, $"Applying candidate {candidate.ShortFingerprint} to {workspace.OriginalRoot} ({candidate.Changes.Summary})");
            result = await _services.Workspaces.ApplyAsync(workspace, candidate, runId, cancellationToken).ConfigureAwait(false);
        }

        if (!result.Applied)
        {
            var message = result.Error ?? "The candidate could not be applied.";
            if (result.Journal is { State: JournalState.Interrupted })
            {
                store.Transition(runId, RunState.ReadyToApply, RunState.NeedsReconciliation, message);
                publisher.Publish(new StateChanged(runId, Clock.GetUtcNow(), RunState.ReadyToApply, RunState.NeedsReconciliation, message));
                publisher.Note(Stages.Blocked, message, NoteLevel.Error);
                return new DeliveryOutcome(false, message, RunState.NeedsReconciliation, run.Disposition, result.Conflicts, result.Journal, decision);
            }

            publisher.Note(Stages.Apply, message, NoteLevel.Error);
            return new DeliveryOutcome(false, message, run.State, run.Disposition, result.Conflicts, result.Journal, decision);
        }

        // Only now, after the delivery step succeeded, is the run completed.
        store.Transition(runId, RunState.ReadyToApply, RunState.Completed, null);
        store.UpdateRun(runId, r => r with { Disposition = RunDisposition.Applied });
        publisher.Publish(new StateChanged(runId, Clock.GetUtcNow(), RunState.ReadyToApply, RunState.Completed, null));

        if (preflight.UnrelatedConcurrentEdits.Count > 0)
        {
            publisher.Note(
                Stages.Apply,
                $"{preflight.UnrelatedConcurrentEdits.Count} other file(s) that you edited meanwhile were left exactly as they are.");
        }

        using (timing.Start(SpanKind.Apply, "new baseline"))
        {
            await AdvanceBaselineAsync(workspace, candidate, publisher, cancellationToken).ConfigureAwait(false);
        }

        var summary = $"Applied {candidate.Changes.Summary}. Reverse it with /undo while the files stay unedited.";
        publisher.Note(Stages.Done, summary, NoteLevel.Success);
        publisher.Publish(new RunFinished(runId, Clock.GetUtcNow(), RunState.Completed, RunDisposition.Applied, null, null));
        return new DeliveryOutcome(true, summary, RunState.Completed, RunDisposition.Applied, [], result.Journal, decision);
    }

    /// <summary>
    /// Keeps how long a run waited for the user, from when it stopped until now, so that the time is not
    /// taken for time in which something was done.
    /// </summary>
    private void SaveWaiting(RunRecord run, string label)
    {
        var now = Clock.GetUtcNow();
        if (now > run.UpdatedAt)
        {
            _services.Store.SaveSpan(new TimingSpan(Ids.NewId("s"), run.RunId, SpanKind.ApprovalWaiting, label, run.UpdatedAt, now - run.UpdatedAt, null));
        }
    }

    /// <summary>After an apply the project is the new starting point, so that a follow-up shows only its own changes.</summary>
    private async Task AdvanceBaselineAsync(IsolatedWorkspace workspace, Candidate candidate, RunPublisher publisher, CancellationToken cancellationToken)
    {
        try
        {
            var merge = await _services.Workspaces.MergeConcurrentEditsAsync(workspace, candidate, cancellationToken).ConfigureAwait(false);
            if (!merge.Merged)
            {
                publisher.Note(
                    Stages.Apply,
                    "The isolated workspace could not be brought up to date with the project. A follow-up starts a new workspace instead.",
                    NoteLevel.Warning);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException or InvalidDataException)
        {
            publisher.Note(Stages.Apply, "The isolated workspace could not be brought up to date with the project: " + ex.Message, NoteLevel.Warning);
        }
    }

    /// <summary>
    /// Brings the user's concurrent edits into the isolated workspace and checks the merged candidate like
    /// any other. The project is not written.
    /// </summary>
    public async Task<RunOutcome> MergeAndRecheckAsync(
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

        if (context.State is not (RunState.ReadyToApply or RunState.Blocked) || context.Candidate is null)
        {
            return NotPossible(runId, $"Run {runId} is {RunStateMachine.Display(context.State)} and has no candidate that could be merged.");
        }

        return await RunResumedAsync(context, cancellationToken, async stop =>
        {
            var workspace = context.RequireWorkspace();
            Transition(context, RunState.Checking, "merging concurrent edits");
            var merge = await _services.Workspaces.MergeConcurrentEditsAsync(workspace, context.Candidate!, stop).ConfigureAwait(false);
            if (!merge.Merged)
            {
                return new Halt(
                    RunState.Blocked, RunOutcomeKind.Blocked,
                    "These files were changed both by you and by the task in ways that cannot be combined automatically: "
                    + string.Join(", ", merge.Unresolved) + ". Nothing was changed. Resolve them by hand, or discard the run.");
            }

            context.Workspace = await _services.Workspaces.FindAsync(workspace.WorkspaceId, stop).ConfigureAwait(false) ?? workspace;
            context.Publisher.Note(
                Stages.Check,
                merge.MergedFiles.Count > 0
                    ? $"Combined your edits with the candidate in {merge.MergedFiles.Count} file(s): {string.Join(", ", merge.MergedFiles.Take(5))}. The result is a new candidate and is checked again."
                    : "Your edits were brought into the isolated workspace. The result is checked again.");
            return await CheckAndRepairAsync(context, stop).ConfigureAwait(false);
        }).ConfigureAwait(false);
    }

    /// <summary>Removes the isolated data of a run's task. The project is never touched.</summary>
    public async Task<DeliveryOutcome> DiscardAsync(string runId, IRunObserver observer, CancellationToken cancellationToken)
    {
        var store = _services.Store;
        var run = store.FindRun(runId);
        if (run is null)
        {
            return Refused($"Run {runId} does not exist.", RunState.Failed, RunDisposition.Pending);
        }

        if (_activeRuns.ContainsKey(runId))
        {
            return Refused($"Run {runId} is still running. Stop it with /stop first.", run.State, run.Disposition);
        }

        if (run.Disposition == RunDisposition.Applied)
        {
            return Refused($"Run {runId} was applied to the project. /discard only removes isolated data; use /undo to reverse an apply.", run.State, run.Disposition);
        }

        var publisher = new RunPublisher(runId, observer, store, Clock);
        publisher.EnablePersistence();
        var task = store.FindTask(run.TaskId);
        await CloseTaskAsync(run.TaskId).ConfigureAwait(false);

        if (task?.WorkspaceId is { } workspaceId
            && await _services.Workspaces.FindAsync(workspaceId, cancellationToken).ConfigureAwait(false) is { } workspace)
        {
            if (workspace.Mode == WorkspaceMode.InPlace)
            {
                return Refused(
                    "This run worked directly in the project, so its changes are in the project and not in isolated data. "
                    + "Apply it and use /undo, or reverse the files with your own tools.",
                    run.State, run.Disposition);
            }

            await _services.Workspaces.DiscardAsync(workspace, cancellationToken).ConfigureAwait(false);
        }

        if (task is not null)
        {
            store.SaveTask(task with { WorkspaceId = null });
        }

        // The workspace belongs to the task, so every run of it that was not delivered loses its candidate.
        foreach (var other in store.ListRuns(run.ProjectPath, 200).Where(r => r.TaskId == run.TaskId && r.Disposition == RunDisposition.Pending))
        {
            if (other.State == RunState.ReadyToApply)
            {
                const string Reason = "Discarded by you. The isolated changes were removed; the project was not touched.";
                store.Transition(other.RunId, RunState.ReadyToApply, RunState.Interrupted, Reason);
                if (other.RunId == runId)
                {
                    publisher.Publish(new StateChanged(runId, Clock.GetUtcNow(), RunState.ReadyToApply, RunState.Interrupted, Reason));
                }
            }

            store.UpdateRun(other.RunId, r => r with { Disposition = RunDisposition.Discarded });
        }

        var final = store.FindRun(runId)!;
        const string Message = "Discarded the isolated changes of this task. The project was not touched.";
        publisher.Note(Stages.Local, Message, NoteLevel.Success);
        publisher.Publish(new RunFinished(runId, Clock.GetUtcNow(), final.State, final.Disposition, Message, null));
        return new DeliveryOutcome(true, Message, final.State, final.Disposition, [], null, null);
    }

    /// <summary>
    /// Reverses the most recent apply in a project, file by file. A file that was edited since is left alone
    /// and reported; it is never overwritten.
    /// </summary>
    public async Task<UndoOutcome> UndoAsync(string projectPath, bool skipConflicts, IRunObserver observer, CancellationToken cancellationToken)
    {
        var project = Path.GetFullPath(projectPath);
        var journals = _services.Journals.ListForProject(project, 100);
        if (journals.Count == 0)
        {
            // The journal is kept for the repository root, which can be above the directory that was opened.
            var inspection = await _services.Workspaces.InspectAsync(project, ProjectConfiguration.Empty, cancellationToken).ConfigureAwait(false);
            if (inspection.RepositoryRoot is { } root)
            {
                journals = _services.Journals.ListForProject(root, 100);
            }
        }

        if (journals.Count == 0)
        {
            return new UndoOutcome(false, "Nothing was applied to this project by YAV, so there is nothing to undo.", null, [], []);
        }

        // Applies are reversed newest first. One that did not finish has to be reconciled before anything older is touched.
        ApplyJournal? journal = null;
        foreach (var candidate in journals)
        {
            if (candidate.State is JournalState.Prepared or JournalState.InProgress or JournalState.Interrupted)
            {
                return new UndoOutcome(
                    false, $"The apply of run {candidate.RunId} did not finish. Run /doctor to reconcile it before anything is undone.", candidate.RunId, [], []);
            }

            if (candidate.State == JournalState.Committed)
            {
                journal = candidate;
                break;
            }
        }

        if (journal is null)
        {
            var why = journals[0].State == JournalState.Undone
                ? "The most recent apply was already undone, and no earlier apply is still in effect."
                : "The most recent apply was rolled back when it failed, and no earlier apply is still in effect.";
            return new UndoOutcome(false, why, journals[0].RunId, [], []);
        }

        var publisher = new RunPublisher(journal.RunId, observer, _services.Store, Clock);
        publisher.EnablePersistence();
        var result = await _services.Workspaces.UndoAsync(journal, skipConflicts, cancellationToken).ConfigureAwait(false);
        foreach (var conflict in result.Conflicts)
        {
            publisher.Note(Stages.Local, $"{conflict.Path}: {conflict.Detail}", NoteLevel.Warning);
        }

        if (!result.Undone)
        {
            // Which command helps is known here and not where the files are handled.
            var message = result.Conflicts.Count > 0 && !skipConflicts
                ? $"{result.Conflicts.Count} file(s) were edited after the apply, so nothing was reversed. "
                  + "Use /undo --skip-edited to reverse the other files and leave those as they are."
                : result.Error ?? "The apply could not be reversed.";
            publisher.Note(Stages.Local, message, NoteLevel.Warning);
            return new UndoOutcome(false, message, journal.RunId, result.Restored, result.Conflicts);
        }

        if (_services.Store.FindRun(journal.RunId) is not null)
        {
            _services.Store.UpdateRun(journal.RunId, r => r with { Disposition = RunDisposition.Undone });
        }

        var done = result.Conflicts.Count == 0
            ? $"Reversed {result.Restored.Count} file(s) of run {journal.RunId}."
            : $"Reversed {result.Restored.Count} file(s) of run {journal.RunId}. {result.Conflicts.Count} file(s) you edited since were left as they are.";
        publisher.Note(Stages.Local, done, NoteLevel.Success);
        return new UndoOutcome(true, done, journal.RunId, result.Restored, result.Conflicts);
    }

    /// <summary>Records the user's decision to accept a candidate although one required check did not pass.</summary>
    public bool WaiveGate(string runId, string gateId, string reason)
    {
        var run = _services.Store.FindRun(runId);
        if (run?.CurrentCandidateId is null || _services.Store.FindCandidate(run.CurrentCandidateId) is not { } candidate || string.IsNullOrWhiteSpace(reason))
        {
            return false;
        }

        _services.Store.SaveWaiver(new GateWaiver(runId, gateId, candidate.Fingerprint, reason.Trim(), Clock.GetUtcNow()));
        return true;
    }

    /// <summary>Records the user's approval of a change to a protected path, for exactly the current candidate.</summary>
    public bool ApproveProtectedPath(string runId, string path)
    {
        var run = _services.Store.FindRun(runId);
        if (run?.CurrentCandidateId is null || _services.Store.FindCandidate(run.CurrentCandidateId) is not { } candidate
            || !candidate.ProtectedPathsTouched.Contains(path, StringComparer.OrdinalIgnoreCase))
        {
            return false;
        }

        _services.Store.ApproveProtectedPath(runId, candidate.Fingerprint, path);
        return true;
    }

    private static DeliveryOutcome Refused(string message, RunState state, RunDisposition disposition) =>
        new(false, message, state, disposition, [], null, null);

    private static RunOutcome NotPossible(string runId, string reason) =>
        new(runId, RunOutcomeKind.Blocked, RunState.Blocked, reason, null, null, null, []);

    private sealed record Located(IsolatedWorkspace? Workspace, Candidate? Candidate, RunProfile? Profile, TaskContext? Task, string? Problem);

    private async Task<Located> LocateAsync(RunRecord run, CancellationToken cancellationToken)
    {
        var store = _services.Store;
        var profile = store.GetProfile(run.RunId);
        var task = store.FindTask(run.TaskId);
        if (profile is null || task is null)
        {
            return new Located(null, null, profile, task, $"The stored data of run {run.RunId} is incomplete.");
        }

        var workspace = task.WorkspaceId is null ? null : await _services.Workspaces.FindAsync(task.WorkspaceId, cancellationToken).ConfigureAwait(false);
        if (workspace is null)
        {
            return new Located(null, null, profile, task, $"The isolated workspace of run {run.RunId} no longer exists, so there is nothing to work with.");
        }

        var candidate = run.CurrentCandidateId is null ? null : store.FindCandidate(run.CurrentCandidateId);
        return new Located(workspace, candidate, profile, task, candidate is null && run.State == RunState.ReadyToApply ? $"Run {run.RunId} has no candidate." : null);
    }

    /// <summary>
    /// True when the run did not require checks, the acceptance of the review alone is not in force for its project
    /// now, and it was withdrawn after the run started. A run with checks optional for every project is caught as well
    /// when the acceptance was withdrawn meanwhile: /quality gates required withdraws it and requires checks for every
    /// project at once, so such a candidate would not be accepted now either.
    /// </summary>
    private bool WithdrawnAfterTheRun(RunRecord run, RunProfile profile) =>
        !profile.Policy.RequireGates
        && !_services.Trust.IsReviewOnlyAccepted(run.ProjectPath)
        && _services.Trust.ReviewOnlyWithdrawnAt(run.ProjectPath) is { } withdrawn
        && withdrawn >= run.CreatedAt;

    /// <summary>The acceptance decision for a stored run, from what is recorded and what is on disk right now.</summary>
    private async Task<AcceptanceDecision> EvaluateStoredAsync(
        RunRecord run,
        RunProfile profile,
        IsolatedWorkspace workspace,
        Candidate candidate,
        CancellationToken cancellationToken)
    {
        var store = _services.Store;
        var state = _services.Validation.LoadConfiguration(run.ProjectPath);
        var gates = state.Effective.RequiredGates.ToList();
        var environment = _services.Validation.ComputeEnvironmentFingerprint(gates, state.TrustedHash);
        var current = await _services.Workspaces.FingerprintAsync(workspace, cancellationToken).ConfigureAwait(false);
        var results = store.GetGateResults(run.RunId)
            .Where(r => !r.IsBaselineRun && string.Equals(r.Binding.CandidateFingerprint, candidate.Fingerprint, StringComparison.Ordinal))
            .Reverse()
            .ToList();

        return AcceptanceGate.Evaluate(new AcceptanceInput(
            profile,
            run.ProfileHash,
            store.GetConfirmations(run.RunId),
            candidate,
            current,
            run.AcceptanceVersion,
            environment,
            store.GetLatestReview(run.RunId, candidate.CandidateId),
            gates,
            results,
            store.GetWaivers(run.RunId),
            store.GetApprovedProtectedPaths(run.RunId, candidate.Fingerprint))
        {
            ImplementerInvolved = run.Kind != RunKind.MechanicalEdit || run.RepairCyclesUsed > 0,
        });
    }
}
