using Yav.Coordinator;
using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.Coordinator;

public class CrashRecoveryTests
{
    private const string Task = "Make the app say fixed.";
    private const int DeadProcess = 999_999;

    /// <summary>
    /// Leaves behind what a crash of YAV in the middle of the implementation would: a run recorded as
    /// Implementing by a process that no longer exists, and an isolated workspace in whatever state it was.
    /// </summary>
    private static async Task<(string RunId, IsolatedWorkspace Workspace)> CrashedRunAsync(
        CoordinatorHarness harness,
        string? written,
        string? session = "thr-crashed",
        bool frozen = false)
    {
        var preflight = await harness.Coordinator.PreflightAsync(new RunRequest(harness.ProjectPath, Task), harness.Configuration, CancellationToken.None);
        Assert.True(preflight.CanRun, string.Join("; ", preflight.Blocking.Select(p => p.Message)));
        var profile = preflight.Resolution.Profile!;

        var workspace = await harness.Workspaces.PrepareAsync(
            new WorkspaceRequest("t-crashed", harness.ProjectPath, WorkspaceMode.GitWorktree, preflight.Configuration.Effective, null, true),
            preflight.Inspection!, null, CancellationToken.None);
        if (written is not null)
        {
            WorkspaceHarness.WriteIn(workspace, "src/app.txt", written);
        }

        var now = harness.Clock.GetUtcNow();
        var database = harness.Database;
        database.SaveTask(new TaskContext("t-crashed", harness.ProjectPath, now, workspace.WorkspaceId, session, session is null ? null : CoordinatorHarness.CodexId, null, null));
        database.SaveRequirement("t-crashed", new Requirement(1, Task, now, []));
        var runId = Ids.NewRunId(harness.Clock);
        database.CreateRun(
            new RunRecord(
                runId, "t-crashed", 1, RunKind.CodingTask, harness.ProjectPath, Task, 1, profile.ComputeHash(), RunState.Preparing,
                RunDisposition.Pending, null, 0, null, now, now, DeadProcess, "0.1.0-test"),
            profile);
        database.Transition(runId, RunState.Preparing, RunState.Implementing, null);
        if (session is not null)
        {
            database.SaveSession(new SessionRecord("t-crashed", AgentRole.Implementer, CoordinatorHarness.CodexId, session, "model-a", null, null, now));
        }

        // What the provider reported is recorded when a conversation is opened, so it was stored before the crash.
        foreach (var (setting, value) in new[] { (ProfileSettings.Model, "model-a"), (ProfileSettings.Effort, "xhigh"), (ProfileSettings.Sandbox, "workspace-write") })
        {
            database.SaveConfirmation(runId, new ProfileConfirmation(AgentRole.Implementer, setting, value, value, VerificationStatus.Verified, "thread/start response", now));
        }

        if (frozen)
        {
            var snapshot = await harness.Workspaces.FreezeAsync(workspace, preflight.Configuration.Effective, CancellationToken.None);
            var candidate = new Candidate(
                "c-crashed", runId, 1, snapshot.Fingerprint, workspace.BaselineFingerprint, 1, profile.ComputeHash(), snapshot.Changes,
                snapshot.ProtectedPathsTouched, snapshot.ExistingTestsTouched, now);
            database.SaveCandidate(candidate, snapshot.ManifestId);
            database.UpdateRun(runId, r => r with { CurrentCandidateId = candidate.CandidateId });
            database.Transition(runId, RunState.Implementing, RunState.Checking, null);
        }

        return (runId, workspace);
    }

    private static CoordinatorHarness Harness(Action<System.Text.Json.Nodes.JsonObject>? codex = null)
    {
        var harness = new CoordinatorHarness(pid => pid != DeadProcess);
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        if (codex is not null)
        {
            harness.Agents.Codex(codex);
        }

        return harness;
    }

