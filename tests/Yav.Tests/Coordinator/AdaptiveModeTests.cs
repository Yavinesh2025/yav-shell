using Yav.Coordinator;
using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.Coordinator;

/// <summary>
/// Adaptive mode is off unless the user turned it on. When it is on, Model A may work at a lower effort
/// for a task for which the user approved exactly that. Model B reviews at maximum effort regardless.
/// </summary>
public class AdaptiveModeTests
{
    private const string Task = "Make the app say fixed.";

    private static CoordinatorHarness PassingRun(bool adaptive)
    {
        var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"));
        harness.Configuration = harness.Configuration with { Policy = new QualityPolicy(Adaptive: adaptive) };
        return harness;
    }

    private static Task<RunOutcome> RunAsync(CoordinatorHarness harness, string? effort, string? taskId = null) =>
        harness.Coordinator.RunAsync(
            new RunRequest(harness.ProjectPath, Task) { ImplementerEffort = effort, TaskId = taskId },
            harness.Configuration, harness.Observer, harness.Approvals, CancellationToken.None);

    private static string Effort(CoordinatorHarness harness, string model) =>
        harness.Agents.CodexRequests("thread/start")
            .Single(t => t["params"]!["model"]!.GetValue<string>() == model)["params"]!["config"]!["model_reasoning_effort"]!.GetValue<string>();

    [Fact]
    public async Task The_effort_that_was_approved_for_the_task_is_what_model_a_is_asked_for_and_what_the_provider_has_to_confirm()
    {
        await using var harness = PassingRun(adaptive: true);

        var outcome = await RunAsync(harness, "medium");

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Equal("medium", Effort(harness, "model-a"));
        Assert.Equal("max", Effort(harness, "model-b"));
        var confirmations = harness.Database.GetConfirmations(outcome.RunId);
        Assert.Contains(confirmations, c => c.Role == AgentRole.Implementer && c.Setting == "effort" && c.Requested == "medium" && c.Effective == "medium" && c.Status == VerificationStatus.Verified);
        Assert.Contains(confirmations, c => c.Role == AgentRole.Reviewer && c.Setting == "effort" && c.Requested == "max" && c.Status == VerificationStatus.Verified);
        Assert.Equal("Adaptive", harness.Database.GetProfile(outcome.RunId)!.Policy.Label);
        Assert.Contains(harness.Observer.Notes(Stages.Prepare), n => n.Message.Contains("'medium' instead of 'xhigh'", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Without_an_approval_for_the_task_model_a_works_at_maximum_effort()
    {
        await using var harness = PassingRun(adaptive: true);

        var outcome = await RunAsync(harness, effort: null);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Equal("xhigh", Effort(harness, "model-a"));
        Assert.Equal("max", Effort(harness, "model-b"));
    }

    [Fact]
    public async Task With_adaptive_mode_off_a_lower_effort_blocks_the_run_before_anything_is_sent()
    {
        await using var harness = PassingRun(adaptive: false);

        var outcome = await RunAsync(harness, "medium");

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains(outcome.Problems, p => p.Code == "adaptive-off");
        Assert.Empty(harness.Agents.CodexRequests("thread/start"));
    }

    [Fact]
    public async Task A_provider_that_puts_another_effort_into_effect_than_the_approved_one_stops_the_run()
    {
        await using var harness = PassingRun(adaptive: true);
        harness.Agents.Codex(c => c["effectiveEffort:implementer"] = "low");

        var outcome = await RunAsync(harness, "medium");

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Empty(harness.Agents.CodexRequests("turn/start"));
        Assert.Contains(harness.Database.GetConfirmations(outcome.RunId), c => c.Setting == "effort" && c.Requested == "medium" && c.Effective == "low" && c.Status == VerificationStatus.Mismatch);
    }

    [Fact]
    public async Task A_follow_up_keeps_the_conversation_when_it_keeps_the_effort_of_its_task()
    {
        await using var harness = PassingRun(adaptive: true);
        harness.Agents
            .ImplementerTurn(Step.Write("README.md", "# App\nmore\n"), Step.Message("More."))
            .ReviewerTurn(Step.Review("pass"));
        var first = await RunAsync(harness, "medium");

        var second = await RunAsync(harness, "medium", first.TaskId);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, second);
        Assert.Single(harness.Agents.CodexRequests("thread/start"), t => t["params"]!["model"]!.GetValue<string>() == "model-a");
        Assert.Equal("medium", harness.Database.GetProfile(second.RunId)!.Implementer.RequestedEffort);
    }
}
