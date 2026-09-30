using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.Workspace;

public class FreezeTests
{
    [Fact]
    public async Task Added_modified_and_deleted_files_are_found()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("keep.txt", "same\n"), ("edit.txt", "old\n"), ("remove.txt", "bye\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(workspace, "edit.txt", "new\n");
        WorkspaceHarness.WriteIn(workspace, "src/ünï dir/added file.cs", "class Added {}\n");
        File.Delete(WorkspaceHarness.InWorkspace(workspace, "remove.txt"));

        var candidate = await harness.FreezeAsync(workspace);

        Assert.Equal(
            [("edit.txt", ChangeKind.Modified), ("remove.txt", ChangeKind.Deleted), ("src/ünï dir/added file.cs", ChangeKind.Added)],
            candidate.Changes.Files.Select(f => (f.Path, f.Kind)));
        Assert.NotEqual(workspace.BaselineFingerprint, candidate.Fingerprint);
    }

    [Fact]
    public async Task Edits_the_user_made_before_the_task_are_not_attributed_to_the_task()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("mine.txt", "committed\n"), ("agent.txt", "committed\n"));
        repo.Write("mine.txt", "my uncommitted edit\n");
        repo.Write("my new file.txt", "mine too\n");
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(workspace, "agent.txt", "changed by the agent\n");

        var candidate = await harness.FreezeAsync(workspace);

        var change = Assert.Single(candidate.Changes.Files);
        Assert.Equal("agent.txt", change.Path);
    }

    [Fact]
    public async Task No_change_gives_the_baseline_fingerprint_and_an_empty_change_set()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "one\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);

        var candidate = await harness.FreezeAsync(workspace);

        Assert.True(candidate.Changes.IsEmpty);
        Assert.Equal(workspace.BaselineFingerprint, candidate.Fingerprint);
    }

    [Fact]
    public async Task Rewriting_a_file_with_the_same_content_is_not_a_change()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "one\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(workspace, "a.txt", "one\n");
        File.SetLastWriteTimeUtc(WorkspaceHarness.InWorkspace(workspace, "a.txt"), DateTime.UtcNow.AddHours(1));

        var candidate = await harness.FreezeAsync(workspace);

        Assert.True(candidate.Changes.IsEmpty);
        Assert.Equal(workspace.BaselineFingerprint, candidate.Fingerprint);
    }

    [Fact]
    public async Task A_change_that_keeps_size_and_modification_time_is_still_found()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "AAAA\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        var path = WorkspaceHarness.InWorkspace(workspace, "a.txt");
        var stamp = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "BBBB\n");
        File.SetLastWriteTimeUtc(path, stamp);

        var candidate = await harness.FreezeAsync(workspace);

        Assert.Equal("a.txt", Assert.Single(candidate.Changes.Files).Path);
    }

    [Fact]
    public async Task A_moved_file_is_reported_with_where_it_came_from()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("src/old.cs", "class Moved { /* enough content to be a real file */ }\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        Directory.CreateDirectory(WorkspaceHarness.InWorkspace(workspace, "src/new home"));
        File.Move(WorkspaceHarness.InWorkspace(workspace, "src/old.cs"), WorkspaceHarness.InWorkspace(workspace, "src/new home/new.cs"));

        var candidate = await harness.FreezeAsync(workspace);

        var added = candidate.Changes.Files.Single(f => f.Kind == ChangeKind.Added);
        Assert.Equal("src/new home/new.cs", added.Path);
        Assert.Equal("src/old.cs", added.RenamedFrom);
        Assert.Contains(candidate.Changes.Files, f => f.Kind == ChangeKind.Deleted && f.Path == "src/old.cs");
    }

    [Fact]
    public async Task A_binary_file_is_marked_as_binary()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.Create();
        repo.WriteBytes("logo.bin", [0x89, 0x50, 0, 1, 2, 3]);
        repo.Write("notes.txt", "text\n");
        repo.CommitAll("initial");
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        File.WriteAllBytes(WorkspaceHarness.InWorkspace(workspace, "logo.bin"), [0x89, 0x50, 0, 9, 9, 9, 9]);
        WorkspaceHarness.WriteIn(workspace, "notes.txt", "changed\n");

        var candidate = await harness.FreezeAsync(workspace);

        Assert.True(candidate.Changes.Files.Single(f => f.Path == "logo.bin").IsBinary);
        Assert.False(candidate.Changes.Files.Single(f => f.Path == "notes.txt").IsBinary);
    }

    [Fact]
    public async Task Ignored_build_output_does_not_change_the_fingerprint()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "one\n"), (".gitignore", "bin/\n*.log\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(workspace, "bin/app.dll", "compiled");
        WorkspaceHarness.WriteIn(workspace, "test.log", "log output");

        var candidate = await harness.FreezeAsync(workspace);

        Assert.True(candidate.Changes.IsEmpty);
        Assert.Equal(workspace.BaselineFingerprint, candidate.Fingerprint);
    }

    [Fact]
    public async Task Changes_to_protected_paths_and_existing_tests_are_called_out()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(
            ("src/login.cs", "class Login {}\n"),
            ("tests/acceptance/login.spec.ts", "it('logs in', () => {});\n"),
            ("tests/unit/login.test.ts", "it('validates', () => {});\n"),
            ("yav.project.json", "{}\n"));
        var configuration = ProjectConfiguration.Empty with { ProtectedPaths = ["tests/acceptance/**", "yav.project.json"] };
        var (_, workspace) = await harness.PrepareAsync(repo.Path, configuration);
        WorkspaceHarness.WriteIn(workspace, "src/login.cs", "class Login { }\n");
        WorkspaceHarness.WriteIn(workspace, "tests/acceptance/login.spec.ts", "it.skip('logs in', () => {});\n");
        WorkspaceHarness.WriteIn(workspace, "tests/unit/login.test.ts", "it('validates more', () => {});\n");
        WorkspaceHarness.WriteIn(workspace, "tests/unit/new.test.ts", "it('is new', () => {});\n");
        WorkspaceHarness.WriteIn(workspace, "yav.project.json", "{ \"gates\": [] }\n");

        var candidate = await harness.FreezeAsync(workspace, configuration);

        Assert.Equal(["tests/acceptance/login.spec.ts", "yav.project.json"], candidate.ProtectedPathsTouched);
        // A new test is an addition, not a change to an existing test.
        Assert.Equal(["tests/acceptance/login.spec.ts", "tests/unit/login.test.ts"], candidate.ExistingTestsTouched);
    }

    [Fact]
    public async Task The_fingerprint_is_the_same_each_time_the_same_state_is_frozen()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "one\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(workspace, "a.txt", "two\n");

        var first = await harness.FreezeAsync(workspace, sequence: 1);
        var second = await harness.FreezeAsync(workspace, sequence: 2);
        var live = await harness.Service.FingerprintAsync(workspace, CancellationToken.None);

        Assert.Equal(first.Fingerprint, second.Fingerprint);
        Assert.Equal(first.Fingerprint, live);
    }

    [Fact]
    public async Task A_change_after_the_freeze_is_visible_as_a_different_fingerprint()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "one\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(workspace, "a.txt", "two\n");
        var candidate = await harness.FreezeAsync(workspace);

        WorkspaceHarness.WriteIn(workspace, "a.txt", "changed while the checks were running\n");
        var live = await harness.Service.FingerprintAsync(workspace, CancellationToken.None);

        Assert.NotEqual(candidate.Fingerprint, live);
    }

    [Fact]
    public async Task A_project_without_git_is_frozen_the_same_way()
    {
        using var harness = new WorkspaceHarness();
        using var project = new TempDirectory("plain");
        project.Write("main.py", "print('hi')\n");
        project.Write("lib/util.py", "x = 1\n");
        var (_, workspace) = await harness.PrepareAsync(project.Path);
        WorkspaceHarness.WriteIn(workspace, "main.py", "print('hello')\n");
        WorkspaceHarness.WriteIn(workspace, "lib/new.py", "y = 2\n");

        var candidate = await harness.FreezeAsync(workspace);

        Assert.Equal(
            [("lib/new.py", ChangeKind.Added), ("main.py", ChangeKind.Modified)],
            candidate.Changes.Files.Select(f => (f.Path, f.Kind)));
    }
}

