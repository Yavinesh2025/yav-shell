using System.Text.Json;
using System.Text.Json.Nodes;
using Yav.Adapters;
using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Runs;
using Yav.Platform.Processes;
using Yav.Tests.Support;

namespace Yav.Tests.Adapters;

public class CodexDetectionTests
{
    [Fact]
    public async Task The_installed_agent_is_found_with_its_version_and_labelled_experimental()
    {
        using var fixture = new AgentFixture();
        await using var adapter = fixture.CodexAppServer();

        var detection = await adapter.DetectAsync(CancellationToken.None);

        Assert.True(detection.Found);
        Assert.Equal("0.158.0", detection.Version);
        Assert.Equal(Fixtures.FakeAgent, detection.ExecutablePath, ignoreCase: true);
        Assert.Equal(AdapterMaturity.Experimental, detection.Maturity);
        Assert.Contains("experimental", detection.MaturityNote, StringComparison.OrdinalIgnoreCase);
        Assert.True(detection.VersionTested);
        Assert.Empty(detection.Problems);
    }

    [Theory]
    [InlineData("0.158.0")]
    [InlineData("0.158.7")]
    [InlineData("0.159.0")]
    [InlineData("0.159.2")]
    public async Task The_release_series_yav_was_tried_with_are_called_tested(string version)
    {
        using var fixture = new AgentFixture().Codex(c => c["version"] = version);
        await using var adapter = fixture.CodexAppServer();

        var detection = await adapter.DetectAsync(CancellationToken.None);

        Assert.True(detection.VersionTested);
        Assert.Equal("0.158.x, 0.159.x", detection.TestedVersions);
    }

    [Theory]
    [InlineData("0.157.9")]
    [InlineData("0.160.0")]
    [InlineData("0.999.0")]
    [InlineData("1.159.2")]
    public async Task A_version_outside_the_tested_range_is_pointed_out(string version)
    {
        using var fixture = new AgentFixture().Codex(c => c["version"] = version);
        await using var adapter = fixture.CodexAppServer();

        var detection = await adapter.DetectAsync(CancellationToken.None);

        Assert.True(detection.Found);
        Assert.Equal(version, detection.Version);
        Assert.False(detection.VersionTested);
    }

    [Fact]
    public async Task An_agent_that_is_not_installed_is_reported_as_not_found()
    {
        using var fixture = new AgentFixture();
        var options = fixture.Options() with { ExecutablePath = @"C:\no\such\codex.exe" };
        await using var adapter = new CodexAppServerAdapter(new ProcessRunner(), fixture.Clock, options);

        var detection = await adapter.DetectAsync(CancellationToken.None);

        Assert.False(detection.Found);
        Assert.False(detection.Usable);
    }

    [Fact]
    public async Task A_chatgpt_plan_is_a_subscription_route_that_needs_acknowledgement()
    {
        using var fixture = new AgentFixture();
        await using var adapter = fixture.CodexAppServer();

        var auth = await adapter.GetAuthStatusAsync(CancellationToken.None);

        Assert.True(auth.Authenticated);
        Assert.Equal(AccountRouteKind.Subscription, auth.Route);
        Assert.Equal("pro", auth.PlanType);
        Assert.Equal(BillingKind.IncludedInSubscription, auth.Billing);
        Assert.Equal(RoutePolicy.RequiresAcknowledgement, auth.Policy);
        Assert.DoesNotContain("user@example.invalid", auth.RouteLabel);
    }

    [Fact]
    public async Task An_api_key_is_a_separately_billed_route()
    {
        using var fixture = new AgentFixture().Codex(c => c["account"] = new JsonObject { ["type"] = "apiKey" });
        await using var adapter = fixture.CodexAppServer();

        var auth = await adapter.GetAuthStatusAsync(CancellationToken.None);

        Assert.Equal(AccountRouteKind.ApiKey, auth.Route);
        Assert.Equal(BillingKind.PayPerToken, auth.Billing);
        Assert.Equal(RoutePolicy.RequiresAcknowledgement, auth.Policy);
    }

    [Fact]
    public async Task No_account_means_not_signed_in()
    {
        using var fixture = new AgentFixture().Codex(c => c["account"] = null);
        await using var adapter = fixture.CodexAppServer();

        var auth = await adapter.GetAuthStatusAsync(CancellationToken.None);

        Assert.False(auth.Authenticated);
        Assert.Equal(AccountRouteKind.NotAuthenticated, auth.Route);
    }

    [Fact]
    public async Task Models_come_with_the_effort_values_the_provider_lists()
    {
        using var fixture = new AgentFixture();
        await using var adapter = fixture.CodexAppServer();

        var models = await adapter.ListModelsAsync(CancellationToken.None);

        Assert.Equal(["model-a", "model-b"], models.Select(m => m.Id));
        Assert.Equal(["low", "medium", "high", "xhigh"], models[0].SupportedEfforts);
        Assert.Equal(["low", "medium", "high", "xhigh", "max"], models[1].SupportedEfforts);
        Assert.True(models[0].IsDefault);
        var tier = Assert.Single(models[0].ServiceTiers);
        Assert.Equal("fast", tier.Id);
        Assert.True(tier.Faster);
    }

    [Fact]
    public async Task The_providers_own_description_of_each_effort_is_kept()
    {
        using var fixture = new AgentFixture().Codex(c => c["models"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "gpt-6-astra",
                ["efforts"] = new JsonArray
                {
                    new JsonObject { ["value"] = "max", ["description"] = "Maximum reasoning depth for the hardest problems" },
                    new JsonObject { ["value"] = "ultra", ["description"] = "Maximum reasoning with automatic task delegation" },
                    "plain",
                },
            },
        });
        await using var adapter = fixture.CodexAppServer();

        var model = Assert.Single(await adapter.ListModelsAsync(CancellationToken.None));

        Assert.Equal(["max", "ultra", "plain"], model.SupportedEfforts);
        Assert.Equal("Maximum reasoning with automatic task delegation", model.EffortDescriptions!["ultra"]);
        Assert.False(model.EffortDescriptions.ContainsKey("plain"));
    }

    [Fact]
    public async Task The_tier_the_catalog_calls_priority_is_the_faster_separately_metered_one()
    {
        using var fixture = new AgentFixture().Codex(c => c["models"] = new JsonArray
        {
            new JsonObject
            {
                ["id"] = "gpt-6-astra",
                ["efforts"] = new JsonArray { "max" },
                ["serviceTiers"] = new JsonArray
                {
                    new JsonObject { ["id"] = "priority", ["name"] = "Fast", ["description"] = "2x speed, increased usage" },
                    new JsonObject { ["id"] = "flex", ["name"] = "Flex", ["description"] = "Lower cost, slower" },
                },
            },
        });
        await using var adapter = fixture.CodexAppServer();

        var model = Assert.Single(await adapter.ListModelsAsync(CancellationToken.None));

        var priority = Assert.Single(model.ServiceTiers, t => t.Id == "priority");
        Assert.True(priority.Faster);
        Assert.Equal("2x speed, increased usage", priority.Description);
        Assert.False(Assert.Single(model.ServiceTiers, t => t.Id == "flex").Faster);
    }

    [Fact]
    public async Task Listing_models_and_reading_the_account_sends_no_turn()
    {
        using var fixture = new AgentFixture();
        await using var adapter = fixture.CodexAppServer();

        await adapter.DetectAsync(CancellationToken.None);
        await adapter.GetAuthStatusAsync(CancellationToken.None);
        await adapter.ListModelsAsync(CancellationToken.None);
        await adapter.GetRateLimitsAsync(CancellationToken.None);

        Assert.Empty(fixture.CodexRequests("turn/start"));
        Assert.Empty(fixture.CodexRequests("thread/start"));
    }

    [Fact]
    public async Task Rate_limits_are_read_with_their_windows()
    {
        using var fixture = new AgentFixture();
        await using var adapter = fixture.CodexAppServer();

        var limits = await adapter.GetRateLimitsAsync(CancellationToken.None);

        Assert.NotNull(limits);
        Assert.Equal(12, limits.Primary!.UsedPercent);
        Assert.Equal(300, limits.Primary.WindowMinutes);
        Assert.Equal(DateTimeOffset.FromUnixTimeSeconds(1_790_000_000), limits.Primary.ResetsAt);
        Assert.Equal(40, limits.Secondary!.UsedPercent);
        Assert.Equal(false, limits.HasCredits);
        Assert.Null(limits.CreditBalance);
    }

    [Fact]
    public async Task Rate_limits_that_cannot_be_read_are_unavailable_not_zero()
    {
        using var fixture = new AgentFixture().Codex(c => c["rateLimitsUnavailable"] = true);
        await using var adapter = fixture.CodexAppServer();

        Assert.Null(await adapter.GetRateLimitsAsync(CancellationToken.None));
    }

    [Fact]
    public async Task The_client_identifies_itself_and_stays_on_the_stable_api_surface()
    {
        using var fixture = new AgentFixture();
        await using var adapter = fixture.CodexAppServer();

        await adapter.GetAuthStatusAsync(CancellationToken.None);

        var initialize = Assert.Single(fixture.CodexRequests("initialize"));
        Assert.Equal("yav_shell", initialize["params"]!["clientInfo"]!["name"]!.GetValue<string>());
        Assert.Equal("0.1.0-test", initialize["params"]!["clientInfo"]!["version"]!.GetValue<string>());
        Assert.NotEqual(true, initialize["params"]?["capabilities"]?["experimentalApi"]?.GetValue<bool>());
        Assert.Null(initialize["jsonrpc"]);
        Assert.Contains(fixture.Received("codex.notification"), n => n["method"]!.GetValue<string>() == "initialized");
    }

    [Fact]
    public async Task One_process_serves_every_request()
    {
        using var fixture = new AgentFixture();
        await using var adapter = fixture.CodexAppServer();

        await adapter.GetAuthStatusAsync(CancellationToken.None);
        await adapter.ListModelsAsync(CancellationToken.None);
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        Assert.Single(fixture.CodexRequests("initialize"));
        Assert.Single(fixture.Received("codex.request").Select(r => r["_pid"]!.GetValue<int>()).Distinct());
    }

    [Fact]
    public async Task An_agent_that_does_not_answer_in_time_is_reported_clearly()
    {
        using var fixture = new AgentFixture().Codex(c => c["startupDelayMs"] = 5000);
        await using var adapter = fixture.CodexAppServer(startupTimeout: TimeSpan.FromMilliseconds(600));

        var error = await Assert.ThrowsAsync<AgentProtocolException>(() => adapter.GetAuthStatusAsync(CancellationToken.None));

        Assert.Contains("did not answer", error.Message);
    }
}

