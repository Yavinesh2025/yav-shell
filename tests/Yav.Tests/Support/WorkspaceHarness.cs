using Microsoft.Data.Sqlite;
using Yav.Core.Ports;
using Yav.Core.Runs;
using Yav.Core.Settings;
using Yav.Platform.Processes;
using Yav.Storage;
using Yav.Workspace;

namespace Yav.Tests.Support;

/// <summary>A workspace service with its own storage directory and database, torn down after the test.</summary>
public sealed class WorkspaceHarness : IDisposable
{
    private readonly TempDirectory _home = new("home");

    public WorkspaceHarness()
    {
        Paths = new YavPaths(_home.Path);
        Paths.EnsureCreated();
        Database = YavDatabase.Open(Paths.Database, Clock);
        Service = new WorkspaceService(Paths, new ProcessRunner(), Database, Clock);
    }

    public ManualClock Clock { get; } = new();

    public YavPaths Paths { get; }

    public YavDatabase Database { get; }

    public WorkspaceService Service { get; }

    public async Task<(WorkspaceInspection Inspection, IsolatedWorkspace Workspace)> PrepareAsync(
        string projectPath,
        ProjectConfiguration? configuration = null,
        string taskId = "task-1",
        string? existingWorkspaceId = null)
    {
        var effective = configuration ?? ProjectConfiguration.Empty;
        var inspection = await Service.InspectAsync(projectPath, effective, CancellationToken.None);
        var mode = inspection.IsGitRepository && inspection.HeadCommit is not null ? WorkspaceMode.GitWorktree : WorkspaceMode.Snapshot;
        var workspace = await Service.PrepareAsync(
            new WorkspaceRequest(taskId, projectPath, mode, effective, existingWorkspaceId, EquivalenceGapsAcknowledged: true),
            inspection,
            progress: null,
            CancellationToken.None);
        return (inspection, workspace);
    }

    /// <summary>Freezes the workspace and wraps the snapshot in a candidate, as the coordinator would.</summary>
    public async Task<Candidate> FreezeAsync(IsolatedWorkspace workspace, ProjectConfiguration? configuration = null, int sequence = 1, string runId = "run-1")
    {
        var snapshot = await Service.FreezeAsync(workspace, configuration ?? ProjectConfiguration.Empty, CancellationToken.None);
        var candidate = new Candidate(
            CandidateId: $"c-{sequence}",
            RunId: runId,
            Sequence: sequence,
            Fingerprint: snapshot.Fingerprint,
            BaselineFingerprint: workspace.BaselineFingerprint,
            AcceptanceVersion: 1,
            ProfileHash: Builders.ProfileHash,
            Changes: snapshot.Changes,
            ProtectedPathsTouched: snapshot.ProtectedPathsTouched,
            ExistingTestsTouched: snapshot.ExistingTestsTouched,
            FrozenAt: Clock.GetUtcNow());
        return candidate;
    }

    /// <summary>A second service over the same storage, with its own way of touching files.</summary>
    public WorkspaceService CreateService(IFileOperations files) => new(Paths, new ProcessRunner(), Database, Clock, files);

    public static string InWorkspace(IsolatedWorkspace workspace, string relative) =>
        Path.Combine(workspace.RootPath, relative.Replace('/', Path.DirectorySeparatorChar));

    public static void WriteIn(IsolatedWorkspace workspace, string relative, string content)
    {
        var path = InWorkspace(workspace, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content, new System.Text.UTF8Encoding(false));
    }

    public static string ReadIn(IsolatedWorkspace workspace, string relative) => File.ReadAllText(InWorkspace(workspace, relative));

    public void Dispose()
    {
        Database.Dispose();
        SqliteConnection.ClearAllPools();
        _home.Dispose();
    }
}

public sealed class SimulatedCrashException() : Exception("The process died here.");

/// <summary>Behaves like the real file operations until the process "dies" in the middle of one.</summary>
public sealed class CrashingFileOperations(int crashOnOperation) : IFileOperations
{
    private readonly FileOperations _real = new();
    private int _count;

    public void Write(BlobStore store, string hash, string targetPath)
    {
        Step();
        _real.Write(store, hash, targetPath);
    }

    public void Delete(string path)
    {
        Step();
        _real.Delete(path);
    }

    private void Step()
    {
        if (++_count == crashOnOperation)
        {
            throw new SimulatedCrashException();
        }
    }
}
