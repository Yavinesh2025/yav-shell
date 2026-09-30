using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.Core;

public class AcceptanceGateTests
{
    [Fact]
    public void A_candidate_with_a_clean_review_passing_gates_and_confirmed_settings_is_accepted()
    {
        var decision = AcceptanceGate.Evaluate(Builders.Acceptance());

        Assert.True(decision.Accepted);
        Assert.Empty(decision.Issues);
    }

    [Fact]
    public void Source_that_changed_after_the_freeze_is_not_accepted()
    {
        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(workspaceFingerprint: Builders.OtherFingerprint));

        Assert.False(decision.Accepted);
        var issue = Assert.Single(decision.Issues, i => i.Kind == AcceptanceIssueKind.CandidateChanged);
        Assert.Equal(IssueResolution.Recheck, issue.Resolution);
    }

    [Fact]
    public void A_missing_review_is_not_accepted()
    {
        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(noReview: true));

        Assert.False(decision.Accepted);
        Assert.Single(decision.Issues, i => i.Kind == AcceptanceIssueKind.ReviewMissing);
    }

    [Fact]
    public void A_review_of_an_earlier_candidate_is_stale()
    {
        var review = Builders.Review(binding: Builders.Binding(fingerprint: Builders.OtherFingerprint));

        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(review: review));

        Assert.False(decision.Accepted);
        Assert.Single(decision.Issues, i => i.Kind == AcceptanceIssueKind.ReviewStale);
    }

    [Fact]
    public void A_review_made_before_the_requirements_changed_is_stale()
    {
        var input = Builders.Acceptance(
            candidate: Builders.Candidate(acceptance: 2),
            acceptanceVersion: 2,
            review: Builders.Review(binding: Builders.Binding(acceptance: 1)),
            results: [Builders.GateResult(binding: Builders.Binding(acceptance: 2))]);

        var decision = AcceptanceGate.Evaluate(input);

        var issue = Assert.Single(decision.Issues);
        Assert.Equal(AcceptanceIssueKind.ReviewStale, issue.Kind);
        Assert.Contains("requirements changed", issue.Message);
    }

    [Fact]
    public void Invalid_reviewer_output_is_not_a_pass_even_when_it_says_pass()
    {
        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(review: Builders.Review(ReviewVerdict.Pass, valid: false)));

        Assert.False(decision.Accepted);
        Assert.Single(decision.Issues, i => i.Kind == AcceptanceIssueKind.ReviewInvalid);
    }

    [Fact]
    public void A_review_during_which_source_changed_does_not_count()
    {
        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(review: Builders.Review(sourceUnchanged: false)));

        Assert.False(decision.Accepted);
        Assert.Single(decision.Issues, i => i.Kind == AcceptanceIssueKind.ReviewSourceChanged);
    }

    [Fact]
    public void A_blocking_finding_blocks_and_can_be_repaired()
    {
        var review = Builders.Review(ReviewVerdict.ChangesRequired, findings: [Builders.Finding()]);

        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(review: review));

        Assert.False(decision.Accepted);
        var issue = Assert.Single(decision.Issues);
        Assert.Equal(AcceptanceIssueKind.BlockingFinding, issue.Kind);
        Assert.Contains("src/login.cs:42", issue.Message);
        Assert.True(decision.CanRepair);
    }

    [Fact]
    public void Optional_suggestions_do_not_block()
    {
        var review = Builders.Review(ReviewVerdict.Pass, findings: [Builders.Finding(optional: true)]);

        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(review: review));

        Assert.True(decision.Accepted);
    }

    [Fact]
    public void Unable_to_verify_blocks_and_needs_the_user()
    {
        var review = Builders.Review(ReviewVerdict.UnableToVerify, limitations: ["Needs a database."]);

        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(review: review));

        var issue = Assert.Single(decision.Issues);
        Assert.Equal(AcceptanceIssueKind.ReviewNotPassed, issue.Kind);
        Assert.Equal(IssueResolution.UserDecision, issue.Resolution);
        Assert.Contains("Needs a database.", issue.Message);
        Assert.False(decision.CanRepair);
    }

    [Fact]
    public void A_passing_review_does_not_stand_in_for_a_failed_gate()
    {
        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(results: [Builders.GateResult(status: GateStatus.Failed, exitCode: 1)]));

        Assert.False(decision.Accepted);
        var issue = Assert.Single(decision.Issues);
        Assert.Equal(AcceptanceIssueKind.GateNotPassed, issue.Kind);
        Assert.Equal(IssueResolution.RepairByImplementer, issue.Resolution);
    }

    [Fact]
    public void A_required_gate_that_never_ran_blocks()
    {
        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(results: []));

        var issue = Assert.Single(decision.Issues);
        Assert.Equal(AcceptanceIssueKind.GateMissing, issue.Kind);
    }

    [Fact]
    public void A_gate_result_for_an_earlier_candidate_is_stale()
    {
        var stale = Builders.GateResult(binding: Builders.Binding(fingerprint: Builders.OtherFingerprint));

        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(results: [stale]));

        Assert.Single(decision.Issues, i => i.Kind == AcceptanceIssueKind.GateStale);
    }

    [Fact]
    public void A_gate_result_from_a_different_toolchain_is_stale()
    {
        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(environment: "env-2"));

        Assert.Contains(decision.Issues, i => i.Kind == AcceptanceIssueKind.GateStale && i.Message.Contains("toolchain"));
        Assert.Contains(decision.Issues, i => i.Kind == AcceptanceIssueKind.ReviewStale);
    }

    [Fact]
    public void The_latest_result_of_a_gate_decides()
    {
        var earlierFailure = Builders.GateResult(status: GateStatus.Failed, startedAt: Builders.Now);
        var laterPass = Builders.GateResult(status: GateStatus.Passed, startedAt: Builders.Now.AddMinutes(5));

        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(results: [laterPass, earlierFailure]));

        Assert.True(decision.Accepted);
    }

    [Fact]
    public void A_baseline_run_never_counts_as_the_candidate_result()
    {
        var baselinePass = Builders.GateResult(status: GateStatus.Passed, baselineRun: true);

        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(results: [baselinePass]));

        Assert.Single(decision.Issues, i => i.Kind == AcceptanceIssueKind.GateMissing);
    }

    [Fact]
    public void A_failure_that_predates_the_task_is_not_sent_for_repair()
    {
        var result = Builders.GateResult(status: GateStatus.Failed, failsOnBaseline: true);

        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(results: [result]));

        var issue = Assert.Single(decision.Issues);
        Assert.Equal(IssueResolution.UserDecision, issue.Resolution);
        Assert.Contains("predates", issue.Message);
        Assert.False(decision.CanRepair);
    }

    [Fact]
    public void A_candidate_that_model_a_did_not_produce_needs_no_confirmation_of_model_a()
    {
        var reviewerOnly = Builders.AllConfirmed().Where(c => c.Role == AgentRole.Reviewer).ToList();

        var local = AcceptanceGate.Evaluate(Builders.Acceptance(confirmations: reviewerOnly) with { ImplementerInvolved = false });
        var coded = AcceptanceGate.Evaluate(Builders.Acceptance(confirmations: reviewerOnly));

        Assert.True(local.Accepted, string.Join("; ", local.Issues.Select(i => i.Message)));
        Assert.Equal(2, coded.Issues.Count(i => i.Kind == AcceptanceIssueKind.ProfileUnverified));
    }

    [Fact]
    public void The_reviewers_settings_are_required_whoever_produced_the_candidate()
    {
        var implementerOnly = Builders.AllConfirmed().Where(c => c.Role == AgentRole.Implementer).ToList();

        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(confirmations: implementerOnly) with { ImplementerInvolved = false });

        Assert.False(decision.Accepted);
        Assert.All(decision.Issues, i => Assert.Contains("Model B", i.Message));
    }

    [Fact]
    public void A_failure_that_predates_the_task_is_repaired_only_when_the_user_chose_that()
    {
        var result = Builders.GateResult(status: GateStatus.Failed, failsOnBaseline: true);
        var profile = Builders.Profile(new QualityPolicy(RepairPreExistingFailures: true));

        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(profile: profile, results: [result]));

        var issue = Assert.Single(decision.Issues);
        Assert.Equal(IssueResolution.RepairByImplementer, issue.Resolution);
        Assert.Contains("also fails on the unmodified baseline", issue.Message);
        Assert.False(decision.Accepted);
        Assert.True(decision.CanRepair);
    }

    [Theory]
    [InlineData(GateStatus.Unverified)]
    [InlineData(GateStatus.TimedOut)]
    [InlineData(GateStatus.Error)]
    public void A_gate_that_could_not_be_verified_is_never_a_pass(GateStatus status)
    {
        var result = Builders.GateResult(status: status, limitation: "DATABASE_URL is not set");

        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(results: [result]));

        Assert.False(decision.Accepted);
        Assert.Equal(IssueResolution.UserDecision, Assert.Single(decision.Issues).Resolution);
    }

    [Fact]
    public void An_explicit_waiver_for_this_candidate_lets_a_failed_gate_through()
    {
        var waiver = new GateWaiver("run-1", "test", Builders.Fingerprint, "Known flaky test", Builders.Now);

        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(
            results: [Builders.GateResult(status: GateStatus.Failed)],
            waivers: [waiver]));

        Assert.True(decision.Accepted);
    }

    [Fact]
    public void A_waiver_for_another_candidate_does_not_apply()
    {
        var waiver = new GateWaiver("run-1", "test", Builders.OtherFingerprint, "Known flaky test", Builders.Now);

        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(
            results: [Builders.GateResult(status: GateStatus.Failed)],
            waivers: [waiver]));

        Assert.False(decision.Accepted);
    }

    [Fact]
    public void No_configured_gate_blocks_when_gates_are_required()
    {
        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(gates: [], results: []));

        var issue = Assert.Single(decision.Issues);
        Assert.Equal(AcceptanceIssueKind.GatesNotConfigured, issue.Kind);
    }

    [Fact]
    public void No_configured_gate_is_accepted_when_the_user_made_gates_optional()
    {
        var profile = Builders.Profile(new QualityPolicy(RequireGates: false));

        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(profile: profile, gates: [], results: []));

        Assert.True(decision.Accepted);
    }

    [Fact]
    public void An_effort_the_provider_lowered_blocks()
    {
        var confirmations = Builders.AllConfirmed().ToList();
        confirmations.Add(new ProfileConfirmation(
            AgentRole.Implementer, ProfileSettings.Effort, "xhigh", "high", VerificationStatus.Mismatch, "init message", Builders.Now.AddMinutes(1)));

        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(confirmations: confirmations));

        Assert.False(decision.Accepted);
        var issue = Assert.Single(decision.Issues);
        Assert.Equal(AcceptanceIssueKind.ProfileNotHonored, issue.Kind);
        Assert.Contains("requested 'xhigh'", issue.Message);
        Assert.Contains("'high'", issue.Message);
    }

    [Fact]
    public void A_setting_that_was_never_confirmed_blocks_under_strict_policy()
    {
        var confirmations = Builders.AllConfirmed().Where(c => !(c.Role == AgentRole.Reviewer && c.Setting == ProfileSettings.Effort)).ToList();

        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(confirmations: confirmations));

        var issue = Assert.Single(decision.Issues);
        Assert.Equal(AcceptanceIssueKind.ProfileUnverified, issue.Kind);
        Assert.Contains("Model B effort", issue.Message);
    }

    [Fact]
    public void A_setting_that_was_never_confirmed_is_tolerated_when_strict_is_off()
    {
        var profile = Builders.Profile(new QualityPolicy(Strict: false));
        var confirmations = Builders.AllConfirmed().Where(c => c.Setting != ProfileSettings.Effort).ToList();

        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(profile: profile, confirmations: confirmations));

        Assert.True(decision.Accepted);
    }

    [Fact]
    public void A_mismatch_blocks_even_when_strict_is_off()
    {
        var profile = Builders.Profile(new QualityPolicy(Strict: false));
        var confirmations = Builders.AllConfirmed().ToList();
        confirmations.Add(new ProfileConfirmation(
            AgentRole.Reviewer, ProfileSettings.Model, "model-b", "model-small", VerificationStatus.Mismatch, "init", Builders.Now.AddMinutes(1)));

        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(profile: profile, confirmations: confirmations));

        Assert.Single(decision.Issues, i => i.Kind == AcceptanceIssueKind.ProfileNotHonored);
    }

    [Fact]
    public void The_same_model_in_both_roles_blocks_under_quality_lock()
    {
        var profile = Builders.Profile(modelA: "same", modelB: "same");
        var confirmations = new[]
        {
            Builders.Confirmed(AgentRole.Implementer, ProfileSettings.Model, "same"),
            Builders.Confirmed(AgentRole.Implementer, ProfileSettings.Effort, "xhigh"),
            Builders.Confirmed(AgentRole.Reviewer, ProfileSettings.Model, "same"),
            Builders.Confirmed(AgentRole.Reviewer, ProfileSettings.Effort, "xhigh"),
        };

        var decision = AcceptanceGate.Evaluate(Builders.Acceptance(profile: profile, confirmations: confirmations));

        Assert.Single(decision.Issues, i => i.Kind == AcceptanceIssueKind.SingleModel);
    }

    [Fact]
    public void A_changed_protected_path_needs_explicit_approval()
    {
        var candidate = Builders.Candidate(protectedPaths: ["tests/acceptance/login.spec.ts"]);

        var blocked = AcceptanceGate.Evaluate(Builders.Acceptance(candidate: candidate));
        var approved = AcceptanceGate.Evaluate(Builders.Acceptance(candidate: candidate, approvedProtected: ["tests/acceptance/login.spec.ts"]));

        Assert.Single(blocked.Issues, i => i.Kind == AcceptanceIssueKind.ProtectedPathChanged);
        Assert.True(approved.Accepted);
    }

    [Fact]
    public void Findings_and_failed_gates_are_repaired_together_while_user_decisions_remain_visible()
    {
        var input = Builders.Acceptance(
            candidate: Builders.Candidate(protectedPaths: ["yav.project.json"]),
            review: Builders.Review(ReviewVerdict.ChangesRequired, findings: [Builders.Finding()]),
            results: [Builders.GateResult(status: GateStatus.Failed)]);

        var decision = AcceptanceGate.Evaluate(input);

        Assert.True(decision.CanRepair);
        Assert.Equal(2, decision.Repairable.Count());
        Assert.Single(decision.NeedingUser);
    }

    [Fact]
    public void Stale_evidence_is_rechecked_before_any_repair()
    {
        var input = Builders.Acceptance(
            review: Builders.Review(ReviewVerdict.ChangesRequired, findings: [Builders.Finding()]),
            results: [Builders.GateResult(binding: Builders.Binding(fingerprint: Builders.OtherFingerprint))]);

        var decision = AcceptanceGate.Evaluate(input);

        Assert.False(decision.CanRepair);
        Assert.Single(decision.NeedingRecheck);
    }

    [Fact]
    public void The_same_evidence_always_gives_the_same_decision()
    {
        var input = Builders.Acceptance(review: Builders.Review(ReviewVerdict.ChangesRequired, findings: [Builders.Finding()]));

        var first = AcceptanceGate.Evaluate(input);
        var second = AcceptanceGate.Evaluate(input);

        Assert.Equal(first.Accepted, second.Accepted);
        Assert.Equal(first.Issues, second.Issues);
    }
}
