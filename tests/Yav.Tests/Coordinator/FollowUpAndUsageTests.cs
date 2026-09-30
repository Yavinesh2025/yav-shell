using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.Coordinator;

public class FollowUpTests
{
    private static CoordinatorHarness Harness()
    {
        var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Usage(1_000, 200, 300, 50, 1_000, 200, 300, 50))
            .ReviewerTurn(Step.Review("pass"), Step.Usage(2_000, 0, 100, 10, 2_000, 0, 100, 10))
            .ImplementerTurn(Step.Write("README.md", "# App\nNow fixed.\n"), Step.Usage(1_800, 900, 450, 60, 800, 700, 150, 10))
            .ReviewerTurn(Step.Review("pass"), Step.Usage(4_500, 1_000, 180, 15, 2_500, 1_000, 80, 5));
        return harness;
    }

    [Fact]
    public async Task A_follow_up_continues_in_the_same_workspace_and_the_same_conversations()
    {
        await using var harness = Harness();
        var first = await harness.RunAsync("Make the app say fixed.");

        var second = await harness.RunAsync("Also mention it in the README.", taskId: first.TaskId);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, second);
        Assert.Equal(first.TaskId, second.TaskId);
        Assert.NotEqual(first.RunId, second.RunId);
        Assert.Equal(2, harness.Agents.CodexRequests("thread/start").Count);
        var turns = harness.Agents.CodexRequests("turn/start").Select(t => t["params"]!["threadId"]!.GetValue<string>()).ToList();
        Assert.Equal(4, turns.Count);
        Assert.Equal(turns[0], turns[2]);
        Assert.Equal(turns[1], turns[3]);
        Assert.Equal(
            ["README.md", "src/app.txt"],
            second.Candidate!.Changes.Files.Select(f => f.Path).Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_follow_up_adds_a_requirement_and_both_models_are_given_all_of_them()
    {
        await using var harness = Harness();
        var first = await harness.RunAsync("Make the app say fixed.");

        var second = await harness.RunAsync("Also mention it in the README.", taskId: first.TaskId);

        var requirements = harness.Database.GetRequirements(first.TaskId!);
        Assert.Equal([1, 2], requirements.Select(r => r.Version).ToArray());
        Assert.Equal(2, harness.Database.FindRun(second.RunId)!.AcceptanceVersion);
        Assert.Equal(2, second.Candidate!.AcceptanceVersion);
        foreach (var prompt in new[] { harness.ImplementerPrompts()[1], harness.ReviewerPrompts()[1] })
        {
            Assert.Contains("Make the app say fixed.", prompt, StringComparison.Ordinal);
            Assert.Contains("Also mention it in the README.", prompt, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Evidence_of_the_earlier_requirements_does_not_count_for_the_follow_up()
    {
        await using var harness = Harness();
        var first = await harness.RunAsync("Make the app say fixed.");

        var second = await harness.RunAsync("Also mention it in the README.", taskId: first.TaskId);

        var review = harness.Database.GetLatestReview(second.RunId, second.Candidate!.CandidateId)!;
        Assert.Equal(2, review.Binding.AcceptanceVersion);
        Assert.Equal(second.Candidate.Fingerprint, review.Binding.CandidateFingerprint);
        Assert.All(harness.Database.GetGateResults(second.RunId), r => Assert.Equal(2, r.Binding.AcceptanceVersion));
        Assert.Equal(2, harness.ReviewerPrompts().Count);
    }

    [Fact]
    public async Task The_earlier_run_can_no_longer_be_applied_by_itself_once_a_follow_up_continued_from_it()
    {
        await using var harness = Harness();
        var first = await harness.RunAsync("Make the app say fixed.");
        await harness.RunAsync("Also mention it in the README.", taskId: first.TaskId);

        var delivery = await harness.Coordinator.ApplyAsync(first.RunId, harness.Observer, CancellationToken.None);

        Assert.False(delivery.Succeeded);
        var stored = harness.Database.FindRun(first.RunId)!;
        Assert.Equal(RunState.Blocked, stored.State);
        Assert.Contains("Superseded", stored.StateReason, StringComparison.Ordinal);
        Assert.Equal("one\n", harness.ReadProject("src/app.txt"));
    }

    [Fact]
    public async Task A_follow_up_after_an_apply_shows_only_its_own_changes()
    {
        await using var harness = Harness();
        var first = await harness.RunAsync("Make the app say fixed.");
        await harness.Coordinator.ApplyAsync(first.RunId, harness.Observer, CancellationToken.None);

        var second = await harness.RunAsync("Also mention it in the README.", taskId: first.TaskId);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, second);
        Assert.Equal("README.md", Assert.Single(second.Candidate!.Changes.Files).Path);
        var delivery = await harness.Coordinator.ApplyAsync(second.RunId, harness.Observer, CancellationToken.None);
        Assert.True(delivery.Succeeded, delivery.Message);
        Assert.Equal("fixed\n", harness.ReadProject("src/app.txt"));
        Assert.Equal("# App\nNow fixed.\n", harness.ReadProject("README.md"));
    }

    [Fact]
    public async Task An_unrelated_task_gets_a_workspace_and_conversations_of_its_own()
    {
        await using var harness = Harness();
        var first = await harness.RunAsync("Make the app say fixed.");

        var second = await harness.RunAsync("Mention it in the README.");

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, second);
        Assert.NotEqual(first.TaskId, second.TaskId);
        Assert.Equal(4, harness.Agents.CodexRequests("thread/start").Count);
        Assert.NotEqual(
            harness.Database.FindTask(first.TaskId!)!.WorkspaceId,
            harness.Database.FindTask(second.TaskId!)!.WorkspaceId);
        Assert.Equal("README.md", Assert.Single(second.Candidate!.Changes.Files).Path);
        Assert.DoesNotContain("Make the app say fixed.", harness.ImplementerPrompts()[1], StringComparison.Ordinal);
    }

    [Fact]
    public async Task One_project_has_one_writer_at_a_time()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents.ImplementerTurn(Step.Write("src/app.txt", "half\n"), Step.Hang());
        using var stop = new CancellationTokenSource();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Observer.Published += e =>
        {
            if (e is AgentActivity { Event: FilesChanged })
            {
                started.TrySetResult();
            }
        };
        var running = harness.RunAsync("Make the app say fixed.", cancellationToken: stop.Token);
        await started.Task.WaitAsync(TimeSpan.FromSeconds(30));

        var second = await harness.RunAsync("Another task.");

        Assert.Equal(RunOutcomeKind.Blocked, second.Kind);
        Assert.Contains("one coding writer at a time", second.Reason, StringComparison.Ordinal);
        Assert.NotNull(harness.Coordinator.ActiveRunFor(harness.ProjectPath));
        await stop.CancelAsync();
        await running.WaitAsync(TimeSpan.FromSeconds(30));
        Assert.Null(harness.Coordinator.ActiveRunFor(harness.ProjectPath));
    }
}

