using Yav.Core.Runs;

namespace Yav.Core.Ports;

public enum ConfigurationTrust
{
    /// <summary>The project has no YAV configuration.</summary>
    None,

    /// <summary>The configuration on disk is exactly the version the user approved.</summary>
    Trusted,

    /// <summary>The file on disk differs from the approved version. The approved version stays in effect until the change is reviewed.</summary>
    Changed,

    /// <summary>A configuration file exists but was never approved. Nothing from it is executed.</summary>
    Untrusted,

    /// <summary>The file could not be parsed.</summary>
    Invalid,
}

public sealed record ProjectConfigurationState(
    ConfigurationTrust Trust,
    // Always the approved version (or empty). Never read from a file the agent could have edited.
    ProjectConfiguration Effective,
    // What the file on disk says, when that differs from the approved version.
    ProjectConfiguration? Pending,
    string? TrustedHash,
    string? FileHash,
    string? FilePath,
    IReadOnlyList<string> Errors);

public sealed record GateRunContext(
    string RunId,
    // Directory the gate runs in: a disposable execution copy, or the candidate workspace.
    string WorkingRoot,
    EvidenceBinding Binding,
    string EvidenceDirectory,
    bool IsBaselineRun);

public sealed record GateProposal(GateDefinition Gate, string Reason);

public interface IValidationService
{
    /// <summary>Name of the optional configuration file in a project's root directory.</summary>
    string ConfigurationFileName { get; }

    ProjectConfigurationState LoadConfiguration(string projectPath);

    /// <summary>Records the user's approval of exactly this configuration content.</summary>
    void TrustConfiguration(string projectPath, ProjectConfiguration configuration, string json);

    /// <summary>Suggests gates from the project type. A suggestion is never run until the user approves it.</summary>
    IReadOnlyList<GateProposal> Detect(string projectPath);

    string Serialize(ProjectConfiguration configuration);

    ProjectConfiguration Parse(string json, out IReadOnlyList<string> errors);

    /// <summary>
    /// Identifies the toolchain the gates would run with: the resolved executables and their versions on disk,
    /// the operating system build and the gate configuration.
    /// </summary>
    string ComputeEnvironmentFingerprint(IReadOnlyList<GateDefinition> gates, string? configurationHash);

    Task<GateResult> RunGateAsync(GateDefinition gate, GateRunContext context, Action<string>? onOutputLine, CancellationToken cancellationToken);
}

public interface IProjectTrustStore
{
    bool IsProjectTrusted(string projectPath);

    void SetProjectTrusted(string projectPath, bool trusted);

    (string Hash, string Json)? GetTrustedConfiguration(string projectPath);

    void SaveTrustedConfiguration(string projectPath, string hash, string json);

    bool IsRouteAcknowledged(string routeKey);

    void AcknowledgeRoute(string routeKey, string statement);

    bool IsPaidSpeedAuthorized(string routeKey);

    void AuthorizePaidSpeed(string routeKey, string statement);

    bool IsInPlaceAcknowledged(string projectPath);

    void AcknowledgeInPlace(string projectPath, string statement);

    /// <summary>True when the user accepted exactly these differences between the project and its isolated copy.</summary>
    bool AreGapsAcknowledged(string projectPath, string gapsFingerprint);

    void AcknowledgeGaps(string projectPath, string gapsFingerprint, string statement);

    /// <summary>
    /// True when the user accepted for this project that a candidate is accepted on the review alone. It is kept as
    /// given, whatever checks the project has. A run applies it only while the approved configuration requires no
    /// check, so a required check that is approved later takes precedence, and the acceptance applies again once no
    /// check is required any more. An approved check that is optional does not set it aside, because a run does not
    /// run optional checks.
    /// </summary>
    bool IsReviewOnlyAccepted(string projectPath);

    void AcceptReviewOnly(string projectPath, string statement);

    /// <summary>Withdraws the user's consent that a candidate of this project is accepted on the review alone. False when there was none.</summary>
    bool WithdrawReviewOnly(string projectPath);

    /// <summary>
    /// When the acceptance of the review alone was last withdrawn for this project; null when it never was. A candidate
    /// that a run accepted on the review alone is not applied once the acceptance was withdrawn after the run started.
    /// </summary>
    DateTimeOffset? ReviewOnlyWithdrawnAt(string projectPath);
}
