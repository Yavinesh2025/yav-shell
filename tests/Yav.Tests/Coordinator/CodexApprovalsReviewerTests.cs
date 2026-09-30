using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.Coordinator;

/// <summary>What a run does when Codex reports that someone other than the user decides its requests for access.</summary>
public class CodexApprovalsReviewerTests
{
    private const string Task = "Make the app say fixed.";

    private static CoordinatorHarness Harness()
    {
        var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.TextGate("tests", "src/app.txt", "fixed"));
        return harness;
    }

    [Fact]
    public async Task A_run_is_blocked_before_anything_is_sent_when_codex_would_decide_approvals_itself()
    {
        await using var harness = Harness();
        harness.Agents
            .Codex(c => c["effectiveApprovalsReviewer"] = "auto_review")
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains("'auto_review'", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("only you decide about access", outcome.Reason, StringComparison.Ordinal);
        Assert.Empty(harness.Agents.CodexRequests("turn/start"));
        Assert.Equal("one\n", harness.ReadProject("src/app.txt"));
    }

    [Fact]
    public async Task A_run_stops_when_codex_hands_approvals_to_its_own_reviewer_during_a_turn()
    {
        await using var harness = Harness();
        harness.Agents
            .ImplementerTurn(Step.Message("Working...", "commentary"), Step.SettingsUpdated(approvalsReviewer: "auto_review"), Step.Hang())
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        // What Codex did while its own reviewer decided is not known to YAV, so the workspace is checked first.
        harness.AssertEnded(RunOutcomeKind.NeedsReconciliation, outcome);
        Assert.Contains("only you decide about access", outcome.Reason, StringComparison.Ordinal);
        Assert.Single(harness.Agents.CodexRequests("turn/start"));
        Assert.Single(harness.Agents.CodexRequests("turn/interrupt"));
        Assert.Equal("one\n", harness.ReadProject("src/app.txt"));
    }
}