public class CodexSessionTests
{
    [Fact]
    public async Task The_implementer_session_asks_for_a_writable_workspace_and_user_approvals()
    {
        using var fixture = new AgentFixture();
        await using var adapter = fixture.CodexAppServer();

        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, model: "model-a", effort: "xhigh"), CancellationToken.None);

        var parameters = Assert.Single(fixture.CodexRequests("thread/start"))["params"]!;
        Assert.Equal("workspace-write", parameters["sandbox"]!.GetValue<string>());
        Assert.Equal("on-request", parameters["approvalPolicy"]!.GetValue<string>());
        Assert.Equal("model-a", parameters["model"]!.GetValue<string>());
        Assert.Equal(fixture.Workspace, parameters["cwd"]!.GetValue<string>());
        Assert.Equal("xhigh", parameters["config"]!["model_reasoning_effort"]!.GetValue<string>());
        Assert.Contains("ROLE INSTRUCTIONS", parameters["developerInstructions"]!.GetValue<string>());
        Assert.Null(parameters["baseInstructions"]);
    }

    [Fact]
    public async Task The_reviewer_session_is_read_only_and_never_asks()
    {
        using var fixture = new AgentFixture();
        await using var adapter = fixture.CodexAppServer();

        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer, model: "model-b", effort: "max"), CancellationToken.None);

        var parameters = Assert.Single(fixture.CodexRequests("thread/start"))["params"]!;
        Assert.Equal("read-only", parameters["sandbox"]!.GetValue<string>());
        Assert.Equal("never", parameters["approvalPolicy"]!.GetValue<string>());
        Assert.Equal("read-only", session.Effective!.Sandbox);
    }

    [Fact]
    public async Task Codex_is_told_that_you_decide_every_approval_even_when_its_own_configuration_says_otherwise()
    {
        // The user's own Codex configuration hands approvals to Codex's reviewer. YAV's conversations override that.
        using var fixture = new AgentFixture().Codex(c =>
        {
            c["configuredApprovalsReviewer"] = "auto_review";
            c["knownThreads"] = new JsonArray { "thr-earlier" };
        });
        await using var adapter = fixture.CodexAppServer();

        await using var started = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);
        await using var resumed = await adapter.ResumeSessionAsync("thr-earlier", fixture.Request(AgentRole.Reviewer), CancellationToken.None);

        Assert.Equal("user", Assert.Single(fixture.CodexRequests("thread/start"))["params"]!["approvalsReviewer"]!.GetValue<string>());
        Assert.Equal("user", Assert.Single(fixture.CodexRequests("thread/resume"))["params"]!["approvalsReviewer"]!.GetValue<string>());
        Assert.Equal("user", started.Effective!.ApprovalsReviewer);
        Assert.Equal("user", resumed.Effective!.ApprovalsReviewer);
    }

    [Theory]
    [InlineData("auto_review", false)]
    [InlineData("guardian_subagent", false)]
    [InlineData("auto_review", true)]
    public async Task A_conversation_whose_approvals_codex_would_decide_itself_is_refused(string reviewer, bool resume)
    {
        // A value the client cannot override, such as a requirement set by an administrator.
        using var fixture = new AgentFixture().Codex(c =>
        {
            c["effectiveApprovalsReviewer"] = reviewer;
            c["knownThreads"] = new JsonArray { "thr-earlier" };
        });
        await using var adapter = fixture.CodexAppServer();

        var error = await Assert.ThrowsAsync<AgentProtocolException>(() => resume
            ? adapter.ResumeSessionAsync("thr-earlier", fixture.Request(AgentRole.Implementer), CancellationToken.None)
            : adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None));

        Assert.True(error.Refused);
        Assert.Contains($"'{reviewer}'", error.Message, StringComparison.Ordinal);
        Assert.Contains("only you decide about access", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task The_users_own_developer_instructions_are_kept_in_front_of_the_role_instructions()
    {
        using var fixture = new AgentFixture().Codex(c => c["developerInstructions"] = "Always answer in British English.");
        await using var adapter = fixture.CodexAppServer();

        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var sent = Assert.Single(fixture.CodexRequests("thread/start"))["params"]!["developerInstructions"]!.GetValue<string>();
        Assert.StartsWith("Always answer in British English.", sent);
        Assert.EndsWith("ROLE INSTRUCTIONS", sent);
    }

    [Fact]
    public async Task The_codex_configuration_is_read_for_the_directory_of_each_conversation()
    {
        using var fixture = new AgentFixture();
        var other = Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(fixture.Workspace)!, "other project")).FullName;
        fixture.Codex(c =>
        {
            c["developerInstructions"] = "User rule.";
            c["projectDeveloperInstructions"] = new JsonObject { [fixture.Workspace] = "Project rule." };
        });
        await using var adapter = fixture.CodexAppServer();

        await using var first = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);
        await using var second = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer) with { WorkingDirectory = other }, CancellationToken.None);
        await using var third = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer), CancellationToken.None);

        // The answer for a directory is kept for it, and only for it.
        Assert.Equal([fixture.Workspace, other], fixture.CodexRequests("config/read").Select(r => r["params"]!["cwd"]!.GetValue<string>()));
        var sent = fixture.CodexRequests("thread/start").Select(r => r["params"]!["developerInstructions"]!.GetValue<string>()).ToList();
        Assert.StartsWith("Project rule.", sent[0]);
        Assert.StartsWith("User rule.", sent[1]);
        Assert.StartsWith("Project rule.", sent[2]);
    }

    [Theory]
    [InlineData("chatgpt", AccountRouteKind.Subscription, "ChatGPT plan (pro)")]
    [InlineData("apiKey", AccountRouteKind.ApiKey, "OpenAI API key")]
    [InlineData("amazonBedrock", AccountRouteKind.CloudProvider, "Amazon Bedrock")]
    public async Task A_conversation_says_which_account_it_works_with_and_not_who_the_person_is(string type, AccountRouteKind route, string description)
    {
        using var fixture = new AgentFixture().Codex(c => c["account"] = new JsonObject { ["type"] = type, ["email"] = "someone@example.invalid", ["planType"] = "pro" });
        await using var adapter = fixture.CodexAppServer();

        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var account = session.Effective!.Account!;
        Assert.Equal(route, account.Route);
        Assert.Equal(description, account.Description);
        Assert.Equal("account/read", account.Source);
        Assert.DoesNotContain("someone", account.Description, StringComparison.Ordinal);
        Assert.DoesNotContain("@", account.Description, StringComparison.Ordinal);
        Assert.Equal(account, Assert.IsType<SessionConfigured>(await FirstEventAsync(session)).Effective.Account);
    }

    [Fact]
    public async Task A_conversation_of_a_codex_that_is_not_signed_in_says_so()
    {
        using var fixture = new AgentFixture().Codex(c => c["account"] = null);
        await using var adapter = fixture.CodexAppServer();

        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        Assert.Equal(AccountRouteKind.NotAuthenticated, session.Effective!.Account!.Route);
    }

    [Fact]
    public async Task When_the_codex_configuration_cannot_be_read_the_conversation_is_told_and_the_next_one_reads_it_again()
    {
        using var fixture = new AgentFixture().Codex(c =>
        {
            c["developerInstructions"] = "Always answer in British English.";
            c["configReadFailures"] = 1;
        });
        await using var adapter = fixture.CodexAppServer();

        await using var first = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);
        await using var second = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var told = Assert.Single(Notices(first), n => n.Contains("developer instructions", StringComparison.Ordinal));
        Assert.Contains("could not be read", told, StringComparison.Ordinal);
        Assert.Contains("not part of this conversation", told, StringComparison.Ordinal);
        Assert.Contains("expected a table at line 3", told, StringComparison.Ordinal);
        Assert.DoesNotContain(Notices(second), n => n.Contains("developer instructions", StringComparison.Ordinal));

        // A failure is not kept: the second conversation reads the configuration again and gets the user's own.
        Assert.Equal(2, fixture.CodexRequests("config/read").Count);
        var sent = fixture.CodexRequests("thread/start").Select(r => r["params"]!["developerInstructions"]!.GetValue<string>()).ToList();
        Assert.Equal("ROLE INSTRUCTIONS", sent[0]);
        Assert.StartsWith("Always answer in British English.", sent[1]);
    }

    /// <summary>The warnings a session gave until now.</summary>
    private static List<string> Notices(IAgentSession session)
    {
        var told = new List<string>();
        while (session.Events.TryRead(out var next))
        {
            if (next is AgentNotice { IsWarning: true } notice)
            {
                told.Add(notice.Message);
            }
        }

        return told;
    }

    [Fact]
    public async Task The_version_of_the_running_codex_is_reported_not_the_one_that_created_the_thread()
    {
        using var fixture = new AgentFixture().Codex(c =>
        {
            c["version"] = "0.158.2";
            c["threadCliVersion"] = "0.150.0";
            c["knownThreads"] = new JsonArray { "thr-earlier" };
        });
        await using var adapter = fixture.CodexAppServer();

        await using var session = await adapter.ResumeSessionAsync("thr-earlier", fixture.Request(AgentRole.Implementer), CancellationToken.None);

        Assert.Equal("0.158.2", session.Effective!.AgentVersion);
    }

    [Fact]
    public async Task What_the_provider_reports_is_recorded_as_the_effective_settings()
    {
        using var fixture = new AgentFixture();
        File.WriteAllText(fixture.InWorkspace("AGENTS.md"), "# Project rules");
        await using var adapter = fixture.CodexAppServer();

        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, model: "model-a", effort: "xhigh"), CancellationToken.None);

        var effective = session.Effective!;
        Assert.Equal("model-a", effective.Model);
        Assert.Equal("xhigh", effective.Effort);
        Assert.Equal("workspace-write", effective.Sandbox);
        Assert.Equal("on-request", effective.ApprovalPolicy);
        Assert.Equal(fixture.Workspace, effective.WorkingDirectory);
        Assert.Equal([fixture.InWorkspace("AGENTS.md")], effective.InstructionSources);
        Assert.NotNull(session.SessionId);
        var configured = Assert.IsType<SessionConfigured>(await FirstEventAsync(session));
        Assert.Equal(session.SessionId, configured.SessionId);
    }

    [Fact]
    public async Task An_effort_the_provider_lowered_is_reported_as_it_is()
    {
        using var fixture = new AgentFixture().Codex(c => c["effectiveEffort"] = "high");
        await using var adapter = fixture.CodexAppServer();

        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, effort: "xhigh"), CancellationToken.None);

        Assert.Equal("high", session.Effective!.Effort);
    }

    [Fact]
    public async Task A_workspace_that_windows_made_read_only_is_reported_as_read_only()
    {
        // Codex gives a read-only policy when a writable one was requested but the Windows sandbox is not set up.
        using var fixture = new AgentFixture().Codex(c => c["effectiveSandbox"] = "readOnly");
        await using var adapter = fixture.CodexAppServer();

        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        Assert.Equal("read-only", session.Effective!.Sandbox);
    }

    [Fact]
    public async Task What_widens_the_sandbox_is_reported_with_the_sandbox()
    {
        using var fixture = new AgentFixture().Codex(c =>
        {
            c["effectiveNetworkAccess:implementer"] = true;
            c["effectiveWritableRoots"] = new JsonArray { @"C:\shared\cache", @"D:\other ünï" };
        });
        await using var adapter = fixture.CodexAppServer();

        await using var implementer = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);
        await using var reviewer = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer), CancellationToken.None);

        var writable = implementer.Effective!.Widening!;
        Assert.True(writable.NetworkAccess);
        Assert.Equal([@"C:\shared\cache", @"D:\other ünï"], writable.AdditionalWritableRoots);
        var readOnly = reviewer.Effective!.Widening!;
        Assert.False(readOnly.NetworkAccess);
        Assert.Empty(readOnly.AdditionalWritableRoots);
    }

    [Theory]
    [InlineData("dangerFullAccess")]
    [InlineData("externalSandbox")]
    public async Task A_sandbox_of_another_kind_than_yav_asks_for_is_reported_without_what_widens_it(string policy)
    {
        // The kind itself is what the verification of the settings does not accept.
        using var fixture = new AgentFixture().Codex(c => c["effectiveSandbox"] = policy);
        await using var adapter = fixture.CodexAppServer();

        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        Assert.NotNull(session.Effective!.Sandbox);
        Assert.Null(session.Effective.Widening);
    }

    [Theory]
    [InlineData("""{"type":"workspaceWrite","writableRoots":[],"networkAccess":"yes"}""", "networkAccess")]
    [InlineData("""{"type":"workspaceWrite","networkAccess":false}""", "writableRoots")]
    [InlineData("""{"type":"workspaceWrite","writableRoots":"C:\\cache","networkAccess":false}""", "writableRoots")]
    [InlineData("""{"type":"workspaceWrite","writableRoots":[42],"networkAccess":false}""", "writableRoots")]
    [InlineData("""{"type":"readOnly"}""", "networkAccess")]
    [InlineData("""{"type":"readOnly","networkAccess":null}""", "networkAccess")]
    public async Task A_sandbox_of_a_kind_yav_asks_for_in_a_form_that_cannot_be_read_is_refused(string policy, string unreadable)
    {
        using var fixture = new AgentFixture().Codex(c => c["effectiveSandboxPolicy"] = JsonNode.Parse(policy));
        await using var adapter = fixture.CodexAppServer();

        var error = await Assert.ThrowsAsync<AgentProtocolException>(() => adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None));

        Assert.True(error.Refused);
        Assert.Contains($"'{unreadable}'", error.Message, StringComparison.Ordinal);
        Assert.Contains("what the sandbox may reach is not known", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("workspaceWrite")]
    [InlineData("readOnly")]
    public async Task A_sandbox_of_a_kind_yav_asks_for_that_does_not_say_what_widens_it_is_refused(string policy)
    {
        using var fixture = new AgentFixture().Codex(c =>
        {
            c["effectiveSandbox"] = policy;
            c["sandboxWithoutDetails"] = true;
        });
        await using var adapter = fixture.CodexAppServer();

        var error = await Assert.ThrowsAsync<AgentProtocolException>(() => adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None));

        Assert.True(error.Refused);
        Assert.Contains("what the sandbox may reach is not known", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conversation_whose_answer_names_no_sandbox_is_refused()
    {
        using var fixture = new AgentFixture().Codex(c => c["omitSandbox"] = true);
        await using var adapter = fixture.CodexAppServer();

        var error = await Assert.ThrowsAsync<AgentProtocolException>(() => adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None));

        Assert.True(error.Refused);
        Assert.Contains("does not say which sandbox", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_conversation_whose_answer_does_not_say_who_decides_about_access_is_refused(bool resume)
    {
        using var fixture = new AgentFixture().Codex(c =>
        {
            c["omitApprovalsReviewer"] = true;
            c["knownThreads"] = new JsonArray { "thr-earlier" };
        });
        await using var adapter = fixture.CodexAppServer();

        var error = await Assert.ThrowsAsync<AgentProtocolException>(() => resume
            ? adapter.ResumeSessionAsync("thr-earlier", fixture.Request(AgentRole.Implementer), CancellationToken.None)
            : adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None));

        Assert.True(error.Refused);
        Assert.Contains("does not say who decides", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_faster_service_tier_is_requested_only_when_the_profile_asks_for_it()
    {
        using var fixture = new AgentFixture();
        await using var adapter = fixture.CodexAppServer();

        await using var standard = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);
        await using var faster = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, serviceTier: "fast"), CancellationToken.None);

        var requests = fixture.CodexRequests("thread/start");
        Assert.Null(requests[0]["params"]!["serviceTier"]);
        Assert.Equal("fast", requests[1]["params"]!["serviceTier"]!.GetValue<string>());
        Assert.Null(standard.Effective!.ServiceTier);
        Assert.Equal("fast", faster.Effective!.ServiceTier);
    }

    [Fact]
    public async Task A_known_session_is_resumed_by_its_identifier()
    {
        using var fixture = new AgentFixture().Codex(c => c["knownThreads"] = new JsonArray { "thr-earlier" });
        await using var adapter = fixture.CodexAppServer();

        await using var session = await adapter.ResumeSessionAsync("thr-earlier", fixture.Request(AgentRole.Implementer), CancellationToken.None);

        Assert.Equal("thr-earlier", session.SessionId);
        Assert.Equal("thr-earlier", Assert.Single(fixture.CodexRequests("thread/resume"))["params"]!["threadId"]!.GetValue<string>());
        Assert.Empty(fixture.CodexRequests("thread/start"));
    }

    [Fact]
    public async Task Resuming_asks_for_the_thread_without_its_history_and_only_with_what_the_method_takes()
    {
        using var fixture = new AgentFixture().Codex(c => c["knownThreads"] = new JsonArray { "thr-earlier" });
        await using var adapter = fixture.CodexAppServer();

        await using var resumed = await adapter.ResumeSessionAsync("thr-earlier", fixture.Request(AgentRole.Implementer), CancellationToken.None);
        await using var started = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        // A long history in one answer could exceed what a single message may be, and close the connection all conversations share.
        var resume = Assert.Single(fixture.CodexRequests("thread/resume"))["params"]!.AsObject();
        Assert.True(resume["excludeTurns"]!.GetValue<bool>());
        Assert.False(resume.ContainsKey("serviceName"));
        Assert.Equal("yav_shell", Assert.Single(fixture.CodexRequests("thread/start"))["params"]!["serviceName"]!.GetValue<string>());
    }

    [Fact]
    public async Task Resuming_a_session_that_does_not_exist_fails_with_the_providers_reason()
    {
        using var fixture = new AgentFixture();
        await using var adapter = fixture.CodexAppServer();

        var error = await Assert.ThrowsAsync<AgentProtocolException>(() =>
            adapter.ResumeSessionAsync("thr-missing", fixture.Request(AgentRole.Implementer), CancellationToken.None));

        Assert.Contains("thread not found", error.Message);
    }

    [Theory]
    [InlineData("completed", SessionProbeState.LastTurnCompleted)]
    [InlineData("interrupted", SessionProbeState.LastTurnInterrupted)]
    [InlineData("failed", SessionProbeState.LastTurnFailed)]

    // Codex reports a turn it recorded as in progress as interrupted when no process runs the thread.
    [InlineData("inProgress", SessionProbeState.LastTurnInterrupted)]
    public async Task A_session_can_be_examined_without_sending_a_turn(string lastTurn, SessionProbeState expected)
    {
        using var fixture = new AgentFixture().Codex(c => c["threadStates"] = new JsonObject { ["thr-x"] = lastTurn });
        await using var adapter = fixture.CodexAppServer();

        var probe = await adapter.ProbeSessionAsync("thr-x", CancellationToken.None);

        Assert.Equal(expected, probe.State);
        Assert.Empty(fixture.CodexRequests("turn/start"));
    }

    [Fact]
    public async Task A_turn_recorded_as_in_progress_in_a_thread_that_nothing_runs_is_not_reported_as_running()
    {
        // A version of Codex that reports the turn as it was recorded, next to a thread that is not loaded.
        using var fixture = new AgentFixture().Codex(c =>
        {
            c["threadStates"] = new JsonObject { ["thr-x"] = "inProgress" };
            c["turnsAsRecorded"] = true;
        });
        await using var adapter = fixture.CodexAppServer();

        var probe = await adapter.ProbeSessionAsync("thr-x", CancellationToken.None);

        Assert.Equal(SessionProbeState.LastTurnInterrupted, probe.State);
    }

    [Fact]
    public async Task A_turn_that_runs_is_reported_as_running()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Working...", "commentary"), Step.Hang());
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);
        await session.StartTurnAsync(new TurnRequest("go", null, []), CancellationToken.None);
        await foreach (var item in session.Events.ReadAllAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token))
        {
            if (item is AssistantMessage)
            {
                break;
            }
        }

        var probe = await adapter.ProbeSessionAsync(session.SessionId!, CancellationToken.None);

        Assert.Equal(SessionProbeState.Active, probe.State);
    }

    [Fact]
    public async Task Examining_a_session_that_does_not_exist_says_so()
    {
        using var fixture = new AgentFixture();
        await using var adapter = fixture.CodexAppServer();

        var probe = await adapter.ProbeSessionAsync("thr-nope", CancellationToken.None);

        Assert.Equal(SessionProbeState.NotFound, probe.State);
    }

    [Fact]
    public async Task A_session_codex_cannot_read_is_reported_as_the_failure_it_is_not_as_one_codex_does_not_know()
    {
        using var fixture = new AgentFixture().Codex(c => c["threadReadError"] = new JsonObject { ["code"] = -32603, ["message"] = "failed to read thread: disk I/O error" });
        await using var adapter = fixture.CodexAppServer();

        var error = await Assert.ThrowsAsync<AgentProtocolException>(() => adapter.ProbeSessionAsync("thr-x", CancellationToken.None));

        Assert.Contains("disk I/O error", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_session_codex_does_not_answer_about_is_reported_as_the_failure_it_is()
    {
        using var fixture = new AgentFixture().Codex(c => c["threadReadHangs"] = true);
        await using var adapter = fixture.CodexAppServer(requestTimeout: TimeSpan.FromSeconds(1));

        var error = await Assert.ThrowsAsync<AgentProtocolException>(() => adapter.ProbeSessionAsync("thr-x", CancellationToken.None));

        Assert.Contains("did not answer 'thread/read'", error.Message, StringComparison.Ordinal);
    }

    private static async Task<AgentEvent> FirstEventAsync(IAgentSession session)
    {
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        return await session.Events.ReadAsync(cancellation.Token);
    }
}

public class CodexTurnTests
{
    private const string Prompt = "Fix the login bug.\r\nKeep \"quotes\", %PATH% and ünï 日本 exactly.\n";

    [Fact]
    public async Task The_prompt_travels_in_the_request_exactly_as_written()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, effort: "xhigh"), CancellationToken.None);

        await session.RunTurnAsync(Prompt);

        var parameters = Assert.Single(fixture.CodexRequests("turn/start"))["params"]!;
        var input = Assert.Single(parameters["input"]!.AsArray())!;
        Assert.Equal("text", input["type"]!.GetValue<string>());
        Assert.Equal(Prompt, input["text"]!.GetValue<string>());
        Assert.Equal("xhigh", parameters["effort"]!.GetValue<string>());
        Assert.Equal(session.SessionId, parameters["threadId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Streamed_text_commands_and_file_changes_become_events_in_order()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.Message("Looking at the code.", "commentary"),
            Step.Command("npm test", "2 passing\n", 0),
            Step.Write("src/login.txt", "fixed"),
            Step.Message("The bug is fixed."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var messages = events.OfType<AssistantMessage>().ToList();
        Assert.Equal(["Looking at the code.", "The bug is fixed."], messages.Select(m => m.Text));
        Assert.Equal([MessagePhase.Commentary, MessagePhase.FinalAnswer], messages.Select(m => m.Phase));
        Assert.Equal("Looking at the code.", string.Concat(events.OfType<AssistantTextDelta>().Where(d => d.ItemId == messages[0].ItemId).Select(d => d.Text)));

        var started = Assert.Single(events.OfType<CommandStarted>());
        var completed = Assert.Single(events.OfType<CommandCompleted>());
        Assert.Equal("npm test", started.Command);
        Assert.Equal(0, completed.ExitCode);
        Assert.Equal("2 passing\n", Assert.Single(events.OfType<CommandOutputDelta>()).Text);

        var change = events.OfType<FilesChanged>().Last();
        Assert.Equal(FileChangeKind.Add, Assert.Single(change.Changes).Kind);
        Assert.Equal("fixed", File.ReadAllText(fixture.InWorkspace("src/login.txt")));

        var completion = events.Completion();
        Assert.Equal(TurnOutcome.Completed, completion.Outcome);
        Assert.Equal("The bug is fixed.", completion.FinalMessage);
        Assert.True(events.IndexOf(started) < events.IndexOf(change));
    }

    [Fact]
    public async Task The_final_message_is_the_final_answer_not_the_last_commentary()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.Message("The answer.", "final_answer"),
            Step.Message("One more remark.", "commentary"));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        Assert.Equal("The answer.", events.Completion().FinalMessage);
    }

    [Fact]
    public async Task Reported_usage_is_normalized_so_cached_and_reasoning_tokens_are_not_counted_twice()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.Usage(input: 5000, cached: 4000, output: 900, reasoning: 300, lastInput: 600, lastCached: 500, lastOutput: 100, lastReasoning: 40),
            Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var usage = Assert.Single(events.OfType<UsageUpdated>()).Usage;
        Assert.Equal(UsageScope.CumulativeForSession, usage.Scope);
        Assert.Equal(1000, usage.Tokens.UncachedInput);
        Assert.Equal(4000, usage.Tokens.CacheRead);
        Assert.Equal(900, usage.Tokens.Output);
        Assert.Equal(300, usage.Tokens.ReasoningWithinOutput);
        Assert.Equal(5900, usage.Tokens.Total);
        Assert.Equal(100, usage.LastRequest!.UncachedInput);
        Assert.Null(usage.ProviderCostUsd);
        Assert.Equal(ValueProvenance.Unavailable, usage.CostProvenance);
    }

    [Fact]
    public async Task The_reviewers_structured_result_is_delivered_as_an_object()
    {
        using var fixture = new AgentFixture().ReviewerTurn(Step.Review("changes_required", file: "src/login.txt", title: "Empty password is accepted"));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer), CancellationToken.None);

        var events = await session.RunTurnAsync("review", ReviewSchema.AsElement());

        var completion = events.Completion();
        Assert.NotNull(completion.StructuredOutput);
        Assert.Equal("changes_required", completion.StructuredOutput.Value.GetProperty("status").GetString());
        Assert.Equal("Empty password is accepted", completion.StructuredOutput.Value.GetProperty("findings")[0].GetProperty("title").GetString());
        var sent = Assert.Single(fixture.CodexRequests("turn/start"))["params"]!["outputSchema"]!;
        Assert.Equal("object", sent["type"]!.GetValue<string>());
    }

    [Fact]
    public async Task A_final_message_that_is_not_the_requested_object_has_no_structured_result()
    {
        using var fixture = new AgentFixture().ReviewerTurn(Step.RawFinal("Looks good to me!"));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer), CancellationToken.None);

        var events = await session.RunTurnAsync("review", ReviewSchema.AsElement());

        Assert.Null(events.Completion().StructuredOutput);
        Assert.Equal("Looks good to me!", events.Completion().FinalMessage);
    }

    [Theory]
    [InlineData("usageLimitExceeded", TurnOutcome.UsageLimitReached)]
    [InlineData("rateLimitExceeded", TurnOutcome.RateLimited)]
    [InlineData("serverOverloaded", TurnOutcome.RateLimited)]
    [InlineData("contextWindowExceeded", TurnOutcome.Failed)]
    [InlineData(null, TurnOutcome.Failed)]
    public async Task A_failed_turn_says_why(string? info, TurnOutcome expected)
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Fail("The provider stopped the turn.", info));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var completion = events.Completion();
        Assert.Equal(expected, completion.Outcome);
        Assert.Equal("The provider stopped the turn.", completion.ErrorMessage);
        Assert.Equal(info, completion.ErrorCode);
    }

    [Fact]
    public async Task An_error_the_provider_will_retry_does_not_end_the_turn()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.Error("stream disconnected", willRetry: true),
            Step.Message("Recovered and finished."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var error = Assert.Single(events.OfType<AgentError>());
        Assert.True(error.WillRetry);
        Assert.Equal(TurnOutcome.Completed, events.Completion().Outcome);
    }

    [Fact]
    public async Task A_model_the_provider_switched_is_reported()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Reroute("model-a", "model-safe"), Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var rerouted = Assert.Single(events.OfType<ModelRerouted>());
        Assert.Equal("model-a", rerouted.FromModel);
        Assert.Equal("model-safe", rerouted.ToModel);
    }

    [Fact]
    public async Task Rate_limit_updates_and_warnings_are_passed_on()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.RateLimits(91), Step.Warning("Approaching the limit."), Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        Assert.Equal(91, Assert.Single(events.OfType<RateLimitUpdated>()).Snapshot.Primary!.UsedPercent);
        Assert.Contains(events.OfType<AgentNotice>(), n => n.Message == "Approaching the limit." && n.IsWarning);
    }

    [Fact]
    public async Task A_rate_limit_update_that_carries_only_some_values_keeps_the_others()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.RateLimitUpdate(new JsonObject { ["secondary"] = Step.LimitWindow(45, 10080) }),
            Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await adapter.GetRateLimitsAsync(CancellationToken.None);
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var merged = Assert.Single(events.OfType<RateLimitUpdated>()).Snapshot;
        Assert.Equal(12, merged.Primary!.UsedPercent);
        Assert.Equal(45, merged.Secondary!.UsedPercent);
        Assert.Equal("pro", merged.PlanType);
        Assert.Equal(false, merged.HasCredits);
    }

    [Fact]
    public async Task An_update_that_follows_a_reading_of_the_limits_is_not_overwritten_by_the_reading()
    {
        using var fixture = new AgentFixture()
            .Codex(c => c["rateLimitsUpdateAfterRead"] = new JsonObject { ["primary"] = Step.LimitWindow(50) })
            .ImplementerTurn(Step.RateLimitUpdate(new JsonObject { ["secondary"] = Step.LimitWindow(45, 10080) }), Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        var read = await adapter.GetRateLimitsAsync(CancellationToken.None);
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        // The reading says what it says; the update that came after it is what is known afterwards.
        Assert.Equal(12, read!.Primary!.UsedPercent);
        var merged = Assert.Single(events.OfType<RateLimitUpdated>()).Snapshot;
        Assert.Equal(50, merged.Primary!.UsedPercent);
        Assert.Equal(45, merged.Secondary!.UsedPercent);
    }

    [Fact]
    public async Task Credits_are_taken_whole_from_an_update_as_the_windows_are()
    {
        using var fixture = new AgentFixture()
            .Codex(c => c["rateLimits"] = new JsonObject
            {
                ["planType"] = "pro",
                ["primary"] = Step.LimitWindow(12),
                ["credits"] = new JsonObject { ["hasCredits"] = true, ["unlimited"] = false, ["balance"] = "12.50" },
            })
            .ImplementerTurn(
                Step.RateLimitUpdate(new JsonObject { ["credits"] = new JsonObject { ["hasCredits"] = false, ["unlimited"] = false, ["balance"] = null } }),
                Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        Assert.Equal("12.50", (await adapter.GetRateLimitsAsync(CancellationToken.None))!.CreditBalance);
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var merged = Assert.Single(events.OfType<RateLimitUpdated>()).Snapshot;
        Assert.Equal(false, merged.HasCredits);
        Assert.Null(merged.CreditBalance);
        Assert.Equal(12, merged.Primary!.UsedPercent);
    }

    [Fact]
    public async Task Rate_limit_updates_are_merged_per_limit()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.RateLimitUpdate(new JsonObject { ["limitId"] = "codex", ["primary"] = Step.LimitWindow(91), ["rateLimitReachedType"] = "rate_limit_reached" }),
            Step.RateLimitUpdate(new JsonObject { ["limitId"] = "codex_other", ["limitName"] = "Other model", ["primary"] = Step.LimitWindow(5) }),
            Step.RateLimitUpdate(new JsonObject { ["limitId"] = "codex", ["credits"] = new JsonObject { ["hasCredits"] = true, ["unlimited"] = false, ["balance"] = "12.50" } }),
            Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var snapshots = events.OfType<RateLimitUpdated>().Select(u => u.Snapshot).ToList();
        Assert.Equal(3, snapshots.Count);
        Assert.Equal("Other model", snapshots[1].LimitName);
        Assert.Equal(5, snapshots[1].Primary!.UsedPercent);
        var codex = snapshots[2];
        Assert.Equal("codex", codex.LimitName);
        Assert.Equal(91, codex.Primary!.UsedPercent);
        Assert.Equal(true, codex.HasCredits);
        Assert.Equal("12.50", codex.CreditBalance);

        // The protocol has no value for "no longer reached", so this is taken from each update and never kept.
        Assert.Equal(true, snapshots[0].LimitReached);
        Assert.Null(codex.LimitReached);
    }

    [Fact]
    public async Task A_rate_limit_update_is_merged_into_the_reading_of_its_own_limit()
    {
        using var fixture = new AgentFixture()
            .Codex(c =>
            {
                c["rateLimits"] = new JsonObject { ["limitId"] = "codex", ["planType"] = "pro", ["primary"] = Step.LimitWindow(12) };
                c["rateLimitsByLimitId"] = new JsonObject
                {
                    ["codex"] = new JsonObject { ["limitId"] = "codex", ["planType"] = "pro", ["primary"] = Step.LimitWindow(12) },
                    ["codex_other"] = new JsonObject { ["limitId"] = "codex_other", ["limitName"] = "Other model", ["primary"] = Step.LimitWindow(70) },
                };
            })
            .ImplementerTurn(
                Step.RateLimitUpdate(new JsonObject { ["limitId"] = "codex_other", ["secondary"] = Step.LimitWindow(10, 10080) }),
                Step.RateLimitUpdate(new JsonObject { ["secondary"] = Step.LimitWindow(33, 10080) }),
                Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        Assert.Equal(12, (await adapter.GetRateLimitsAsync(CancellationToken.None))!.Primary!.UsedPercent);
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var snapshots = events.OfType<RateLimitUpdated>().Select(u => u.Snapshot).ToList();
        Assert.Equal("Other model", snapshots[0].LimitName);
        Assert.Equal(70, snapshots[0].Primary!.UsedPercent);
        Assert.Equal(10, snapshots[0].Secondary!.UsedPercent);

        // An update that names no limit is about the one the reading gave as the account's own.
        Assert.Equal(12, snapshots[1].Primary!.UsedPercent);
        Assert.Equal(33, snapshots[1].Secondary!.UsedPercent);
    }

    [Fact]
    public async Task Warnings_that_name_no_conversation_reach_every_open_conversation()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.WarningForAll("The model catalog could not be refreshed."),
            Step.ConfigWarning("Unknown key 'modle'", details: "Did you mean 'model'?", path: @"C:\Users\me\.codex\config.toml"),
            Step.DeprecationNotice("The approval policy 'on-failure' is deprecated.", details: "Use 'on-request'."),
            Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var implementer = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);
        await using var reviewer = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer), CancellationToken.None);

        var events = await implementer.RunTurnAsync("go");

        var told = events.OfType<AgentNotice>().Where(n => n.IsWarning).Select(n => n.Message).ToList();
        Assert.Equal(3, told.Count);
        Assert.Equal("The model catalog could not be refreshed.", told[0]);
        Assert.Contains("Unknown key 'modle'", told[1], StringComparison.Ordinal);
        Assert.Contains("Did you mean 'model'?", told[1], StringComparison.Ordinal);
        Assert.Contains(@"C:\Users\me\.codex\config.toml", told[1], StringComparison.Ordinal);
        Assert.Contains("'on-failure' is deprecated", told[2], StringComparison.Ordinal);
        Assert.Contains("Use 'on-request'.", told[2], StringComparison.Ordinal);

        var alsoTold = new List<string>();
        while (reviewer.Events.TryRead(out var said))
        {
            if (said is AgentNotice notice)
            {
                alsoTold.Add(notice.Message);
            }
        }

        Assert.Equal(told, alsoTold);
    }

    [Fact]
    public async Task A_sub_agent_is_announced_once_on_its_parent_conversation_and_its_work_is_not_shown()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.SubAgent("Found the login handler.", resumed: true), Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var notice = Assert.Single(events.OfType<AgentNotice>());
        Assert.True(notice.IsWarning);
        Assert.Contains("started a sub-agent", notice.Message, StringComparison.Ordinal);
        Assert.Contains("neither shows nor counts what it does", notice.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(events.OfType<AssistantMessage>(), m => m.Text == "Found the login handler.");
        Assert.Empty(events.OfType<UsageUpdated>());
        Assert.Equal("Done.", events.Completion().FinalMessage);
    }

    [Fact]
    public async Task A_sub_agent_that_asks_for_approval_is_refused_and_the_user_is_told_once_for_it()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.SubAgent("Looked around.", asks: 2), Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var told = Assert.Single(events.OfType<AgentNotice>(), n => n.Message.Contains("asked for approval", StringComparison.Ordinal));
        Assert.True(told.IsWarning);
        Assert.Contains("Scout", told.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(events, e => e is ApprovalRequested);
        var answers = fixture.Received("codex.response");
        Assert.Equal(2, answers.Count);
        Assert.All(answers, answer => Assert.Equal(-32601, answer["error"]!["code"]!.GetValue<int>()));
        Assert.Equal("Done.", events.Completion().FinalMessage);
    }

    [Fact]
    public async Task A_conversation_is_given_up_when_codex_says_that_its_own_reviewer_decided_for_one_of_its_sub_agents()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.SubAgent("Looked around.", autoReview: "item/autoApprovalReview/completed"), Step.Hang());
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var ended = Assert.IsType<SessionEnded>(events[^1]);
        Assert.Contains("'item/autoApprovalReview/completed'", ended.Reason, StringComparison.Ordinal);
        Assert.Contains("sub-agent", ended.Reason, StringComparison.Ordinal);
        Assert.Contains("only you decide about access", ended.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_settings_change_during_a_conversation_is_reported_as_what_is_now_in_effect()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.SettingsUpdated(
                model: "model-b",
                effort: "low",
                sandboxPolicy: new JsonObject { ["type"] = "workspaceWrite", ["writableRoots"] = new JsonArray { @"C:\elsewhere" }, ["networkAccess"] = true }),
            Step.Message("Done."));
        File.WriteAllText(fixture.InWorkspace("AGENTS.md"), "# Project rules");
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, model: "model-a", effort: "xhigh"), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var reports = events.OfType<SessionConfigured>().ToList();
        Assert.Equal(2, reports.Count);
        var now = reports[1].Effective;
        Assert.Equal("model-b", now.Model);
        Assert.Equal("low", now.Effort);
        Assert.Equal("workspace-write", now.Sandbox);
        Assert.True(now.Widening!.NetworkAccess);
        Assert.Equal([@"C:\elsewhere"], now.Widening.AdditionalWritableRoots);
        Assert.Equal("on-request", now.ApprovalPolicy);
        Assert.Equal("user", now.ApprovalsReviewer);
        Assert.Contains("thread/settings/updated", now.Source, StringComparison.Ordinal);

        // What the notification does not carry is kept from the start of the conversation.
        Assert.Equal([fixture.InWorkspace("AGENTS.md")], now.InstructionSources);
        Assert.Same(now, session.Effective);
        Assert.Equal(TurnOutcome.Completed, events.Completion().Outcome);
    }

    [Theory]
    [InlineData("apikey", null, AccountRouteKind.ApiKey, "OpenAI API key")]
    [InlineData("chatgpt", "plus", AccountRouteKind.Subscription, "ChatGPT plan (plus)")]
    [InlineData("bedrockApiKey", null, AccountRouteKind.CloudProvider, "Amazon Bedrock")]
    [InlineData(null, null, AccountRouteKind.NotAuthenticated, "Not signed in")]
    [InlineData("personalAccessToken", null, AccountRouteKind.Unknown, "'personalAccessToken'")]
    public async Task When_codex_says_that_its_account_changed_the_conversation_reports_what_is_in_effect_again(
        string? authMode, string? plan, AccountRouteKind route, string described)
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.AccountUpdated(authMode, plan), Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var reports = events.OfType<SessionConfigured>().ToList();
        Assert.Equal(2, reports.Count);
        Assert.Equal(AccountRouteKind.Subscription, reports[0].Effective.Account!.Route);
        var now = reports[1].Effective;
        Assert.Equal(route, now.Account!.Route);
        Assert.Contains(described, now.Account.Description, StringComparison.Ordinal);
        Assert.Equal("account/updated", now.Account.Source);

        // Only the account changed.
        Assert.Equal(reports[0].Effective with { Account = now.Account }, now);
        Assert.Same(now, session.Effective);
    }

    [Fact]
    public async Task A_conversation_is_given_up_when_codex_hands_its_approvals_to_its_own_reviewer()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.Message("Working...", "commentary"),
            Step.SettingsUpdated(approvalsReviewer: "auto_review"),
            Step.Hang());
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        Assert.Equal("auto_review", events.OfType<SessionConfigured>().Last().Effective.ApprovalsReviewer);
        var ended = Assert.IsType<SessionEnded>(events[^1]);
        Assert.Contains("'auto_review'", ended.Reason, StringComparison.Ordinal);
        Assert.Contains("only you decide about access", ended.Reason, StringComparison.Ordinal);

        // The turn that was running is stopped through the provider, and nothing more is sent in this conversation.
        for (var attempt = 0; attempt < 100 && fixture.CodexRequests("turn/interrupt").Count == 0; attempt++)
        {
            await Task.Delay(100);
        }

        Assert.Single(fixture.CodexRequests("turn/interrupt"));
        var refused = await Assert.ThrowsAsync<AgentProtocolException>(() => session.StartTurnAsync(new TurnRequest("again", null, []), CancellationToken.None));
        Assert.True(refused.Refused);
        Assert.Single(fixture.CodexRequests("turn/start"));
    }

    [Theory]
    [InlineData("item/autoApprovalReview/started")]
    [InlineData("item/autoApprovalReview/completed")]
    [InlineData("autoApprovalReview/strictReviewRequired")]
    [InlineData("guardianWarning")]
    public async Task A_conversation_is_given_up_when_codex_says_that_its_own_reviewer_takes_part_in_deciding_about_access(string method)
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Working...", "commentary"), Step.AutoReview(method), Step.Hang());
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var ended = Assert.IsType<SessionEnded>(events[^1]);
        Assert.Contains($"'{method}'", ended.Reason, StringComparison.Ordinal);
        Assert.Contains("only you decide about access", ended.Reason, StringComparison.Ordinal);
        for (var attempt = 0; attempt < 100 && fixture.CodexRequests("turn/interrupt").Count == 0; attempt++)
        {
            await Task.Delay(100);
        }

        Assert.Single(fixture.CodexRequests("turn/interrupt"));
    }

    [Fact]
    public async Task A_conversation_is_given_up_when_a_change_of_its_settings_does_not_say_who_decides_about_access()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.Message("Working...", "commentary"),
            Step.SettingsUpdated(withoutApprovalsReviewer: true),
            Step.Hang());
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var ended = Assert.IsType<SessionEnded>(events[^1]);
        Assert.Contains("does not say who decides", ended.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_conversation_is_given_up_when_a_change_of_its_settings_reports_a_sandbox_that_cannot_be_read()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.Message("Working...", "commentary"),
            Step.SettingsUpdated(sandboxPolicy: new JsonObject { ["type"] = "workspaceWrite", ["writableRoots"] = new JsonArray(), ["networkAccess"] = "on" }),
            Step.Hang());
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var ended = Assert.IsType<SessionEnded>(events[^1]);
        Assert.Contains("what the sandbox may reach is not known", ended.Reason, StringComparison.Ordinal);
        Assert.Contains("'networkAccess'", ended.Reason, StringComparison.Ordinal);
    }
}