public class UsageRecordingTests
{
    private const string Task = "Make the app say fixed.";

    [Fact]
    public async Task Usage_is_recorded_for_each_role_with_its_source_and_billing_route()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Usage(input: 1_000, cached: 200, output: 300, reasoning: 50, 1_000, 200, 300, 50))
            .ReviewerTurn(Step.Review("pass"), Step.Usage(input: 2_000, cached: 0, output: 100, reasoning: 10, 2_000, 0, 100, 10));

        var outcome = await harness.RunAsync(Task);

        var usage = harness.Database.GetUsage(outcome.RunId, null);
        var implementer = Assert.Single(usage, u => u.Role == AgentRole.Implementer);
        var reviewer = Assert.Single(usage, u => u.Role == AgentRole.Reviewer);

        // Cached input is part of the input the provider counts, so it is not added on top.
        Assert.Equal(new TokenCounts(800, 200, null, 300, 50), implementer.Tokens);
        Assert.Equal(1_300, implementer.Tokens.Total);
        Assert.Equal(2_100, reviewer.Tokens.Total);
        Assert.Equal("ChatGPT plan (pro)", implementer.BillingRoute);
        Assert.Equal(1, implementer.Turns);
        Assert.True(implementer.RunShareKnown);
        Assert.Equal("model-a", implementer.Model);
        Assert.Equal("model-b", reviewer.Model);
        Assert.False(string.IsNullOrEmpty(implementer.Source));
    }

    [Fact]
    public async Task A_charge_the_provider_did_not_report_is_unavailable_and_never_zero()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Usage(1_000, 200, 300, 50, 1_000, 200, 300, 50))
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        var usage = harness.Database.GetUsage(outcome.RunId, null);
        var implementer = usage.Single(u => u.Role == AgentRole.Implementer);
        var reviewer = usage.Single(u => u.Role == AgentRole.Reviewer);
        Assert.Null(implementer.ProviderCostUsd);
        Assert.Equal(ValueProvenance.Unavailable, implementer.CostProvenance);
        Assert.Null(reviewer.Tokens.Total);
        Assert.Equal(TokenCounts.Unavailable, reviewer.Tokens);
        Assert.Equal(1, reviewer.Turns);
    }

    [Fact]
    public async Task A_follow_up_is_charged_only_with_its_own_share_of_a_continued_conversation()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Usage(1_000, 200, 300, 50, 1_000, 200, 300, 50))
            .ReviewerTurn(Step.Review("pass"), Step.Usage(2_000, 0, 100, 10, 2_000, 0, 100, 10))
            .ImplementerTurn(Step.Write("README.md", "# App\nNow fixed.\n"), Step.Usage(1_800, 900, 450, 60, 800, 700, 150, 10))
            .ReviewerTurn(Step.Review("pass"), Step.Usage(4_500, 1_000, 180, 15, 2_500, 1_000, 80, 5));
        var first = await harness.RunAsync(Task);

        var second = await harness.RunAsync("Also mention it in the README.", taskId: first.TaskId);

        var implementer = harness.Database.GetUsage(second.RunId, null).Single(u => u.Role == AgentRole.Implementer);
        // The conversation's totals are 900 uncached, 900 cached and 450 output; the first run had 800, 200 and 300 of them.
        Assert.Equal(new TokenCounts(100, 700, null, 150, 10), implementer.Tokens);
        Assert.True(implementer.RunShareKnown);
        var all = harness.Database.GetUsage(null, null).Where(u => u.Role == AgentRole.Implementer).ToList();
        Assert.Equal(2, all.Count);
        Assert.Equal(1_300 + 950, all.Sum(u => u.Tokens.Total!.Value));
    }

    [Fact]
    public async Task Repair_turns_are_counted_as_turns_of_the_same_run()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "a bug\n"), Step.Usage(1_000, 0, 100, 0, 1_000, 0, 100, 0))
            .ReviewerTurn(Step.Review("pass"))
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Usage(2_500, 0, 250, 0, 1_500, 0, 150, 0))
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        var implementer = harness.Database.GetUsage(outcome.RunId, null).Single(u => u.Role == AgentRole.Implementer);
        Assert.Equal(2, implementer.Turns);
        Assert.Equal(2_750, implementer.Tokens.Total);
        Assert.Equal(2, harness.Database.GetUsage(outcome.RunId, null).Single(u => u.Role == AgentRole.Reviewer).Turns);
    }

    [Fact]
    public async Task Retries_of_the_provider_are_counted()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents
            .ImplementerTurn(Step.Error("stream disconnected", willRetry: true), Step.Error("stream disconnected", willRetry: true), Step.Write("src/app.txt", "fixed\n"))
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Equal(2, harness.Database.GetUsage(outcome.RunId, null).Single(u => u.Role == AgentRole.Implementer).Retries);
    }
}

