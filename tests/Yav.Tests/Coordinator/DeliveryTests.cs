using Yav.Core.Ports;
using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.Coordinator;

public class DeliveryTests
{
    private const string Task = "Make the app say fixed.";
    private const string Poem = "line 1\nline 2\nline 3\nline 4\nline 5\nline 6\nline 7\nline 8\n";

    private static CoordinatorHarness Ready(string implemented = "fixed\n", params (string Path, string Content)[] files)
    {
        var harness = new CoordinatorHarness(files);
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", implemented), Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"))
            .ReviewerTurn(Step.Review("pass"));
        return harness;
    }

    private static Task<IsolatedWorkspace?> WorkspaceOf(CoordinatorHarness harness, RunOutcome outcome) =>
        harness.Workspaces.FindAsync(harness.Database.FindTask(outcome.TaskId!)!.WorkspaceId!, CancellationToken.None);

    [Fact]
    public async Task An_edit_the_user_made_meanwhile_is_never_overwritten()
    {
        await using var harness = Ready();
        var outcome = await harness.RunAsync(Task);
        harness.WriteProject("src/app.txt", "edited by the user\n");

        var delivery = await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);

        Assert.False(delivery.Succeeded);
        Assert.Equal("edited by the user\n", harness.ReadProject("src/app.txt"));
        var conflict = Assert.Single(delivery.Conflicts);
        Assert.Equal("src/app.txt", conflict.Path);
        Assert.Equal(ApplyConflictKind.ConcurrentEdit, conflict.Kind);
        Assert.True(delivery.MergePossible);
        Assert.Contains("Nothing was written", delivery.Message, StringComparison.Ordinal);
        Assert.Equal(RunState.ReadyToApply, harness.Database.FindRun(outcome.RunId)!.State);
    }

    [Fact]
    public async Task Edits_to_other_files_do_not_stand_in_the_way_and_are_left_as_they_are()
    {
        await using var harness = Ready();
        var outcome = await harness.RunAsync(Task);
        harness.WriteProject("README.md", "# App, edited by the user\n");
        harness.WriteProject("notes.txt", "new file of the user\n");

        var delivery = await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);

        Assert.True(delivery.Succeeded, delivery.Message);
        Assert.Equal("fixed\n", harness.ReadProject("src/app.txt"));
        Assert.Equal("# App, edited by the user\n", harness.ReadProject("README.md"));
        Assert.Equal("new file of the user\n", harness.ReadProject("notes.txt"));
    }

    [Fact]
    public async Task A_merged_candidate_is_reviewed_and_tested_again_before_it_can_be_applied()
    {
        await using var harness = Ready(Poem.Replace("line 2", "line 2, fixed"), ("src/app.txt", Poem), ("README.md", "# App\n"));
        var first = await harness.RunAsync(Task);
        harness.AssertEnded(RunOutcomeKind.ReadyToApply, first);
        harness.WriteProject("src/app.txt", Poem.Replace("line 7", "line 7, by the user"));

        var refused = await harness.Coordinator.ApplyAsync(first.RunId, harness.Observer, CancellationToken.None);
        var merged = await harness.Coordinator.MergeAndRecheckAsync(first.RunId, harness.Configuration, harness.Observer, harness.Approvals, CancellationToken.None);

        Assert.False(refused.Succeeded);
        harness.AssertEnded(RunOutcomeKind.ReadyToApply, merged);
        Assert.Equal(2, merged.Candidate!.Sequence);
        Assert.NotEqual(first.Candidate!.Fingerprint, merged.Candidate.Fingerprint);
        Assert.Equal(2, harness.ReviewerPrompts().Count);
        // The user's edit is now part of what the task starts from, so the second review is shown the task's change only.
        Assert.Contains("+line 2, fixed", harness.ReviewerPrompts()[1], StringComparison.Ordinal);
        Assert.DoesNotContain("+line 7, by the user", harness.ReviewerPrompts()[1], StringComparison.Ordinal);
        var isolated = await WorkspaceOf(harness, merged);
        Assert.Equal(
            Poem.Replace("line 2", "line 2, fixed").Replace("line 7", "line 7, by the user"),
            WorkspaceHarness.ReadIn(isolated!, "src/app.txt"));
        Assert.Equal(2, harness.Database.GetGateResults(first.RunId).Count(r => !r.IsBaselineRun));
        // The project itself was not written by the merge.
        Assert.Equal(Poem.Replace("line 7", "line 7, by the user"), harness.ReadProject("src/app.txt"));

        var delivery = await harness.Coordinator.ApplyAsync(first.RunId, harness.Observer, CancellationToken.None);

        Assert.True(delivery.Succeeded, delivery.Message);
        Assert.Equal(Poem.Replace("line 2", "line 2, fixed").Replace("line 7", "line 7, by the user"), harness.ReadProject("src/app.txt"));
    }

    [Fact]
    public async Task Edits_that_cannot_be_combined_are_reported_and_nothing_is_changed()
    {
        await using var harness = Ready();
        var outcome = await harness.RunAsync(Task);
        harness.WriteProject("src/app.txt", "the user wants this instead\n");

        var merged = await harness.Coordinator.MergeAndRecheckAsync(outcome.RunId, harness.Configuration, harness.Observer, harness.Approvals, CancellationToken.None);

        harness.AssertEnded(RunOutcomeKind.Blocked, merged);
        Assert.Contains("src/app.txt", merged.Reason, StringComparison.Ordinal);
        Assert.Equal("the user wants this instead\n", harness.ReadProject("src/app.txt"));
        var workspace = await WorkspaceOf(harness, outcome);
        Assert.Equal("fixed\n", WorkspaceHarness.ReadIn(workspace!, "src/app.txt"));
    }

    [Fact]
    public async Task A_candidate_whose_workspace_changed_after_the_checks_is_not_applied()
    {
        await using var harness = Ready();
        var outcome = await harness.RunAsync(Task);
        var workspace = await WorkspaceOf(harness, outcome);
        WorkspaceHarness.WriteIn(workspace!, "src/app.txt", "changed after the review\n");

        var delivery = await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);

        Assert.False(delivery.Succeeded);
        Assert.Contains(delivery.Decision!.Issues, i => i.Kind == AcceptanceIssueKind.CandidateChanged);
        Assert.Equal("one\n", harness.ReadProject("src/app.txt"));
        Assert.Equal(RunState.Blocked, harness.Database.FindRun(outcome.RunId)!.State);
    }

    [Fact]
    public async Task A_run_that_is_not_ready_cannot_be_applied()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Configuration = harness.Configuration with { Limits = RunLimits.Default with { MaxRepairCycles = 0 } };
        harness.Agents.ImplementerTurn(Step.Write("src/app.txt", "a bug\n")).ReviewerTurn(Step.Review("pass"));
        var blocked = await harness.RunAsync(Task);

        var delivery = await harness.Coordinator.ApplyAsync(blocked.RunId, harness.Observer, CancellationToken.None);

        Assert.False(delivery.Succeeded);
        Assert.Contains("Blocked", delivery.Message, StringComparison.Ordinal);
        Assert.Equal("one\n", harness.ReadProject("src/app.txt"));
    }

    [Fact]
    public async Task A_run_cannot_be_applied_twice()
    {
        await using var harness = Ready();
        var outcome = await harness.RunAsync(Task);
        await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);

        var again = await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);

        Assert.False(again.Succeeded);
        Assert.Contains("already applied", again.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Discarding_removes_the_isolated_changes_and_leaves_the_project_and_the_repository_alone()
    {
        await using var harness = Ready();
        var outcome = await harness.RunAsync(Task);
        var workspace = await WorkspaceOf(harness, outcome);
        var before = GitRepo.ReadTree(harness.ProjectPath);
        var references = await harness.Runner.RunAsync(
            new ProcessSpec("git", ["for-each-ref"], harness.ProjectPath), new CaptureOptions(Timeout: TimeSpan.FromSeconds(30)), CancellationToken.None);

        var delivery = await harness.Coordinator.DiscardAsync(outcome.RunId, harness.Observer, CancellationToken.None);

        Assert.True(delivery.Succeeded, delivery.Message);
        Assert.False(Directory.Exists(workspace!.RootPath));
        Assert.Equal(before, GitRepo.ReadTree(harness.ProjectPath));
        var after = await harness.Runner.RunAsync(
            new ProcessSpec("git", ["for-each-ref"], harness.ProjectPath), new CaptureOptions(Timeout: TimeSpan.FromSeconds(30)), CancellationToken.None);
        Assert.Equal(references.StandardOutput, after.StandardOutput);
        var worktrees = await harness.Runner.RunAsync(
            new ProcessSpec("git", ["worktree", "list", "--porcelain"], harness.ProjectPath), new CaptureOptions(Timeout: TimeSpan.FromSeconds(30)), CancellationToken.None);
        Assert.DoesNotContain(harness.Paths.Workspaces.Replace('\\', '/'), worktrees.StandardOutput.Replace('\\', '/'), StringComparison.OrdinalIgnoreCase);

        var stored = harness.Database.FindRun(outcome.RunId)!;
        Assert.Equal(RunDisposition.Discarded, stored.Disposition);
        Assert.NotEqual(RunState.ReadyToApply, stored.State);
    }

    [Fact]
    public async Task A_discarded_run_can_no_longer_be_applied()
    {
        await using var harness = Ready();
        var outcome = await harness.RunAsync(Task);
        await harness.Coordinator.DiscardAsync(outcome.RunId, harness.Observer, CancellationToken.None);

        var delivery = await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);

        Assert.False(delivery.Succeeded);
        Assert.Equal("one\n", harness.ReadProject("src/app.txt"));
    }

    [Fact]
    public async Task An_applied_run_is_not_discarded_but_undone()
    {
        await using var harness = Ready();
        var outcome = await harness.RunAsync(Task);
        await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);

        var delivery = await harness.Coordinator.DiscardAsync(outcome.RunId, harness.Observer, CancellationToken.None);

        Assert.False(delivery.Succeeded);
        Assert.Contains("/undo", delivery.Message, StringComparison.Ordinal);
        Assert.Equal("fixed\n", harness.ReadProject("src/app.txt"));
    }

    [Fact]
    public async Task Undo_puts_back_what_the_apply_replaced()
    {
        await using var harness = Ready();
        var outcome = await harness.RunAsync(Task);
        await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);

        var undo = await harness.Coordinator.UndoAsync(harness.ProjectPath, skipConflicts: false, harness.Observer, CancellationToken.None);

        Assert.True(undo.Succeeded, undo.Message);
        Assert.Equal(outcome.RunId, undo.RunId);
        Assert.Equal(["src/app.txt"], undo.Restored);
        Assert.Equal("one\n", harness.ReadProject("src/app.txt"));
        Assert.Equal(RunDisposition.Undone, harness.Database.FindRun(outcome.RunId)!.Disposition);
    }

    [Fact]
    public async Task Undo_refuses_to_destroy_what_the_user_edited_after_the_apply()
    {
        await using var harness = Ready();
        var outcome = await harness.RunAsync(Task);
        await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);
        harness.WriteProject("src/app.txt", "fixed, and improved by the user\n");

        var undo = await harness.Coordinator.UndoAsync(harness.ProjectPath, skipConflicts: false, harness.Observer, CancellationToken.None);

        Assert.False(undo.Succeeded);
        Assert.Equal("src/app.txt", Assert.Single(undo.Conflicts).Path);
        Assert.Equal("fixed, and improved by the user\n", harness.ReadProject("src/app.txt"));
        Assert.Equal(RunDisposition.Applied, harness.Database.FindRun(outcome.RunId)!.Disposition);
    }

    [Fact]
    public async Task There_is_nothing_to_undo_in_a_project_yav_never_wrote_to()
    {
        await using var harness = Ready();

        var undo = await harness.Coordinator.UndoAsync(harness.ProjectPath, skipConflicts: false, harness.Observer, CancellationToken.None);

        Assert.False(undo.Succeeded);
        Assert.Contains("nothing to undo", undo.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Undo_goes_back_one_apply_at_a_time_newest_first()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "first\n"))
            .ReviewerTurn(Step.Review("pass"))
            .ImplementerTurn(Step.Write("src/app.txt", "second\n"))
            .ReviewerTurn(Step.Review("pass"));
        var first = await harness.RunAsync("Say first.");
        await harness.Coordinator.ApplyAsync(first.RunId, harness.Observer, CancellationToken.None);
        var second = await harness.RunAsync("Say second.", taskId: first.TaskId);
        harness.AssertEnded(RunOutcomeKind.ReadyToApply, second);
        await harness.Coordinator.ApplyAsync(second.RunId, harness.Observer, CancellationToken.None);
        Assert.Equal("second\n", harness.ReadProject("src/app.txt"));

        var newest = await harness.Coordinator.UndoAsync(harness.ProjectPath, skipConflicts: false, harness.Observer, CancellationToken.None);
        var afterNewest = harness.ReadProject("src/app.txt");
        var older = await harness.Coordinator.UndoAsync(harness.ProjectPath, skipConflicts: false, harness.Observer, CancellationToken.None);

        Assert.Equal(second.RunId, newest.RunId);
        Assert.Equal("first\n", afterNewest);
        Assert.Equal(first.RunId, older.RunId);
        Assert.Equal("one\n", harness.ReadProject("src/app.txt"));
    }

    [Fact]
    public async Task The_same_apply_is_not_undone_twice()
    {
        await using var harness = Ready();
        var outcome = await harness.RunAsync(Task);
        await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);
        await harness.Coordinator.UndoAsync(harness.ProjectPath, skipConflicts: false, harness.Observer, CancellationToken.None);
        harness.WriteProject("src/app.txt", "the user went on\n");

        var again = await harness.Coordinator.UndoAsync(harness.ProjectPath, skipConflicts: false, harness.Observer, CancellationToken.None);

        Assert.False(again.Succeeded);
        Assert.Contains("already undone", again.Message, StringComparison.Ordinal);
        Assert.Equal("the user went on\n", harness.ReadProject("src/app.txt"));
    }
}