    [Fact]
    public async Task A_run_whose_process_is_gone_is_marked_and_nothing_is_sent_to_an_agent()
    {
        await using var harness = Harness(c =>
        {
            c["knownThreads"] = new System.Text.Json.Nodes.JsonArray { "thr-crashed" };
            c["threadStates"] = new System.Text.Json.Nodes.JsonObject { ["thr-crashed"] = "completed" };
        });
        var (runId, _) = await CrashedRunAsync(harness, written: "fixed\n");

        var report = await harness.Coordinator.ReconcileAsync(CancellationToken.None);

        var finding = Assert.Single(report.Runs);
        Assert.Equal(runId, finding.RunId);
        Assert.Equal(RunState.Implementing, finding.StateBefore);
        Assert.Equal(ReconciliationAdvice.CheckWorkspace, finding.Advice);
        Assert.Contains(finding.Observations, o => o.Contains("never frozen", StringComparison.Ordinal));
        Assert.Contains(finding.Observations, o => o.Contains("last turn completed", StringComparison.Ordinal));
        Assert.Equal(RunState.NeedsReconciliation, harness.Database.FindRun(runId)!.State);
        Assert.Empty(harness.Agents.CodexRequests("turn/start"));
        Assert.Empty(harness.Agents.CodexRequests("thread/start"));
        Assert.Empty(harness.Agents.CodexRequests("thread/resume"));
    }

    [Fact]
    public async Task A_run_of_a_process_that_is_still_alive_is_left_alone()
    {
        await using var harness = new CoordinatorHarness(_ => true);
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        var (runId, _) = await CrashedRunAsync(harness, written: "fixed\n");

        var report = await harness.Coordinator.ReconcileAsync(CancellationToken.None);

        Assert.Empty(report.Runs);
        Assert.Equal(RunState.Implementing, harness.Database.FindRun(runId)!.State);
    }

    [Fact]
    public async Task Changes_of_a_turn_that_completed_are_checked_without_sending_the_request_again()
    {
        await using var harness = Harness(c =>
        {
            c["knownThreads"] = new System.Text.Json.Nodes.JsonArray { "thr-crashed" };
            c["threadStates"] = new System.Text.Json.Nodes.JsonObject { ["thr-crashed"] = "completed" };
        });
        harness.Agents.ReviewerTurn(Step.Review("pass"));
        var (runId, _) = await CrashedRunAsync(harness, written: "fixed\n");
        await harness.Coordinator.ReconcileAsync(CancellationToken.None);

        var outcome = await harness.Coordinator.ResumeAsync(runId, harness.Configuration, harness.Observer, harness.Approvals, CancellationToken.None);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Empty(harness.ImplementerPrompts());
        Assert.Single(harness.ReviewerPrompts());
        Assert.Equal("src/app.txt", Assert.Single(outcome.Candidate!.Changes.Files).Path);
    }

