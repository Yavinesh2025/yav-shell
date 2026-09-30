using Yav.Core.Ports;
using Yav.Tests.Support;
using Yav.Workspace;

namespace Yav.Tests.Workspace;

public class RestoreCandidateTests
{
    private static string In(IsolatedWorkspace workspace, string relative) => WorkspaceHarness.InWorkspace(workspace, relative);

    [Fact]
    public async Task The_workspace_is_put_back_to_exactly_the_frozen_candidate()
    {
        using var repo = GitRepo.WithFiles(("src/a.txt", "one\n"), ("src/b.txt", "two\n"), ("src/c.txt", "three\n"));
        using var harness = new WorkspaceHarness();
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(workspace, "src/a.txt", "changed by the task\n");
        File.Delete(In(workspace, "src/b.txt"));
        WorkspaceHarness.WriteIn(workspace, "src/new ünï.txt", "added by the task\n");
        var candidate = await harness.FreezeAsync(workspace);

        // Afterwards something else writes into the workspace.
        WorkspaceHarness.WriteIn(workspace, "src/a.txt", "tampered\n");
        WorkspaceHarness.WriteIn(workspace, "src/b.txt", "resurrected\n");
        File.Delete(In(workspace, "src/new ünï.txt"));
        File.Delete(In(workspace, "src/c.txt"));
        WorkspaceHarness.WriteIn(workspace, "src/stray.txt", "stray\n");

        var restored = await harness.Service.RestoreCandidateAsync(workspace, candidate, CancellationToken.None);

        Assert.Equal(candidate.Fingerprint, await harness.Service.FingerprintAsync(workspace, CancellationToken.None));
        Assert.Equal("changed by the task\n", WorkspaceHarness.ReadIn(workspace, "src/a.txt"));
        Assert.False(File.Exists(In(workspace, "src/b.txt")));
        Assert.Equal("added by the task\n", WorkspaceHarness.ReadIn(workspace, "src/new ünï.txt"));
        Assert.Equal("three\n", WorkspaceHarness.ReadIn(workspace, "src/c.txt"));
        Assert.False(File.Exists(In(workspace, "src/stray.txt")));
        Assert.Equal(["src/a.txt", "src/b.txt", "src/c.txt", "src/new ünï.txt", "src/stray.txt"], restored.Order(StringComparer.Ordinal).ToArray());
    }

    [Fact]
    public async Task A_workspace_that_still_holds_the_candidate_is_left_alone()
    {
        using var repo = GitRepo.WithFiles(("src/a.txt", "one\n"));
        using var harness = new WorkspaceHarness();
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(workspace, "src/a.txt", "changed by the task\n");
        var candidate = await harness.FreezeAsync(workspace);
        var written = File.GetLastWriteTimeUtc(In(workspace, "src/a.txt"));

        var restored = await harness.Service.RestoreCandidateAsync(workspace, candidate, CancellationToken.None);

        Assert.Empty(restored);
        Assert.Equal(written, File.GetLastWriteTimeUtc(In(workspace, "src/a.txt")));
    }

    [Fact]
    public async Task Putting_a_candidate_back_never_touches_the_project()
    {
        using var repo = GitRepo.WithFiles(("src/a.txt", "one\n"));
        using var harness = new WorkspaceHarness();
        var (_, workspace) = await harness.PrepareAsync(repo.Path);
        WorkspaceHarness.WriteIn(workspace, "src/a.txt", "changed by the task\n");
        var candidate = await harness.FreezeAsync(workspace);
        WorkspaceHarness.WriteIn(workspace, "src/a.txt", "tampered\n");
        var before = repo.Snapshot();
        var tree = repo.WorkingTree();

        await harness.Service.RestoreCandidateAsync(workspace, candidate, CancellationToken.None);

        Assert.Equal(before, repo.Snapshot());
        Assert.Equal(tree, repo.WorkingTree());
    }

    [Fact]
    public async Task A_run_that_works_in_the_project_itself_is_never_rewritten_this_way()
    {
        using var directory = new TempDirectory("inplace");
        directory.Write("a.txt", "one\n");
        using var harness = new WorkspaceHarness();
        var inspection = await harness.Service.InspectAsync(directory.Path, Yav.Core.Runs.ProjectConfiguration.Empty, CancellationToken.None);
        var workspace = await harness.Service.PrepareAsync(
            new WorkspaceRequest("task-1", directory.Path, WorkspaceMode.InPlace, Yav.Core.Runs.ProjectConfiguration.Empty, null, true),
            inspection, null, CancellationToken.None);
        directory.Write("a.txt", "changed by the task\n");
        var candidate = await harness.FreezeAsync(workspace);
        directory.Write("a.txt", "edited by the user\n");

        await Assert.ThrowsAsync<WorkspaceUnsupportedException>(() =>
            harness.Service.RestoreCandidateAsync(workspace, candidate, CancellationToken.None));

        Assert.Equal("edited by the user\n", directory.Read("a.txt"));
    }
}
