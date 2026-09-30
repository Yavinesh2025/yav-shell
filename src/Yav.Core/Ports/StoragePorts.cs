using Yav.Core.Agents;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Core.Timing;

namespace Yav.Core.Ports;

/// <summary>One line of a run's history. Text is stored already sanitized and bounded.</summary>
public sealed record RunEventRecord(
    long Sequence,
    string RunId,
    DateTimeOffset At,
    // Stable machine-readable name, for example "state", "agent.message", "gate.result".
    string Type,
    // PREPARE, CODE A, CHECK, REVIEW B, TESTS, READY and so on.
    string Stage,
    string Summary,
    string? Detail);

public sealed record UsageRecord(
    string RunId,
    AgentRole Role,
    string AdapterId,
    string? SessionId,
    string? Model,
    TokenCounts Tokens,
    // False when a resumed session's earlier usage was unknown, so the run share could not be separated.
    bool RunShareKnown,
    decimal? ProviderCostUsd,
    ValueProvenance CostProvenance,
    int Turns,
    int Retries,
    string BillingRoute,
    string Source,
    DateTimeOffset ObservedAt);

public sealed record ApprovalRecord(
    string RunId,
    string ApprovalId,
    AgentRole Role,
    ApprovalKind Kind,
    string Title,
    string? Command,
    string Decision,
    // "user" or the policy that answered, for example "non-interactive".
    string DecidedBy,
    DateTimeOffset RequestedAt,
    DateTimeOffset DecidedAt);

public sealed record RunSummary(
    string RunId,
    string TaskId,
    int Sequence,
    string ProjectPath,
    string RequestText,
    RunState State,
    RunDisposition Disposition,
    string? StateReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? ModelA,
    string? ModelB);

public sealed record RetentionReport(int RunsRemoved, int EventsRemoved, long BytesFreed);

/// <summary>
/// Durable run state. Every method is atomic: a crash leaves either the old state or the new state, never a mixture.
/// </summary>
public interface IRunStore
{
    void SaveTask(TaskContext task);

    TaskContext? FindTask(string taskId);

    void SaveRequirement(string taskId, Requirement requirement);

    IReadOnlyList<Requirement> GetRequirements(string taskId);

    void CreateRun(RunRecord run, RunProfile profile);

    RunRecord? FindRun(string runId);

    RunProfile? GetProfile(string runId);

    /// <summary>
    /// Moves a run to a new state. Throws <see cref="InvalidRunTransitionException"/> when the transition is not
    /// allowed or when the stored state is not <paramref name="from"/>.
    /// </summary>
    RunRecord Transition(string runId, RunState from, RunState to, string? reason);

    void UpdateRun(string runId, Func<RunRecord, RunRecord> change);

    void AppendEvent(RunEventRecord record);

    IReadOnlyList<RunEventRecord> GetEvents(string runId, int limit);

    void SaveCandidate(Candidate candidate, string manifestId);

    Candidate? FindCandidate(string candidateId);

    IReadOnlyList<Candidate> GetCandidates(string runId);

    void SaveReview(ReviewResult review);

    ReviewResult? GetLatestReview(string runId, string candidateId);

    IReadOnlyList<ReviewResult> GetReviews(string runId);

    void SaveGateResult(GateResult result);

    IReadOnlyList<GateResult> GetGateResults(string runId);

    void SaveWaiver(GateWaiver waiver);

    IReadOnlyList<GateWaiver> GetWaivers(string runId);

    void ApproveProtectedPath(string runId, string candidateFingerprint, string path);

    IReadOnlyList<string> GetApprovedProtectedPaths(string runId, string candidateFingerprint);

    void SaveConfirmation(string runId, ProfileConfirmation confirmation);

    IReadOnlyList<ProfileConfirmation> GetConfirmations(string runId);

    void SaveSession(SessionRecord session);

    SessionRecord? FindSession(string taskId, AgentRole role);

    void SaveUsage(UsageRecord usage);

    IReadOnlyList<UsageRecord> GetUsage(string? runId, DateTimeOffset? since);

    void SaveSpan(TimingSpan span);

    IReadOnlyList<TimingSpan> GetSpans(string? runId);

    void SaveApproval(ApprovalRecord approval);

    IReadOnlyList<RunSummary> ListRuns(string? projectPath, int limit);

    /// <summary>Runs that were active in a process that no longer exists. They are moved to Needs Reconciliation.</summary>
    IReadOnlyList<RunRecord> MarkOrphanedRuns(Func<int, bool> processIsAlive);

    void Enqueue(QueuedRequest request);

    IReadOnlyList<QueuedRequest> GetQueue(string projectPath);

    bool RemoveFromQueue(string queueId);

    int ClearQueue(string projectPath);

    RetentionReport ApplyRetention(TimeSpan keepFor, DateTimeOffset now);

    /// <summary>Deletes everything stored for one run. Isolated workspace data is removed by the workspace service.</summary>
    bool DeleteRun(string runId);
}
