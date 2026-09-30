using Yav.Core.Agents;
using Yav.Core.Profiles;

namespace Yav.Core.Runs;

/// <summary>The stage labels shown at the start of each status line.</summary>
public static class Stages
{
    public const string Prepare = "PREPARE";
    public const string CodeA = "CODE A";
    public const string Check = "CHECK";
    public const string ReviewB = "REVIEW B";
    public const string Tests = "TESTS";
    public const string Repair = "REPAIR";
    public const string Ready = "READY";
    public const string Apply = "APPLY";
    public const string Blocked = "BLOCKED";
    public const string Approval = "APPROVAL";
    public const string Stopped = "STOPPED";
    public const string Limit = "LIMIT";
    public const string Failed = "FAILED";
    public const string Done = "DONE";
    public const string Local = "LOCAL";
    public const string Queue = "QUEUE";

    public static string For(AgentRole role) => role == AgentRole.Implementer ? CodeA : ReviewB;
}

/// <summary>
/// Something that actually happened in a run. The console, the JSON output and the stored history are all
/// produced from the same events, so they cannot disagree.
/// </summary>
public abstract record RunEvent(string RunId, DateTimeOffset At);

public sealed record RunStarted(string RunId, DateTimeOffset At, string TaskId, string RequestText, RunProfile Profile, string ProfileHash, bool IsFollowUp)
    : RunEvent(RunId, At);

public sealed record StateChanged(string RunId, DateTimeOffset At, RunState From, RunState To, string? Reason) : RunEvent(RunId, At);

/// <summary>A one-line status message for a stage.</summary>
public sealed record StageNote(string RunId, DateTimeOffset At, string Stage, string Message, NoteLevel Level = NoteLevel.Info) : RunEvent(RunId, At);

public enum NoteLevel
{
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>An event from one of the agents, passed through unchanged.</summary>
public sealed record AgentActivity(string RunId, DateTimeOffset At, AgentRole Role, AgentEvent Event) : RunEvent(RunId, At);

public sealed record SettingConfirmed(string RunId, DateTimeOffset At, ProfileConfirmation Confirmation) : RunEvent(RunId, At);

public sealed record CandidateFrozen(string RunId, DateTimeOffset At, Candidate Candidate) : RunEvent(RunId, At);

public sealed record ReviewCompleted(string RunId, DateTimeOffset At, ReviewResult Review) : RunEvent(RunId, At);

public sealed record GateStarted(string RunId, DateTimeOffset At, GateDefinition Gate, string WorkingDirectory) : RunEvent(RunId, At);

public sealed record GateOutput(string RunId, DateTimeOffset At, string GateId, string Line) : RunEvent(RunId, At);

public sealed record GateCompleted(string RunId, DateTimeOffset At, GateResult Result) : RunEvent(RunId, At);

public sealed record AcceptanceEvaluated(string RunId, DateTimeOffset At, Candidate Candidate, AcceptanceDecision Decision) : RunEvent(RunId, At);

public sealed record RepairStarted(string RunId, DateTimeOffset At, int Cycle, int MaxCycles, int Findings, int FailedGates) : RunEvent(RunId, At);

public sealed record RunFinished(string RunId, DateTimeOffset At, RunState State, RunDisposition Disposition, string? Reason, string? FinalMessage)
    : RunEvent(RunId, At);

/// <summary>Receives run events. Implementations must return quickly and never throw.</summary>
public interface IRunObserver
{
    void OnEvent(RunEvent runEvent);
}

/// <summary>Answers approval requests. The interactive console asks the user; non-interactive runs never grant access.</summary>
public interface IApprovalBroker
{
    /// <summary>False when nobody can answer, in which case a request ends the run with Approval Required.</summary>
    bool CanAsk { get; }

    Task<ApprovalDecision> AskAsync(string runId, AgentRole role, ApprovalRequest request, CancellationToken cancellationToken);
}

public enum RunOutcomeKind
{
    ReadyToApply,
    Completed,
    Blocked,
    Interrupted,
    RateLimited,
    Failed,
    ApprovalRequired,
    NeedsReconciliation,
}

public sealed record RunOutcome(
    string RunId,
    RunOutcomeKind Kind,
    RunState State,
    string? Reason,
    Candidate? Candidate,
    AcceptanceDecision? Decision,
    string? FinalMessage,
    IReadOnlyList<ApprovalRequest> PendingApprovals)
{
    /// <summary>What kept the run from starting, when it was blocked before any work was done.</summary>
    public IReadOnlyList<ProfileProblem> Problems { get; init; } = [];

    /// <summary>The task the run belongs to. A follow-up names it to continue in the same workspace and sessions.</summary>
    public string? TaskId { get; init; }
}
