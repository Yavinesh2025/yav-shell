using Yav.Core.Agents;
using Yav.Core.Templates;

namespace Yav.Core.Profiles;

public enum ProblemSeverity
{
    Info,
    Warning,

    /// <summary>The run must not start until this is resolved or the user explicitly changes the policy.</summary>
    Blocking,
}

public sealed record ProfileProblem(AgentRole? Role, ProblemSeverity Severity, string Code, string Message, string? Remedy);

/// <summary>What is known about one adapter at the moment a profile is resolved.</summary>
public sealed record AdapterSnapshot(
    string AdapterId,
    string Provider,
    AdapterDetection Detection,
    AuthStatus? Auth,
    IReadOnlyList<ModelInfo> Models,
    AdapterCapabilities Capabilities,
    // True when the user acknowledged the billing route and provider terms for exactly this route.
    bool RouteAcknowledged,
    // True when the user authorized the paid provider speed tier for exactly this route.
    bool PaidSpeedAuthorized);

public sealed record ProfileRequest(
    string ProjectPath,
    bool ProjectTrusted,
    QualityPolicy Policy,
    RoleSelection? Implementer,
    RoleSelection? Reviewer,
    ProviderSpeedMode Speed,
    IReadOnlyDictionary<string, AdapterSnapshot> Adapters,
    IReadOnlyList<string> ProjectInstructionFiles,
    IReadOnlyList<string> RequiredGateIds,
    string? GateConfigurationHash,
    string YavVersion,
    DateTimeOffset Now)
{
    /// <summary>
    /// The effort the user approved for Model A for this one task. It is used in Adaptive mode only, and
    /// only for Model A: the model stays the same and the review stays at the effort chosen for it.
    /// </summary>
    public string? ApprovedImplementerEffort { get; init; }
}

public sealed record ProfileResolution(RunProfile? Profile, IReadOnlyList<ProfileProblem> Problems)
{
    public bool CanRun => Profile is not null && !Problems.Any(p => p.Severity == ProblemSeverity.Blocking);

    public IEnumerable<ProfileProblem> Blocking => Problems.Where(p => p.Severity == ProblemSeverity.Blocking);
}

/// <summary>
/// Turns the user's choices into the exact provider settings for a run. Nothing is ever silently
/// substituted: a model, effort, tool set or billing route that cannot be honored produces a problem.
/// </summary>
public static class ProfileResolver
{
    /// <summary>
    /// Known effort names from lowest to highest. Effort names are provider specific; a name that is not
    /// in this list cannot be ranked, so "maximum" is then not resolved automatically.
    /// </summary>
    private static readonly string[] EffortRanking = ["none", "minimal", "low", "medium", "high", "xhigh", "max"];

