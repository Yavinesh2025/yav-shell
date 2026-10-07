using Yav.Core;
using Yav.Core.Agents;
using Yav.Core.Runs;
using Yav.Core.Timing;
using Yav.Tests.Support;

namespace Yav.Tests.Core;

public class TokenCountsTests
{
    [Fact]
    public void Cached_tokens_inside_the_input_count_are_not_counted_twice()
    {
        // The provider reports input 1000 of which 800 were cached, and output 300 of which 120 were reasoning.
        var counts = TokenCounts.FromInclusiveCounters(input: 1000, cachedInput: 800, output: 300, reasoningOutput: 120);

        Assert.Equal(200, counts.UncachedInput);
        Assert.Equal(800, counts.CacheRead);
        Assert.Equal(300, counts.Output);
        Assert.Equal(120, counts.ReasoningWithinOutput);
        Assert.Equal(1300, counts.Total);
    }

    [Fact]
    public void Counters_that_exclude_the_cache_are_added_as_they_are()
    {
        var counts = TokenCounts.FromExclusiveCounters(input: 50, cacheRead: 900, cacheWrite: 100, output: 300, thinking: 250);

        Assert.Equal(1350, counts.Total);
    }

    [Fact]
    public void Missing_information_is_unavailable_and_never_zero()
    {
        Assert.Null(TokenCounts.Unavailable.Total);
        Assert.Null(TokenCounts.FromInclusiveCounters(null, null, null, null).Total);
        Assert.Null(TokenCounts.FromInclusiveCounters(100, null, 20, null).CacheRead);
    }

    [Fact]
    public void A_counter_that_went_backwards_is_unavailable_rather_than_negative()
    {
        var delta = TokenCounts.Delta(new TokenCounts(10, 0, 0, 5, 0), new TokenCounts(50, 0, 0, 5, 0));

        Assert.Null(delta.UncachedInput);
        Assert.Equal(0, delta.Output);
    }
}

public class SessionUsageTrackerTests
{
    private static UsageSnapshot Cumulative(long input, long output, long? lastInput = null, long? lastOutput = null, decimal? cost = null) => new(
        "codex-app-server", "thread-1", "model-a", UsageScope.CumulativeForSession,
        new TokenCounts(input, 0, 0, output, 0),
        lastInput is null ? null : new TokenCounts(lastInput, 0, 0, lastOutput, 0),
        cost, cost is null ? ValueProvenance.Unavailable : ValueProvenance.Estimated, null, "thread/tokenUsage/updated", Builders.Now);

    [Fact]
    public void A_new_session_counts_everything_it_reports()
    {
        var tracker = new SessionUsageTracker(null, null, sessionIsNew: true);

        tracker.Observe(Cumulative(100, 20));
        tracker.Observe(Cumulative(260, 55));

        Assert.Equal(260, tracker.RunTokens.UncachedInput);
        Assert.Equal(55, tracker.RunTokens.Output);
    }

    [Fact]
    public void A_resumed_session_counts_only_what_this_run_added()
    {
        var earlierTotal = new TokenCounts(5000, 0, 0, 900, 0);
        var tracker = new SessionUsageTracker(earlierTotal, 1.50m, sessionIsNew: false);

        tracker.Observe(Cumulative(5400, 1000, cost: 1.75m));

        Assert.Equal(400, tracker.RunTokens.UncachedInput);
        Assert.Equal(100, tracker.RunTokens.Output);
        Assert.Equal(0.25m, tracker.RunCostUsd);
    }

    [Fact]
    public void A_resumed_session_with_no_stored_total_uses_the_last_request_to_find_where_the_run_began()
    {
        var tracker = new SessionUsageTracker(null, null, sessionIsNew: false);

        tracker.Observe(Cumulative(5400, 1000, lastInput: 400, lastOutput: 100));
        tracker.Observe(Cumulative(5900, 1150, lastInput: 500, lastOutput: 150));

        Assert.Equal(900, tracker.RunTokens.UncachedInput);
        Assert.Equal(250, tracker.RunTokens.Output);
        Assert.True(tracker.BaselineKnown);
    }

    [Fact]
    public void A_resumed_session_whose_earlier_usage_is_unknown_reports_unavailable()
    {
        var tracker = new SessionUsageTracker(null, null, sessionIsNew: false);

        tracker.Observe(Cumulative(5400, 1000, cost: 2.00m));

        Assert.False(tracker.BaselineKnown);
        Assert.Null(tracker.RunTokens.Total);
        Assert.Null(tracker.RunCostUsd);
    }

