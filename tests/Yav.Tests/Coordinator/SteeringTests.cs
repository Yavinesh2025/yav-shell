using Yav.Adapters;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.Coordinator;

/// <summary>
/// A request that is added to a turn that is running changes what the task has to fulfil. It is recorded
/// as a requirement, and nothing that was checked against fewer requirements counts any more.
/// </summary>
public class SteeringTests
{
    private const string Task = "Make the app say fixed.";
    private const string Addition = "Also end the file with a line break.";

    private static async Task<string> ActiveRunAsync(CoordinatorHarness harness, Func<bool> reached)
    {
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!reached() || harness.Coordinator.ActiveRunFor(harness.ProjectPath) is null)
        {
            Assert.True(DateTime.UtcNow < deadline, "the run did not get there\n" + harness.Observer.Transcript());
            await System.Threading.Tasks.Task.Delay(25);
        }

        return harness.Coordinator.ActiveRunFor(harness.ProjectPath)!;
    }

    /// <summary>What a turn of the stand-in for Codex waits for: the test has steered, or tried to.</summary>
    private const string Steered = "steered";

    private static void Signal(CoordinatorHarness harness) =>
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(harness.Agents.Workspace)!, Steered), "go");

    private static bool Implementing(CoordinatorHarness harness) =>
        harness.Agents.CodexRequests("turn/start").Count == 1 && harness.Observer.States().LastOrDefault() == RunState.Implementing;

    [Fact]
    public async Task What_is_added_to_the_running_turn_is_a_requirement_that_both_models_are_given()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents
            .ImplementerTurn(Step.WaitForSignal(Steered), Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"));

        var running = harness.RunAsync(Task);
        var runId = await ActiveRunAsync(harness, () => Implementing(harness));
        var accepted = await harness.Coordinator.SteerAsync(runId, Addition, CancellationToken.None);
        Signal(harness);
        var outcome = await running;

        Assert.True(accepted);
        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        var steered = Assert.Single(harness.Agents.CodexRequests("turn/steer"));
        Assert.Equal(Addition, steered["params"]!["input"]![0]!["text"]!.GetValue<string>());
        Assert.Equal([Task, Addition], harness.Database.GetRequirements(outcome.TaskId!).Select(r => r.Text).ToArray());

        var review = Assert.Single(harness.ReviewerPrompts());
        Assert.Contains(Task, review, StringComparison.Ordinal);
        Assert.Contains(Addition, review, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Everything_that_is_checked_afterwards_is_bound_to_the_requirements_as_they_are_now()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents
            .ImplementerTurn(Step.WaitForSignal(Steered), Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"));

        var running = harness.RunAsync(Task);
        var runId = await ActiveRunAsync(harness, () => Implementing(harness));
        await harness.Coordinator.SteerAsync(runId, Addition, CancellationToken.None);
        Signal(harness);
        var outcome = await running;

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Equal(2, outcome.Candidate!.AcceptanceVersion);
        Assert.Equal(2, harness.Database.FindRun(outcome.RunId)!.AcceptanceVersion);
        Assert.Equal(2, harness.Database.GetLatestReview(outcome.RunId, outcome.Candidate.CandidateId)!.Binding.AcceptanceVersion);
        Assert.All(
            harness.Database.GetGateResults(outcome.RunId).Where(r => !r.IsBaselineRun),
            result => Assert.Equal(2, result.Binding.AcceptanceVersion));
        Assert.Contains(harness.Observer.Notes(Stages.CodeA), n => n.Message.Contains("requirement 2", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task What_is_added_during_a_repair_counts_for_the_repaired_candidate_and_not_for_what_was_checked_before()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("changes_required", file: "src/app.txt", title: "The empty case is not handled"))
            .ImplementerTurn(Step.WaitForSignal(Steered), Step.Write("src/app.txt", "fixed, also when empty\n"), Step.Message("Repaired."))
            .ReviewerTurn(Step.Review("pass"));

        var running = harness.RunAsync(Task);
        var runId = await ActiveRunAsync(harness, () => harness.Observer.States().LastOrDefault() == RunState.Repairing && harness.Agents.CodexRequests("turn/start").Count == 3);
        var accepted = await harness.Coordinator.SteerAsync(runId, Addition, CancellationToken.None);
        Signal(harness);
        var outcome = await running;

        Assert.True(accepted);
        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.True(outcome.Decision!.Accepted);
        Assert.Equal(2, outcome.Candidate!.AcceptanceVersion);
        var results = harness.Database.GetGateResults(outcome.RunId).Where(r => !r.IsBaselineRun).ToList();
        Assert.Equal([1, 2], results.Select(r => r.Binding.AcceptanceVersion).Order().ToArray());
        Assert.Equal(2, harness.ReviewerPrompts().Count);
        Assert.DoesNotContain(Addition, harness.ReviewerPrompts()[0], StringComparison.Ordinal);
        Assert.Contains(Addition, harness.ReviewerPrompts()[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_candidate_that_was_accepted_for_fewer_requirements_is_not_accepted_for_more()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"));
        var outcome = await harness.RunAsync(Task);
        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);

        // A requirement that reaches the task after its candidate was accepted, as it would through another run of the task.
        harness.Database.SaveRequirement(outcome.TaskId!, new Requirement(2, Addition, DateTimeOffset.UtcNow, []));
        harness.Database.UpdateRun(outcome.RunId, run => run with { AcceptanceVersion = 2 });
        var delivery = await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);

        Assert.False(delivery.Succeeded);
        Assert.Equal("one\n", harness.ReadProject("src/app.txt"));
        Assert.Contains("The evidence no longer covers this candidate", delivery.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task While_the_candidate_is_checked_nothing_can_be_added_and_nothing_is_recorded()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
            .ReviewerTurn(Step.WaitForSignal(Steered), Step.Review("pass"));

        var running = harness.RunAsync(Task);
        var runId = await ActiveRunAsync(harness, () => harness.Observer.States().LastOrDefault() == RunState.Checking && harness.Agents.CodexRequests("turn/start").Count == 2);
        var accepted = await harness.Coordinator.SteerAsync(runId, Addition, CancellationToken.None);
        Signal(harness);
        var outcome = await running;

        Assert.False(accepted);
        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Empty(harness.Agents.CodexRequests("turn/steer"));
        Assert.Equal([Task], harness.Database.GetRequirements(outcome.TaskId!).Select(r => r.Text).ToArray());
        Assert.Equal(1, outcome.Candidate!.AcceptanceVersion);
    }

    [Fact]
    public async Task What_the_agent_did_not_take_is_no_requirement()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents.Codex(c => c["refuseSteer"] = true);
        harness.Agents
            .ImplementerTurn(Step.WaitForSignal(Steered), Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"));

        var running = harness.RunAsync(Task);
        var runId = await ActiveRunAsync(harness, () => Implementing(harness));
        var accepted = await harness.Coordinator.SteerAsync(runId, Addition, CancellationToken.None);
        Signal(harness);
        var outcome = await running;

        Assert.False(accepted);
        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Single(harness.Agents.CodexRequests("turn/steer"));
        Assert.Equal([Task], harness.Database.GetRequirements(outcome.TaskId!).Select(r => r.Text).ToArray());
        Assert.Equal(1, outcome.Candidate!.AcceptanceVersion);
        Assert.DoesNotContain(Addition, Assert.Single(harness.ReviewerPrompts()), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public async Task Nothing_is_added_when_there_is_nothing_to_add(string text)
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents
            .ImplementerTurn(Step.WaitForSignal(Steered), Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"));

        var running = harness.RunAsync(Task);
        var runId = await ActiveRunAsync(harness, () => Implementing(harness));
        var accepted = await harness.Coordinator.SteerAsync(runId, text, CancellationToken.None);
        Signal(harness);
        var outcome = await running;

        Assert.False(accepted);
        Assert.Empty(harness.Agents.CodexRequests("turn/steer"));
        Assert.Single(harness.Database.GetRequirements(outcome.TaskId!));
    }

    [Fact]
    public async Task An_agent_that_cannot_take_input_during_a_turn_is_not_given_any_and_nothing_is_recorded()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Configuration = harness.Configuration with
        {
            ModelA = new RoleSelection(ClaudeCliAdapter.AdapterId, "opus"),
            Policy = new QualityPolicy(Strict: false),
        };
        harness.Agents
            .ImplementerTurn(Step.Sleep(1_500), Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"));
        Assert.False(harness.Claude.Capabilities.Has(Yav.Core.Agents.AdapterFeatures.Steering));

        var running = harness.RunAsync(Task);
        var runId = await ActiveRunAsync(harness, () => harness.Observer.States().LastOrDefault() == RunState.Implementing && harness.Agents.Received("claude.input").Count > 0);
        var accepted = await harness.Coordinator.SteerAsync(runId, Addition, CancellationToken.None);
        var outcome = await running;

        Assert.False(accepted);
        Assert.Equal([Task], harness.Database.GetRequirements(outcome.TaskId!).Select(r => r.Text).ToArray());
        Assert.DoesNotContain(harness.Agents.Received("claude.input"), m => m.ToJsonString().Contains(Addition, StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_follow_up_after_a_steered_run_is_given_all_three_requirements()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents
            .ImplementerTurn(Step.WaitForSignal(Steered), Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"))
            .ImplementerTurn(Step.Write("README.md", "# App\nfixed\n"), Step.Message("Done again."))
            .ReviewerTurn(Step.Review("pass"));
        var running = harness.RunAsync(Task);
        var runId = await ActiveRunAsync(harness, () => Implementing(harness));
        await harness.Coordinator.SteerAsync(runId, Addition, CancellationToken.None);
        Signal(harness);
        var first = await running;

        var second = await harness.RunAsync("Say it in the README as well.", first.TaskId);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, second);
        Assert.Equal(3, second.Candidate!.AcceptanceVersion);
        var review = harness.ReviewerPrompts()[^1];
        Assert.Contains(Task, review, StringComparison.Ordinal);
        Assert.Contains(Addition, review, StringComparison.Ordinal);
        Assert.Contains("Say it in the README as well.", review, StringComparison.Ordinal);
    }
}
