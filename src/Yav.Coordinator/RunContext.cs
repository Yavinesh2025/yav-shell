using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Core.Timing;

namespace Yav.Coordinator;

/// <summary>Usage of one role during one run.</summary>
internal sealed class RoleUsage(string adapterId, string billingRoute, SessionUsageTracker tracker)
{
    public string AdapterId { get; } = adapterId;

    public string BillingRoute { get; } = billingRoute;

    public SessionUsageTracker Tracker { get; set; } = tracker;

    public string? SessionId { get; set; }

    public string? Model { get; set; }

    public int Turns { get; set; }

    public int Retries { get; set; }
}

/// <summary>An agent conversation that is kept open for a task's repair cycles and follow-ups.</summary>
internal sealed class SessionSlot(IAgentSession session, RoleProfile role, string workingDirectory, bool resumed)
{
    public IAgentSession Session { get; } = session;

    public RoleProfile Role { get; } = role;

    public string WorkingDirectory { get; } = workingDirectory;

    /// <summary>True when the conversation existed before this process opened it.</summary>
    public bool Resumed { get; } = resumed;

    /// <summary>Set when the agent's process or connection ended. The slot is then never used again.</summary>
    public bool Lost { get; set; }

    /// <summary>True once the settings this session reported were compared with the run profile.</summary>
    public bool Verified { get; set; }

    // The account an agent works with is fixed when its process starts: a route that changed since is a new conversation.
    public bool Matches(RoleProfile role, string workingDirectory) =>
        !Lost
        && string.Equals(Role.AdapterId, role.AdapterId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Role.ModelId, role.ModelId, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Role.RequestedEffort, role.RequestedEffort, StringComparison.OrdinalIgnoreCase)
        && string.Equals(Role.ServiceTier, role.ServiceTier, StringComparison.OrdinalIgnoreCase)
        && Role.Sandbox == role.Sandbox
        && Role.AccountRoute == role.AccountRoute
        && string.Equals(Role.AccountRouteLabel, role.AccountRouteLabel, StringComparison.Ordinal)
        && string.Equals(WorkingDirectory, workingDirectory, StringComparison.OrdinalIgnoreCase);
}

internal sealed class TaskSessions
{
    public SessionSlot? Implementer { get; set; }

    public SessionSlot? Reviewer { get; set; }

    public SessionSlot? For(AgentRole role) => role == AgentRole.Implementer ? Implementer : Reviewer;

    public void Set(AgentRole role, SessionSlot? slot)
    {
        if (role == AgentRole.Implementer)
        {
            Implementer = slot;
        }
        else
        {
            Reviewer = slot;
        }
    }
}

/// <summary>Why a run stopped where it did. Null in its place means "carry on with the next stage".</summary>
internal sealed record Halt(
    RunState State,
    RunOutcomeKind Kind,
    string? Reason,
    RunDisposition? Disposition = null,
    string? FinalMessage = null,
    AcceptanceDecision? Decision = null,
    NoteLevel Level = NoteLevel.Warning);

/// <summary>Everything about one run that changes while it is running.</summary>
internal sealed class RunContext
{
    private readonly Lock _requirementsGate = new();
    private readonly List<Requirement> _requirements = [];

    public required string RunId { get; init; }

    public required string TaskId { get; init; }

    public required RunRequest Request { get; init; }

    public required RunConfiguration Configuration { get; init; }

    public required RunProfile Profile { get; init; }

    public required string ProfileHash { get; init; }

    /// <summary>The project configuration the user approved. Never read from a file an agent could have edited.</summary>
    public required ProjectConfiguration Project { get; init; }

    public required string? GateConfigurationHash { get; init; }

    public required RunPublisher Publisher { get; init; }

    public required IApprovalBroker Approvals { get; init; }

    public required TimingRecorder Timing { get; init; }

    /// <summary>What the task has to fulfil, oldest first. It grows when the user adds a request to a running turn.</summary>
    public required IReadOnlyList<Requirement> Requirements
    {
        get
        {
            lock (_requirementsGate)
            {
                return _requirements.ToArray();
            }
        }

        init => _requirements.AddRange(value);
    }

    public required bool IsFollowUp { get; init; }

    /// <summary>True when the candidate comes from an explicit local edit, not from Model A.</summary>
    public bool LocalEdit { get; init; }

    /// <summary>False as long as Model A had no part in the candidate: a local edit that needed no repair.</summary>
    public bool ImplementerInvolved => !LocalEdit || RepairCyclesUsed > 0;

    public required long StartTimestamp { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    /// <summary>Counts the requirements. Evidence that was produced for another count is stale.</summary>
    public int AcceptanceVersion
    {
        get
        {
            lock (_requirementsGate)
            {
                return _requirements.Count;
            }
        }
    }

    /// <summary>
    /// Held while a requirement is on its way to the agent and while a candidate is frozen, so that a
    /// candidate is never checked against fewer requirements than the agent was given.
    /// </summary>
    public SemaphoreSlim RequirementsChange { get; } = new(1, 1);

    public Lock StateGate { get; } = new();

    public RunState State { get; set; } = RunState.Preparing;

    /// <summary>Implementing or Repairing: the state to return to when an approval was answered.</summary>
    public RunState WorkState { get; set; } = RunState.Implementing;

    public IsolatedWorkspace? Workspace { get; set; }

    public int RepairCyclesUsed { get; set; }

    public int CandidateSequence { get; set; }

    public Candidate? Candidate { get; set; }

    public string? ImplementerSummary { get; set; }

    public ReviewResult? LastReview { get; set; }

    /// <summary>True when the user asked for the required checks to run again, so existing results are not reused.</summary>
    public bool ForceGates { get; set; }

    public DateTimeOffset? FirstActionAt { get; set; }

    public DateTimeOffset? AcceptedAt { get; set; }

    /// <summary>The roles whose sandbox was said to reach the network, so that it is said once for each.</summary>
    public HashSet<AgentRole> NetworkAccessSaid { get; } = [];

    /// <summary>The same for a sandbox that lets a role write outside the workspace, where that does not stop the run.</summary>
    public HashSet<AgentRole> WritableOutsideSaid { get; } = [];

    /// <summary>What a provider said last about a limit.</summary>
    public RateLimitSnapshot? RateLimits { get; set; }

    /// <summary>
    /// What was said last about each limit, by agent and by the name the provider gives the limit. Providers
    /// report their limits one by one, so the one that is nearly used up need not be the one they named last.
    /// </summary>
    public Dictionary<string, RateLimitSnapshot> RateLimitsByName { get; } = new(StringComparer.Ordinal);

    public List<ProfileConfirmation> Confirmations { get; } = [];

    public Dictionary<AgentRole, RoleUsage> Usage { get; } = [];

    public List<ApprovalRequest> UnansweredApprovals { get; } = [];

    /// <summary>Results of required checks on the unmodified baseline, by gate, so each is run at most once.</summary>
    public Dictionary<string, GateResult> BaselineResults { get; } = new(StringComparer.Ordinal);

    public IsolatedWorkspace RequireWorkspace() =>
        Workspace ?? throw new InvalidOperationException("The isolated workspace has not been prepared.");

    public Requirement AddRequirement(string text, DateTimeOffset now)
    {
        lock (_requirementsGate)
        {
            var requirement = new Requirement(_requirements.Count + 1, text, now, []);
            _requirements.Add(requirement);
            return requirement;
        }
    }
}
