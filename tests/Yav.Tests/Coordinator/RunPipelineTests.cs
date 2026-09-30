using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.Coordinator;

public class RunPipelineTests
{
    private const string Task = "Make the app say fixed.";

    /// <summary>The implementer changes the file, the reviewer passes it, and the gate accepts the change.</summary>
    private static CoordinatorHarness PassingRun()
    {
        var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.TextGate("tests", "src/app.txt", "fixed"));
        harness.Agents
            .ImplementerTurn(Step.Reasoning("PRIVATE REASONING OF MODEL A"), Step.Write("src/app.txt", "fixed\n"), Step.Message("Changed app.txt to say fixed."))
            .ReviewerTurn(Step.Review("pass"));
        return harness;
    }

    [Fact]
    public async Task A_task_is_ready_only_after_implementation_review_and_required_checks()
    {
        await using var harness = PassingRun();

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Equal(RunState.ReadyToApply, outcome.State);
        Assert.True(outcome.Decision!.Accepted, string.Join("; ", outcome.Decision.Issues.Select(i => i.Message)));
        var changed = Assert.Single(outcome.Candidate!.Changes.Files);
        Assert.Equal("src/app.txt", changed.Path);
        Assert.Equal(ChangeKind.Modified, changed.Kind);

        var stored = harness.Database.FindRun(outcome.RunId)!;
        Assert.Equal(RunState.ReadyToApply, stored.State);
        Assert.Equal(RunDisposition.Pending, stored.Disposition);
        Assert.True(harness.Database.GetLatestReview(outcome.RunId, outcome.Candidate.CandidateId)!.IsCleanPass);
        Assert.Equal(GateStatus.Passed, Assert.Single(harness.Database.GetGateResults(outcome.RunId)).Status);
    }

    [Fact]
    public async Task A_file_the_agent_changed_is_named_as_it_is_in_the_project_and_in_full_when_it_is_outside_the_workspace()
    {
        await using var harness = new CoordinatorHarness();
        using var outside = new TempDirectory("outside");
        var elsewhere = outside.File("note.txt");
        harness.TrustGates(CoordinatorHarness.TextGate("tests", "src/app.txt", "fixed"));
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Write(elsewhere, "written outside"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"));

        await harness.RunAsync(Task);

        var named = harness.Observer.Of<AgentActivity>().Select(a => a.Event).OfType<FilesChanged>()
            .SelectMany(f => f.Changes).Select(c => c.Path).Distinct().ToList();
        Assert.Contains("src/app.txt", named);
        Assert.Contains(elsewhere, named);
        Assert.Equal(2, named.Count);
    }

    [Fact]
    public async Task The_stages_are_reported_in_the_order_they_happened()
    {
        await using var harness = PassingRun();

        await harness.RunAsync(Task);

        var order = harness.Observer.StageOrder();
        Assert.Equal(Stages.Prepare, order[0]);
        Assert.Equal(Stages.CodeA, order[1]);
        Assert.Equal(Stages.Check, order[2]);
        Assert.Contains(Stages.ReviewB, order);
        Assert.Contains(Stages.Tests, order);
        Assert.Equal(Stages.Ready, order[^1]);
        Assert.Equal(
            [RunState.Implementing, RunState.Checking, RunState.ReadyToApply],
            harness.Observer.States().ToArray());
    }

    [Fact]
    public async Task The_project_is_not_written_before_the_candidate_is_applied()
    {
        await using var harness = PassingRun();

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Equal("one\n", harness.ReadProject("src/app.txt"));
    }

    [Fact]
    public async Task Applying_writes_the_candidate_and_only_then_the_run_is_completed()
    {
        await using var harness = PassingRun();
        var outcome = await harness.RunAsync(Task);

        var delivery = await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);

        Assert.True(delivery.Succeeded, delivery.Message);
        Assert.Equal("fixed\n", harness.ReadProject("src/app.txt"));
        var stored = harness.Database.FindRun(outcome.RunId)!;
        Assert.Equal(RunState.Completed, stored.State);
        Assert.Equal(RunDisposition.Applied, stored.Disposition);
    }

    [Fact]
    public async Task An_answer_that_changes_nothing_completes_without_a_review()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.TextGate("tests", "src/app.txt", "one"));
        harness.Agents.ImplementerTurn(Step.Message("The app prints one because app.txt contains one."));

        var outcome = await harness.RunAsync("Why does the app print one?");

        harness.AssertEnded(RunOutcomeKind.Completed, outcome);
        Assert.Equal(RunDisposition.NoChanges, harness.Database.FindRun(outcome.RunId)!.Disposition);
        Assert.Equal("The app prints one because app.txt contains one.", outcome.FinalMessage);
        Assert.Single(harness.Agents.CodexRequests("turn/start"));
    }

    [Fact]
    public async Task A_configuration_that_cannot_be_honored_blocks_before_any_usage_is_spent()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.TextGate("tests", "src/app.txt", "fixed"));
        harness.Configuration = harness.Configuration with { ModelB = new RoleSelection(CoordinatorHarness.CodexId, "model-that-does-not-exist") };

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains(outcome.Problems, p => p.Code == "model-unavailable" && p.Role == AgentRole.Reviewer);
        Assert.Contains("model-that-does-not-exist", outcome.Reason, StringComparison.Ordinal);
        Assert.Empty(harness.Agents.CodexRequests("thread/start"));
        Assert.Empty(harness.Agents.CodexRequests("turn/start"));
    }

    [Fact]
    public async Task Without_trusted_gates_a_strict_run_does_not_start()
    {
        await using var harness = new CoordinatorHarness();
        harness.Agents.ImplementerTurn(Step.Write("src/app.txt", "fixed\n"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains(outcome.Problems, p => p.Code == "gates-missing");
        Assert.Empty(harness.Agents.CodexRequests("turn/start"));
    }

    [Fact]
    public async Task Each_role_is_asked_for_the_highest_effort_its_model_lists_and_the_answer_is_recorded()
    {
        await using var harness = PassingRun();

        var outcome = await harness.RunAsync(Task);

        var starts = harness.Agents.CodexRequests("thread/start");
        Assert.Equal(2, starts.Count);
        var implementer = starts.Single(s => s["params"]!["sandbox"]!.GetValue<string>() == "workspace-write")["params"]!;
        var reviewer = starts.Single(s => s["params"]!["sandbox"]!.GetValue<string>() == "read-only")["params"]!;
        Assert.Equal("model-a", implementer["model"]!.GetValue<string>());
        Assert.Equal("xhigh", implementer["config"]!["model_reasoning_effort"]!.GetValue<string>());
        Assert.Equal("model-b", reviewer["model"]!.GetValue<string>());
        Assert.Equal("max", reviewer["config"]!["model_reasoning_effort"]!.GetValue<string>());

        var confirmations = harness.Database.GetConfirmations(outcome.RunId);
        foreach (var role in new[] { AgentRole.Implementer, AgentRole.Reviewer })
        {
            foreach (var setting in new[] { ProfileSettings.Model, ProfileSettings.Effort, ProfileSettings.Sandbox })
            {
                var confirmation = Assert.Single(confirmations, c => c.Role == role && c.Setting == setting);
                Assert.Equal(VerificationStatus.Verified, confirmation.Status);
            }
        }
    }

    [Fact]
    public async Task The_reviewer_works_in_its_own_conversation_and_does_not_inherit_the_implementers_reasoning()
    {
        await using var harness = PassingRun();

        await harness.RunAsync(Task);

        var turns = harness.Agents.CodexRequests("turn/start");
        Assert.Equal(2, turns.Count);
        Assert.NotEqual(turns[0]["params"]!["threadId"]!.GetValue<string>(), turns[1]["params"]!["threadId"]!.GetValue<string>());

        var prompt = Assert.Single(harness.ReviewerPrompts());
        Assert.Contains(Task, prompt, StringComparison.Ordinal);
        Assert.Contains("+fixed", prompt, StringComparison.Ordinal);
        Assert.Contains("-one", prompt, StringComparison.Ordinal);
        Assert.DoesNotContain("PRIVATE REASONING", prompt, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_implementer_receives_the_task_exactly_as_the_user_wrote_it()
    {
        await using var harness = PassingRun();
        const string Exact = "Make the app say fixed.\n  Keep the trailing newline; don't touch README.md!";

        await harness.RunAsync(Exact);

        Assert.Contains(Exact, Assert.Single(harness.ImplementerPrompts()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_implementer_works_in_an_isolated_copy_outside_the_project()
    {
        await using var harness = PassingRun();

        await harness.RunAsync(Task);

        var cwd = harness.Agents.CodexRequests("thread/start")[0]["params"]!["cwd"]!.GetValue<string>();
        Assert.StartsWith(harness.Paths.Workspaces, cwd, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(harness.ProjectPath, cwd, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task The_run_profile_is_stored_with_the_run_and_every_result_is_bound_to_its_hash()
    {
        await using var harness = PassingRun();

        var outcome = await harness.RunAsync(Task);

        var profile = harness.Database.GetProfile(outcome.RunId)!;
        var hash = profile.ComputeHash();
        Assert.Equal(hash, harness.Database.FindRun(outcome.RunId)!.ProfileHash);
        Assert.Equal(hash, outcome.Candidate!.ProfileHash);
        Assert.Equal(hash, harness.Database.GetLatestReview(outcome.RunId, outcome.Candidate.CandidateId)!.Binding.ProfileHash);
        Assert.Equal(hash, Assert.Single(harness.Database.GetGateResults(outcome.RunId)).Binding.ProfileHash);
        Assert.Equal(outcome.Candidate.Fingerprint, Assert.Single(harness.Database.GetGateResults(outcome.RunId)).Binding.CandidateFingerprint);
        Assert.Equal(["tests"], profile.RequiredGateIds);
    }
}