    [Fact]
    public async Task A_turn_that_did_not_finish_is_continued_in_its_own_conversation()
    {
        await using var harness = Harness(c =>
        {
            c["knownThreads"] = new System.Text.Json.Nodes.JsonArray { "thr-crashed" };
            c["threadStates"] = new System.Text.Json.Nodes.JsonObject { ["thr-crashed"] = "interrupted" };
        });
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Finished."))
            .ReviewerTurn(Step.Review("pass"));
        var (runId, _) = await CrashedRunAsync(harness, written: "half\n");
        var report = await harness.Coordinator.ReconcileAsync(CancellationToken.None);

        var outcome = await harness.Coordinator.ResumeAsync(runId, harness.Configuration, harness.Observer, harness.Approvals, CancellationToken.None);

        Assert.Equal(ReconciliationAdvice.ContinueImplementation, Assert.Single(report.Runs).Advice);
        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        var resumed = Assert.Single(harness.Agents.CodexRequests("thread/resume"));
        Assert.Equal("thr-crashed", resumed["params"]!["threadId"]!.GetValue<string>());
        var continuation = harness.Agents.CodexRequests("turn/start")[0]["params"]!["input"]![0]!["text"]!.GetValue<string>();
        Assert.Contains("did not finish", continuation, StringComparison.Ordinal);
        Assert.Contains("## Required checks", continuation, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_turn_the_provider_still_reports_as_running_is_not_started_a_second_time()
    {
        await using var harness = Harness(c =>
        {
            c["knownThreads"] = new System.Text.Json.Nodes.JsonArray { "thr-crashed" };
            c["threadStates"] = new System.Text.Json.Nodes.JsonObject { ["thr-crashed"] = "inProgress" };

            // Codex runs the thread: a turn it records as in progress in a thread nothing runs is one that was cut off.
            c["threadStatus"] = new System.Text.Json.Nodes.JsonObject { ["thr-crashed"] = "active" };
        });
        var (runId, _) = await CrashedRunAsync(harness, written: "half\n");
        var report = await harness.Coordinator.ReconcileAsync(CancellationToken.None);

        var outcome = await harness.Coordinator.ResumeAsync(runId, harness.Configuration, harness.Observer, harness.Approvals, CancellationToken.None);

        Assert.Equal(ReconciliationAdvice.WaitForProvider, Assert.Single(report.Runs).Advice);
        Assert.Equal(RunOutcomeKind.NeedsReconciliation, outcome.Kind);
        Assert.Equal(RunState.NeedsReconciliation, harness.Database.FindRun(runId)!.State);
        Assert.Empty(harness.Agents.CodexRequests("turn/start"));
    }

    [Fact]
    public async Task A_candidate_that_was_frozen_before_the_crash_is_checked_again_as_it_is()
    {
        await using var harness = Harness();
        harness.Agents.ReviewerTurn(Step.Review("pass"));
        var (runId, _) = await CrashedRunAsync(harness, written: "fixed\n", session: null, frozen: true);
        var report = await harness.Coordinator.ReconcileAsync(CancellationToken.None);

        var outcome = await harness.Coordinator.ResumeAsync(runId, harness.Configuration, harness.Observer, harness.Approvals, CancellationToken.None);

        Assert.Equal(ReconciliationAdvice.Recheck, Assert.Single(report.Runs).Advice);
        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Equal("c-crashed", outcome.Candidate!.CandidateId);
        Assert.Empty(harness.ImplementerPrompts());
    }

    [Fact]
    public async Task A_run_whose_workspace_is_gone_can_only_be_discarded()
    {
        await using var harness = Harness();
        var (runId, workspace) = await CrashedRunAsync(harness, written: "fixed\n");
        await harness.Workspaces.DiscardAsync(workspace, CancellationToken.None);

        var report = await harness.Coordinator.ReconcileAsync(CancellationToken.None);
        var outcome = await harness.Coordinator.ResumeAsync(runId, harness.Configuration, harness.Observer, harness.Approvals, CancellationToken.None);
        var discarded = await harness.Coordinator.DiscardAsync(runId, harness.Observer, CancellationToken.None);

        Assert.Equal(ReconciliationAdvice.DiscardOnly, Assert.Single(report.Runs).Advice);
        Assert.Equal(RunOutcomeKind.Blocked, outcome.Kind);
        Assert.True(discarded.Succeeded, discarded.Message);
        Assert.Equal(RunDisposition.Discarded, harness.Database.FindRun(runId)!.Disposition);
        Assert.Equal("one\n", harness.ReadProject("src/app.txt"));
    }

    [Fact]
    public async Task An_apply_that_a_crash_interrupted_is_reported_by_the_reconciliation()
    {
        await using var harness = Harness();
        harness.Database.Save(new ApplyJournal(
            "j-crashed", "run-gone", "c-1", Builders.Fingerprint, harness.ProjectPath, JournalState.Prepared, [], harness.Clock.GetUtcNow(), harness.Clock.GetUtcNow()));

        var report = await harness.Coordinator.ReconcileAsync(CancellationToken.None);

        var journal = Assert.Single(report.Journals);
        Assert.Equal("j-crashed", journal.JournalId);
        Assert.DoesNotContain(journal.State, new[] { JournalState.Prepared, JournalState.InProgress });
    }
}

public class MechanicalEditTests
{
    private static CoordinatorHarness Harness()
    {
        var harness = new CoordinatorHarness(("src/app.txt", "timeout = 30\nretries = 3\n"), ("src/other.txt", "timeout = 30\n"));
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents.ReviewerTurn(Step.Review("pass"));
        return harness;
    }

    private static Task<RunOutcome> EditAsync(CoordinatorHarness harness, MechanicalEditRequest edit) =>
        harness.Coordinator.RunAsync(
            new RunRequest(harness.ProjectPath, $"Replace \"{edit.ExpectedText}\" with \"{edit.ReplacementText}\" in {edit.Path}") { MechanicalEdit = edit },
            harness.Configuration, harness.Observer, harness.Approvals, CancellationToken.None);

    [Fact]
    public async Task An_explicit_literal_replacement_is_made_locally_and_still_reviewed_and_tested()
    {
        await using var harness = Harness();

        var outcome = await EditAsync(harness, new MechanicalEditRequest("src/app.txt", "timeout = 30", "timeout = 60", ExpectedMatches: 1, AllOccurrences: false));

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Empty(harness.ImplementerPrompts());
        Assert.Single(harness.ReviewerPrompts());
        Assert.Single(harness.Agents.CodexRequests("thread/start"));
        Assert.Equal(GateStatus.Passed, harness.Database.GetGateResults(outcome.RunId).Single().Status);
        Assert.Equal(RunKind.MechanicalEdit, harness.Database.FindRun(outcome.RunId)!.Kind);
        Assert.Equal("src/app.txt", Assert.Single(outcome.Candidate!.Changes.Files).Path);

        await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);
        Assert.Equal("timeout = 60\nretries = 3\n", harness.ReadProject("src/app.txt"));
        Assert.Equal("timeout = 30\n", harness.ReadProject("src/other.txt"));
    }

    [Fact]
    public async Task The_reviewer_is_not_shown_a_summary_nobody_wrote()
    {
        await using var harness = Harness();

        await EditAsync(harness, new MechanicalEditRequest("src/app.txt", "timeout = 30", "timeout = 60", 1, false));

        Assert.DoesNotContain("Implementer's summary", Assert.Single(harness.ReviewerPrompts()), StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_replacement_that_does_not_match_what_the_user_expected_is_not_performed()
    {
        await using var harness = Harness();

        var outcome = await EditAsync(harness, new MechanicalEditRequest("src/app.txt", "timeout", "deadline", ExpectedMatches: 3, AllOccurrences: true));

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains("The edit was not performed", outcome.Reason, StringComparison.Ordinal);
        Assert.Empty(harness.Agents.CodexRequests("thread/start"));
        Assert.Null(outcome.Candidate);
    }

    [Fact]
    public async Task A_replacement_outside_the_project_is_refused()
    {
        await using var harness = Harness();

        var outcome = await EditAsync(harness, new MechanicalEditRequest(@"..\..\outside.txt", "a", "b", null, true));

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Empty(harness.Agents.CodexRequests("thread/start"));
    }

    [Fact]
    public async Task The_run_says_where_the_text_was_replaced()
    {
        await using var harness = new CoordinatorHarness(("src/app.txt", "timeout = 30\nretries = 3\nretry timeout = 5\n"));
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents.ReviewerTurn(Step.Review("pass"));

        await EditAsync(harness, new MechanicalEditRequest("src/app.txt", "timeout", "deadline", ExpectedMatches: 2, AllOccurrences: true));

        Assert.Contains(
            harness.Observer.Notes(Stages.Local),
            note => note.Message == "Replaced 2 occurrence(s) in src/app.txt (lines 1, 3) locally. Model A was not called; review and checks follow as for any change.");
    }

    private static GitRepo RepositoryWithAService() =>
        GitRepo.WithFiles(("config.txt", "timeout = 30\n"), ("services/api/config.txt", "timeout = 30\n"), ("services/web/config.txt", "timeout = 30\n"));

    private static CoordinatorHarness InTheService(GitRepo repository)
    {
        var harness = new CoordinatorHarness(Path.Combine(repository.Path, "services", "api"));
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "config.txt", "bug"));
        harness.Agents.ReviewerTurn(Step.Review("pass"));
        return harness;
    }

    [Fact]
    public async Task A_path_is_read_from_the_directory_that_was_opened_which_need_not_be_the_root_of_the_repository()
    {
        using var repository = RepositoryWithAService();
        await using var harness = InTheService(repository);

        var outcome = await EditAsync(harness, new MechanicalEditRequest("config.txt", "timeout = 30", "timeout = 60", ExpectedMatches: 1, AllOccurrences: false));

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Equal("services/api/config.txt", Assert.Single(outcome.Candidate!.Changes.Files).Path);
        await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);
        Assert.Equal("timeout = 60\n", repository.Read("services/api/config.txt"));
        Assert.Equal("timeout = 30\n", repository.Read("config.txt"));
        Assert.Equal("timeout = 30\n", repository.Read("services/web/config.txt"));
    }

