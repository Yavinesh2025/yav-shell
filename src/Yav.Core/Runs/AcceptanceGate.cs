using Yav.Core.Agents;
using Yav.Core.Profiles;

namespace Yav.Core.Runs;

public enum AcceptanceIssueKind
{
    CandidateChanged,
    ProfileNotHonored,
    ProfileUnverified,
    ReviewMissing,
    ReviewInvalid,
    ReviewStale,
    ReviewSourceChanged,
    ReviewNotPassed,
    BlockingFinding,
    GatesNotConfigured,
    GateMissing,
    GateStale,
    GateNotPassed,
    ProtectedPathChanged,
    SingleModel,
}

/// <summary>Who can resolve an issue.</summary>
public enum IssueResolution
{
    /// <summary>A defect in the candidate that Model A can fix.</summary>
    RepairByImplementer,

    /// <summary>Evidence is missing or stale. Running the check again can resolve it.</summary>
    Recheck,

    /// <summary>Only the user can decide or fix this.</summary>
    UserDecision,
}

public sealed record AcceptanceIssue(AcceptanceIssueKind Kind, string Message, IssueResolution Resolution);

public sealed record AcceptanceInput(
    RunProfile Profile,
    string ProfileHash,
    IReadOnlyList<ProfileConfirmation> Confirmations,
    Candidate Candidate,
    // Fingerprint of the isolated workspace as it is right now.
    string CurrentWorkspaceFingerprint,
    int CurrentAcceptanceVersion,
    string CurrentEnvironmentFingerprint,
    ReviewResult? Review,
    IReadOnlyList<GateDefinition> RequiredGates,
    IReadOnlyList<GateResult> GateResults,
    IReadOnlyList<GateWaiver> Waivers,
    // Protected paths whose change the user explicitly approved for this candidate.
    IReadOnlyList<string> ApprovedProtectedPaths)
{
    /// <summary>
    /// False when the candidate was produced by an explicit local edit and Model A never worked on it.
    /// There is then nothing of Model A to confirm.
    /// </summary>
    public bool ImplementerInvolved { get; init; } = true;
}

public sealed record AcceptanceDecision(bool Accepted, IReadOnlyList<AcceptanceIssue> Issues)
{
    public IEnumerable<AcceptanceIssue> Repairable => Issues.Where(i => i.Resolution == IssueResolution.RepairByImplementer);

    public IEnumerable<AcceptanceIssue> NeedingRecheck => Issues.Where(i => i.Resolution == IssueResolution.Recheck);

    public IEnumerable<AcceptanceIssue> NeedingUser => Issues.Where(i => i.Resolution == IssueResolution.UserDecision);

    /// <summary>
    /// True when Model A has something to fix and the evidence describing it is sound.
    /// Issues that only the user can decide do not prevent a repair of the others.
    /// </summary>
    public bool CanRepair => !Accepted && Repairable.Any() && !NeedingRecheck.Any();
}

/// <summary>
/// The application, not either model, decides whether a candidate is ready. The decision is a pure function
/// of recorded evidence, so the same evidence always gives the same answer.
/// </summary>
public static class AcceptanceGate
{
    public static AcceptanceDecision Evaluate(AcceptanceInput input)
    {
        var issues = new List<AcceptanceIssue>();
        var expected = new EvidenceBinding(
            input.Candidate.Fingerprint,
            input.CurrentAcceptanceVersion,
            input.ProfileHash,
            input.CurrentEnvironmentFingerprint);

        if (!string.Equals(input.Candidate.Fingerprint, input.CurrentWorkspaceFingerprint, StringComparison.Ordinal))
        {
            issues.Add(new AcceptanceIssue(
                AcceptanceIssueKind.CandidateChanged,
                $"The workspace no longer matches candidate {input.Candidate.ShortFingerprint}: source changed after the candidate was frozen.",
                IssueResolution.Recheck));
        }

        if (input.Candidate.AcceptanceVersion != input.CurrentAcceptanceVersion)
        {
            issues.Add(new AcceptanceIssue(
                AcceptanceIssueKind.CandidateChanged,
                "The requirements changed after this candidate was produced.",
                IssueResolution.UserDecision));
        }

        if (!string.Equals(input.Candidate.ProfileHash, input.ProfileHash, StringComparison.Ordinal))
        {
            issues.Add(new AcceptanceIssue(
                AcceptanceIssueKind.ProfileNotHonored,
                "The candidate was produced under a different run configuration than the one being accepted.",
                IssueResolution.UserDecision));
        }

        CheckProfile(input, issues);
        CheckReview(input, expected, issues);
        CheckGates(input, expected, issues);

        foreach (var path in input.Candidate.ProtectedPathsTouched)
        {
            if (!input.ApprovedProtectedPaths.Contains(path, StringComparer.OrdinalIgnoreCase))
            {
                issues.Add(new AcceptanceIssue(
                    AcceptanceIssueKind.ProtectedPathChanged,
                    $"The candidate changes protected path '{path}'. Changes to trusted acceptance tests or gate configuration need your explicit approval.",
                    IssueResolution.UserDecision));
            }
        }

        return new AcceptanceDecision(issues.Count == 0, issues);
    }