/// <summary>What Codex says to every conversation, and what it says before a conversation is known by its id.</summary>
public class CodexWarningTests
{
    [Fact]
    public async Task Warnings_codex_sends_right_after_it_starts_reach_every_conversation_opened_later_once()
    {
        using var fixture = new AgentFixture()
            .Codex(c => c["afterInitialize"] = new JsonArray
            {
                Step.Notification("configWarning", new JsonObject { ["summary"] = "Unknown key 'modle'", ["details"] = "Did you mean 'model'?", ["path"] = null, ["range"] = null }),
                Step.Notification("warning", new JsonObject { ["message"] = "The model catalog could not be refreshed.", ["threadId"] = null }),
                Step.Notification("deprecationNotice", new JsonObject { ["summary"] = "The approval policy 'on-failure' is deprecated.", ["details"] = null }),
            })
            .ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();

        // The first connection is made before any conversation exists.
        await adapter.GetAuthStatusAsync(CancellationToken.None);
        await using var first = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);
        await using var second = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer), CancellationToken.None);
        var firstEvents = await first.RunTurnAsync("go");

        var told = Warnings(firstEvents);
        Assert.Equal(3, told.Count);
        Assert.Contains("Unknown key 'modle'", told[0], StringComparison.Ordinal);
        Assert.Equal("The model catalog could not be refreshed.", told[1]);
        Assert.Contains("'on-failure' is deprecated", told[2], StringComparison.Ordinal);
        Assert.Equal(told, Warnings(Available(second)));
    }

    [Fact]
    public async Task A_warning_that_names_no_conversation_reaches_a_conversation_opened_after_it_once()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.WarningForAll("The model catalog could not be refreshed."), Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var first = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);
        var firstEvents = await first.RunTurnAsync("go");

        await using var second = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer), CancellationToken.None);

        Assert.Equal(["The model catalog could not be refreshed."], Warnings(firstEvents));
        Assert.Equal(["The model catalog could not be refreshed."], Warnings(Available(second)));
        Assert.Empty(Warnings(Available(first)));
    }

    [Fact]
    public async Task Only_the_last_twenty_warnings_are_kept_for_conversations_opened_later()
    {
        var warnings = Enumerable.Range(1, 25).Select(i => $"Warning {i}.").ToList();
        using var fixture = new AgentFixture().Codex(c => c["afterInitialize"] = new JsonArray(
            warnings.Select(w => (JsonNode?)Step.Notification("warning", new JsonObject { ["message"] = w, ["threadId"] = null })).ToArray()));
        await using var adapter = fixture.CodexAppServer();

        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        Assert.Equal(warnings.Skip(5), Warnings(Available(session)));
    }

    [Fact]
    public async Task A_warning_that_the_windows_sandbox_cannot_protect_folders_names_some_and_counts_the_rest()
    {
        var folders = Enumerable.Range(1, 7).Select(i => $@"C:\shared\folder {i}").ToArray();
        using var fixture = new AgentFixture().Codex(c => c["afterInitialize"] = new JsonArray { Step.WorldWritableWarning(folders, extraCount: 3, failedScan: true) });
        await using var adapter = fixture.CodexAppServer();

        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var warning = Assert.Single(Warnings(Available(session)));
        Assert.Contains("cannot protect", warning, StringComparison.Ordinal);
        foreach (var named in folders.Take(5))
        {
            Assert.Contains(named, warning, StringComparison.Ordinal);
        }

        Assert.DoesNotContain(folders[5], warning, StringComparison.Ordinal);
        Assert.Contains("and 5 more", warning, StringComparison.Ordinal);
        Assert.Contains("could not check every folder", warning, StringComparison.Ordinal);
    }

    [Fact]
    public async Task What_codex_says_about_a_conversation_right_after_opening_it_reaches_the_conversation()
    {
        using var fixture = new AgentFixture().Codex(c => c["afterThreadStart"] = new JsonArray
        {
            Step.Notification("warning", new JsonObject { ["message"] = "The effort xhigh is not supported; high is used." }, forThread: true),
            Step.Notification("warning", new JsonObject { ["message"] = "The project's AGENTS.md is very long." }, forThread: true),
        });
        await using var adapter = fixture.CodexAppServer();

        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        Assert.Equal(["The effort xhigh is not supported; high is used.", "The project's AGENTS.md is very long."], Warnings(await UntilAsync(session, 2)));
    }

    [Fact]
    public async Task What_codex_asks_for_a_conversation_that_is_refused_while_it_is_opened_is_still_answered()
    {
        using var fixture = new AgentFixture().Codex(c =>
        {
            c["omitApprovalsReviewer"] = true;
            c["afterThreadStart"] = new JsonArray { Step.UnansweredCommandApproval("rm -rf build") };
        });
        await using var adapter = fixture.CodexAppServer();

        await Assert.ThrowsAsync<AgentProtocolException>(() => adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None));

        // Nobody will use the conversation, so what Codex asked in it is refused rather than left waiting.
        for (var attempt = 0; attempt < 200 && fixture.Received("codex.response").Count == 0; attempt++)
        {
            await Task.Delay(50);
        }

        Assert.Equal(-32601, Assert.Single(fixture.Received("codex.response"))["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_change_of_who_decides_about_access_right_after_opening_a_conversation_is_not_missed()
    {
        using var fixture = new AgentFixture().Codex(c => c["afterThreadStart"] = new JsonArray { Step.SettingsUpdated(approvalsReviewer: "auto_review") });
        await using var adapter = fixture.CodexAppServer();

        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var ended = Assert.IsType<SessionEnded>((await UntilEndAsync(session))[^1]);
        Assert.Contains("'auto_review'", ended.Reason, StringComparison.Ordinal);
        var refused = await Assert.ThrowsAsync<AgentProtocolException>(() => session.StartTurnAsync(new TurnRequest("go", null, []), CancellationToken.None));
        Assert.True(refused.Refused);
    }

    private static List<AgentEvent> Available(IAgentSession session)
    {
        var events = new List<AgentEvent>();
        while (session.Events.TryRead(out var next))
        {
            events.Add(next);
        }

        return events;
    }

    /// <summary>The events of a session until it has given the number of warnings, or until it ended.</summary>
    private static async Task<List<AgentEvent>> UntilAsync(IAgentSession session, int warnings)
    {
        var events = new List<AgentEvent>();
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await foreach (var next in session.Events.ReadAllAsync(patience.Token))
            {
                events.Add(next);
                if (Warnings(events).Count >= warnings || next is SessionEnded)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // What arrived is what is checked.
        }

        return events;
    }

    private static async Task<List<AgentEvent>> UntilEndAsync(IAgentSession session)
    {
        var events = new List<AgentEvent>();
        using var patience = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        try
        {
            await foreach (var next in session.Events.ReadAllAsync(patience.Token))
            {
                events.Add(next);
            }
        }
        catch (OperationCanceledException)
        {
            events.Add(new AgentNotice(DateTimeOffset.MinValue, "The session did not end.", IsWarning: false));
        }

        return events;
    }

    private static List<string> Warnings(IEnumerable<AgentEvent> events) =>
        events.OfType<AgentNotice>().Where(n => n.IsWarning).Select(n => n.Message).ToList();
}

public class CodexApprovalAndControlTests
{
    [Fact]
    public async Task An_accepted_approval_lets_the_action_happen()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.Approval("npm install left-pad", onAccept: [Step.Write("installed.txt", "yes")], onDecline: [Step.Write("declined.txt", "no")], reason: "needs network access"),
            Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
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

        Assert.NotNull(asked);
        Assert.Equal(ApprovalKind.CommandExecution, asked.Kind);
        Assert.Equal("npm install left-pad", asked.Command);
        Assert.Equal("needs network access", asked.Reason);
        Assert.True(File.Exists(fixture.InWorkspace("installed.txt")));
        Assert.False(File.Exists(fixture.InWorkspace("declined.txt")));
        var response = Assert.Single(fixture.Received("codex.response"));
        Assert.Equal("accept", response["result"]!["decision"]!.GetValue<string>());
        Assert.Equal(TurnOutcome.Completed, events.Completion().Outcome);
    }

    [Theory]
    [InlineData(ApprovalDecision.Decline, "decline", TurnOutcome.Completed)]
    [InlineData(ApprovalDecision.Cancel, "cancel", TurnOutcome.Interrupted)]
    [InlineData(ApprovalDecision.AcceptForSession, "acceptForSession", TurnOutcome.Completed)]
    public async Task Each_decision_is_sent_as_the_value_the_protocol_defines(ApprovalDecision decision, string wire, TurnOutcome outcome)
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.Approval("rm -rf build", onAccept: [Step.Write("ran.txt", "x")]),
            Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go", onEvent: async e =>
        {
            if (e is ApprovalRequested requested)
            {
                await session.RespondToApprovalAsync(requested.Request.ApprovalId, decision, CancellationToken.None);
            }
        });

        Assert.Equal(wire, Assert.Single(fixture.Received("codex.response"))["result"]!["decision"]!.GetValue<string>());
        Assert.Equal(outcome, events.Completion().Outcome);
        Assert.Equal(decision == ApprovalDecision.AcceptForSession, File.Exists(fixture.InWorkspace("ran.txt")));
    }

    [Fact]
    public async Task A_file_change_approval_is_recognized_as_such()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Approval(string.Empty, kind: "file", reason: "write outside the workspace"), Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
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

        Assert.Equal(ApprovalKind.FileChange, asked!.Kind);
        Assert.Equal("write outside the workspace", asked.Reason);
    }

    [Fact]
    public async Task An_approval_for_a_file_change_names_the_files()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.FileChangeApproval(
                [Step.Changed("src/login.txt"), Step.Changed("src/new ünï.txt", "add"), Step.Changed("old.txt", "delete"), Step.Changed("a.txt", movedTo: "b.txt")],
                reason: "write outside the workspace",
                grantRoot: @"C:\outside"),
            Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var asked = await FirstApprovalAsync(session);

        Assert.Equal(ApprovalKind.FileChange, asked.Kind);
        Assert.Equal(
            [
                @"Write access below C:\outside",
                "Update " + Full(fixture, "src/login.txt"),
                "Add " + Full(fixture, "src/new ünï.txt"),
                "Delete " + Full(fixture, "old.txt"),
                $"Move {Full(fixture, "a.txt")} to {Full(fixture, "b.txt")}",
            ],
            asked.Details);
    }

    [Fact]
    public async Task The_files_of_a_file_change_approval_are_those_of_the_latest_patch()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.FileChangeApproval([Step.Changed("second.txt")], announced: [Step.Changed("first.txt")]),
            Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var asked = await FirstApprovalAsync(session);

        Assert.Equal(["Update " + Full(fixture, "second.txt")], asked.Details);
    }

    [Fact]
    public async Task An_approval_for_many_files_names_twenty_and_counts_the_rest()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.FileChangeApproval(Enumerable.Range(1, 25).Select(i => Step.Changed($"src/file{i:00}.txt")).ToArray()),
            Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var asked = await FirstApprovalAsync(session);

        Assert.Equal(21, asked.Details.Count);
        Assert.Equal("Update " + Full(fixture, "src/file01.txt"), asked.Details[0]);
        Assert.Equal("Update " + Full(fixture, "src/file20.txt"), asked.Details[19]);
        Assert.Equal("and 5 more", asked.Details[20]);

        // Files that are not named are not allowed in passing, and not for the rest of the conversation.
        Assert.True(asked.Deliberate);
        Assert.False(asked.CanAcceptForSession);
    }

    [Fact]
    public async Task An_approval_that_names_every_file_can_be_given_with_one_answer()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.FileChangeApproval(Enumerable.Range(1, 20).Select(i => Step.Changed($"src/file{i:00}.txt")).ToArray()),
            Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var asked = await FirstApprovalAsync(session);

        Assert.Equal(20, asked.Details.Count);
        Assert.False(asked.Deliberate);
        Assert.True(asked.CanAcceptForSession);
    }

    [Fact]
    public async Task An_approval_for_a_change_that_was_not_announced_says_that_the_files_are_not_known()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.FileChangeApproval([Step.Changed("src/login.txt")], announce: false),
            Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var asked = await FirstApprovalAsync(session);

        Assert.Equal(["Codex did not say which files it would change."], asked.Details);
    }

    [Theory]
    [InlineData("writeStdin", "npm test", null, "Send input to a running command")]
    [InlineData("command", null, "registry.npmjs.org", "Allow network access to registry.npmjs.org")]
    [InlineData("command", null, null, "Run a command that Codex did not name")]
    [InlineData("command", "npm test", null, "Run a command")]
    [InlineData("futureKind", "npm test", null, "Approve a request of the kind 'futureKind', which this version of YAV does not know")]
    public async Task A_command_approval_is_titled_by_what_it_asks_for(string kind, string? command, string? host, string title)
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.CommandApproval(command, kind, host), Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var asked = await FirstApprovalAsync(session);

        Assert.Equal(ApprovalKind.CommandExecution, asked.Kind);
        Assert.Equal(title, asked.Title);
        Assert.Equal(command, asked.Command);
        Assert.Equal(kind == "writeStdin", asked.Details.Any(d => d.Contains("already running", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData("command", true)]
    [InlineData("writeStdin", true)]
    [InlineData("futureKind", false)]
    public async Task Only_an_approval_of_a_kind_yav_knows_can_be_given_for_the_whole_conversation(string kind, bool known)
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.CommandApproval("npm test", kind), Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var asked = await FirstApprovalAsync(session);

        Assert.Equal(known, asked.CanAcceptForSession);
        Assert.Equal(!known, asked.Deliberate);
    }

    [Fact]
    public async Task A_change_of_a_kind_yav_does_not_know_is_named_as_codex_names_it_and_is_not_given_for_the_conversation()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.FileChangeApproval([Step.Changed("src/login.txt"), Step.Changed("src/data.bin", "truncate")]),
            Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var asked = await FirstApprovalAsync(session);

        Assert.Equal("Update " + Full(fixture, "src/login.txt"), asked.Details[0]);
        Assert.Contains("'truncate'", asked.Details[1], StringComparison.Ordinal);
        Assert.Contains(Full(fixture, "src/data.bin"), asked.Details[1], StringComparison.Ordinal);
        Assert.False(asked.Details[1].StartsWith("Update ", StringComparison.Ordinal));
        Assert.False(asked.CanAcceptForSession);
        Assert.True(asked.Deliberate);
    }

    /// <summary>Runs a turn, declines every approval request and returns the first one.</summary>
    private static async Task<ApprovalRequest> FirstApprovalAsync(IAgentSession session)
    {
        ApprovalRequest? asked = null;
        await session.RunTurnAsync("go", onEvent: async e =>
        {
            if (e is ApprovalRequested requested)
            {
                asked ??= requested.Request;
                await session.RespondToApprovalAsync(requested.Request.ApprovalId, ApprovalDecision.Decline, CancellationToken.None);
            }
        });

        Assert.NotNull(asked);
        return asked;
    }

    private static string Full(AgentFixture fixture, string relative) => Path.GetFullPath(fixture.InWorkspace(relative));

    [Fact]
    public async Task The_reviewer_is_never_given_more_access_whatever_the_agent_asks_for()
    {
        using var fixture = new AgentFixture().ReviewerTurn(
            Step.Approval("git commit -am wip", onAccept: [Step.Write("reviewer-wrote.txt", "x")]),
            Step.Review("pass"));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer), CancellationToken.None);

        var events = await session.RunTurnAsync("review", ReviewSchema.AsElement());

        Assert.DoesNotContain(events, e => e is ApprovalRequested);
        Assert.Equal("decline", Assert.Single(fixture.Received("codex.response"))["result"]!["decision"]!.GetValue<string>());
        Assert.False(File.Exists(fixture.InWorkspace("reviewer-wrote.txt")));
        Assert.Contains(events.OfType<AgentNotice>(), n => n.IsWarning && n.Message.Contains("declined"));
    }

    [Fact]
    public async Task The_reviewer_is_granted_no_permission_whatever_it_asks_for()
    {
        using var fixture = new AgentFixture().ReviewerTurn(
            Step.PermissionsApproval(WiderAccess(), onAccept: [Step.Write("reviewer-wrote.txt", "x")]),
            Step.Review("pass"));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer), CancellationToken.None);

        var events = await session.RunTurnAsync("review", ReviewSchema.AsElement());

        Assert.DoesNotContain(events, e => e is ApprovalRequested);
        var answer = Assert.Single(fixture.Received("codex.response"))["result"]!.AsObject();
        Assert.Empty(answer["permissions"]!.AsObject());
        Assert.NotEqual("session", answer["scope"]?.GetValue<string>());
        Assert.False(File.Exists(fixture.InWorkspace("reviewer-wrote.txt")));
        Assert.Contains(events.OfType<AgentNotice>(), n => n.IsWarning && n.Message.Contains("declined", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(ApprovalDecision.Accept)]
    [InlineData(ApprovalDecision.AcceptForSession)]
    public async Task Permissions_the_user_grants_are_sent_back_as_asked_and_for_the_turn_only(ApprovalDecision decision)
    {
        var asked = WiderAccess();
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.PermissionsApproval(asked, onAccept: [Step.Write("granted.txt", "x")], reason: "needs the shared cache"),
            Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        ApprovalRequest? request = null;
        await session.RunTurnAsync("go", onEvent: async e =>
        {
            if (e is ApprovalRequested requested)
            {
                request = requested.Request;
                await session.RespondToApprovalAsync(requested.Request.ApprovalId, decision, CancellationToken.None);
            }
        });

        Assert.Equal(ApprovalKind.Permissions, request!.Kind);
        Assert.Equal("needs the shared cache", request.Reason);
        Assert.False(request.CanAcceptForSession);
        Assert.Contains(request.Details, d => d.StartsWith("Requested permissions: ", StringComparison.Ordinal));

        // What was asked is granted, nothing more, and never for the rest of the conversation: that was not offered.
        var answer = Assert.Single(fixture.Received("codex.response"))["result"]!.AsObject();
        Assert.True(JsonNode.DeepEquals(asked, answer["permissions"]));
        Assert.Equal("turn", answer["scope"]!.GetValue<string>());
        Assert.True(File.Exists(fixture.InWorkspace("granted.txt")));
    }

    [Fact]
    public async Task Permissions_the_user_declines_are_not_granted()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.PermissionsApproval(WiderAccess(), onAccept: [Step.Write("granted.txt", "x")]),
            Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        await FirstApprovalAsync(session);

        var answer = Assert.Single(fixture.Received("codex.response"))["result"]!.AsObject();
        Assert.Empty(answer["permissions"]!.AsObject());
        Assert.False(File.Exists(fixture.InWorkspace("granted.txt")));
    }

    /// <summary>More than a sandbox gives: the network, and writing to a folder outside the workspace.</summary>
    private static JsonObject WiderAccess() => new()
    {
        ["network"] = new JsonObject { ["enabled"] = true },
        ["fileSystem"] = new JsonObject { ["read"] = null, ["write"] = new JsonArray { @"C:\shared\cache" } },
    };

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_request_of_codex_is_answered_and_withdrawn_under_the_id_it_came_with_a_number_or_a_text(bool textIds)
    {
        using var fixture = new AgentFixture()
            .Codex(c => c["stringRequestIds"] = textIds)
            .ImplementerTurn(
                Step.Approval("npm test", onAccept: [Step.Write("ran.txt", "x")]),
                Step.WithdrawnApproval("npm run lint"),
                Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var answered = false;
        var events = await session.RunTurnAsync("go", onEvent: async e =>
        {
            if (e is ApprovalRequested requested && !answered)
            {
                answered = true;
                await session.RespondToApprovalAsync(requested.Request.ApprovalId, ApprovalDecision.Accept, CancellationToken.None);
            }
        });

        var asked = fixture.Received("codex.response");
        var id = Assert.Single(asked)["id"]!;
        Assert.Equal(textIds ? JsonValueKind.String : JsonValueKind.Number, id.GetValueKind());
        Assert.True(File.Exists(fixture.InWorkspace("ran.txt")));
        var withdrawn = events.OfType<ApprovalRequested>().Last().Request.ApprovalId;
        Assert.Contains(events.OfType<ApprovalWithdrawn>(), w => w.ApprovalId == withdrawn);
    }

    [Fact]
    public async Task Answering_an_approval_that_was_never_asked_is_refused()
    {
        using var fixture = new AgentFixture();
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.RespondToApprovalAsync("srv-999", ApprovalDecision.Accept, CancellationToken.None));

        Assert.Empty(fixture.Received("codex.response"));
    }

    [Fact]
    public async Task Interrupting_stops_the_turn_through_the_provider()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Working...", "commentary"), Step.Hang());
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go", onEvent: async e =>
        {
            if (e is AssistantMessage)
            {
                await session.InterruptAsync(CancellationToken.None);
            }
        });

        Assert.Equal(TurnOutcome.Interrupted, events.Completion().Outcome);
        var interrupt = Assert.Single(fixture.CodexRequests("turn/interrupt"))["params"]!;
        Assert.Equal(session.SessionId, interrupt["threadId"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(interrupt["turnId"]!.GetValue<string>()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task A_turn_that_ignores_the_interrupt_is_ended_with_the_agents_own_process_after_the_grace_period(bool reportsWhileEnding)
    {
        // Ending the process closes its input first. An agent can then still report the turn as interrupted before
        // it is gone; the turn did not stop when it was asked, and it ends with the process all the same.
        using var fixture = new AgentFixture()
            .Codex(c =>
            {
                c["ignoreInterrupt"] = true;
                c["reportTurnWhenInputEnds"] = reportsWhileEnding;
            })
            .ImplementerTurn(Step.Message("Working...", "commentary"), Step.Hang());
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);
        await session.StartTurnAsync(new TurnRequest("go", null, []), CancellationToken.None);
        var seen = new List<AgentEvent>();
        await foreach (var item in session.Events.ReadAllAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token))
        {
            seen.Add(item);
            if (item is AssistantMessage)
            {
                break;
            }
        }

        var agent = fixture.CodexRequests("turn/start")[0]["_pid"]!.GetValue<int>();
        var watch = System.Diagnostics.Stopwatch.StartNew();

        await session.ShutdownAsync(TimeSpan.FromSeconds(1), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.InRange(watch.Elapsed, TimeSpan.FromMilliseconds(900), TimeSpan.FromSeconds(15));
        Assert.Single(fixture.CodexRequests("turn/interrupt"));
        for (var attempt = 0; attempt < 50 && ProcessRunner.IsProcessAlive(agent, "yav-fake-agent"); attempt++)
        {
            await Task.Delay(100);
        }

        Assert.False(ProcessRunner.IsProcessAlive(agent, "yav-fake-agent"));
        var rest = new List<AgentEvent>();
        await foreach (var item in session.Events.ReadAllAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token))
        {
            rest.Add(item);
        }

        Assert.Equal(TurnErrorCodes.AgentExited, rest.OfType<TurnCompleted>().Single().ErrorCode);
        Assert.IsType<SessionEnded>(rest[^1]);
    }

    [Fact]
    public async Task After_an_agent_process_was_ended_the_next_session_gets_a_new_one()
    {
        using var fixture = new AgentFixture()
            .Codex(c => c["ignoreInterrupt"] = true)
            .ImplementerTurn(Step.Message("Working...", "commentary"), Step.Hang())
            .ImplementerTurn(Step.Message("Second process, done."));
        await using var adapter = fixture.CodexAppServer();
        var first = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);
        await first.StartTurnAsync(new TurnRequest("go", null, []), CancellationToken.None);
        await foreach (var item in first.Events.ReadAllAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token))
        {
            if (item is AssistantMessage)
            {
                break;
            }
        }

        await first.ShutdownAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));
        await first.DisposeAsync();

        await using var second = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);
        var events = await second.RunTurnAsync("again");

        Assert.Equal("Second process, done.", events.Completion().FinalMessage);
        var processes = fixture.CodexRequests("initialize").Select(r => r["_pid"]!.GetValue<int>()).Distinct().ToList();
        Assert.Equal(2, processes.Count);
    }

    [Fact]
    public async Task A_turn_that_stops_when_asked_leaves_the_agent_process_running()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Working...", "commentary"), Step.Hang());
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);
        await session.StartTurnAsync(new TurnRequest("go", null, []), CancellationToken.None);
        await foreach (var item in session.Events.ReadAllAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token))
        {
            if (item is AssistantMessage)
            {
                break;
            }
        }

        var agent = fixture.CodexRequests("turn/start")[0]["_pid"]!.GetValue<int>();

        await session.ShutdownAsync(TimeSpan.FromSeconds(5), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(30));

        Assert.True(ProcessRunner.IsProcessAlive(agent, "yav-fake-agent"));
        Assert.Equal(TurnOutcome.Interrupted, (await session.ReadTurnAsync()).Completion().Outcome);
    }

    [Fact]
    public async Task The_session_stays_usable_after_an_interrupted_turn()
    {
        using var fixture = new AgentFixture()
            .ImplementerTurn(Step.Message("Working...", "commentary"), Step.Hang())
            .ImplementerTurn(Step.Message("Second turn done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);
        await session.RunTurnAsync("go", onEvent: async e =>
        {
            if (e is AssistantMessage)
            {
                await session.InterruptAsync(CancellationToken.None);
            }
        });

        var events = await session.RunTurnAsync("continue");

        Assert.Equal("Second turn done.", events.Completion().FinalMessage);
    }

    [Fact]
    public async Task Steering_adds_input_to_the_running_turn()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Working...", "commentary"), Step.Sleep(1500), Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        bool? steered = null;
        await session.RunTurnAsync("go", onEvent: async e =>
        {
            if (e is AssistantMessage { Phase: MessagePhase.Commentary })
            {
                steered = await session.SteerAsync("Also update the changelog.", CancellationToken.None);
            }
        });

        Assert.True(steered);
        var steer = Assert.Single(fixture.CodexRequests("turn/steer"))["params"]!;
        Assert.Equal("Also update the changelog.", steer["input"]![0]!["text"]!.GetValue<string>());
        Assert.False(string.IsNullOrEmpty(steer["expectedTurnId"]!.GetValue<string>()));
    }

    [Fact]
    public async Task Steering_without_a_running_turn_is_not_possible()
    {
        using var fixture = new AgentFixture();
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        Assert.False(await session.SteerAsync("hello", CancellationToken.None));
    }

    [Fact]
    public async Task A_question_of_an_mcp_server_is_declined_and_the_user_is_told()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Elicitation("docs", "Which page should I open?"), Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var told = Assert.Single(events.OfType<AgentNotice>(), n => n.Message.Contains("'docs'", StringComparison.Ordinal));
        Assert.True(told.IsWarning);
        Assert.Contains("Which page should I open?", told.Message, StringComparison.Ordinal);
        Assert.Contains("declined", told.Message, StringComparison.Ordinal);
        Assert.Equal("decline", Assert.Single(fixture.Received("codex.response"))["result"]!["action"]!.GetValue<string>());
        Assert.Equal("Done.", events.Completion().FinalMessage);
    }

    [Fact]
    public async Task A_request_this_version_does_not_handle_is_refused_and_the_user_is_told()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.ServerRequest("item/tool/requestUserInput", new JsonObject { ["itemId"] = "item-9", ["questions"] = new JsonArray() }, wait: true),
            Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var told = Assert.Single(events.OfType<AgentNotice>(), n => n.Message.Contains("'item/tool/requestUserInput'", StringComparison.Ordinal));
        Assert.True(told.IsWarning);
        Assert.Contains("refused", told.Message, StringComparison.Ordinal);
        Assert.Equal(-32601, Assert.Single(fixture.Received("codex.response"))["error"]!["code"]!.GetValue<int>());
    }

    [Fact]
    public async Task A_turn_is_asked_to_stop_once_however_often_it_is_interrupted()
    {
        using var fixture = new AgentFixture()
            .Codex(c => c["ignoreInterrupt"] = true)
            .ImplementerTurn(Step.Message("Working...", "commentary"), Step.Hang());
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);
        await session.StartTurnAsync(new TurnRequest("go", null, []), CancellationToken.None);
        await foreach (var item in session.Events.ReadAllAsync(new CancellationTokenSource(TimeSpan.FromSeconds(30)).Token))
        {
            if (item is AssistantMessage)
            {
                break;
            }
        }

        await session.InterruptAsync(CancellationToken.None);
        await session.InterruptAsync(CancellationToken.None);

        Assert.Single(fixture.CodexRequests("turn/interrupt"));
    }

    [Fact]
    public async Task What_a_conversation_that_was_given_up_is_asked_afterwards_is_answered_with_cancel()
    {
        using var fixture = new AgentFixture()
            .Codex(c => c["ignoreInterrupt"] = true)
            .ImplementerTurn(
                Step.Message("Working...", "commentary"),
                Step.SettingsUpdated(approvalsReviewer: "auto_review"),
                Step.Approval("rm -rf build"),
                Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        Assert.IsType<SessionEnded>(events[^1]);
        Assert.DoesNotContain(events, e => e is ApprovalRequested);
        var answer = Assert.Single(await AnswersAsync(fixture));
        Assert.Equal("cancel", answer["result"]!["decision"]!.GetValue<string>());
    }

    [Fact]
    public async Task What_was_left_unanswered_when_a_conversation_was_given_up_is_answered_with_cancel()
    {
        using var fixture = new AgentFixture()
            .Codex(c => c["ignoreInterrupt"] = true)
            .ImplementerTurn(
                Step.Message("Working...", "commentary"),
                Step.UnansweredCommandApproval("rm -rf build"),
                Step.SettingsUpdated(approvalsReviewer: "auto_review"),
                Step.Hang());
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        // Nobody answers the question: the conversation is given up while it waits.
        var events = await session.RunTurnAsync("go");

        Assert.Single(events.OfType<ApprovalRequested>());
        Assert.IsType<SessionEnded>(events[^1]);
        var answer = Assert.Single(await AnswersAsync(fixture));
        Assert.Equal("cancel", answer["result"]!["decision"]!.GetValue<string>());
    }

    /// <summary>What the stand-in received as answers to its requests, once there is at least one.</summary>
    private static async Task<List<JsonObject>> AnswersAsync(AgentFixture fixture)
    {
        for (var attempt = 0; attempt < 200 && fixture.Received("codex.response").Count == 0; attempt++)
        {
            await Task.Delay(50);
        }

        return fixture.Received("codex.response");
    }
}

