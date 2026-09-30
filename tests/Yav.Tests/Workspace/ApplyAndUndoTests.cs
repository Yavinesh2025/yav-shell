using Yav.Core.Ports;
using Yav.Core.Runs;
using Yav.Tests.Support;
using Yav.Workspace;

namespace Yav.Tests.Workspace;

public class ApplyTests
{
    private static async Task<(IsolatedWorkspace Workspace, Candidate Candidate)> CandidateAsync(WorkspaceHarness harness, GitRepo repo)
    {
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(workspace, "edit.txt", "edited by the agent\n");
        WorkspaceHarness.WriteIn(workspace, "new dir/ünï 日本/added.txt", "added by the agent\n");
        File.Delete(WorkspaceHarness.InWorkspace(workspace, "remove.txt"));
        return (workspace, await harness.FreezeAsync(workspace));
    }

    private static GitRepo Project() =>
        GitRepo.WithFiles(("edit.txt", "original\n"), ("remove.txt", "to be removed\n"), ("untouched.txt", "untouched\n"));

    [Fact]
    public async Task Applying_writes_the_candidate_into_the_project()
    {
        using var harness = new WorkspaceHarness();
        using var repo = Project();
        var (workspace, candidate) = await CandidateAsync(harness, repo);

        var result = await harness.Service.ApplyAsync(workspace, candidate, "run-1", CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Empty(result.Conflicts);
        Assert.Equal("edited by the agent\n", repo.Read("edit.txt"));
        Assert.Equal("added by the agent\n", repo.Read("new dir/ünï 日本/added.txt"));
        Assert.False(repo.Exists("remove.txt"));
        Assert.Equal("untouched\n", repo.Read("untouched.txt"));
        Assert.Equal(JournalState.Committed, result.Journal!.State);
        Assert.All(result.Journal.Entries, e => Assert.True(e.Done));
        Assert.Equal(JournalState.Committed, harness.Database.FindForRun("run-1")!.State);
    }

    [Fact]
    public async Task Applying_does_not_stage_commit_or_move_anything_in_git()
    {
        using var harness = new WorkspaceHarness();
        using var repo = Project();
        repo.Write("untouched.txt", "user staged this\n");
        repo.Git("add", "untouched.txt");
        var (workspace, candidate) = await CandidateAsync(harness, repo);
        var headBefore = repo.Head;
        var indexBefore = File.ReadAllBytes(Path.Combine(repo.Path, ".git", "index"));
        var refsBefore = repo.Git("for-each-ref");

        await harness.Service.ApplyAsync(workspace, candidate, "run-1", CancellationToken.None);

        Assert.Equal(headBefore, repo.Head);
        Assert.Equal(indexBefore, File.ReadAllBytes(Path.Combine(repo.Path, ".git", "index")));
        Assert.Equal(refsBefore, repo.Git("for-each-ref"));
        Assert.Equal(string.Empty, repo.Git("stash", "list"));
        Assert.Equal("user staged this\n", repo.Read("untouched.txt"));
    }

    [Fact]
    public async Task What_is_applied_is_the_frozen_candidate_not_whatever_the_workspace_holds_now()
    {
        using var harness = new WorkspaceHarness();
        using var repo = Project();
        var (workspace, candidate) = await CandidateAsync(harness, repo);
        WorkspaceHarness.WriteIn(workspace, "edit.txt", "changed after review and tests\n");
        WorkspaceHarness.WriteIn(workspace, "sneaked in.txt", "never reviewed\n");

        await harness.Service.ApplyAsync(workspace, candidate, "run-1", CancellationToken.None);

        Assert.Equal("edited by the agent\n", repo.Read("edit.txt"));
        Assert.False(repo.Exists("sneaked in.txt"));
    }

    [Fact]
    public async Task A_file_the_user_edited_meanwhile_is_never_overwritten()
    {
        using var harness = new WorkspaceHarness();
        using var repo = Project();
        var (workspace, candidate) = await CandidateAsync(harness, repo);
        repo.Write("edit.txt", "the user kept working here\n");

        var preflight = await harness.Service.PreflightApplyAsync(workspace, candidate, CancellationToken.None);
        var result = await harness.Service.ApplyAsync(workspace, candidate, "run-1", CancellationToken.None);

        Assert.False(preflight.CanApply);
        var conflict = Assert.Single(preflight.Conflicts);
        Assert.Equal("edit.txt", conflict.Path);
        Assert.Equal(ApplyConflictKind.ConcurrentEdit, conflict.Kind);
        Assert.False(result.Applied);
        Assert.Equal("the user kept working here\n", repo.Read("edit.txt"));
        // Nothing at all is written when any file conflicts.
        Assert.False(repo.Exists("new dir/ünï 日本/added.txt"));
        Assert.True(repo.Exists("remove.txt"));
    }

    [Fact]
    public async Task A_file_the_candidate_adds_that_now_exists_with_other_content_is_a_conflict()
    {
        using var harness = new WorkspaceHarness();
        using var repo = Project();
        var (workspace, candidate) = await CandidateAsync(harness, repo);
        repo.Write("new dir/ünï 日本/added.txt", "the user created this first\n");

        var result = await harness.Service.ApplyAsync(workspace, candidate, "run-1", CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(ApplyConflictKind.AlreadyExists, Assert.Single(result.Conflicts).Kind);
        Assert.Equal("the user created this first\n", repo.Read("new dir/ünï 日本/added.txt"));
    }

    [Fact]
    public async Task A_file_the_user_already_changed_to_the_same_content_is_not_a_conflict()
    {
        using var harness = new WorkspaceHarness();
        using var repo = Project();
        var (workspace, candidate) = await CandidateAsync(harness, repo);
        repo.Write("edit.txt", "edited by the agent\n");

        var result = await harness.Service.ApplyAsync(workspace, candidate, "run-1", CancellationToken.None);

        Assert.True(result.Applied);
    }

    [Fact]
    public async Task A_file_the_user_deleted_that_the_candidate_changes_is_a_conflict()
    {
        using var harness = new WorkspaceHarness();
        using var repo = Project();
        var (workspace, candidate) = await CandidateAsync(harness, repo);
        repo.Delete("edit.txt");

        var result = await harness.Service.ApplyAsync(workspace, candidate, "run-1", CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Equal(ApplyConflictKind.Missing, Assert.Single(result.Conflicts).Kind);
    }

    [Fact]
    public async Task Edits_to_other_files_do_not_block_and_are_reported()
    {
        using var harness = new WorkspaceHarness();
        using var repo = Project();
        var (workspace, candidate) = await CandidateAsync(harness, repo);
        repo.Write("untouched.txt", "the user edited an unrelated file\n");

        var preflight = await harness.Service.PreflightApplyAsync(workspace, candidate, CancellationToken.None);
        var result = await harness.Service.ApplyAsync(workspace, candidate, "run-1", CancellationToken.None);

        Assert.True(preflight.CanApply);
        Assert.Equal(["untouched.txt"], preflight.UnrelatedConcurrentEdits);
        Assert.True(result.Applied);
        Assert.Equal("the user edited an unrelated file\n", repo.Read("untouched.txt"));
    }

    [Fact]
    public async Task A_locked_file_stops_the_apply_and_everything_already_written_is_rolled_back()
    {
        using var harness = new WorkspaceHarness();
        using var repo = Project();
        var (workspace, candidate) = await CandidateAsync(harness, repo);
        var before = repo.WorkingTree();

        // Another program holds the file open: it can be read, but not deleted or replaced.
        ApplyResult result;
        using (new FileStream(repo.File("remove.txt"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            result = await harness.Service.ApplyAsync(workspace, candidate, "run-1", CancellationToken.None);
        }

        Assert.False(result.Applied);
        Assert.True(result.RolledBack);
        Assert.Equal(before, repo.WorkingTree());
        Assert.Equal(JournalState.RolledBack, harness.Database.FindForRun("run-1")!.State);
    }

    [Fact]
    public async Task An_apply_that_a_crash_interrupted_is_rolled_back_on_the_next_start()
    {
        using var harness = new WorkspaceHarness();
        using var repo = Project();
        var (workspace, candidate) = await CandidateAsync(harness, repo);
        var before = repo.WorkingTree();

        // The process dies while the third file is being written: the journal is still in progress.
        var dying = harness.CreateService(new CrashingFileOperations(crashOnOperation: 3));
        await Assert.ThrowsAsync<SimulatedCrashException>(() => dying.ApplyAsync(workspace, candidate, "run-1", CancellationToken.None));
        Assert.Equal(JournalState.InProgress, harness.Database.FindForRun("run-1")!.State);
        Assert.NotEqual(before, repo.WorkingTree());

        var reconciled = await harness.Service.ReconcileJournalsAsync(CancellationToken.None);

        Assert.Equal(JournalState.RolledBack, Assert.Single(reconciled).State);
        Assert.Equal(before, repo.WorkingTree());
        Assert.Empty(harness.Database.FindUnfinished());
    }

    [Fact]
    public async Task Reconciling_does_not_undo_an_edit_the_user_made_after_the_crash()
    {
        using var harness = new WorkspaceHarness();
        using var repo = Project();
        var (workspace, candidate) = await CandidateAsync(harness, repo);
        // edit.txt and the added file were written before the process died.
        var dying = harness.CreateService(new CrashingFileOperations(crashOnOperation: 3));
        await Assert.ThrowsAsync<SimulatedCrashException>(() => dying.ApplyAsync(workspace, candidate, "run-1", CancellationToken.None));
        repo.Write("edit.txt", "edited by the user after the crash\n");

        var reconciled = await harness.Service.ReconcileJournalsAsync(CancellationToken.None);

        Assert.Equal("edited by the user after the crash\n", repo.Read("edit.txt"));
        Assert.Equal(JournalState.Interrupted, Assert.Single(reconciled).State);
    }

    [Fact]
    public async Task A_candidate_without_changes_has_nothing_to_apply()
    {
        using var harness = new WorkspaceHarness();
        using var repo = Project();
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        var candidate = await harness.FreezeAsync(workspace);

        var result = await harness.Service.ApplyAsync(workspace, candidate, "run-1", CancellationToken.None);

        Assert.False(result.Applied);
        Assert.Null(result.Journal);
        Assert.Contains("no changes", result.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_project_without_git_is_applied_the_same_way()
    {
        using var harness = new WorkspaceHarness();
        using var project = new TempDirectory("plain");
        project.Write("main.py", "print('hi')\n");
        var (_, workspace) = await harness.PrepareAsync(project.Path);
        WorkspaceHarness.WriteIn(workspace, "main.py", "print('hello')\n");
        WorkspaceHarness.WriteIn(workspace, "lib/new.py", "y = 2\n");
        var candidate = await harness.FreezeAsync(workspace);

        var result = await harness.Service.ApplyAsync(workspace, candidate, "run-1", CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal("print('hello')\n", project.Read("main.py"));
        Assert.Equal("y = 2\n", project.Read("lib/new.py"));
    }
}

public class UndoTests
{
    private static async Task<(GitRepo Repo, ApplyJournal Journal, Dictionary<string, string> Before)> AppliedAsync(WorkspaceHarness harness)
    {
        var repo = GitRepo.WithFiles(("edit.txt", "original\n"), ("remove.txt", "to be removed\n"), ("untouched.txt", "untouched\n"));
        var before = repo.WorkingTree();
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(workspace, "edit.txt", "edited by the agent\n");
        WorkspaceHarness.WriteIn(workspace, "new dir/added.txt", "added by the agent\n");
        File.Delete(WorkspaceHarness.InWorkspace(workspace, "remove.txt"));
        var candidate = await harness.FreezeAsync(workspace);
        var result = await harness.Service.ApplyAsync(workspace, candidate, "run-1", CancellationToken.None);
        return (repo, result.Journal!, before);
    }

    [Fact]
    public async Task Undo_puts_back_exactly_what_was_there_before_the_apply()
    {
        using var harness = new WorkspaceHarness();
        var (repo, journal, before) = await AppliedAsync(harness);
        using var _ = repo;

        var result = await harness.Service.UndoAsync(journal, skipConflicts: false, CancellationToken.None);

        Assert.True(result.Undone);
        Assert.Equal(before, repo.WorkingTree());
        Assert.False(Directory.Exists(repo.File("new dir")), "The directory the apply created should be removed again.");
        Assert.Equal(JournalState.Undone, harness.Database.Find(journal.JournalId)!.State);
    }

    [Fact]
    public async Task Undo_refuses_when_the_user_edited_an_applied_file_afterwards()
    {
        using var harness = new WorkspaceHarness();
        var (repo, journal, _) = await AppliedAsync(harness);
        using var _2 = repo;
        repo.Write("edit.txt", "the user improved the agent's version\n");
        var afterEdit = repo.WorkingTree();

        var result = await harness.Service.UndoAsync(journal, skipConflicts: false, CancellationToken.None);

        Assert.False(result.Undone);
        Assert.Equal("edit.txt", Assert.Single(result.Conflicts).Path);
        Assert.Equal(afterEdit, repo.WorkingTree());
        Assert.Equal(JournalState.Committed, harness.Database.Find(journal.JournalId)!.State);
    }

    [Fact]
    public async Task Undo_can_skip_the_conflicting_files_when_asked_to()
    {
        using var harness = new WorkspaceHarness();
        var (repo, journal, _) = await AppliedAsync(harness);
        using var _2 = repo;
        repo.Write("edit.txt", "the user improved the agent's version\n");

        var result = await harness.Service.UndoAsync(journal, skipConflicts: true, CancellationToken.None);

        Assert.True(result.Undone);
        Assert.Equal("the user improved the agent's version\n", repo.Read("edit.txt"));
        Assert.Equal("to be removed\n", repo.Read("remove.txt"));
        Assert.False(repo.Exists("new dir/added.txt"));
        Assert.Equal(["new dir/added.txt", "remove.txt"], result.Restored.Order());
    }

    [Fact]
    public async Task Undo_refuses_to_delete_an_added_file_the_user_changed()
    {
        using var harness = new WorkspaceHarness();
        var (repo, journal, _) = await AppliedAsync(harness);
        using var _2 = repo;
        repo.Write("new dir/added.txt", "the user extended the new file\n");

        var result = await harness.Service.UndoAsync(journal, skipConflicts: false, CancellationToken.None);

        Assert.False(result.Undone);
        Assert.Equal("the user extended the new file\n", repo.Read("new dir/added.txt"));
    }

    [Fact]
    public async Task Undo_refuses_to_restore_over_a_file_the_user_created_where_one_was_deleted()
    {
        using var harness = new WorkspaceHarness();
        var (repo, journal, _) = await AppliedAsync(harness);
        using var _2 = repo;
        repo.Write("remove.txt", "the user made a new file with the old name\n");

        var result = await harness.Service.UndoAsync(journal, skipConflicts: false, CancellationToken.None);

        Assert.False(result.Undone);
        Assert.Equal("the user made a new file with the old name\n", repo.Read("remove.txt"));
    }

    [Fact]
    public async Task An_apply_can_only_be_undone_once()
    {
        using var harness = new WorkspaceHarness();
        var (repo, journal, _) = await AppliedAsync(harness);
        using var _2 = repo;
        await harness.Service.UndoAsync(journal, skipConflicts: false, CancellationToken.None);
        var undone = harness.Database.Find(journal.JournalId)!;

        var again = await harness.Service.UndoAsync(undone, skipConflicts: false, CancellationToken.None);

        Assert.False(again.Undone);
        Assert.Contains("already", again.Error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_journal_whose_paths_point_outside_the_project_is_refused()
    {
        using var harness = new WorkspaceHarness();
        var (repo, journal, _) = await AppliedAsync(harness);
        using var _2 = repo;
        var outside = Path.Combine(Path.GetDirectoryName(repo.Path)!, "outside-" + Guid.NewGuid().ToString("N")[..6] + ".txt");
        File.WriteAllText(outside, "must survive");
        try
        {
            var forged = journal with
            {
                Entries = [new JournalEntry(0, "../" + Path.GetFileName(outside), ChangeKind.Added, null, journal.Entries[0].PostImageHash, true)],
            };

            var result = await harness.Service.UndoAsync(forged, skipConflicts: true, CancellationToken.None);

            Assert.False(result.Undone);
            Assert.Equal(ApplyConflictKind.UnsafePath, Assert.Single(result.Conflicts).Kind);
            Assert.Equal("must survive", File.ReadAllText(outside));
        }
        finally
        {
            File.Delete(outside);
        }
    }
}

public class MergeTests
{
    [Fact]
    public async Task Edits_the_user_made_to_other_files_are_brought_into_the_workspace()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("agent.txt", "one\n"), ("user.txt", "one\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(workspace, "agent.txt", "agent\n");
        var candidate = await harness.FreezeAsync(workspace);
        repo.Write("user.txt", "user\n");
        repo.Write("user new.txt", "new from user\n");

        var merge = await harness.Service.MergeConcurrentEditsAsync(workspace, candidate, CancellationToken.None);
        var refreshed = (await harness.Service.FindAsync(workspace.WorkspaceId, CancellationToken.None))!;
        var merged = await harness.FreezeAsync(refreshed, sequence: 2);

        Assert.True(merge.Merged);
        Assert.Empty(merge.Unresolved);
        Assert.Equal("user\n", WorkspaceHarness.ReadIn(refreshed, "user.txt"));
        Assert.Equal("new from user\n", WorkspaceHarness.ReadIn(refreshed, "user new.txt"));
        Assert.Equal("agent\n", WorkspaceHarness.ReadIn(refreshed, "agent.txt"));
        Assert.Equal(merge.NewBaselineFingerprint, refreshed.BaselineFingerprint);
        // Against the new baseline only the agent's change remains, and the candidate is a different one.
        Assert.Equal("agent.txt", Assert.Single(merged.Changes.Files).Path);
        Assert.NotEqual(candidate.Fingerprint, merged.Fingerprint);
    }

    [Fact]
    public async Task Edits_to_different_parts_of_the_same_file_are_merged()
    {
        using var harness = new WorkspaceHarness();
        var original = string.Join('\n', Enumerable.Range(1, 20).Select(i => $"line {i}")) + "\n";
        using var repo = GitRepo.WithFiles(("shared.txt", original));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(workspace, "shared.txt", original.Replace("line 2\n", "line 2 (agent)\n"));
        var candidate = await harness.FreezeAsync(workspace);
        repo.Write("shared.txt", original.Replace("line 19\n", "line 19 (user)\n"));

        var merge = await harness.Service.MergeConcurrentEditsAsync(workspace, candidate, CancellationToken.None);

        Assert.True(merge.Merged);
        Assert.Equal(["shared.txt"], merge.MergedFiles);
        var text = WorkspaceHarness.ReadIn(workspace, "shared.txt");
        Assert.Contains("line 2 (agent)\n", text);
        Assert.Contains("line 19 (user)\n", text);
        // The project itself is not written by a merge.
        Assert.DoesNotContain("(agent)", repo.Read("shared.txt"));
    }

    [Fact]
    public async Task Edits_to_the_same_lines_are_left_unresolved_for_a_decision()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("shared.txt", "one\ntwo\nthree\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(workspace, "shared.txt", "one\nagent\nthree\n");
        var candidate = await harness.FreezeAsync(workspace);
        repo.Write("shared.txt", "one\nuser\nthree\n");

        var merge = await harness.Service.MergeConcurrentEditsAsync(workspace, candidate, CancellationToken.None);

        Assert.False(merge.Merged);
        Assert.Equal(["shared.txt"], merge.Unresolved);
        Assert.Equal("one\nuser\nthree\n", repo.Read("shared.txt"));
        Assert.Equal("one\nagent\nthree\n", WorkspaceHarness.ReadIn(workspace, "shared.txt"));
    }

    [Fact]
    public async Task After_an_apply_the_project_state_becomes_the_new_baseline()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "one\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(workspace, "a.txt", "agent\n");
        var candidate = await harness.FreezeAsync(workspace);
        await harness.Service.ApplyAsync(workspace, candidate, "run-1", CancellationToken.None);

        var merge = await harness.Service.MergeConcurrentEditsAsync(workspace, candidate, CancellationToken.None);
        var refreshed = (await harness.Service.FindAsync(workspace.WorkspaceId, CancellationToken.None))!;
        var next = await harness.FreezeAsync(refreshed, sequence: 2);

        Assert.True(merge.Merged);
        Assert.True(next.Changes.IsEmpty);
        Assert.Equal(refreshed.BaselineFingerprint, next.Fingerprint);
    }
}

public class DiscardAndGuardTests
{
    [Fact]
    public async Task Discarding_removes_the_isolated_data_and_leaves_the_project_alone()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "one\n"));
        repo.Write("a.txt", "user's uncommitted edit\n");
        var before = repo.Snapshot();
        var treeBefore = repo.WorkingTree();
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(workspace, "a.txt", "agent\n");

        await harness.Service.DiscardAsync(workspace, CancellationToken.None);

        Assert.False(Directory.Exists(workspace.RootPath));
        Assert.Null(await harness.Service.FindAsync(workspace.WorkspaceId, CancellationToken.None));
        Assert.Equal(before, repo.Snapshot());
        Assert.Equal(treeBefore, repo.WorkingTree());
        Assert.DoesNotContain(Path.GetFileName(Path.GetDirectoryName(workspace.RootPath)!), repo.Git("worktree", "list"));
    }

    [Fact]
    public async Task A_workspace_record_that_points_outside_yav_storage_is_never_deleted()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "one\n"));
        using var victim = new TempDirectory("victim");
        victim.Write("precious.txt", "do not delete");
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        var forged = workspace with { RootPath = victim.Path, MetadataPath = victim.Path, EvidencePath = victim.Path };

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.DiscardAsync(forged, CancellationToken.None));

        Assert.Equal("do not delete", victim.Read("precious.txt"));
    }

    [Fact]
    public async Task An_untouched_repository_passes_the_guard()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "one\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(workspace, "a.txt", "agent\n");

        var differences = await harness.Service.VerifyRepositoryUntouchedAsync(workspace, CancellationToken.None);

        Assert.Empty(differences);
    }

    [Fact]
    public async Task A_branch_created_or_moved_in_the_shared_repository_is_reported()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "one\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);

        // Run from inside the worktree, as an agent would: branches live in the shared repository.
        var runner = new Yav.Platform.Processes.ProcessRunner();
        await runner.RunAsync(
            new ProcessSpec("git", ["branch", "agent-made-this"], workspace.RootPath),
            new CaptureOptions(Timeout: TimeSpan.FromSeconds(30)),
            CancellationToken.None);

        var differences = await harness.Service.VerifyRepositoryUntouchedAsync(workspace, CancellationToken.None);

        Assert.Contains(differences, d => d.Contains("agent-made-this"));
    }
}