    [Fact]
    public void Per_turn_readings_are_added_up()
    {
        var tracker = new SessionUsageTracker(null, null, sessionIsNew: false);
        UsageSnapshot Turn(long input, long output) => new(
            "claude-cli", "s", "m", UsageScope.PerTurn, new TokenCounts(input, 0, 0, output, 0), null, null,
            ValueProvenance.Unavailable, null, "result", Builders.Now);

        tracker.Observe(Turn(100, 10));
        tracker.Observe(Turn(200, 30));

        Assert.Equal(300, tracker.RunTokens.UncachedInput);
        Assert.Equal(40, tracker.RunTokens.Output);
    }

    [Fact]
    public void The_cumulative_total_is_kept_for_the_next_run()
    {
        var tracker = new SessionUsageTracker(null, null, sessionIsNew: true);

        tracker.Observe(Cumulative(700, 80, cost: 0.4m));

        Assert.Equal(700, tracker.CumulativeForStorage!.Value.Tokens.UncachedInput);
        Assert.Equal(0.4m, tracker.CumulativeForStorage.Value.Cost);
    }
}

public class TimingTests
{
    private static TimingSpan Span(SpanKind kind, int startSecond, int seconds, string? group = null) =>
        new(Ids.NewId("s"), "run-1", kind, kind.ToString(), Builders.Now.AddSeconds(startSecond), TimeSpan.FromSeconds(seconds), group);

    [Fact]
    public void Checks_that_ran_side_by_side_are_not_added_up_as_elapsed_time()
    {
        var review = Span(SpanKind.Review, 0, 60, "check-1");
        var tests = Span(SpanKind.Tests, 10, 40, "check-1");

        Assert.Equal(TimeSpan.FromSeconds(60), TimingMath.WallClock([review, tests]));
        Assert.Equal(TimeSpan.FromSeconds(100), TimingMath.Work([review, tests]));
    }

    [Fact]
    public void Separate_intervals_are_added_and_gaps_are_excluded()
    {
        var first = Span(SpanKind.Implementation, 0, 30);
        var second = Span(SpanKind.Implementation, 100, 20);

        Assert.Equal(TimeSpan.FromSeconds(50), TimingMath.WallClock([second, first]));
    }

    [Fact]
    public void Approval_waiting_is_reported_separately_from_active_time()
    {
        var spans = new[]
        {
            Span(SpanKind.Implementation, 0, 100),
            Span(SpanKind.ApprovalWaiting, 20, 30),
            Span(SpanKind.Review, 100, 50),
        };

        var report = TimingMath.Report(spans, Builders.Now, Builders.Now.AddSeconds(150), Builders.Now.AddSeconds(4), Builders.Now.AddSeconds(150));

        Assert.Equal(TimeSpan.FromSeconds(150), report.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(30), report.ApprovalWaiting);
        Assert.Equal(TimeSpan.FromSeconds(120), report.ActiveTime);
        Assert.Equal(TimeSpan.FromSeconds(4), report.TimeToFirstAction);
        Assert.Equal(TimeSpan.FromSeconds(150), report.TimeToAcceptedResult);
        Assert.Equal(SpanKind.Implementation, report.Bottleneck);
    }

    [Fact]
    public void Time_that_no_stage_accounts_for_is_named_and_not_hidden()
    {
        var spans = new[]
        {
            Span(SpanKind.LocalPreparation, 0, 10),
            Span(SpanKind.Implementation, 15, 60),
            Span(SpanKind.Review, 80, 20, "check-1"),
            Span(SpanKind.Tests, 85, 10, "check-1"),
        };

        var report = TimingMath.Report(spans, Builders.Now, Builders.Now.AddSeconds(110), null, null);

        // 110 seconds passed, of which the stages account for 10, 60 and 20.
        Assert.Equal(TimeSpan.FromSeconds(110), report.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(20), report.BetweenStages);
    }

    [Fact]
    public void A_run_begins_with_its_first_stage_even_when_the_run_was_recorded_after_it()
    {
        // The project and the agents are checked before there is a record of the run.
        var spans = new[] { Span(SpanKind.LocalPreparation, -5, 5), Span(SpanKind.Implementation, 0, 10) };

        var report = TimingMath.Report(spans, Builders.Now, Builders.Now.AddSeconds(10), Builders.Now.AddSeconds(1), null);

        Assert.Equal(TimeSpan.FromSeconds(15), report.Elapsed);
        Assert.Equal(TimeSpan.FromSeconds(6), report.TimeToFirstAction);
        Assert.Equal(TimeSpan.Zero, report.BetweenStages);
    }

