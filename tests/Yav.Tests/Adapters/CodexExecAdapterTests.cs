using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.Adapters;

public class CodexExecAdapterTests
{
    private const string Prompt = "Fix the login bug.\r\nKeep \"quotes\", %PATH%, & | > and ünï 日本 exactly.\n";

    [Fact]
    public async Task The_prompt_travels_over_standard_input_and_never_on_the_command_line()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.CodexExec();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, model: "model-a", effort: "xhigh"), CancellationToken.None);

        await session.RunTurnAsync(Prompt);

        var invocation = Assert.Single(fixture.Received("exec.invocation"));
        Assert.Equal(Prompt, invocation["prompt"]!.GetValue<string>());
        var arguments = invocation["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        Assert.DoesNotContain(arguments, a => a.Contains("login bug"));
        Assert.Contains("--json", arguments);
        Assert.Equal("workspace-write", arguments[arguments.IndexOf("-s") + 1]);
        Assert.Equal("model-a", arguments[arguments.IndexOf("-m") + 1]);
        Assert.Equal(fixture.Workspace, arguments[arguments.IndexOf("-C") + 1]);
        Assert.Contains("model_reasoning_effort=xhigh", arguments);
        Assert.DoesNotContain("--dangerously-bypass-approvals-and-sandbox", arguments);
    }

    [Fact]
    public async Task The_reviewer_runs_in_a_read_only_sandbox()
    {
        using var fixture = new AgentFixture().ReviewerTurn(Step.Review("pass"));
        await using var adapter = fixture.CodexExec();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer), CancellationToken.None);

        await session.RunTurnAsync("review", ReviewSchema.AsElement());

        var arguments = Assert.Single(fixture.Received("exec.invocation"))["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        Assert.Equal("read-only", arguments[arguments.IndexOf("-s") + 1]);
    }

    [Fact]
    public async Task Events_are_translated_and_the_final_message_is_kept()
    {
        using var fixture = new AgentFixture().ImplementerTurn(
            Step.Command("npm test", "1 failing\n", 1),
            Step.Write("src/fix.txt", "fixed"),
            Step.Message("The bug is fixed."));
        await using var adapter = fixture.CodexExec();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var command = Assert.Single(events.OfType<CommandCompleted>());
        Assert.Equal("npm test", command.Command);
        Assert.Equal(1, command.ExitCode);
        Assert.Equal("1 failing\n", command.Output);
        Assert.Equal(FileChangeKind.Add, Assert.Single(Assert.Single(events.OfType<FilesChanged>()).Changes).Kind);
        Assert.Equal("fixed", File.ReadAllText(fixture.InWorkspace("src/fix.txt")));
        Assert.Equal(TurnOutcome.Completed, events.Completion().Outcome);
        Assert.Equal("The bug is fixed.", events.Completion().FinalMessage);
        Assert.StartsWith("thr-exec-", session.SessionId);
    }

    [Fact]
    public async Task Usage_is_the_running_total_of_the_session_with_nothing_counted_twice()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Done."));
        await using var adapter = fixture.CodexExec();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var usage = Assert.Single(events.OfType<UsageUpdated>()).Usage;
        Assert.Equal(UsageScope.CumulativeForSession, usage.Scope);
        Assert.Equal(200, usage.Tokens.UncachedInput);
        Assert.Equal(1000, usage.Tokens.CacheRead);
        Assert.Equal(300, usage.Tokens.Output);
        Assert.Equal(1500, usage.Tokens.Total);
        Assert.Equal(ValueProvenance.Unavailable, usage.CostProvenance);
    }

    [Fact]
    public async Task A_second_turn_resumes_the_same_session_by_its_identifier()
    {
        using var fixture = new AgentFixture()
            .ImplementerTurn(Step.Message("First."))
            .ImplementerTurn(Step.Message("Second."));
        await using var adapter = fixture.CodexExec();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);
        await session.RunTurnAsync("one");
        var threadId = session.SessionId!;

        var events = await session.RunTurnAsync("two");

        Assert.Equal("Second.", events.Completion().FinalMessage);
        var second = fixture.Received("exec.invocation")[1]["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        Assert.Equal(threadId, second[second.IndexOf("resume") + 1]);
        Assert.Equal(threadId, session.SessionId);
    }

    [Fact]
    public async Task A_saved_session_is_resumed_explicitly()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Resumed."));
        await using var adapter = fixture.CodexExec();
        await using var session = await adapter.ResumeSessionAsync("thr-saved", fixture.Request(AgentRole.Implementer), CancellationToken.None);

        await session.RunTurnAsync("continue");

        var arguments = Assert.Single(fixture.Received("exec.invocation"))["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        Assert.Equal("thr-saved", arguments[arguments.IndexOf("resume") + 1]);
        Assert.DoesNotContain("--last", arguments);
    }

    [Fact]
    public async Task An_action_that_needs_approval_ends_as_approval_required_instead_of_hanging()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Approval("rm -rf build"), Step.Message("never reached"));
        await using var adapter = fixture.CodexExec();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var completion = events.Completion();
        Assert.Equal(TurnOutcome.ApprovalRequired, completion.Outcome);
        Assert.Contains("approval", completion.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(events, e => e is ApprovalRequested);
    }

    [Fact]
    public async Task The_structured_result_is_requested_through_a_schema_file_that_is_removed_afterwards()
    {
        using var fixture = new AgentFixture().ReviewerTurn(Step.Review("changes_required", title: "Off by one"));
        await using var adapter = fixture.CodexExec();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Reviewer), CancellationToken.None);

        var events = await session.RunTurnAsync("review", ReviewSchema.AsElement());

        Assert.Equal("changes_required", events.Completion().StructuredOutput!.Value.GetProperty("status").GetString());
        var invocation = Assert.Single(fixture.Received("exec.invocation"));
        Assert.True(invocation["hasSchema"]!.GetValue<bool>());
        var arguments = invocation["args"]!.AsArray().Select(a => a!.GetValue<string>()).ToList();
        var schemaFile = arguments[arguments.IndexOf("--output-schema") + 1];
        Assert.False(File.Exists(schemaFile), "The temporary schema file was left behind.");
        Assert.False(schemaFile.StartsWith(fixture.Workspace, StringComparison.OrdinalIgnoreCase), "The schema file was written into the workspace.");
    }

    [Fact]
    public async Task A_failed_turn_reports_the_providers_message()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Fail("You've hit your usage limit."));
        await using var adapter = fixture.CodexExec();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        Assert.Equal(TurnOutcome.UsageLimitReached, events.Completion().Outcome);
        Assert.Equal("You've hit your usage limit.", events.Completion().ErrorMessage);
    }

    [Fact]
    public async Task An_agent_that_dies_ends_the_turn_as_failed_with_its_last_words()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Working...", "commentary"), Step.Crash(7));
        await using var adapter = fixture.CodexExec();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        var completion = events.Completion();
        Assert.Equal(TurnOutcome.Failed, completion.Outcome);
        Assert.Contains("7", completion.ErrorMessage);
        Assert.Contains("simulated crash", completion.ErrorMessage);
    }

    [Fact]
    public async Task Interrupting_ends_the_process_because_this_mode_has_no_other_way()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Message("Working...", "commentary"), Step.Hang());
        await using var adapter = fixture.CodexExec();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go", onEvent: async e =>
        {
            if (e is AssistantMessage)
            {
                await session.InterruptAsync(CancellationToken.None);
            }
        });

        Assert.Equal(TurnOutcome.Interrupted, events.Completion().Outcome);
    }

    [Fact]
    public async Task A_line_that_is_not_json_is_skipped()
    {
        using var fixture = new AgentFixture().ImplementerTurn(Step.Garbage(), Step.Message("Still here."));
        await using var adapter = fixture.CodexExec();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer), CancellationToken.None);

        var events = await session.RunTurnAsync("go");

        Assert.Equal("Still here.", events.Completion().FinalMessage);
    }

    [Fact]
    public async Task What_cannot_be_confirmed_in_this_mode_is_reported_as_unknown()
    {
        using var fixture = new AgentFixture();
        await using var adapter = fixture.CodexExec();
        await using var session = await adapter.StartSessionAsync(fixture.Request(AgentRole.Implementer, effort: "xhigh"), CancellationToken.None);

        Assert.Null(session.Effective!.Effort);
        Assert.Null(session.Effective.Model);

        // The directory it was asked to work in is what YAV asked for, not what the agent said: it is not reported either.
        Assert.Null(session.Effective.WorkingDirectory);
        Assert.False(adapter.Capabilities.Has(AdapterFeatures.InteractiveApprovals));
        Assert.False(adapter.Capabilities.Has(AdapterFeatures.EffortReadback));
        Assert.False(adapter.Capabilities.Has(AdapterFeatures.Steering));
        Assert.True(adapter.Capabilities.Has(AdapterFeatures.ResumeSession));
        Assert.False(await session.SteerAsync("hello", CancellationToken.None));
    }

    [Fact]
    public async Task The_agent_is_found_and_described_as_the_stable_compatibility_path()
    {
        using var fixture = new AgentFixture();
        await using var adapter = fixture.CodexExec();

        var detection = await adapter.DetectAsync(CancellationToken.None);
        var auth = await adapter.GetAuthStatusAsync(CancellationToken.None);

        Assert.True(detection.Found);
        Assert.Equal("0.158.0", detection.Version);
        Assert.Equal(AdapterMaturity.Stable, detection.Maturity);
        Assert.True(auth.Authenticated);
        Assert.Equal(AccountRouteKind.Subscription, auth.Route);
        Assert.Empty(await adapter.ListModelsAsync(CancellationToken.None));
    }

    [Theory]
    [InlineData("0.159.2", true)]
    [InlineData("0.157.9", false)]
    public async Task A_version_is_called_tested_as_it_is_for_the_app_server(string version, bool tested)
    {
        // Both adapters start the same Codex CLI.
        using var fixture = new AgentFixture().Codex(c => c["version"] = version);
        await using var adapter = fixture.CodexExec();

        var detection = await adapter.DetectAsync(CancellationToken.None);

        Assert.Equal(tested, detection.VersionTested);
        Assert.Equal("0.158.x, 0.159.x", detection.TestedVersions);
    }

    [Fact]
    public async Task An_agent_that_is_signed_out_is_reported_as_such()
    {
        using var fixture = new AgentFixture().Codex(c => c["loginStatus"] = "Not logged in");
        await using var adapter = fixture.CodexExec();

        var auth = await adapter.GetAuthStatusAsync(CancellationToken.None);

        Assert.False(auth.Authenticated);
    }
}
