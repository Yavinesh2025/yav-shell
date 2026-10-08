using System.Text.Json.Nodes;
using Yav.Adapters;
using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Runs;
using Yav.Platform.Processes;
using Yav.Tests.Support;

namespace Yav.Tests.Adapters;

public class ClaudeDetectionTests
{
    [Fact]
    public async Task The_installed_agent_is_found_with_its_version()
    {
        using var fixture = new AgentFixture();
        await using var adapter = fixture.ClaudeCli();

        var detection = await adapter.DetectAsync(CancellationToken.None);

        Assert.True(detection.Found);
        Assert.Equal("2.1.284", detection.Version);
        Assert.Equal(AdapterMaturity.Stable, detection.Maturity);
        Assert.True(detection.VersionTested);
    }

    [Theory]
    [InlineData("2.1.284 (Claude Code)")]
    [InlineData("2.1.285 (Claude Code)")]
    public async Task The_releases_yav_was_tried_with_are_called_tested(string version)
    {
        using var fixture = new AgentFixture().Claude(c => c["version"] = version);
        await using var adapter = fixture.ClaudeCli();

        var detection = await adapter.DetectAsync(CancellationToken.None);

        Assert.True(detection.VersionTested);
        Assert.Equal("2.1.284, 2.1.285", detection.TestedVersions);
    }

    [Theory]
    [InlineData("2.1.286 (Claude Code)")]
    [InlineData("2.1.259 (Claude Code)")]
    [InlineData("2.2.0 (Claude Code)")]
    [InlineData("3.0.0 (Claude Code)")]
    public async Task A_version_other_than_the_ones_yav_was_tested_with_can_be_used_and_is_not_called_tested(string version)
    {
        // Claude Code changes what it says from one release to the next. What was tried is two releases.
        using var fixture = new AgentFixture().Claude(c => c["version"] = version);
        await using var adapter = fixture.ClaudeCli();

        var detection = await adapter.DetectAsync(CancellationToken.None);

        Assert.True(detection.Usable);
        Assert.False(detection.VersionTested);
        Assert.Equal("2.1.284, 2.1.285", detection.TestedVersions);
    }

    [Fact]
    public async Task A_version_too_old_for_the_flags_yav_relies_on_is_a_problem()
    {
        using var fixture = new AgentFixture().Claude(c => c["version"] = "2.1.100 (Claude Code)");
        await using var adapter = fixture.ClaudeCli();

        var detection = await adapter.DetectAsync(CancellationToken.None);

        Assert.False(detection.Usable);
        Assert.Contains(detection.Problems, p => p.Contains("2.1.259"));
    }

    [Fact]
    public async Task A_subscription_sign_in_is_reported_with_the_providers_policy_and_needs_acknowledgement()
    {
        using var fixture = new AgentFixture();
        await using var adapter = fixture.ClaudeCli();

        var auth = await adapter.GetAuthStatusAsync(CancellationToken.None);

        Assert.True(auth.Authenticated);
        Assert.Equal(AccountRouteKind.Subscription, auth.Route);
        Assert.Equal("max", auth.PlanType);
        Assert.Equal(BillingKind.IncludedInSubscription, auth.Billing);
        Assert.Equal(RoutePolicy.RequiresAcknowledgement, auth.Policy);
        Assert.Contains("code.claude.com/docs/en/legal-and-compliance", auth.PolicyNote);
        Assert.DoesNotContain("user@example.invalid", auth.RouteLabel + auth.PolicyNote);
    }

    [Fact]
    public async Task A_key_held_by_yav_makes_the_route_a_separately_billed_api_key()
    {
        using var fixture = new AgentFixture();
        await using var adapter = fixture.ClaudeCli(() => "sk-ant-test-0123456789");

        var auth = await adapter.GetAuthStatusAsync(CancellationToken.None);

        Assert.Equal(AccountRouteKind.ApiKey, auth.Route);
        Assert.Equal(BillingKind.PayPerToken, auth.Billing);
        Assert.DoesNotContain("sk-ant-test", auth.RouteLabel + auth.PolicyNote + auth.Source);
    }

    [Theory]

    // Signed in to claude.ai: a subscription.
    [InlineData("claude.ai", "max", null, AccountRouteKind.Subscription, BillingKind.IncludedInSubscription)]

    // Signed in to the Claude Console: Claude Code calls that "claude.ai" as well, but it works with a key it
    // was given there, and what is used is billed per token.
    [InlineData("claude.ai", null, "/login managed key", AccountRouteKind.ApiKey, BillingKind.PayPerToken)]
    [InlineData("api_key", null, "ANTHROPIC_API_KEY", AccountRouteKind.ApiKey, BillingKind.PayPerToken)]
    [InlineData("api_key_helper", null, "apiKeyHelper", AccountRouteKind.ApiKey, BillingKind.PayPerToken)]

    // A token in the environment. With a plan it belongs to a subscription; without one nobody can say.
    [InlineData("oauth_token", "pro", null, AccountRouteKind.Subscription, BillingKind.IncludedInSubscription)]
    [InlineData("oauth_token", null, null, AccountRouteKind.Unknown, BillingKind.Unknown)]
    [InlineData("something new", null, null, AccountRouteKind.Unknown, BillingKind.Unknown)]
    public async Task The_route_is_what_claude_code_works_with_and_not_what_its_sign_in_is_called(
        string method, string? plan, string? keySource, AccountRouteKind route, BillingKind billing)
    {
        using var fixture = new AgentFixture().Claude(c => c["auth"] = new JsonObject
        {
            ["loggedIn"] = true, ["authMethod"] = method, ["apiProvider"] = "firstParty", ["subscriptionType"] = plan, ["apiKeySource"] = keySource,
        });
        await using var adapter = fixture.ClaudeCli();

        var auth = await adapter.GetAuthStatusAsync(CancellationToken.None);

        Assert.True(auth.Authenticated);
        Assert.Equal(route, auth.Route);
        Assert.Equal(billing, auth.Billing);
        Assert.Equal(RoutePolicy.RequiresAcknowledgement, auth.Policy);
        Assert.Equal(plan, auth.PlanType);
    }

    [Fact]
    public async Task A_cloud_provider_is_its_own_route()
    {
        using var fixture = new AgentFixture().Claude(c => c["auth"] = new JsonObject { ["loggedIn"] = true, ["apiProvider"] = "bedrock" });
        await using var adapter = fixture.ClaudeCli();

        var auth = await adapter.GetAuthStatusAsync(CancellationToken.None);

        Assert.Equal(AccountRouteKind.CloudProvider, auth.Route);
        Assert.Equal(BillingKind.CloudProviderBilled, auth.Billing);
        Assert.Equal("bedrock", auth.Backend);
    }

    [Fact]
    public async Task Signed_out_is_reported_as_signed_out()
    {
        using var fixture = new AgentFixture().Claude(c => c["auth"] = new JsonObject { ["loggedIn"] = false });
        await using var adapter = fixture.ClaudeCli();

        var auth = await adapter.GetAuthStatusAsync(CancellationToken.None);

        Assert.False(auth.Authenticated);
        Assert.Equal(AccountRouteKind.NotAuthenticated, auth.Route);
    }