    private static void CheckProfile(AcceptanceInput input, List<AcceptanceIssue> issues)
    {
        var policy = input.Profile.Policy;
        if (!policy.QualityLock)
        {
            return;
        }

        if (policy.RequireReview && !input.Profile.RolesUseDistinctModels)
        {
            issues.Add(new AcceptanceIssue(
                AcceptanceIssueKind.SingleModel,
                "Both roles use the same model. Quality Lock requires an implementation model and a distinct review model.",
                IssueResolution.UserDecision));
        }

        foreach (var role in new[] { AgentRole.Implementer, AgentRole.Reviewer })
        {
            if (role == AgentRole.Reviewer && !policy.RequireReview)
            {
                continue;
            }

            if (role == AgentRole.Implementer && !input.ImplementerInvolved)
            {
                continue;
            }

            foreach (var setting in new[] { ProfileSettings.Model, ProfileSettings.Effort })
            {
                var confirmation = input.Confirmations
                    .Where(c => c.Role == role && c.Setting == setting)
                    .OrderByDescending(c => c.ObservedAt)
                    .FirstOrDefault();
                var name = $"{RoleName(role)} {setting}";

                if (confirmation is null)
                {
                    if (policy.Strict)
                    {
                        issues.Add(new AcceptanceIssue(
                            AcceptanceIssueKind.ProfileUnverified,
                            $"{name} was never confirmed by the provider (Requested / Unverified).",
                            IssueResolution.UserDecision));
                    }

                    continue;
                }

                switch (confirmation.Status)
                {
                    case VerificationStatus.Verified:
                        break;
                    case VerificationStatus.Mismatch:
                    case VerificationStatus.Unsupported:
                        issues.Add(new AcceptanceIssue(
                            AcceptanceIssueKind.ProfileNotHonored,
                            $"{name}: requested '{confirmation.Requested}' but the provider reported '{confirmation.Effective ?? "nothing"}' ({confirmation.Source}).",
                            IssueResolution.UserDecision));
                        break;
                    default:
                        if (policy.Strict)
                        {
                            issues.Add(new AcceptanceIssue(
                                AcceptanceIssueKind.ProfileUnverified,
                                $"{name} '{confirmation.Requested}' could not be confirmed ({confirmation.Source}).",
                                IssueResolution.UserDecision));
                        }

                        break;
                }
            }
        }
    }

    private static void CheckReview(AcceptanceInput input, EvidenceBinding expected, List<AcceptanceIssue> issues)
    {
        if (!input.Profile.Policy.RequireReview)
        {
            return;
        }

        var review = input.Review;
        if (review is null)
        {
            issues.Add(new AcceptanceIssue(AcceptanceIssueKind.ReviewMissing, "No Model B review exists for this candidate.", IssueResolution.Recheck));
            return;
        }

        if (!review.Binding.Matches(expected, out var difference))
        {
            issues.Add(new AcceptanceIssue(AcceptanceIssueKind.ReviewStale, $"The review is stale: {difference}.", IssueResolution.Recheck));
            return;
        }

        if (!review.SourceUnchangedDuringReview)
        {
            issues.Add(new AcceptanceIssue(
                AcceptanceIssueKind.ReviewSourceChanged,
                "Source changed while the review was running, so the review does not describe this candidate.",
                IssueResolution.Recheck));
            return;
        }

        if (!review.OutputValid)
        {
            var detail = review.ValidationErrors.Count > 0 ? " " + review.ValidationErrors[0] : string.Empty;
            issues.Add(new AcceptanceIssue(
                AcceptanceIssueKind.ReviewInvalid,
                "The reviewer's output was invalid or incomplete, which is not a pass." + detail,
                IssueResolution.Recheck));
            return;
        }

        foreach (var finding in review.BlockingFindings)
        {
            issues.Add(new AcceptanceIssue(
                AcceptanceIssueKind.BlockingFinding,
                $"[{finding.Severity}] {finding.Location}: {finding.Title}",
                IssueResolution.RepairByImplementer));
        }

        if (review.Verdict == ReviewVerdict.UnableToVerify)
        {
            var reason = review.Limitations.Count > 0 ? string.Join("; ", review.Limitations) : "no reason given";
            issues.Add(new AcceptanceIssue(
                AcceptanceIssueKind.ReviewNotPassed,
                $"The reviewer was unable to verify the candidate: {reason}",
                IssueResolution.UserDecision));
        }
    }

