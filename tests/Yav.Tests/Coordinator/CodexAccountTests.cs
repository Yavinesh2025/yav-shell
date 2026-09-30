using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.Coordinator;

/// <summary>What a run does with what Codex says about the account a conversation works with.</summary>
public class CodexAccountTests
{
    private const string Task = "Make the app say fixed.";

    [Fact]
    public async Task A_run_stops_when_codex_says_during_a_turn_that_it_now_works_with_another_account()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.TextGate("tests", "src/app.txt", "fixed"));
        harness.Agents
            .ImplementerTurn(Step.Message("Working...", "commentary"), Step.AccountUpdated("apikey"), Step.Hang())
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains("billing route", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("OpenAI API key", outcome.Reason, StringComparison.Ordinal);
        Assert.Single(harness.Agents.CodexRequests("turn/interrupt"));
        Assert.Equal("one\n", harness.ReadProject("src/app.txt"));
    }
}