public class TwoProviderTests
{
    private const string Task = "Make the app say fixed.";

    [Fact]
    public async Task The_reviewer_can_be_a_model_of_another_provider()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Configuration = harness.Configuration with { ModelB = new RoleSelection(CoordinatorHarness.ClaudeId, "opus") };
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        var profile = harness.Database.GetProfile(outcome.RunId)!;
        Assert.Equal("openai", profile.Implementer.Provider);
        Assert.Equal("anthropic", profile.Reviewer.Provider);
        Assert.Single(harness.Agents.CodexRequests("thread/start"));
        var review = harness.Database.GetLatestReview(outcome.RunId, outcome.Candidate!.CandidateId)!;
        Assert.Equal(CoordinatorHarness.ClaudeId, review.ReviewerAdapterId);
        Assert.True(review.IsCleanPass);
        var sandbox = harness.Database.GetConfirmations(outcome.RunId).Single(c => c.Role == AgentRole.Reviewer && c.Setting == ProfileSettings.Sandbox);
        Assert.Equal(VerificationStatus.Verified, sandbox.Status);
    }

    [Fact]
    public async Task The_roles_can_be_swapped()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Configuration = harness.Configuration with
        {
            ModelA = new RoleSelection(CoordinatorHarness.ClaudeId, "opus"),
            ModelB = new RoleSelection(CoordinatorHarness.CodexId, "model-b"),
        };
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        var start = Assert.Single(harness.Agents.CodexRequests("thread/start"));
        Assert.Equal("read-only", start["params"]!["sandbox"]!.GetValue<string>());
        Assert.Equal("anthropic", harness.Database.GetProfile(outcome.RunId)!.Implementer.Provider);
    }

    [Fact]
    public async Task A_second_model_that_cannot_run_blocks_the_run_and_names_the_exact_reason()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Configuration = harness.Configuration with { ModelB = new RoleSelection(CoordinatorHarness.ClaudeId, "opus") };
        harness.Agents.Claude(c => c["auth"] = new System.Text.Json.Nodes.JsonObject { ["loggedIn"] = false });

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains(outcome.Problems, p => p.Role == AgentRole.Reviewer && p.Code == "auth-missing");
        Assert.Empty(harness.Agents.CodexRequests("turn/start"));
    }
}
