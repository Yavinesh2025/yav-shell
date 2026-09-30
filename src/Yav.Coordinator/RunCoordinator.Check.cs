using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Core.Templates;
using Yav.Core.Timing;

namespace Yav.Coordinator;

public sealed partial class RunCoordinator
{
    /// <summary>What checking one candidate produced: a decision, or the reason the checks could not be completed.</summary>
    private sealed record CheckResult(AcceptanceDecision? Decision, Halt? Halt);

    /// <summary>
    /// Freezes what Model A produced, checks it, and lets Model A repair it a bounded number of times.
    /// Always ends in a state the user can act on.
    /// </summary>
    private async Task<Halt> CheckAndRepairAsync(RunContext context, CancellationToken stop)
    {
        var rechecks = 0;
        var repairing = false;
        while (true)
        {
            Transition(context, RunState.Checking, null);
            var previous = context.Candidate;
            var candidate = await FreezeAsync(context, stop).ConfigureAwait(false);

            if (candidate.Changes.IsEmpty && previous is null)
            {
                // Nothing was changed, so there is nothing to review, test or apply.
                return new Halt(RunState.Completed, RunOutcomeKind.Completed, null, RunDisposition.NoChanges, context.ImplementerSummary, Level: NoteLevel.Success);
            }

            if (repairing && previous is not null && string.Equals(previous.Fingerprint, candidate.Fingerprint, StringComparison.Ordinal))
            {
                return new Halt(
                    RunState.Blocked, RunOutcomeKind.Blocked,
                    "Model A was asked to repair the candidate but changed nothing, so the same issues remain. "
                    + (string.IsNullOrWhiteSpace(context.ImplementerSummary) ? string.Empty : "Its answer: " + Shorten(context.ImplementerSummary, 400)),
                    Decision: null);
            }

            var check = await CheckCandidateAsync(context, candidate, stop).ConfigureAwait(false);
            if (check.Halt is not null)
            {
                return check.Halt;
            }

            var decision = check.Decision!;
            if (decision.Accepted)
            {
                if (await RepositoryTamperedAsync(context, stop).ConfigureAwait(false) is { } tampered)
                {
                    return tampered with { Decision = decision };
                }

                context.AcceptedAt = Clock.GetUtcNow();
                return new Halt(RunState.ReadyToApply, RunOutcomeKind.ReadyToApply, null, Decision: decision, Level: NoteLevel.Success);
            }

            foreach (var issue in decision.Issues)
            {
                context.Publisher.Note(Stages.Check, issue.Message, NoteLevel.Warning);
            }

            if (decision.NeedingRecheck.Any() && rechecks < _options.MaxAutomaticRechecks)
            {
                // The evidence is unusable, not the candidate. Only what is missing or stale is produced again.
                rechecks++;
                repairing = false;
                context.Publisher.Note(Stages.Check, "The evidence was incomplete or no longer matched the candidate. Checking again.");
                continue;
            }

            var maxCycles = Math.Min(context.Profile.Policy.MaxRepairCycles, context.Configuration.Limits.MaxRepairCycles);
            if (!decision.CanRepair)
            {
                return new Halt(RunState.Blocked, RunOutcomeKind.Blocked, Summarize(decision), Decision: decision);
            }

            if (context.RepairCyclesUsed >= maxCycles)
            {
                return new Halt(
                    RunState.Blocked, RunOutcomeKind.Blocked,
                    $"The limit of {maxCycles} repair cycle(s) is reached and the candidate still has unresolved issues. {Summarize(decision)} "
                    + "The candidate and all findings are kept. Decide how to continue: /diff, /review, /limits, or a follow-up request.",
                    Decision: decision);
            }

            var halt = await RepairAsync(context, decision, maxCycles, stop).ConfigureAwait(false);
            if (halt is not null)
            {
                return halt with { Decision = decision };
            }

            rechecks = 0;
            repairing = true;
        }
    }

    private static string Summarize(AcceptanceDecision decision)
    {
        var parts = decision.Issues.Select(i => i.Message).Take(6).ToList();
        var more = decision.Issues.Count > parts.Count ? $" (and {decision.Issues.Count - parts.Count} more)" : string.Empty;
        return "The candidate is not accepted: " + string.Join(" | ", parts) + more;
    }