public class MechanicalEditTests
{
    [Fact]
    public async Task The_preview_counts_the_matches_and_shows_the_change()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("src/config.cs", "var timeout = 30;\nvar retries = 3;\nvar other = timeout;\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);

        var preview = await harness.Service.PreviewMechanicalEditAsync(
            workspace, new MechanicalEditRequest("src/config.cs", "timeout = 30", "timeout = 60", ExpectedMatches: 1, AllOccurrences: false), CancellationToken.None);

        Assert.True(preview.Valid);
        Assert.Equal(1, preview.MatchCount);
        Assert.Equal([1], preview.MatchLines);
        Assert.Contains("-var timeout = 30;\n+var timeout = 60;\n", preview.PreviewDiff);
        Assert.Equal("var timeout = 30;\nvar retries = 3;\nvar other = timeout;\n", WorkspaceHarness.ReadIn(workspace, "src/config.cs"));
    }

    [Fact]
    public async Task An_edit_that_matches_more_than_once_is_ambiguous()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "value\nvalue\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);

        var preview = await harness.Service.PreviewMechanicalEditAsync(
            workspace, new MechanicalEditRequest("a.txt", "value", "other", ExpectedMatches: null, AllOccurrences: false), CancellationToken.None);

        Assert.False(preview.Valid);
        Assert.Equal(2, preview.MatchCount);
        Assert.Equal([1, 2], preview.MatchLines);
        Assert.Contains("2", preview.Problem);
    }

    [Theory]
    [InlineData("a.txt", "missing text", 0)]
    [InlineData("no such file.txt", "value", 0)]
    public async Task An_edit_with_nothing_to_replace_is_refused(string path, string expected, int matches)
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "value\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);

        var preview = await harness.Service.PreviewMechanicalEditAsync(
            workspace, new MechanicalEditRequest(path, expected, "other", null, false), CancellationToken.None);

        Assert.False(preview.Valid);
        Assert.Equal(matches, preview.MatchCount);
    }

    [Fact]
    public async Task An_edit_whose_match_count_differs_from_the_expected_count_is_refused()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "value\nvalue\nvalue\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);

        var preview = await harness.Service.PreviewMechanicalEditAsync(
            workspace, new MechanicalEditRequest("a.txt", "value", "other", ExpectedMatches: 2, AllOccurrences: true), CancellationToken.None);

        Assert.False(preview.Valid);
        Assert.Equal(3, preview.MatchCount);
    }

    [Theory]
    [InlineData("../outside.txt")]
    [InlineData("C:/Windows/win.ini")]
    [InlineData(".git/config")]
    public async Task An_edit_outside_the_workspace_is_refused(string path)
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "value\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);

        var preview = await harness.Service.PreviewMechanicalEditAsync(
            workspace, new MechanicalEditRequest(path, "value", "other", null, false), CancellationToken.None);

        Assert.False(preview.Valid);
    }

    [Fact]
    public async Task Applying_replaces_every_occurrence_in_the_workspace_and_keeps_line_endings()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "old name\r\nuses old name twice: old name\r\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        var request = new MechanicalEditRequest("a.txt", "old name", "new name", ExpectedMatches: 3, AllOccurrences: true);

        await harness.Service.ApplyMechanicalEditAsync(workspace, request, CancellationToken.None);
        var candidate = await harness.FreezeAsync(workspace);

        Assert.Equal("new name\r\nuses new name twice: new name\r\n", WorkspaceHarness.ReadIn(workspace, "a.txt"));
        Assert.Equal("old name\r\nuses old name twice: old name\r\n", repo.Read("a.txt"));
        Assert.Equal("a.txt", Assert.Single(candidate.Changes.Files).Path);
    }

    [Fact]
    public async Task Applying_an_invalid_edit_changes_nothing()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "value\nvalue\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);

        await Assert.ThrowsAsync<InvalidOperationException>(() => harness.Service.ApplyMechanicalEditAsync(
            workspace, new MechanicalEditRequest("a.txt", "value", "other", null, false), CancellationToken.None));

        Assert.Equal("value\nvalue\n", WorkspaceHarness.ReadIn(workspace, "a.txt"));
    }
}