public class DiffTests
{
    [Fact]
    public async Task The_diff_shows_the_task_changes_against_the_baseline()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("src/login.cs", "line one\nline two\nline three\n"), ("gone.txt", "bye\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(workspace, "src/login.cs", "line one\nline 2\nline three\n");
        WorkspaceHarness.WriteIn(workspace, "src/new.cs", "brand new\n");
        File.Delete(WorkspaceHarness.InWorkspace(workspace, "gone.txt"));
        var candidate = await harness.FreezeAsync(workspace);

        var diff = await harness.Service.DiffAsync(workspace, candidate, 100_000, CancellationToken.None);

        Assert.False(diff.Truncated);
        Assert.Equal(3, diff.FileCount);
        Assert.Contains("--- a/src/login.cs\n+++ b/src/login.cs\n@@ -1,3 +1,3 @@\n line one\n-line two\n+line 2\n line three\n", diff.Text);
        Assert.Contains("--- /dev/null\n+++ b/src/new.cs\n@@ -0,0 +1,1 @@\n+brand new\n", diff.Text);
        Assert.Contains("--- a/gone.txt\n+++ /dev/null\n@@ -1,1 +0,0 @@\n-bye\n", diff.Text);
    }

    [Fact]
    public async Task The_diff_is_of_the_frozen_candidate_even_after_the_workspace_changed()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "one\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(workspace, "a.txt", "frozen\n");
        var candidate = await harness.FreezeAsync(workspace);
        WorkspaceHarness.WriteIn(workspace, "a.txt", "changed later\n");

