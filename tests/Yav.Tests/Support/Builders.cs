using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Profiles;
using Yav.Core.Runs;

namespace Yav.Tests.Support;

/// <summary>Builds domain objects with sensible defaults so a test states only what it is about.</summary>
public static class Builders
{
    public static readonly DateTimeOffset Now = new(2026, 9, 29, 10, 0, 0, TimeSpan.Zero);

    public const string Fingerprint = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    public const string OtherFingerprint = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    public const string ProfileHash = "profile-hash-1";
    public const string Environment = "env-1";

    public static RoleProfile Role(AgentRole role, string adapter = "codex-app-server", string model = "model-a", string effort = "xhigh") => new(
        Role: role,
        AdapterId: adapter,
        Provider: "openai",
        AdapterVersion: "0.158.0",
        AdapterMaturity: AdapterMaturity.Experimental,
        ModelId: model,
        ModelDisplayName: model,
        ResolvedModelId: null,
        ModelAvailability: VerificationStatus.Verified,
        EffortPreference: RoleSelection.MaximumEffort,
        RequestedEffort: effort,
        EffortSupport: VerificationStatus.Verified,
        SupportedEfforts: ["low", "medium", "high", "xhigh"],
        Sandbox: role == AgentRole.Reviewer ? SandboxLevel.ReadOnly : SandboxLevel.WorkspaceWrite,
        Approvals: role == AgentRole.Reviewer ? ApprovalMode.NeverAsk : ApprovalMode.AskUser,
        Speed: ProviderSpeedMode.Standard,
        ServiceTier: null,
        AccountRoute: AccountRouteKind.Subscription,
        AccountRouteLabel: "ChatGPT plan (pro)",
        Billing: BillingKind.IncludedInSubscription,
        InstructionTemplateId: "t",
        InstructionTemplateVersion: "v1");

    public static RunProfile Profile(QualityPolicy? policy = null, string modelA = "model-a", string modelB = "model-b") => new(
        ProfileId: "p-1",
        CreatedAt: Now,
        ProjectPath: @"C:\Projects\MyApp",
        ProjectTrusted: true,
        Policy: policy ?? new QualityPolicy(),
        Implementer: Role(AgentRole.Implementer, model: modelA),
        Reviewer: Role(AgentRole.Reviewer, model: modelB),
        ProjectInstructionFiles: [],
        RequiredGateIds: ["test"],
        GateConfigurationHash: "gates-1",
        YavVersion: "0.1.0");

    public static EvidenceBinding Binding(string fingerprint = Fingerprint, int acceptance = 1, string profile = ProfileHash, string environment = Environment) =>
        new(fingerprint, acceptance, profile, environment);

    public static Candidate Candidate(
        string fingerprint = Fingerprint,
        int acceptance = 1,
        string profile = ProfileHash,
        IReadOnlyList<string>? protectedPaths = null) => new(
        CandidateId: "c-1",
        RunId: "run-1",
        Sequence: 1,
        Fingerprint: fingerprint,
        BaselineFingerprint: "baseline",
        AcceptanceVersion: acceptance,
        ProfileHash: profile,
        Changes: new ChangeSet([new ChangedFile("src/login.cs", ChangeKind.Modified, "h1", "h2", 10, 12, false, null)]),
        ProtectedPathsTouched: protectedPaths ?? [],
        ExistingTestsTouched: [],
        FrozenAt: Now);

    public static Finding Finding(bool optional = false, string title = "Null user is dereferenced") => new(
        FindingId: "F1",
        Severity: FindingSeverity.Major,
        Category: FindingCategory.Defect,
        Optional: optional,
        File: "src/login.cs",
        Line: 42,
        Title: title,
        FailureScenario: "Unknown user throws.",
        Evidence: "Line 42 reads user before the null check.",
        SuggestedCorrection: null,
        Limitation: null,
        LocationVerified: true);