    [Theory]
    [InlineData(@"..\..\config.txt")]
    [InlineData("../web/config.txt")]
    public async Task A_path_that_leaves_the_directory_that_was_opened_is_refused_although_it_stays_in_the_repository(string path)
    {
        using var repository = RepositoryWithAService();
        await using var harness = InTheService(repository);

        var outcome = await EditAsync(harness, new MechanicalEditRequest(path, "timeout = 30", "timeout = 60", ExpectedMatches: 1, AllOccurrences: false));

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains($"'{path}' is not a path inside the project.", outcome.Reason, StringComparison.Ordinal);
        Assert.Null(outcome.Candidate);
        Assert.Empty(harness.Agents.CodexRequests("thread/start"));
    }
}

public class WorkspaceChoiceTests
{
    private const string Task = "Make the app say fixed.";

    [Fact]
    public async Task What_the_user_had_changed_before_the_task_is_not_attributed_to_the_task()
    {
        await using var harness = new CoordinatorHarness(("src/app.txt", "one\n"), ("src/other.txt", "two\n"));
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.WriteProject("src/other.txt", "two, edited by the user and not committed\n");
        harness.WriteProject("notes.txt", "an untracked file of the user\n");
        harness.Agents.ImplementerTurn(Step.Write("src/app.txt", "fixed\n")).ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Equal("src/app.txt", Assert.Single(outcome.Candidate!.Changes.Files).Path);
        var prompt = Assert.Single(harness.ReviewerPrompts());
        Assert.DoesNotContain("edited by the user", prompt, StringComparison.Ordinal);
        Assert.Contains(harness.Observer.Notes(Stages.Prepare), n => n.Message.Contains("uncommitted change(s) of yours are part of the baseline", StringComparison.Ordinal));

        var workspace = await harness.Workspaces.FindAsync(harness.Database.FindTask(outcome.TaskId!)!.WorkspaceId!, CancellationToken.None);
        Assert.Equal("two, edited by the user and not committed\n", WorkspaceHarness.ReadIn(workspace!, "src/other.txt"));
        Assert.Equal("an untracked file of the user\n", WorkspaceHarness.ReadIn(workspace!, "notes.txt"));
    }

