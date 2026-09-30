using Yav.Core.Runs;

namespace Yav.Core.Ports;

public enum WorkspaceMode
{
    /// <summary>A detached Git worktree outside the project. Separate checkout, shared repository metadata; not a security sandbox.</summary>
    GitWorktree,

    /// <summary>A protected copy of a project that is not a Git repository.</summary>
    Snapshot,

    /// <summary>The agent works directly in the project. Weaker protection; requires explicit acknowledgement.</summary>
    InPlace,
}

public sealed record DirtyEntry(string Path, string Status, bool Staged, bool Unstaged, bool Untracked, string? RenamedFrom);

public sealed record IgnoredAsset(string Path, bool IsDirectory, bool LooksLikeSecret, long? SizeBytes);

/// <summary>What YAV found in the project before isolating it. Nothing in the project is modified by an inspection.</summary>
public sealed record WorkspaceInspection(
    string ProjectPath,
    bool IsGitRepository,
    string? RepositoryRoot,
    string? HeadCommit,
    string? Branch,
    IReadOnlyList<DirtyEntry> DirtyEntries,
    bool HasSubmodules,
    bool UsesLfs,
    bool LfsAvailable,
    IReadOnlyList<string> Symlinks,
    IReadOnlyList<string> CaseCollisions,
    IReadOnlyList<IgnoredAsset> IgnoredAssets,
    // Reasons an isolated workspace would not be an equivalent reproduction of the project.
    IReadOnlyList<string> EquivalenceGaps,
    IReadOnlyList<string> Warnings,
    // Reasons isolation cannot be used at all.
    IReadOnlyList<string> Unsupported)
{
    public bool HasStagedChanges => DirtyEntries.Any(e => e.Staged);

    public bool IsDirty => DirtyEntries.Count > 0;
}

public sealed record WorkspaceRequest(
    string TaskId,
    string ProjectPath,
    WorkspaceMode Mode,
    ProjectConfiguration Configuration,
    // Identifier of the workspace to reuse for a follow-up, or null to create a new one.
    string? ExistingWorkspaceId,
    // True when the user accepted that the isolated workspace is not an equivalent reproduction.
    bool EquivalenceGapsAcknowledged);

public sealed record IsolatedWorkspace(
    string WorkspaceId,
    string TaskId,
    WorkspaceMode Mode,
    // The directory the user opened.
    string OriginalPath,
    // The repository root when the project is inside a Git repository; otherwise the same as OriginalPath.
    string OriginalRoot,
    // Root of the isolated copy. Changes are tracked for everything below it.
    string RootPath,
    // Directory the agent starts in: the counterpart of OriginalPath inside the isolated copy.
    string AgentDirectory,
    string MetadataPath,
    string EvidencePath,
    string BaselineId,
    string BaselineFingerprint,
    string? BaseCommit,
    int FileCount,
    bool Reused,
    IReadOnlyList<string> Notes);

public sealed record CandidateSnapshot(
    string Fingerprint,
    string ManifestId,
    ChangeSet Changes,
    IReadOnlyList<string> ProtectedPathsTouched,
    IReadOnlyList<string> ExistingTestsTouched,
    int FileCount,
    TimeSpan Duration);

public sealed record DiffResult(string Text, bool Truncated, string? FullDiffPath, int FileCount, IReadOnlyList<string> BinaryFiles);

public sealed record ExecutionCopy(string Path, bool IsCandidateWorkspace, string Fingerprint, IReadOnlyList<string> Notes);

public enum ApplyConflictKind
{
    /// <summary>The user edited the file after the run's baseline was taken.</summary>
    ConcurrentEdit,

    /// <summary>The candidate adds a file that now exists in the project with different content.</summary>
    AlreadyExists,

    /// <summary>The candidate changes or deletes a file that no longer exists in the project.</summary>
    Missing,
    Locked,
    UnsafePath,
}

public sealed record ApplyConflict(string Path, ApplyConflictKind Kind, string Detail, bool AutoMergePossible);

public sealed record ApplyPreflight(
    bool CanApply,
    IReadOnlyList<ChangedFile> Operations,
    IReadOnlyList<ApplyConflict> Conflicts,
    // Files in the project that changed since the baseline but are not touched by the candidate.
    IReadOnlyList<string> UnrelatedConcurrentEdits);

public enum JournalState
{
    Prepared,
    InProgress,
    Committed,
    RolledBack,
    Undone,

    /// <summary>The process stopped in the middle of an apply. The journal must be reconciled before anything else.</summary>
    Interrupted,
}

public sealed record JournalEntry(
    int Index,
    string Path,
    ChangeKind Kind,
    // Content of the project file before the apply, kept for rollback and undo. Null when the file did not exist.
    string? PreImageHash,
    // Content written by the apply. Null for a deletion.
    string? PostImageHash,
    bool Done);

