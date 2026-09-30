using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Core.Settings;

namespace Yav.Coordinator;

/// <summary>What the user has configured at the moment a run starts. A run never sees later changes.</summary>
public sealed record RunConfiguration(
    RoleSelection? ModelA,
    RoleSelection? ModelB,
    QualityPolicy Policy,
    ProviderSpeedMode Speed,
    RunLimits Limits)
{
    public static RunConfiguration From(AppSettings settings) => new(
        settings.ModelA,
        settings.ModelB,
        settings.ToPolicy(),
        settings.Speed,
        new RunLimits(
            MaxElapsed: settings.Limits.MaxElapsedMinutes > 0 ? TimeSpan.FromMinutes(settings.Limits.MaxElapsedMinutes) : null,
            MaxRepairCycles: Math.Clamp(settings.Limits.MaxRepairCycles, 0, 10),
            MaxQueueLength: Math.Max(0, settings.Limits.MaxQueueLength),
            MaxRunTokens: settings.Limits.MaxRunTokens > 0 ? settings.Limits.MaxRunTokens : null,
            StopAtRateLimitPercent: settings.Limits.StopAtRateLimitPercent > 0 ? settings.Limits.StopAtRateLimitPercent : null));
}

public sealed record RunRequest(string ProjectPath, string Text)
{
    /// <summary>Files the user attached with /attach. They are named to the implementer, never pasted into the prompt.</summary>
    public IReadOnlyList<string> Attachments { get; init; } = [];

    /// <summary>The task this request continues, or null for a new, unrelated task.</summary>
    public string? TaskId { get; init; }

    /// <summary>Set for an explicit, unambiguous local edit. The candidate is then produced without Model A.</summary>
    public MechanicalEditRequest? MechanicalEdit { get; init; }

    /// <summary>Null chooses a worktree for a Git repository with a commit and a protected copy otherwise. In-place is never chosen automatically.</summary>
    public WorkspaceMode? Mode { get; init; }

    /// <summary>True when the user accepted, for this request, that the isolated workspace is not an equivalent reproduction.</summary>
    public bool EquivalenceGapsAcknowledged { get; init; }

    /// <summary>
    /// The effort the user approved for Model A for the task of this request, in Adaptive mode. Null uses
    /// the effort that was chosen for the role.
    /// </summary>
    public string? ImplementerEffort { get; init; }
}

public sealed record CoordinatorServices(
    IReadOnlyDictionary<string, IAgentAdapter> Adapters,
    IWorkspaceService Workspaces,
    IValidationService Validation,
    IRunStore Store,
    IProjectTrustStore Trust,
    IJournalStore Journals,
    TimeProvider Clock,
    string YavVersion);

public sealed class CoordinatorOptions
{
    /// <summary>How long a turn may take to end after the provider was asked to interrupt it.</summary>
    public TimeSpan InterruptGrace { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>How long an agent process may take to exit on its own before its process tree is ended.</summary>
    public TimeSpan ShutdownGrace { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Upper bound of the diff text placed in the review request. The complete diff is always available as a file.</summary>
    public int ReviewDiffCharacters { get; init; } = 150_000;

    /// <summary>How long a reading of an agent's account and models is used before it is read again.</summary>
    public TimeSpan SnapshotMaxAge { get; init; } = TimeSpan.FromMinutes(10);

    /// <summary>How often checks are repeated on their own for one candidate when their evidence turned out unusable.</summary>
    public int MaxAutomaticRechecks { get; init; } = 1;

    public int OwnerProcessId { get; init; } = Environment.ProcessId;

    /// <summary>Tells whether the process that owned a run still exists. Used to find runs a crash left behind.</summary>
    public Func<int, bool>? ProcessIsAlive { get; init; }
}

/// <summary>Everything that is known before a run starts. Producing it never sends an inference request.</summary>
public sealed record Preflight(
    string ProjectPath,
    ProfileResolution Resolution,
    WorkspaceInspection? Inspection,
    ProjectConfigurationState Configuration,
    WorkspaceMode Mode,
    // Problems of the run configuration, the project and the workspace, in one list.
    IReadOnlyList<ProfileProblem> Problems)
{
    public bool CanRun => Resolution.Profile is not null && !Problems.Any(p => p.Severity == ProblemSeverity.Blocking);

    public IEnumerable<ProfileProblem> Blocking => Problems.Where(p => p.Severity == ProblemSeverity.Blocking);
}

public sealed record DeliveryOutcome(
    bool Succeeded,
    string Message,
    RunState State,
    RunDisposition Disposition,
    IReadOnlyList<ApplyConflict> Conflicts,
    ApplyJournal? Journal,
    AcceptanceDecision? Decision)
{
    /// <summary>True when the conflicts are concurrent edits that can be merged in isolation and checked again.</summary>
    public bool MergePossible => Conflicts.Count > 0 && Conflicts.All(c => c.Kind == ApplyConflictKind.ConcurrentEdit);
}

public sealed record UndoOutcome(bool Succeeded, string Message, string? RunId, IReadOnlyList<string> Restored, IReadOnlyList<ApplyConflict> Conflicts);

public enum ReconciliationAdvice
{
    /// <summary>The workspace holds changes that were never checked. They can be checked without sending anything to Model A.</summary>
    CheckWorkspace,

    /// <summary>The last candidate is intact; its checks can be run again.</summary>
    Recheck,

    /// <summary>The implementer's turn did not finish. Continuing sends a continuation to the same session.</summary>
    ContinueImplementation,

    /// <summary>The provider still reports a running turn. Wait, or stop it.</summary>
    WaitForProvider,

    /// <summary>The isolated workspace no longer exists. Only discarding is possible.</summary>
    DiscardOnly,
}

public sealed record ReconciliationFinding(
    string RunId,
    string ProjectPath,
    string RequestText,
    RunState StateBefore,
    ReconciliationAdvice Advice,
    IReadOnlyList<string> Observations);

public sealed record ReconciliationReport(
    IReadOnlyList<ReconciliationFinding> Runs,
    // Applies that a crash interrupted and that were finished or rolled back.
    IReadOnlyList<ApplyJournal> Journals);