    [Fact]
    public async Task Models_are_listed_without_sending_a_prompt()
    {
        using var fixture = new AgentFixture();
        await using var adapter = fixture.ClaudeCli();

        var models = await adapter.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["opus", "sonnet"], models.Select(m => m.Id));
        Assert.Equal("claude-opus-5-5", models[0].ResolvedModelId);
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], models[0].SupportedEfforts);
        Assert.True(Assert.Single(models[0].ServiceTiers).Faster);
        Assert.Empty(models[1].ServiceTiers);
        Assert.DoesNotContain(fixture.Received("claude.input"), m => m["type"]!.GetValue<string>() == "user");
    }
}

public class ClaudeSessionTests
{
    private static List<string> Arguments(AgentFixture fixture, int index = 0) =>
        fixture.Received("claude.invocation")[index]["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();

    private static string ValueOf(List<string> arguments, string flag) => arguments[arguments.IndexOf(flag) + 1];

    [Fact]
    public async Task The_reviewer_has_no_tool_that_can_change_anything()
    {
        using var fixture = new AgentFixture().ReviewerTurn(Step.Review("pass"));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer, model: "opus", effort: "max"), CancellationToken.None);

        await session.RunTurnAsync("review", ReviewSchema.AsElement());

        var arguments = Arguments(fixture);
        Assert.Contains("--restricted", arguments);
        Assert.Equal("Read,Glob,Grep", ValueOf(arguments, "--tools"));
        Assert.Equal("mcp__*", ValueOf(arguments, "--disallowedTools"));
        Assert.Contains("--strict-mcp-config", arguments);
        Assert.Equal("dontAsk", ValueOf(arguments, "--permission-mode"));
        Assert.Equal("none", ValueOf(arguments, "--permission-prompts"));
        Assert.Equal("max", ValueOf(arguments, "--effort"));
        Assert.Equal("opus", ValueOf(arguments, "--model"));
        Assert.Contains("--json-schema", arguments);
        Assert.DoesNotContain("--dangerously-skip-permissions", arguments);
        Assert.DoesNotContain("--permission-prompt-tool", arguments);
        Assert.DoesNotContain(arguments, a => a.Contains("bypassPermissions"));
    }

    [Fact]
    public async Task The_implementer_edits_freely_and_asks_the_user_for_everything_else()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, model: "opus", effort: "max"), CancellationToken.None);

        await session.RunTurnAsync("go");

        var arguments = Arguments(fixture);
        Assert.Equal("acceptEdits", ValueOf(arguments, "--permission-mode"));
        Assert.Equal("stdio", ValueOf(arguments, "--permission-prompt-tool"));
        Assert.Equal("AskUserQuestion", ValueOf(arguments, "--disallowedTools"));
        Assert.DoesNotContain("--restricted", arguments);
        Assert.DoesNotContain("--dangerously-skip-permissions", arguments);
        Assert.Equal("stream-json", ValueOf(arguments, "--output-format"));
        Assert.Equal("stream-json", ValueOf(arguments, "--input-format"));
        Assert.Contains("--verbose", arguments);
        Assert.Contains("-p", arguments);
    }

    [Theory]
    [InlineData(true, "user,project,local")]
    [InlineData(false, "user")]
    public async Task Settings_the_repository_controls_are_loaded_only_for_a_trusted_project(bool trusted, string expectedSources)
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, trusted: trusted), CancellationToken.None);

        await session.RunTurnAsync("go");

        var arguments = Arguments(fixture);
        Assert.Equal(expectedSources, ValueOf(arguments, "--setting-sources"));
        Assert.Equal(!trusted, arguments.Contains("--strict-mcp-config"));
    }

    [Fact]
    public async Task The_role_instructions_are_appended_and_the_agents_own_prompt_is_kept()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, instructions: "ROLE INSTRUCTIONS\nsecond line"), CancellationToken.None);

        await session.RunTurnAsync("go");

        var arguments = Arguments(fixture);
        var file = ValueOf(arguments, "--append-system-prompt-file");
        Assert.DoesNotContain("--system-prompt", arguments);
        Assert.DoesNotContain("--system-prompt-file", arguments);
        Assert.False(file.StartsWith(fixture.Workspace, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task The_prompt_travels_over_standard_input_exactly_as_written()
    {
        const string Prompt = "Fix the login bug.\r\nKeep \"quotes\", %PATH% and ünï 日本 exactly.\n";
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        await session.RunTurnAsync(Prompt);

        var message = Assert.Single(fixture.Received("claude.input"), m => m["type"]!.GetValue<string>() == "user");
        Assert.Equal("user", message["message"]!["role"]!.GetValue<string>());
        Assert.Equal(Prompt, message["message"]!["content"]!.GetValue<string>());
        Assert.DoesNotContain(Arguments(fixture), a => a.Contains("login bug"));
    }

    [Fact]
    public async Task What_the_agent_reports_at_the_start_of_the_turn_is_the_effective_configuration()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, model: "opus", effort: "max"), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var configured = Assert.Single(events.OfType<SessionConfigured>());
        Assert.Equal("opus", configured.Effective.Model);
        Assert.Equal("max", configured.Effective.Effort);
        Assert.Equal("acceptEdits", configured.Effective.ApprovalPolicy);
        Assert.Equal("none", configured.Effective.CredentialSource);
        Assert.Equal("2.1.284", configured.Effective.AgentVersion);
        Assert.Contains("Bash", configured.Effective.Tools);
        Assert.Equal(session.SessionId, configured.SessionId);
    }

    [Fact]
    public async Task An_effort_the_agent_lowered_is_reported_as_it_is()
    {
        // Claude Code falls back to the highest level a model supports; its answer to get_settings says what is really used.
        using var fixture = new AgentFixture().Claude(c => c["effectiveEffort"] = "high").ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, effort: "max"), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        Assert.Equal("high", Assert.Single(events.OfType<SessionConfigured>()).Effective.Effort);
        Assert.Equal("max", Arguments(fixture)[Arguments(fixture).IndexOf("--effort") + 1]);
    }

    [Fact]
    public async Task The_tools_the_reviewer_really_has_are_reported()
    {
        using var fixture = new AgentFixture().ReviewerTurn(Step.Review("pass"));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer), CancellationToken.None);

        var events = await session.RunTurnAsync("review", ReviewSchema.AsElement());

        // The fourth is the tool the result is handed back with. It is there because a schema was given.
        var effective = Assert.Single(events.OfType<SessionConfigured>()).Effective;
        Assert.Equal(["Glob", "Grep", "Read", "StructuredOutput"], effective.Tools);
        Assert.Equal("read-only", effective.Sandbox);
    }

    [Theory]
    [InlineData("Bash")]
    [InlineData("PowerShell")]
    [InlineData("Write")]
    [InlineData("Edit")]
    [InlineData("NotebookEdit")]
    [InlineData("WebFetch")]
    [InlineData("Task")]
    [InlineData("mcp__files__write")]
    [InlineData("A tool of a later version")]
    public async Task A_single_tool_that_is_not_known_to_be_harmless_leaves_the_boundary_unconfirmed(string tool)
    {
        using var fixture = new AgentFixture()
            .Claude(c => c["additionalTools"] = new JsonArray { tool })
            .ReviewerTurn(Step.Review("pass"));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer), CancellationToken.None);

        var events = await session.RunTurnAsync("review", ReviewSchema.AsElement());

        var effective = Assert.Single(events.OfType<SessionConfigured>()).Effective;
        Assert.Contains(tool, effective.Tools);
        Assert.Null(effective.Sandbox);
    }

    [Fact]
    public async Task The_settings_in_effect_are_asked_for_before_the_prompt_is_sent_and_before_every_turn()
    {
        using var fixture = new AgentFixture()
            .ImplementerTurn(Step.Message("One."))
            .ImplementerTurn(Step.Message("Two."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, model: "opus", effort: "max"), CancellationToken.None);

        await session.RunTurnAsync("first");
        await session.RunTurnAsync("second");

        var received = fixture.Received("claude.input")
            .Select(m => m["type"]!.GetValue<string>() == "user" ? "prompt" : m["request"]?["subtype"]?.GetValue<string>())
            .ToList();
        Assert.Equal(["get_settings", "initialize", "prompt", "get_settings", "initialize", "prompt"], received);
        Assert.Single(fixture.Received("claude.invocation"));
    }

    [Fact]
    public async Task The_account_the_conversation_works_with_is_reported_without_who_the_user_is()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, model: "opus", effort: "max"), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var account = Assert.Single(events.OfType<SessionConfigured>()).Effective.Account;
        Assert.NotNull(account);
        Assert.Equal(AccountRouteKind.Subscription, account.Route);
        Assert.Equal("subscription (Claude Max)", account.Description);
        Assert.Equal("initialize answer", account.Source);
        Assert.Null(account.Note);

        // The answer names the address and the organization of the user. Neither is passed on.
        Assert.Contains(fixture.Received("claude.output.account"), m => m["email"]!.GetValue<string>() == "user@example.invalid");
        Assert.DoesNotContain(events, e => e.ToString()!.Contains("example.invalid", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain("Example", account.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("""{"apiProvider":"firstParty","email":"user@example.invalid","organization":"Example","subscriptionType":"Claude Pro"}""", AccountRouteKind.Subscription, "subscription (Claude Pro)")]
    [InlineData("""{"apiKeySource":"ANTHROPIC_API_KEY","apiProvider":"firstParty","tokenSource":"claude.ai"}""", AccountRouteKind.ApiKey, "API key (ANTHROPIC_API_KEY)")]
    [InlineData("""{"apiKeySource":"none","apiProvider":"firstParty","subscriptionType":"Claude Max"}""", AccountRouteKind.Subscription, "subscription (Claude Max)")]
    [InlineData("""{"apiProvider":"firstParty","tokenSource":"ANTHROPIC_AUTH_TOKEN"}""", null, "token (ANTHROPIC_AUTH_TOKEN)")]
    [InlineData("""{"apiProvider":"bedrock"}""", AccountRouteKind.CloudProvider, "cloud provider (bedrock)")]
    [InlineData("""{"apiProvider":"bedrock","subscriptionType":"Claude Max"}""", AccountRouteKind.CloudProvider, "cloud provider (bedrock)")]
    [InlineData("""{"apiProvider":"gateway"}""", AccountRouteKind.Gateway, "gateway")]
    [InlineData("""{"apiProvider":"firstParty"}""", null, "an account it says nothing more about")]
    [InlineData("""{}""", null, "an account it says nothing more about")]
    public async Task The_route_follows_from_what_the_conversation_says_it_works_with(string said, AccountRouteKind? route, string description)
    {
        // The first five are what Claude Code 2.1.284 said when it was started in these ways.
        using var fixture = new AgentFixture()
            .Claude(c => c["sessionAccount"] = JsonNode.Parse(said))
            .ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, model: "opus", effort: "max"), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var account = Assert.Single(events.OfType<SessionConfigured>()).Effective.Account;
        Assert.NotNull(account);
        Assert.Equal(route, account.Route);
        Assert.Equal(description, account.Description);
    }

    [Theory]
    [InlineData(AgentRole.Reviewer, true)]
    [InlineData(AgentRole.Implementer, false)]
    public async Task That_a_review_is_started_without_the_settings_of_the_user_is_said_with_its_account(AgentRole role, bool noted)
    {
        // A key or a provider that is set in the settings of the user is what was shown before the run, and not what a review works with.
        using var fixture = new AgentFixture().ReviewerTurn(Step.Review("pass")).ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(role, model: "opus", effort: "max"), CancellationToken.None);

        var events = await session.RunTurnAsync("go", role == AgentRole.Reviewer ? ReviewSchema.AsElement() : null);

        var account = Assert.Single(events.OfType<SessionConfigured>()).Effective.Account;
        Assert.NotNull(account);
        Assert.Equal(noted, account.Note?.Contains("without the settings of the user", StringComparison.Ordinal) ?? false);
    }

    [Theory]
    [InlineData("unsupported", "Unsupported control request subtype: initialize")]
    [InlineData("withoutAccount", "its answer names no account")]
    [InlineData("silent", "no answer within 2 seconds")]
    public async Task A_version_that_does_not_say_which_account_it_works_with_is_not_taken_for_one_that_said_it(string behaviour, string reason)
    {
        using var fixture = new AgentFixture()
            .Claude(c => c["accountRequest"] = behaviour)
            .ImplementerTurn(Step.Message("One."))
            .ImplementerTurn(Step.Message("Two."));
        await using var adapter = fixture.ClaudeCli(startupTimeout: TimeSpan.FromSeconds(2));
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, model: "opus", effort: "max"), CancellationToken.None);

        var first = await session.RunTurnAsync("first");
        var second = await session.RunTurnAsync("second");

        var effective = Assert.Single(first.OfType<SessionConfigured>()).Effective;
        Assert.Null(effective.Account);

        // What was said is not lost with what was not.
        Assert.Equal("max", effective.Effort);
        Assert.Equal("none", effective.CredentialSource);
        Assert.Equal(TurnOutcome.Completed, first.Completion().Outcome);
        var notice = Assert.Single(first.OfType<AgentNotice>(), n => n.IsWarning);
        Assert.Contains("which account", notice.Message, StringComparison.Ordinal);
        Assert.Contains(reason, notice.Message, StringComparison.Ordinal);

        // The same process is not asked again, so that no turn waits for nothing and nothing is said twice.
        Assert.Single(fixture.Received("claude.input"), m => m["request"]?["subtype"]?.GetValue<string>() == "initialize");
        Assert.DoesNotContain(second.OfType<AgentNotice>(), n => n.IsWarning);
        Assert.Null(Assert.Single(second.OfType<SessionConfigured>()).Effective.Account);
    }

    [Fact]
    public async Task The_effort_is_said_to_come_from_the_answer_and_the_rest_from_the_start_of_the_turn()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, model: "opus", effort: "max"), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var effective = Assert.Single(events.OfType<SessionConfigured>()).Effective;
        Assert.Equal("init message", effective.Source);
        Assert.Equal("get_settings answer", effective.EffortSource);
    }

    [Fact]
    public async Task A_model_that_takes_no_effort_is_reported_as_that_and_not_as_unknown()
    {
        // What was asked for is not in effect. That is known, which is more than "not reported".
        using var fixture = new AgentFixture().Claude(c => c["modelTakesNoEffort"] = true).ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, model: "haiku", effort: "max"), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        Assert.Equal("none", Assert.Single(events.OfType<SessionConfigured>()).Effective.Effort);
        Assert.Equal(TurnOutcome.Completed, events.Completion().Outcome);
    }

    [Fact]
    public async Task A_version_that_does_not_know_the_question_leaves_the_effort_unreported()
    {
        using var fixture = new AgentFixture().Claude(c => c["settingsRequest"] = "unsupported").ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, model: "opus", effort: "max"), CancellationToken.None);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var events = await session.RunTurnAsync("go");

        // The answer that says "I do not know this" is an answer: nothing is waited for.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the turn took {watch.Elapsed}");
        var effective = Assert.Single(events.OfType<SessionConfigured>()).Effective;
        Assert.Null(effective.Effort);
        Assert.Equal("get_settings (not answered)", effective.EffortSource);
        Assert.Equal("opus", effective.Model);
        Assert.Equal(TurnOutcome.Completed, events.Completion().Outcome);
        var notice = Assert.Single(events.OfType<AgentNotice>(), n => n.IsWarning);
        Assert.Contains("which effort is in effect", notice.Message, StringComparison.Ordinal);
        Assert.Contains("Unsupported control request subtype: get_settings", notice.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("silent")]
    public async Task An_agent_that_did_not_say_is_not_asked_again_by_the_same_process(string behaviour)
    {
        using var fixture = new AgentFixture()
            .Claude(c => c["settingsRequest"] = behaviour)
            .ImplementerTurn(Step.Message("One."))
            .ImplementerTurn(Step.Message("Two."));
        await using var adapter = fixture.ClaudeCli(startupTimeout: TimeSpan.FromSeconds(2));
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, model: "opus", effort: "max"), CancellationToken.None);
        var first = await session.RunTurnAsync("first");

        var second = await session.RunTurnAsync("second");

        // Asked once: so the second turn did not wait for an answer again.
        Assert.Single(fixture.Received("claude.input"), m => m["request"]?["subtype"]?.GetValue<string>() == "get_settings");
        Assert.Single(first.OfType<AgentNotice>(), n => n.IsWarning);
        Assert.DoesNotContain(second.OfType<AgentNotice>(), n => n.IsWarning);
        Assert.Null(Assert.Single(second.OfType<SessionConfigured>()).Effective.Effort);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("number")]
    public async Task An_answer_that_does_not_name_a_level_is_not_taken_for_no_effort(string answer)
    {
        // Only an explicit null says that no effort is sent. Anything else is an answer YAV cannot read.
        using var fixture = new AgentFixture().Claude(c => c["appliedEffort"] = answer).ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, model: "opus", effort: "max"), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var effective = Assert.Single(events.OfType<SessionConfigured>()).Effective;
        Assert.Null(effective.Effort);
        Assert.Equal("get_settings answer", effective.EffortSource);
    }

    [Fact]
    public async Task What_a_later_turn_reports_is_what_is_in_effect_then()
    {
        using var fixture = new AgentFixture()
            .Claude(c => c["effectiveEfforts"] = new JsonArray { "max", "high" })
            .ImplementerTurn(Step.Message("One."))
            .ImplementerTurn(Step.Message("Two."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, model: "opus", effort: "max"), CancellationToken.None);

        var first = await session.RunTurnAsync("first");
        var second = await session.RunTurnAsync("second");

        Assert.Equal("max", Assert.Single(first.OfType<SessionConfigured>()).Effective.Effort);
        Assert.Equal("high", Assert.Single(second.OfType<SessionConfigured>()).Effective.Effort);
        Assert.Equal("high", session.Effective!.Effort);
    }

    [Fact]
    public async Task An_answer_to_what_somebody_else_asked_is_not_taken_for_the_settings()
    {
        using var fixture = new AgentFixture().Claude(c => c["strayAnswer"] = true).ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, model: "opus", effort: "max"), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        Assert.Equal("max", Assert.Single(events.OfType<SessionConfigured>()).Effective.Effort);
    }

    [Theory]
    [InlineData("projectSettings")]
    [InlineData("localSettings")]
    public async Task Settings_of_the_workspace_that_were_loaded_for_a_review_leave_the_boundary_unconfirmed(string source)
    {
        // Settings can carry hooks, which are programs. The ones in the workspace are what Model A left there.
        using var fixture = new AgentFixture()
            .Claude(c => c["settingsSources"] = new JsonArray { "userSettings", source })
            .ReviewerTurn(Step.Review("pass"));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer), CancellationToken.None);

        var events = await session.RunTurnAsync("review", ReviewSchema.AsElement());

        Assert.Null(Assert.Single(events.OfType<SessionConfigured>()).Effective.Sandbox);
        var notice = Assert.Single(events.OfType<AgentNotice>(), n => n.IsWarning);
        Assert.Contains(source, notice.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Settings_of_the_user_or_of_the_command_line_do_not_stand_in_the_way_of_the_boundary()
    {
        using var fixture = new AgentFixture()
            .Claude(c => c["settingsSources"] = new JsonArray { "userSettings", "flagSettings", "policySettings" })
            .ReviewerTurn(Step.Review("pass"));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer), CancellationToken.None);

        var events = await session.RunTurnAsync("review", ReviewSchema.AsElement());

        Assert.Equal("read-only", Assert.Single(events.OfType<SessionConfigured>()).Effective.Sandbox);
    }

    [Theory]
    [InlineData("unsupported")]
    [InlineData("silent")]
    public async Task A_review_whose_agent_does_not_say_which_settings_it_loaded_has_no_confirmed_boundary(string behaviour)
    {
        using var fixture = new AgentFixture().Claude(c => c["settingsRequest"] = behaviour).ReviewerTurn(Step.Review("pass"));
        await using var adapter = fixture.ClaudeCli(startupTimeout: TimeSpan.FromSeconds(2));
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer), CancellationToken.None);

        var events = await session.RunTurnAsync("review", ReviewSchema.AsElement());

        Assert.Null(Assert.Single(events.OfType<SessionConfigured>()).Effective.Sandbox);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_reviewer_is_never_given_the_settings_of_the_workspace(bool trusted)
    {
        using var fixture = new AgentFixture().ReviewerTurn(Step.Review("pass"));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer, trusted: trusted), CancellationToken.None);

        await session.RunTurnAsync("review", ReviewSchema.AsElement());

        Assert.Equal("user", ValueOf(Arguments(fixture), "--setting-sources"));
    }

    [Fact]
    public async Task What_the_reviewer_is_sent_is_marked_as_composed_so_that_nothing_in_it_is_expanded()
    {
        // The candidate is quoted in it. Claude Code would take "@path" in it for a file to attach.
        using var fixture = new AgentFixture().ReviewerTurn(Step.Review("pass")).ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var reviewer = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer), CancellationToken.None);
        await using var implementer = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        await reviewer.RunTurnAsync("+ see @C:/Users/me/.ssh/id_rsa", ReviewSchema.AsElement());
        await implementer.RunTurnAsync("Change @src/app.txt as it says.");

        var sent = fixture.Received("claude.input").Where(m => m["type"]!.GetValue<string>() == "user").ToList();
        var review = Assert.Single(sent, m => m["message"]!["content"]!.GetValue<string>().Contains("id_rsa", StringComparison.Ordinal));
        var request = Assert.Single(sent, m => m["message"]!["content"]!.GetValue<string>().Contains("app.txt", StringComparison.Ordinal));
        Assert.True(review["client_composed"]!.GetValue<bool>());
        Assert.Null(request["client_composed"]);
    }

    [Fact]
    public async Task What_quotes_an_agent_or_a_tool_is_marked_as_composed_for_the_implementer_too()
    {
        // A repair quotes what the reviewer found and what the checks wrote. What the user wrote is left as it is.
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("One.")).ImplementerTurn(Step.Message("Two."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        await session.RunTurnAsync("Change @src/app.txt as it says.");
        await session.StartTurnAsync(new TurnRequest("The check wrote: see @C:/Users/me/.ssh/id_rsa", null, [], QuotesOutput: true), CancellationToken.None);
        await session.ReadTurnAsync(TimeSpan.FromSeconds(20));

        var sent = fixture.Received("claude.input").Where(m => m["type"]!.GetValue<string>() == "user").ToList();
        Assert.Equal(2, sent.Count);
        Assert.Null(sent[0]["client_composed"]);
        Assert.True(sent[1]["client_composed"]?.GetValue<bool>());
    }

    [Fact]
    public async Task A_claude_code_that_ended_after_it_was_asked_is_not_started_again_for_the_prompt()
    {
        // It said what it works with and ended before it was given the prompt. A second process would be given
        // the prompt without anybody having compared what it works with.
        using var fixture = new AgentFixture().Claude(c => c["endAfterQuestions"] = true).ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, model: "opus", effort: "max"), CancellationToken.None);
        var request = new TurnRequest("go", null, []);
        Assert.NotNull(await ((IReportsBeforeTurn)session).PrepareTurnAsync(request, CancellationToken.None));

        // Ended, and time for YAV to notice it: a process that ended is what this is about.
        var ended = Assert.Single(fixture.Received("claude.invocation"))["_pid"]!.GetValue<int>();
        for (var waited = 0; ProcessRunner.IsProcessAlive(ended, null); waited++)
        {
            Assert.True(waited < 300, "the stand-in did not end");
            await Task.Delay(100);
        }

        await Task.Delay(2_000);
        AgentProtocolException? refused = null;
        try
        {
            await session.StartTurnAsync(request, CancellationToken.None);
            Assert.Equal(TurnErrorCodes.AgentExited, (await session.ReadTurnAsync(TimeSpan.FromSeconds(20))).Completion().ErrorCode);
        }
        catch (AgentProtocolException ex)
        {
            refused = ex;
        }

        Assert.Single(fixture.Received("claude.invocation"));
        Assert.DoesNotContain(fixture.Received("claude.input"), m => m["type"]!.GetValue<string>() == "user");
        if (refused is not null)
        {
            // Where it had noticed the end in time, it says so; otherwise the turn ends as that of an agent that ended.
            Assert.Contains("after it said what it works with and before it was given the prompt. No prompt was sent.", refused.Message, StringComparison.Ordinal);
            Assert.Contains("ended after it was asked", refused.Message, StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task A_program_that_is_not_there_any_more_fails_the_turn_and_does_not_wait()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Done."));
        var gone = Path.Combine(fixture.Workspace, "was-here", "claude.exe");
        await using var adapter = new ClaudeCliAdapter(
            new Yav.Platform.Processes.ProcessRunner(), fixture.Clock, fixture.Options("claude") with { ExecutablePath = gone }, () => null);
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var failure = await Assert.ThrowsAsync<AgentProtocolException>(
            () => session.StartTurnAsync(new TurnRequest("go", null, []), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));

        Assert.Contains("was not found", failure.Message, StringComparison.Ordinal);

        // The session is not left believing that a turn is running.
        await Assert.ThrowsAsync<AgentProtocolException>(
            () => session.StartTurnAsync(new TurnRequest("again", null, []), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(10)));
    }

    [Fact]
    public async Task An_agent_that_asks_for_something_and_is_gone_ends_the_turn()
    {
        // The reviewer is answered at once, with no. When nobody reads the answer, the turn still has to end.
        using var fixture = new AgentFixture().Claude(c => c["deafAfterPrompt"] = true).ReviewerTurn(Step.AskAndLeave());
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer), CancellationToken.None);

        await session.StartTurnAsync(new TurnRequest("review", ReviewSchema.AsElement(), []), CancellationToken.None);
        var events = await session.ReadTurnAsync(TimeSpan.FromSeconds(20));

        Assert.Equal(TurnOutcome.Failed, events.Completion().Outcome);
        Assert.Equal(TurnErrorCodes.AgentExited, events.Completion().ErrorCode);
    }

    [Fact]
    public async Task An_agent_that_never_answers_the_question_holds_the_turn_up_no_longer_than_it_may_take_to_start()
    {
        using var fixture = new AgentFixture().Claude(c => c["settingsRequest"] = "silent").ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli(startupTimeout: TimeSpan.FromSeconds(2));
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, model: "opus", effort: "max"), CancellationToken.None);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var events = await session.RunTurnAsync("go");

        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(1.9), TimeSpan.FromSeconds(15));
        Assert.Null(Assert.Single(events.OfType<SessionConfigured>()).Effective.Effort);
        Assert.Equal(TurnOutcome.Completed, events.Completion().Outcome);
    }

    [Fact]
    public async Task A_faster_tier_is_switched_on_only_when_the_profile_asks_for_it()
    {
        using var fixture = new AgentFixture()
            .ImplementerTurn(Step.Message("One."))
            .ImplementerTurn(Step.Message("Two."));
        await using var adapter = fixture.ClaudeCli();
        await using (var standard = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None))
        {
            await standard.RunTurnAsync("go");
        }

        await using var faster = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, serviceTier: "fast"), CancellationToken.None);
        await faster.RunTurnAsync("go");

        Assert.DoesNotContain("--settings", Arguments(fixture, 0));
        Assert.Contains("\"fastMode\":true", Arguments(fixture, 1)[Arguments(fixture, 1).IndexOf("--settings") + 1]);
    }
}

