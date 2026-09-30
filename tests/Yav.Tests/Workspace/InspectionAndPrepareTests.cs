using Yav.Core.Ports;
using Yav.Core.Runs;
using Yav.Tests.Support;
using Yav.Workspace;

namespace Yav.Tests.Workspace;

public class InspectionTests
{
    /// <summary>
    /// Counts how many of the processes that are run to their end run at the same time. An answer is held back
    /// until no process has been started for a second: what is asked side by side is then open at the same time
    /// however slow the machine is, and what is asked one after the other still is not.
    /// </summary>
    private sealed class WatchingRunner(IProcessRunner inner) : IProcessRunner
    {
        private int _running;
        private int _mostAtOnce;
        private int _started;
        private long _lastStart;

        public int MostAtOnce => Volatile.Read(ref _mostAtOnce);

        public int Started => Volatile.Read(ref _started);

        public string? Resolve(string command, string? workingDirectory = null) => inner.Resolve(command, workingDirectory);

        public IRunningProcess Start(ProcessSpec spec) => inner.Start(spec);

        public async Task<ProcessResult> RunAsync(ProcessSpec spec, CaptureOptions options, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref _started);
            Interlocked.Exchange(ref _lastStart, System.Diagnostics.Stopwatch.GetTimestamp());
            var now = Interlocked.Increment(ref _running);
            int seen;
            while ((seen = Volatile.Read(ref _mostAtOnce)) < now && Interlocked.CompareExchange(ref _mostAtOnce, now, seen) != seen)
            {
            }

            try
            {
                var result = await inner.RunAsync(spec, options, cancellationToken);
                while (System.Diagnostics.Stopwatch.GetElapsedTime(Volatile.Read(ref _lastStart)) < TimeSpan.FromSeconds(1))
                {
                    await Task.Delay(20, cancellationToken);
                }

                return result;
            }
            finally
            {
                Interlocked.Decrement(ref _running);
            }
        }