    public static ProfileResolution Resolve(ProfileRequest request)
    {
        var problems = new List<ProfileProblem>();

        if (request.Implementer is null)
        {
            problems.Add(new ProfileProblem(
                AgentRole.Implementer, ProblemSeverity.Blocking, "model-a-missing",
                "No implementation model (Model A) is selected.", "Choose one with /models."));
        }

        if (request.Reviewer is null && request.Policy.RequireReview)
        {
            problems.Add(new ProfileProblem(
                AgentRole.Reviewer, ProblemSeverity.Blocking, "model-b-missing",
                "No review model (Model B) is selected.", "Choose one with /models."));
        }

        var implementerSelection = request.Implementer;
        if (request.ApprovedImplementerEffort is { } approved && implementerSelection is not null)
        {
            if (request.Policy.Adaptive)
            {
                implementerSelection = implementerSelection with { EffortPreference = approved.Trim().Length == 0 ? approved : approved.Trim() };
            }
            else
            {
                problems.Add(new ProfileProblem(
                    AgentRole.Implementer, ProblemSeverity.Blocking, "adaptive-off",
                    $"Model A: effort '{approved}' was asked for this task, but Adaptive mode is off. No other effort than the one you chose is used.",
                    "Turn Adaptive mode on with /adaptive on, or send the request without another effort."));
            }
        }

        var implementer = implementerSelection is null ? null : ResolveRole(AgentRole.Implementer, implementerSelection, request, problems);
        if (implementer is not null && request.Policy.Adaptive && request.ApprovedImplementerEffort is not null
            && implementer.EffortSupport == VerificationStatus.Verified
            && HighestEffort(implementer.SupportedEfforts) is { } highest
            && !string.Equals(highest, implementer.RequestedEffort, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(new ProfileProblem(
                AgentRole.Implementer, ProblemSeverity.Warning, "adaptive-effort",
                $"Model A works at effort '{implementer.RequestedEffort}' instead of '{highest}' for this task, as you approved. The model is the same and the review "
                + "keeps its effort. A lower effort changes how the model reasons; review and checks do not make the result the same as at the higher effort.",
                null));
        }
        var reviewerSelection = request.Reviewer ?? request.Implementer;
        var reviewer = reviewerSelection is null ? null : ResolveRole(AgentRole.Reviewer, reviewerSelection, request, problems);

        if (request.Policy.QualityLock && request.Policy.RequireReview
            && request.Implementer is not null && request.Reviewer is not null
            && string.Equals(request.Implementer.AdapterId, request.Reviewer.AdapterId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(request.Implementer.ModelId, request.Reviewer.ModelId, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(new ProfileProblem(
                null,
                request.Policy.Strict ? ProblemSeverity.Blocking : ProblemSeverity.Warning,
                "single-model",
                "Model A and Model B are the same model. A single-model configuration is not the dual-model workflow.",
                "Choose a distinct review model with /models."));
        }

        if (request.Policy.RequireGates && request.RequiredGateIds.Count == 0)
        {
            problems.Add(new ProfileProblem(
                null,
                ProblemSeverity.Blocking,
                "gates-missing",
                "No trusted validation gate is configured for this project, so a candidate could never be shown to pass required checks.",
                "A request entered in the shell (yav) asks you whether to approve the checks YAV finds or to accept the review alone for this project. "
                + "As commands: /test detect, or /quality gates optional for every project."));
        }

        if (request.Policy.Adaptive)
        {
            problems.Add(new ProfileProblem(
                null, ProblemSeverity.Info, "adaptive",
                "Adaptive mode is ON. Runs are marked Adaptive, not Strict Max; implementation effort may be lowered only with your approval per task.",
                "Turn it off with /adaptive off."));
        }

        if (implementer is null || reviewer is null)
        {
            return new ProfileResolution(null, problems);
        }

        var profile = new RunProfile(
            ProfileId: Ids.NewId("p"),
            CreatedAt: request.Now,
            ProjectPath: request.ProjectPath,
            ProjectTrusted: request.ProjectTrusted,
            Policy: request.Policy,
            Implementer: implementer,
            Reviewer: reviewer,
            ProjectInstructionFiles: request.ProjectInstructionFiles,
            RequiredGateIds: request.RequiredGateIds,
            GateConfigurationHash: request.GateConfigurationHash,
            YavVersion: request.YavVersion);

        return new ProfileResolution(profile, problems);
    }

    /// <summary>
    /// The listed efforts that rank below the given one, lowest first. Empty when a name in the list
    /// cannot be ranked, because then nothing can be said to be lower.
    /// </summary>
    public static IReadOnlyList<string> EffortsBelow(IReadOnlyList<string> supported, string effort)
    {
        static int Rank(string name) => Array.FindIndex(EffortRanking, e => string.Equals(e, name, StringComparison.OrdinalIgnoreCase));

        var limit = Rank(effort);
        if (limit < 0 || supported.Any(e => Rank(e) < 0))
        {
            return [];
        }

        return supported.Where(e => Rank(e) < limit).OrderBy(Rank).ToList();
    }

    /// <summary>The highest effort in the list, or null when the names cannot all be ranked.</summary>
    public static string? HighestEffort(IReadOnlyList<string> supported)
    {
        string? best = null;
        var bestRank = -1;
        foreach (var effort in supported)
        {
            var rank = Array.FindIndex(EffortRanking, e => string.Equals(e, effort, StringComparison.OrdinalIgnoreCase));
            if (rank < 0)
            {
                return null;
            }

            if (rank > bestRank)
            {
                bestRank = rank;
                best = effort;
            }
        }

        return best;
    }

    private static RoleProfile? ResolveRole(AgentRole role, RoleSelection selection, ProfileRequest request, List<ProfileProblem> problems)
    {
        var strict = request.Policy.QualityLock && request.Policy.Strict;
        var unverifiedSeverity = strict ? ProblemSeverity.Blocking : ProblemSeverity.Warning;
        var name = role == AgentRole.Implementer ? "Model A" : "Model B";

        if (!request.Adapters.TryGetValue(selection.AdapterId, out var adapter))
        {
            problems.Add(new ProfileProblem(
                role, ProblemSeverity.Blocking, "adapter-unknown",
                $"{name} uses adapter '{selection.AdapterId}', which is not available in this build.", "Choose another with /models."));
            return null;
        }

        if (!adapter.Detection.Found)
        {
            problems.Add(new ProfileProblem(
                role, ProblemSeverity.Blocking, "adapter-not-found",
                $"{name}: the {adapter.Provider} agent was not found on this machine.", "Run /doctor for installation details."));
            return null;
        }

        foreach (var problem in adapter.Detection.Problems)
        {
            problems.Add(new ProfileProblem(role, ProblemSeverity.Blocking, "adapter-prerequisite", $"{name}: {problem}", "Run /doctor."));
        }

        if (!adapter.Detection.VersionTested)
        {
            problems.Add(new ProfileProblem(
                role, ProblemSeverity.Warning, "adapter-version-untested",
                $"{name}: agent version {adapter.Detection.Version ?? "unknown"} is outside the tested range ({adapter.Detection.TestedVersions}).",
                null));
        }

        if (adapter.Detection.Maturity == AdapterMaturity.Experimental)
        {
            problems.Add(new ProfileProblem(
                role, ProblemSeverity.Info, "adapter-experimental",
                $"{name}: {adapter.Detection.MaturityNote ?? "the provider labels this integration experimental"}.", null));
        }

        CheckAuth(role, name, adapter, problems);

        var model = adapter.Models.FirstOrDefault(m => string.Equals(m.Id, selection.ModelId, StringComparison.OrdinalIgnoreCase))
            ?? adapter.Models.FirstOrDefault(m => string.Equals(m.ResolvedModelId, selection.ModelId, StringComparison.OrdinalIgnoreCase));

        VerificationStatus availability;
        if (model is not null)
        {
            availability = VerificationStatus.Verified;
        }
        else if (adapter.Models.Count > 0)
        {
            availability = VerificationStatus.Unsupported;
            problems.Add(new ProfileProblem(
                role, ProblemSeverity.Blocking, "model-unavailable",
                $"{name}: model '{selection.ModelId}' is not in the list the provider returned for this account. No other model is substituted.",
                "Choose an available model with /models."));
        }
        else
        {
            availability = VerificationStatus.RequestedUnverified;
            problems.Add(new ProfileProblem(
                role, unverifiedSeverity, "model-unverified",
                $"{name}: model '{selection.ModelId}' is Requested / Unverified because the available models could not be listed.",
                "Check /doctor and /login, or relax the policy with /quality strict off."));
        }

        var supported = model?.SupportedEfforts ?? [];
        var (effort, effortSupport) = ResolveEffort(role, name, selection, model, supported, unverifiedSeverity, problems);

        if (!adapter.Capabilities.Has(AdapterFeatures.EffortReadback))
        {
            // Known before the run starts, so it is said before any usage is spent on a run that could not be accepted.
            problems.Add(new ProfileProblem(
                role, unverifiedSeverity, "settings-unverifiable",
                $"{name}: this adapter does not report the model and effort in effect, so they stay Requested / Unverified for the whole run.",
                "Choose an adapter that reports its settings, or relax the policy with /quality strict off."));
        }

        if (role == AgentRole.Reviewer && !adapter.Capabilities.Has(AdapterFeatures.ReadOnlyEnforcement))
        {
            problems.Add(new ProfileProblem(
                role, ProblemSeverity.Blocking, "reviewer-not-read-only",
                $"{name}: this adapter cannot enforce a read-only source boundary with tool restrictions or a sandbox. A prompt alone is not enforcement.",
                "Choose a review adapter that supports read-only enforcement."));
        }

        if (role == AgentRole.Reviewer && !adapter.Capabilities.Has(AdapterFeatures.StructuredOutput))
        {
            problems.Add(new ProfileProblem(
                role, ProblemSeverity.Warning, "reviewer-unstructured",
                $"{name}: this adapter has no structured-output facility. The review result is still validated by YAV; invalid output is not a pass.",
                null));
        }

        string? serviceTier = null;
        var speed = ProviderSpeedMode.Standard;
        if (request.Speed == ProviderSpeedMode.Provider)
        {
            (speed, serviceTier) = ResolveSpeed(role, name, adapter, model, problems);
        }

        var template = RuntimeTemplates.For(role);
        return new RoleProfile(
            Role: role,
            AdapterId: adapter.AdapterId,
            Provider: adapter.Provider,
            AdapterVersion: adapter.Detection.Version ?? "unknown",
            AdapterMaturity: adapter.Detection.Maturity,
            ModelId: model?.Id ?? selection.ModelId,
            ModelDisplayName: model?.DisplayName,
            ResolvedModelId: model?.ResolvedModelId,
            ModelAvailability: availability,
            EffortPreference: selection.EffortPreference,
            RequestedEffort: effort,
            EffortSupport: effortSupport,
            SupportedEfforts: supported,
            Sandbox: role == AgentRole.Reviewer ? SandboxLevel.ReadOnly : SandboxLevel.WorkspaceWrite,
            Approvals: role == AgentRole.Reviewer ? ApprovalMode.NeverAsk : ApprovalMode.AskUser,
            Speed: speed,
            ServiceTier: serviceTier,
            AccountRoute: adapter.Auth?.Route ?? AccountRouteKind.Unknown,
            AccountRouteLabel: adapter.Auth?.RouteLabel ?? "Unavailable",
            Billing: adapter.Auth?.Billing ?? BillingKind.Unknown,
            InstructionTemplateId: template.Id,
            InstructionTemplateVersion: template.Version);
    }

    private static void CheckAuth(AgentRole role, string name, AdapterSnapshot adapter, List<ProfileProblem> problems)
    {
        var auth = adapter.Auth;
        if (auth is null)
        {
            problems.Add(new ProfileProblem(
                role, ProblemSeverity.Blocking, "auth-unknown",
                $"{name}: the authentication status of the {adapter.Provider} agent could not be read.", "Run /doctor."));
            return;
        }

        if (!auth.Authenticated)
        {
            problems.Add(new ProfileProblem(
                role, ProblemSeverity.Blocking, "auth-missing",
                $"{name}: the {adapter.Provider} agent is not signed in.", $"Run /login {adapter.Provider}."));
            return;
        }

        switch (auth.Policy)
        {
            case RoutePolicy.NotPermitted:
                problems.Add(new ProfileProblem(
                    role, ProblemSeverity.Blocking, "route-not-permitted",
                    $"{name}: the account route '{auth.RouteLabel}' is not permitted for this integration. {auth.PolicyNote}",
                    $"Configure a supported route with /login {adapter.Provider}."));
                break;

            case RoutePolicy.RequiresAcknowledgement when !adapter.RouteAcknowledged:
                problems.Add(new ProfileProblem(
                    role, ProblemSeverity.Blocking, "route-unacknowledged",
                    $"{name}: the account route '{auth.RouteLabel}' has not been acknowledged. {auth.PolicyNote}",
                    $"Review and acknowledge it with /login {adapter.Provider}."));
                break;
        }
    }

    private static (string Effort, VerificationStatus Support) ResolveEffort(
        AgentRole role,
        string name,
        RoleSelection selection,
        ModelInfo? model,
        IReadOnlyList<string> supported,
        ProblemSeverity unverifiedSeverity,
        List<ProfileProblem> problems)
    {
        if (model is null)
        {
            // Without the provider's list the maximum cannot be known. Nothing is invented.
            var requested = selection.WantsMaximum ? string.Empty : selection.EffortPreference;
            problems.Add(new ProfileProblem(
                role, unverifiedSeverity, "effort-unverified",
                selection.WantsMaximum
                    ? $"{name}: the maximum supported effort cannot be determined because the provider's model list is unavailable."
                    : $"{name}: effort '{requested}' is Requested / Unverified because the provider's model list is unavailable.",
                "Set an exact provider value with /effort once the model list is available."));
            return (requested, VerificationStatus.RequestedUnverified);
        }

        if (supported.Count == 0)
        {
            if (!selection.WantsMaximum)
            {
                problems.Add(new ProfileProblem(
                    role, ProblemSeverity.Blocking, "effort-unsupported",
                    $"{name}: model '{model.Id}' has no effort setting, but effort '{selection.EffortPreference}' was requested.",
                    "Use /effort to select maximum, or choose another model."));
                return (selection.EffortPreference, VerificationStatus.Unsupported);
            }

            return (string.Empty, VerificationStatus.Verified);
        }

        if (selection.WantsMaximum)
        {
            var highest = HighestEffort(supported);
            if (highest is not null)
            {
                return (highest, VerificationStatus.Verified);
            }

            // Which value is "the maximum" is then a decision, not a lookup, so it is the user's under every policy.
            problems.Add(new ProfileProblem(
                role, ProblemSeverity.Blocking, "effort-unranked",
                $"{name}: the provider lists effort values YAV cannot rank, so the maximum is not chosen for you. {DescribeEfforts(model, supported)}",
                "Set the exact value with /effort."));
            return (string.Empty, VerificationStatus.RequestedUnverified);
        }

        var match = supported.FirstOrDefault(e => string.Equals(e, selection.EffortPreference, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            problems.Add(new ProfileProblem(
                role, ProblemSeverity.Blocking, "effort-unsupported",
                $"{name}: effort '{selection.EffortPreference}' is not supported by '{model.Id}'. Supported: {string.Join(", ", supported)}. It is not lowered automatically.",
                "Choose a supported value with /effort."));
            return (selection.EffortPreference, VerificationStatus.Unsupported);
        }

        var maximum = HighestEffort(supported);
        if (maximum is not null && !string.Equals(maximum, match, StringComparison.OrdinalIgnoreCase))
        {
            problems.Add(new ProfileProblem(
                role, ProblemSeverity.Info, "effort-below-maximum",
                $"{name}: effort '{match}' is below the maximum this model supports ('{maximum}').", "Use /effort to select maximum."));
        }

        return (match, VerificationStatus.Verified);
    }

    /// <summary>The effort values with the provider's own description of each, where it gave one.</summary>
    public static string DescribeEfforts(ModelInfo model, IReadOnlyList<string> supported) =>
        "Listed: " + string.Join("; ", supported.Select(effort =>
            model.EffortDescriptions is not null && model.EffortDescriptions.TryGetValue(effort, out var description) && description.Length > 0
                ? $"{effort}: {description}"
                : effort)) + ".";

    private static (ProviderSpeedMode Speed, string? ServiceTier) ResolveSpeed(
        AgentRole role,
        string name,
        AdapterSnapshot adapter,
        ModelInfo? model,
        List<ProfileProblem> problems)
    {
        if (!adapter.Capabilities.Has(AdapterFeatures.ProviderSpeed))
        {
            problems.Add(new ProfileProblem(
                role, ProblemSeverity.Blocking, "speed-unsupported",
                $"{name}: provider speed is Unavailable for this adapter.", "Return to standard speed with /speed standard."));
            return (ProviderSpeedMode.Standard, null);
        }

        var tier = model?.ServiceTiers.FirstOrDefault(t => t.Faster);
        if (tier is null)
        {
            problems.Add(new ProfileProblem(
                role, ProblemSeverity.Blocking, "speed-unavailable",
                $"{name}: the provider lists no faster service tier for model '{model?.Id}' on this account. The model is never swapped for a faster one.",
                "Return to standard speed with /speed standard."));
            return (ProviderSpeedMode.Standard, null);
        }

        if (!adapter.PaidSpeedAuthorized)
        {
            problems.Add(new ProfileProblem(
                role, ProblemSeverity.Blocking, "speed-unauthorized",
                $"{name}: the faster tier '{tier.Name}' changes billing and has not been authorized.",
                "Review the billing change and authorize it with /speed provider."));
            return (ProviderSpeedMode.Standard, null);
        }

        return (ProviderSpeedMode.Provider, tier.Id);
    }
}
