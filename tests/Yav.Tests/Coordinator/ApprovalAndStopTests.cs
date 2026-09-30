using Yav.Core.Agents;
using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.Coordinator;

public class ApprovalTests
{
    private const string Task = "Make the app say fixed.";

    private static CoordinatorHarness Harness()
    {
        var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.TextGate("tests", "src/app.txt", "fixed"));
        return harness;
    }

    private static CoordinatorHarness AskingToInstall()
    {
        var harness = Harness();
        harness.Agents
            .ImplementerTurn(Step.Approval(
                "npm install left-pad",
                onAccept: [Step.Write("src/app.txt", "fixed\n"), Step.Message("Installed and changed.")],
                onDecline: [Step.Message("I could not install the package, so nothing was changed.")],
                reason: "needs network access"))
            .ReviewerTurn(Step.Review("pass"));
        return harness;
    }

    [Fact]
    public async Task The_user_is_asked_and_the_answer_reaches_the_agent()
    {
        await using var harness = AskingToInstall();

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        var asked = Assert.Single(harness.Approvals.Requests);
        Assert.Equal("npm install left-pad", asked.Command);
        Assert.Equal(ApprovalKind.CommandExecution, asked.Kind);
        Assert.Equal(
            [RunState.Implementing, RunState.AwaitingApproval, RunState.Implementing, RunState.Checking, RunState.ReadyToApply],
            harness.Observer.States().ToArray());
    }

    [Fact]
    public async Task A_refusal_is_passed_on_and_the_agent_goes_on_without_the_action()
    {
        await using var harness = AskingToInstall();
        harness.Approvals.Decide = _ => ApprovalDecision.Decline;

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Completed, outcome);
        Assert.Equal(RunDisposition.NoChanges, harness.Database.FindRun(outcome.RunId)!.Disposition);
        Assert.Contains("could not install", outcome.FinalMessage, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Every_decision_is_recorded_with_who_made_it()
    {
        await using var harness = AskingToInstall();

        var outcome = await harness.RunAsync(Task);

        var note = Assert.Single(harness.Observer.Notes(Stages.Approval));
        Assert.Contains("Accept (user)", note.Message, StringComparison.Ordinal);
        var events = harness.Database.GetEvents(outcome.RunId, 500);
        Assert.Contains(events, e => e.Type == "approval.requested" && e.Detail == "npm install left-pad");
    }

    [Fact]
    public async Task Without_anyone_to_ask_the_run_ends_as_approval_required_and_nothing_is_granted()
    {
        await using var harness = AskingToInstall();
        harness.Approvals.CanAsk = false;

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ApprovalRequired, outcome);
        Assert.Equal(RunState.Blocked, outcome.State);
        Assert.Equal("npm install left-pad", Assert.Single(outcome.PendingApprovals).Command);
        Assert.Contains("Approval Required", outcome.Reason, StringComparison.Ordinal);
        Assert.Empty(harness.Approvals.Requests);
        Assert.Null(outcome.Candidate);
        Assert.DoesNotContain(RunState.AwaitingApproval, harness.Observer.States());
    }

    [Fact]
    public async Task An_answer_to_a_request_the_agent_took_back_never_reaches_the_agent()
    {
        await using var harness = Harness();
        harness.Agents
            .ImplementerTurn(Step.WithdrawnApproval("npm install left-pad", afterMs: 400), Step.Write("src/app.txt", "fixed\n"))
            .ReviewerTurn(Step.Review("pass"));
        harness.Approvals.Hold = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Single(harness.Approvals.Requests);
        Assert.True(harness.Approvals.WasCancelled);
        Assert.DoesNotContain(harness.Agents.Received("codex.response"), r => r["result"]?["decision"] is not null);
        Assert.Contains(harness.Observer.Notes(Stages.Approval), n => n.Message.Contains("withdrawn", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Waiting_for_the_user_is_measured_separately_from_the_work()
    {
        await using var harness = AskingToInstall();

        var outcome = await harness.RunAsync(Task);

        var spans = harness.Database.GetSpans(outcome.RunId);
        var waiting = Assert.Single(spans, s => s.Kind == Yav.Core.Timing.SpanKind.ApprovalWaiting);
        Assert.Equal("npm install left-pad", waiting.Label);
        Assert.NotNull(waiting.Duration);
    }
}

public class StopAndLimitTests
{
    private const string Task = "Make the app say fixed.";

    private static CoordinatorHarness Harness()
    {
        var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.TextGate("tests", "src/app.txt", "fixed"));
        return harness;
    }

    /// <summary>The implementer's first turn hangs after its first change; what follows is what a resumed run meets.</summary>
    private static CoordinatorHarness Hanging()
    {
        var harness = Harness();
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "half done\n"), Step.Hang())
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Message("Finished."))
            .ReviewerTurn(Step.Review("pass"));
        return harness;
    }

    /// <summary>Starts a run and stops it once the implementer's first change was seen.</summary>
    private static async Task<RunOutcome> RunAndStopAsync(CoordinatorHarness harness)
    {
        using var stop = new CancellationTokenSource();
        var written = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        harness.Observer.Published += e =>
        {
            if (e is AgentActivity { Event: FilesChanged { Status: "completed" } })
            {
                written.TrySetResult();
            }
        };

        var running = harness.RunAsync(Task, cancellationToken: stop.Token);
        await written.Task.WaitAsync(TimeSpan.FromSeconds(30));
        await stop.CancelAsync();
        return await running.WaitAsync(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task Stopping_asks_the_provider_to_interrupt_and_keeps_the_work()
    {
        await using var harness = Hanging();

        var outcome = await RunAndStopAsync(harness);

        harness.AssertEnded(RunOutcomeKind.Interrupted, outcome);
        Assert.Equal(RunState.Interrupted, harness.Database.FindRun(outcome.RunId)!.State);
        Assert.Single(harness.Agents.CodexRequests("turn/interrupt"));
        var workspace = await harness.Workspaces.FindAsync(harness.Database.FindTask(outcome.TaskId!)!.WorkspaceId!, CancellationToken.None);
        Assert.Equal("half done\n", WorkspaceHarness.ReadIn(workspace!, "src/app.txt"));
        Assert.Equal("one\n", harness.ReadProject("src/app.txt"));
    }

    [Fact]
    public async Task A_stopped_run_continues_in_the_same_conversation_when_it_is_resumed()
    {
        await using var harness = Hanging();
        var stopped = await RunAndStopAsync(harness);

        var outcome = await harness.Coordinator.ResumeAsync(stopped.RunId, harness.Configuration, harness.Observer, harness.Approvals, CancellationToken.None);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Equal(stopped.RunId, outcome.RunId);
        var implementer = harness.ImplementerPrompts();
        Assert.Equal(2, implementer.Count);
        Assert.Contains("did not finish", implementer[1], StringComparison.Ordinal);
        Assert.Contains(Task, implementer[1], StringComparison.Ordinal);
        var turns = harness.Agents.CodexRequests("turn/start").Select(t => t["params"]!["threadId"]!.GetValue<string>()).ToList();
        Assert.Equal(turns[0], turns[1]);
    }

    [Fact]
    public async Task Stopping_a_run_leaves_processes_that_are_not_its_own_alive()
    {
        await using var harness = Hanging();
        var bystander = harness.Runner.Start(Fixtures.Tool(harness.ProjectPath, "sleep", "60"));
        try
        {
            await RunAndStopAsync(harness);

            Assert.False(bystander.HasExited);
            Assert.True(Yav.Platform.Processes.ProcessRunner.IsProcessAlive(bystander.ProcessId, "yav-fake-agent"));
        }
        finally
        {
            bystander.Terminate();
            await bystander.DisposeAsync();
        }
    }

    [Fact]
    public async Task An_agent_that_does_not_stop_is_ended_after_the_grace_period()
    {
        await using var harness = Hanging();
        harness.Agents.Codex(c => c["ignoreInterrupt"] = true);

        var outcome = await RunAndStopAsync(harness);

        harness.AssertEnded(RunOutcomeKind.Interrupted, outcome);
        var agent = harness.Agents.CodexRequests("turn/start")[0]["_pid"]!.GetValue<int>();
        for (var attempt = 0; attempt < 50 && Yav.Platform.Processes.ProcessRunner.IsProcessAlive(agent, "yav-fake-agent"); attempt++)
        {
            await System.Threading.Tasks.Task.Delay(100);
        }

        Assert.False(Yav.Platform.Processes.ProcessRunner.IsProcessAlive(agent, "yav-fake-agent"));
    }

    [Fact]
    public async Task A_run_whose_agent_had_to_be_ended_is_continued_in_a_new_process_that_resumes_the_conversation()
    {
        await using var harness = Hanging();
        harness.Agents.Codex(c => c["ignoreInterrupt"] = true);
        var stopped = await RunAndStopAsync(harness);
        var conversation = harness.Agents.CodexRequests("turn/start")[0]["params"]!["threadId"]!.GetValue<string>();
        harness.Agents.Codex(c =>
        {
            c["ignoreInterrupt"] = false;
            c["knownThreads"] = new System.Text.Json.Nodes.JsonArray { conversation };
            c["threadStates"] = new System.Text.Json.Nodes.JsonObject { [conversation] = "interrupted" };
        });

        var outcome = await harness.Coordinator.ResumeAsync(stopped.RunId, harness.Configuration, harness.Observer, harness.Approvals, CancellationToken.None);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        var resumed = Assert.Single(harness.Agents.CodexRequests("thread/resume"));
        Assert.Equal(conversation, resumed["params"]!["threadId"]!.GetValue<string>());
        Assert.Equal(2, harness.Agents.CodexRequests("initialize").Select(r => r["_pid"]!.GetValue<int>()).Distinct().Count());
    }

    [Theory]
    [InlineData("usageLimitExceeded", "usage limit")]
    [InlineData("rateLimitExceeded", "rate limit")]
    public async Task A_limit_of_the_provider_stops_the_run_in_its_own_state_and_nothing_is_switched(string error, string expected)
    {
        await using var harness = Harness();
        harness.Agents.ImplementerTurn(Step.Write("src/app.txt", "half\n"), Step.Fail("You have hit your limit.", info: error));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.RateLimited, outcome);
        Assert.Equal(RunState.RateLimited, harness.Database.FindRun(outcome.RunId)!.State);
        Assert.Contains(expected, outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("Nothing was bought or switched", outcome.Reason, StringComparison.Ordinal);
        Assert.Single(harness.Agents.CodexRequests("thread/start"));
    }

    [Fact]
    public async Task A_reviewer_that_runs_into_a_limit_does_not_turn_into_a_missing_review()
    {
        await using var harness = Harness();
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"))
            .ReviewerTurn(Step.Fail("You have hit your limit.", info: "usageLimitExceeded"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.RateLimited, outcome);
        Assert.Contains("Model B", outcome.Reason, StringComparison.Ordinal);
        Assert.NotNull(outcome.Candidate);
    }

    [Fact]
    public async Task A_failed_turn_fails_the_run_with_the_providers_message()
    {
        await using var harness = Harness();
        harness.Agents.ImplementerTurn(Step.Fail("The model is unavailable."));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Failed, outcome);
        Assert.Contains("The model is unavailable.", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task An_agent_that_dies_in_the_middle_of_a_turn_leaves_the_run_for_reconciliation()
    {
        await using var harness = Harness();
        harness.Agents.ImplementerTurn(Step.Write("src/app.txt", "half\n"), Step.Crash());

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.NeedsReconciliation, outcome);
        Assert.Equal(RunState.NeedsReconciliation, harness.Database.FindRun(outcome.RunId)!.State);
        Assert.Single(harness.Agents.CodexRequests("turn/start"));
        Assert.Contains("Nothing is sent again automatically", outcome.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_token_limit_stops_further_turns_and_keeps_the_candidate()
    {
        await using var harness = Harness();
        harness.Configuration = harness.Configuration with { Limits = RunLimits.Default with { MaxRunTokens = 1_000 } };
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.Usage(input: 4_000, cached: 1_000, output: 500, reasoning: 100))
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains("token limit of 1,000", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("4,500", outcome.Reason, StringComparison.Ordinal);
        Assert.Single(harness.Agents.CodexRequests("turn/start"));
        Assert.NotNull(outcome.Candidate);
    }

    [Fact]
    public async Task A_rate_limit_threshold_stops_further_turns()
    {
        await using var harness = Harness();
        harness.Configuration = harness.Configuration with { Limits = RunLimits.Default with { StopAtRateLimitPercent = 90 } };
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.RateLimits(93))
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains("93%", outcome.Reason, StringComparison.Ordinal);
        Assert.Single(harness.Agents.CodexRequests("turn/start"));
    }

    [Fact]
    public async Task A_limit_that_was_reported_earlier_than_another_one_still_stops_further_turns()
    {
        // Claude Code reports every window by itself. The one that is nearly used up is not the one it named last.
        await using var harness = Harness();
        harness.Configuration = harness.Configuration with
        {
            ModelA = new Yav.Core.Profiles.RoleSelection(CoordinatorHarness.ClaudeId, "opus"),
            Limits = RunLimits.Default with { StopAtRateLimitPercent = 90 },
        };
        harness.Agents
            .ImplementerTurn(Step.Write("src/app.txt", "fixed\n"), Step.ClaudeRateLimit(0.95, "seven_day"), Step.ClaudeRateLimit(0.2, "five_hour"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains("95%", outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("seven_day", outcome.Reason, StringComparison.Ordinal);
        Assert.Empty(harness.Agents.CodexRequests("turn/start"));
    }

    [Fact]
    public async Task The_second_window_of_a_limit_stops_further_turns_as_the_first_one_does()
    {
        await using var harness = Harness();
        harness.Configuration = harness.Configuration with { Limits = RunLimits.Default with { StopAtRateLimitPercent = 90 } };
        harness.Agents
            .ImplementerTurn(
                Step.Write("src/app.txt", "fixed\n"),
                Step.RateLimitUpdate(new System.Text.Json.Nodes.JsonObject
                {
                    ["limitId"] = "codex",
                    ["primary"] = Step.LimitWindow(12),
                    ["secondary"] = Step.LimitWindow(97, 10_080),
                }))
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains("97%", outcome.Reason, StringComparison.Ordinal);
        Assert.Single(harness.Agents.CodexRequests("turn/start"));
    }

    [Fact]
    public async Task An_elapsed_time_limit_stops_further_turns_and_does_not_cut_off_a_running_one()
    {
        var clock = new ManualClock();
        using var repo = GitRepo.WithFiles(("src/app.txt", "one\n"));
        await using var harness = new CoordinatorHarness(repo.Path, clock);
        harness.TrustGates(CoordinatorHarness.TextGate("tests", "src/app.txt", "fixed"));
        harness.Configuration = harness.Configuration with { Limits = RunLimits.Default with { MaxElapsed = TimeSpan.FromMinutes(10) } };
        harness.Agents
            .ImplementerTurn(Step.Message("Thinking.", "commentary"), Step.Write("src/app.txt", "fixed\n"), Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"));
        harness.Observer.Published += e =>
        {
            if (e is AgentActivity { Event: AssistantMessage { Text: "Thinking." } })
            {
                clock.Advance(TimeSpan.FromMinutes(11));
            }
        };

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains("elapsed-time limit of 10m", outcome.Reason, StringComparison.Ordinal);
        // The running turn was allowed to finish, so its work is in the candidate.
        Assert.Equal("src/app.txt", Assert.Single(outcome.Candidate!.Changes.Files).Path);
        Assert.Single(harness.Agents.CodexRequests("turn/start"));
        Assert.Empty(harness.Agents.CodexRequests("turn/interrupt"));
    }
}
