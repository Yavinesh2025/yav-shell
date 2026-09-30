using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.Coordinator;

public class ProfileEnforcementTests
{
    private const string Task = "Make the app say fixed.";

    private static CoordinatorHarness Harness()
    {
        var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.TextGate("tests", "src/app.txt", "fixed"));
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"));
        return harness;
    }

    [Fact]
    public async Task A_lower_effort_than_requested_stops_the_run_before_anything_is_sent()
    {
        await using var harness = Harness();
        harness.Agents.Codex(c => c["effectiveEffort:implementer"] = "high");

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Equal(RunState.Blocked, harness.Database.FindRun(outcome.RunId)!.State);
        Assert.Contains("'xhigh' was requested but the provider reports 'high'", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("No request was sent", outcome.Reason, StringComparison.Ordinal);
        Assert.Empty(harness.Agents.CodexRequests("turn/start"));

        var effort = harness.Database.GetConfirmations(outcome.RunId).Single(c => c.Role == AgentRole.Implementer && c.Setting == ProfileSettings.Effort);
        Assert.Equal(VerificationStatus.Mismatch, effort.Status);
        Assert.Equal("high", effort.Effective);
    }

    [Fact]
    public async Task A_reviewer_that_would_run_at_lower_effort_is_not_started_and_the_candidate_is_kept()
    {
        await using var harness = Harness();
        harness.Agents.Codex(c => c["effectiveEffort:reviewer"] = "medium");

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains("Model B effort", outcome.Reason, StringComparison.Ordinal);
        Assert.Single(harness.Agents.CodexRequests("turn/start"));
        Assert.NotNull(outcome.Candidate);
        Assert.Empty(harness.Database.GetReviews(outcome.RunId));
        Assert.Equal("one\n", harness.ReadProject("src/app.txt"));
    }

    [Fact]
    public async Task A_different_model_than_the_chosen_one_is_never_accepted()
    {
        await using var harness = Harness();
        harness.Agents.Codex(c => c["effectiveModel:implementer"] = "model-a-mini");

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains("model-a-mini", outcome.Reason, StringComparison.Ordinal);
        Assert.Empty(harness.Agents.CodexRequests("turn/start"));
    }

    [Fact]
    public async Task A_reviewer_that_is_not_confirmed_read_only_is_never_started()
    {
        await using var harness = Harness();
        harness.Agents.Codex(c => c["effectiveSandbox:reviewer"] = "workspaceWrite");

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains("read-only", outcome.Reason, StringComparison.Ordinal);
        Assert.Single(harness.Agents.CodexRequests("turn/start"));
        var sandbox = harness.Database.GetConfirmations(outcome.RunId).Single(c => c.Role == AgentRole.Reviewer && c.Setting == ProfileSettings.Sandbox);
        Assert.Equal(VerificationStatus.Mismatch, sandbox.Status);
    }

    [Fact]
    public async Task An_implementer_that_was_given_a_read_only_sandbox_is_explained_instead_of_run()
    {
        await using var harness = Harness();
        harness.Agents.Codex(c => c["effectiveSandbox:implementer"] = "readOnly");

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains("cannot write", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("/doctor", outcome.Reason, StringComparison.Ordinal);
        Assert.Empty(harness.Agents.CodexRequests("turn/start"));
    }

    [Fact]
    public async Task A_sandbox_that_reaches_the_network_is_said_once_for_each_model_and_the_run_goes_on()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.TextGate("tests", "src/app.txt", "fixed"));
        harness.Agents.Codex(c => c["effectiveNetworkAccess"] = true);
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "almost\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("changes_required", file: "src/app.txt", title: "It does not say fixed"))
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Repaired."))
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        var said = harness.Observer.Events.OfType<StageNote>().Where(n => n.Message.Contains("access to the network", StringComparison.Ordinal)).ToList();
        Assert.Equal(["Model A", "Model B"], said.Select(n => n.Message[..7]).Order());
        Assert.All(said, note => Assert.Equal(NoteLevel.Warning, note.Level));
        var network = harness.Database.GetConfirmations(outcome.RunId).Where(c => c.Setting == ProfileSettings.NetworkAccess).ToList();
        Assert.All(network, row => Assert.Equal("on", row.Effective));
        Assert.NotEmpty(network);
    }

    [Fact]
    public async Task An_implementer_that_may_write_outside_the_workspace_is_not_sent_anything()
    {
        await using var harness = Harness();
        harness.Agents.Codex(c => c["effectiveWritableRoots"] = new System.Text.Json.Nodes.JsonArray { @"C:\cache" });

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains(@"may also write to C:\cache", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("No request was sent", outcome.Reason, StringComparison.Ordinal);
        Assert.Empty(harness.Agents.CodexRequests("turn/start"));
        Assert.Equal("one\n", harness.ReadProject("src/app.txt"));
    }

    [Fact]
    public async Task With_quality_lock_off_folders_outside_the_workspace_that_can_be_written_are_said_and_the_run_goes_on()
    {
        // What is not enforced is still not learned from a table only.
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.TextGate("tests", "src/app.txt", "fixed"));
        harness.Configuration = harness.Configuration with { Policy = new QualityPolicy(QualityLock: false) };
        harness.Agents.Codex(c => c["effectiveWritableRoots"] = new System.Text.Json.Nodes.JsonArray { @"C:\cache" });
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "almost\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("changes_required", file: "src/app.txt", title: "It does not say fixed"))
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Repaired."))
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        var said = harness.Observer.Events.OfType<StageNote>().Where(n => n.Message.Contains(@"may also write to C:\cache", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(said);
        Assert.Equal(said.Count, said.Select(n => n.Message[..7]).Distinct().Count());
        Assert.All(said, note => Assert.Equal(NoteLevel.Warning, note.Level));
    }

    [Fact]
    public async Task A_model_the_provider_swaps_in_the_middle_of_a_turn_stops_the_turn()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.TextGate("tests", "src/app.txt", "fixed"));
        harness.Agents.ImplementerTurn(Step.Reroute("model-a", "model-a-mini"), Step.Sleep(20_000), Step.Write("src/app.txt", "fixed\n"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains("rerouted the request from 'model-a' to 'model-a-mini'", outcome.Reason, StringComparison.Ordinal);
        Assert.Single(harness.Agents.CodexRequests("turn/interrupt"));
        Assert.Null(outcome.Candidate);
    }

    [Fact]
    public async Task The_settings_in_effect_are_published_for_the_status_display()
    {
        await using var harness = Harness();

        await harness.RunAsync(Task);

        var confirmed = harness.Observer.Of<SettingConfirmed>().Select(e => e.Confirmation).ToList();
        Assert.Contains(confirmed, c => c is { Role: AgentRole.Implementer, Setting: ProfileSettings.Effort, Effective: "xhigh", Status: VerificationStatus.Verified });
        Assert.Contains(confirmed, c => c is { Role: AgentRole.Reviewer, Setting: ProfileSettings.Sandbox, Effective: "read-only", Status: VerificationStatus.Verified });
        Assert.Contains(confirmed, c => c is { Role: AgentRole.Reviewer, Setting: ProfileSettings.Model, Effective: "model-b" });
    }
}

public class ReviewerBoundaryTests
{
    private const string Task = "Make the app say fixed.";

    private static CoordinatorHarness Harness()
    {
        var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.TextGate("tests", "src/app.txt", "fixed"));
        return harness;
    }

    [Fact]
    public async Task What_a_reviewer_changed_is_reverted_and_never_becomes_part_of_the_candidate()
    {
        await using var harness = Harness();
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"))
            .ReviewerTurn(Step.Write("src/app.txt", "fixed, improved by the reviewer\n"), Step.Write("src/extra.txt", "x\n"), Step.Review("pass"))
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Equal(1, outcome.Candidate!.Sequence);
        Assert.Equal("src/app.txt", Assert.Single(outcome.Candidate.Changes.Files).Path);
        Assert.Equal(2, harness.ReviewerPrompts().Count);
        Assert.Contains(harness.Observer.Notes(), n => n.Level == NoteLevel.Warning && n.Message.Contains("src/extra.txt", StringComparison.Ordinal));

        var delivery = await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);
        Assert.True(delivery.Succeeded, delivery.Message);
        Assert.Equal("fixed\n", harness.ReadProject("src/app.txt"));
        Assert.False(File.Exists(harness.InProject("src/extra.txt")));
    }

    [Fact]
    public async Task A_review_during_which_the_source_changed_is_not_a_valid_review()
    {
        await using var harness = Harness();
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"))
            .ReviewerTurn(Step.Write("src/app.txt", "fixed, improved by the reviewer\n"), Step.Review("pass"))
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        var reviews = harness.Database.GetReviews(outcome.RunId);
        Assert.Equal(2, reviews.Count);
        Assert.False(reviews[0].SourceUnchangedDuringReview);
        Assert.False(reviews[0].IsCleanPass);
        Assert.True(reviews[1].IsCleanPass);
    }

    [Fact]
    public async Task A_reviewer_that_changes_the_source_again_blocks_the_run()
    {
        await using var harness = Harness();
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"))
            .ReviewerTurn(Step.Write("src/app.txt", "tampered 1\n"), Step.Review("pass"))
            .ReviewerTurn(Step.Write("src/app.txt", "tampered 2\n"), Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains(outcome.Decision!.Issues, i => i.Kind == AcceptanceIssueKind.ReviewSourceChanged);
        Assert.Single(harness.ImplementerPrompts());

        // The isolated workspace holds the implementer's candidate again, not the reviewer's text.
        var workspace = await harness.Workspaces.FindAsync(harness.Database.FindTask(outcome.TaskId!)!.WorkspaceId!, CancellationToken.None);
        Assert.Equal(outcome.Candidate!.Fingerprint, await harness.Workspaces.FingerprintAsync(workspace!, CancellationToken.None));
    }

    [Fact]
    public async Task A_reviewer_that_asks_for_more_access_is_refused_without_asking_the_user()
    {
        await using var harness = Harness();
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"))
            .ReviewerTurn(
                Step.Approval("git push", onAccept: [Step.Write("src/app.txt", "pushed\n")], onDecline: []),
                Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Empty(harness.Approvals.Requests);
        Assert.DoesNotContain(RunState.AwaitingApproval, harness.Observer.States());
    }
}
