namespace Yav.Bench;

/// <summary>What one run of one step of a task gave, for one arm of the comparison.</summary>
public sealed record StepResult
{
    public required string Arm { get; init; }

    public required string TaskId { get; init; }

    /// <summary>1 for the request of the task, 2 for its follow-up.</summary>
    public int Step { get; init; } = 1;

    public int Repetition { get; init; } = 1;

    /// <summary>"fixture" when the agents were the scripted stand-in, "live" when they were the real ones.</summary>
    public string Mode { get; init; } = "fixture";

    /// <summary>How the run ended, in the words of what ran it.</summary>
    public required string Outcome { get; init; }

    /// <summary>
    /// True when every acceptance check of the step passes in the project after the run. Decided by
    /// the benchmark itself, the same way for every arm, and never by what ran the task.
    /// </summary>
    public required bool Succeeded { get; init; }

    /// <summary>Checks that passed before the run and do not pass after it.</summary>
    public IReadOnlyList<string> Regressions { get; init; } = [];

    public IReadOnlyList<string> FailedChecks { get; init; } = [];

    /// <summary>From the start of the run to its end, including everything: preparation, agents, checks, review, apply.</summary>
    public required long ElapsedMs { get; init; }

    /// <summary>Elapsed time without the time that was spent waiting for an approval. Null when it was not measured.</summary>
    public long? ActiveMs { get; init; }

    public long? ApprovalWaitingMs { get; init; }

    /// <summary>Until an agent did something for the first time. Null when it was not measured.</summary>
    public long? FirstActionMs { get; init; }

    public int RepairCycles { get; init; }

    /// <summary>Null when a role did not report its usage. Never zero for "not reported".</summary>
    public long? TotalTokens { get; init; }

    public long? ImplementerTokens { get; init; }

    public long? ReviewerTokens { get; init; }

    /// <summary>What the agents themselves reported as charge. Null when none of them reported one.</summary>
    public decimal? CostUsd { get; init; }

    public int Turns { get; init; }

    public int Retries { get; init; }

    public bool Warm { get; init; }

    public string? RunId { get; init; }

    public string? Note { get; init; }

    public DateTimeOffset StartedAt { get; init; }
}
