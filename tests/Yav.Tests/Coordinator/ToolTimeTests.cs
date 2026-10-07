using Yav.Adapters;
using Yav.Core.Agents;
using Yav.Core.Profiles;
using Yav.Core.Runs;
using Yav.Core.Timing;
using Yav.Platform.Processes;
using Yav.Tests.Support;

namespace Yav.Tests.Coordinator;

/// <summary>
/// The time an agent spends in its tools, with an agent whose reports carry the times the test gives them and a
/// clock the test moves: what is measured does not depend on how busy the machine is.
/// </summary>
public class ToolTimeTests
{
    private const string Task = "Make the app say fixed.";
    private const string PassingReview = """{"status":"pass","summary":"ok","coverage":"read the file","limitations":[],"findings":[]}""";

    /// <summary>How long YAV is kept from taking up what the agent reports, from the moment its turn began.</summary>
    private static readonly TimeSpan Busy = TimeSpan.FromSeconds(2);

    [Fact]
    public async Task A_tool_lasts_from_the_report_of_its_start_to_the_report_of_its_end_however_late_they_are_taken_up()
    {
        var clock = new ManualClock();
        var began = clock.GetUtcNow();

        // A command that ran for 1200 ms, then a search that took 300 ms.
        var spans = await RunAsync(
            clock,
            new CommandStarted(began, "c-1", "dotnet test", null),
            new CommandCompleted(began.AddMilliseconds(1_200), "c-1", "dotnet test", 1, 1_200, "failed", "1 failed"),
            new ToolActivity(began.AddMilliseconds(1_200), "t-1", "Grep", "fixed", "started"),
            new ToolActivity(began.AddMilliseconds(1_500), "t-1", "Grep", "fixed", "completed"));

        var implementation = spans.Single(s => s.Kind == SpanKind.Implementation);
        var tools = spans.Where(s => s.Kind == SpanKind.ToolActivity).OrderBy(s => s.StartedAt).ToList();
        Assert.Equal(["Model A: command", "Model A: tool"], tools.Select(s => s.Label).ToArray());
        Assert.Equal(began, tools[0].StartedAt);
        Assert.Equal(TimeSpan.FromMilliseconds(1_200), tools[0].Duration);
        Assert.Equal(began.AddMilliseconds(1_200), tools[1].StartedAt);
        Assert.Equal(TimeSpan.FromMilliseconds(300), tools[1].Duration);

        // What is left of the turn is the time of the provider: of the 2 s of the turn, 1.5 s were spent in tools.
        var report = TimingMath.Report(spans, began, spans.Max(s => s.EndedAt), null, null);
        Assert.Equal(Busy, implementation.Duration);
        Assert.Equal(TimeSpan.FromMilliseconds(1_500), report.InTools);
        Assert.Equal(TimeSpan.FromMilliseconds(500), implementation.Duration - report.InTools);
    }

    [Fact]
    public async Task A_report_with_a_time_from_before_the_turn_counts_from_when_it_is_taken_up()
    {
        // As a report left from an earlier turn has, or one made on a clock that is not the one of the run.
        var clock = new ManualClock();
        var earlier = clock.GetUtcNow().AddHours(-1);

        var spans = await RunAsync(
            clock,
            new CommandStarted(earlier, "c-1", "git status", null),
            new CommandCompleted(earlier.AddMilliseconds(300), "c-1", "git status", 0, 300, "completed", string.Empty));

        var implementation = spans.Single(s => s.Kind == SpanKind.Implementation);
        var tool = Assert.Single(spans, s => s.Kind == SpanKind.ToolActivity);
        Assert.Equal(implementation.StartedAt + Busy, tool.StartedAt);
        Assert.Equal(TimeSpan.Zero, tool.Duration);
        Assert.True(tool.StartedAt >= implementation.StartedAt && tool.EndedAt <= implementation.EndedAt, "the command lies outside the turn it belongs to");
    }

    /// <summary>
    /// A run in which the implementer reports what it is given. YAV is busy until the agent has reported all of it,
    /// and then takes the reports up one right after the other.
    /// </summary>
    private static async Task<IReadOnlyList<TimingSpan>> RunAsync(ManualClock clock, params AgentEvent[] reports)
    {
        var adapter = new ScriptedAdapter((session, _) =>
        {
            if (session.Role == AgentRole.Reviewer)
            {
                session.Complete(PassingReview);
                return System.Threading.Tasks.Task.CompletedTask;
            }

            foreach (var report in reports)
            {
                session.Emit(report);
            }

            File.WriteAllText(session.File("src/app.txt"), "fixed\n");
            session.Complete("Done.");
            return System.Threading.Tasks.Task.CompletedTask;
        });
        using var repo = GitRepo.WithFiles(("src/app.txt", "one\n"));
        await using var harness = new CoordinatorHarness(repo.Path, clock);
        harness.Adapters[adapter.Id] = adapter;
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Configuration = harness.Configuration with
        {
            ModelA = new RoleSelection(ScriptedAdapter.AdapterId, "scripted-a"),
            ModelB = new RoleSelection(ScriptedAdapter.AdapterId, "scripted-b"),
        };

        // The clock is moved where the turn takes up its first report, so it is never moved while the turn reads it.
        var busy = true;
        harness.Observer.Published += e =>
        {
            if (busy && e is AgentActivity { Role: AgentRole.Implementer, Event: TurnStarted })
            {
                busy = false;
                clock.Advance(Busy);
            }
        };

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        return harness.Database.GetSpans(outcome.RunId);
    }
}