public sealed record ApplyJournal(
    string JournalId,
    string RunId,
    string CandidateId,
    string CandidateFingerprint,
    string ProjectPath,
    JournalState State,
    IReadOnlyList<JournalEntry> Entries,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record ApplyResult(bool Applied, ApplyJournal? Journal, IReadOnlyList<ApplyConflict> Conflicts, string? Error, bool RolledBack);

public sealed record UndoResult(
    bool Undone,
    IReadOnlyList<string> Restored,
    IReadOnlyList<ApplyConflict> Conflicts,
    string? Error);

public sealed record MergeResult(
    bool Merged,
    // Files whose concurrent edits could not be merged automatically.
    IReadOnlyList<string> Unresolved,
    IReadOnlyList<string> MergedFiles,
    string NewBaselineFingerprint);

public sealed record MechanicalEditRequest(
    string Path,
    string ExpectedText,
    string ReplacementText,
    // The number of matches the user expects. The edit is refused when the actual count differs.
    int? ExpectedMatches,
    bool AllOccurrences);

public sealed record MechanicalEditPreview(
    bool Valid,
    string? Problem,
    int MatchCount,
    IReadOnlyList<int> MatchLines,
    string PreviewDiff);

/// <summary>Workspace protection. The original project is only ever written by <see cref="ApplyAsync"/> and <see cref="UndoAsync"/>.</summary>
public interface IWorkspaceService
{
    Task<WorkspaceInspection> InspectAsync(string projectPath, ProjectConfiguration configuration, CancellationToken cancellationToken);

    Task<IsolatedWorkspace> PrepareAsync(
        WorkspaceRequest request,
        WorkspaceInspection inspection,
        IProgress<string>? progress,
        CancellationToken cancellationToken);

    Task<IsolatedWorkspace?> FindAsync(string workspaceId, CancellationToken cancellationToken);

    /// <summary>Fingerprint of the workspace as it is right now.</summary>
    Task<string> FingerprintAsync(IsolatedWorkspace workspace, CancellationToken cancellationToken);

    /// <summary>Records the current state as an immutable candidate: manifest, fingerprint and the content of changed files.</summary>
    Task<CandidateSnapshot> FreezeAsync(IsolatedWorkspace workspace, ProjectConfiguration configuration, CancellationToken cancellationToken);

    Task<DiffResult> DiffAsync(IsolatedWorkspace workspace, Candidate candidate, int maxCharacters, CancellationToken cancellationToken);

    /// <summary>True when the workspace-relative path exists in the candidate or existed in the baseline.</summary>
    Task<Func<string, bool>> GetPathLookupAsync(IsolatedWorkspace workspace, Candidate candidate, CancellationToken cancellationToken);

    Task<ExecutionCopy> PrepareExecutionCopyAsync(
        IsolatedWorkspace workspace,
        Candidate candidate,
        ProjectConfiguration configuration,
        CancellationToken cancellationToken);

    /// <summary>A copy of the unmodified baseline, used to tell pre-existing failures from new ones.</summary>
    Task<ExecutionCopy> PrepareBaselineCopyAsync(IsolatedWorkspace workspace, ProjectConfiguration configuration, CancellationToken cancellationToken);

    Task<ApplyPreflight> PreflightApplyAsync(IsolatedWorkspace workspace, Candidate candidate, CancellationToken cancellationToken);

    Task<ApplyResult> ApplyAsync(IsolatedWorkspace workspace, Candidate candidate, string runId, CancellationToken cancellationToken);

    Task<UndoResult> UndoAsync(ApplyJournal journal, bool skipConflicts, CancellationToken cancellationToken);

    /// <summary>
    /// Brings the user's concurrent edits into the isolated workspace so that a merged candidate can be
    /// checked again. The project itself is not written.
    /// </summary>
    Task<MergeResult> MergeConcurrentEditsAsync(IsolatedWorkspace workspace, Candidate candidate, CancellationToken cancellationToken);

    /// <summary>
    /// Makes the isolated workspace hold exactly the frozen candidate again, after something other than the
    /// implementer wrote into it. Returns the paths that were put back. Never used for a run that works in the project itself.
    /// </summary>
    Task<IReadOnlyList<string>> RestoreCandidateAsync(IsolatedWorkspace workspace, Candidate candidate, CancellationToken cancellationToken);

    /// <summary>Removes isolated run data only. The project is never touched.</summary>
    Task DiscardAsync(IsolatedWorkspace workspace, CancellationToken cancellationToken);

    /// <summary>Finishes or rolls back applies that a crash interrupted. Returns the journals it handled.</summary>
    Task<IReadOnlyList<ApplyJournal>> ReconcileJournalsAsync(CancellationToken cancellationToken);

    Task<MechanicalEditPreview> PreviewMechanicalEditAsync(IsolatedWorkspace workspace, MechanicalEditRequest request, CancellationToken cancellationToken);

    Task ApplyMechanicalEditAsync(IsolatedWorkspace workspace, MechanicalEditRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Compares the shared repository's branches, tags and stash with their state before the run.
    /// Returns a description of every difference, or an empty list when nothing changed.
    /// </summary>
    Task<IReadOnlyList<string>> VerifyRepositoryUntouchedAsync(IsolatedWorkspace workspace, CancellationToken cancellationToken);
}

public interface IJournalStore
{
    void Save(ApplyJournal journal);

    ApplyJournal? Find(string journalId);

    ApplyJournal? FindLatestForProject(string projectPath);

    ApplyJournal? FindForRun(string runId);

    /// <summary>The applies that were started for a project, newest first, whatever state they ended in.</summary>
    IReadOnlyList<ApplyJournal> ListForProject(string projectPath, int limit);

    IReadOnlyList<ApplyJournal> FindUnfinished();
}
