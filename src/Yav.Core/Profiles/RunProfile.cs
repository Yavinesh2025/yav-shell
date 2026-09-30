using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Yav.Core.Agents;

namespace Yav.Core.Profiles;

/// <summary>The user's choice for one role, before it is resolved against what the provider supports.</summary>
public sealed record RoleSelection(
    string AdapterId,
    string ModelId,
    // "maximum" resolves to the highest effort the provider lists for the model; anything else is an exact provider value.
    string EffortPreference = RoleSelection.MaximumEffort)
{
    public const string MaximumEffort = "maximum";

    public bool WantsMaximum => string.Equals(EffortPreference, MaximumEffort, StringComparison.OrdinalIgnoreCase);
}

public enum ProviderSpeedMode
{
    Standard,

    /// <summary>The provider's own paid fast serving, when officially supported for the exact model and route.</summary>
    Provider,
}

public sealed record QualityPolicy(
    bool QualityLock = true,
    // Under strict policy an unverified or unsupported configuration blocks the run.
    bool Strict = true,
    bool RequireReview = true,
    bool RequireGates = true,
    int MaxRepairCycles = 2,
    bool Adaptive = false,
    bool Optimization = true,
    // False: a required check that already failed before the task is the user's decision (waive it, or fix it first).
    // True: it is sent to Model A like any other failure, for projects whose acceptance checks are written before the change.
    bool RepairPreExistingFailures = false)
{
    public string Label => Adaptive ? "Adaptive" : Strict ? "Strict Max" : "Relaxed";
}

/// <summary>Settings for one role as they will be requested from the provider.</summary>
public sealed record RoleProfile(
    AgentRole Role,
    string AdapterId,
    string Provider,
    string AdapterVersion,
    AdapterMaturity AdapterMaturity,
    string ModelId,
    string? ModelDisplayName,
    string? ResolvedModelId,
    VerificationStatus ModelAvailability,
    string EffortPreference,
    string RequestedEffort,
    VerificationStatus EffortSupport,
    IReadOnlyList<string> SupportedEfforts,
    SandboxLevel Sandbox,
    ApprovalMode Approvals,
    ProviderSpeedMode Speed,
    string? ServiceTier,
    AccountRouteKind AccountRoute,
    string AccountRouteLabel,
    BillingKind Billing,
    string InstructionTemplateId,
    string InstructionTemplateVersion);

/// <summary>
/// The configuration a run was started with. It is written once, hashed, and never changed;
/// changing models, effort, project or permissions creates a new profile for a later run.
/// </summary>
public sealed record RunProfile(
    string ProfileId,
    DateTimeOffset CreatedAt,
    string ProjectPath,
    bool ProjectTrusted,
    QualityPolicy Policy,
    RoleProfile Implementer,
    RoleProfile Reviewer,
    IReadOnlyList<string> ProjectInstructionFiles,
    IReadOnlyList<string> RequiredGateIds,
    string? GateConfigurationHash,
    string YavVersion)
{
    public bool RolesUseDistinctModels =>
        !(string.Equals(Implementer.AdapterId, Reviewer.AdapterId, StringComparison.OrdinalIgnoreCase)
          && string.Equals(Implementer.ModelId, Reviewer.ModelId, StringComparison.OrdinalIgnoreCase));

    public RoleProfile For(AgentRole role) => role == AgentRole.Implementer ? Implementer : Reviewer;

    public string ToCanonicalJson() => JsonSerializer.Serialize(this, ProfileJson.Default.RunProfile);

    /// <summary>SHA-256 of the canonical JSON. Evidence is bound to this value.</summary>
    public string ComputeHash()
    {
        var bytes = Encoding.UTF8.GetBytes(ToCanonicalJson());
        return Convert.ToHexStringLower(SHA256.HashData(bytes));
    }

    public static RunProfile FromJson(string json) =>
        JsonSerializer.Deserialize(json, ProfileJson.Default.RunProfile)
        ?? throw new InvalidDataException("The stored run profile is empty.");
}

/// <summary>What the provider reported for one setting during the run, compared with the profile.</summary>
public sealed record ProfileConfirmation(
    AgentRole Role,
    string Setting,
    string? Requested,
    string? Effective,
    VerificationStatus Status,
    string Source,
    DateTimeOffset ObservedAt);

[JsonSourceGenerationOptions(
    WriteIndented = false,
    PropertyNamingPolicy = JsonKnownNamingPolicy.CamelCase,
    UseStringEnumConverter = true,
    DefaultIgnoreCondition = JsonIgnoreCondition.Never)]
[JsonSerializable(typeof(RunProfile))]
[JsonSerializable(typeof(ProfileConfirmation))]
[JsonSerializable(typeof(List<ProfileConfirmation>))]
[JsonSerializable(typeof(QualityPolicy))]
[JsonSerializable(typeof(RoleSelection))]
internal sealed partial class ProfileJson : JsonSerializerContext;