    [Fact]
    public void A_run_that_has_not_finished_has_no_elapsed_time()
    {
        var report = TimingMath.Report([Span(SpanKind.Implementation, 0, 10)], Builders.Now, null, null, null);

        Assert.Null(report.Elapsed);
        Assert.Null(report.TimeToAcceptedResult);
    }

    [Fact]
    public void The_recorder_measures_with_the_monotonic_clock()
    {
        var clock = new ManualClock();
        var recorder = new TimingRecorder(clock, "run-1");

        using (recorder.Start(SpanKind.Tests, "unit tests"))
        {
            clock.Advance(TimeSpan.FromSeconds(12));
        }

        var span = Assert.Single(recorder.Completed);
        Assert.Equal(TimeSpan.FromSeconds(12), span.Duration);
        Assert.Equal(SpanKind.Tests, span.Kind);
    }
}

public class RunStateMachineTests
{
    [Fact]
    public void Completed_is_final()
    {
        foreach (var state in Enum.GetValues<RunState>())
        {
            Assert.False(RunStateMachine.CanTransition(RunState.Completed, state), $"Completed must not move to {state}.");
        }
    }

    [Theory]
    [InlineData(RunState.Preparing, RunState.Implementing)]
    [InlineData(RunState.Implementing, RunState.AwaitingApproval)]
    [InlineData(RunState.AwaitingApproval, RunState.Implementing)]
    [InlineData(RunState.Implementing, RunState.Checking)]
    [InlineData(RunState.Checking, RunState.ReadyToApply)]
    [InlineData(RunState.Checking, RunState.Repairing)]
    [InlineData(RunState.Repairing, RunState.Checking)]
    [InlineData(RunState.ReadyToApply, RunState.Completed)]
    [InlineData(RunState.Checking, RunState.RateLimited)]
    [InlineData(RunState.Implementing, RunState.NeedsReconciliation)]
    // An answer that changed nothing: freezing the workspace, the first step of checking, showed there is nothing to apply.
    [InlineData(RunState.Checking, RunState.Completed)]
    public void The_pipeline_transitions_are_allowed(RunState from, RunState to)
    {
        Assert.True(RunStateMachine.CanTransition(from, to));
    }

    [Theory]
    // Nothing becomes ready without being checked, and nothing is completed without being ready.
    [InlineData(RunState.Implementing, RunState.ReadyToApply)]
    [InlineData(RunState.Repairing, RunState.ReadyToApply)]
    [InlineData(RunState.Preparing, RunState.ReadyToApply)]
    // Not even for an answer: only freezing the workspace shows that nothing was changed.
    [InlineData(RunState.Implementing, RunState.Completed)]
    [InlineData(RunState.Repairing, RunState.Completed)]
    [InlineData(RunState.Blocked, RunState.Completed)]
    [InlineData(RunState.Blocked, RunState.ReadyToApply)]
    [InlineData(RunState.Failed, RunState.Completed)]
    public void Shortcuts_around_checking_are_refused(RunState from, RunState to)
    {
        Assert.False(RunStateMachine.CanTransition(from, to));
    }

    [Fact]
    public void Run_identifiers_sort_by_time_and_are_recognized()
    {
        var clock = new ManualClock(new DateTimeOffset(2026, 9, 29, 11, 25, 1, TimeSpan.Zero));
        var first = Ids.NewRunId(clock);
        clock.Advance(TimeSpan.FromSeconds(1));
        var second = Ids.NewRunId(clock);

        Assert.StartsWith("20260929-112501-", first);
        Assert.True(string.CompareOrdinal(first, second) < 0);
        Assert.True(Ids.IsRunId(first));
        Assert.False(Ids.IsRunId("20260929-112501"));
        Assert.False(Ids.IsRunId("../../etc/passwd-------"));
    }

    [Fact]
    public void Evidence_is_stale_for_each_kind_of_difference()
    {
        var binding = Builders.Binding();

        Assert.True(binding.Matches(Builders.Binding(), out _));
        Assert.False(binding.Matches(Builders.Binding(fingerprint: Builders.OtherFingerprint), out var candidate));
        Assert.False(binding.Matches(Builders.Binding(acceptance: 2), out var requirements));
        Assert.False(binding.Matches(Builders.Binding(profile: "other"), out var configuration));
        Assert.False(binding.Matches(Builders.Binding(environment: "other"), out var environment));
        Assert.Contains("candidate", candidate);
        Assert.Contains("requirements", requirements);
        Assert.Contains("configuration", configuration);
        Assert.Contains("toolchain", environment);
    }
}
