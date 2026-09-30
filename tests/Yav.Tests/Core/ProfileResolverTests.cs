using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Profiles;
using Yav.Tests.Support;

namespace Yav.Tests.Core;

public class ProfileResolverTests
{
    private const string Codex = "codex-app-server";
    private const string Claude = "claude-cli";

    private static ModelInfo Model(string id, params string[] efforts) =>
        new(id, id, null, efforts, efforts.Length > 0 ? efforts[0] : null, false, false, [], null, "model/list");

    private static ModelInfo ModelWithFastTier(string id) =>
        new(id, id, null, ["low", "high"], "high", false, false, [new ServiceTierInfo("fast", "Fast", "Faster responses", Faster: true)], null, "model/list");

    private static AdapterSnapshot Adapter(
        string id,
        IReadOnlyList<ModelInfo>? models = null,
        bool found = true,
        bool authenticated = true,
        RoutePolicy policy = RoutePolicy.RequiresAcknowledgement,
        bool acknowledged = true,
        AdapterFeatures? features = null,
        bool paidSpeedAuthorized = false,
        bool versionTested = true)
    {
        var detection = new AdapterDetection(
            id, id == Claude ? "anthropic" : "openai", found, found ? @"C:\tools\agent.exe" : null, found ? "1.0.0" : null,
            AdapterMaturity.Stable, null, versionTested, "1.0.x", []);
        var auth = new AuthStatus(
            authenticated, AccountRouteKind.Subscription, "Plan (pro)", "pro", "firstParty", BillingKind.IncludedInSubscription,
            policy, "See the provider terms.", "status command", Builders.Now);
        var capabilities = new AdapterCapabilities(
            features ?? (AdapterFeatures.Streaming | AdapterFeatures.ReadOnlyEnforcement | AdapterFeatures.StructuredOutput
                | AdapterFeatures.ModelListing | AdapterFeatures.EffortReadback | AdapterFeatures.ProviderSpeed),
            []);
        return new AdapterSnapshot(
            id, detection.Provider, detection, auth,
            models ?? [Model("model-a", "low", "medium", "high", "xhigh"), Model("model-b", "low", "high", "max")],
            capabilities, acknowledged, paidSpeedAuthorized);
    }

    private static ProfileRequest Request(
        RoleSelection? implementer = null,
        RoleSelection? reviewer = null,
        QualityPolicy? policy = null,
        IReadOnlyDictionary<string, AdapterSnapshot>? adapters = null,
        ProviderSpeedMode speed = ProviderSpeedMode.Standard,
        IReadOnlyList<string>? gates = null) => new(
        ProjectPath: @"C:\Projects\MyApp",
        ProjectTrusted: true,
        Policy: policy ?? new QualityPolicy(),
        Implementer: implementer ?? new RoleSelection(Codex, "model-a"),
        Reviewer: reviewer ?? new RoleSelection(Codex, "model-b"),
        Speed: speed,
        Adapters: adapters ?? new Dictionary<string, AdapterSnapshot> { [Codex] = Adapter(Codex) },
        ProjectInstructionFiles: [],
        RequiredGateIds: gates ?? ["test"],
        GateConfigurationHash: "gates-1",
        YavVersion: "0.1.0",
        Now: Builders.Now);

    [Fact]
    public void Maximum_effort_resolves_to_the_highest_value_each_model_lists()
    {
        var resolution = ProfileResolver.Resolve(Request());

        Assert.True(resolution.CanRun);
        Assert.Equal("xhigh", resolution.Profile!.Implementer.RequestedEffort);
        Assert.Equal("max", resolution.Profile.Reviewer.RequestedEffort);
        Assert.Equal(VerificationStatus.Verified, resolution.Profile.Implementer.EffortSupport);
    }

    [Fact]
    public void The_highest_effort_does_not_depend_on_the_order_the_provider_lists_them()
    {
        Assert.Equal("xhigh", ProfileResolver.HighestEffort(["xhigh", "low", "high", "medium"]));
        Assert.Equal("MAX", ProfileResolver.HighestEffort(["low", "MAX", "high"]));
    }

