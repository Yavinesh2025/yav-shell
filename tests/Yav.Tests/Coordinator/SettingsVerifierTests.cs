using Yav.Coordinator;
using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.Coordinator;

public class SettingsVerifierTests
{
    private static EffectiveSettings Reported(
        string? model = "model-a",
        string? effort = "xhigh",
        string? sandbox = "workspace-write",
        string? tier = null,
        string? credential = null) => new(
        Model: model,
        Effort: effort,
        Sandbox: sandbox,
        ApprovalPolicy: "on-request",
        ServiceTier: tier,
        WorkingDirectory: @"C:\ws",
        CredentialSource: credential,
        AgentVersion: "1.0.0",
        InstructionSources: [],
        Tools: [],
        Source: "thread/start response");

    private static SettingsVerification Verify(RoleProfile role, EffectiveSettings effective, QualityPolicy? policy = null) =>
        SettingsVerifier.Verify(role, policy ?? new QualityPolicy(), effective, Builders.Now);

    private static ProfileConfirmation Setting(SettingsVerification result, string setting) =>
        Assert.Single(result.Confirmations, c => c.Setting == setting);

    [Fact]
    public void Settings_the_provider_reports_back_unchanged_are_verified()
    {
        var result = Verify(Builders.Role(AgentRole.Implementer), Reported());

        Assert.Empty(result.Violations);
        Assert.Equal(VerificationStatus.Verified, Setting(result, ProfileSettings.Model).Status);
        Assert.Equal(VerificationStatus.Verified, Setting(result, ProfileSettings.Effort).Status);
        Assert.Equal(VerificationStatus.Verified, Setting(result, ProfileSettings.Sandbox).Status);
        Assert.All(result.Confirmations, c => Assert.Equal("thread/start response", c.Source));
    }

    [Fact]
    public void A_lower_effort_than_requested_is_a_violation_that_names_both_values()
    {
        var result = Verify(Builders.Role(AgentRole.Implementer, effort: "xhigh"), Reported(effort: "high"));

        var effort = Setting(result, ProfileSettings.Effort);
        Assert.Equal(VerificationStatus.Mismatch, effort.Status);
        Assert.Equal("xhigh", effort.Requested);
        Assert.Equal("high", effort.Effective);
        var violation = Assert.Single(result.Violations);
        Assert.Contains("xhigh", violation, StringComparison.Ordinal);
        Assert.Contains("high", violation, StringComparison.Ordinal);
        Assert.Contains("Model A", violation, StringComparison.Ordinal);
    }

    [Fact]
    public void A_different_model_than_requested_is_a_violation()
    {
        var result = Verify(Builders.Role(AgentRole.Reviewer, model: "model-b"), Reported(model: "model-b-mini", sandbox: "read-only"));

        Assert.Equal(VerificationStatus.Mismatch, Setting(result, ProfileSettings.Model).Status);
        Assert.Contains(result.Violations, v => v.Contains("model-b-mini", StringComparison.Ordinal) && v.Contains("Model B", StringComparison.Ordinal));
    }

    [Fact]
    public void An_alias_that_the_provider_reports_by_its_full_name_is_the_requested_model()
    {
        var role = Builders.Role(AgentRole.Implementer, model: "opus") with { ResolvedModelId = "claude-opus-5-5" };

        var result = Verify(role, Reported(model: "claude-opus-5-5"));

        Assert.Empty(result.Violations);
        Assert.Equal(VerificationStatus.Verified, Setting(result, ProfileSettings.Model).Status);
    }