        public Task<int> RunForegroundAsync(ProcessSpec spec, CancellationToken cancellationToken) => inner.RunForegroundAsync(spec, cancellationToken);
    }

    [Fact]
    public async Task What_is_asked_about_a_repository_is_asked_side_by_side()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("src/app.cs", "class App {}\n"), ("README.md", "# App\n"));
        repo.Write("notes.txt", "not committed\n");
        var runner = new WatchingRunner(new Yav.Platform.Processes.ProcessRunner());
        var service = new WorkspaceService(harness.Paths, runner, harness.Database, harness.Clock);

        var inspection = await service.InspectAsync(repo.Path, ProjectConfiguration.Empty, CancellationToken.None);

        // The answers are what they are when one question is asked after the other.
        var expected = await harness.Service.InspectAsync(repo.Path, ProjectConfiguration.Empty, CancellationToken.None);
        Assert.Equal(expected.HeadCommit, inspection.HeadCommit);
        Assert.Equal(expected.Branch, inspection.Branch);
        Assert.Equal(expected.DirtyEntries.Select(e => e.Path), inspection.DirtyEntries.Select(e => e.Path));
        // Where the repository is has to be known first. Everything else does not wait for anything.
        Assert.True(runner.Started >= 5, $"{runner.Started} processes");
        Assert.True(runner.MostAtOnce >= runner.Started - 1, $"at most {runner.MostAtOnce} of {runner.Started} processes ran at the same time");
    }

    [Fact]
    public async Task A_clean_repository_is_reported_as_clean()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("src/app.cs", "class App {}\n"), ("README.md", "# App\n"));

        var inspection = await harness.Service.InspectAsync(repo.Path, ProjectConfiguration.Empty, CancellationToken.None);

        Assert.True(inspection.IsGitRepository);
        Assert.Equal(repo.Head, inspection.HeadCommit);
        Assert.Equal("main", inspection.Branch);
        Assert.False(inspection.IsDirty);
        Assert.Empty(inspection.Unsupported);
        Assert.Empty(inspection.EquivalenceGaps);
    }

    [Fact]
    public async Task Staged_unstaged_untracked_deleted_and_renamed_changes_are_each_recorded()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(
            ("staged.txt", "one\n"), ("unstaged.txt", "one\n"), ("both.txt", "one\n"), ("deleted.txt", "bye\n"), ("old name.txt", "rename me, this line is long enough to be matched\n"));
        repo.Write("staged.txt", "two\n");
        repo.Git("add", "staged.txt");
        repo.Write("unstaged.txt", "two\n");
        repo.Write("both.txt", "two\n");
        repo.Git("add", "both.txt");
        repo.Write("both.txt", "three\n");
        repo.Delete("deleted.txt");
        repo.Git("mv", "old name.txt", "new näme.txt");
        repo.Write("untracked dir/ünï.txt", "new\n");

        var inspection = await harness.Service.InspectAsync(repo.Path, ProjectConfiguration.Empty, CancellationToken.None);

        var entries = inspection.DirtyEntries.ToDictionary(e => e.Path);
        Assert.True(entries["staged.txt"].Staged);
        Assert.False(entries["staged.txt"].Unstaged);
        Assert.True(entries["unstaged.txt"].Unstaged);
        Assert.False(entries["unstaged.txt"].Staged);
        Assert.True(entries["both.txt"].Staged && entries["both.txt"].Unstaged);
        Assert.True(entries["deleted.txt"].Unstaged);
        Assert.Equal("old name.txt", entries["new näme.txt"].RenamedFrom);
        Assert.True(entries["untracked dir/ünï.txt"].Untracked);
        Assert.True(inspection.HasStagedChanges);
    }

    [Fact]
    public async Task A_directory_that_is_not_a_repository_is_reported_as_such()
    {
        using var harness = new WorkspaceHarness();
        using var project = new TempDirectory("plain");
        project.Write("main.py", "print('hi')\n");

        var inspection = await harness.Service.InspectAsync(project.Path, ProjectConfiguration.Empty, CancellationToken.None);

        Assert.False(inspection.IsGitRepository);
        Assert.Null(inspection.HeadCommit);
        Assert.Empty(inspection.DirtyEntries);
    }

    [Fact]
    public async Task Submodules_and_large_file_storage_are_detected_as_equivalence_gaps()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(
            ("src/app.cs", "class App {}\n"),
            (".gitattributes", "*.psd filter=lfs diff=lfs merge=lfs -text\n"),
            (".gitmodules", "[submodule \"lib\"]\n\tpath = lib\n\turl = https://example.invalid/lib.git\n"));

        var inspection = await harness.Service.InspectAsync(repo.Path, ProjectConfiguration.Empty, CancellationToken.None);

        Assert.True(inspection.UsesLfs);
        Assert.True(inspection.HasSubmodules);
        Assert.Contains(inspection.EquivalenceGaps, g => g.Contains("submodule", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Ignored_files_are_listed_and_likely_secrets_are_marked()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("src/app.cs", "class App {}\n"), (".gitignore", ".env\nnode_modules/\nlocal.settings.json\n"));
        repo.Write(".env", "API_KEY=secret\n");
        repo.Write("node_modules/pkg/index.js", "module.exports = 1;\n");
        repo.Write("local.settings.json", "{}\n");

        var inspection = await harness.Service.InspectAsync(repo.Path, ProjectConfiguration.Empty, CancellationToken.None);

        var assets = inspection.IgnoredAssets.ToDictionary(a => a.Path.TrimEnd('/'));
        Assert.True(assets[".env"].LooksLikeSecret);
        Assert.False(assets["local.settings.json"].LooksLikeSecret);
        Assert.True(assets["node_modules"].IsDirectory);
        Assert.Contains(inspection.EquivalenceGaps, g => g.Contains("ignored", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Unresolved_merge_conflicts_make_isolation_unsupported()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "base\n"));
        repo.Git("checkout", "--quiet", "-b", "feature");
        repo.Write("a.txt", "feature\n");
        repo.CommitAll("feature");
        repo.Git("checkout", "--quiet", "main");
        repo.Write("a.txt", "main\n");
        repo.CommitAll("main");
        try
        {
            repo.Git("merge", "--no-edit", "feature");
        }
        catch (InvalidOperationException)
        {
            // The merge is expected to stop with a conflict.
        }

        var inspection = await harness.Service.InspectAsync(repo.Path, ProjectConfiguration.Empty, CancellationToken.None);

        Assert.Contains(inspection.Unsupported, u => u.Contains("conflict", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task Inspecting_does_not_change_the_repository()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "one\n"), ("touched.txt", "same content\n"));
        repo.Write("a.txt", "two\n");
        repo.Git("add", "a.txt");
        repo.Write("b.txt", "untracked\n");

        var before = repo.Snapshot();

        // Same content, different time stamp: this is when "git status" normally refreshes and rewrites the index.
        // The time is changed after the snapshot, because taking the snapshot runs "git status" itself.
        File.SetLastWriteTimeUtc(repo.File("touched.txt"), DateTime.UtcNow.AddMinutes(-90));
        var indexBefore = File.ReadAllBytes(Path.Combine(repo.Path, ".git", "index"));

        await harness.Service.InspectAsync(repo.Path, ProjectConfiguration.Empty, CancellationToken.None);

        Assert.Equal(indexBefore, File.ReadAllBytes(Path.Combine(repo.Path, ".git", "index")));
        Assert.Equal(before, repo.Snapshot());
    }
}