public class CodexRobustnessTests
{
    [Fact]
    public async Task A_line_that_is_not_json_is_skipped_and_the_stream_continues()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Garbage(), Step.Message("Still here."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        Assert.Equal("Still here.", events.Completion().FinalMessage);
        Assert.Contains(events.OfType<AgentNotice>(), n => n.IsWarning && n.Message.Contains("not valid", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_message_that_arrives_in_small_pieces_is_put_together()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Pieces("ünï 日本 🙂 split across reads", size: 3), Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        Assert.Contains(events.OfType<AssistantTextDelta>(), d => d.Text == "ünï 日本 🙂 split across reads");
    }

    [Fact]
    public async Task A_notification_this_version_does_not_know_is_ignored()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Unknown(), Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        Assert.Equal(TurnOutcome.Completed, events.Completion().Outcome);
    }

    [Fact]
    public async Task A_very_large_message_is_delivered_completely()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.BigMessage(512), Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        Assert.Equal(512 * 1024, events.OfType<AssistantMessage>().First().Text.Length);
    }

    [Fact]
    public async Task An_agent_that_dies_in_the_middle_of_a_turn_ends_the_turn_as_failed()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Working...", "commentary"), Step.Crash(3));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var completion = events.Completion();
        Assert.Equal(TurnOutcome.Failed, completion.Outcome);
        Assert.Contains("ended", completion.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("simulated crash", completion.ErrorMessage);
    }