    [Fact]
    public void An_effort_name_that_cannot_be_ranked_is_not_guessed()
    {
        Assert.Null(ProfileResolver.HighestEffort(["low", "high", "turbo"]));

        var adapters = new Dictionary<string, AdapterSnapshot>
        {
            [Codex] = Adapter(Codex, [Model("model-a", "low", "high", "turbo"), Model("model-b", "low", "high")]),
        };
        var resolution = ProfileResolver.Resolve(Request(adapters: adapters));

        Assert.False(resolution.CanRun);
        Assert.Contains(resolution.Blocking, p => p.Code == "effort-unranked" && p.Role == AgentRole.Implementer);
    }

    [Fact]
    public void An_unrankable_maximum_needs_an_explicit_choice_even_when_the_policy_is_relaxed()
    {
        var delegating = Model("model-a", "low", "high", "max", "ultra") with
        {
            EffortDescriptions = new Dictionary<string, string>
            {
                ["max"] = "Maximum reasoning depth for the hardest problems",
                ["ultra"] = "Maximum reasoning with automatic task delegation",
            },
        };
        var adapters = new Dictionary<string, AdapterSnapshot> { [Codex] = Adapter(Codex, [delegating, Model("model-b", "low", "high")]) };

        var resolution = ProfileResolver.Resolve(Request(adapters: adapters, policy: new QualityPolicy(Strict: false)));

        Assert.False(resolution.CanRun);
        var problem = Assert.Single(resolution.Blocking);
        Assert.Equal("effort-unranked", problem.Code);
        Assert.Contains("ultra: Maximum reasoning with automatic task delegation", problem.Message, StringComparison.Ordinal);
        Assert.Equal(string.Empty, resolution.Profile!.Implementer.RequestedEffort);
        Assert.Equal(VerificationStatus.RequestedUnverified, resolution.Profile.Implementer.EffortSupport);
    }

    [Fact]
    public void An_exact_effort_the_provider_lists_is_accepted_even_when_yav_cannot_rank_it()
    {
        var adapters = new Dictionary<string, AdapterSnapshot>
        {
            [Codex] = Adapter(Codex, [Model("model-a", "low", "high", "max", "ultra"), Model("model-b", "low", "high")]),
        };

        var resolution = ProfileResolver.Resolve(Request(implementer: new RoleSelection(Codex, "model-a", "ultra"), adapters: adapters));

        Assert.True(resolution.CanRun);
        Assert.Equal("ultra", resolution.Profile!.Implementer.RequestedEffort);
        Assert.Equal(VerificationStatus.Verified, resolution.Profile.Implementer.EffortSupport);
    }