public class ProtectedChangeTests
{
    private const string Task = "Make the app say fixed.";

    [Fact]
    public async Task A_change_to_a_trusted_acceptance_test_needs_the_users_explicit_approval()
    {
        await using var harness = new CoordinatorHarness(("src/app.txt", "one\n"), ("acceptance/login.txt", "must reject unknown users\n"));
        harness.Trust(ProjectConfiguration.Empty with
        {
            Gates = [CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug")],
            ProtectedPaths = ["acceptance/**"],
        });
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Write("acceptance/login.txt", "may accept anyone\n"))
            .ReviewerTurn(Step.Review("pass"));

        var blocked = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, blocked);
        Assert.Equal(["acceptance/login.txt"], blocked.Candidate!.ProtectedPathsTouched);
        Assert.Contains(blocked.Decision!.Issues, i => i.Kind == AcceptanceIssueKind.ProtectedPathChanged);
        Assert.Single(harness.ImplementerPrompts());

        Assert.False(harness.Coordinator.ApproveProtectedPath(blocked.RunId, "src/app.txt"));
        Assert.True(harness.Coordinator.ApproveProtectedPath(blocked.RunId, "acceptance/login.txt"));
        var outcome = await harness.Coordinator.ResumeAsync(blocked.RunId, harness.Configuration, harness.Observer, harness.Approvals, CancellationToken.None);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Single(harness.ImplementerPrompts());
        Assert.Single(harness.ReviewerPrompts());
    }

    [Fact]
    public async Task A_gate_configuration_written_by_the_agent_changes_nothing_about_the_checks_that_run()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Configuration = harness.Configuration with { Limits = RunLimits.Default with { MaxRepairCycles = 0 } };
        var lenient = harness.Validation.Serialize(ProjectConfiguration.Empty with { Gates = [CoordinatorHarness.ToolGate("tests", "exit", "0")] });
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "a bug\n"), Step.Write("yav.project.json", lenient))
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        var result = harness.Database.GetGateResults(outcome.RunId).Single(r => !r.IsBaselineRun);
        Assert.Equal(GateStatus.Failed, result.Status);
        Assert.Contains("expect-no-text", result.CommandLine, StringComparison.Ordinal);
        Assert.Contains("yav.project.json", outcome.Candidate!.ProtectedPathsTouched);
    }

    [Fact]
    public async Task Changed_existing_tests_are_pointed_out_to_the_user_and_to_the_reviewer()
    {
        await using var harness = new CoordinatorHarness(("src/app.txt", "one\n"), ("tests/AppTests.cs", "Assert.Equal(1, 1);\n"));
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Write("tests/AppTests.cs", "// Assert.Equal(1, 1);\n"))
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        Assert.Equal(["tests/AppTests.cs"], outcome.Candidate!.ExistingTestsTouched);
        Assert.Contains(harness.Observer.Notes(Stages.Check), n => n.Level == NoteLevel.Warning && n.Message.Contains("tests/AppTests.cs", StringComparison.Ordinal));
        Assert.Contains("Check that no test was weakened", Assert.Single(harness.ReviewerPrompts()), StringComparison.Ordinal);
    }
}