public class ClaudeTurnTests
{
    [Fact]
    public async Task Text_tool_use_and_results_become_events()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.Command("npm test", "2 passing", 0),
            Step.Write("src/fix.txt", "fixed"),
            Step.Message("The bug is fixed."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        Assert.Equal("npm test", Assert.Single(events.OfType<CommandStarted>()).Command);
        var completed = Assert.Single(events.OfType<CommandCompleted>());
        Assert.Equal("2 passing", completed.Output);
        Assert.Equal("completed", completed.Status);
        var change = Assert.Single(Assert.Single(events.OfType<FilesChanged>()).Changes);
        Assert.Equal(fixture.InWorkspace("src/fix.txt"), change.Path, ignoreCase: true);
        Assert.Equal("fixed", File.ReadAllText(fixture.InWorkspace("src/fix.txt")));
        Assert.Equal("The bug is fixed.", Assert.Single(events.OfType<AssistantMessage>()).Text);
        Assert.Equal(TurnOutcome.Completed, events.Completion().Outcome);
        Assert.Equal("The bug is fixed.", events.Completion().FinalMessage);
    }

    [Fact]
    public async Task Hidden_reasoning_is_not_shown_or_stored()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Reasoning("private chain of thought"), Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        Assert.DoesNotContain(events.OfType<ReasoningSummary>(), r => r.Text.Contains("private chain"));
        Assert.DoesNotContain(events.OfType<AssistantMessage>(), m => m.Text.Contains("private chain"));
    }

    [Fact]
    public async Task The_structured_result_comes_from_the_field_the_agent_validated()
    {
        using var fixture = new AgentFixture().ReviewerTurn(Step.Review("changes_required", title: "Empty password is accepted"));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer), CancellationToken.None);

        var events = await session.RunTurnAsync("review", ReviewSchema.AsElement());

        var output = events.Completion().StructuredOutput!.Value;
        Assert.Equal("changes_required", output.GetProperty("status").GetString());
        Assert.Equal("Empty password is accepted", output.GetProperty("findings")[0].GetProperty("title").GetString());
    }

    [Fact]
    public async Task The_tool_a_result_is_handed_back_with_is_not_reported_as_work_of_the_agent()
    {
        using var fixture = new AgentFixture().ReviewerTurn(Step.Review("pass"));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer), CancellationToken.None);

        var events = await session.RunTurnAsync("review", ReviewSchema.AsElement());

        Assert.Empty(events.OfType<ToolActivity>());
        Assert.Equal("pass", events.Completion().StructuredOutput!.Value.GetProperty("status").GetString());
    }

    [Fact]
    public async Task A_tool_that_only_reads_is_reported_with_what_it_was_given_when_it_begins_and_when_it_ends()
    {
        using var fixture = new AgentFixture().ReviewerTurn(
            Step.Tool("Read", new JsonObject { ["file_path"] = "{cwd}/src/app.txt" }),
            Step.Tool("Grep", new JsonObject { ["pattern"] = "strip\\(", ["path"] = "{cwd}" }),
            Step.Tool("Glob", new JsonObject { ["pattern"] = "**/*.py" }),
            Step.Tool("Read", new JsonObject { ["file_path"] = "C:/elsewhere/notes.txt" }, fails: true),
            Step.Tool("TodoWrite", new JsonObject { ["todos"] = new JsonArray() }),
            Step.Review("pass"));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer), CancellationToken.None);

        var events = await session.RunTurnAsync("review", ReviewSchema.AsElement());

        // What is looked for and where are kept apart, and the path is the one the agent gave: where a path is
        // shown, it is shortened by who knows the workspace.
        var here = fixture.Workspace;
        Assert.Equal(
            [
                $"Read started:  | {here}/src/app.txt", $"Read completed:  | {here}/src/app.txt",
                $"Grep started: strip\\( | {here}", $"Grep completed: strip\\( | {here}",
                "Glob started: **/*.py | ", "Glob completed: **/*.py | ",
                "Read started:  | C:/elsewhere/notes.txt", "Read failed:  | C:/elsewhere/notes.txt",
                "TodoWrite started:  | ", "TodoWrite completed:  | ",
            ],
            events.OfType<ToolActivity>().Select(t => $"{t.Tool} {t.Status}: {t.Summary} | {t.Path}"));
    }

    [Theory]
    [InlineData(1.05, 105)]
    [InlineData(1.0, 100)]
    [InlineData(0.62, 62)]
    [InlineData(0.004, 0)]
    public async Task The_part_of_a_window_that_is_used_is_a_fraction_also_when_more_than_all_of_it_is_used(double utilization, int percent)
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.ClaudeRateLimit(utilization, "seven_day_opus", "allowed_warning"), Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var limits = Assert.Single(events.OfType<RateLimitUpdated>()).Snapshot;
        Assert.Equal(percent, limits.Primary!.UsedPercent);
        Assert.Equal(10_080, limits.Primary.WindowMinutes);
        Assert.Equal("seven_day_opus", limits.LimitName);
    }

    [Fact]
    public async Task Usage_counts_cache_tokens_separately_and_cost_is_marked_as_an_estimate()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var usage = Assert.Single(events.OfType<UsageUpdated>()).Usage;
        Assert.Equal(UsageScope.CumulativeForSession, usage.Scope);
        Assert.Equal(50, usage.Tokens.UncachedInput);
        Assert.Equal(900, usage.Tokens.CacheRead);
        Assert.Equal(100, usage.Tokens.CacheWrite);
        Assert.Equal(300, usage.Tokens.Output);
        Assert.Equal(120, usage.Tokens.ReasoningWithinOutput);
        Assert.Equal(1350, usage.Tokens.Total);
        Assert.Equal(0.25m, usage.ProviderCostUsd);
        Assert.Equal(ValueProvenance.Estimated, usage.CostProvenance);
    }

    [Fact]
    public async Task A_second_turn_uses_the_same_process_and_session()
    {
        using var fixture = new AgentFixture()
            .ImplementerTurn(Step.Message("First."))
            .ImplementerTurn(Step.Message("Second."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);
        await session.RunTurnAsync("one");

        var events = await session.RunTurnAsync("two");

        Assert.Equal("Second.", events.Completion().FinalMessage);
        Assert.Single(fixture.Received("claude.invocation"));
        var usage = Assert.Single(events.OfType<UsageUpdated>()).Usage;
        Assert.Equal(100, usage.Tokens.UncachedInput);
        Assert.Equal(0.5m, usage.ProviderCostUsd);
    }

    [Fact]
    public async Task A_saved_session_is_resumed_by_its_identifier()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Resumed."));
        await using var adapter = fixture.ClaudeCli();
        var saved = Guid.NewGuid().ToString();
        await using var session = await adapter.ResumeSessionAsync(saved, fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("continue");

        var arguments = fixture.Received("claude.invocation")[0]["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        Assert.Equal(saved, arguments[arguments.IndexOf("--resume") + 1]);
        Assert.DoesNotContain("--continue", arguments);
        Assert.DoesNotContain("--session-id", arguments);
        Assert.Equal(saved, session.SessionId);
        Assert.Equal("Resumed.", events.Completion().FinalMessage);
    }

    [Fact]
    public async Task A_retry_by_the_agent_is_reported_and_does_not_end_the_turn()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Retry(1, "overloaded"), Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var retry = Assert.Single(events.OfType<ProviderRetry>());
        Assert.Equal(1, retry.Attempt);
        Assert.Equal("overloaded", retry.Reason);
        Assert.Equal(TurnOutcome.Completed, events.Completion().Outcome);
    }

    [Theory]
    [InlineData("API Error: 429 rate limited", null, TurnOutcome.RateLimited)]
    [InlineData("You have reached your usage limit", null, TurnOutcome.UsageLimitReached)]
    [InlineData("You've hit your session limit · resets 3pm", null, TurnOutcome.UsageLimitReached)]
    [InlineData("You've hit your weekly limit", null, TurnOutcome.UsageLimitReached)]
    [InlineData("You've hit your Opus limit", null, TurnOutcome.UsageLimitReached)]
    [InlineData("API Error: 500 internal", null, TurnOutcome.Failed)]
    [InlineData("budget", "error_max_budget_usd", TurnOutcome.UsageLimitReached)]
    [InlineData("turns", "error_max_turns", TurnOutcome.Failed)]
    public async Task A_failed_turn_says_why(string message, string? subtype, TurnOutcome expected)
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Fail(message, subtype: subtype));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        Assert.Equal(expected, events.Completion().Outcome);
        Assert.Contains(message, events.Completion().ErrorMessage);
    }

    [Fact]
    public async Task Messages_this_version_does_not_know_and_broken_lines_are_skipped()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.UnknownMessage(), Step.Garbage(), Step.Message("Still here."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        Assert.Equal("Still here.", events.Completion().FinalMessage);
    }

    [Fact]
    public async Task An_agent_that_dies_ends_the_turn_as_failed_with_its_last_words()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Working..."), Step.Crash(9));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        Assert.Equal(TurnOutcome.Failed, events.Completion().Outcome);
        Assert.Contains("simulated crash", events.Completion().ErrorMessage);
    }

    [Fact]
    public async Task An_agent_that_cannot_start_reports_what_it_said()
    {
        using var fixture = new AgentFixture().Claude(c => c["startupFailure"] = "Invalid API key · Please run /login");
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var watch = System.Diagnostics.Stopwatch.StartNew();
        var events = await session.RunTurnAsync("go");

        // What was asked of it before the prompt is given up when it has ended, not when the time is up.
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), $"the turn took {watch.Elapsed}");
        Assert.Equal(TurnOutcome.Failed, events.Completion().Outcome);
        Assert.Contains("Invalid API key", events.Completion().ErrorMessage);
    }
}

