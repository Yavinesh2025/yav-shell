using Yav.Core.Agents;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.Coordinator;

public class RepairLoopTests
{
    private const string Task = "Make the app say fixed.";

    /// <summary>The required check passes on the project as it is and fails when a change introduces "bug".</summary>
    private static CoordinatorHarness Harness()
    {
        var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        return harness;
    }

    [Fact]
    public async Task The_implementer_is_told_in_its_task_and_in_a_repair_which_checks_yav_runs_itself()
    {
        await using var harness = Harness();
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed, with a bug\n"))
            .ReviewerTurn(Step.Review("pass"))
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"))
            .ReviewerTurn(Step.Review("pass"));

        await harness.RunAsync(Task);

        var prompts = harness.ImplementerPrompts();
        Assert.Equal(2, prompts.Count);
        Assert.All(prompts, prompt => Assert.Contains("## Required checks", prompt, StringComparison.Ordinal));
        Assert.All(prompts, prompt => Assert.Contains("\n- tests: ", prompt, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_finding_goes_back_to_the_implementer_and_the_repaired_candidate_is_checked_again()
    {
        await using var harness = Harness();
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("changes_required", file: "src/app.txt", title: "The empty case is not handled"))
            .ImplementerTurn(Step.Write("src/app.txt", "fixed, also when empty\n"), Step.Message("Handled the empty case."))
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Equal(2, outcome.Candidate!.Sequence);
        Assert.Equal(1, harness.Database.FindRun(outcome.RunId)!.RepairCyclesUsed);
        Assert.Equal(2, harness.Database.GetCandidates(outcome.RunId).Count);
        Assert.Equal(
            [RunState.Implementing, RunState.Checking, RunState.Repairing, RunState.Checking, RunState.ReadyToApply],
            harness.Observer.States().ToArray());

        var repair = harness.ImplementerPrompts()[1];
        Assert.Contains("The empty case is not handled", repair, StringComparison.Ordinal);
        Assert.Contains(Task, repair, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_implementer_and_the_reviewer_keep_their_conversations_for_the_repair()
    {
        await using var harness = Harness();
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"))
            .ReviewerTurn(Step.Review("changes_required"))
            .ImplementerTurn(Step.Write("src/app.txt", "fixed better\n"))
            .ReviewerTurn(Step.Review("pass"));

        await harness.RunAsync(Task);

        Assert.Equal(2, harness.Agents.CodexRequests("thread/start").Count);
        var threads = harness.Agents.CodexRequests("turn/start").Select(t => t["params"]!["threadId"]!.GetValue<string>()).ToList();
        Assert.Equal(4, threads.Count);
        Assert.Equal(threads[0], threads[2]);
        Assert.Equal(threads[1], threads[3]);
        Assert.NotEqual(threads[0], threads[1]);
    }

    [Fact]
    public async Task The_second_review_is_told_which_findings_to_verify_and_sees_the_new_diff()
    {
        await using var harness = Harness();
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"))
            .ReviewerTurn(Step.Review("changes_required", file: "src/app.txt", title: "The empty case is not handled"))
            .ImplementerTurn(Step.Write("src/app.txt", "fixed, also when empty\n"))
            .ReviewerTurn(Step.Review("pass"));

        await harness.RunAsync(Task);

        var second = harness.ReviewerPrompts()[1];
        Assert.Contains("Earlier findings to verify", second, StringComparison.Ordinal);
        Assert.Contains("The empty case is not handled", second, StringComparison.Ordinal);
        Assert.Contains("+fixed, also when empty", second, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_failed_required_check_goes_back_to_the_implementer_with_its_output()
    {
        await using var harness = Harness();
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed, with a bug\n"))
            .ReviewerTurn(Step.Review("pass"))
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"))
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        var repair = harness.ImplementerPrompts()[1];
        Assert.Contains("Failed validation", repair, StringComparison.Ordinal);
        Assert.Contains("FAIL src/app.txt contains 'bug'", repair, StringComparison.Ordinal);
        var results = harness.Database.GetGateResults(outcome.RunId).Where(r => !r.IsBaselineRun).ToList();
        Assert.Equal([GateStatus.Failed, GateStatus.Passed], results.Select(r => r.Status).ToArray());
        Assert.False(results[0].FailsOnBaseline);
    }

    [Fact]
    public async Task Findings_and_failed_checks_are_sent_in_one_repair_request()
    {
        await using var harness = Harness();
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed, with a bug\n"))
            .ReviewerTurn(Step.Review("changes_required", title: "The empty case is not handled"))
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"))
            .ReviewerTurn(Step.Review("pass"));

        await harness.RunAsync(Task);

        Assert.Equal(2, harness.ImplementerPrompts().Count);
        var repair = harness.ImplementerPrompts()[1];
        Assert.Contains("Failed validation", repair, StringComparison.Ordinal);
        Assert.Contains("The empty case is not handled", repair, StringComparison.Ordinal);
    }

    [Fact]
    public async Task After_the_allowed_repair_cycles_the_run_is_blocked_and_nothing_is_lost()
    {
        await using var harness = Harness();
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "bug 1\n"))
            .ReviewerTurn(Step.Review("pass"))
            .ImplementerTurn(Step.Write("src/app.txt", "bug 2\n"))
            .ReviewerTurn(Step.Review("pass"))
            .ImplementerTurn(Step.Write("src/app.txt", "bug 3\n"))
            .ReviewerTurn(Step.Review("pass"))
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Equal(RunState.Blocked, harness.Database.FindRun(outcome.RunId)!.State);
        Assert.Equal(2, harness.Database.FindRun(outcome.RunId)!.RepairCyclesUsed);
        Assert.Equal(3, harness.ImplementerPrompts().Count);
        Assert.Contains("2 repair cycle(s)", outcome.Reason, StringComparison.Ordinal);
        Assert.Equal(3, outcome.Candidate!.Sequence);
        Assert.False(outcome.Decision!.Accepted);
        Assert.Equal("one\n", harness.ReadProject("src/app.txt"));
    }

    [Fact]
    public async Task The_number_of_repair_cycles_is_the_users_choice()
    {
        await using var harness = Harness();
        harness.Configuration = harness.Configuration with
        {
            Policy = new QualityPolicy(MaxRepairCycles: 0),
            Limits = RunLimits.Default with { MaxRepairCycles = 0 },
        };
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed, with a bug\n"))
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Single(harness.ImplementerPrompts());
    }

    [Fact]
    public async Task A_repair_that_changes_nothing_ends_the_loop_instead_of_repeating_it()
    {
        await using var harness = Harness();
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"))
            .ReviewerTurn(Step.Review("changes_required", title: "Rename the variable"))
            .ImplementerTurn(Step.Message("The finding is wrong: there is no variable in this file."));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains("changed nothing", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("there is no variable", outcome.Reason, StringComparison.Ordinal);
        Assert.Equal(2, harness.ImplementerPrompts().Count);
        Assert.Single(harness.ReviewerPrompts());
    }

    [Fact]
    public async Task Optional_suggestions_do_not_block_and_are_not_sent_for_repair()
    {
        await using var harness = Harness();
        var suggestion = Step.Review("pass");
        suggestion["findings"] = new System.Text.Json.Nodes.JsonArray
        {
            new System.Text.Json.Nodes.JsonObject
            {
                ["severity"] = "minor", ["category"] = "style", ["optional"] = true, ["file"] = "src/app.txt", ["line"] = 1,
                ["title"] = "Consider a friendlier wording", ["failure_scenario"] = "None.", ["evidence"] = "Line 1.",
                ["suggested_correction"] = null, ["limitation"] = null,
            },
        };
        harness.Agents.ImplementerTurn(Step.Write("src/app.txt", "fixed\n")).ReviewerTurn(suggestion);

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Single(harness.ImplementerPrompts());
        var review = harness.Database.GetLatestReview(outcome.RunId, outcome.Candidate!.CandidateId)!;
        Assert.Equal("Consider a friendlier wording", Assert.Single(review.Suggestions).Title);
    }

    [Fact]
    public async Task A_reviewer_that_cannot_verify_leaves_the_decision_to_the_user()
    {
        await using var harness = Harness();
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"))
            .ReviewerTurn(Step.Review("unable_to_verify", limitation: "The payment sandbox is not reachable."));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains("The payment sandbox is not reachable.", outcome.Reason, StringComparison.Ordinal);
        Assert.Single(harness.ImplementerPrompts());
    }

    [Fact]
    public async Task Invalid_reviewer_output_is_not_a_pass_and_is_asked_for_once_more()
    {
        await using var harness = Harness();
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"))
            .ReviewerTurn(Step.RawFinal("Looks good to me!"))
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Equal(2, harness.ReviewerPrompts().Count);
        Assert.Single(harness.ImplementerPrompts());
        var reviews = harness.Database.GetReviews(outcome.RunId);
        Assert.Equal(2, reviews.Count);
        Assert.False(reviews[0].OutputValid);
        // The required check ran once: its result for this candidate was still valid.
        Assert.Single(harness.Database.GetGateResults(outcome.RunId));
    }

    [Fact]
    public async Task Reviewer_output_that_stays_invalid_blocks_the_run()
    {
        await using var harness = Harness();
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"))
            .ReviewerTurn(Step.RawFinal("Looks good to me!"))
            .ReviewerTurn(Step.RawFinal("{\"status\": \"approved\"}"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains(outcome.Decision!.Issues, i => i.Kind == AcceptanceIssueKind.ReviewInvalid);
        Assert.Equal(2, harness.ReviewerPrompts().Count);
    }

    [Fact]
    public async Task A_check_that_already_failed_before_the_task_is_not_blamed_on_the_implementer()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(
            CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"),
            CoordinatorHarness.TextGate("legacy", "README.md", "this text was never there"));
        harness.Agents.ImplementerTurn(Step.Write("src/app.txt", "fixed\n")).ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Single(harness.ImplementerPrompts());
        var legacy = harness.Database.GetGateResults(outcome.RunId).Single(r => r.GateId == "legacy" && !r.IsBaselineRun);
        Assert.True(legacy.FailsOnBaseline);
        Assert.Contains(harness.Database.GetGateResults(outcome.RunId), r => r.GateId == "legacy" && r.IsBaselineRun && r.Status == GateStatus.Failed);
        Assert.Contains("predates this task", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_check_written_before_the_change_is_repaired_when_the_user_chose_that()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.TextGate("acceptance", "src/app.txt", "fixed"));
        harness.Configuration = harness.Configuration with { Policy = new QualityPolicy(RepairPreExistingFailures: true) };
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "almost\n"))
            .ReviewerTurn(Step.Review("pass"))
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"))
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        var repair = harness.ImplementerPrompts()[1];
        Assert.Contains("FAIL src/app.txt does not contain 'fixed'", repair, StringComparison.Ordinal);
        Assert.Contains("already failed before your change", repair, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_waiver_is_an_explicit_recorded_decision_for_exactly_one_candidate()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(
            CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"),
            CoordinatorHarness.TextGate("legacy", "README.md", "this text was never there"));
        harness.Agents.ImplementerTurn(Step.Write("src/app.txt", "fixed\n")).ReviewerTurn(Step.Review("pass"));
        var blocked = await harness.RunAsync(Task);

        Assert.True(harness.Coordinator.WaiveGate(blocked.RunId, "legacy", "Known failure, tracked as issue 12."));
        var outcome = await harness.Coordinator.ResumeAsync(blocked.RunId, harness.Configuration, harness.Observer, harness.Approvals, CancellationToken.None);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        var waiver = Assert.Single(harness.Database.GetWaivers(blocked.RunId));
        Assert.Equal(blocked.Candidate!.Fingerprint, waiver.CandidateFingerprint);
        Assert.Equal("Known failure, tracked as issue 12.", waiver.Reason);
        // Nothing was sent to either model to get there.
        Assert.Single(harness.ImplementerPrompts());
        Assert.Single(harness.ReviewerPrompts());
    }
}