public class ChecksAndSourceTests
{
    private const string Task = "Make the app say fixed.";

    [Fact]
    public async Task A_check_that_writes_files_cannot_change_the_candidate()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.ToolGate("tests", "expect-then-write", "src/app.txt", "overwritten by the test run\n"));
        harness.Agents.ImplementerTurn(Step.Write("src/app.txt", "fixed\n")).ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);
        Assert.Equal("fixed\n", harness.ReadProject("src/app.txt"));
    }

    [Fact]
    public async Task A_check_runs_in_a_copy_outside_the_project_and_outside_the_candidate()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.ToolGate("tests", "cwd"));
        harness.Agents.ImplementerTurn(Step.Write("src/app.txt", "fixed\n")).ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        var result = harness.Database.GetGateResults(outcome.RunId).Single();
        var workspace = await harness.Workspaces.FindAsync(harness.Database.FindTask(outcome.TaskId!)!.WorkspaceId!, CancellationToken.None);
        Assert.StartsWith(harness.Paths.Workspaces, result.WorkingDirectory, StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(Path.GetFullPath(workspace!.RootPath), Path.GetFullPath(result.WorkingDirectory));
        Assert.NotEqual(Path.GetFullPath(harness.ProjectPath), Path.GetFullPath(result.WorkingDirectory));
    }

    [Fact]
    public async Task Source_that_changes_while_a_check_runs_in_the_candidate_makes_its_result_worthless()
    {
        await using var harness = new CoordinatorHarness();
        harness.Trust(ProjectConfiguration.Empty with
        {
            Gates = [CoordinatorHarness.ToolGate("tests", "expect-then-write", "src/app.txt", "overwritten by the test run\n")],
            ValidationExecution = ProjectConfiguration.ExecutionCandidate,
        });
        harness.Agents.ImplementerTurn(Step.Write("src/app.txt", "fixed\n")).ReviewerTurn(Step.Review("pass")).ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        var result = harness.Database.GetGateResults(outcome.RunId).Last(r => !r.IsBaselineRun);
        Assert.Equal(GateStatus.Error, result.Status);
        Assert.Contains("Source files changed while the check ran", result.Limitation, StringComparison.Ordinal);
        Assert.Single(harness.ImplementerPrompts());

        var workspace = await harness.Workspaces.FindAsync(harness.Database.FindTask(outcome.TaskId!)!.WorkspaceId!, CancellationToken.None);
        Assert.Equal("fixed\n", WorkspaceHarness.ReadIn(workspace!, "src/app.txt"));
    }

    [Fact]
    public async Task A_check_that_cannot_be_performed_here_is_unverified_and_never_a_pass()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.ToolGate("integration", "exit", "0") with { Requires = ["env:YAV_TEST_VARIABLE_THAT_IS_NOT_SET"] });
        harness.Agents.ImplementerTurn(Step.Write("src/app.txt", "fixed\n")).ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Equal(GateStatus.Unverified, harness.Database.GetGateResults(outcome.RunId).Single().Status);
        Assert.Contains("YAV_TEST_VARIABLE_THAT_IS_NOT_SET", outcome.Reason, StringComparison.Ordinal);
        Assert.Single(harness.ImplementerPrompts());
    }

    [Fact]
    public async Task A_preparation_command_that_fails_stops_the_run_before_model_a_is_called()
    {
        await using var harness = new CoordinatorHarness();
        harness.Trust(ProjectConfiguration.Empty with
        {
            Gates = [CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug")],
            Prepare = [CoordinatorHarness.ToolGate("restore", "exit", "7")],
        });
        harness.Agents.ImplementerTurn(Step.Write("src/app.txt", "fixed\n"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains("Preparation did not succeed", outcome.Reason, StringComparison.Ordinal);
        Assert.Empty(harness.Agents.CodexRequests("thread/start"));
    }

    private static async Task<(CoordinatorHarness Harness, RunOutcome Outcome)> ReadyRunAsync()
    {
        var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents.ImplementerTurn(Step.Write("src/app.txt", "fixed\n")).ReviewerTurn(Step.Review("pass"));
        var outcome = await harness.RunAsync(Task);
        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        return (harness, outcome);
    }

    /// <summary>The part of the time between the first and the last interval that the intervals account for.</summary>
    private static double Accounted(IReadOnlyList<Yav.Core.Timing.TimingSpan> spans)
    {
        var whole = spans.Max(s => s.EndedAt!.Value) - spans.Min(s => s.StartedAt);
        return Yav.Core.Timing.TimingMath.WallClock(spans) / whole;
    }

    [Fact]
    public async Task What_a_run_does_is_measured_from_its_first_moment_to_its_last()
    {
        var (harness, outcome) = await ReadyRunAsync();
        await using var disposal = harness;

        var spans = harness.Database.GetSpans(outcome.RunId);

        Assert.Equal(
            ["Model A session", "Model B session", "agents", "evidence", "freeze", "implementation", "preflight", "project", "repository", "required checks", "review", "workspace"],
            spans.Select(s => s.Label).Order(StringComparer.Ordinal).ToArray());
        Assert.Contains(spans, s => s is { Kind: Yav.Core.Timing.SpanKind.LocalPreparation, Label: "preflight" });
        Assert.Contains(spans, s => s is { Kind: Yav.Core.Timing.SpanKind.Acceptance, Label: "evidence" });
        Assert.Contains(spans, s => s is { Kind: Yav.Core.Timing.SpanKind.Acceptance, Label: "repository" });
        Assert.True(Accounted(spans) >= 0.85, $"{Accounted(spans):P0} of the time of the run is accounted for");
    }

    [Fact]
    public async Task The_project_and_the_agents_are_looked_at_side_by_side_before_a_run()
    {
        var (harness, outcome) = await ReadyRunAsync();
        await using var disposal = harness;

        var spans = harness.Database.GetSpans(outcome.RunId);

        var project = spans.Single(s => s.Label == "project");
        var agents = spans.Single(s => s.Label == "agents");
        var whole = spans.Single(s => s.Label == "preflight");
        Assert.Equal(Yav.Core.Timing.SpanKind.LocalPreparation, project.Kind);
        Assert.Equal(Yav.Core.Timing.SpanKind.AgentInitialization, agents.Kind);
        Assert.NotNull(project.ParallelGroup);
        Assert.Equal(project.ParallelGroup, agents.ParallelGroup);
        Assert.True(project.StartedAt < agents.EndedAt && agents.StartedAt < project.EndedAt, $"one after the other: project {project.StartedAt:O} for {project.Duration}, agents {agents.StartedAt:O} for {agents.Duration}");
        Assert.True(whole.Duration < project.Duration + agents.Duration, $"{whole.Duration} for {project.Duration} and {agents.Duration}");
    }

    [Theory]
    [InlineData(CoordinatorHarness.CodexId, "model-a")]
    [InlineData(CoordinatorHarness.ClaudeId, "opus")]
    public async Task The_time_an_agent_spends_in_its_tools_is_measured_apart_from_the_time_of_the_provider(string adapter, string model)
    {
        await using var harness = new CoordinatorHarness();
        harness.Configuration = harness.Configuration with { ModelA = new Yav.Core.Profiles.RoleSelection(adapter, model) };
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents
            .ImplementerTurn(
                Step.Sleep(300),
                Step.Command("dotnet test --filter Secret=hunter2", "1 failed", exitCode: 1, milliseconds: 1_200),
                Step.Write("src/app.txt", "fixed\n"),
                Step.Command("dotnet test", "all passed", milliseconds: 900),
                Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        var spans = harness.Database.GetSpans(outcome.RunId);
        var implementation = spans.Single(s => s.Kind == Yav.Core.Timing.SpanKind.Implementation);
        var tools = spans.Where(s => s.Kind == Yav.Core.Timing.SpanKind.ToolActivity).OrderBy(s => s.StartedAt).ToList();
        Assert.Equal(2, tools.Count);
        // The commands run for 1200 and 900 ms. What is measured begins when the report of the start is taken up,
        // which on a machine that is busy can be tenths of a second after it was written.
        Assert.True(tools[0].Duration >= TimeSpan.FromMilliseconds(400), $"the first command: {tools[0].Duration}");
        Assert.True(tools[1].Duration >= TimeSpan.FromMilliseconds(300), $"the second command: {tools[1].Duration}");
        Assert.All(tools, tool => Assert.True(
            tool.StartedAt >= implementation.StartedAt && tool.EndedAt <= implementation.EndedAt, "a command lies outside the turn it belongs to"));

        // What is kept with a measurement says whose tool it was, and not what the command was.
        Assert.All(tools, tool => Assert.Equal("Model A: command", tool.Label));

        var started = harness.Database.FindRun(outcome.RunId)!.CreatedAt;
        var report = Yav.Core.Timing.TimingMath.Report(spans, started, spans.Max(s => s.EndedAt), null, null);
        Assert.True(report.InTools >= TimeSpan.FromMilliseconds(700), $"in tools: {report.InTools}");
        Assert.True(report.InTools < implementation.Duration, $"in tools {report.InTools}, the turn {implementation.Duration}");
    }

    [Fact]
    public async Task A_tool_that_had_not_ended_when_its_turn_ended_is_measured_up_to_there()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents.ImplementerTurn(Step.Write("src/app.txt", "half\n"), Step.Command("dotnet test", milliseconds: 60_000));
        using var stop = new CancellationTokenSource();

        var running = harness.RunAsync(Task, cancellationToken: stop.Token);
        var deadline = DateTime.UtcNow.AddSeconds(60);
        while (!harness.Observer.Events.Any(e => e is AgentActivity { Event: Yav.Core.Agents.CommandStarted }))
        {
            Assert.True(DateTime.UtcNow < deadline, "the command did not start");
            await System.Threading.Tasks.Task.Delay(25);
        }

        await System.Threading.Tasks.Task.Delay(300);
        await stop.CancelAsync();
        var outcome = await running;

        harness.AssertEnded(RunOutcomeKind.Interrupted, outcome);
        var tool = Assert.Single(harness.Database.GetSpans(outcome.RunId), s => s.Kind == Yav.Core.Timing.SpanKind.ToolActivity);
        Assert.InRange(tool.Duration!.Value, TimeSpan.FromMilliseconds(250), TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task What_an_apply_does_is_measured_from_its_first_moment_to_its_last()
    {
        var (harness, outcome) = await ReadyRunAsync();
        await using var disposal = harness;

        var applied = await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);

        Assert.True(applied.Succeeded, applied.Message);
        var spans = harness.Database.GetSpans(outcome.RunId).Where(s => s.Kind == Yav.Core.Timing.SpanKind.Apply).OrderBy(s => s.StartedAt).ToList();
        Assert.Equal(["evidence", "apply", "new baseline"], spans.Select(s => s.Label).ToArray());
        Assert.True(Accounted(spans) >= 0.85, $"{Accounted(spans):P0} of the time of the apply is accounted for");
    }

    [Fact]
    public async Task The_time_a_candidate_waited_for_the_user_is_kept_apart_from_the_time_in_which_something_was_done()
    {
        var (harness, outcome) = await ReadyRunAsync();
        await using var disposal = harness;
        var before = harness.Database.GetSpans(outcome.RunId);
        await System.Threading.Tasks.Task.Delay(400);

        await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);

        var spans = harness.Database.GetSpans(outcome.RunId);
        var waited = Assert.Single(spans, s => s.Kind == Yav.Core.Timing.SpanKind.ApprovalWaiting);
        Assert.Equal("until /apply", waited.Label);
        // A delay of 400 ms can end a fraction of a millisecond early by the clock the spans are measured with.
        Assert.True(waited.Duration >= TimeSpan.FromMilliseconds(350), $"waited {waited.Duration}");
        Assert.True(waited.StartedAt >= before.Max(s => s.EndedAt!.Value), "the waiting began before the run had ended");
        Assert.True(waited.EndedAt <= spans.Where(s => s.Kind == Yav.Core.Timing.SpanKind.Apply).Min(s => s.StartedAt), "the waiting went on after the apply had begun");
    }

    [Fact]
    public async Task The_time_until_a_run_was_continued_is_kept_apart_as_well()
    {
        var (harness, outcome) = await ReadyRunAsync();
        await using var disposal = harness;
        harness.Agents.ReviewerTurn(Step.Review("pass"));
        await System.Threading.Tasks.Task.Delay(400);

        var again = await harness.Coordinator.RecheckAsync(
            outcome.RunId, Yav.Coordinator.RecheckScope.Review, harness.Configuration, harness.Observer, harness.Approvals, CancellationToken.None);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, again);
        var waited = Assert.Single(harness.Database.GetSpans(outcome.RunId), s => s.Kind == Yav.Core.Timing.SpanKind.ApprovalWaiting);
        Assert.Equal("until it was continued", waited.Label);
        // A delay of 400 ms can end a fraction of a millisecond early by the clock the spans are measured with.
        Assert.True(waited.Duration >= TimeSpan.FromMilliseconds(350), $"waited {waited.Duration}");
    }

    [Fact]
    public async Task Review_and_checks_run_side_by_side_and_are_not_added_up_as_elapsed_time()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.ToolGate("tests", "sleep", "1.5"));
        harness.Agents.ImplementerTurn(Step.Write("src/app.txt", "fixed\n")).ReviewerTurn(Step.Sleep(1500), Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        var spans = harness.Database.GetSpans(outcome.RunId);
        var review = spans.Single(s => s.Kind == Yav.Core.Timing.SpanKind.Review);
        var tests = spans.Single(s => s.Kind == Yav.Core.Timing.SpanKind.Tests);
        Assert.NotNull(review.ParallelGroup);
        Assert.Equal(review.ParallelGroup, tests.ParallelGroup);
        Assert.True(review.Duration >= TimeSpan.FromSeconds(1.4), $"review {review.Duration}");
        Assert.True(tests.Duration >= TimeSpan.FromSeconds(1.4), $"tests {tests.Duration}");

        // Both stages last at least a second and a half. Side by side, the time that passed is at least a second
        // less than the work that was done, however busy the machine is.
        var wall = Yav.Core.Timing.TimingMath.WallClock([review, tests]);
        var work = Yav.Core.Timing.TimingMath.Work([review, tests]);
        Assert.True(work - wall >= TimeSpan.FromSeconds(1), $"wall {wall}, work {work}: the two stages did not overlap");
    }
}