public class ClaudeApprovalTests
{
    [Theory]
    [InlineData("defaultToNo", true)]
    [InlineData(null, false)]
    public async Task What_the_agent_marks_as_not_to_be_allowed_by_one_key_is_passed_on_with_that_mark(string? marked, bool deliberate)
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Approval("rm -rf build", marked: marked), Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        ApprovalRequest? asked = null;
        await session.RunTurnAsync("go", onEvent: async e =>
        {
            if (e is ApprovalRequested requested)
            {
                asked = requested.Request;
                await session.RespondToApprovalAsync(requested.Request.ApprovalId, ApprovalDecision.Decline, CancellationToken.None);
            }
        });

        Assert.Equal(deliberate, asked!.Deliberate);
    }

    [Fact]
    public async Task What_asks_the_user_something_that_cannot_be_shown_here_is_declined_and_said()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.Approval("which colour?", onAccept: [Step.Write("answered.txt", "yes")], onDecline: [Step.Message("Went on without.")], marked: "needsTheUser"));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        Assert.Empty(events.OfType<ApprovalRequested>());
        Assert.Single(events.OfType<AgentNotice>(), n => n.IsWarning && n.Message.Contains("asks the user something in a way YAV cannot show", StringComparison.Ordinal));
        var response = Assert.Single(fixture.Received("claude.input"), m => m["type"]!.GetValue<string>() == "control_response");
        Assert.Equal("deny", response["response"]!["response"]!["behavior"]!.GetValue<string>());
        Assert.False(response["response"]!["response"]!["interrupt"]!.GetValue<bool>());
        Assert.False(File.Exists(fixture.InWorkspace("answered.txt")));
        Assert.Equal(TurnOutcome.Completed, events.Completion().Outcome);
    }

    private static async Task<ApprovalRequest> AskedAsync(params JsonObject[] steps)
    {
        using var fixture = new AgentFixture().ImplementerTurn(steps);
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        ApprovalRequest? asked = null;
        await session.RunTurnAsync("go", onEvent: async e =>
        {
            if (e is ApprovalRequested requested)
            {
                asked = requested.Request;
                await session.RespondToApprovalAsync(requested.Request.ApprovalId, ApprovalDecision.Decline, CancellationToken.None);
            }
        });

        return asked!;
    }

    [Fact]
    public async Task Everything_a_tool_is_given_is_listed_when_the_agent_asks_to_use_it()
    {
        // What is sent away is in a field nobody would think of looking for. All of them are what is decided about.
        var asked = await AskedAsync(
            Step.ToolApproval("mcp__github__create_gist", new JsonObject
            {
                ["description"] = "notes",
                ["public"] = true,
                ["count"] = 3,
                ["files"] = new JsonObject { ["secrets.txt"] = new JsonObject { ["content"] = "the key is 1234" } },
                ["nothing"] = null,
            }),
            Step.Message("Done."));

        Assert.Equal(ApprovalKind.ToolUse, asked.Kind);
        Assert.Null(asked.Command);
        Assert.Equal("Use mcp__github__create_gist", asked.Title);
        Assert.Equal(
            ["description: notes", "public: true", "count: 3", """files: {"secrets.txt":{"content":"the key is 1234"}}""", "nothing: null"],
            asked.Details);
        Assert.False(asked.Deliberate);
    }

    [Fact]
    public async Task What_is_written_into_a_file_is_listed_line_by_line()
    {
        var asked = await AskedAsync(
            Step.ToolApproval("Write", new JsonObject { ["file_path"] = @"C:\elsewhere\notes.txt", ["content"] = "first line\r\nsecond line\n\nlast line" }),
            Step.Message("Done."));

        Assert.Equal(ApprovalKind.FileChange, asked.Kind);
        Assert.Equal(
            [@"file_path: C:\elsewhere\notes.txt", "content:", "  first line", "  second line", "  ", "  last line"],
            asked.Details);
    }

    [Fact]
    public async Task What_a_shell_is_given_beside_its_command_is_listed_too()
    {
        var asked = await AskedAsync(
            Step.ToolApproval("Bash", new JsonObject
            {
                ["command"] = "npm publish",
                ["description"] = "Lists the files",
                ["run_in_background"] = true,
            }),
            Step.Message("Done."));

        Assert.Equal(ApprovalKind.CommandExecution, asked.Kind);
        Assert.Equal("npm publish", asked.Command);

        // The command is shown as the command. What the agent says about it is the agent's word for it.
        Assert.Equal(["description, in the words of the agent: Lists the files", "run_in_background: true"], asked.Details);
    }

    [Fact]
    public async Task What_is_too_much_to_be_listed_is_counted_and_is_not_allowed_by_one_key()
    {
        var content = string.Join("\n", Enumerable.Range(1, 1000).Select(number => $"line {number}"));

        var asked = await AskedAsync(
            Step.ToolApproval("Write", new JsonObject { ["file_path"] = @"C:\elsewhere\big.txt", ["content"] = content, ["after"] = "the end" }),
            Step.Message("Done."));

        Assert.Equal(401, asked.Details.Count);
        Assert.Equal(@"file_path: C:\elsewhere\big.txt", asked.Details[0]);
        Assert.Equal("  line 398", asked.Details[399]);
        Assert.Equal("603 more lines of what the tool is given are not listed.", asked.Details[400]);
        Assert.True(asked.Deliberate);
    }

    [Fact]
    public async Task A_line_that_is_too_long_to_be_listed_is_cut_where_that_is_said()
    {
        var asked = await AskedAsync(
            Step.ToolApproval("WebFetch", new JsonObject { ["url"] = "https://example.invalid/?" + new string('a', 5000), ["prompt"] = "short" }),
            Step.Message("Done."));

        Assert.Equal(2, asked.Details.Count);
        // "url: " and the address are 5030 characters.
        Assert.Equal(2000 + " (3030 more characters are not shown)".Length, asked.Details[0].Length);
        Assert.StartsWith("url: https://example.invalid/?aaaa", asked.Details[0], StringComparison.Ordinal);
        Assert.EndsWith("a (3030 more characters are not shown)", asked.Details[0], StringComparison.Ordinal);
        Assert.Equal("prompt: short", asked.Details[1]);
        Assert.True(asked.Deliberate);
    }

    [Fact]
    public async Task An_accepted_approval_lets_the_action_happen()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.Approval("npm install left-pad", onAccept: [Step.Write("installed.txt", "yes")], reason: "Network access"),
            Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        ApprovalRequest? asked = null;
        var events = await session.RunTurnAsync("go", onEvent: async e =>
        {
            if (e is ApprovalRequested requested)
            {
                asked = requested.Request;
                await session.RespondToApprovalAsync(requested.Request.ApprovalId, ApprovalDecision.Accept, CancellationToken.None);
            }
        });

        Assert.Equal("npm install left-pad", asked!.Command);
        Assert.Equal(ApprovalKind.CommandExecution, asked.Kind);
        Assert.Equal("Network access", asked.Reason);
        Assert.False(asked.CanAcceptForSession);
        Assert.True(File.Exists(fixture.InWorkspace("installed.txt")));
        var response = Assert.Single(fixture.Received("claude.input"), m => m["type"]!.GetValue<string>() == "control_response");
        Assert.Equal("success", response["response"]!["subtype"]!.GetValue<string>());
        Assert.Equal("allow", response["response"]!["response"]!["behavior"]!.GetValue<string>());
        Assert.Equal("npm install left-pad", response["response"]!["response"]!["updatedInput"]!["command"]!.GetValue<string>());
        Assert.Empty(events.Completion().Denials);
    }

    [Theory]
    [InlineData(ApprovalDecision.Decline, false, TurnOutcome.Completed)]
    [InlineData(ApprovalDecision.Cancel, true, TurnOutcome.Interrupted)]
    public async Task A_refused_approval_is_sent_as_a_denial(ApprovalDecision decision, bool interrupt, TurnOutcome outcome)
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.Approval("rm -rf build", onAccept: [Step.Write("ran.txt", "x")]),
            Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go", onEvent: async e =>
        {
            if (e is ApprovalRequested requested)
            {
                await session.RespondToApprovalAsync(requested.Request.ApprovalId, decision, CancellationToken.None);
            }
        });

        var response = Assert.Single(fixture.Received("claude.input"), m => m["type"]!.GetValue<string>() == "control_response");
        Assert.Equal("deny", response["response"]!["response"]!["behavior"]!.GetValue<string>());
        Assert.Equal(interrupt, response["response"]!["response"]!["interrupt"]?.GetValue<bool>() ?? false);
        Assert.False(File.Exists(fixture.InWorkspace("ran.txt")));
        Assert.Equal(outcome, events.Completion().Outcome);
    }

    [Fact]
    public async Task The_reviewer_is_never_asked_and_a_denied_tool_is_listed()
    {
        using var fixture = new AgentFixture().ReviewerTurn(Step.Write("hack.txt", "reviewer tried to write"), Step.Review("pass"));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer), CancellationToken.None);

        var events = await session.RunTurnAsync("review", ReviewSchema.AsElement());

        Assert.DoesNotContain(events, e => e is ApprovalRequested);
        Assert.False(File.Exists(fixture.InWorkspace("hack.txt")));
        Assert.Equal("Write", Assert.Single(events.Completion().Denials).Tool);
    }

    [Fact]
    public async Task Interrupting_stops_the_turn_through_the_agent_and_keeps_the_process()
    {
        using var fixture = new AgentFixture()
            .ImplementerTurn(Step.Message("Working..."), Step.Hang())
            .ImplementerTurn(Step.Message("Second turn done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var first = await session.RunTurnAsync("go", onEvent: async e =>
        {
            if (e is AssistantMessage)
            {
                await session.InterruptAsync(CancellationToken.None);
            }
        });
        var second = await session.RunTurnAsync("continue");

        Assert.Equal(TurnOutcome.Interrupted, first.Completion().Outcome);
        Assert.Contains(fixture.Received("claude.input"), m =>
            m["type"]!.GetValue<string>() == "control_request" && m["request"]!["subtype"]!.GetValue<string>() == "interrupt");
        Assert.Equal("Second turn done.", second.Completion().FinalMessage);
        Assert.Single(fixture.Received("claude.invocation"));
    }
}

