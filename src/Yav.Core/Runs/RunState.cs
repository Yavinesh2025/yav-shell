namespace Yav.Core.Runs;

/// <summary>
/// The distinct states a run can be in. <see cref="Completed"/> is only reached after the required
/// delivery step succeeded; an agent exit code or a reassuring message never sets it.
/// </summary>
public enum RunState
{
    Preparing,
    Implementing,
    AwaitingApproval,
    Checking,
    Repairing,
    ReadyToApply,
    Completed,
    Blocked,
    Interrupted,
    RateLimited,
    Failed,
    NeedsReconciliation,
}

/// <summary>What finally happened to the changes a run produced.</summary>
public enum RunDisposition
{
    /// <summary>The candidate, if any, still lives only in the isolated workspace.</summary>
    Pending,
    Applied,
    Discarded,
    Undone,

    /// <summary>The run produced an answer but no source changes, so there was nothing to apply.</summary>
    NoChanges,
}

public static class RunStateMachine
{
    private static readonly Dictionary<RunState, RunState[]> Allowed = new()
    {
        [RunState.Preparing] =
        [
            RunState.Implementing, RunState.Checking, RunState.Blocked, RunState.Interrupted, RunState.Failed,
            RunState.NeedsReconciliation,
        ],
        [RunState.Implementing] =
        [
            RunState.AwaitingApproval, RunState.Checking, RunState.Completed, RunState.Blocked, RunState.Interrupted,
            RunState.RateLimited, RunState.Failed, RunState.NeedsReconciliation,
        ],
        [RunState.AwaitingApproval] =
        [
            RunState.Implementing, RunState.Repairing, RunState.Checking, RunState.Blocked, RunState.Interrupted,
            RunState.Failed, RunState.NeedsReconciliation,
        ],
        [RunState.Checking] =
        [
            RunState.ReadyToApply, RunState.Repairing, RunState.Blocked, RunState.Interrupted, RunState.RateLimited,
            RunState.Failed, RunState.NeedsReconciliation,
        ],
        [RunState.Repairing] =
        [
            RunState.AwaitingApproval, RunState.Checking, RunState.Blocked, RunState.Interrupted, RunState.RateLimited,
            RunState.Failed, RunState.NeedsReconciliation,
        ],
        [RunState.ReadyToApply] =
        [
            RunState.Completed, RunState.Checking, RunState.Blocked, RunState.Interrupted, RunState.NeedsReconciliation,
            RunState.Failed,
        ],
        [RunState.Blocked] =
        [
            RunState.Preparing, RunState.Implementing, RunState.Repairing, RunState.Checking, RunState.Interrupted,
            RunState.Failed, RunState.NeedsReconciliation,
        ],
        [RunState.Interrupted] =
        [
            RunState.Preparing, RunState.Implementing, RunState.Repairing, RunState.Checking, RunState.Blocked,
            RunState.Failed, RunState.NeedsReconciliation,
        ],
        [RunState.RateLimited] =
        [
            RunState.Implementing, RunState.Repairing, RunState.Checking, RunState.Blocked, RunState.Interrupted,
            RunState.Failed, RunState.NeedsReconciliation,
        ],
        [RunState.NeedsReconciliation] =
        [
            RunState.Preparing, RunState.Implementing, RunState.Repairing, RunState.Checking, RunState.ReadyToApply,
            RunState.Blocked, RunState.Interrupted, RunState.Failed,
        ],
        [RunState.Failed] = [RunState.Preparing, RunState.NeedsReconciliation],
        [RunState.Completed] = [],
    };

    public static bool CanTransition(RunState from, RunState to) =>
        Allowed.TryGetValue(from, out var targets) && Array.IndexOf(targets, to) >= 0;

    /// <summary>States in which YAV-owned processes may be doing work for the run.</summary>
    public static bool IsActive(RunState state) => state is
        RunState.Preparing or RunState.Implementing or RunState.AwaitingApproval or RunState.Checking or RunState.Repairing;

    /// <summary>States that need the user before anything else can happen.</summary>
    public static bool NeedsAttention(RunState state) => state is
        RunState.Blocked or RunState.Interrupted or RunState.RateLimited or RunState.Failed or RunState.NeedsReconciliation;

    public static string Display(RunState state) => state switch
    {
        RunState.AwaitingApproval => "Awaiting Approval",
        RunState.ReadyToApply => "Ready to Apply",
        RunState.RateLimited => "Rate Limited",
        RunState.NeedsReconciliation => "Needs Reconciliation",
        RunState.Blocked => "Blocked / Needs Attention",
        _ => state.ToString(),
    };
}

public sealed class InvalidRunTransitionException(string runId, RunState from, RunState to)
    : InvalidOperationException($"Run {runId} cannot move from {RunStateMachine.Display(from)} to {RunStateMachine.Display(to)}.")
{
    public string RunId { get; } = runId;

    public RunState From { get; } = from;

    public RunState To { get; } = to;
}
