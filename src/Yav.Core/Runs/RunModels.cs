using Yav.Core.Agents;

namespace Yav.Core.Runs;

/// <summary>
/// A unit of user intent. The first request and its follow-ups share one isolated workspace,
/// one implementation session and one review session.
/// </summary>
public sealed record TaskContext(
    string TaskId,
    string ProjectPath,
    DateTimeOffset CreatedAt,
    string? WorkspaceId,
    string? ImplementerSessionId,
    string? ImplementerAdapterId,
    string? ReviewerSessionId,
    string? ReviewerAdapterId);

/// <summary>One thing the user asked for, kept exactly as typed.</summary>
public sealed record Requirement(int Version, string Text, DateTimeOffset AddedAt, IReadOnlyList<string> Attachments);

public enum RunKind
{
    /// <summary>Natural-language change that needs reasoning. Goes to Model A.</summary>
    CodingTask,

    /// <summary>An explicit, unambiguous literal replacement or formatter run performed locally.</summary>
    MechanicalEdit,
}

public sealed record RunRecord(
    string RunId,
    string TaskId,
    int Sequence,
    RunKind Kind,
    string ProjectPath,
    string RequestText,
    int AcceptanceVersion,
    string ProfileHash,
    RunState State,
    RunDisposition Disposition,
    string? StateReason,
    int RepairCyclesUsed,
    string? CurrentCandidateId,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    int OwnerProcessId,
    string YavVersion);

/// <summary>Identity that every piece of evidence is tied to. Evidence with a different binding is stale.</summary>
public sealed record EvidenceBinding(
    string CandidateFingerprint,
    int AcceptanceVersion,
    string ProfileHash,
    string EnvironmentFingerprint)
{
    public bool Matches(EvidenceBinding other, out string? difference)
    {
        if (!string.Equals(CandidateFingerprint, other.CandidateFingerprint, StringComparison.Ordinal))
        {
            difference = "the candidate changed after this evidence was produced";
            return false;
        }

        if (AcceptanceVersion != other.AcceptanceVersion)
        {
            difference = "the requirements changed after this evidence was produced";
            return false;
        }

        if (!string.Equals(ProfileHash, other.ProfileHash, StringComparison.Ordinal))
        {
            difference = "the run configuration differs from the one this evidence was produced with";
            return false;
        }

        if (!string.Equals(EnvironmentFingerprint, other.EnvironmentFingerprint, StringComparison.Ordinal))
        {
            difference = "the toolchain or environment changed after this evidence was produced";
            return false;
        }

        difference = null;
        return true;
    }
}

public enum ChangeKind
{
    Added,
    Modified,
    Deleted,
}

public sealed record ChangedFile(
    string Path,
    ChangeKind Kind,
    string? BaselineHash,
    string? CandidateHash,
    long? BaselineLength,
    long? CandidateLength,
    bool IsBinary,
    // Set when an added file has the same content as a deleted file.
    string? RenamedFrom);

public sealed record ChangeSet(IReadOnlyList<ChangedFile> Files)
{
    public static readonly ChangeSet Empty = new([]);

    public bool IsEmpty => Files.Count == 0;

    public int Added => Files.Count(f => f.Kind == ChangeKind.Added);

    public int Modified => Files.Count(f => f.Kind == ChangeKind.Modified);

    public int Deleted => Files.Count(f => f.Kind == ChangeKind.Deleted);

    public string Summary => IsEmpty ? "no changes" : $"{Files.Count} file(s): +{Added} ~{Modified} -{Deleted}";
}

/// <summary>A frozen, fingerprinted state of the isolated workspace that review and tests are run against.</summary>
public sealed record Candidate(
    string CandidateId,
    string RunId,
    int Sequence,
    string Fingerprint,
    string BaselineFingerprint,
    int AcceptanceVersion,
    string ProfileHash,
    ChangeSet Changes,
    IReadOnlyList<string> ProtectedPathsTouched,
    IReadOnlyList<string> ExistingTestsTouched,
    DateTimeOffset FrozenAt)
{
    public string ShortFingerprint => Fingerprint.Length > 12 ? Fingerprint[..12] : Fingerprint;
}

public sealed record SessionRecord(
    string TaskId,
    AgentRole Role,
    string AdapterId,
    string SessionId,
    string? Model,
    TokenCounts? CumulativeTokens,
    decimal? CumulativeCostUsd,
    DateTimeOffset UpdatedAt);

/// <summary>A user instruction waiting for the active run to finish.</summary>
public sealed record QueuedRequest(
    string QueueId,
    string ProjectPath,
    string Text,
    bool IsFollowUp,
    IReadOnlyList<string> Attachments,
    DateTimeOffset QueuedAt);

public sealed record RunLimits(
    TimeSpan? MaxElapsed,
    int MaxRepairCycles,
    int MaxQueueLength,
    long? MaxRunTokens,
    int? StopAtRateLimitPercent)
{
    public static readonly RunLimits Default = new(null, 2, 20, null, null);
}
