using System.Text.Json;
using System.Threading.Channels;

namespace Yav.Core.Agents;

public enum AgentRole
{
    /// <summary>Model A. The only AI writer.</summary>
    Implementer,

    /// <summary>Model B. Reviews in a separate conversation with a read-only source boundary.</summary>
    Reviewer,
}

public enum AdapterMaturity
{
    Stable,

    /// <summary>The provider itself labels this integration surface experimental.</summary>
    Experimental,
    Unknown,
}

/// <summary>Result of looking for an agent CLI on this machine. Detection never sends an inference request.</summary>
public sealed record AdapterDetection(
    string AdapterId,
    string Provider,
    bool Found,
    string? ExecutablePath,
    string? Version,
    AdapterMaturity Maturity,
    string? MaturityNote,
    bool VersionTested,
    string TestedVersions,
    IReadOnlyList<string> Problems)
{
    public bool Usable => Found && Problems.Count == 0;
}

public enum AccountRouteKind
{
    Unknown,

    /// <summary>The provider's consumer or team subscription plan.</summary>
    Subscription,
    ApiKey,
    CloudProvider,
    Gateway,
    NotAuthenticated,
}

public enum BillingKind
{
    Unknown,

    /// <summary>Usage counted against plan limits included in a subscription fee.</summary>
    IncludedInSubscription,

    /// <summary>Billed per token to an API account.</summary>
    PayPerToken,

    /// <summary>Drawn from purchased credits.</summary>
    Credits,

    /// <summary>Billed by a cloud provider under that provider's agreement.</summary>
    CloudProviderBilled,
}

public enum RoutePolicy
{
    /// <summary>The provider documents this route for this kind of integration.</summary>
    Allowed,

    /// <summary>YAV cannot verify entitlement; the user must acknowledge the provider terms and billing first.</summary>
    RequiresAcknowledgement,

    /// <summary>The provider does not permit this route for this integration.</summary>
    NotPermitted,
}

/// <summary>The account route an adapter would use. Never contains credentials.</summary>
public sealed record AuthStatus(
    bool Authenticated,
    AccountRouteKind Route,
    string RouteLabel,
    string? PlanType,
    string? Backend,
    BillingKind Billing,
    RoutePolicy Policy,
    string? PolicyNote,
    string Source,
    DateTimeOffset ObservedAt)
{
    /// <summary>Stable key used to record a user's acknowledgement of this exact route.</summary>
    public string RouteKey(string adapterId) => $"{adapterId}:{Route}:{Backend ?? "default"}";
}

/// <summary>A model offered by the provider for the current account, with its provider-specific effort values.</summary>
public sealed record ModelInfo(
    string Id,
    string DisplayName,
    string? Description,
    IReadOnlyList<string> SupportedEfforts,
    string? DefaultEffort,
    bool IsDefault,
    bool Hidden,
    IReadOnlyList<ServiceTierInfo> ServiceTiers,
    string? ResolvedModelId,
    string Source)
{
    /// <summary>What the provider itself says each effort value does, for the values it describes.</summary>
    public IReadOnlyDictionary<string, string>? EffortDescriptions { get; init; }
}

/// <summary>A serving tier the provider lists for a model.</summary>
/// <param name="Faster">True only when the adapter knows from provider documentation that this tier is the faster, separately billed serving option.</param>
public sealed record ServiceTierInfo(string Id, string Name, string? Description, bool Faster);

[Flags]
public enum AdapterFeatures
{
    None = 0,
    Streaming = 1 << 0,
    InteractiveApprovals = 1 << 1,
    Interrupt = 1 << 2,
    Steering = 1 << 3,
    ResumeSession = 1 << 4,
    StructuredOutput = 1 << 5,
    ModelListing = 1 << 6,
    EffortReadback = 1 << 7,
    UsageReporting = 1 << 8,
    RateLimitReporting = 1 << 9,
    ReadOnlyEnforcement = 1 << 10,
    ProviderSpeed = 1 << 11,
    MultiTurnProcess = 1 << 12,
}