    [Fact]
    public void A_setting_that_is_not_reported_is_unverified_and_stops_a_strict_run_only()
    {
        var role = Builders.Role(AgentRole.Implementer);

        var strict = Verify(role, Reported(effort: null));
        var relaxed = Verify(role, Reported(effort: null), new QualityPolicy(Strict: false));

        Assert.Equal(VerificationStatus.RequestedUnverified, Setting(strict, ProfileSettings.Effort).Status);
        Assert.Contains(strict.Violations, v => v.Contains("Requested / Unverified", StringComparison.Ordinal));
        Assert.Equal(VerificationStatus.RequestedUnverified, Setting(relaxed, ProfileSettings.Effort).Status);
        Assert.Empty(relaxed.Violations);
    }

    [Theory]
    [InlineData("xhigh", VerificationStatus.Verified)]
    [InlineData("high", VerificationStatus.Mismatch)]
    [InlineData(null, VerificationStatus.RequestedUnverified)]
    public void An_effort_that_was_reported_somewhere_else_than_the_rest_says_where(string? effort, VerificationStatus expected)
    {
        var reported = Reported(effort: effort) with { EffortSource = "get_settings answer" };

        var result = Verify(Builders.Role(AgentRole.Implementer, effort: "xhigh"), reported);

        Assert.Equal(expected, Setting(result, ProfileSettings.Effort).Status);
        Assert.Equal("get_settings answer", Setting(result, ProfileSettings.Effort).Source);
        Assert.Equal("thread/start response", Setting(result, ProfileSettings.Model).Source);
        Assert.Equal("thread/start response", Setting(result, ProfileSettings.Sandbox).Source);
    }

    [Theory]
    [InlineData("xhigh", true, 0)]
    [InlineData("high", true, 1)]
    [InlineData("high", false, 1)]
    [InlineData(null, true, 1)]
    [InlineData(null, false, 0)]
    public void Before_a_turn_the_effort_is_compared_and_a_model_that_is_named_later_is_not_missed(string? effort, bool strict, int violations)
    {
        var early = new EarlySettings(Model: null, Effort: effort, Source: "get_settings answer");

        var result = SettingsVerifier.VerifyBeforeTurn(Builders.Role(AgentRole.Implementer, effort: "xhigh"), new QualityPolicy(Strict: strict), early, Builders.Now);

        Assert.Equal(violations, result.Violations.Count);
        Assert.DoesNotContain(result.Confirmations, c => c.Setting == ProfileSettings.Model);
        Assert.Equal("get_settings answer", Setting(result, ProfileSettings.Effort).Source);
    }