    private static string Shorten(string text, int limit) => text.Length <= limit ? text : string.Concat(text.AsSpan(0, limit), "…");

    private async Task<Halt?> RepairAsync(RunContext context, AcceptanceDecision decision, int maxCycles, CancellationToken stop)
    {
        var candidate = context.Candidate!;
        var findings = context.LastReview is { OutputValid: true } review && review.CandidateId == candidate.CandidateId
            ? review.BlockingFindings.ToList()
            : [];
        var failed = LatestResults(context, candidate)
            .Where(r => r.Status == GateStatus.Failed && (r.FailsOnBaseline != true || context.Profile.Policy.RepairPreExistingFailures))
            .GroupBy(r => r.GateId, StringComparer.Ordinal)
            .Select(g => g.First())
            .ToList();

        context.RepairCyclesUsed++;
        _services.Store.UpdateRun(context.RunId, run => run with { RepairCyclesUsed = context.RepairCyclesUsed });
        context.Publisher.Publish(new RepairStarted(context.RunId, Clock.GetUtcNow(), context.RepairCyclesUsed, maxCycles, findings.Count, failed.Count));
        context.Publisher.Note(
            Stages.Repair,
            $"Repair {context.RepairCyclesUsed} of {maxCycles}: {findings.Count} finding(s) and {failed.Count} failed check(s) go to Model A in one request");
        _ = decision;

        // One request carries everything, so Model A deliberates once per cycle.
        var prompt = PromptBuilder.RepairRequest(context.Requirements, findings, failed, context.RepairCyclesUsed, maxCycles, context.Project.RequiredGates.ToList());
        // A repair quotes what the reviewer found and what the checks wrote.
        return await ImplementAsync(context, prompt, SpanKind.Repair, quotesOutput: true, stop).ConfigureAwait(false);
    }

    private async Task<Candidate> FreezeAsync(RunContext context, CancellationToken stop)
    {
        // A requirement that is on its way to the agent is recorded before the candidate is frozen.
        await context.RequirementsChange.WaitAsync(stop).ConfigureAwait(false);
        try
        {
            return await FreezeCandidateAsync(context, stop).ConfigureAwait(false);
        }
        finally
        {
            context.RequirementsChange.Release();
        }
    }

    private async Task<Candidate> FreezeCandidateAsync(RunContext context, CancellationToken stop)
    {
        var workspace = context.RequireWorkspace();
        CandidateSnapshot snapshot;
        using (context.Timing.Start(SpanKind.Freeze, "freeze"))
        {
            snapshot = await _services.Workspaces.FreezeAsync(workspace, context.Project, stop).ConfigureAwait(false);
        }

        if (context.Candidate is { } current
            && string.Equals(current.Fingerprint, snapshot.Fingerprint, StringComparison.Ordinal)
            && current.AcceptanceVersion == context.AcceptanceVersion)
        {
            // Nothing changed since the last freeze: the candidate and its evidence stay what they are.
            return current;
        }

        var candidate = new Candidate(
            Ids.NewId("c"),
            context.RunId,
            ++context.CandidateSequence,
            snapshot.Fingerprint,
            workspace.BaselineFingerprint,
            context.AcceptanceVersion,
            context.ProfileHash,
            snapshot.Changes,
            snapshot.ProtectedPathsTouched,
            snapshot.ExistingTestsTouched,
            Clock.GetUtcNow());

        _services.Store.SaveCandidate(candidate, snapshot.ManifestId);
        _services.Store.UpdateRun(context.RunId, run => run with { CurrentCandidateId = candidate.CandidateId });
        context.Candidate = candidate;
        context.Publisher.Publish(new CandidateFrozen(context.RunId, candidate.FrozenAt, candidate));

        if (candidate.ExistingTestsTouched.Count > 0)
        {
            context.Publisher.Note(
                Stages.Check,
                $"Existing tests were changed or deleted: {string.Join(", ", candidate.ExistingTestsTouched.Take(5))}. The reviewer is asked to check that none was weakened.",
                NoteLevel.Warning);
        }

        if (candidate.ProtectedPathsTouched.Count > 0)
        {
            context.Publisher.Note(
                Stages.Check,
                $"Protected paths were changed: {string.Join(", ", candidate.ProtectedPathsTouched.Take(5))}. They need your explicit approval before the candidate can be accepted.",
                NoteLevel.Warning);
        }

        return candidate;
    }