    [Fact]
    public async Task A_project_that_is_not_a_git_repository_is_worked_on_in_a_protected_copy()
    {
        using var directory = new TempDirectory("plain");
        directory.Write("src/app.txt", "one\n");
        await using var harness = new CoordinatorHarness(directory.Path);
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents.ImplementerTurn(Step.Write("src/app.txt", "fixed\n")).ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Equal("one\n", directory.Read("src/app.txt"));
        Assert.Contains(harness.Observer.Notes(Stages.Prepare), n => n.Message.Contains("protected copy", StringComparison.Ordinal));
        var delivery = await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);
        Assert.True(delivery.Succeeded, delivery.Message);
        Assert.Equal("fixed\n", directory.Read("src/app.txt"));
    }

    [Fact]
    public async Task Working_in_the_project_itself_needs_an_explicit_acknowledgement()
    {
        using var directory = new TempDirectory("inplace");
        directory.Write("src/app.txt", "one\n");
        await using var harness = new CoordinatorHarness(directory.Path);
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents.ImplementerTurn(Step.Write("src/app.txt", "fixed\n")).ReviewerTurn(Step.Review("pass"));

        var refused = await harness.RunAsync(Task, mode: WorkspaceMode.InPlace);
        harness.Database.AcknowledgeInPlace(directory.Path, "Weaker protection accepted.");
        var outcome = await harness.RunAsync(Task, mode: WorkspaceMode.InPlace);

        Assert.Equal(RunOutcomeKind.Blocked, refused.Kind);
        Assert.Contains(refused.Problems, p => p.Code == "workspace-in-place");
        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Equal("fixed\n", directory.Read("src/app.txt"));
        Assert.Contains(harness.Observer.Notes(Stages.Prepare), n => n.Message.Contains("not isolated", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Files_that_would_be_missing_from_the_isolated_copy_are_the_users_decision()
    {
        await using var harness = new CoordinatorHarness(("src/app.txt", "one\n"), (".gitignore", "local.settings\n"));
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.WriteProject("local.settings", "port=8080\n");
        harness.Agents.ImplementerTurn(Step.Write("src/app.txt", "fixed\n")).ReviewerTurn(Step.Review("pass"));

        var refused = await harness.RunAsync(Task);
        var startedByTheRefusedRun = harness.Agents.CodexRequests("thread/start").Count;
        var preflight = await harness.Coordinator.PreflightAsync(new RunRequest(harness.ProjectPath, Task), harness.Configuration, CancellationToken.None);
        harness.Database.AcknowledgeGaps(harness.ProjectPath, RunCoordinator.GapsFingerprint(preflight.Inspection!.EquivalenceGaps), "Accepted.");
        var outcome = await harness.RunAsync(Task);

        Assert.Equal(RunOutcomeKind.Blocked, refused.Kind);
        var problem = Assert.Single(refused.Problems, p => p.Code == "workspace-gaps");
        Assert.Contains("local.settings", problem.Message, StringComparison.Ordinal);
        Assert.Equal(0, startedByTheRefusedRun);
        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
    }

    [Fact]
    public async Task A_project_directory_that_does_not_exist_blocks_with_a_plain_reason()
    {
        await using var harness = new CoordinatorHarness();
        var missing = Path.Combine(harness.ProjectPath, "does", "not", "exist");

        var outcome = await harness.Coordinator.RunAsync(
            new RunRequest(missing, Task), harness.Configuration, harness.Observer, harness.Approvals, CancellationToken.None);

        Assert.Equal(RunOutcomeKind.Blocked, outcome.Kind);
        Assert.Contains(outcome.Problems, p => p.Code == "project-missing");
    }
}

public class StartingReferenceTests
{
    [Fact]
    public void Paths_named_in_the_request_are_offered_when_they_exist()
    {
        using var directory = new TempDirectory("refs");
        directory.Write("src/login.cs", "x");
        directory.Write("README.md", "x");
        directory.CreateDirectory("docs");

        var references = StartingReferences.Find(
            "Fix the bug in src/login.cs (see `README.md`), and update docs/. Ignore src/missing.cs.", directory.Path, Builders.Now);

        Assert.Equal(["src/login.cs", "README.md", "docs"], references.Select(r => r.Path).ToArray());
        Assert.All(references, r => Assert.Equal(Builders.Now, r.VerifiedAt));
        Assert.Contains("directory", references[2].Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_path_outside_the_workspace_is_never_offered()
    {
        using var directory = new TempDirectory("refs");
        directory.Write("inside/a.txt", "x");
        var outside = directory.Write("outside.txt", "x");

        var references = StartingReferences.Find(
            $@"Look at ..\outside.txt and {outside} and C:\Windows\win.ini and inside/../../outside.txt", Path.Combine(directory.Path, "inside"), Builders.Now);

        Assert.Empty(references);
    }

    [Fact]
    public void Ordinary_words_and_addresses_are_not_mistaken_for_paths()
    {
        using var directory = new TempDirectory("refs");
        directory.Write("fix", "x");
        directory.Write("e.g", "x");

        var references = StartingReferences.Find(
            "Please fix the login, e.g. as described at https://example.invalid/docs/login.html. Use *.cs files only?", directory.Path, Builders.Now);

        Assert.Empty(references);
    }

    [Fact]
    public void The_same_path_is_offered_once_and_the_list_is_bounded()
    {
        using var directory = new TempDirectory("refs");
        var names = Enumerable.Range(1, 30).Select(i => $"file{i}.txt").ToList();
        foreach (var name in names)
        {
            directory.Write(name, "x");
        }

        var references = StartingReferences.Find("file1.txt FILE1.TXT ./file1.txt " + string.Join(' ', names), directory.Path, Builders.Now);

        Assert.Equal(12, references.Count);
        Assert.Equal(references.Count, references.Select(r => r.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