public class PrepareTests
{
    [Fact]
    public async Task The_isolated_workspace_is_outside_the_project_and_inside_yav_storage()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("src/app.cs", "class App {}\n"));

        var (_, workspace) = await harness.PrepareAsync(repo.Path);

        Assert.Equal(WorkspaceMode.GitWorktree, workspace.Mode);
        Assert.StartsWith(harness.Paths.Workspaces, workspace.RootPath, StringComparison.OrdinalIgnoreCase);
        Assert.False(workspace.RootPath.StartsWith(repo.Path, StringComparison.OrdinalIgnoreCase));
        Assert.Equal("class App {}\n", WorkspaceHarness.ReadIn(workspace, "src/app.cs"));
        Assert.Equal(repo.Head, workspace.BaseCommit);
    }

    [Fact]
    public async Task Preparing_leaves_the_original_exactly_as_it_was()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "one\n"), ("b.txt", "one\n"));
        repo.Write("a.txt", "staged\n");
        repo.Git("add", "a.txt");
        repo.Write("b.txt", "unstaged\n");
        repo.Write("new.txt", "untracked\n");
        var statusBefore = repo.Git("status", "--porcelain=v2", "--untracked-files=all");
        var treeBefore = repo.WorkingTree();
        var indexBefore = File.ReadAllBytes(Path.Combine(repo.Path, ".git", "index"));
        var refsBefore = repo.Git("for-each-ref");

        await harness.PrepareAsync(repo.Path);

        Assert.Equal(statusBefore, repo.Git("status", "--porcelain=v2", "--untracked-files=all"));
        Assert.Equal(treeBefore, repo.WorkingTree());
        Assert.Equal(indexBefore, File.ReadAllBytes(Path.Combine(repo.Path, ".git", "index")));
        Assert.Equal(refsBefore, repo.Git("for-each-ref"));
        Assert.Equal(string.Empty, repo.Git("stash", "list"));
    }

    [Fact]
    public async Task Uncommitted_work_is_carried_into_the_isolated_workspace()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(
            ("modified.txt", "old\n"), ("deleted.txt", "bye\n"), ("old name.txt", "rename me, this line is long enough to be matched\n"), ("untouched.txt", "same\n"));
        repo.Write("modified.txt", "new ünï 日本\r\nsecond line\r\n");
        repo.Delete("deleted.txt");
        repo.Git("mv", "old name.txt", "new näme.txt");
        repo.Write("untracked dir/with space/file.txt", "untracked\n");
        repo.WriteBytes("image.bin", [0, 1, 2, 3, 255, 0, 13, 10]);

        var (_, workspace) = await harness.PrepareAsync(repo.Path);

        Assert.Equal(repo.WorkingTree(), GitRepo.ReadTree(workspace.RootPath));
        Assert.False(File.Exists(WorkspaceHarness.InWorkspace(workspace, "deleted.txt")));
        Assert.False(File.Exists(WorkspaceHarness.InWorkspace(workspace, "old name.txt")));
        Assert.Equal("new ünï 日本\r\nsecond line\r\n", WorkspaceHarness.ReadIn(workspace, "modified.txt"));
    }

    [Fact]
    public async Task The_baseline_is_the_state_right_after_preparing()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "one\n"));
        repo.Write("a.txt", "dirty\n");

        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        var now = await harness.Service.FingerprintAsync(workspace, CancellationToken.None);

        Assert.Equal(workspace.BaselineFingerprint, now);
        Assert.Equal(64, now.Length);
    }

    [Fact]
    public async Task Ignored_files_are_not_carried_over_unless_configured()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "one\n"), (".gitignore", "local.json\n.env\nbuild/\n"));
        repo.Write("local.json", "{\"port\": 1}\n");
        repo.Write(".env", "SECRET=1\n");
        repo.Write("build/out.bin", "artifact");

        var (_, plain) = await harness.PrepareAsync(repo.Path, taskId: "task-plain");
        var configured = ProjectConfiguration.Empty with { ReplicateIgnored = ["local.json", ".env"] };
        var (_, replicated) = await harness.PrepareAsync(repo.Path, configured, taskId: "task-replicate");

        Assert.False(File.Exists(WorkspaceHarness.InWorkspace(plain, "local.json")));
        Assert.Equal("{\"port\": 1}\n", WorkspaceHarness.ReadIn(replicated, "local.json"));
        Assert.False(File.Exists(WorkspaceHarness.InWorkspace(replicated, ".env")), "A likely secret was copied without explicit permission.");
        Assert.Contains(replicated.Notes, n => n.Contains(".env"));
    }

    [Fact]
    public async Task A_likely_secret_is_copied_only_when_explicitly_allowed()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "one\n"), (".gitignore", ".env\n"));
        repo.Write(".env", "SECRET=1\n");
        var configuration = ProjectConfiguration.Empty with { ReplicateIgnored = [".env"], AllowSecrets = [".env"] };

        var (_, workspace) = await harness.PrepareAsync(repo.Path, configuration);

        Assert.Equal("SECRET=1\n", WorkspaceHarness.ReadIn(workspace, ".env"));
    }

    [Fact]
    public async Task A_project_without_git_is_isolated_as_a_protected_copy()
    {
        using var harness = new WorkspaceHarness();
        using var project = new TempDirectory("plain");
        project.Write("main.py", "print('hi')\n");
        project.Write("pkg/ünï mod/util.py", "x = 1\r\n");
        project.WriteBytes("data/blob.bin", [0, 9, 8, 7]);
        var before = GitRepo.ReadTree(project.Path);

        var (_, workspace) = await harness.PrepareAsync(project.Path);

        Assert.Equal(WorkspaceMode.Snapshot, workspace.Mode);
        Assert.Equal(before, GitRepo.ReadTree(workspace.RootPath));
        Assert.Equal(before, GitRepo.ReadTree(project.Path));
        Assert.False(Directory.Exists(Path.Combine(project.Path, ".git")));
        Assert.Equal(3, workspace.FileCount);
    }

    [Fact]
    public async Task A_follow_up_reuses_the_same_workspace_and_keeps_what_the_agent_built()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "one\n"), (".gitignore", "deps/\n"));
        var (_, first) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(first, "deps/cache.bin", "installed dependency");
        WorkspaceHarness.WriteIn(first, "a.txt", "agent edit\n");

        var (_, second) = await harness.PrepareAsync(repo.Path, existingWorkspaceId: first.WorkspaceId);

        Assert.True(second.Reused);
        Assert.Equal(first.WorkspaceId, second.WorkspaceId);
        Assert.Equal(first.RootPath, second.RootPath);
        Assert.Equal("installed dependency", WorkspaceHarness.ReadIn(second, "deps/cache.bin"));
        Assert.Equal("agent edit\n", WorkspaceHarness.ReadIn(second, "a.txt"));
        Assert.Equal(first.BaselineFingerprint, second.BaselineFingerprint);
    }

    [Fact]
    public async Task A_prepared_workspace_can_be_found_again_by_its_identifier()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "one\n"));
        var (_, workspace) = await harness.PrepareAsync(repo.Path);

        var found = await harness.Service.FindAsync(workspace.WorkspaceId, CancellationToken.None);
        var missing = await harness.Service.FindAsync("ws-does-not-exist", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(workspace.RootPath, found.RootPath);
        Assert.Equal(workspace.BaselineFingerprint, found.BaselineFingerprint);
        Assert.Null(missing);
    }

    [Fact]
    public async Task Isolation_is_refused_when_the_repository_has_unresolved_conflicts()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "base\n"));
        repo.Git("checkout", "--quiet", "-b", "feature");
        repo.Write("a.txt", "feature\n");
        repo.CommitAll("feature");
        repo.Git("checkout", "--quiet", "main");
        repo.Write("a.txt", "main\n");
        repo.CommitAll("main");
        try
        {
            repo.Git("merge", "--no-edit", "feature");
        }
        catch (InvalidOperationException)
        {
        }

        await Assert.ThrowsAsync<WorkspaceUnsupportedException>(() => harness.PrepareAsync(repo.Path));
    }

    [Fact]
    public async Task Equivalence_gaps_must_be_acknowledged_before_isolating()
    {
        using var harness = new WorkspaceHarness();
        using var repo = GitRepo.WithFiles(("a.txt", "one\n"), (".gitmodules", "[submodule \"lib\"]\n\tpath = lib\n\turl = https://example.invalid/lib.git\n"));
        var inspection = await harness.Service.InspectAsync(repo.Path, ProjectConfiguration.Empty, CancellationToken.None);
        var request = new WorkspaceRequest("task-1", repo.Path, WorkspaceMode.GitWorktree, ProjectConfiguration.Empty, null, EquivalenceGapsAcknowledged: false);

        var error = await Assert.ThrowsAsync<WorkspaceUnsupportedException>(() =>
            harness.Service.PrepareAsync(request, inspection, null, CancellationToken.None));

        Assert.Contains("submodule", error.Message, StringComparison.OrdinalIgnoreCase);
    }
}