/// <summary>
/// The same with the stand-ins of the real agents, which run as processes of their own and report as they work.
/// What is measured is from when the adapter read the report of a start to when it read the report of the end. A
/// machine busy with other tests can still keep it from reading the first until the command has ended, so these
/// run while no other test runs.
/// </summary>
[Collection(nameof(ToolTimeOfAgentProcessesTests))]
[CollectionDefinition(nameof(ToolTimeOfAgentProcessesTests), DisableParallelization = true)]
public class ToolTimeOfAgentProcessesTests
{
    private const string Task = "Make the app say fixed.";

    [Theory]
    [InlineData(CoordinatorHarness.CodexId, "model-a")]
    [InlineData(CoordinatorHarness.ClaudeId, "opus")]
    public async Task The_time_an_agent_spends_in_its_tools_is_measured_apart_from_the_time_of_the_provider(string adapter, string model)
    {
        await using var harness = new CoordinatorHarness();
        await OnTheClockOfTheRunAsync(harness);
        harness.Configuration = harness.Configuration with { ModelA = new RoleSelection(adapter, model) };
        harness.TrustGates(CoordinatorHarness.NoTextGate("tests", "src/app.txt", "bug"));
        harness.Agents
            .ImplementerTurn(
                Step.Sleep(300),
                Step.Command("dotnet test --filter Secret=hunter2", "1 failed", exitCode: 1, milliseconds: 1_200),
                Step.Write("src/app.txt", "fixed\n"),
                Step.Command("dotnet test", "all passed", milliseconds: 900),
                Step.Message("Done."))
            .ReviewerTurn(Step.Review("pass"));

        var outcome = await harness.RunAsync(Task);

        harness.AssertEnded(RunOutcomeKind.ReadyToApply, outcome);
        var spans = harness.Database.GetSpans(outcome.RunId);
        var implementation = spans.Single(s => s.Kind == SpanKind.Implementation);
        var tools = spans.Where(s => s.Kind == SpanKind.ToolActivity).OrderBy(s => s.StartedAt).ToList();
        Assert.Equal(2, tools.Count);
        // The commands run for 1200 and 900 ms. A report of a start that is read late makes the command shorter
        // by as much.
        Assert.True(tools[0].Duration >= TimeSpan.FromMilliseconds(400), $"the first command: {tools[0].Duration}");
        Assert.True(tools[1].Duration >= TimeSpan.FromMilliseconds(300), $"the second command: {tools[1].Duration}");
        Assert.All(tools, tool => Assert.True(
            tool.StartedAt >= implementation.StartedAt && tool.EndedAt <= implementation.EndedAt, "a command lies outside the turn it belongs to"));

        // What is kept with a measurement says whose tool it was, and not what the command was.
        Assert.All(tools, tool => Assert.Equal("Model A: command", tool.Label));

        var started = harness.Database.FindRun(outcome.RunId)!.CreatedAt;
        var report = TimingMath.Report(spans, started, spans.Max(s => s.EndedAt), null, null);
        Assert.True(report.InTools >= TimeSpan.FromMilliseconds(700), $"in tools: {report.InTools}");
        Assert.True(report.InTools < implementation.Duration, $"in tools {report.InTools}, the turn {implementation.Duration}");
    }

    /// <summary>
    /// The stand-ins report on the clock of the run, as the agents do in YAV. Those of the harness report on a clock
    /// of their own, and a turn does not take the times of another clock for the times of a tool.
    /// </summary>
    private static async Task OnTheClockOfTheRunAsync(CoordinatorHarness harness)
    {
        IAgentAdapter[] replaced = [harness.Adapters[CoordinatorHarness.CodexId], harness.Adapters[CoordinatorHarness.ClaudeId]];
        harness.Adapters[CoordinatorHarness.CodexId] = new CodexAppServerAdapter(new ProcessRunner(), harness.Clock, harness.Agents.Options("codex"));
        harness.Adapters[CoordinatorHarness.ClaudeId] = new ClaudeCliAdapter(new ProcessRunner(), harness.Clock, harness.Agents.Options("claude"), () => null);
        foreach (var adapter in replaced)
        {
            await adapter.DisposeAsync();
        }
    }
}
