namespace Yav.Bench;

/// <summary>What the runs of one arm add up to.</summary>
public sealed record ArmSummary(
    string Arm,
    int Runs,
    int Successes,
    int RunsWithRegressions,
    double? MedianSeconds,
    double? TailSeconds,
    double? MedianActiveSeconds,
    double? MedianFirstActionSeconds,
    long? TotalTokens,
    long? TokensPerSuccess,
    decimal? TotalCostUsd,
    decimal? CostPerSuccessUsd,
    int RunsWithoutUsage,
    int RunsWithoutCost,
    int RepairCycles)
{
    public double SuccessRate => Runs == 0 ? 0 : (double)Successes / Runs;
}

/// <summary>The times of two arms, for the runs in which both succeeded at the same task.</summary>
public sealed record MatchedTimes(
    string First,
    string Second,
    int Pairs,
    double? MedianSecondsFirst,
    double? MedianSecondsSecond,
    double? MedianDifferenceSeconds);

/// <summary>
/// The numbers of a benchmark report. They describe the runs that were made and nothing else: a finite
/// number of runs of a finite number of tasks.
/// </summary>
public static class BenchStatistics
{
    /// <summary>The percentile that is reported as the tail.</summary>
    public const int Tail = 90;

    public static double? Median(IEnumerable<double> values)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
        {
            return null;
        }

        var middle = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[middle] : (sorted[middle - 1] + sorted[middle]) / 2;
    }

    /// <summary>The smallest measured value that is not below that share of the values. Always a value that was measured.</summary>
    public static double? Percentile(IEnumerable<double> values, int percentile)
    {
        var sorted = values.Order().ToList();
        if (sorted.Count == 0)
        {
            return null;
        }

        var rank = (int)Math.Ceiling(Math.Clamp(percentile, 1, 100) / 100.0 * sorted.Count);
        return sorted[Math.Clamp(rank, 1, sorted.Count) - 1];
    }

    public static ArmSummary Summarize(string arm, IReadOnlyList<StepResult> results)
    {
        var successes = results.Count(r => r.Succeeded);
        var withoutUsage = results.Count(r => r.TotalTokens is null);
        var withoutCost = results.Count(r => r.CostUsd is null);

        // A sum of what was reported would look like the whole when a run reported nothing. It is given only when it is the whole.
        long? tokens = results.Count > 0 && withoutUsage == 0 ? results.Sum(r => r.TotalTokens!.Value) : null;
        decimal? cost = results.Count > 0 && withoutCost == 0 ? results.Sum(r => r.CostUsd!.Value) : null;

        return new ArmSummary(
            arm,
            results.Count,
            successes,
            results.Count(r => r.Regressions.Count > 0),
            Median(results.Select(r => r.ElapsedMs / 1000.0)),
            Percentile(results.Select(r => r.ElapsedMs / 1000.0), Tail),
            Median(results.Where(r => r.ActiveMs is not null).Select(r => r.ActiveMs!.Value / 1000.0)),
            Median(results.Where(r => r.FirstActionMs is not null).Select(r => r.FirstActionMs!.Value / 1000.0)),
            tokens,
            tokens is null || successes == 0 ? null : tokens.Value / successes,
            cost,
            cost is null || successes == 0 ? null : decimal.Round(cost.Value / successes, 4),
            withoutUsage,
            withoutCost,
            results.Sum(r => r.RepairCycles));
    }

    public static MatchedTimes Matched(IReadOnlyList<StepResult> first, IReadOnlyList<StepResult> second)
    {
        var firstName = first.Count > 0 ? first[0].Arm : string.Empty;
        var secondName = second.Count > 0 ? second[0].Arm : string.Empty;
        var others = second
            .Where(r => r.Succeeded)
            .GroupBy(r => (r.TaskId, r.Step, r.Repetition))
            .ToDictionary(g => g.Key, g => g.First());

        var pairs = first
            .Where(r => r.Succeeded && others.ContainsKey((r.TaskId, r.Step, r.Repetition)))
            .Select(r => (First: r.ElapsedMs / 1000.0, Second: others[(r.TaskId, r.Step, r.Repetition)].ElapsedMs / 1000.0))
            .ToList();

        return new MatchedTimes(
            firstName,
            secondName,
            pairs.Count,
            Median(pairs.Select(p => p.First)),
            Median(pairs.Select(p => p.Second)),
            Median(pairs.Select(p => p.First - p.Second)));
    }

    /// <summary>The same items in an order that depends on the seed only, so that a run can be repeated.</summary>
    public static List<T> Shuffle<T>(IReadOnlyList<T> items, int seed)
    {
        var shuffled = items.ToList();
        var random = new Random(seed);
        for (var i = shuffled.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
        }

        return shuffled;
    }
}