public sealed record AdapterCapabilities(AdapterFeatures Features, IReadOnlyList<string> Limitations)
{
    public bool Has(AdapterFeatures feature) => (Features & feature) == feature;
}

public enum SandboxLevel
{
    /// <summary>The agent may read but not modify files or run mutating commands.</summary>
    ReadOnly,

    /// <summary>The agent may write inside the run workspace only.</summary>
    WorkspaceWrite,
}

public enum ApprovalMode
{
    /// <summary>Ask the user. Used by interactive runs for actions outside the sandbox policy.</summary>
    AskUser,

    /// <summary>Never ask. Anything that would need approval is refused by the agent's own policy.</summary>
    NeverAsk,
}

/// <summary>Everything an adapter needs to start or resume a session for one role.</summary>
public sealed record SessionRequest(
    AgentRole Role,
    string ModelId,
    string Effort,
    string WorkingDirectory,
    SandboxLevel Sandbox,
    ApprovalMode Approvals,
    string RoleInstructions,
    bool ProjectTrusted,
    string? ServiceTier,
    IReadOnlyList<string> AdditionalReadableDirectories);

/// <param name="QuotesOutput">
/// The prompt quotes what an agent or a tool wrote, which nobody checked. An agent that reads more into a
/// prompt than its text, such as a file to attach, is told to take this one as it is written.
/// </param>
public sealed record TurnRequest(
    string Prompt,
    JsonElement? OutputSchema,
    IReadOnlyList<string> AttachmentPaths,
    bool QuotesOutput = false);

/// <summary>What the provider reported as in effect for a session. Null means the provider did not report it.</summary>
public sealed record EffectiveSettings(
    string? Model,
    string? Effort,
    string? Sandbox,
    string? ApprovalPolicy,
    string? ServiceTier,
    string? WorkingDirectory,
    string? CredentialSource,
    string? AgentVersion,
    IReadOnlyList<string> InstructionSources,
    IReadOnlyList<string> Tools,
    string Source,
    // Where the effort was reported, when that is not where the rest was reported.
    string? EffortSource = null,
    // Who decides the agent's requests for access, in the agent's own words; "user" means you. Null when the agent does not say.
    string? ApprovalsReviewer = null,
    // What reaches beyond the sandbox's own limits. Null when the agent does not say.
    SandboxWidening? Widening = null,
    // The account the conversation itself says it works with. Null when the agent does not say.
    AccountSaid? Account = null);

/// <summary>
/// What an agent reports as reaching beyond its sandbox: network access, and folders outside the working
/// directory that it may write.
/// </summary>
public sealed record SandboxWidening(bool NetworkAccess, IReadOnlyList<string> AdditionalWritableRoots);

/// <summary>
/// What a conversation says about the account it works with. The route that was shown before the run was
/// read somewhere else, and a conversation can be started so that it works with another one.
/// Never a credential, and not who the user is.
/// </summary>
/// <param name="Route">The kind of route that follows from what was said. Null when none follows from it.</param>
/// <param name="Description">What was said, for the user to read.</param>
/// <param name="Source">Where it was said.</param>
/// <param name="Note">What the user has to know when it is not the route that was shown. Null when there is nothing to add.</param>
public sealed record AccountSaid(AccountRouteKind? Route, string Description, string Source, string? Note = null);

public enum ApprovalKind
{
    CommandExecution,
    FileChange,
    Permissions,
    ToolUse,
    UserInput,
}

public sealed record ApprovalRequest(
    string ApprovalId,
    ApprovalKind Kind,
    string Title,
    string? Command,
    string? WorkingDirectory,
    string? Reason,
    IReadOnlyList<string> Details,
    bool CanAcceptForSession,
    // The agent marks what it asks for as something that must not be granted by a single key that was pressed
    // by accident, such as a command it takes to be dangerous.
    bool Deliberate = false);

public enum ApprovalDecision
{
    Accept,
    AcceptForSession,

    /// <summary>Refuse this action; the agent continues the turn.</summary>
    Decline,

    /// <summary>Refuse this action and interrupt the turn.</summary>
    Cancel,
}

public enum TurnOutcome
{
    Completed,
    Interrupted,
    Failed,
    RateLimited,
    UsageLimitReached,