    /// <summary>Reviews and tests one frozen candidate side by side, then lets the acceptance gate decide.</summary>
    private async Task<CheckResult> CheckCandidateAsync(RunContext context, Candidate candidate, CancellationToken stop)
    {
        var workspace = context.RequireWorkspace();
        var gates = context.Project.RequiredGates.ToList();
        var environment = _services.Validation.ComputeEnvironmentFingerprint(gates, context.GateConfigurationHash);
        var binding = new EvidenceBinding(candidate.Fingerprint, context.AcceptanceVersion, context.ProfileHash, environment);

        context.Publisher.Note(
            Stages.Check,
            $"Reviewing and testing candidate {candidate.ShortFingerprint} ({candidate.Changes.Summary})");

        // Review and required checks only start now that a stable candidate exists, and they overlap.
        var group = Ids.NewId("pg");
        using var sibling = CancellationTokenSource.CreateLinkedTokenSource(stop);
        var reviewing = context.Profile.Policy.RequireReview
            ? ReviewAsync(context, candidate, binding, group, sibling.Token)
            : Task.FromResult<Halt?>(null);
        var testing = RunGatesAsync(context, candidate, gates, binding, group, sibling.Token);

        Halt? reviewHalt = null;
        Exception? failure = null;
        try
        {
            reviewHalt = await reviewing.ConfigureAwait(false);
            if (reviewHalt is not null)
            {
                // The run cannot go on; the checks that are still running are not needed.
                await sibling.CancelAsync().ConfigureAwait(false);
            }
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            failure = ex;
            await sibling.CancelAsync().ConfigureAwait(false);
        }

        try
        {
            await testing.ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (failure is not null || reviewHalt is not null)
        {
            // Cancelled because the review could not be completed.
        }
        catch (Exception ex) when (failure is null && ex is not OutOfMemoryException)
        {
            failure = ex;
        }

        if (failure is not null)
        {
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        stop.ThrowIfCancellationRequested();
        if (reviewHalt is not null)
        {
            return new CheckResult(null, reviewHalt);
        }

        using var evaluating = context.Timing.Start(SpanKind.Acceptance, "evidence");
        var current = await _services.Workspaces.FingerprintAsync(workspace, stop).ConfigureAwait(false);
        if (!string.Equals(current, candidate.Fingerprint, StringComparison.Ordinal)
            && await RestoreCandidateAsync(context, candidate, "Source changed while the candidate was being checked", stop).ConfigureAwait(false))
        {
            current = await _services.Workspaces.FingerprintAsync(workspace, stop).ConfigureAwait(false);
        }

        List<ProfileConfirmation> confirmations;
        lock (context.Confirmations)
        {
            confirmations = [.. context.Confirmations];
        }

        var decision = AcceptanceGate.Evaluate(new AcceptanceInput(
            context.Profile,
            context.ProfileHash,
            confirmations,
            candidate,
            current,
            context.AcceptanceVersion,
            // Read again: a toolchain that changed while the checks ran makes their results stale.
            _services.Validation.ComputeEnvironmentFingerprint(gates, context.GateConfigurationHash),
            context.LastReview is { } review && review.CandidateId == candidate.CandidateId ? review : null,
            gates,
            LatestResults(context, candidate),
            _services.Store.GetWaivers(context.RunId),
            _services.Store.GetApprovedProtectedPaths(context.RunId, candidate.Fingerprint))
        {
            ImplementerInvolved = context.ImplementerInvolved,
        });

        context.Publisher.Publish(new AcceptanceEvaluated(context.RunId, Clock.GetUtcNow(), candidate, decision));
        return new CheckResult(decision, null);
    }

    /// <summary>The newest result of every gate for this candidate, newest first.</summary>
    private List<GateResult> LatestResults(RunContext context, Candidate candidate) =>
        _services.Store.GetGateResults(context.RunId)
            .Where(r => !r.IsBaselineRun && string.Equals(r.Binding.CandidateFingerprint, candidate.Fingerprint, StringComparison.Ordinal))
            .Reverse()
            .ToList();

    private async Task<Halt?> ReviewAsync(RunContext context, Candidate candidate, EvidenceBinding binding, string group, CancellationToken stop)
    {
        var workspace = context.RequireWorkspace();
        var reviewer = context.Profile.Reviewer;

        if (context.LastReview is { OutputValid: true, SourceUnchangedDuringReview: true } earlier
            && earlier.CandidateId == candidate.CandidateId
            && earlier.Binding.Matches(binding, out _))
        {
            // Complete, valid and produced for exactly this candidate and configuration.
            context.Publisher.Note(Stages.ReviewB, "The existing review of this candidate is still valid and is used");
            return null;
        }

        if (LimitReached(context) is { } limit)
        {
            return new Halt(RunState.Blocked, RunOutcomeKind.Blocked, limit);
        }

        var findingsToVerify = context.LastReview is { OutputValid: true } previous && previous.CandidateId != candidate.CandidateId
            ? previous.BlockingFindings.ToList()
            : [];

        using var span = context.Timing.Start(SpanKind.Review, "review", group);
        var diff = await _services.Workspaces.DiffAsync(workspace, candidate, _options.ReviewDiffCharacters, stop).ConfigureAwait(false);
        var pathExists = await _services.Workspaces.GetPathLookupAsync(workspace, candidate, stop).ConfigureAwait(false);
        var prompt = PromptBuilder.ReviewRequest(
            context.Requirements, candidate, diff.Text, diff.Truncated, diff.FullDiffPath, context.ImplementerSummary, findingsToVerify);

        var slot = await OpenSessionAsync(context, AgentRole.Reviewer, stop).ConfigureAwait(false);
        context.Publisher.Note(
            Stages.ReviewB,
            $"{reviewer.ModelDisplayName ?? reviewer.ModelId} is reviewing candidate {candidate.ShortFingerprint} in its own conversation, read-only"
            + (reviewer.RequestedEffort.Length > 0 ? $" (effort {reviewer.RequestedEffort})" : string.Empty));

        var started = Clock.GetUtcNow();
        var result = await _turns.RunAsync(context, slot, new TurnRequest(prompt, ReviewSchema.AsElement(), [], QuotesOutput: true), stop).ConfigureAwait(false);
        SaveSession(context, slot);
        if (Interpret(context, AgentRole.Reviewer, result) is { } halt)
        {
            return halt;
        }

        var parsed = ReviewOutputParser.Parse(result.StructuredOutput, result.FinalMessage, pathExists);
        var after = await _services.Workspaces.FingerprintAsync(workspace, stop).ConfigureAwait(false);
        var unchanged = string.Equals(after, candidate.Fingerprint, StringComparison.Ordinal);

        var review = new ReviewResult(
            Ids.NewId("rev"),
            context.RunId,
            candidate.CandidateId,
            // Bound by the host to the candidate it froze, never to an identifier the model echoes.
            binding,
            parsed.Verdict,
            parsed.Summary,
            parsed.Coverage,
            parsed.Findings,
            parsed.Limitations,
            parsed.OutputValid,
            parsed.ValidationErrors,
            reviewer.AdapterId,
            context.Usage[AgentRole.Reviewer].Model ?? reviewer.ModelId,
            slot.Session.SessionId,
            unchanged,
            started,
            Clock.GetUtcNow());

        _services.Store.SaveReview(review);
        context.LastReview = review;
        context.Publisher.Publish(new ReviewCompleted(context.RunId, review.CompletedAt, review));
        context.Publisher.Note(
            Stages.ReviewB,
            review.IsCleanPass && !review.Suggestions.Any() ? "No blocking findings reported" : RunPublisher.Describe(review),
            review.IsCleanPass ? NoteLevel.Success : NoteLevel.Warning);

        foreach (var note in parsed.Notes)
        {
            context.Publisher.Note(Stages.ReviewB, note);
        }

        if (!unchanged)
        {
            context.Publisher.Note(
                Stages.ReviewB,
                "Source changed while the review was running, so the review does not describe this candidate.",
                NoteLevel.Warning);
            if (!ChecksRunInCandidate(context))
            {
                // Nothing but the reviewer could have written here, and the reviewer is not a writer.
                await RestoreCandidateAsync(context, candidate, "Model B is read-only and must not change the candidate", stop).ConfigureAwait(false);
            }
        }

        return null;
    }

    private static bool ChecksRunInCandidate(RunContext context) =>
        context.RequireWorkspace().Mode == WorkspaceMode.InPlace
        || string.Equals(context.Project.ValidationExecution, ProjectConfiguration.ExecutionCandidate, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Puts the isolated workspace back to the frozen candidate after something other than Model A wrote
    /// into it. Returns false when that is not possible, which leaves the difference for the acceptance gate to report.
    /// </summary>
    private async Task<bool> RestoreCandidateAsync(RunContext context, Candidate candidate, string why, CancellationToken stop)
    {
        var workspace = context.RequireWorkspace();
        if (workspace.Mode == WorkspaceMode.InPlace)
        {
            context.Publisher.Note(
                Stages.Check,
                $"Source changed after candidate {candidate.ShortFingerprint} was frozen. This run works in the project itself, so nothing is put back automatically.",
                NoteLevel.Warning);
            return false;
        }

        try
        {
            var restored = await _services.Workspaces.RestoreCandidateAsync(workspace, candidate, stop).ConfigureAwait(false);
            if (restored.Count > 0)
            {
                var more = restored.Count > 6 ? $" and {restored.Count - 6} more" : string.Empty;
                context.Publisher.Note(
                    Stages.Check,
                    $"{why}. Put back to candidate {candidate.ShortFingerprint}: {string.Join(", ", restored.Take(6))}{more}.",
                    NoteLevel.Warning);
            }

            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            context.Publisher.Note(Stages.Check, $"The workspace could not be put back to candidate {candidate.ShortFingerprint}: {ex.Message}", NoteLevel.Error);
            return false;
        }
    }

    private async Task RunGatesAsync(
        RunContext context,
        Candidate candidate,
        IReadOnlyList<GateDefinition> gates,
        EvidenceBinding binding,
        string group,
        CancellationToken stop)
    {
        if (gates.Count == 0)
        {
            return;
        }

        var workspace = context.RequireWorkspace();
        var earlier = LatestResults(context, candidate);
        var pending = new List<GateDefinition>();
        foreach (var gate in gates)
        {
            var existing = context.ForceGates ? null : earlier.FirstOrDefault(r => r.GateId == gate.Id);
            if (existing is { Status: GateStatus.Passed or GateStatus.Failed } && existing.Binding.Matches(binding, out _))
            {
                // Complete and produced for exactly this candidate, configuration and toolchain.
                context.Publisher.Note(Stages.Tests, $"{gate.Title}: the result for this candidate is still valid and is used ({existing.Status})");
                continue;
            }

            pending.Add(gate);
        }

        if (pending.Count == 0)
        {
            return;
        }

        using var span = context.Timing.Start(SpanKind.Tests, "required checks", group);
        var inCandidate = string.Equals(context.Project.ValidationExecution, ProjectConfiguration.ExecutionCandidate, StringComparison.OrdinalIgnoreCase);
        ExecutionCopy copy;
        if (inCandidate)
        {
            copy = new ExecutionCopy(workspace.RootPath, true, candidate.Fingerprint, []);
        }
        else
        {
            // Checks run in a disposable copy made from the frozen content, so they cannot change the candidate.
            copy = await _services.Workspaces.PrepareExecutionCopyAsync(workspace, candidate, context.Project, stop).ConfigureAwait(false);
            foreach (var note in copy.Notes)
            {
                context.Publisher.Note(Stages.Tests, note);
            }
        }

        var directory = ProjectDirectoryIn(workspace, copy.Path);
        var allPassed = true;
        foreach (var gate in pending)
        {
            stop.ThrowIfCancellationRequested();
            context.Publisher.Publish(new GateStarted(context.RunId, Clock.GetUtcNow(), gate, directory));
            var result = await _services.Validation.RunGateAsync(
                gate,
                new GateRunContext(context.RunId, directory, binding, workspace.EvidencePath, IsBaselineRun: false),
                line => context.Publisher.Publish(new GateOutput(context.RunId, Clock.GetUtcNow(), gate.Id, line)),
                stop).ConfigureAwait(false);

            if (inCandidate && !stop.IsCancellationRequested
                && !string.Equals(await _services.Workspaces.FingerprintAsync(workspace, stop).ConfigureAwait(false), candidate.Fingerprint, StringComparison.Ordinal))
            {
                // A result obtained while the source was changing does not describe the candidate, whatever it says.
                await RestoreCandidateAsync(context, candidate, $"Source changed while '{gate.Title}' ran in the candidate workspace", stop).ConfigureAwait(false);
                result = result with
                {
                    Status = GateStatus.Error,
                    Limitation = "Source files changed while the check ran, so its result does not describe the candidate. "
                        + "Let checks write to ignored output directories only, or run them in a copy (validationExecution: copy).",
                };
            }

            if (result.Status == GateStatus.Failed)
            {
                result = result with { FailsOnBaseline = await FailsOnBaselineAsync(context, gate, stop).ConfigureAwait(false) };
            }

            _services.Store.SaveGateResult(result);
            context.Publisher.Publish(new GateCompleted(context.RunId, Clock.GetUtcNow(), result));
            allPassed &= result.Status == GateStatus.Passed;
            if (result.Status != GateStatus.Passed)
            {
                var preExisting = result.FailsOnBaseline == true ? " It also fails on the unmodified baseline." : string.Empty;
                context.Publisher.Note(Stages.Tests, RunPublisher.Describe(result) + "." + preExisting, NoteLevel.Warning);
            }
        }

        context.ForceGates = false;
        if (allPassed)
        {
            context.Publisher.Note(Stages.Tests, gates.Count == 1 ? "Required check passed" : $"Required checks passed ({gates.Count})", NoteLevel.Success);
        }
    }

    /// <summary>
    /// Runs a failed check on the unmodified baseline, once per run, to tell a failure the task introduced
    /// from one that was already there. Null when that could not be determined.
    /// </summary>
    private async Task<bool?> FailsOnBaselineAsync(RunContext context, GateDefinition gate, CancellationToken stop)
    {
        if (context.BaselineResults.TryGetValue(gate.Id, out var known))
        {
            return known.Status == GateStatus.Failed ? true : known.Status == GateStatus.Passed ? false : null;
        }

        var workspace = context.RequireWorkspace();
        try
        {
            context.Publisher.Note(Stages.Tests, $"{gate.Title} failed. Running it on the unmodified baseline to see whether the failure was already there");
            var copy = await _services.Workspaces.PrepareBaselineCopyAsync(workspace, context.Project, stop).ConfigureAwait(false);
            var environment = _services.Validation.ComputeEnvironmentFingerprint([gate], context.GateConfigurationHash);
            var result = await _services.Validation.RunGateAsync(
                gate,
                new GateRunContext(
                    context.RunId, ProjectDirectoryIn(workspace, copy.Path),
                    new EvidenceBinding(workspace.BaselineFingerprint, context.AcceptanceVersion, context.ProfileHash, environment),
                    workspace.EvidencePath, IsBaselineRun: true),
                null,
                stop).ConfigureAwait(false);

            context.BaselineResults[gate.Id] = result;
            _services.Store.SaveGateResult(result);
            context.Publisher.Publish(new GateCompleted(context.RunId, Clock.GetUtcNow(), result));
            return result.Status == GateStatus.Failed ? true : result.Status == GateStatus.Passed ? false : null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            context.Publisher.Note(Stages.Tests, "The baseline could not be checked: " + ex.Message, NoteLevel.Warning);
            return null;
        }
    }

    /// <summary>The shared repository's branches, tags and stash must be what they were before the run.</summary>
    private async Task<Halt?> RepositoryTamperedAsync(RunContext context, CancellationToken stop)
    {
        using var verifying = context.Timing.Start(SpanKind.Acceptance, "repository");
        var differences = await _services.Workspaces.VerifyRepositoryUntouchedAsync(context.RequireWorkspace(), stop).ConfigureAwait(false);
        if (differences.Count == 0)
        {
            return null;
        }

        return new Halt(
            RunState.Blocked, RunOutcomeKind.Blocked,
            "The repository itself was changed during the run, which a task must not do: " + string.Join("; ", differences.Take(5))
            + ". The candidate is kept but not offered for application until you have looked at this.",
            Level: NoteLevel.Error);
    }
}
