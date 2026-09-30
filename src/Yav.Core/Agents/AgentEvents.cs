using System.Text.Json;

namespace Yav.Core.Agents;

/// <summary>
/// Normalized events from any adapter. Only things that actually happened are represented:
/// there are no progress percentages and no synthesized reasoning text.
/// </summary>
public abstract record AgentEvent(DateTimeOffset At);

public sealed record SessionConfigured(DateTimeOffset At, string SessionId, EffectiveSettings Effective) : AgentEvent(At);

public sealed record TurnStarted(DateTimeOffset At, string TurnId) : AgentEvent(At);

public sealed record AssistantTextDelta(DateTimeOffset At, string ItemId, string Text) : AgentEvent(At);

public enum MessagePhase
{
    Unknown,
    Commentary,
    FinalAnswer,
}

public sealed record AssistantMessage(DateTimeOffset At, string ItemId, string Text, MessagePhase Phase) : AgentEvent(At);

/// <summary>A reasoning summary the provider chose to expose. Hidden reasoning is never requested.</summary>
public sealed record ReasoningSummary(DateTimeOffset At, string ItemId, string Text) : AgentEvent(At);

public sealed record CommandStarted(DateTimeOffset At, string ItemId, string Command, string? WorkingDirectory) : AgentEvent(At);

public sealed record CommandOutputDelta(DateTimeOffset At, string ItemId, string Text) : AgentEvent(At);

public sealed record CommandCompleted(DateTimeOffset At, string ItemId, string Command, int? ExitCode, long? DurationMs, string Status, string? Output)
    : AgentEvent(At);

public enum FileChangeKind
{
    Add,
    Update,
    Delete,
    Move,
}

public sealed record FileChange(string Path, FileChangeKind Kind, string? MovedTo);

public sealed record FilesChanged(DateTimeOffset At, string ItemId, IReadOnlyList<FileChange> Changes, string Status) : AgentEvent(At);

/// <param name="Summary">What the tool was given, in a few words: a pattern, a question, an address.</param>
/// <param name="Path">The file or directory the tool was pointed at, as the agent named it. Null when it named none.</param>
public sealed record ToolActivity(DateTimeOffset At, string ItemId, string Tool, string Summary, string Status, string? Path = null) : AgentEvent(At)
{
    /// <summary>What the tool was given, on one line: what it looks for, and where.</summary>
    public string Given => (Summary.Length > 0, string.IsNullOrEmpty(Path)) switch
    {
        (true, false) => $"{Summary} in {Path}",
        (true, true) => Summary,
        (false, false) => Path!,
        _ => string.Empty,
    };
}

public sealed record ApprovalRequested(DateTimeOffset At, ApprovalRequest Request) : AgentEvent(At);

public sealed record ApprovalWithdrawn(DateTimeOffset At, string ApprovalId) : AgentEvent(At);

public sealed record UsageUpdated(DateTimeOffset At, UsageSnapshot Usage) : AgentEvent(At);

public sealed record RateLimitUpdated(DateTimeOffset At, RateLimitSnapshot Snapshot) : AgentEvent(At);

public sealed record ModelRerouted(DateTimeOffset At, string FromModel, string ToModel, string Reason) : AgentEvent(At);

public sealed record ProviderRetry(DateTimeOffset At, int Attempt, int? MaxAttempts, long? DelayMs, string Reason) : AgentEvent(At);

public sealed record AgentNotice(DateTimeOffset At, string Message, bool IsWarning) : AgentEvent(At);

public sealed record AgentError(DateTimeOffset At, string Message, string? Code, bool WillRetry) : AgentEvent(At);

public sealed record TurnCompleted(
    DateTimeOffset At,
    string TurnId,
    TurnOutcome Outcome,
    string? FinalMessage,
    JsonElement? StructuredOutput,
    string? ErrorCode,
    string? ErrorMessage,
    IReadOnlyList<PermissionDenial> Denials) : AgentEvent(At);

/// <summary>The agent process or connection ended. No further events follow.</summary>
public sealed record SessionEnded(DateTimeOffset At, int? ExitCode, string? Reason) : AgentEvent(At);