    /// <summary>The agent needed an approval that this run mode cannot give.</summary>
    ApprovalRequired,
}

public sealed record PermissionDenial(string Tool, string Summary);

/// <summary>Error codes of a turn that mean the same for every adapter.</summary>
public static class TurnErrorCodes
{
    /// <summary>The agent's process or connection ended while the turn was running. What it had already done is unknown.</summary>
    public const string AgentExited = "agentExited";
}

/// <summary>An agent could not be used: it answered with an error, did not answer, or an adapter declined to start it.</summary>
public class AgentException : Exception
{
    public AgentException(string message, Exception? inner = null, bool refused = false)
        : base(message, inner)
    {
        Refused = refused;
    }

    /// <summary>
    /// True when the adapter itself declined because a protection it needs could not be established.
    /// That is a decision for the user, not a failure to retry.
    /// </summary>
    public bool Refused { get; }
}

/// <summary>
/// One agent conversation. Events arrive in order on <see cref="Events"/>; the channel completes when
/// the session has ended. Implementations never block event production on a slow reader beyond the
/// channel's bounded capacity.
/// </summary>
public interface IAgentSession : IAsyncDisposable
{
    string AdapterId { get; }

    AgentRole Role { get; }

    /// <summary>The provider's own session or thread identifier, once known.</summary>
    string? SessionId { get; }

    EffectiveSettings? Effective { get; }

    ChannelReader<AgentEvent> Events { get; }

    Task StartTurnAsync(TurnRequest request, CancellationToken cancellationToken);

    Task RespondToApprovalAsync(string approvalId, ApprovalDecision decision, CancellationToken cancellationToken);

    /// <summary>Asks the provider to stop the active turn. Does not kill the process.</summary>
    Task InterruptAsync(CancellationToken cancellationToken);

    /// <summary>Adds input to the running turn. Returns false when the adapter cannot steer.</summary>
    Task<bool> SteerAsync(string text, CancellationToken cancellationToken);

    /// <summary>Provider interruption first, then bounded graceful shutdown, then forced cleanup of YAV-owned processes.</summary>
    Task ShutdownAsync(TimeSpan grace, CancellationToken cancellationToken);
}

public interface IAgentAdapter : IAsyncDisposable
{
    string Id { get; }

    string Provider { get; }

    string DisplayName { get; }

    AdapterCapabilities Capabilities { get; }

    Task<AdapterDetection> DetectAsync(CancellationToken cancellationToken);

    Task<AuthStatus> GetAuthStatusAsync(CancellationToken cancellationToken);

    /// <summary>Lists models without sending an inference request. Empty when the adapter cannot list models.</summary>
    Task<IReadOnlyList<ModelInfo>> ListModelsAsync(CancellationToken cancellationToken);

    Task<RateLimitSnapshot?> GetRateLimitsAsync(CancellationToken cancellationToken);

    Task<IAgentSession> StartSessionAsync(SessionRequest request, CancellationToken cancellationToken);

    Task<IAgentSession> ResumeSessionAsync(string sessionId, SessionRequest request, CancellationToken cancellationToken);

    /// <summary>
    /// Asks the provider what happened to a session after a crash or ambiguous timeout, without sending a turn.
    /// </summary>
    Task<SessionProbe> ProbeSessionAsync(string sessionId, CancellationToken cancellationToken);

    /// <summary>Starts the provider's own login flow in the foreground. Returns the launch description, or null when unsupported.</summary>
    LoginFlow? GetLoginFlow();
}

public enum SessionProbeState
{
    /// <summary>The adapter cannot inspect sessions without sending a turn.</summary>
    Unsupported,
    NotFound,
    Idle,
    Active,
    LastTurnCompleted,
    LastTurnInterrupted,
    LastTurnFailed,
}

public sealed record SessionProbe(SessionProbeState State, string? Detail);

/// <summary>A provider-owned login command that YAV hands the console to. YAV never sees the credentials.</summary>
public sealed record LoginFlow(string Executable, IReadOnlyList<string> Arguments, string Description, IReadOnlyList<string> Notes);