        var diff = await harness.Service.DiffAsync(workspace, candidate, 100_000, CancellationToken.None);

        Assert.Contains("+frozen\n", diff.Text);
        Assert.DoesNotContain("changed later", diff.Text);
    }

    [Fact]
    public async Task A_binary_change_is_named_but_not_printed()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.Create();
        repo.WriteBytes("logo.bin", [0x89, 0x50, 0, 1, 2, 3]);
        repo.CommitAll("initial");
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        File.WriteAllBytes(WorkspaceHarness.InWorkspace(workspace, "logo.bin"), [0x89, 0x50, 0, 9, 9]);
        var candidate = await harness.FreezeAsync(workspace);

        var diff = await harness.Service.DiffAsync(workspace, candidate, 100_000, CancellationToken.None);

        Assert.Equal(["logo.bin"], diff.BinaryFiles);
        Assert.Contains("Binary file logo.bin changed (6 -> 5 bytes)", diff.Text);
    }

    [Fact]
    public async Task A_long_diff_is_shortened_for_display_and_kept_complete_in_a_file()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("big.txt", "start\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        var lines = string.Join('\n', Enumerable.Range(1, 3000).Select(i => $"generated line number {i}")) + "\n";
        WorkspaceHarness.WriteIn(workspace, "big.txt", lines);
        var candidate = await harness.FreezeAsync(workspace);

        var diff = await harness.Service.DiffAsync(workspace, candidate, 2000, CancellationToken.None);

        Assert.True(diff.Truncated);
        Assert.True(diff.Text.Length <= 2200);
        Assert.NotNull(diff.FullDiffPath);
        var full = await File.ReadAllTextAsync(diff.FullDiffPath);
        Assert.Contains("+generated line number 3000\n", full);
        Assert.StartsWith(workspace.EvidencePath, diff.FullDiffPath, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_path_is_found_whether_it_exists_now_or_existed_before()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("src/Login.cs", "x\n"), ("removed.cs", "y\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        File.Delete(WorkspaceHarness.InWorkspace(workspace, "removed.cs"));
        WorkspaceHarness.WriteIn(workspace, "src/added.cs", "z\n");
        var candidate = await harness.FreezeAsync(workspace);

        var exists = await harness.Service.GetPathLookupAsync(workspace, candidate, CancellationToken.None);

        Assert.True(exists("src/Login.cs"));
        Assert.True(exists("src/login.cs"));
        Assert.True(exists("removed.cs"));
        Assert.True(exists("src/added.cs"));
        Assert.False(exists("src/never.cs"));
    }
}

public class ExecutionCopyTests
{
    [Fact]
    public async Task The_copy_holds_exactly_the_frozen_candidate()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "one\n"), ("sub dir/b.txt", "two\n"), ("gone.txt", "x\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(workspace, "a.txt", "candidate\n");
        File.Delete(WorkspaceHarness.InWorkspace(workspace, "gone.txt"));
        var candidate = await harness.FreezeAsync(workspace);
        WorkspaceHarness.WriteIn(workspace, "a.txt", "changed after the freeze\n");

        var copy = await harness.Service.PrepareExecutionCopyAsync(workspace, candidate, ProjectConfiguration.Empty, CancellationToken.None);

        Assert.False(copy.IsCandidateWorkspace);
        Assert.NotEqual(workspace.RootPath, copy.Path);
        Assert.Equal("candidate\n", File.ReadAllText(Path.Combine(copy.Path, "a.txt")));
        Assert.Equal("two\n", File.ReadAllText(Path.Combine(copy.Path, "sub dir", "b.txt")));
        Assert.False(File.Exists(Path.Combine(copy.Path, "gone.txt")));
        Assert.Equal(candidate.Fingerprint, copy.Fingerprint);
    }

    [Fact]
    public async Task Build_output_in_the_copy_survives_the_next_candidate_and_removed_sources_do_not()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "one\n"), ("temp.txt", "temporary\n"), (".gitignore", "bin/\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(workspace, "a.txt", "first\n");
        var first = await harness.FreezeAsync(workspace, sequence: 1);
        var copy = await harness.Service.PrepareExecutionCopyAsync(workspace, first, ProjectConfiguration.Empty, CancellationToken.None);
        Directory.CreateDirectory(Path.Combine(copy.Path, "bin"));
        File.WriteAllText(Path.Combine(copy.Path, "bin", "cache.dll"), "compiled");

        WorkspaceHarness.WriteIn(workspace, "a.txt", "second\n");
        File.Delete(WorkspaceHarness.InWorkspace(workspace, "temp.txt"));
        var second = await harness.FreezeAsync(workspace, sequence: 2);
        var again = await harness.Service.PrepareExecutionCopyAsync(workspace, second, ProjectConfiguration.Empty, CancellationToken.None);

        Assert.Equal(copy.Path, again.Path);
        Assert.Equal("second\n", File.ReadAllText(Path.Combine(again.Path, "a.txt")));
        Assert.False(File.Exists(Path.Combine(again.Path, "temp.txt")));
        Assert.Equal("compiled", File.ReadAllText(Path.Combine(again.Path, "bin", "cache.dll")));
    }

    [Fact]
    public async Task A_test_that_rewrote_a_source_file_in_the_copy_is_undone_before_the_next_check()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("snapshot.txt", "expected\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(workspace, "snapshot.txt", "candidate\n");
        var candidate = await harness.FreezeAsync(workspace);
        var copy = await harness.Service.PrepareExecutionCopyAsync(workspace, candidate, ProjectConfiguration.Empty, CancellationToken.None);
        File.WriteAllText(Path.Combine(copy.Path, "snapshot.txt"), "rewritten by a test run\n");

        var again = await harness.Service.PrepareExecutionCopyAsync(workspace, candidate, ProjectConfiguration.Empty, CancellationToken.None);

        Assert.Equal("candidate\n", File.ReadAllText(Path.Combine(again.Path, "snapshot.txt")));
        Assert.Equal("candidate\n", WorkspaceHarness.ReadIn(workspace, "snapshot.txt"));
    }

    [Fact]
    public async Task Running_in_the_candidate_workspace_is_possible_when_configured()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "one\n"));
        var configuration = ProjectConfiguration.Empty with { ValidationExecution = ProjectConfiguration.ExecutionCandidate };
        var (_, workspace) = await harness.PrepareAsync(repo.Path, configuration);
        WorkspaceHarness.WriteIn(workspace, "a.txt", "candidate\n");
        var candidate = await harness.FreezeAsync(workspace, configuration);

        var copy = await harness.Service.PrepareExecutionCopyAsync(workspace, candidate, configuration, CancellationToken.None);

        Assert.True(copy.IsCandidateWorkspace);
        Assert.Equal(workspace.RootPath, copy.Path);
    }

    [Fact]
    public async Task The_baseline_copy_holds_the_state_before_the_task()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "committed\n"));
        repo.Write("a.txt", "user's uncommitted edit\n");
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(workspace, "a.txt", "agent\n");
        await harness.FreezeAsync(workspace);

        var baseline = await harness.Service.PrepareBaselineCopyAsync(workspace, ProjectConfiguration.Empty, CancellationToken.None);

        Assert.Equal("user's uncommitted edit\n", File.ReadAllText(Path.Combine(baseline.Path, "a.txt")));
        Assert.Equal(workspace.BaselineFingerprint, baseline.Fingerprint);
    }
}