    [Fact]
    public async Task After_the_agent_died_a_new_request_starts_a_new_process()
    {
        using var fixture = new AgentFixture()
            .ImplementerTurn(Step.Crash(3))
            .ImplementerTurn(Step.Message("Back again."));
        await using var adapter = fixture.CodexAppServer();
        await using (var first = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None))
        {
            await first.RunTurnAsync("go");
        }

        await using var second = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);
        var events = await second.RunTurnAsync("again");

        Assert.Equal("Back again.", events.Completion().FinalMessage);
        Assert.Equal(2, fixture.CodexRequests("initialize").Count);
    }

    [Fact]
    public async Task A_conversation_codex_closed_ends_and_says_so()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Done."), Step.ThreadClosed());
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");
        var rest = await ReadUntilAsync(session, e => e is SessionEnded);

        Assert.Equal("Done.", events.Completion().FinalMessage);
        var ended = Assert.IsType<SessionEnded>(rest[^1]);
        Assert.Contains("closed the conversation", ended.Reason, StringComparison.Ordinal);
        await Assert.ThrowsAsync<AgentProtocolException>(() => session.StartTurnAsync(new TurnRequest("again", null, []), CancellationToken.None));
    }

    [Fact]
    public async Task Giving_up_a_process_ends_only_its_own_conversations_and_what_it_asks_is_answered_on_it()
    {
        using var fixture = new AgentFixture()
            .Codex(c => c["ignoreInterrupt"] = true)
            .ImplementerTurn(
                Step.Message("Working...", "commentary"),
                Step.WaitForSignal("the old process may ask"),
                Step.ServerRequest("account/chatgptAuthTokens/refresh", new JsonObject { ["reason"] = "unauthorized", ["previousAccountId"] = null }, forThread: false),
                Step.Hang())
            .ImplementerTurn(Step.Message("Second process, done."));
        var runner = new CodexHeldShutdownRunner(new ProcessRunner(), held: 1);
        await using var adapter = new CodexAppServerAdapter(runner, fixture.Clock, fixture.Options());
        var first = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);
        await first.StartTurnAsync(new TurnRequest("go", null, []), CancellationToken.None);
        await ReadUntilAsync(first, e => e is AssistantMessage);
        var oldProcess = fixture.CodexRequests("initialize")[0]["_pid"]!.GetValue<int>();

        // The turn does not stop, so its process is given up; the process is held while it shuts down.
        var shutdown = first.ShutdownAsync(TimeSpan.FromMilliseconds(300), CancellationToken.None);
        await runner.Started(0).ShutdownAsked.WaitAsync(TimeSpan.FromSeconds(60));
        await using var second = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        // The old process asks something that belongs to no conversation: it is answered on its own connection.
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(fixture.Workspace)!, "the old process may ask"), "go");
        var answer = Assert.Single(await ResponsesAsync(fixture));
        Assert.Equal(oldProcess, answer["_pid"]!.GetValue<int>());
        Assert.Equal(-32601, answer["error"]!["code"]!.GetValue<int>());

        runner.Started(0).Release();
        await shutdown.WaitAsync(TimeSpan.FromSeconds(60));
        Assert.IsType<SessionEnded>((await ReadUntilAsync(first, e => e is SessionEnded))[^1]);

        // The conversation of the new process was not told that the old one ended, and goes on.
        Assert.DoesNotContain(Available(second), e => e is SessionEnded);
        Assert.Equal("Second process, done.", (await second.RunTurnAsync("again")).Completion().FinalMessage);
        await first.DisposeAsync();
    }

    private static async Task<List<AgentEvent>> ReadUntilAsync(IAgentSession session, Func<AgentEvent, bool> until)
    {
        var events = new List<AgentEvent>();
        await foreach (var item in session.Events.ReadAllAsync(new CancellationTokenSource(TimeSpan.FromSeconds(60)).Token))
        {
            events.Add(item);
            if (until(item))
            {
                break;
            }
        }

        return events;
    }

    private static List<AgentEvent> Available(IAgentSession session)
    {
        var events = new List<AgentEvent>();
        while (session.Events.TryRead(out var next))
        {
            events.Add(next);
        }

        return events;
    }

    /// <summary>What the stand-in received as answers to its requests, once there is at least one.</summary>
    private static async Task<List<JsonObject>> ResponsesAsync(AgentFixture fixture)
    {
        for (var attempt = 0; attempt < 600 && fixture.Received("codex.response").Count == 0; attempt++)
        {
            await Task.Delay(50);
        }

        return fixture.Received("codex.response");
    }

    [Fact]
    public async Task Two_sessions_on_one_connection_each_receive_only_their_own_events()
    {
        using var fixture = new AgentFixture()
            .ImplementerTurn(Step.Sleep(300), Step.Message("from the implementer"))
            .ReviewerTurn(Step.Sleep(100), Step.RawFinal("from the reviewer"));
        await using var adapter = fixture.CodexAppServer();
        await using var implementer = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);
        await using var reviewer = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer), CancellationToken.None);

        var first = implementer.RunTurnAsync("implement");
        var second = reviewer.RunTurnAsync("review");
        var results = await Task.WhenAll(first, second);

        Assert.Equal(["from the implementer"], results[0].OfType<AssistantMessage>().Select(m => m.Text));
        Assert.Equal(["from the reviewer"], results[1].OfType<AssistantMessage>().Select(m => m.Text));
        Assert.NotEqual(implementer.SessionId, reviewer.SessionId);
    }

    [Fact]
    public async Task What_the_agent_writes_to_standard_error_never_reaches_the_protocol()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Stderr("WARN something noisy {\"method\":\"fake\"}"), Step.Message("Done."));
        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        Assert.Equal(TurnOutcome.Completed, events.Completion().Outcome);
        Assert.DoesNotContain(events.OfType<AssistantMessage>(), m => m.Text.Contains("noisy"));
    }

    [Fact]
    public async Task Closing_the_adapter_ends_the_agent_process()
    {
        using var fixture = new AgentFixture();
        int processId;
        await using (var adapter = fixture.CodexAppServer())
        {
            await adapter.GetAuthStatusAsync(CancellationToken.None);
            processId = Assert.Single(fixture.CodexRequests("initialize"))["_pid"]!.GetValue<int>();
            Assert.True(ProcessRunner.IsProcessAlive(processId, null));
        }

        for (var i = 0; i < 100 && ProcessRunner.IsProcessAlive(processId, null); i++)
        {
            await Task.Delay(100);
        }

        Assert.False(ProcessRunner.IsProcessAlive(processId, null));
    }

    [Fact]
    public void The_adapter_declares_what_it_can_and_cannot_do()
    {
        using var fixture = new AgentFixture();
        var adapter = fixture.CodexAppServer();

        Assert.True(adapter.Capabilities.Has(AdapterFeatures.InteractiveApprovals));
        Assert.True(adapter.Capabilities.Has(AdapterFeatures.Interrupt));
        Assert.True(adapter.Capabilities.Has(AdapterFeatures.Steering));
        Assert.True(adapter.Capabilities.Has(AdapterFeatures.ReadOnlyEnforcement));
        Assert.True(adapter.Capabilities.Has(AdapterFeatures.EffortReadback));
        Assert.Contains(adapter.Capabilities.Limitations, l => l.Contains("experimental", StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The agent closes its input and plays on; what YAV writes to it afterwards has to fail. That holds only while
/// the agent is the one reader of that pipe. A program another test starts at the same moment with inherited
/// handles is given every handle that is inheritable then, the end of this pipe among them, and the pipe has a
/// reader more. These run while no other test runs.
/// </summary>
[Collection(nameof(CodexClosedInputTests))]
[CollectionDefinition(nameof(CodexClosedInputTests), DisableParallelization = true)]
public class CodexClosedInputTests
{
    [Theory]
    [InlineData("reviewerApproval")]
    [InlineData("elicitation")]
    [InlineData("unknownRequest")]
    public async Task An_answer_the_agent_can_no_longer_receive_is_reported_and_the_turn_still_ends(string asked)
    {
        // Each of these is answered from where the messages of the agent are read.
        var reviewer = asked == "reviewerApproval";
        var request = asked switch
        {
            "reviewerApproval" => Step.UnansweredCommandApproval("git commit -am wip"),
            "elicitation" => Step.Elicitation("docs", "Which page?", wait: false),
            _ => Step.ServerRequest("item/tool/requestUserInput", new JsonObject { ["itemId"] = "item-9", ["questions"] = new JsonArray() }),
        };
        using var fixture = new AgentFixture().Codex(c => c["deafAfterTurnStart"] = true);
        JsonObject[] steps = [Step.CloseInput(), request, Step.Message("Done.")];
        if (reviewer)
        {
            fixture.ReviewerTurn(steps);
        }
        else
        {
            fixture.ImplementerTurn(steps);
        }

        await using var adapter = fixture.CodexAppServer();
        await using var session = await adapter.StartSessionAsync(fixture.Request(reviewer ? AgentRole.Reviewer : AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        Assert.Equal("Done.", events.Completion().FinalMessage);
        Assert.Contains(events.OfType<AgentNotice>(), n => n.IsWarning && n.Message.Contains("could not answer", StringComparison.Ordinal));
    }
}