public class ClaudeCredentialTests
{
    private const string Key = "sk-ant-test-0123456789abcdef";

    [Fact]
    public async Task The_key_reaches_the_agent_through_its_environment_and_nowhere_else()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli(() => Key);
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, trusted: true), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var invocation = Assert.Single(fixture.Received("claude.invocation"));
        Assert.Equal(Key, invocation["apiKeyValue"]!.GetValue<string>());
        Assert.DoesNotContain(invocation["args"]!.AsArray(), a => a!.GetValue<string>().Contains(Key));
        Assert.Equal("ANTHROPIC_API_KEY", Assert.Single(events.OfType<SessionConfigured>()).Effective.CredentialSource);
        Assert.DoesNotContain(events, e => e.ToString()!.Contains(Key));
    }

    [Fact]
    public async Task Without_a_key_held_by_yav_none_is_put_into_the_agents_environment()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.ClaudeCli();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        await session.RunTurnAsync("go");

        Assert.False(Assert.Single(fixture.Received("claude.invocation"))["apiKeyPresent"]!.GetValue<bool>());
    }

    [Fact]
    public async Task An_implementer_in_an_untrusted_project_is_refused_because_commands_would_inherit_the_key()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("never"));
        await using var adapter = fixture.ClaudeCli(() => Key);

        var error = await Assert.ThrowsAsync<CredentialIsolationException>(() =>
            adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, trusted: false), CancellationToken.None));

        Assert.Contains("ANTHROPIC_API_KEY", error.Message);
        Assert.DoesNotContain(Key, error.Message);
        Assert.Empty(fixture.Received("claude.invocation"));
    }

    [Fact]
    public async Task A_reviewer_in_an_untrusted_project_is_allowed_because_it_cannot_run_anything()
    {
        using var fixture = new AgentFixture().ReviewerTurn(Step.Review("pass"));
        await using var adapter = fixture.ClaudeCli(() => Key);
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer, trusted: false), CancellationToken.None);

        var events = await session.RunTurnAsync("review", ReviewSchema.AsElement());

        Assert.Equal(TurnOutcome.Completed, events.Completion().Outcome);
    }

    [Fact]
    public async Task A_key_that_shows_up_in_output_is_removed_before_anyone_sees_it()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.Command("env", "PATH=C:\\x\nANTHROPIC_API_KEY=" + Key + "\n", 0),
            Step.Message("Your key is " + Key));
        await using var adapter = fixture.ClaudeCli(() => Key);
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, trusted: true), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        Assert.DoesNotContain(events, e => e.ToString()!.Contains(Key));
        Assert.Contains("[redacted]", Assert.Single(events.OfType<CommandCompleted>()).Output);
        Assert.Contains("[redacted]", events.Completion().FinalMessage);
    }
}