    [Fact]
    public void Before_a_turn_a_model_that_is_named_is_compared()
    {
        var early = new EarlySettings(Model: "model-a-mini", Effort: "xhigh", Source: "thread/start response");

        var result = SettingsVerifier.VerifyBeforeTurn(Builders.Role(AgentRole.Implementer, effort: "xhigh"), new QualityPolicy(), early, Builders.Now);

        Assert.Contains("model-a-mini", Assert.Single(result.Violations), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AgentRole.Reviewer, true, 1)]
    [InlineData(AgentRole.Reviewer, false, 1)]
    [InlineData(AgentRole.Implementer, true, 0)]
    public void A_boundary_that_is_known_not_to_hold_keeps_a_review_from_being_sent_under_every_policy(AgentRole role, bool qualityLock, int violations)
    {
        var early = new EarlySettings(null, "xhigh", "get_settings answer", BoundaryProblem: "Claude Code loaded settings of the workspace (projectSettings).");

        var result = SettingsVerifier.VerifyBeforeTurn(Builders.Role(role, effort: "xhigh"), new QualityPolicy(QualityLock: qualityLock, Strict: false), early, Builders.Now);

        Assert.Equal(violations, result.Violations.Count);
        if (violations > 0)
        {
            Assert.Contains("projectSettings", result.Violations[0], StringComparison.Ordinal);
            Assert.Contains("A prompt alone is not enforcement.", result.Violations[0], StringComparison.Ordinal);
            Assert.Equal(VerificationStatus.RequestedUnverified, Setting(result, ProfileSettings.Sandbox).Status);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void An_agent_whose_requests_for_access_somebody_else_decides_is_not_run_under_any_policy(bool qualityLock, bool strict)
    {
        var reported = Reported() with { ApprovalsReviewer = "auto_review" };

        var result = Verify(Builders.Role(AgentRole.Implementer), reported, new QualityPolicy(QualityLock: qualityLock, Strict: strict));

        var row = Setting(result, ProfileSettings.ApprovalsReviewer);
        Assert.Equal(VerificationStatus.Mismatch, row.Status);
        Assert.Equal("you", row.Requested);
        Assert.Equal("auto_review", row.Effective);
        Assert.Contains(result.Violations, v => v.Contains("auto_review", StringComparison.Ordinal) && v.Contains("only you decide", StringComparison.Ordinal));
    }

    [Fact]
    public void That_the_user_decides_about_access_is_recorded_when_the_agent_says_so_and_left_out_when_it_does_not_say()
    {
        var said = Verify(Builders.Role(AgentRole.Implementer), Reported() with { ApprovalsReviewer = "user" });
        var silent = Verify(Builders.Role(AgentRole.Implementer), Reported());

        Assert.Empty(said.Violations);
        Assert.Equal(VerificationStatus.Verified, Setting(said, ProfileSettings.ApprovalsReviewer).Status);
        Assert.Equal("you", Setting(said, ProfileSettings.ApprovalsReviewer).Effective);
        Assert.DoesNotContain(silent.Confirmations, c => c.Setting == ProfileSettings.ApprovalsReviewer);
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void A_sandbox_that_lets_the_implementer_write_outside_the_workspace_is_not_the_one_that_was_asked_for(bool qualityLock, int violations)
    {
        var reported = Reported() with { Widening = new SandboxWidening(false, [@"C:\cache", @"D:\shared"]) };

        var result = Verify(Builders.Role(AgentRole.Implementer), reported, new QualityPolicy(QualityLock: qualityLock));

        var row = Setting(result, ProfileSettings.WritableOutside);
        Assert.Equal(VerificationStatus.Mismatch, row.Status);
        Assert.Equal("nothing", row.Requested);
        Assert.Equal(@"C:\cache; D:\shared", row.Effective);
        Assert.Equal(violations, result.Violations.Count);
        if (violations > 0)
        {
            Assert.Contains(@"C:\cache", result.Violations[0], StringComparison.Ordinal);
            Assert.Contains("Access is never widened", result.Violations[0], StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(AgentRole.Implementer, "workspace-write", true, "on")]
    [InlineData(AgentRole.Implementer, "workspace-write", false, "off")]
    [InlineData(AgentRole.Reviewer, "read-only", true, "on")]
    public void What_the_sandbox_says_about_the_network_is_shown_and_stops_nothing(AgentRole role, string sandbox, bool network, string shown)
    {
        var reported = Reported(model: role == AgentRole.Reviewer ? "model-b" : "model-a", sandbox: sandbox) with { Widening = new SandboxWidening(network, []) };

        var result = Verify(Builders.Role(role, model: role == AgentRole.Reviewer ? "model-b" : "model-a"), reported);

        Assert.Empty(result.Violations);
        Assert.Equal(shown, Setting(result, ProfileSettings.NetworkAccess).Effective);
        Assert.Equal(VerificationStatus.Verified, Setting(result, ProfileSettings.NetworkAccess).Status);
        Assert.Equal("nothing", Setting(result, ProfileSettings.WritableOutside).Effective);
        Assert.Equal(VerificationStatus.Verified, Setting(result, ProfileSettings.WritableOutside).Status);
    }

    [Fact]
    public void An_agent_that_does_not_say_what_widens_its_sandbox_gets_no_line_about_it()
    {
        var result = Verify(Builders.Role(AgentRole.Implementer), Reported());

        Assert.DoesNotContain(result.Confirmations, c => c.Setting is ProfileSettings.WritableOutside or ProfileSettings.NetworkAccess);
    }

    [Fact]
    public void An_effort_that_was_not_reported_says_where_it_was_asked_for()
    {
        var reported = Reported(effort: null) with { EffortSource = "get_settings (not answered)" };

        var result = Verify(Builders.Role(AgentRole.Implementer, effort: "xhigh"), reported, new QualityPolicy(Strict: false));

        Assert.Equal(VerificationStatus.RequestedUnverified, Setting(result, ProfileSettings.Effort).Status);
        Assert.Equal("get_settings (not answered)", Setting(result, ProfileSettings.Effort).Source);
    }

    [Fact]
    public void An_effort_that_is_known_not_to_be_sent_stops_a_run_that_asked_for_one_even_when_the_policy_is_not_strict()
    {
        // "none" is what an agent reports for a model that takes no effort. It is a lower effort, not a missing report.
        var result = Verify(Builders.Role(AgentRole.Implementer, effort: "max"), Reported(effort: "none"), new QualityPolicy(Strict: false));

        Assert.Equal(VerificationStatus.Mismatch, Setting(result, ProfileSettings.Effort).Status);
        Assert.Contains("'max' was requested but the provider reports 'none'", Assert.Single(result.Violations), StringComparison.Ordinal);
    }

    [Fact]
    public void Casing_is_not_a_difference()
    {
        var result = Verify(Builders.Role(AgentRole.Implementer, effort: "xhigh"), Reported(effort: "XHigh", model: "MODEL-A"));

        Assert.Empty(result.Violations);
    }

    [Theory]
    [InlineData("low")]
    [InlineData("max")]
    [InlineData(null)]
    public void An_effort_that_could_not_be_asked_for_is_never_called_verified(string? reported)
    {
        // The maximum was wanted, and what the maximum is could not be found out: no value was passed to the agent.
        var role = Builders.Role(AgentRole.Implementer, effort: string.Empty) with { EffortSupport = VerificationStatus.RequestedUnverified };

        var relaxed = Verify(role, Reported(effort: reported), new QualityPolicy(Strict: false));
        var strict = Verify(role, Reported(effort: reported));

        var effort = Setting(relaxed, ProfileSettings.Effort);
        Assert.Equal(VerificationStatus.RequestedUnverified, effort.Status);
        Assert.Equal(RoleSelection.MaximumEffort, effort.Requested);
        Assert.Equal(reported, effort.Effective);
        Assert.Empty(relaxed.Violations);
        Assert.Contains(strict.Violations, v => v.Contains("Requested / Unverified", StringComparison.Ordinal) && v.Contains("effort", StringComparison.Ordinal));
    }

    [Fact]
    public void A_model_without_an_effort_setting_has_nothing_to_dishonor()
    {
        var role = Builders.Role(AgentRole.Implementer, effort: string.Empty) with { SupportedEfforts = [] };

        var result = Verify(role, Reported(effort: null));

        Assert.Empty(result.Violations);
        Assert.Equal(VerificationStatus.Verified, Setting(result, ProfileSettings.Effort).Status);
    }

    [Theory]
    [InlineData("workspace-write")]
    [InlineData("danger-full-access")]
    [InlineData(null)]
    public void A_reviewer_that_is_not_confirmed_read_only_is_a_violation_under_every_policy(string? reported)
    {
        var role = Builders.Role(AgentRole.Reviewer, model: "model-b");
        var lenient = new QualityPolicy(QualityLock: false, Strict: false);

        var result = Verify(role, Reported(model: "model-b", sandbox: reported), lenient);

        Assert.NotEqual(VerificationStatus.Verified, Setting(result, ProfileSettings.Sandbox).Status);
        Assert.Contains(result.Violations, v => v.Contains("read-only", StringComparison.Ordinal));
    }

    [Fact]
    public void An_implementer_that_was_given_a_read_only_sandbox_cannot_do_its_work()
    {
        var result = Verify(Builders.Role(AgentRole.Implementer), Reported(sandbox: "read-only"));

        Assert.Equal(VerificationStatus.Mismatch, Setting(result, ProfileSettings.Sandbox).Status);
        Assert.Contains(result.Violations, v => v.Contains("cannot write", StringComparison.Ordinal));
    }

    [Fact]
    public void An_implementer_with_more_access_than_requested_is_a_violation()
    {
        var result = Verify(Builders.Role(AgentRole.Implementer), Reported(sandbox: "danger-full-access"));

        Assert.Equal(VerificationStatus.Mismatch, Setting(result, ProfileSettings.Sandbox).Status);
        Assert.Single(result.Violations);
    }

    [Fact]
    public void An_implementer_whose_agent_reports_no_sandbox_is_shown_as_unavailable_not_as_verified()
    {
        var result = Verify(Builders.Role(AgentRole.Implementer), Reported(sandbox: null));

        Assert.Equal(VerificationStatus.Unavailable, Setting(result, ProfileSettings.Sandbox).Status);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void A_paid_tier_that_was_not_requested_is_a_violation()
    {
        var result = Verify(Builders.Role(AgentRole.Implementer), Reported(tier: "priority"));

        var tier = Setting(result, ProfileSettings.ServiceTier);
        Assert.Equal(VerificationStatus.Mismatch, tier.Status);
        Assert.Equal("priority", tier.Effective);
        Assert.Contains(result.Violations, v => v.Contains("priority", StringComparison.Ordinal) && v.Contains("/speed", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("default")]
    [InlineData("auto")]
    public void Standard_serving_is_reported_in_more_than_one_way(string? reported)
    {
        var result = Verify(Builders.Role(AgentRole.Implementer), Reported(tier: reported));

        Assert.Empty(result.Violations);
        Assert.Equal(VerificationStatus.Verified, Setting(result, ProfileSettings.ServiceTier).Status);
    }

    [Fact]
    public void A_requested_tier_is_verified_only_when_it_is_reported_as_active()
    {
        var role = Builders.Role(AgentRole.Implementer) with { Speed = ProviderSpeedMode.Provider, ServiceTier = "priority" };

        var active = Verify(role, Reported(tier: "priority"));
        var fallback = Verify(role, Reported(tier: "default"));

        Assert.Empty(active.Violations);
        Assert.Equal(VerificationStatus.Verified, Setting(active, ProfileSettings.ServiceTier).Status);
        Assert.Equal(VerificationStatus.Mismatch, Setting(fallback, ProfileSettings.ServiceTier).Status);
        Assert.Single(fallback.Violations);
    }

    [Fact]
    public void An_api_key_in_use_although_the_subscription_was_shown_is_a_changed_billing_route()
    {
        var role = Builders.Role(AgentRole.Implementer) with { AccountRoute = AccountRouteKind.Subscription };

        var result = Verify(role, Reported(credential: "ANTHROPIC_API_KEY"));

        Assert.Equal(VerificationStatus.Mismatch, Setting(result, ProfileSettings.CredentialSource).Status);
        Assert.Contains(result.Violations, v => v.Contains("billing route", StringComparison.Ordinal));
    }

    [Fact]
    public void The_credential_source_that_belongs_to_the_route_is_verified()
    {
        var subscription = Builders.Role(AgentRole.Implementer) with { AccountRoute = AccountRouteKind.Subscription };
        var apiKey = Builders.Role(AgentRole.Implementer) with { AccountRoute = AccountRouteKind.ApiKey };

        Assert.Empty(Verify(subscription, Reported(credential: "none")).Violations);
        Assert.Empty(Verify(apiKey, Reported(credential: "ANTHROPIC_API_KEY")).Violations);
        Assert.Single(Verify(apiKey, Reported(credential: "none")).Violations);
        Assert.Equal(VerificationStatus.Verified, Setting(Verify(apiKey, Reported(credential: "ANTHROPIC_API_KEY")), ProfileSettings.CredentialSource).Status);
    }

    [Theory]
    [InlineData(AccountRouteKind.Subscription, "none")]
    [InlineData(AccountRouteKind.CloudProvider, "none")]
    [InlineData(AccountRouteKind.Unknown, "none")]
    [InlineData(AccountRouteKind.Unknown, "ANTHROPIC_API_KEY")]
    public void That_no_key_is_named_does_not_say_which_account_is_used(AccountRouteKind shown, string reported)
    {
        // Nothing contradicts what was shown, and nothing confirms it: a subscription, a token and a cloud provider all name no key.
        var role = Builders.Role(AgentRole.Implementer) with { AccountRoute = shown };

        var result = Verify(role, Reported(credential: reported));

        Assert.Empty(result.Violations);
        Assert.Equal(VerificationStatus.RequestedUnverified, Setting(result, ProfileSettings.CredentialSource).Status);
        Assert.Equal(reported, Setting(result, ProfileSettings.CredentialSource).Effective);
    }

    private static AccountSaid Said(AccountRouteKind? route, string description = "what the agent said", string? note = null) =>
        new(route, description, "initialize answer", note);

    [Theory]
    [InlineData(AccountRouteKind.Subscription, AccountRouteKind.Subscription, true)]
    [InlineData(AccountRouteKind.ApiKey, AccountRouteKind.ApiKey, true)]
    [InlineData(AccountRouteKind.CloudProvider, AccountRouteKind.CloudProvider, true)]
    [InlineData(AccountRouteKind.Subscription, AccountRouteKind.ApiKey, false)]
    [InlineData(AccountRouteKind.Subscription, AccountRouteKind.CloudProvider, false)]
    [InlineData(AccountRouteKind.ApiKey, AccountRouteKind.Subscription, false)]
    [InlineData(AccountRouteKind.CloudProvider, AccountRouteKind.Subscription, false)]
    [InlineData(AccountRouteKind.Gateway, AccountRouteKind.CloudProvider, false)]
    public void The_account_a_conversation_works_with_is_compared_with_the_route_that_was_shown(AccountRouteKind shown, AccountRouteKind said, bool same)
    {
        var role = Builders.Role(AgentRole.Implementer) with { AccountRoute = shown, AccountRouteLabel = "the route that was shown" };

        var result = Verify(role, Reported() with { Account = Said(said, "the route that is used") });

        var row = Setting(result, ProfileSettings.CredentialSource);
        Assert.Equal(same ? VerificationStatus.Verified : VerificationStatus.Mismatch, row.Status);
        Assert.Equal("the route that was shown", row.Requested);
        Assert.Equal("the route that is used", row.Effective);
        Assert.Equal("initialize answer", row.Source);
        if (same)
        {
            Assert.Empty(result.Violations);
        }
        else
        {
            var violation = Assert.Single(result.Violations);
            Assert.Contains("the route that was shown", violation, StringComparison.Ordinal);
            Assert.Contains("the route that is used", violation, StringComparison.Ordinal);
            Assert.Contains("billing route", violation, StringComparison.Ordinal);
        }
    }

    [Theory]
    [InlineData(true, true)]
    [InlineData(true, false)]
    [InlineData(false, false)]
    public void A_billing_route_other_than_the_one_that_was_shown_is_not_used_under_any_policy(bool qualityLock, bool strict)
    {
        // What is paid and to whom is not a matter of quality: nobody agreed to the other route.
        var role = Builders.Role(AgentRole.Implementer) with { AccountRoute = AccountRouteKind.Subscription };
        var policy = new QualityPolicy(QualityLock: qualityLock, Strict: strict);

        var byAccount = Verify(role, Reported() with { Account = Said(AccountRouteKind.ApiKey) }, policy);
        var byKey = Verify(role, Reported(credential: "ANTHROPIC_API_KEY"), policy);

        Assert.Contains("billing route", Assert.Single(byAccount.Violations), StringComparison.Ordinal);
        Assert.Contains("billing route", Assert.Single(byKey.Violations), StringComparison.Ordinal);
    }

    [Fact]
    public void What_the_user_has_to_know_about_the_other_route_is_said_with_it()
    {
        var role = Builders.Role(AgentRole.Reviewer, model: "model-b") with { AccountRoute = AccountRouteKind.ApiKey };
        var account = Said(AccountRouteKind.Subscription, note: "A review is started without the settings of the user.");

        var result = Verify(role, Reported(model: "model-b", sandbox: "read-only") with { Account = account });

        Assert.EndsWith("A review is started without the settings of the user.", Assert.Single(result.Violations), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AccountRouteKind.Subscription, null)]
    [InlineData(AccountRouteKind.Unknown, AccountRouteKind.Subscription)]
    [InlineData(AccountRouteKind.Unknown, null)]
    public void An_account_that_cannot_be_compared_is_shown_and_is_not_called_verified(AccountRouteKind shown, AccountRouteKind? said)
    {
        // Either what was shown was not a route YAV knows, or what the conversation says names none.
        var role = Builders.Role(AgentRole.Implementer) with { AccountRoute = shown };

        var result = Verify(role, Reported() with { Account = Said(said, "token (ANTHROPIC_AUTH_TOKEN)") });

        var row = Setting(result, ProfileSettings.CredentialSource);
        Assert.Equal(VerificationStatus.RequestedUnverified, row.Status);
        Assert.Equal("token (ANTHROPIC_AUTH_TOKEN)", row.Effective);
        Assert.Empty(result.Violations);
    }

    [Fact]
    public void What_the_conversation_says_about_the_account_counts_for_more_than_the_name_of_a_key()
    {
        // The start of a turn names no key for a cloud provider either. The account says what is used.
        var role = Builders.Role(AgentRole.Implementer) with { AccountRoute = AccountRouteKind.Subscription };

        var result = Verify(role, Reported(credential: "none") with { Account = Said(AccountRouteKind.CloudProvider, "cloud provider (bedrock)") });

        Assert.Equal(VerificationStatus.Mismatch, Setting(result, ProfileSettings.CredentialSource).Status);
        Assert.Contains("cloud provider (bedrock)", Assert.Single(result.Violations), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AccountRouteKind.Subscription, 0)]
    [InlineData(AccountRouteKind.ApiKey, 1)]
    public void Before_a_turn_the_account_is_compared_so_that_nothing_is_sent_over_another_route(AccountRouteKind said, int violations)
    {
        var role = Builders.Role(AgentRole.Reviewer, model: "model-b", effort: "xhigh") with { AccountRoute = AccountRouteKind.Subscription };
        var early = new EarlySettings(null, "xhigh", "get_settings answer", Account: Said(said));

        var result = SettingsVerifier.VerifyBeforeTurn(role, new QualityPolicy(QualityLock: false, Strict: false), early, Builders.Now);

        Assert.Equal(violations, result.Violations.Count);
        Assert.Equal(violations == 0 ? VerificationStatus.Verified : VerificationStatus.Mismatch, Setting(result, ProfileSettings.CredentialSource).Status);
    }

    [Theory]
    [InlineData(@"C:\ws\run-1", @"C:\ws\run-1", true)]
    [InlineData(@"C:\ws\run-1", @"c:\WS\Run-1\", true)]
    [InlineData(@"C:\ws\run-1", @"C:/ws/run-1", true)]
    [InlineData(@"C:\ws\run-1", @"C:\ws\run-1\src\..", true)]
    [InlineData(@"C:\ws\run-1", @"C:\ws\run-2", false)]
    [InlineData(@"C:\ws\run-1", @"C:\ws\run-1\src", false)]
    [InlineData(@"C:\ws\run-1", @"C:\ws", false)]
    [InlineData(@"C:\ws\run-1", @"C:\ws\run-10", false)]
    [InlineData(@"C:\ws\run-1", "not a path: \0", false)]
    public void The_directory_an_agent_works_in_is_compared_with_the_workspace_it_was_started_in(string workspace, string reported, bool same)
    {
        var effective = Reported() with { WorkingDirectory = reported };

        // What the agent writes is the candidate only when it writes it into the workspace: whatever the policy says.
        var result = SettingsVerifier.Verify(
            Builders.Role(AgentRole.Implementer), new QualityPolicy(QualityLock: false, Strict: false), effective, Builders.Now, workspace);

        var row = Setting(result, ProfileSettings.WorkingDirectory);
        Assert.Equal(same ? VerificationStatus.Verified : VerificationStatus.Mismatch, row.Status);
        Assert.Equal(workspace, row.Requested);
        Assert.Equal(reported, row.Effective);
        if (same)
        {
            Assert.Empty(result.Violations);
        }
        else
        {
            var violation = Assert.Single(result.Violations);
            Assert.Contains(workspace, violation, StringComparison.Ordinal);
            Assert.Contains("Model A", violation, StringComparison.Ordinal);
            Assert.Contains("would not be the candidate", violation, StringComparison.Ordinal);
        }
    }

    [ShortNamesFact]
    public void A_directory_that_is_named_by_its_short_name_is_the_same_directory()
    {
        // Windows keeps a short name for a name that is long or contains a blank, and programs report either.
        using var directory = new TempDirectory("a long name with blanks");
        var workspace = Directory.CreateDirectory(Path.Combine(directory.Path, "the workspace of a run")).FullName;
        var (longName, shortName) = (WindowsNames.Long(workspace), WindowsNames.Short(workspace));

        var byShortName = SettingsVerifier.Verify(
            Builders.Role(AgentRole.Implementer), new QualityPolicy(), Reported() with { WorkingDirectory = shortName }, Builders.Now, longName);
        var byLongName = SettingsVerifier.Verify(
            Builders.Role(AgentRole.Implementer), new QualityPolicy(), Reported() with { WorkingDirectory = longName }, Builders.Now, shortName);

        Assert.Empty(byShortName.Violations);
        Assert.Empty(byLongName.Violations);
        Assert.Equal(VerificationStatus.Verified, Setting(byShortName, ProfileSettings.WorkingDirectory).Status);
    }

    [Fact]
    public void An_agent_that_does_not_say_where_it_works_is_not_said_to_work_in_the_workspace()
    {
        var silent = SettingsVerifier.Verify(
            Builders.Role(AgentRole.Implementer), new QualityPolicy(), Reported() with { WorkingDirectory = null }, Builders.Now, @"C:\ws\run-1");

        Assert.Empty(silent.Violations);
        Assert.Equal(VerificationStatus.RequestedUnverified, Setting(silent, ProfileSettings.WorkingDirectory).Status);
    }

    [Fact]
    public void Where_no_workspace_is_named_nothing_is_said_about_the_directory()
    {
        var result = Verify(Builders.Role(AgentRole.Implementer), Reported());

        Assert.DoesNotContain(result.Confirmations, c => c.Setting == ProfileSettings.WorkingDirectory);
    }

    [Fact]
    public void An_agent_that_cannot_report_effort_leaves_it_unverified()
    {
        var result = Verify(Builders.Role(AgentRole.Implementer), Reported(model: null, effort: null));

        Assert.Equal(VerificationStatus.RequestedUnverified, Setting(result, ProfileSettings.Model).Status);
        Assert.Equal(VerificationStatus.RequestedUnverified, Setting(result, ProfileSettings.Effort).Status);
        Assert.Equal(2, result.Violations.Count);
    }
}