    private static void CheckGates(AcceptanceInput input, EvidenceBinding expected, List<AcceptanceIssue> issues)
    {
        if (input.RequiredGates.Count == 0)
        {
            if (input.Profile.Policy.RequireGates)
            {
                issues.Add(new AcceptanceIssue(
                    AcceptanceIssueKind.GatesNotConfigured,
                    "No trusted validation gate is configured for this project, so required checks cannot be shown to pass. "
                    + "A request typed in the shell asks whether to approve the checks YAV finds or to accept the review alone for "
                    + "this project. As commands: /test detect, or /quality gates optional for every project.",
                    IssueResolution.UserDecision));
            }

            return;
        }

        foreach (var gate in input.RequiredGates)
        {
            var result = input.GateResults
                .Where(r => r.GateId == gate.Id && !r.IsBaselineRun)
                .OrderByDescending(r => r.StartedAt)
                .FirstOrDefault();

            if (result is null)
            {
                issues.Add(new AcceptanceIssue(
                    AcceptanceIssueKind.GateMissing,
                    $"Required gate '{gate.Title}' has not run for this candidate.",
                    IssueResolution.Recheck));
                continue;
            }

            if (!result.Binding.Matches(expected, out var difference))
            {
                issues.Add(new AcceptanceIssue(
                    AcceptanceIssueKind.GateStale,
                    $"The result of gate '{gate.Title}' is stale: {difference}.",
                    IssueResolution.Recheck));
                continue;
            }

            if (result.Status == GateStatus.Passed)
            {
                continue;
            }

            var waived = input.Waivers.Any(w =>
                w.GateId == gate.Id && string.Equals(w.CandidateFingerprint, input.Candidate.Fingerprint, StringComparison.Ordinal));
            if (waived)
            {
                continue;
            }

            var preExisting = result.FailsOnBaseline == true;
            var repairPreExisting = input.Profile.Policy.RepairPreExistingFailures;
            var message = result.Status switch
            {
                GateStatus.Failed when preExisting && repairPreExisting =>
                    $"Gate '{gate.Title}' failed (exit {result.ExitCode}). It also fails on the unmodified baseline; your policy sends such failures for repair.",
                GateStatus.Failed when preExisting =>
                    $"Gate '{gate.Title}' failed (exit {result.ExitCode}). It also fails on the unmodified baseline, so the failure predates this task.",
                GateStatus.Failed => $"Gate '{gate.Title}' failed (exit {result.ExitCode}).",
                GateStatus.TimedOut => $"Gate '{gate.Title}' timed out after {gate.TimeoutSeconds}s.",
                GateStatus.Unverified => $"Gate '{gate.Title}' is Unverified: {result.Limitation ?? "it could not be run here"}.",
                GateStatus.Cancelled => $"Gate '{gate.Title}' was cancelled before it finished.",
                _ => $"Gate '{gate.Title}' could not be run: {result.Limitation ?? "unknown error"}.",
            };

            var resolution = result.Status switch
            {
                // A failure that was already there is the user's decision unless they chose to have it repaired.
                GateStatus.Failed when !preExisting || repairPreExisting => IssueResolution.RepairByImplementer,
                GateStatus.Cancelled => IssueResolution.Recheck,
                _ => IssueResolution.UserDecision,
            };
            issues.Add(new AcceptanceIssue(AcceptanceIssueKind.GateNotPassed, message, resolution));
        }
    }

    private static string RoleName(AgentRole role) => role == AgentRole.Implementer ? "Model A" : "Model B";
}

public static class ProfileSettings
{
    public const string Model = "model";
    public const string Effort = "effort";
    public const string Sandbox = "sandbox";
    public const string ServiceTier = "service tier";
    public const string CredentialSource = "credential source";

    /// <summary>Who decides what an agent asks for. It is the user, or the conversation is not used.</summary>
    public const string ApprovalsReviewer = "access decided by";

    /// <summary>Folders outside the workspace that the sandbox of an agent lets it write.</summary>
    public const string WritableOutside = "writable outside the workspace";

    public const string NetworkAccess = "network access";

    /// <summary>The directory an agent works in. It is the workspace, or what it changes is not the candidate.</summary>
    public const string WorkingDirectory = "working directory";
}