    [Fact]
    public void An_agent_that_cannot_report_its_settings_blocks_a_strict_run_before_usage_is_spent()
    {
        var silent = Adapter(Codex, features: AdapterFeatures.Streaming | AdapterFeatures.ReadOnlyEnforcement | AdapterFeatures.StructuredOutput | AdapterFeatures.ModelListing);
        var adapters = new Dictionary<string, AdapterSnapshot> { [Codex] = silent };

        var strict = ProfileResolver.Resolve(Request(adapters: adapters));
        var relaxed = ProfileResolver.Resolve(Request(adapters: adapters, policy: new QualityPolicy(Strict: false)));

        Assert.False(strict.CanRun);
        Assert.Contains(strict.Blocking, p => p.Code == "settings-unverifiable" && p.Role == AgentRole.Implementer);
        Assert.Contains(strict.Blocking, p => p.Code == "settings-unverifiable" && p.Role == AgentRole.Reviewer);
        Assert.True(relaxed.CanRun);
        var warning = Assert.Single(relaxed.Problems, p => p.Code == "settings-unverifiable" && p.Role == AgentRole.Implementer);
        Assert.Equal(ProblemSeverity.Warning, warning.Severity);
        Assert.Contains("Requested / Unverified", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_unsupported_effort_blocks_and_is_not_lowered()
    {
        var resolution = ProfileResolver.Resolve(Request(implementer: new RoleSelection(Codex, "model-a", "max")));

        Assert.False(resolution.CanRun);
        var problem = Assert.Single(resolution.Blocking);
        Assert.Equal("effort-unsupported", problem.Code);
        Assert.Equal("max", resolution.Profile!.Implementer.RequestedEffort);
        Assert.Equal(VerificationStatus.Unsupported, resolution.Profile.Implementer.EffortSupport);
    }

    [Fact]
    public void An_explicit_effort_below_the_maximum_is_allowed_but_pointed_out()
    {
        var resolution = ProfileResolver.Resolve(Request(implementer: new RoleSelection(Codex, "model-a", "medium")));

        Assert.True(resolution.CanRun);
        Assert.Equal("medium", resolution.Profile!.Implementer.RequestedEffort);
        Assert.Contains(resolution.Problems, p => p.Code == "effort-below-maximum" && p.Severity == ProblemSeverity.Info);
    }

    [Fact]
    public void A_model_the_account_does_not_list_blocks_and_nothing_is_substituted()
    {
        var resolution = ProfileResolver.Resolve(Request(reviewer: new RoleSelection(Codex, "model-z")));

        Assert.False(resolution.CanRun);
        Assert.Contains(resolution.Blocking, p => p.Code == "model-unavailable" && p.Role == AgentRole.Reviewer);
        Assert.Equal("model-z", resolution.Profile!.Reviewer.ModelId);
    }

    [Fact]
    public void When_models_cannot_be_listed_the_selection_is_unverified_and_blocks_under_strict_policy()
    {
        var adapters = new Dictionary<string, AdapterSnapshot> { [Codex] = Adapter(Codex, models: []) };

        var strict = ProfileResolver.Resolve(Request(adapters: adapters));
        var relaxed = ProfileResolver.Resolve(Request(adapters: adapters, policy: new QualityPolicy(Strict: false)));

        Assert.False(strict.CanRun);
        Assert.Contains(strict.Blocking, p => p.Code == "model-unverified");
        Assert.Equal(VerificationStatus.RequestedUnverified, strict.Profile!.Implementer.ModelAvailability);
        Assert.True(relaxed.CanRun);
        Assert.Contains(relaxed.Problems, p => p.Code == "model-unverified" && p.Severity == ProblemSeverity.Warning);
    }

    [Fact]
    public void The_same_model_in_both_roles_blocks_under_strict_quality_lock()
    {
        var resolution = ProfileResolver.Resolve(Request(reviewer: new RoleSelection(Codex, "model-a")));

        Assert.False(resolution.CanRun);
        Assert.Contains(resolution.Blocking, p => p.Code == "single-model");
    }

    [Fact]
    public void Two_distinct_models_through_one_adapter_are_a_valid_configuration()
    {
        var resolution = ProfileResolver.Resolve(Request());

        Assert.True(resolution.CanRun);
        Assert.True(resolution.Profile!.RolesUseDistinctModels);
        Assert.Equal(resolution.Profile.Implementer.AdapterId, resolution.Profile.Reviewer.AdapterId);
    }

    [Fact]
    public void A_missing_second_model_blocks()
    {
        var request = Request() with { Reviewer = null };

        var resolution = ProfileResolver.Resolve(request);

        Assert.False(resolution.CanRun);
        Assert.Contains(resolution.Blocking, p => p.Code == "model-b-missing");
    }

    [Fact]
    public void An_agent_that_is_not_installed_blocks()
    {
        var adapters = new Dictionary<string, AdapterSnapshot>
        {
            [Codex] = Adapter(Codex),
            [Claude] = Adapter(Claude, found: false),
        };

        var resolution = ProfileResolver.Resolve(Request(reviewer: new RoleSelection(Claude, "model-b"), adapters: adapters));

        Assert.False(resolution.CanRun);
        Assert.Contains(resolution.Blocking, p => p.Code == "adapter-not-found" && p.Role == AgentRole.Reviewer);
    }

    [Fact]
    public void A_route_that_was_not_acknowledged_blocks()
    {
        var adapters = new Dictionary<string, AdapterSnapshot> { [Codex] = Adapter(Codex, acknowledged: false) };

        var resolution = ProfileResolver.Resolve(Request(adapters: adapters));

        Assert.False(resolution.CanRun);
        Assert.Contains(resolution.Blocking, p => p.Code == "route-unacknowledged");
    }

    [Fact]
    public void A_route_the_provider_does_not_permit_blocks_even_when_acknowledged()
    {
        var adapters = new Dictionary<string, AdapterSnapshot> { [Codex] = Adapter(Codex, policy: RoutePolicy.NotPermitted, acknowledged: true) };

        var resolution = ProfileResolver.Resolve(Request(adapters: adapters));

        Assert.False(resolution.CanRun);
        Assert.Contains(resolution.Blocking, p => p.Code == "route-not-permitted");
    }

    [Fact]
    public void An_agent_that_is_not_signed_in_blocks()
    {
        var adapters = new Dictionary<string, AdapterSnapshot> { [Codex] = Adapter(Codex, authenticated: false) };

        var resolution = ProfileResolver.Resolve(Request(adapters: adapters));

        Assert.Contains(resolution.Blocking, p => p.Code == "auth-missing");
    }

    [Fact]
    public void A_reviewer_without_read_only_enforcement_blocks()
    {
        var weak = Adapter(Claude, features: AdapterFeatures.Streaming | AdapterFeatures.StructuredOutput);
        var adapters = new Dictionary<string, AdapterSnapshot> { [Codex] = Adapter(Codex), [Claude] = weak };

        var resolution = ProfileResolver.Resolve(Request(reviewer: new RoleSelection(Claude, "model-b"), adapters: adapters));

        Assert.Contains(resolution.Blocking, p => p.Code == "reviewer-not-read-only");
    }

    [Fact]
    public void The_reviewer_is_always_read_only_and_never_asks_for_approval()
    {
        var profile = ProfileResolver.Resolve(Request()).Profile!;

        Assert.Equal(SandboxLevel.ReadOnly, profile.Reviewer.Sandbox);
        Assert.Equal(ApprovalMode.NeverAsk, profile.Reviewer.Approvals);
        Assert.Equal(SandboxLevel.WorkspaceWrite, profile.Implementer.Sandbox);
    }

    [Fact]
    public void Required_gates_that_are_not_configured_block_before_any_usage_is_spent()
    {
        var blocked = ProfileResolver.Resolve(Request(gates: []));
        var allowed = ProfileResolver.Resolve(Request(gates: [], policy: new QualityPolicy(RequireGates: false)));

        Assert.Contains(blocked.Blocking, p => p.Code == "gates-missing");
        Assert.True(allowed.CanRun);
    }

    [Fact]
    public void Provider_speed_without_authorization_blocks_and_standard_speed_stays_in_the_profile()
    {
        var adapters = new Dictionary<string, AdapterSnapshot>
        {
            [Codex] = Adapter(Codex, [ModelWithFastTier("model-a"), ModelWithFastTier("model-b")], paidSpeedAuthorized: false),
        };

        var resolution = ProfileResolver.Resolve(Request(adapters: adapters, speed: ProviderSpeedMode.Provider));

        Assert.False(resolution.CanRun);
        Assert.Contains(resolution.Blocking, p => p.Code == "speed-unauthorized");
        Assert.Equal(ProviderSpeedMode.Standard, resolution.Profile!.Implementer.Speed);
        Assert.Null(resolution.Profile.Implementer.ServiceTier);
    }

    [Fact]
    public void Authorized_provider_speed_uses_the_tier_the_model_lists_and_keeps_the_model()
    {
        var adapters = new Dictionary<string, AdapterSnapshot>
        {
            [Codex] = Adapter(Codex, [ModelWithFastTier("model-a"), ModelWithFastTier("model-b")], paidSpeedAuthorized: true),
        };

        var resolution = ProfileResolver.Resolve(Request(adapters: adapters, speed: ProviderSpeedMode.Provider));

        Assert.True(resolution.CanRun);
        Assert.Equal("fast", resolution.Profile!.Implementer.ServiceTier);
        Assert.Equal("model-a", resolution.Profile.Implementer.ModelId);
        Assert.Equal(ProviderSpeedMode.Provider, resolution.Profile.Implementer.Speed);
    }

    [Fact]
    public void Provider_speed_is_unavailable_when_the_model_lists_no_faster_tier()
    {
        var resolution = ProfileResolver.Resolve(Request(speed: ProviderSpeedMode.Provider));

        Assert.False(resolution.CanRun);
        Assert.Contains(resolution.Blocking, p => p.Code == "speed-unavailable");
    }

    [Fact]
    public void Adaptive_mode_is_labelled_and_never_called_strict_max()
    {
        var resolution = ProfileResolver.Resolve(Request(policy: new QualityPolicy(Adaptive: true)));

        Assert.Equal("Adaptive", resolution.Profile!.Policy.Label);
        Assert.Contains(resolution.Problems, p => p.Code == "adaptive");
    }

    [Fact]
    public void In_adaptive_mode_the_effort_the_user_approved_for_the_task_is_used_for_model_a_and_the_review_stays_at_maximum()
    {
        var request = Request(policy: new QualityPolicy(Adaptive: true)) with { ApprovedImplementerEffort = "medium" };

        var resolution = ProfileResolver.Resolve(request);

        Assert.True(resolution.CanRun, string.Join("; ", resolution.Problems.Select(p => p.Message)));
        Assert.Equal("medium", resolution.Profile!.Implementer.RequestedEffort);
        Assert.Equal("model-a", resolution.Profile.Implementer.ModelId);
        Assert.Equal("max", resolution.Profile.Reviewer.RequestedEffort);
        Assert.Equal("Adaptive", resolution.Profile.Policy.Label);
        Assert.Contains(resolution.Problems, p => p.Code == "adaptive-effort" && p.Severity == ProblemSeverity.Warning && p.Message.Contains("'medium' instead of 'xhigh'", StringComparison.Ordinal));
    }

    [Fact]
    public void In_adaptive_mode_a_task_for_which_nothing_was_approved_runs_at_maximum()
    {
        var resolution = ProfileResolver.Resolve(Request(policy: new QualityPolicy(Adaptive: true)));

        Assert.Equal("xhigh", resolution.Profile!.Implementer.RequestedEffort);
        Assert.Equal("max", resolution.Profile.Reviewer.RequestedEffort);
    }

    [Fact]
    public void Without_adaptive_mode_no_other_effort_than_the_chosen_one_is_used()
    {
        var resolution = ProfileResolver.Resolve(Request() with { ApprovedImplementerEffort = "medium" });

        Assert.False(resolution.CanRun);
        Assert.Contains(resolution.Blocking, p => p.Code == "adaptive-off");
    }

    [Theory]
    [InlineData("turbo")]
    [InlineData("max")]
    [InlineData(" ")]
    public void An_effort_the_model_does_not_list_is_not_used_in_adaptive_mode_either(string effort)
    {
        var request = Request(policy: new QualityPolicy(Adaptive: true)) with { ApprovedImplementerEffort = effort };

        var resolution = ProfileResolver.Resolve(request);

        Assert.False(resolution.CanRun);
        Assert.Contains(resolution.Blocking, p => p.Code == "effort-unsupported" && p.Role == AgentRole.Implementer);
    }

    [Fact]
    public void The_effort_that_was_approved_for_a_task_is_part_of_what_identifies_the_configuration()
    {
        var policy = new QualityPolicy(Adaptive: true);
        var maximum = ProfileResolver.Resolve(Request(policy: policy)).Profile!;
        var lowered = ProfileResolver.Resolve(Request(policy: policy) with { ApprovedImplementerEffort = "low" }).Profile!;

        Assert.NotEqual(maximum.ComputeHash(), lowered.ComputeHash());
    }

    [Fact]
    public void The_profile_hash_changes_when_any_setting_changes()
    {
        var first = ProfileResolver.Resolve(Request()).Profile!;
        var sameSettings = first with { };
        var otherEffort = first with { Implementer = first.Implementer with { RequestedEffort = "high" } };

        Assert.Equal(first.ComputeHash(), sameSettings.ComputeHash());
        Assert.NotEqual(first.ComputeHash(), otherEffort.ComputeHash());
        Assert.Equal(64, first.ComputeHash().Length);
    }

    [Fact]
    public void A_stored_profile_reads_back_identically()
    {
        var profile = ProfileResolver.Resolve(Request()).Profile!;

        var restored = RunProfile.FromJson(profile.ToCanonicalJson());

        Assert.Equal(profile.ComputeHash(), restored.ComputeHash());
        Assert.Equal("xhigh", restored.Implementer.RequestedEffort);
        Assert.Equal(SandboxLevel.ReadOnly, restored.Reviewer.Sandbox);
    }
}
