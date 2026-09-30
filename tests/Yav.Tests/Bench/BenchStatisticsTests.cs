using Yav.Bench;

namespace Yav.Tests.Bench;

public class BenchStatisticsTests
{
    private static StepResult Result(
        string arm,
        string task,
        bool succeeded,
        double seconds,
        long? tokens = null,
        decimal? cost = null,
        int repetition = 1,
        int step = 1,
        string[]? regressions = null) => new()
    {
        Arm = arm,
        TaskId = task,
        Step = step,
        Repetition = repetition,
        Outcome = succeeded ? "ready_to_apply" : "blocked",
        Succeeded = succeeded,
        Regressions = regressions ?? [],
        ElapsedMs = (long)(seconds * 1000),
        TotalTokens = tokens,
        CostUsd = cost,
    };

    [Theory]
    [InlineData(new double[] { 5 }, 5)]
    [InlineData(new double[] { 1, 9 }, 5)]
    [InlineData(new double[] { 9, 1, 5 }, 5)]
    [InlineData(new double[] { 4, 1, 3, 2 }, 2.5)]
    public void The_median_is_the_value_in_the_middle(double[] values, double expected)
    {
        Assert.Equal(expected, BenchStatistics.Median(values));
    }

    [Fact]
    public void Nothing_has_no_median()
    {
        Assert.Null(BenchStatistics.Median([]));
        Assert.Null(BenchStatistics.Percentile([], 90));
    }

    [Theory]
    [InlineData(90, 9)]
    [InlineData(50, 5)]
    [InlineData(100, 10)]
    [InlineData(1, 1)]
    public void A_percentile_is_a_value_that_was_measured(int percentile, double expected)
    {
        var values = Enumerable.Range(1, 10).Select(v => (double)v).Reverse().ToArray();

        Assert.Equal(expected, BenchStatistics.Percentile(values, percentile));
    }

    [Fact]
    public void The_success_rate_counts_every_run_that_was_started()
    {
        var summary = BenchStatistics.Summarize("yav",
        [
            Result("yav", "01", true, 10),
            Result("yav", "02", false, 30),
            Result("yav", "03", true, 20),
            Result("yav", "04", true, 40),
        ]);

        Assert.Equal(4, summary.Runs);
        Assert.Equal(3, summary.Successes);
        Assert.Equal(0.75, summary.SuccessRate);
        Assert.Equal(25, summary.MedianSeconds);
        Assert.Equal(40, summary.TailSeconds);
    }

    [Fact]
    public void What_a_failed_run_consumed_is_part_of_the_cost_of_a_successful_task()
    {
        var summary = BenchStatistics.Summarize("yav",
        [
            Result("yav", "01", true, 10, tokens: 1_000, cost: 0.10m),
            Result("yav", "02", false, 30, tokens: 5_000, cost: 0.50m),
            Result("yav", "03", true, 20, tokens: 2_000, cost: 0.20m),
        ]);

        Assert.Equal(8_000, summary.TotalTokens);
        Assert.Equal(0.80m, summary.TotalCostUsd);
        Assert.Equal(4_000, summary.TokensPerSuccess);
        Assert.Equal(0.40m, summary.CostPerSuccessUsd);
    }

    [Fact]
    public void Usage_that_a_run_did_not_report_is_unavailable_and_not_zero()
    {
        var summary = BenchStatistics.Summarize("yav",
        [
            Result("yav", "01", true, 10, tokens: 1_000, cost: null),
            Result("yav", "02", true, 30, tokens: null, cost: null),
        ]);

        Assert.Null(summary.TotalTokens);
        Assert.Null(summary.TokensPerSuccess);
        Assert.Null(summary.TotalCostUsd);
        Assert.Null(summary.CostPerSuccessUsd);
        Assert.Equal(1, summary.RunsWithoutUsage);
        Assert.Equal(2, summary.RunsWithoutCost);
    }

    [Fact]
    public void Without_a_success_there_is_no_cost_of_a_successful_task()
    {
        var summary = BenchStatistics.Summarize("yav", [Result("yav", "01", false, 10, tokens: 1_000, cost: 0.10m)]);

        Assert.Equal(0, summary.SuccessRate);
        Assert.Null(summary.CostPerSuccessUsd);
        Assert.Null(summary.TokensPerSuccess);
        Assert.Equal(0.10m, summary.TotalCostUsd);
    }

    [Fact]
    public void Regressions_are_counted_by_run()
    {
        var summary = BenchStatistics.Summarize("yav",
        [
            Result("yav", "01", true, 10),
            Result("yav", "02", false, 10, regressions: ["tests", "lint"]),
        ]);

        Assert.Equal(1, summary.RunsWithRegressions);
    }

    [Fact]
    public void Times_are_compared_only_where_both_succeeded_at_the_same_task()
    {
        StepResult[] yav =
        [
            Result("yav", "01", true, 10),
            Result("yav", "02", true, 50),
            Result("yav", "03", false, 5),
            Result("yav", "04", true, 30),
        ];
        StepResult[] plain =
        [
            Result("plain", "01", true, 20),
            Result("plain", "02", false, 1),
            Result("plain", "03", true, 8),
            Result("plain", "04", true, 60),
        ];

        var matched = BenchStatistics.Matched(yav, plain);

        Assert.Equal(2, matched.Pairs);
        Assert.Equal(20, matched.MedianSecondsFirst);
        Assert.Equal(40, matched.MedianSecondsSecond);
        Assert.Equal(-20, matched.MedianDifferenceSeconds);
    }

    [Fact]
    public void Repetitions_and_steps_are_matched_one_by_one()
    {
        StepResult[] first = [Result("a", "01", true, 10, repetition: 1), Result("a", "01", true, 30, repetition: 2), Result("a", "01", true, 5, step: 2)];
        StepResult[] second = [Result("b", "01", true, 20, repetition: 2), Result("b", "01", true, 7, step: 2)];

        var matched = BenchStatistics.Matched(first, second);

        Assert.Equal(2, matched.Pairs);
        Assert.Equal((30 + 5) / 2.0, matched.MedianSecondsFirst);
    }

    [Fact]
    public void Where_nothing_matches_nothing_is_compared()
    {
        var matched = BenchStatistics.Matched([Result("a", "01", true, 10)], [Result("b", "02", true, 20)]);

        Assert.Equal(0, matched.Pairs);
        Assert.Null(matched.MedianDifferenceSeconds);
    }

    [Fact]
    public void The_order_of_the_runs_is_shuffled_the_same_way_for_the_same_seed()
    {
        var items = Enumerable.Range(0, 40).ToList();

        var first = BenchStatistics.Shuffle(items, seed: 7);
        var again = BenchStatistics.Shuffle(items, seed: 7);
        var other = BenchStatistics.Shuffle(items, seed: 8);

        Assert.Equal(first, again);
        Assert.NotEqual(first, other);
        Assert.NotEqual(items, first);
        Assert.Equal(items, first.Order().ToList());
    }
}
