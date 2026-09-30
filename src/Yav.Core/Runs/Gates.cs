namespace Yav.Core.Runs;

public enum GateKind
{
    Build,
    Lint,
    TypeCheck,
    Test,
    Smoke,
    Integration,
    Custom,
}

/// <summary>A trusted, explicit validation command. The agent cannot define or change one.</summary>
public sealed record GateDefinition(
    string Id,
    GateKind Kind,
    string Title,
    string Command,
    IReadOnlyList<string> Arguments,
    // Relative to the workspace root. Empty means the root itself.
    string WorkingDirectory,
    int TimeoutSeconds,
    bool Required,
    IReadOnlyDictionary<string, string> Environment,
    IReadOnlyList<int> SuccessExitCodes,
    // Things the gate needs that may be absent: a credential name, a service, hardware, manual inspection.
    IReadOnlyList<string> Requires)
{
    public string DisplayCommand => Arguments.Count == 0 ? Command : Command + " " + string.Join(' ', Arguments.Select(QuoteForDisplay));

    private static string QuoteForDisplay(string argument) =>
        argument.Length == 0 || argument.Any(char.IsWhiteSpace) ? "\"" + argument + "\"" : argument;
}

public enum GateStatus
{
    Passed,
    Failed,
    TimedOut,

    /// <summary>The gate could not be run here, for a stated reason. This is never treated as a pass.</summary>
    Unverified,

    /// <summary>The gate could not be started, for example because the command was not found.</summary>
    Error,
    Cancelled,
}

public sealed record GateResult(
    string ResultId,
    string RunId,
    string GateId,
    string GateTitle,
    GateKind Kind,
    bool Required,
    EvidenceBinding Binding,
    GateStatus Status,
    int? ExitCode,
    string CommandLine,
    string WorkingDirectory,
    DateTimeOffset StartedAt,
    long DurationMs,
    // Full captured output. The display may shorten it; this file never is.
    string? OutputPath,
    string OutputTail,
    long OutputBytes,
    string? Limitation,
    // True when the same gate also failed on the unmodified baseline.
    bool? FailsOnBaseline,
    // True when the check ran against the unmodified baseline rather than a candidate.
    bool IsBaselineRun);

/// <summary>A user's explicit, recorded decision to accept a run although one required gate did not pass.</summary>
public sealed record GateWaiver(string RunId, string GateId, string CandidateFingerprint, string Reason, DateTimeOffset GrantedAt);

/// <summary>Trusted project configuration. Loaded from the pinned, user-approved version only.</summary>
public sealed record ProjectConfiguration(
    int SchemaVersion,
    IReadOnlyList<GateDefinition> Gates,
    // Commands run once in a fresh isolated workspace, for example to restore dependencies.
    IReadOnlyList<GateDefinition> Prepare,
    // Workspace-relative globs for trusted acceptance tests and other files the agent must not quietly change.
    IReadOnlyList<string> ProtectedPaths,
    // Ignored files to copy into the isolated workspace. Never applied to paths that look like secrets unless listed in AllowSecrets.
    IReadOnlyList<string> ReplicateIgnored,
    IReadOnlyList<string> AllowSecrets,
    // Globs that identify test files, used to report when existing tests were modified.
    IReadOnlyList<string> TestPaths,
    // "copy" runs gates in a disposable execution copy; "candidate" runs them in the candidate workspace and verifies it afterwards.
    string ValidationExecution)
{
    public const string ExecutionCopy = "copy";
    public const string ExecutionCandidate = "candidate";

    public static readonly IReadOnlyList<string> DefaultTestPaths =
    [
        "**/test/**", "**/tests/**", "**/__tests__/**", "**/spec/**", "**/*.test.*", "**/*.spec.*", "**/*_test.*",
        "**/test_*.py", "**/*Tests.cs", "**/*Test.cs", "**/*Tests.java", "**/*Test.java", "**/*.Tests/**", "**/*.Test/**",
    ];

    public static ProjectConfiguration Empty { get; } = new(1, [], [], [], [], [], DefaultTestPaths, ExecutionCopy);

    public IEnumerable<GateDefinition> RequiredGates => Gates.Where(g => g.Required);
}