    public static ReviewResult Review(
        ReviewVerdict verdict = ReviewVerdict.Pass,
        EvidenceBinding? binding = null,
        IReadOnlyList<Finding>? findings = null,
        bool valid = true,
        bool sourceUnchanged = true,
        IReadOnlyList<string>? limitations = null) => new(
        ReviewId: "rev-1",
        RunId: "run-1",
        CandidateId: "c-1",
        Binding: binding ?? Binding(),
        Verdict: verdict,
        Summary: "Reviewed.",
        Coverage: "Read the changed files.",
        Findings: findings ?? [],
        Limitations: limitations ?? [],
        OutputValid: valid,
        ValidationErrors: valid ? [] : ["The reviewer's output is not valid JSON."],
        ReviewerAdapterId: "codex-app-server",
        ReviewerModel: "model-b",
        ReviewerSessionId: "thread-b",
        SourceUnchangedDuringReview: sourceUnchanged,
        StartedAt: Now,
        CompletedAt: Now.AddMinutes(1));

    public static GateDefinition Gate(string id = "test", bool required = true) => new(
        Id: id,
        Kind: GateKind.Test,
        Title: id == "test" ? "Unit tests" : id,
        Command: "dotnet",
        Arguments: ["test"],
        WorkingDirectory: string.Empty,
        TimeoutSeconds: 600,
        Required: required,
        Environment: new Dictionary<string, string>(),
        SuccessExitCodes: [0],
        Requires: []);

    public static GateResult GateResult(
        string gateId = "test",
        GateStatus status = GateStatus.Passed,
        EvidenceBinding? binding = null,
        int? exitCode = null,
        bool? failsOnBaseline = null,
        bool baselineRun = false,
        string? limitation = null,
        DateTimeOffset? startedAt = null) => new(
        ResultId: Ids.NewId("g"),
        RunId: "run-1",
        GateId: gateId,
        GateTitle: gateId == "test" ? "Unit tests" : gateId,
        Kind: GateKind.Test,
        Required: true,
        Binding: binding ?? Binding(),
        Status: status,
        ExitCode: exitCode ?? (status == GateStatus.Passed ? 0 : 1),
        CommandLine: "dotnet test",
        WorkingDirectory: @"C:\ws\x",
        StartedAt: startedAt ?? Now,
        DurationMs: 1000,
        OutputPath: null,
        OutputTail: "output",
        OutputBytes: 6,
        Limitation: limitation,
        FailsOnBaseline: failsOnBaseline,
        IsBaselineRun: baselineRun);

    public static ProfileConfirmation Confirmed(AgentRole role, string setting, string value) =>
        new(role, setting, value, value, VerificationStatus.Verified, "thread/start response", Now);

    public static IReadOnlyList<ProfileConfirmation> AllConfirmed() =>
    [
        Confirmed(AgentRole.Implementer, ProfileSettings.Model, "model-a"),
        Confirmed(AgentRole.Implementer, ProfileSettings.Effort, "xhigh"),
        Confirmed(AgentRole.Reviewer, ProfileSettings.Model, "model-b"),
        Confirmed(AgentRole.Reviewer, ProfileSettings.Effort, "xhigh"),
    ];

    public static AcceptanceInput Acceptance(
        RunProfile? profile = null,
        IReadOnlyList<ProfileConfirmation>? confirmations = null,
        Candidate? candidate = null,
        string? workspaceFingerprint = null,
        int acceptanceVersion = 1,
        string environment = Environment,
        ReviewResult? review = null,
        bool noReview = false,
        IReadOnlyList<GateDefinition>? gates = null,
        IReadOnlyList<GateResult>? results = null,
        IReadOnlyList<GateWaiver>? waivers = null,
        IReadOnlyList<string>? approvedProtected = null) => new(
        Profile: profile ?? Profile(),
        ProfileHash: ProfileHash,
        Confirmations: confirmations ?? AllConfirmed(),
        Candidate: candidate ?? Candidate(),
        CurrentWorkspaceFingerprint: workspaceFingerprint ?? Fingerprint,
        CurrentAcceptanceVersion: acceptanceVersion,
        CurrentEnvironmentFingerprint: environment,
        Review: noReview ? null : review ?? Review(),
        RequiredGates: gates ?? [Gate()],
        GateResults: results ?? [GateResult()],
        Waivers: waivers ?? [],
        ApprovedProtectedPaths: approvedProtected ?? []);
}
